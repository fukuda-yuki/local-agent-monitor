using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    private const int DeletionLockTimeoutSeconds = 1;

    internal ValueTask<RetentionAdapterResult> ExecuteSqliteDeletionAsync(
        RetentionDeleteContext context,
        RetentionSqliteSourceMutation mutateSource) =>
        ExecuteSqliteDeletionAsync(context, mutateSource, null);

    internal async ValueTask<RetentionAdapterResult> ExecuteSqliteDeletionAsync(
        RetentionDeleteContext context,
        RetentionSqliteSourceMutation mutateSource,
        Action<RetentionSqliteDeletePhase>? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutateSource);
        var gate = this.context?.Gate;
        if (gate is not null) await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenDeletion();
            using var transaction = connection.BeginTransaction(deferred: false);
            var authorization = AuthorizeSqliteDeletion(connection, transaction, context);
            if (authorization.Result is not null)
            {
                transaction.Commit();
                return authorization.Result;
            }

            var proof = StrictSourceProof(connection, transaction, authorization.Key!);
            if (proof != SourceReceiptProof.Match)
            {
                transaction.Rollback();
                return SourceProofResult(proof);
            }

            var sourceToken = SourceToken(connection, transaction, authorization.Key!);
            if (sourceToken is null) { transaction.Rollback(); return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.UnexpectedSourceMissing); }
            var grant = new RetentionSqliteDeletionGrant(authorization.Key!, sourceToken);
            var affected = await mutateSource(connection, transaction, grant).ConfigureAwait(false);
            if (affected < 0)
            {
                transaction.Rollback();
                return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed);
            }
            checkpoint?.Invoke(RetentionSqliteDeletePhase.AfterSourceMutation);
            context.CancellationToken.ThrowIfCancellationRequested();

            var postProof = StrictSourceProof(connection, transaction, authorization.Key!);
            if (postProof != SourceReceiptProof.Missing)
            {
                transaction.Rollback();
                return postProof == SourceReceiptProof.CatalogBusy
                    ? RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy)
                    : RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed);
            }
            checkpoint?.Invoke(RetentionSqliteDeletePhase.AfterSourceAbsenceVerified);
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!AdvanceCursor(connection, transaction, context))
            {
                transaction.Rollback();
                return RetentionAdapterResult.LeaseLost;
            }
            checkpoint?.Invoke(RetentionSqliteDeletePhase.AfterDeleteCursorAdvanced);
            context.CancellationToken.ThrowIfCancellationRequested();

            var completedAt = timeProvider.GetUtcNow();
            if (Complete(connection, transaction, new RetentionDeleteFence(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), completedAt, checkpoint) != RetentionMutationDisposition.Applied)
            {
                transaction.Rollback();
                return RetentionAdapterResult.LeaseLost;
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke(RetentionSqliteDeletePhase.BeforeCommit);
            transaction.Commit();
            return RetentionAdapterResult.Deleted;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy); }
        catch (ArgumentException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity); }
        catch (FormatException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity); }
        catch { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        finally { gate?.Release(); }
    }

    private (RetentionAdapterResult? Result, RetentionOwnershipKey? Key, byte[]? Receipt) AuthorizeSqliteDeletion(SqliteConnection connection, SqliteTransaction transaction, RetentionDeleteContext context)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,state,revision FROM retention_items WHERE item_id=$id;";
        command.Parameters.AddWithValue("$id", context.ItemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (RetentionAdapterResult.LeaseLost, null, null);
        var store = reader.GetString(0);
        var kind = TryParseKind(reader.GetString(1));
        var source = reader.GetString(2);
        var receiptVersion = reader.GetInt32(3);
        var receipt = reader.GetFieldValue<byte[]>(4);
        var state = reader.GetString(5);
        var revision = reader.GetInt64(6);
        reader.Close();
        if (kind is null || receiptVersion != 1 || receipt.Length != 32) return (RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity), null, null);
        if (!string.Equals(store, context.StoreInstanceId, StringComparison.Ordinal)
            || kind != context.StoreKind
            || !string.Equals(source, context.SourceIdentity.SourceItemId, StringComparison.Ordinal)
            || !ReceiptMatches(context.SourceIdentity.OwnershipReceipt, receipt))
            return (RetentionAdapterResult.LeaseLost, null, null);
        if (!IsCanonicalSourceIdentity(kind.Value, source)) return (RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity), null, null);
        if (state == "deleted" && HasFinalizedTombstone(connection, transaction, context.ItemId)) return (RetentionAdapterResult.Deleted, null, null);
        if (revision != context.ExpectedRevision
            || state != "deleting"
            || !Owns(connection, transaction, new RetentionDeleteFence(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), timeProvider.GetUtcNow())
            || context.IntentCursor != 0
            || !CurrentJournalMatches(connection, transaction, context))
            return (RetentionAdapterResult.LeaseLost, null, null);
        return (null, new RetentionOwnershipKey(store, kind.Value, source), receipt);
    }

    private static bool CurrentJournalMatches(SqliteConnection connection, SqliteTransaction transaction, RetentionDeleteContext context)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_delete_journal WHERE item_id=$id AND expected_revision=$revision AND durable_cursor=$cursor);";
        command.Parameters.AddWithValue("$id", context.ItemId);
        command.Parameters.AddWithValue("$revision", context.ExpectedRevision);
        command.Parameters.AddWithValue("$cursor", context.IntentCursor);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool IsCanonicalSourceIdentity(RetentionStoreKind kind, string sourceItemId) =>
        kind == RetentionStoreKind.SessionEventContent
            ? Guid.TryParseExact(sourceItemId, "D", out var guid) && string.Equals(sourceItemId, guid.ToString("D"), StringComparison.Ordinal)
            : long.TryParse(sourceItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    private static bool AdvanceCursor(SqliteConnection connection, SqliteTransaction transaction, RetentionDeleteContext context)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE retention_delete_journal SET durable_cursor=1 WHERE item_id=$id AND expected_revision=$revision AND durable_cursor=0;";
        command.Parameters.AddWithValue("$id", context.ItemId);
        command.Parameters.AddWithValue("$revision", context.ExpectedRevision);
        return command.ExecuteNonQuery() == 1;
    }

    private static bool HasFinalizedTombstone(SqliteConnection connection, SqliteTransaction transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_tombstones WHERE item_id=$id);";
        command.Parameters.AddWithValue("$id", itemId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool ReceiptMatches(string encodedReceipt, byte[] receipt)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(encodedReceipt), receipt); }
        catch (FormatException) { return false; }
    }

    private static RetentionAdapterResult SourceProofResult(SourceReceiptProof proof) => proof switch
    {
        SourceReceiptProof.Missing => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.UnexpectedSourceMissing),
        SourceReceiptProof.InvalidIdentity => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity),
        SourceReceiptProof.InvalidOrMismatched => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch),
        SourceReceiptProof.CatalogBusy => RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy),
        _ => RetentionAdapterResult.LeaseLost
    };

    private SqliteConnection OpenDeletion()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = DeletionLockTimeoutSeconds
        }.ToString());
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=0; PRAGMA foreign_keys=ON;";
        busy.ExecuteNonQuery();
        return connection;
    }
}

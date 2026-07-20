using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionMutationVersionVector MaterializeMutationVersionVector(IReadOnlyList<string> itemIds)
    {
        var ids = ValidateTargetIds(itemIds);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var vector = MaterializeMutationVersionVector(connection, transaction, ids);
        transaction.Commit();
        return vector;
    }

    internal async ValueTask<RetentionMutationCasResult> TryCompareAndSwapMutationAsync(
        RetentionMutationVersionVector expected,
        Func<SqliteConnection, SqliteTransaction, RetentionMutationVersionVector, ValueTask> writeCallback,
        Func<SqliteConnection, SqliteTransaction, string, ValueTask> resultVersionWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(writeCallback);
        ArgumentNullException.ThrowIfNull(resultVersionWriter);
        cancellationToken.ThrowIfCancellationRequested();

        var ids = ValidateTargetIds(expected.ExpectedItems.Select(static item => item.ItemId).ToArray());
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = MaterializeMutationVersionVector(connection, transaction, ids);
        if (!MatchesExpected(expected, current))
        {
            transaction.Rollback();
            return new(RetentionMutationCasDisposition.Stale, null);
        }

        await writeCallback(connection, transaction, current).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = MaterializeMutationVersionVector(connection, transaction, ids);
        await resultVersionWriter(connection, transaction, result.ExpectedStateVersion).ConfigureAwait(false);
        transaction.Commit();
        return new(RetentionMutationCasDisposition.Committed, result.ExpectedStateVersion);
    }

    private static string[] ValidateTargetIds(IReadOnlyList<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count > RetentionMutationConstants.TargetItemLimit)
            throw new ArgumentOutOfRangeException(nameof(itemIds));
        var ids = itemIds.ToArray();
        if (ids.Any(static id => string.IsNullOrWhiteSpace(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("retention_mutation_target_invalid", nameof(itemIds));
        return ids;
    }

    private static RetentionMutationVersionVector MaterializeMutationVersionVector(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> itemIds)
    {
        var expected = new List<RetentionMutationExpectedStateItem>(itemIds.Count);
        var targets = new List<RetentionMutationDigestItem>(itemIds.Count);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[itemIds.Count];
        for (var index = 0; index < itemIds.Count; index++)
        {
            parameters[index] = $"$item{index}";
            command.Parameters.AddWithValue(parameters[index], itemIds[index]);
        }

        command.CommandText = itemIds.Count == 0
            ? "SELECT item_id,store_kind,state,revision FROM retention_items WHERE 0 ORDER BY item_id COLLATE BINARY;"
            : $"SELECT item_id,store_kind,state,revision FROM retention_items WHERE item_id IN ({string.Join(',', parameters)}) ORDER BY item_id COLLATE BINARY;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var itemId = reader.GetString(0);
            var storeKind = ParseStoreKind(reader.GetString(1));
            var state = ParseLifecycle(reader.GetString(2));
            expected.Add(new(itemId, reader.GetInt64(3), RetentionMutationStateProjection.PinState(state), state));
            targets.Add(new(itemId, storeKind));
        }

        if (expected.Count != itemIds.Count)
            throw new InvalidOperationException("retention_mutation_target_not_found");

        return new(
            expected,
            targets,
            RetentionMutationDigests.ExpectedStateVersion(expected),
            RetentionMutationDigests.TargetItemSetDigest(targets));
    }

    internal RetentionMutationVersionVector MaterializeMutationVersionVectorWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> itemIds) =>
        MaterializeMutationVersionVector(connection, transaction, ValidateTargetIds(itemIds));

    internal bool MutationVersionMatches(
        RetentionMutationVersionVector expected,
        RetentionMutationVersionVector current) =>
        MatchesExpected(expected, current);

    private static bool MatchesExpected(RetentionMutationVersionVector expected, RetentionMutationVersionVector current)
    {
        if (!string.Equals(expected.TargetItemSetDigest, current.TargetItemSetDigest, StringComparison.Ordinal)
            || expected.ExpectedItems.Count != current.ExpectedItems.Count)
            return false;

        for (var index = 0; index < expected.ExpectedItems.Count; index++)
        {
            var expectedItem = expected.ExpectedItems[index];
            var currentItem = current.ExpectedItems[index];
            if (expectedItem != currentItem) return false;
        }

        return string.Equals(expected.ExpectedStateVersion, current.ExpectedStateVersion, StringComparison.Ordinal);
    }

    private static RetentionStoreKind ParseStoreKind(string value) => value switch
    {
        "session_event_content" => RetentionStoreKind.SessionEventContent,
        "raw_record" => RetentionStoreKind.RawRecord,
        "analysis_run_raw" => RetentionStoreKind.AnalysisRunRaw,
        "sensitive_bundle" => RetentionStoreKind.SensitiveBundle,
        "analysis_sdk_directory" => RetentionStoreKind.AnalysisSdkDirectory,
        _ => throw new InvalidOperationException("retention_mutation_target_not_applicable")
    };

    private static RetentionItemLifecycle ParseLifecycle(string value) => value switch
    {
        "expiring" => RetentionItemLifecycle.Expiring,
        "retained_by_policy" => RetentionItemLifecycle.RetainedByPolicy,
        "expired_pending_deletion" => RetentionItemLifecycle.ExpiredPendingDeletion,
        "deletion_queued" => RetentionItemLifecycle.DeletionQueued,
        "deleting" => RetentionItemLifecycle.Deleting,
        "deleted" => RetentionItemLifecycle.Deleted,
        "deletion_failed" => RetentionItemLifecycle.DeletionFailed,
        _ => throw new InvalidOperationException("retention_mutation_target_not_applicable")
    };
}

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RawRecordRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider timeProvider;
    private readonly ILocalWorkspaceProjectionTransactionParticipant participant;
    private readonly ILocalWorkspacePublicationGate? publicationGate;

    internal RawRecordRetentionAdapter(RetentionCatalogStore catalog, TimeProvider? timeProvider = null, ILocalWorkspaceProjectionTransactionParticipant? participant = null, ILocalWorkspacePublicationGate? publicationGate = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.participant = participant ?? UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance;
        this.publicationGate = publicationGate;
    }

    public RetentionStoreKind StoreKind => RetentionStoreKind.RawRecord;

    public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
    {
        await using var publicationLease = publicationGate is null
            ? null
            : await publicationGate.AcquireReadAsync(context.CancellationToken);
        return await catalog.ExecuteSqliteDeletionAsync(
            context,
            (connection, transaction, grant) => DeleteRawRecordAsync(connection, transaction, grant, timeProvider, participant));
    }

    private static ValueTask<int> DeleteRawRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionSqliteDeletionGrant grant,
        TimeProvider timeProvider,
        ILocalWorkspaceProjectionTransactionParticipant participant)
    {
        if (!long.TryParse(grant.OwnershipKey.SourceItemId, CultureInfo.InvariantCulture, out var rawRecordId)
            || rawRecordId <= 0)
            throw new ArgumentException("Raw record identity is invalid.");

        var now = timeProvider.GetUtcNow();
        participant.ValidateInstallationState(connection, transaction);
        PrepareAffectedSessionStage(connection, transaction, rawRecordId);

        try
        {
            using (var installed = connection.CreateCommand())
            {
                installed.Transaction = transaction;
                installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='local_workspace_span_facts');";
                if (Convert.ToInt64(installed.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    using var facts = connection.CreateCommand();
                    facts.Transaction = transaction;
                    facts.CommandText = "DELETE FROM local_workspace_span_facts WHERE raw_record_id=$id;";
                    facts.Parameters.AddWithValue("$id", rawRecordId);
                    facts.ExecuteNonQuery();
                }
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$retention_owner_token;";
            command.Parameters.AddWithValue("$id", rawRecordId);
            grant.BindSourceToken(command);
            var deleted = command.ExecuteNonQuery() == 1;
            if (deleted)
                participant.RefreshSessionBatches(connection, transaction, ReadAffectedSessionIdBatches(connection, transaction), now);
            return ValueTask.FromResult(deleted ? 1 : -1);
        }
        finally
        {
            using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DROP TABLE IF EXISTS temp.local_workspace_retention_affected_sessions;";
            cleanup.ExecuteNonQuery();
        }
    }

    private static void PrepareAffectedSessionStage(SqliteConnection connection, SqliteTransaction transaction, long rawRecordId)
    {
        using (var stage = connection.CreateCommand())
        {
            stage.Transaction = transaction;
            stage.CommandText = """
            DROP TABLE IF EXISTS temp.local_workspace_retention_affected_sessions;
            CREATE TEMP TABLE local_workspace_retention_affected_sessions(session_id TEXT PRIMARY KEY) WITHOUT ROWID;
            """;
            stage.ExecuteNonQuery();
        }
        using var installed = connection.CreateCommand();
        installed.Transaction = transaction;
        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans') AND EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_events');";
        if (Convert.ToInt64(installed.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_workspace_retention_affected_sessions(session_id)
            SELECT DISTINCT e.session_id
            FROM monitor_spans m JOIN session_events e
              ON e.source_adapter='otel-exact' COLLATE BINARY
             AND lower(e.trace_id)=lower(m.trace_id) COLLATE BINARY
             AND lower(e.source_event_id)=lower(m.trace_id||'/'||m.span_id) COLLATE BINARY
            WHERE m.raw_record_id=$id
            ORDER BY e.session_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$id", rawRecordId);
        command.ExecuteNonQuery();
    }

    private static IEnumerable<IReadOnlyCollection<string>> ReadAffectedSessionIdBatches(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        string? after = null;
        while (true)
        {
            string[] result;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT session_id FROM local_workspace_retention_affected_sessions WHERE $after IS NULL OR session_id COLLATE BINARY>$after COLLATE BINARY ORDER BY session_id COLLATE BINARY LIMIT 200;";
                command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                var batch = new List<string>(200);
                while (reader.Read()) batch.Add(reader.GetString(0));
                result = batch.ToArray();
            }
            if (result.Length == 0) yield break;
            yield return result;
            after = result[^1];
        }
    }
}

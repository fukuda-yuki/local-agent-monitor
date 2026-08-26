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
        this.participant = participant ?? LocalWorkspaceProjectionTransactionParticipant.Instance;
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

        var sessionIds = ReadAffectedSessionIds(connection, transaction, rawRecordId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$retention_owner_token;";
        command.Parameters.AddWithValue("$id", rawRecordId);
        grant.BindSourceToken(command);
        var deleted = command.ExecuteNonQuery() == 1;
        if (deleted && sessionIds.Length != 0)
            participant.RefreshSessions(connection, transaction, sessionIds, timeProvider.GetUtcNow());
        return ValueTask.FromResult(deleted ? 1 : -1);
    }

    private static string[] ReadAffectedSessionIds(SqliteConnection connection, SqliteTransaction transaction, long rawRecordId)
    {
        using var installed = connection.CreateCommand();
        installed.Transaction = transaction;
        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans') AND EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_events');";
        if (Convert.ToInt64(installed.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT e.session_id FROM monitor_spans m JOIN session_events e ON e.source_adapter='otel-exact' AND e.source_event_id=m.trace_id||'/'||m.span_id WHERE m.raw_record_id=$id ORDER BY e.session_id;";
        command.Parameters.AddWithValue("$id", rawRecordId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
}

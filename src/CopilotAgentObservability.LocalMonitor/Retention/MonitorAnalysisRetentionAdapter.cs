using System.Globalization;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class MonitorAnalysisRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;

    internal MonitorAnalysisRetentionAdapter(RetentionCatalogStore catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public RetentionStoreKind StoreKind => RetentionStoreKind.AnalysisRunRaw;

    public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return catalog.ExecuteSqliteDeletionAsync(
            context,
            (connection, transaction, grant) => grant.OwnershipKey.SourceItemId.StartsWith("local_ai:", StringComparison.Ordinal)
                ? DeleteLocalAiAsync(connection, transaction, grant)
                : SqliteMonitorAnalysisStore.DeleteOwnedRawFieldsAsync(connection, transaction, grant, ParseRunId(grant.OwnershipKey.SourceItemId)));
    }

    private static ValueTask<int> DeleteLocalAiAsync(SqliteConnection connection, SqliteTransaction transaction, RetentionSqliteDeletionGrant grant)
    {
        var parts=grant.OwnershipKey.SourceItemId.Split(':');
        if(parts.Length!=3 || parts[0]!="local_ai" || parts[1] is not ("snapshot" or "result") || !LocalAiResultValidatorV1.CanonicalUuid7(parts[2])) throw new ArgumentException("The Local AI content identity is invalid.");
        connection.CreateFunction<string,string,long>("local_ai_retention_delete_authorized",(kind,id)=>kind==parts[1]&&id==parts[2]?1L:0L,isDeterministic:false);
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText=parts[1]=="snapshot"
            ? "UPDATE local_ai_snapshots SET payload_json=NULL,evidence_index_json=NULL WHERE snapshot_id=$id AND scope_kind='session' AND retention_owner_token=$retention_owner_token AND payload_json IS NOT NULL;"
            : "UPDATE local_ai_results SET result_json=NULL WHERE result_id=$id AND retention_owner_token=$retention_owner_token AND result_json IS NOT NULL;";
        command.Parameters.AddWithValue("$id",parts[2]); grant.BindSourceToken(command); return ValueTask.FromResult(command.ExecuteNonQuery());
    }

    private static long ParseRunId(string sourceItemId)
    {
        if (!long.TryParse(sourceItemId, CultureInfo.InvariantCulture, out var runId) || runId <= 0)
            throw new ArgumentException("The analysis run identity is invalid.", nameof(sourceItemId));

        return runId;
    }
}

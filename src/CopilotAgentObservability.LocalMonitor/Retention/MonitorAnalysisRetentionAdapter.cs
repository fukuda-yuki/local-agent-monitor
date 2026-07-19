using System.Globalization;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

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
            (connection, transaction, grant) =>
                SqliteMonitorAnalysisStore.DeleteOwnedRawFieldsAsync(connection, transaction, grant, ParseRunId(grant.OwnershipKey.SourceItemId)));
    }

    private static long ParseRunId(string sourceItemId)
    {
        if (!long.TryParse(sourceItemId, CultureInfo.InvariantCulture, out var runId) || runId <= 0)
            throw new ArgumentException("The analysis run identity is invalid.", nameof(sourceItemId));

        return runId;
    }
}

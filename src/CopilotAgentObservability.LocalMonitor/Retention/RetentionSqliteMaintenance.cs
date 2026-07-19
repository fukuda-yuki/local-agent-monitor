using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class RetentionSqliteMaintenance
{
    private readonly RetentionCatalogStore? catalog;

    internal RetentionSqliteMaintenance() { }
    internal RetentionSqliteMaintenance(RetentionCatalogStore catalog) => this.catalog=catalog;

    internal ValueTask<bool> RunAsync(DateTimeOffset now,CancellationToken cancellationToken) =>
        catalog is null ? ValueTask.FromResult(false) : catalog.TryRunMaintenanceAsync(now,cancellationToken);
}

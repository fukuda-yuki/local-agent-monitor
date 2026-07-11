using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed class SessionOtelEnrichmentWorker : BackgroundService
{
    private readonly SqliteSessionOtelEnricher enricher;
    private readonly TimeSpan pollInterval;

    public SessionOtelEnrichmentWorker(SqliteSessionOtelEnricher enricher, TimeSpan? pollInterval = null)
    {
        this.enricher = enricher;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (enricher.ProcessNextBatch() > 0) continue;
            }
            catch (SqliteException)
            {
                // The dedicated cursor remains unchanged; retry after the projection/schema writer advances.
            }
            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

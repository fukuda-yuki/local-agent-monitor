using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.LocalMonitor.Events;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed class SessionOtelEnrichmentWorker : BackgroundService
{
    private readonly SqliteSessionOtelEnricher enricher;
    private readonly TimeSpan pollInterval;
    private readonly MonitorEventBroker? eventBroker;

    public SessionOtelEnrichmentWorker(SqliteSessionOtelEnricher enricher, TimeSpan? pollInterval = null, MonitorEventBroker? eventBroker = null)
    {
        this.enricher = enricher;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        this.eventBroker = eventBroker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (enricher.ProcessNextBatch() > 0)
                {
                    eventBroker?.PublishProjectionChanged();
                    continue;
                }
            }
            catch (SqliteException)
            {
                // The dedicated cursor remains unchanged; retry after the projection/schema writer advances.
            }
            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

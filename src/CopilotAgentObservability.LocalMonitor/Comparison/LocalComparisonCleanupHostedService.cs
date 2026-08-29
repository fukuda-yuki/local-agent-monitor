namespace CopilotAgentObservability.LocalMonitor;

internal sealed class LocalComparisonCleanupHostedService(
    SqliteLocalComparisonStore store,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal LocalComparisonCleanupResult RunOnce(CancellationToken cancellationToken) =>
        store.CleanupExpired(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _ = RunOnce(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

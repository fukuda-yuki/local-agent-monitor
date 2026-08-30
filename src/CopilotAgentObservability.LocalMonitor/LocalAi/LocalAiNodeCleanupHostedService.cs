using CopilotAgentObservability.Persistence.Sqlite.LocalAi;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class LocalAiNodeCleanupHostedService(
    string databasePath,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal int RunOnce() => LocalAiAnalysisStoreV1.DeleteExpiredTransientRunsIfPresent(databasePath,clock.GetUtcNow());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _ = RunOnce();
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

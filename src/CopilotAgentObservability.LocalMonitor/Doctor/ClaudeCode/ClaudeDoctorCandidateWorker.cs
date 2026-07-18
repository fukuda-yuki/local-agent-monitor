using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;

namespace CopilotAgentObservability.LocalMonitor.Doctor.ClaudeCode;

internal sealed class ClaudeDoctorCandidateWorker : BackgroundService
{
    private readonly ClaudeDoctorCandidateObserver observer;
    private readonly TimeSpan pollInterval;

    public ClaudeDoctorCandidateWorker(
        ClaudeDoctorCandidateObserver observer,
        TimeSpan? pollInterval = null)
    {
        this.observer = observer;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            observer.RunOnce();
            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

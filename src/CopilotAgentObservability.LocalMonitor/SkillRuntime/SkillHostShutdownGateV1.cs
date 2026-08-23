namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class SkillHostShutdownGateV1
{
    private int normalShutdownStarted;
    private readonly CancellationTokenSource stopping = new();

    internal bool IsNormalShutdownStarted => Volatile.Read(ref normalShutdownStarted) != 0;
    internal CancellationToken StoppingToken => stopping.Token;

    internal bool TryStartNormalShutdown()
    {
        if (Interlocked.CompareExchange(ref normalShutdownStarted, 1, 0) != 0) return false;
        stopping.Cancel();
        return true;
    }
}

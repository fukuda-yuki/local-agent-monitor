namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class SkillHostShutdownGateV1
{
    private int normalShutdownStarted;

    internal bool IsNormalShutdownStarted => Volatile.Read(ref normalShutdownStarted) != 0;

    internal bool TryStartNormalShutdown() =>
        Interlocked.CompareExchange(ref normalShutdownStarted, 1, 0) == 0;
}

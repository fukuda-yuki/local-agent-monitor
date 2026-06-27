using CopilotAgentObservability.LocalMonitor.Health;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> whose clock only moves when the test
/// calls <see cref="Advance"/>. Shared by the health and readiness-failure tests so
/// stall / projection-lag windows are exercised without real waiting.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset now;

    public MutableTimeProvider(DateTimeOffset start)
    {
        now = start;
    }

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan delta) => now += delta;
}

internal static class MonitorTestHealth
{
    /// <summary>
    /// A fully healthy, caught-up readiness state bound to <paramref name="time"/>:
    /// loopback bound, migration complete, writer and projection worker running, and
    /// a known projection status with zero backlog / zero lag.
    /// </summary>
    public static MonitorHealthState Ready(MutableTimeProvider time)
    {
        var health = new MonitorHealthState(time);
        health.SetLoopbackBound(true);
        health.MarkMigrationComplete();
        health.SetWriterRunning(true);
        health.SetProjectionWorkerRunning(true);
        health.SetProjectionStatus(backlog: 0, oldestUnprocessedReceivedAt: null);
        return health;
    }
}

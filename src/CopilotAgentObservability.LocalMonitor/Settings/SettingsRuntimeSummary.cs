using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Settings;

internal sealed class SettingsRuntimeSummary(
    RawTelemetryStore store,
    MonitorHealthState health,
    TimeProvider timeProvider,
    int ingestionStallThresholdSeconds,
    int projectionLagThresholdSeconds)
{
    private static readonly HashSet<string> CaptureReasons =
    [
        "loopback_unbound", "db_unavailable", "migration_failed", "fatal_error", "ingestion_stalled",
        "ingestion_backpressure", "writer_not_running",
    ];

    private DateTimeOffset? applicationStartedAt;
    private int port;

    internal void MarkApplicationStarted(int boundPort)
    {
        applicationStartedAt ??= timeProvider.GetUtcNow();
        port = boundPort;
    }

    internal object Read()
    {
        var readiness = health.Evaluate(ingestionStallThresholdSeconds, projectionLagThresholdSeconds);
        var snapshot = health.Snapshot();
        RawReceiveActivity? activity = null;
        try
        {
            activity = store.GetRawReceiveActivity(timeProvider.GetUtcNow().AddSeconds(-300));
        }
        catch
        {
        }

        return new
        {
            application_started_at = applicationStartedAt?.ToUniversalTime().ToString("O"),
            receiver_readiness = readiness.Status,
            endpoint = new { transport = "http", scope = "loopback", port },
            activity_state = activity is null ? "unavailable" : "available",
            latest_received_at = activity?.LatestReceivedAt?.ToUniversalTime().ToString("O"),
            recent_received_count = activity?.RecentReceivedCount,
            projection_backlog = snapshot.ProjectionStatusKnown ? snapshot.ProjectionBacklog : (int?)null,
            capture_reasons = readiness.DegradedReasons.Where(CaptureReasons.Contains).ToArray(),
            projection_reasons = readiness.DegradedReasons.Where(reason => !CaptureReasons.Contains(reason)).ToArray(),
            restart_requirement = "unavailable",
        };
    }
}

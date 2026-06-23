namespace CopilotAgentObservability.LocalMonitor.Health;

/// <summary>
/// Shared, thread-safe readiness state for the Local Ingestion Monitor. The
/// ingestion writer worker and the HTTP layer mutate it; the health endpoints
/// read it. M3 tracks ingestion/migration/writer state only; projection fields
/// remain absent (false / 0) until M4 adds the projection worker.
/// </summary>
internal sealed class MonitorHealthState
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;

    private bool loopbackBound;
    private bool dbOpen;
    private bool migrationComplete;
    private bool writerRunning;
    private bool fatalError;
    private DateTimeOffset? unableToCommitSince;
    private bool projectionWorkerRunning;
    private int projectionBacklog;
    private DateTimeOffset? oldestUnprocessedReceivedAt;
    private int projectionFailureCount;

    public MonitorHealthState(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void SetLoopbackBound(bool value)
    {
        lock (gate)
        {
            loopbackBound = value;
        }
    }

    public void SetDbOpen(bool value)
    {
        lock (gate)
        {
            dbOpen = value;
        }
    }

    public void SetWriterRunning(bool value)
    {
        lock (gate)
        {
            writerRunning = value;
        }
    }

    public void MarkMigrationComplete()
    {
        lock (gate)
        {
            migrationComplete = true;
            dbOpen = true;
        }
    }

    public void MarkMigrationFailed()
    {
        lock (gate)
        {
            migrationComplete = false;
        }
    }

    public void MarkFatal()
    {
        lock (gate)
        {
            fatalError = true;
        }
    }

    /// <summary>A commit (or accept) succeeded: clears any continuous-stall window.</summary>
    public void RecordCommitSuccess()
    {
        lock (gate)
        {
            unableToCommitSince = null;
        }
    }

    /// <summary>
    /// The writer is unable to accept/commit right now (queue full or commit
    /// failing). Starts the continuous-stall window if it is not already open.
    /// </summary>
    public void RecordBackpressure()
    {
        lock (gate)
        {
            unableToCommitSince ??= timeProvider.GetUtcNow();
        }
    }

    /// <summary>The projection worker has started (true) or stopped (false).</summary>
    public void SetProjectionWorkerRunning(bool value)
    {
        lock (gate)
        {
            projectionWorkerRunning = value;
        }
    }

    /// <summary>
    /// The projection worker's latest view of the backlog and the oldest
    /// unprocessed ingestion time. Readiness derives live projection lag from the
    /// oldest timestamp, so a stalled worker still shows growing lag.
    /// </summary>
    public void SetProjectionStatus(int backlog, DateTimeOffset? oldestUnprocessedReceivedAt)
    {
        lock (gate)
        {
            projectionBacklog = backlog;
            this.oldestUnprocessedReceivedAt = oldestUnprocessedReceivedAt;
        }
    }

    /// <summary>A projection attempt failed for a non-busy reason; the raw record is retained and retried.</summary>
    public void RecordProjectionFailure()
    {
        lock (gate)
        {
            projectionFailureCount++;
        }
    }

    public MonitorHealthSnapshot Snapshot()
    {
        lock (gate)
        {
            return new MonitorHealthSnapshot(
                LoopbackBound: loopbackBound,
                DbOpen: dbOpen,
                MigrationComplete: migrationComplete,
                WriterRunning: writerRunning,
                FatalError: fatalError,
                UnableToCommitSince: unableToCommitSince,
                ProjectionWorkerRunning: projectionWorkerRunning,
                ProjectionBacklog: projectionBacklog,
                OldestUnprocessedReceivedAt: oldestUnprocessedReceivedAt,
                ProjectionFailureCount: projectionFailureCount);
        }
    }

    /// <summary>
    /// Evaluates the readiness contract. In M3 the result is always
    /// <c>not_ready</c> because the projection worker is deferred to M4; the
    /// reasons explain why (projection_worker_missing when ingestion is otherwise
    /// healthy, or migration_failed / fatal_error / ingestion_stalled).
    /// </summary>
    public MonitorReadiness Evaluate(int ingestionStallThresholdSeconds)
    {
        lock (gate)
        {
            var reasons = new List<string>();
            var blocking = false;

            if (!migrationComplete)
            {
                reasons.Add("migration_failed");
                blocking = true;
            }

            if (fatalError)
            {
                reasons.Add("fatal_error");
                blocking = true;
            }

            if (unableToCommitSince is { } since
                && (timeProvider.GetUtcNow() - since).TotalSeconds >= ingestionStallThresholdSeconds)
            {
                reasons.Add("ingestion_stalled");
                blocking = true;
            }

            // M3 deliberately ships without a projection worker; surface that as
            // the reason whenever ingestion itself is otherwise healthy.
            const bool projectionWorkerRunning = false;
            if (!blocking)
            {
                reasons.Add("projection_worker_missing");
            }

            return new MonitorReadiness(
                Status: "not_ready",
                LoopbackBound: loopbackBound,
                DbOpen: dbOpen,
                MigrationComplete: migrationComplete,
                WriterRunning: writerRunning,
                ProjectionWorkerRunning: projectionWorkerRunning,
                IngestionAccepting: unableToCommitSince is null,
                ProjectionLagSeconds: 0,
                ProjectionBacklog: 0,
                DegradedReasons: reasons);
        }
    }
}

internal sealed record MonitorHealthSnapshot(
    bool LoopbackBound,
    bool DbOpen,
    bool MigrationComplete,
    bool WriterRunning,
    bool FatalError,
    DateTimeOffset? UnableToCommitSince,
    bool ProjectionWorkerRunning,
    int ProjectionBacklog,
    DateTimeOffset? OldestUnprocessedReceivedAt,
    int ProjectionFailureCount);

internal sealed record MonitorReadiness(
    string Status,
    bool LoopbackBound,
    bool DbOpen,
    bool MigrationComplete,
    bool WriterRunning,
    bool ProjectionWorkerRunning,
    bool IngestionAccepting,
    int ProjectionLagSeconds,
    int ProjectionBacklog,
    IReadOnlyList<string> DegradedReasons)
{
    /// <summary>HTTP mapping: <c>ready</c> and <c>degraded</c> map to 200; <c>not_ready</c> maps to 503.</summary>
    public bool IsReady => Status is "ready" or "degraded";
}

internal static class MonitorReadinessJson
{
    public static string Serialize(MonitorReadiness readiness)
    {
        var body = new
        {
            status = readiness.Status,
            checks = new
            {
                loopback_bound = readiness.LoopbackBound,
                db_open = readiness.DbOpen,
                migration_complete = readiness.MigrationComplete,
                writer_running = readiness.WriterRunning,
                projection_worker_running = readiness.ProjectionWorkerRunning,
                ingestion_accepting = readiness.IngestionAccepting,
                projection_lag_seconds = readiness.ProjectionLagSeconds,
                projection_backlog = readiness.ProjectionBacklog,
            },
            degraded_reasons = readiness.DegradedReasons,
        };

        return JsonSerializer.Serialize(body);
    }
}

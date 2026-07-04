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
    private bool projectionStatusKnown;
    private int projectionBacklog;
    private DateTimeOffset? oldestUnprocessedReceivedAt;
    private int spanProjectionBacklog;
    private DateTimeOffset? oldestUnprocessedSpanReceivedAt;
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
            projectionStatusKnown = true;
        }
    }

    /// <summary>
    /// The projection worker's latest view of the independent per-span projection
    /// backlog. This is surfaced for upgraded databases without making span
    /// backfill a readiness gate by itself.
    /// </summary>
    public void SetSpanProjectionStatus(int backlog, DateTimeOffset? oldestUnprocessedReceivedAt)
    {
        lock (gate)
        {
            spanProjectionBacklog = backlog;
            oldestUnprocessedSpanReceivedAt = oldestUnprocessedReceivedAt;
        }
    }

    /// <summary>
    /// The projection worker could not read backlog / lag this pass (list or status
    /// read failed). Until a successful refresh, lag is unknown and readiness must
    /// not claim ready on a stale zero-lag snapshot.
    /// </summary>
    public void RecordProjectionStatusUnavailable()
    {
        lock (gate)
        {
            projectionStatusKnown = false;
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
                SpanProjectionBacklog: spanProjectionBacklog,
                OldestUnprocessedSpanReceivedAt: oldestUnprocessedSpanReceivedAt,
                ProjectionFailureCount: projectionFailureCount);
        }
    }

    /// <summary>
    /// Evaluates the readiness contract over both the ingestion-stall and
    /// projection-lag thresholds. <c>not_ready</c> (HTTP 503) on any blocking
    /// condition (migration_failed / fatal_error / ingestion_stalled /
    /// projection_worker_missing / projection_lag_exceeded); <c>degraded</c>
    /// (HTTP 200) on momentary backpressure or sub-threshold projection lag;
    /// otherwise <c>ready</c> (HTTP 200, empty reasons).
    /// </summary>
    public MonitorReadiness Evaluate(int ingestionStallThresholdSeconds, int projectionLagThresholdSeconds)
    {
        lock (gate)
        {
            var blocking = new List<string>();
            var degraded = new List<string>();

            // Required infrastructure gates: ready demands loopback bind, an open DB,
            // a completed migration, and a running ingestion writer (telemetry-ingestion.md).
            if (!loopbackBound)
            {
                blocking.Add("loopback_unbound");
            }

            if (!dbOpen)
            {
                blocking.Add("db_unavailable");
            }

            if (!migrationComplete)
            {
                blocking.Add("migration_failed");
            }

            if (fatalError)
            {
                blocking.Add("fatal_error");
            }

            var ingestionAccepting = unableToCommitSince is null;
            if (unableToCommitSince is { } since)
            {
                if ((timeProvider.GetUtcNow() - since).TotalSeconds >= ingestionStallThresholdSeconds)
                {
                    blocking.Add("ingestion_stalled");
                }
                else
                {
                    degraded.Add("ingestion_backpressure");
                }
            }

            if (!writerRunning)
            {
                blocking.Add("writer_not_running");
            }

            if (!projectionWorkerRunning)
            {
                blocking.Add("projection_worker_missing");
            }
            else if (!projectionStatusKnown)
            {
                // Worker is running but no successful backlog/lag read yet (startup or
                // sustained status-read failure): lag is unknown, so do not claim ready.
                blocking.Add("projection_status_unknown");
            }

            var lagSeconds = ComputeProjectionLagSeconds();
            var spanLagSeconds = ComputeSpanProjectionLagSeconds();
            if (lagSeconds >= projectionLagThresholdSeconds)
            {
                blocking.Add("projection_lag_exceeded");
            }
            else if (lagSeconds > 0)
            {
                degraded.Add("projection_lag");
            }

            if (spanProjectionBacklog > 0 || spanLagSeconds > 0)
            {
                degraded.Add("span_projection_backlog");
            }

            string status;
            List<string> reasons;
            if (blocking.Count > 0)
            {
                status = "not_ready";
                reasons = blocking.Concat(degraded).ToList();
            }
            else if (degraded.Count > 0)
            {
                status = "degraded";
                reasons = degraded;
            }
            else
            {
                status = "ready";
                reasons = new List<string>();
            }

            return new MonitorReadiness(
                Status: status,
                LoopbackBound: loopbackBound,
                DbOpen: dbOpen,
                MigrationComplete: migrationComplete,
                WriterRunning: writerRunning,
                ProjectionWorkerRunning: projectionWorkerRunning,
                IngestionAccepting: ingestionAccepting,
                ProjectionLagSeconds: lagSeconds,
                ProjectionBacklog: projectionBacklog,
                SpanProjectionLagSeconds: spanLagSeconds,
                SpanProjectionBacklog: spanProjectionBacklog,
                ProjectionFailureCount: projectionFailureCount,
                DegradedReasons: reasons);
        }
    }

    private int ComputeProjectionLagSeconds()
    {
        if (oldestUnprocessedReceivedAt is not { } oldest)
        {
            return 0;
        }

        var seconds = (timeProvider.GetUtcNow() - oldest).TotalSeconds;
        return seconds <= 0 ? 0 : (int)Math.Floor(seconds);
    }

    private int ComputeSpanProjectionLagSeconds()
    {
        if (oldestUnprocessedSpanReceivedAt is not { } oldest)
        {
            return 0;
        }

        var seconds = (timeProvider.GetUtcNow() - oldest).TotalSeconds;
        return seconds <= 0 ? 0 : (int)Math.Floor(seconds);
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
    int SpanProjectionBacklog,
    DateTimeOffset? OldestUnprocessedSpanReceivedAt,
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
    int SpanProjectionLagSeconds,
    int SpanProjectionBacklog,
    int ProjectionFailureCount,
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
                span_projection_lag_seconds = readiness.SpanProjectionLagSeconds,
                span_projection_backlog = readiness.SpanProjectionBacklog,
                projection_failure_count = readiness.ProjectionFailureCount,
            },
            degraded_reasons = readiness.DegradedReasons,
        };

        return JsonSerializer.Serialize(body);
    }
}

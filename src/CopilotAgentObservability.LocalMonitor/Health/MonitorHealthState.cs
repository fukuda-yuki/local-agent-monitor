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
                UnableToCommitSince: unableToCommitSince);
        }
    }
}

internal sealed record MonitorHealthSnapshot(
    bool LoopbackBound,
    bool DbOpen,
    bool MigrationComplete,
    bool WriterRunning,
    bool FatalError,
    DateTimeOffset? UnableToCommitSince);

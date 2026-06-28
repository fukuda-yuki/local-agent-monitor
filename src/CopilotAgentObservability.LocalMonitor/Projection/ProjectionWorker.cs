using CopilotAgentObservability.LocalMonitor.Events;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Telemetry;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Projection;

/// <summary>
/// Builds the sanitized <c>monitor_ingestions</c> / <c>monitor_traces</c>
/// projections from unprocessed <c>raw_records</c>. A separate writer from the
/// ingestion writer (it owns only the projection tables), it catches up at
/// startup, retries on <see cref="PersistenceBusyException"/>, isolates non-busy
/// projection failures without losing raw, and pushes backlog / oldest-unprocessed
/// status into <see cref="MonitorHealthState"/> for projection-lag readiness.
/// </summary>
internal sealed class ProjectionWorker : BackgroundService
{
    private const int BatchSize = 100;

    private readonly IMonitorProjectionStore store;
    private readonly MonitorHealthState health;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly MonitorEventBroker? eventBroker;

    public ProjectionWorker(
        IMonitorProjectionStore store,
        MonitorHealthState health,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        MonitorEventBroker? eventBroker = null)
    {
        this.store = store;
        this.health = health;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        this.eventBroker = eventBroker;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        health.SetProjectionWorkerRunning(true);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        health.SetProjectionWorkerRunning(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunProjectionPass(stoppingToken);

                try
                {
                    await Task.Delay(pollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            health.SetProjectionWorkerRunning(false);
        }
    }

    /// <summary>
    /// Runs one projection pass. Exposed for deterministic tests; the
    /// <see cref="ExecuteAsync"/> loop calls it on the poll interval.
    /// </summary>
    internal Task RunProjectionPassAsync(CancellationToken cancellationToken = default)
    {
        RunProjectionPass(cancellationToken);
        return Task.CompletedTask;
    }

    private void RunProjectionPass(CancellationToken cancellationToken)
    {
        // The ingestion writer owns the additive migration; do not project until
        // the projection tables exist, so worker start order is not relied upon.
        if (!health.Snapshot().MigrationComplete)
        {
            return;
        }

        try
        {
            var records = store.ListUnprocessedForProjection(BatchSize);
            var anyProjected = false;
            foreach (var record in records)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var projection = MonitorProjectionBuilder.Build(record);
                    var projected = store.ApplyProjection(
                        record.Id!.Value,
                        record.Source,
                        record.ReceivedAt,
                        projection,
                        timeProvider.GetUtcNow());
                    if (projected)
                    {
                        anyProjected = true;
                    }
                }
                catch (PersistenceBusyException)
                {
                    // DB busy: stop this pass; this record and the rest retry next cycle.
                    break;
                }
                catch (Exception)
                {
                    // Non-busy projection failure: keep the raw record, record the
                    // failure, and continue with the next record.
                    health.RecordProjectionFailure();
                }
            }

            var status = store.GetProjectionStatus();
            health.SetProjectionStatus(status.Backlog, status.OldestUnprocessedReceivedAt);

            // Phase 2: span projection (runs after trace projection in the same pass).
            var spanRecords = store.ListUnprocessedForSpanProjection(BatchSize);
            foreach (var record in spanRecords)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var spans = MonitorSpanProjectionBuilder.Build(record);
                    var newlyProjected = store.ApplySpanProjection(
                        record.Id!.Value,
                        spans,
                        timeProvider.GetUtcNow());
                    if (newlyProjected)
                    {
                        anyProjected = true;
                    }
                }
                catch (PersistenceBusyException)
                {
                    // DB busy: stop this pass; retry next cycle.
                    break;
                }
                catch (Exception)
                {
                    // Non-busy span projection failure: record failure and continue.
                    health.RecordProjectionFailure();
                }
            }

            var spanStatus = store.GetSpanProjectionStatus();
            health.SetSpanProjectionStatus(spanStatus.Backlog, spanStatus.OldestUnprocessedReceivedAt);

            if (anyProjected)
            {
                eventBroker?.PublishProjectionChanged();
            }
        }
        catch (PersistenceBusyException)
        {
            // Listing or status read was busy; lag is unknown until a successful
            // refresh, so readiness must not report ready on a stale snapshot.
            health.RecordProjectionStatusUnavailable();
        }
        catch (Exception)
        {
            health.RecordProjectionFailure();
            health.RecordProjectionStatusUnavailable();
        }
    }
}

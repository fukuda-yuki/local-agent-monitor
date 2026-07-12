using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Ingestion;

/// <summary>
/// The single SQLite writer. Owns all monitor writes: runs the additive
/// migration at startup, drains the ingestion queue sequentially, persists each
/// already-validated record, completes each ack with a typed result, and keeps
/// <see cref="MonitorHealthState"/> current. On shutdown it stops accepting new
/// work and drains already-accepted queued items.
/// </summary>
internal sealed class IngestionWriterWorker : BackgroundService
{
    private readonly IngestionQueue queue;
    private readonly IIngestionCommitStore commitStore;
    private readonly ISourceCompatibilityStore compatibilityStore;
    private readonly MonitorHealthState health;
    private readonly TaskCompletionSource readerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool migrated;

    public IngestionWriterWorker(
        IngestionQueue queue,
        IIngestionCommitStore commitStore,
        ISourceCompatibilityStore compatibilityStore,
        MonitorHealthState health)
    {
        this.queue = queue;
        this.commitStore = commitStore;
        this.compatibilityStore = compatibilityStore;
        this.health = health;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Migrate synchronously during host startup so the schema exists before
        // the host accepts requests and before a concurrent external reader opens
        // the database. Migration failure is surfaced as not-ready, not a crash.
        if (TryMigrate())
        {
            migrated = true;
            health.MarkMigrationComplete();
            health.SetWriterRunning(true);
        }
        else
        {
            migrated = false;
            health.MarkMigrationFailed();
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        await readerStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        readerStarted.TrySetResult();
        try
        {
            // Intentionally not cancelled by stoppingToken: shutdown completes the
            // queue (see StopAsync) so already-accepted items are drained first.
            await foreach (var request in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (migrated)
                {
                    ProcessRequest(request);
                }
                else
                {
                    health.RecordBackpressure();
                    request.Complete(IngestionCommitResult.Failed);
                }
            }
        }
        catch (Exception)
        {
            health.MarkFatal();
        }
        finally
        {
            health.SetWriterRunning(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.CompleteAdding();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool TryMigrate()
    {
        try
        {
            compatibilityStore.CreateSchema();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ProcessRequest(IngestionWriteRequest request)
    {
        try
        {
            IngestionCommitResult result;
            if (request.Batch is { } batch)
            {
                var committed = commitStore.Commit(batch);
                result = IngestionCommitResult.Committed(committed.RawRecordId, committed.ObservationId);
            }
            else
            {
                var observationId = compatibilityStore.RecordAdapterFailure(
                    request.AdapterFailure ?? throw new InvalidOperationException("An ingestion request has no write payload."));
                result = IngestionCommitResult.AdapterFailureRecorded(observationId);
            }
            health.RecordCommitSuccess();
            request.Complete(result);
        }
        catch (IngestionCommitBusyException)
        {
            health.RecordBackpressure();
            request.Complete(IngestionCommitResult.Busy);
        }
        catch (IngestionCommitFailedException)
        {
            health.RecordBackpressure();
            request.Complete(IngestionCommitResult.Failed);
        }
        catch (Exception)
        {
            // Isolate the request: an unexpected persistence error must not stop
            // the worker or leave the awaiting HTTP ack hanging. Complete it as a
            // non-busy failure and keep draining the queue.
            health.RecordBackpressure();
            request.Complete(IngestionCommitResult.Failed);
        }
    }
}

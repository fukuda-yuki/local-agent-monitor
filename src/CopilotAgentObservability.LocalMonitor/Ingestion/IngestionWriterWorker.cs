using CopilotAgentObservability.LocalMonitor.Health;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Ingestion;

/// <summary>
/// Persistence seam owned by the single ingestion writer. Concrete persistence
/// exceptions are classified here into busy vs. non-busy so the worker can map
/// them to typed ack results without leaking raw exception text.
/// </summary>
internal interface IRawTelemetryWriter
{
    void EnsureSchema();

    long Insert(RawTelemetryRecord record);
}

internal sealed class PersistenceBusyException : Exception
{
}

internal sealed class PersistenceFailedException : Exception
{
}

internal sealed class RawTelemetryStoreWriter : IRawTelemetryWriter
{
    private readonly RawTelemetryStore store;

    public RawTelemetryStoreWriter(RawTelemetryStore store)
    {
        this.store = store;
    }

    public void EnsureSchema()
    {
        store.CreateMonitorSchema();
    }

    public long Insert(RawTelemetryRecord record)
    {
        try
        {
            return store.Insert(record);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new PersistenceBusyException();
        }
        catch (SqliteException)
        {
            throw new PersistenceFailedException();
        }
        catch (IOException)
        {
            throw new PersistenceFailedException();
        }
        catch (UnauthorizedAccessException)
        {
            throw new PersistenceFailedException();
        }
    }
}

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
    private readonly IRawTelemetryWriter writer;
    private readonly MonitorHealthState health;
    private readonly TaskCompletionSource readerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool migrated;

    public IngestionWriterWorker(
        IngestionQueue queue,
        IRawTelemetryWriter writer,
        MonitorHealthState health)
    {
        this.queue = queue;
        this.writer = writer;
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
            writer.EnsureSchema();
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
            var rawRecordId = writer.Insert(request.Record);
            health.RecordCommitSuccess();
            request.Complete(IngestionCommitResult.Committed(rawRecordId));
        }
        catch (PersistenceBusyException)
        {
            health.RecordBackpressure();
            request.Complete(IngestionCommitResult.Busy);
        }
        catch (PersistenceFailedException)
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

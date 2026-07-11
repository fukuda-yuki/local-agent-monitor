using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed class SessionEventWriterWorker : BackgroundService
{
    private readonly SessionEventQueue queue;
    private readonly SessionEventNormalizer normalizer;
    private readonly TaskCompletionSource readerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SessionEventWriterWorker(SessionEventQueue queue, SessionEventNormalizer normalizer)
    {
        this.queue = queue;
        this.normalizer = normalizer;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        await readerStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        readerStarted.TrySetResult();
        await foreach (var request in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            queue.MarkDequeued();
            try
            {
                normalizer.NormalizeAndWrite(request.Envelope);
                request.Complete(SessionEventCommitStatus.Committed);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                request.Complete(SessionEventCommitStatus.Busy);
            }
            catch
            {
                request.Complete(SessionEventCommitStatus.Failed);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.CompleteAdding();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

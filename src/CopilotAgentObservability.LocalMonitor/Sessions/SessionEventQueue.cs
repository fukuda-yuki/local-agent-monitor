using System.Threading.Channels;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal enum SessionEventCommitStatus { Committed, Busy, Failed }

internal sealed class SessionEventWriteRequest
{
    private enum Lifecycle { Pending, Claimed, Completed, Abandoned }

    private readonly TaskCompletionSource<SessionEventCommitStatus> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int lifecycle;

    public SessionEventWriteRequest(SessionIngestEnvelope envelope) => Envelope = envelope;
    public SessionIngestEnvelope Envelope { get; }
    public Task<SessionEventCommitStatus> Completion => completion.Task;
    public bool TryClaim() => Interlocked.CompareExchange(
        ref lifecycle, (int)Lifecycle.Claimed, (int)Lifecycle.Pending) == (int)Lifecycle.Pending;

    public bool TryAbandon()
    {
        if (Interlocked.CompareExchange(
            ref lifecycle, (int)Lifecycle.Abandoned, (int)Lifecycle.Pending) != (int)Lifecycle.Pending) return false;
        completion.TrySetResult(SessionEventCommitStatus.Failed);
        return true;
    }

    public void Complete(SessionEventCommitStatus status)
    {
        if (Interlocked.CompareExchange(
            ref lifecycle, (int)Lifecycle.Completed, (int)Lifecycle.Claimed) == (int)Lifecycle.Claimed)
            completion.TrySetResult(status);
    }
}

internal sealed class SessionEventQueue
{
    public const int DefaultCapacity = 256;
    private readonly Channel<SessionEventWriteRequest> channel;
    private int count;
    private volatile bool closed;

    public SessionEventQueue(int capacity = DefaultCapacity)
    {
        channel = Channel.CreateBounded<SessionEventWriteRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<SessionEventWriteRequest> Reader => channel.Reader;
    public int Count => Volatile.Read(ref count);
    public bool IsClosed => closed;

    public bool TryEnqueue(SessionIngestEnvelope envelope, [NotNullWhen(true)] out SessionEventWriteRequest? request)
    {
        var candidate = new SessionEventWriteRequest(envelope);
        if (!channel.Writer.TryWrite(candidate))
        {
            request = null;
            return false;
        }
        Interlocked.Increment(ref count);
        request = candidate;
        return true;
    }

    public void MarkDequeued() => Interlocked.Decrement(ref count);

    public void CompleteAdding()
    {
        closed = true;
        channel.Writer.TryComplete();
    }
}

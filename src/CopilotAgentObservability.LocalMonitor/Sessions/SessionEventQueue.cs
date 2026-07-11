using System.Threading.Channels;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal enum SessionEventCommitStatus { Committed, Busy, Failed }

internal sealed class SessionEventWriteRequest
{
    private readonly TaskCompletionSource<SessionEventCommitStatus> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SessionEventWriteRequest(SessionIngestEnvelope envelope) => Envelope = envelope;
    public SessionIngestEnvelope Envelope { get; }
    public Task<SessionEventCommitStatus> Completion => completion.Task;
    public void Complete(SessionEventCommitStatus status) => completion.TrySetResult(status);
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

using System.Threading.Channels;

namespace CopilotAgentObservability.LocalMonitor.Ingestion;

internal sealed class IngestionQueue
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<IngestionWriteRequest> channel;
    private readonly TimeProvider timeProvider;
    private volatile bool closed;

    public IngestionQueue(TimeProvider? timeProvider = null)
        : this(DefaultCapacity, timeProvider)
    {
    }

    internal IngestionQueue(int capacity, TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        channel = Channel.CreateBounded<IngestionWriteRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<IngestionWriteRequest> Reader => channel.Reader;

    /// <summary>True once the queue has stopped accepting new work (shutdown).</summary>
    public bool IsClosed => closed;

    public bool TryEnqueue(RawTelemetryRecord record, [NotNullWhen(true)] out IngestionWriteRequest? request)
    {
        var candidate = new IngestionWriteRequest(record, timeProvider.GetUtcNow());
        if (channel.Writer.TryWrite(candidate))
        {
            request = candidate;
            return true;
        }

        request = null;
        return false;
    }

    public void CompleteAdding()
    {
        closed = true;
        channel.Writer.TryComplete();
    }
}

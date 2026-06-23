using System.Threading.Channels;

namespace CopilotAgentObservability.LocalMonitor.Ingestion;

internal sealed class IngestionQueue
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<IngestionWriteRequest> channel;
    private readonly TimeProvider timeProvider;

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

    public void CompleteAdding() => channel.Writer.TryComplete();
}

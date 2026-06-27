using System.Threading.Channels;

namespace CopilotAgentObservability.LocalMonitor.Events;

/// <summary>
/// In-memory fan-out of notification-only "projection changed" events to SSE
/// subscribers. Each subscriber gets a bounded channel (drop-oldest) so a slow
/// reader cannot back up the worker. Events carry an id and a type only — never
/// raw payloads, trace ids, raw record ids, or PII. SSE is not a source of truth;
/// reconnecting clients recover gaps via the <c>/api/monitor/*</c> cursors.
/// </summary>
internal sealed class MonitorEventBroker
{
    private readonly object gate = new();
    private readonly List<Channel<MonitorEvent>> subscribers = [];
    private long nextId;

    public MonitorSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (gate)
        {
            subscribers.Add(channel);
        }

        return new MonitorSubscription(this, channel);
    }

    public void PublishProjectionChanged()
    {
        var evt = new MonitorEvent(Interlocked.Increment(ref nextId), "projection");

        Channel<MonitorEvent>[] snapshot;
        lock (gate)
        {
            snapshot = subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            subscriber.Writer.TryWrite(evt);
        }
    }

    private void Unsubscribe(Channel<MonitorEvent> channel)
    {
        lock (gate)
        {
            subscribers.Remove(channel);
        }
    }

    internal sealed class MonitorSubscription : IDisposable
    {
        private readonly MonitorEventBroker broker;
        private readonly Channel<MonitorEvent> channel;
        private bool disposed;

        public MonitorSubscription(MonitorEventBroker broker, Channel<MonitorEvent> channel)
        {
            this.broker = broker;
            this.channel = channel;
        }

        public ChannelReader<MonitorEvent> Reader => channel.Reader;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            broker.Unsubscribe(channel);
        }
    }
}

namespace CopilotAgentObservability.LocalMonitor;

internal sealed record RawReplayTransientLimits(
    int MaximumEntries,
    long MaximumBytes,
    TimeSpan Lifetime,
    TimeSpan SweepInterval)
{
    internal static RawReplayTransientLimits Default { get; } = new(
        8,
        256L * 1024 * 1024,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(1));
}

internal sealed class RawReplayTransientStore : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(string Kind, string Key), Entry> entries = [];
    private readonly TimeProvider timeProvider;
    private readonly RawReplayTransientLimits limits;
    private readonly ITimer sweepTimer;
    private long nextSequence;
    private long totalBytes;
    private bool disposed;

    internal RawReplayTransientStore(TimeProvider timeProvider, RawReplayTransientLimits limits)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.limits = Validate(limits);
        sweepTimer = timeProvider.CreateTimer(
            static state => ((RawReplayTransientStore)state!).Sweep(),
            this,
            limits.SweepInterval,
            limits.SweepInterval);
    }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    internal long TotalBytes
    {
        get
        {
            lock (gate)
            {
                return totalBytes;
            }
        }
    }

    internal DateTimeOffset ExpirationFromNow() => timeProvider.GetUtcNow().Add(limits.Lifetime);

    internal bool Put(string kind, string key, byte[] bytes, object metadata)
    {
        var now = timeProvider.GetUtcNow();
        return Put(kind, key, bytes, metadata, now, now.Add(limits.Lifetime));
    }

    internal bool Put(string kind, string key, byte[] bytes, object metadata, DateTimeOffset expiresAt)
    {
        var now = timeProvider.GetUtcNow();
        return Put(kind, key, bytes, metadata, now, expiresAt);
    }

    internal bool TryGet<T>(string kind, string key, out byte[] bytes, out T metadata)
    {
        bytes = [];
        metadata = default!;
        if (!TryKey(kind, key, out var itemKey)) return false;
        lock (gate)
        {
            if (disposed) return false;
            PurgeExpired(timeProvider.GetUtcNow());
            if (!entries.TryGetValue(itemKey, out var entry) || entry.Metadata is not T value) return false;
            bytes = entry.Bytes.ToArray();
            metadata = value;
            return true;
        }
    }

    internal bool TryGetMetadata<T>(string kind, string key, out T metadata)
    {
        metadata = default!;
        if (!TryKey(kind, key, out var itemKey)) return false;
        lock (gate)
        {
            if (disposed) return false;
            PurgeExpired(timeProvider.GetUtcNow());
            if (!entries.TryGetValue(itemKey, out var entry) || entry.Metadata is not T value) return false;
            metadata = value;
            return true;
        }
    }

    internal bool TryTake<T>(string kind, string key, out byte[] bytes, out T metadata)
    {
        bytes = [];
        metadata = default!;
        if (!TryKey(kind, key, out var itemKey)) return false;
        lock (gate)
        {
            if (disposed) return false;
            PurgeExpired(timeProvider.GetUtcNow());
            if (!entries.TryGetValue(itemKey, out var entry) || entry.Metadata is not T value) return false;
            entries.Remove(itemKey);
            totalBytes -= entry.Bytes.LongLength;
            bytes = entry.Bytes;
            metadata = value;
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            entries.Clear();
            totalBytes = 0;
        }
        sweepTimer.Dispose();
    }

    private bool Put(
        string kind,
        string key,
        byte[] bytes,
        object metadata,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        var itemKey = Key(kind, key);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(metadata);
        lock (gate)
        {
            if (disposed) return false;
            PurgeExpired(now);
            if (bytes.LongLength > limits.MaximumBytes || expiresAt <= now || expiresAt > now.Add(limits.Lifetime)) return false;

            if (entries.Remove(itemKey, out var replaced)) totalBytes -= replaced.Bytes.LongLength;
            var entry = new Entry(bytes.ToArray(), metadata, expiresAt, nextSequence++);
            entries.Add(itemKey, entry);
            totalBytes += entry.Bytes.LongLength;
            EvictOldestUntilBounded();
            return entries.ContainsKey(itemKey);
        }
    }

    private void Sweep()
    {
        lock (gate)
        {
            if (!disposed) PurgeExpired(timeProvider.GetUtcNow());
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var item in entries.Where(item => item.Value.ExpiresAt <= now).ToArray()) Remove(item.Key, item.Value);
    }

    private void EvictOldestUntilBounded()
    {
        while (entries.Count > limits.MaximumEntries || totalBytes > limits.MaximumBytes)
        {
            var oldest = entries.MinBy(static item => item.Value.Sequence);
            Remove(oldest.Key, oldest.Value);
        }
    }

    private void Remove((string Kind, string Key) key, Entry entry)
    {
        if (!entries.Remove(key)) return;
        totalBytes -= entry.Bytes.LongLength;
    }

    private static (string Kind, string Key) Key(string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return (kind, key);
    }

    private static bool TryKey(string? kind, string? key, out (string Kind, string Key) itemKey)
    {
        itemKey = default;
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key)) return false;
        itemKey = (kind, key);
        return true;
    }

    private static RawReplayTransientLimits Validate(RawReplayTransientLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        if (limits.MaximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        if (limits.Lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limits));
        if (limits.SweepInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limits));
        return limits;
    }

    private sealed record Entry(byte[] Bytes, object Metadata, DateTimeOffset ExpiresAt, long Sequence);
}

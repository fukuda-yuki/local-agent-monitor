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

internal enum RawReplayTransientPreparationCheckpoint
{
    BeforeCopy,
    BeforeClock,
    BeforeValidation,
    BeforeCapacity,
}

internal sealed class RawReplayTransientStore : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(string Kind, string Key), Entry> entries = [];
    private readonly Dictionary<(string Kind, string Key), ReservedEntry> reservations = [];
    private readonly TimeProvider timeProvider;
    private readonly RawReplayTransientLimits limits;
    private readonly ITimer sweepTimer;
    private readonly Action<RawReplayTransientPreparationCheckpoint>? preparationCheckpointForTesting;
    private long nextSequence;
    private long totalBytes;
    private long reservedBytes;
    private int acceptingReservations = 1;
    private bool disposed;

    internal RawReplayTransientStore(
        TimeProvider timeProvider,
        RawReplayTransientLimits limits,
        Action<RawReplayTransientPreparationCheckpoint>? preparationCheckpointForTesting = null)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.limits = Validate(limits);
        this.preparationCheckpointForTesting = preparationCheckpointForTesting;
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

    internal bool TryReserve(
        string kind,
        string key,
        long byteLength,
        out RawReplayTransientReservation? reservation)
    {
        reservation = null;
        if (Volatile.Read(ref acceptingReservations) == 0) return false;
        var itemKey = Key(kind, key);
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (disposed || byteLength < 0 || byteLength > limits.MaximumBytes
                || Volatile.Read(ref acceptingReservations) == 0
                || reservations.ContainsKey(itemKey))
                return false;
            PurgeExpired(now);
            var plannedSequences = reservations.Values
                .SelectMany(static reserved => reserved.Victims)
                .Select(static victim => victim.Sequence)
                .ToHashSet();
            var reservedCountGrowth = 0;
            long reservedByteGrowth = 0;
            foreach (var reserved in reservations.Values)
            {
                var liveVictims = reserved.Victims
                    .Select(victim => entries.TryGetValue(victim.Key, out var current) && current.Sequence == victim.Sequence
                        ? current
                        : null)
                    .Where(static victim => victim is not null)
                    .ToArray();
                reservedCountGrowth += Math.Max(0, 1 - liveVictims.Length);
                reservedByteGrowth += Math.Max(
                    0,
                    reserved.ByteLength - liveVictims.Sum(static victim => victim!.Bytes.LongLength));
            }
            entries.TryGetValue(itemKey, out var replaced);
            if (replaced is not null && plannedSequences.Contains(replaced.Sequence)) return false;
            var plannedCount = entries.Count - (replaced is null ? 0 : 1);
            var plannedBytes = totalBytes - (replaced?.Bytes.LongLength ?? 0);
            var victims = new List<PlannedRemoval>();
            if (replaced is not null) victims.Add(new(itemKey, replaced.Sequence));
            foreach (var candidate in entries
                         .Where(entry => entry.Key != itemKey && !plannedSequences.Contains(entry.Value.Sequence))
                         .OrderBy(entry => entry.Value.Sequence))
            {
                if (plannedCount + reservedCountGrowth + 1 <= limits.MaximumEntries
                    && plannedBytes + reservedByteGrowth + byteLength <= limits.MaximumBytes)
                    break;
                victims.Add(new(candidate.Key, candidate.Value.Sequence));
                plannedCount--;
                plannedBytes -= candidate.Value.Bytes.LongLength;
            }
            if (plannedCount + reservedCountGrowth + 1 > limits.MaximumEntries
                || plannedBytes + reservedByteGrowth + byteLength > limits.MaximumBytes)
                return false;
            reservations.Add(itemKey, new(byteLength, victims.ToArray()));
            reservedBytes += byteLength;
            reservation = new RawReplayTransientReservation(this, itemKey, byteLength);
            return true;
        }
    }

    internal void StopAcceptingReservations() => Volatile.Write(ref acceptingReservations, 0);

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
        StopAcceptingReservations();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            reservations.Clear();
            reservedBytes = 0;
            entries.Clear();
            totalBytes = 0;
        }
        sweepTimer.Dispose();
    }

    private Entry PrepareReservation(
        (string Kind, string Key) itemKey,
        long byteLength,
        byte[] bytes,
        object metadata)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(metadata);
        if (bytes.LongLength != byteLength)
            throw new ArgumentException("The committed transient bytes must match the exact reservation.", nameof(bytes));
        preparationCheckpointForTesting?.Invoke(RawReplayTransientPreparationCheckpoint.BeforeCopy);
        var frozenBytes = bytes.ToArray();
        try
        {
            preparationCheckpointForTesting?.Invoke(RawReplayTransientPreparationCheckpoint.BeforeClock);
            var expiresAt = timeProvider.GetUtcNow().Add(limits.Lifetime);
            lock (gate)
            {
                preparationCheckpointForTesting?.Invoke(RawReplayTransientPreparationCheckpoint.BeforeValidation);
                if (!reservations.TryGetValue(itemKey, out var reserved)
                    || reserved.ByteLength != byteLength)
                    throw new InvalidOperationException("The transient reservation is no longer active.");
                preparationCheckpointForTesting?.Invoke(RawReplayTransientPreparationCheckpoint.BeforeCapacity);
                entries.EnsureCapacity(entries.Count + (entries.ContainsKey(itemKey) ? 0 : 1));
                return new(frozenBytes, metadata, expiresAt, nextSequence++);
            }
        }
        catch
        {
            Array.Clear(frozenBytes);
            throw;
        }
    }

    private void ActivateReservation((string Kind, string Key) itemKey, long byteLength, Entry prepared)
    {
        lock (gate)
        {
            if (!reservations.TryGetValue(itemKey, out var reserved)
                || reserved.ByteLength != byteLength)
                return;
            entries.TryGetValue(itemKey, out var replaced);
            entries[itemKey] = prepared;
            totalBytes += byteLength - (replaced?.Bytes.LongLength ?? 0);
            reservations.Remove(itemKey);
            reservedBytes -= byteLength;
            foreach (var victim in reserved.Victims)
            {
                if (victim.Key == itemKey) continue;
                if (entries.TryGetValue(victim.Key, out var current) && current.Sequence == victim.Sequence)
                    Remove(victim.Key, current);
            }
            Monitor.PulseAll(gate);
        }
    }

    private void CancelReservation((string Kind, string Key) itemKey, long byteLength)
    {
        lock (gate)
        {
            if (!reservations.Remove(itemKey)) return;
            reservedBytes -= byteLength;
            Monitor.PulseAll(gate);
        }
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
            if (reservations.ContainsKey(itemKey)) return false;

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
        while (entries.Count + reservations.Count > limits.MaximumEntries
               || totalBytes + reservedBytes > limits.MaximumBytes)
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
    private sealed record PlannedRemoval((string Kind, string Key) Key, long Sequence);
    private sealed record ReservedEntry(long ByteLength, PlannedRemoval[] Victims);

    internal sealed class RawReplayTransientReservation(
        RawReplayTransientStore store,
        (string Kind, string Key) itemKey,
        long byteLength) : IDisposable
    {
        private readonly object gate = new();
        private Entry? prepared;
        private int completed;

        internal void Prepare(byte[] bytes, object metadata)
        {
            lock (gate)
            {
                if (completed != 0)
                    throw new InvalidOperationException("The transient reservation is no longer active.");
                if (prepared is not null) return;
                prepared = store.PrepareReservation(itemKey, byteLength, bytes, metadata);
            }
        }

        internal void Activate()
        {
            lock (gate)
            {
                if (completed != 0 || prepared is null) return;
                completed = 1;
                var activated = prepared;
                prepared = null;
                store.ActivateReservation(itemKey, byteLength, activated);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (completed != 0) return;
                completed = 1;
                if (prepared is { } abandoned) Array.Clear(abandoned.Bytes);
                prepared = null;
                store.CancelReservation(itemKey, byteLength);
            }
        }
    }
}

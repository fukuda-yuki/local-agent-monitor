using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

internal sealed class SetupTestPlatform : ISetupPlatform
{
    private readonly object gate = new();
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> environment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Exception>> faults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SetupTestBarrierState> barriers = new(StringComparer.Ordinal);
    private readonly List<string> operations = [];

    public SetupTestPlatform(DateTimeOffset utcNow)
    {
        Clock = new SetupTestClock(utcNow);
        Identifiers = new SetupTestIdentifierGenerator();
        FileSystem = new SetupTestFileSystem(this);
        UserEnvironment = new SetupTestUserEnvironment(this);
        Execution = new SetupTestExecution(this);
    }

    public ISetupFileSystem FileSystem { get; }

    public ISetupUserEnvironment UserEnvironment { get; }

    public ISetupClock Clock { get; }

    public ISetupIdentifierGenerator Identifiers { get; }

    public ISetupExecution Execution { get; }

    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (gate)
            {
                return operations.ToArray();
            }
        }
    }

    public void InjectFault(string operation, Exception exception)
    {
        lock (gate)
        {
            if (!faults.TryGetValue(operation, out var queuedFaults))
            {
                queuedFaults = new Queue<Exception>();
                faults.Add(operation, queuedFaults);
            }

            queuedFaults.Enqueue(exception);
        }
    }

    public SetupTestBarrier AddBarrier(
        string operation,
        CancellationToken cancellationToken = default,
        TimeSpan? maximumWait = null)
    {
        var state = new SetupTestBarrierState(cancellationToken, maximumWait ?? TimeSpan.FromSeconds(10));
        lock (gate)
        {
            barriers.Add(operation, state);
        }

        return new SetupTestBarrier(this, operation, state);
    }

    public void SeedFile(string path, byte[] bytes) => files[path] = bytes.ToArray();

    public byte[] ReadSeededFile(string path) => files[path].ToArray();

    public void SeedUserEnvironment(string name, string? value) => environment[name] = value;

    public string? ReadUserEnvironment(string name) => environment.TryGetValue(name, out var value) ? value : null;

    private void Record(string operation)
    {
        SetupTestBarrierState? barrier;
        lock (gate)
        {
            operations.Add(operation);
            if (faults.TryGetValue(operation, out var queuedFaults) && queuedFaults.TryDequeue(out var exception))
            {
                throw exception;
            }

            barriers.TryGetValue(operation, out barrier);
            barrier?.AcquireLease();
        }

        if (barrier is null)
        {
            return;
        }

        try
        {
            lock (gate)
            {
                barrier.Arrive();
            }

            barrier.WaitForRelease();
        }
        finally
        {
            lock (gate)
            {
                barrier.ReleaseLease();
            }
        }
    }

    internal bool HasReached(SetupTestBarrierState state)
    {
        lock (gate)
        {
            return state.HasReached;
        }
    }

    internal void WaitUntilReached(SetupTestBarrierState state, CancellationToken cancellationToken)
    {
        WaitWithLease(state, barrier => barrier.WaitUntilReached(cancellationToken));
    }

    internal void WaitUntilArrivals(SetupTestBarrierState state, int expectedArrivals, CancellationToken cancellationToken)
    {
        WaitWithLease(state, barrier => barrier.WaitUntilArrivals(expectedArrivals, cancellationToken));
    }

    internal void ReleaseBarrier(SetupTestBarrierState state)
    {
        lock (gate)
        {
            state.Release();
            state.DisposeHandlesWhenUnleased();
        }
    }

    internal void DisposeBarrier(string operation, SetupTestBarrierState state)
    {
        lock (gate)
        {
            if (barriers.TryGetValue(operation, out var registered) && ReferenceEquals(registered, state))
            {
                barriers.Remove(operation);
            }

            state.Retire();
            state.DisposeHandlesWhenUnleased();
        }
    }

    private void WaitWithLease(SetupTestBarrierState state, Action<SetupTestBarrierState> wait)
    {
        lock (gate)
        {
            if (state.IsRetired)
            {
                return;
            }

            state.AcquireLease();
        }

        try
        {
            wait(state);
        }
        finally
        {
            lock (gate)
            {
                state.ReleaseLease();
            }
        }
    }

    private sealed class SetupTestFileSystem(SetupTestPlatform platform) : ISetupFileSystem
    {
        public bool FileExists(string path)
        {
            platform.Record($"file.exists:{path}");
            return platform.files.ContainsKey(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            platform.Record($"file.read:{path}");
            return platform.files[path].ToArray();
        }

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            platform.Record($"file.write:{path}");
            platform.files[path] = bytes.ToArray();
        }

        public void FlushFile(string path) => platform.Record($"file.flush:{path}");

        public void ReplaceFile(string sourcePath, string destinationPath)
        {
            platform.Record($"file.replace:{sourcePath}->{destinationPath}");
            platform.files[destinationPath] = platform.files[sourcePath];
            platform.files.Remove(sourcePath);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            platform.Record($"file.move:{sourcePath}->{destinationPath}");
            if (!overwrite && platform.files.ContainsKey(destinationPath))
            {
                throw new IOException("Destination already exists.");
            }

            platform.files[destinationPath] = platform.files[sourcePath];
            platform.files.Remove(sourcePath);
        }

        public void DeleteFile(string path)
        {
            platform.Record($"file.delete:{path}");
            platform.files.Remove(path);
        }

        public SetupFileMetadata GetFileMetadata(string path)
        {
            platform.Record($"file.metadata:{path}");
            return platform.files.TryGetValue(path, out var bytes)
                ? new SetupFileMetadata(true, bytes.Length, FileAttributes.Normal, platform.Clock.UtcNow)
                : SetupFileMetadata.Missing;
        }
    }

    private sealed class SetupTestUserEnvironment(SetupTestPlatform platform) : ISetupUserEnvironment
    {
        public string? Get(string name)
        {
            platform.Record($"environment.get:{name}");
            return platform.environment.TryGetValue(name, out var value) ? value : null;
        }

        public void Set(string name, string? value)
        {
            platform.Record($"environment.set:{name}");
            if (value is null)
            {
                platform.environment.Remove(name);
                return;
            }

            platform.environment[name] = value;
        }

        public void NotifyChange() => platform.Record("environment.notify");
    }

    private sealed class SetupTestClock(DateTimeOffset utcNow) : ISetupClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class SetupTestIdentifierGenerator : ISetupIdentifierGenerator
    {
        private long next = 1;

        public Guid CreateUuidV7() => Guid.Parse($"00000000-0000-7000-8000-{next++:D12}");
    }

    private sealed class SetupTestExecution(SetupTestPlatform platform) : ISetupExecution
    {
        public void Checkpoint(string operation) => platform.Record($"checkpoint:{operation}");
    }
}

internal sealed class SetupTestBarrier(SetupTestPlatform platform, string operation, SetupTestBarrierState state) : IDisposable
{
    private int disposed;

    public bool HasReached => platform.HasReached(state);

    public void WaitUntilReached(CancellationToken cancellationToken) => platform.WaitUntilReached(state, cancellationToken);

    public void WaitUntilArrivals(int expectedArrivals, CancellationToken cancellationToken) => platform.WaitUntilArrivals(state, expectedArrivals, cancellationToken);

    public void Release() => platform.ReleaseBarrier(state);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            platform.DisposeBarrier(operation, state);
        }
    }
}

internal sealed class SetupTestBarrierState
{
    private readonly ManualResetEventSlim reached = new(false);
    private readonly ManualResetEventSlim release = new(false);
    private readonly SemaphoreSlim arrivals = new(0);
    private readonly CancellationToken cancellationToken;
    private readonly TimeSpan maximumWait;
    private int arrivalCount;
    private int leaseCount;
    private bool retired;
    private bool handlesDisposed;

    public SetupTestBarrierState(CancellationToken cancellationToken, TimeSpan maximumWait)
    {
        this.cancellationToken = cancellationToken;
        this.maximumWait = maximumWait;
    }

    public bool HasReached => arrivalCount > 0;

    public bool IsRetired => retired;

    public void AcquireLease() => leaseCount++;

    public void ReleaseLease()
    {
        leaseCount--;
        DisposeHandlesWhenUnleased();
    }

    public void Arrive()
    {
        Interlocked.Increment(ref arrivalCount);
        reached.Set();
        arrivals.Release();
    }

    public void WaitUntilReached(CancellationToken cancellationToken)
    {
        if (!reached.Wait(maximumWait, cancellationToken))
        {
            throw new TimeoutException("Setup test barrier did not receive the expected checkpoint.");
        }
    }

    public void WaitUntilArrivals(int expectedArrivals, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref arrivalCount) < expectedArrivals)
        {
            if (!arrivals.Wait(maximumWait, cancellationToken))
            {
                throw new TimeoutException("Setup test barrier did not receive every expected checkpoint.");
            }
        }
    }

    public void Release()
    {
        if (!handlesDisposed)
        {
            release.Set();
        }
    }

    public void Retire()
    {
        retired = true;
        Release();
    }

    public void WaitForRelease()
    {
        if (!release.Wait(maximumWait, cancellationToken))
        {
            throw new TimeoutException("Setup test barrier was not released.");
        }
    }

    public void DisposeHandlesWhenUnleased()
    {
        if (!retired || leaseCount != 0 || handlesDisposed)
        {
            return;
        }

        handlesDisposed = true;
        reached.Dispose();
        release.Dispose();
        arrivals.Dispose();
    }

}

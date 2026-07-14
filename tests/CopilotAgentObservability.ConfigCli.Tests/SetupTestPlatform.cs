using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

internal sealed class SetupTestPlatform : ISetupPlatform
{
    private readonly object gate = new();
    private readonly Dictionary<string, byte[]> files;
    private readonly Dictionary<string, string?> environment;
    private readonly Dictionary<string, Queue<(Exception Exception, Action? Callback)>> faults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(Exception Exception, Action? Callback)>> afterEffectFaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SetupTestBarrierState> barriers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SetupPathMetadata> pathMetadata;
    private readonly HashSet<string> exclusiveLocks;
    private readonly Dictionary<string, Queue<SetupProcessObservation>> processObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<SetupManagedLocation, SetupManagedObservation> managedObservations = [];
    private readonly Queue<SetupHttpProbeObservation> httpProbeObservations = [];
    private readonly List<string> operations = [];

    public SetupTestPlatform(
        DateTimeOffset utcNow,
        string localApplicationData = "C:\\setup-test-local-app-data",
        SetupPathStyle pathStyle = SetupPathStyle.Windows,
        SetupPlanningOs planningOs = SetupPlanningOs.Windows,
        string applicationData = "C:\\Users\\setup-test\\AppData\\Roaming",
        string userProfile = "C:\\Users\\setup-test")
    {
        LocalApplicationData = localApplicationData;
        PathStyle = pathStyle;
        var pathComparer = pathStyle == SetupPathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        files = new Dictionary<string, byte[]>(pathComparer);
        environment = new Dictionary<string, string?>(pathComparer);
        pathMetadata = new Dictionary<string, SetupPathMetadata>(pathComparer);
        exclusiveLocks = new HashSet<string>(pathComparer);
        Clock = new SetupTestClock(utcNow);
        Identifiers = new SetupTestIdentifierGenerator();
        FileSystem = new SetupTestFileSystem(this);
        UserEnvironment = new SetupTestUserEnvironment(this);
        Execution = new SetupTestExecution(this);
        OperatingSystem = new SetupTestOperatingSystem(planningOs, applicationData, userProfile);
        ProcessRunner = new SetupTestProcessRunner(this);
        ManagedSettings = new SetupTestManagedSettingsSource(this);
        HttpProbe = new SetupTestHttpProbe(this);
    }

    public string LocalApplicationData { get; }

    public SetupPathStyle PathStyle { get; }

    public ISetupFileSystem FileSystem { get; }

    public ISetupUserEnvironment UserEnvironment { get; }

    public ISetupClock Clock { get; }

    public ISetupIdentifierGenerator Identifiers { get; }

    public ISetupExecution Execution { get; }

    public ISetupOperatingSystem OperatingSystem { get; }

    public ISetupProcessRunner ProcessRunner { get; }

    public ISetupManagedSettingsSource ManagedSettings { get; }

    public ISetupHttpProbe HttpProbe { get; }

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

    public void InjectFault(string operation, Exception exception, Action? callback = null)
    {
        lock (gate)
        {
            if (!faults.TryGetValue(operation, out var queuedFaults))
            {
                queuedFaults = new Queue<(Exception, Action?)>();
                faults.Add(operation, queuedFaults);
            }

            queuedFaults.Enqueue((exception, callback));
        }
    }

    public void InjectAfterEffectFault(string operation, Exception exception, Action? callback = null)
    {
        lock (gate)
        {
            if (!afterEffectFaults.TryGetValue(operation, out var queuedFaults))
            {
                queuedFaults = new Queue<(Exception, Action?)>();
                afterEffectFaults.Add(operation, queuedFaults);
            }

            queuedFaults.Enqueue((exception, callback));
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

    public void SeedFile(string path, byte[] bytes)
    {
        files[path] = bytes.ToArray();
        pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
    }

    public void SeedDirectory(string path) =>
        pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);

    public void SeedPathMetadata(string path, SetupPathMetadata metadata) => pathMetadata[path] = metadata;

    public byte[] ReadSeededFile(string path) => files[path].ToArray();

    public void SeedUserEnvironment(string name, string? value)
    {
        lock (gate)
        {
            environment[name] = value;
        }
    }

    public string? ReadUserEnvironment(string name)
    {
        lock (gate)
        {
            return environment.TryGetValue(name, out var value) ? value : null;
        }
    }

    public void ScriptProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        SetupProcessObservation observation)
    {
        var key = ProcessKey(fileName, arguments);
        if (!processObservations.TryGetValue(key, out var observations))
        {
            observations = new Queue<SetupProcessObservation>();
            processObservations.Add(key, observations);
        }

        observations.Enqueue(observation);
    }

    public void SeedManagedObservation(SetupManagedLocation location, SetupManagedObservation observation) =>
        managedObservations[location] = observation;

    public void ScriptHttpProbe(SetupHttpProbeObservation observation) => httpProbeObservations.Enqueue(observation);

    private static string ProcessKey(string fileName, IReadOnlyList<string> arguments) =>
        $"{fileName}\0{string.Join('\0', arguments)}";

    private void Record(string operation)
    {
        (Exception Exception, Action? Callback)? fault = null;
        SetupTestBarrierState? barrier;
        lock (gate)
        {
            operations.Add(operation);
            if (faults.TryGetValue(operation, out var queuedFaults) && queuedFaults.TryDequeue(out var queuedFault))
            {
                fault = queuedFault;
            }

            if (fault is null)
            {
                barriers.TryGetValue(operation, out barrier);
                barrier?.AcquireLease();
            }
            else
            {
                barrier = null;
            }
        }

        if (fault is { } beforeEffectFault)
        {
            beforeEffectFault.Callback?.Invoke();
            throw beforeEffectFault.Exception;
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

    private void RecordAfterEffect(string operation)
    {
        (Exception Exception, Action? Callback)? fault = null;
        lock (gate)
        {
            if (afterEffectFaults.TryGetValue(operation, out var queuedFaults) && queuedFaults.TryDequeue(out var queuedFault))
            {
                fault = queuedFault;
            }
        }

        if (fault is { } afterEffectFault)
        {
            afterEffectFault.Callback?.Invoke();
            throw afterEffectFault.Exception;
        }
    }

    private void ReleaseExclusiveFileLock(string path)
    {
        lock (gate)
        {
            exclusiveLocks.Remove(path);
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
        public void CreateDirectory(string path)
        {
            platform.Record($"directory.create:{path}");
            lock (platform.gate)
            {
                if (platform.files.ContainsKey(path))
                {
                    throw new IOException("A file already exists at the directory path.");
                }

                platform.pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);
            }
        }

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

        public SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            if (maximumBytes == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            platform.Record($"file.read-bounded:{path}:{maximumBytes}");
            lock (platform.gate)
            {
                var bytes = platform.files[path];
                var length = Math.Min(bytes.Length, maximumBytes + 1);
                return new SetupBoundedFileRead(bytes.AsSpan(0, length).ToArray(), bytes.Length <= maximumBytes);
            }
        }

        public bool HasDirectories(string path)
        {
            platform.Record($"directory.has-child:{path}");
            var separator = platform.PathStyle == SetupPathStyle.Windows ? '\\' : '/';
            var comparison = platform.PathStyle == SetupPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var prefix = path.TrimEnd('\\', '/') + separator;
            lock (platform.gate)
            {
                return platform.pathMetadata.Any(entry => entry.Value.Kind == SetupPathKind.Directory &&
                        entry.Key.StartsWith(prefix, comparison) &&
                        entry.Key[prefix.Length..].Length > 0 &&
                        !entry.Key[prefix.Length..].Contains(separator));
            }
        }

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            var operation = $"file.write:{path}";
            platform.Record(operation);
            if (platform.pathMetadata.TryGetValue(path, out var existing) && existing.Kind == SetupPathKind.Directory)
            {
                throw new UnauthorizedAccessException("Destination is a directory.");
            }

            platform.files[path] = bytes.ToArray();
            platform.pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
            platform.RecordAfterEffect(operation);
        }

        public void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            var operation = $"file.write-new:{path}";
            platform.Record(operation);
            if (platform.files.ContainsKey(path) || platform.pathMetadata.TryGetValue(path, out var metadata) && metadata.Exists)
            {
                throw new IOException("Destination already exists.");
            }

            platform.files[path] = bytes.ToArray();
            platform.pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
            platform.RecordAfterEffect(operation);
        }

        public bool TryWriteNewAllBytesAndFlush(string path, ReadOnlySpan<byte> bytes)
        {
            var operation = $"file.try-write-new-flushed:{path}";
            platform.Record(operation);
            lock (platform.gate)
            {
                if (platform.files.ContainsKey(path) ||
                    platform.pathMetadata.TryGetValue(path, out var metadata) && metadata.Exists)
                {
                    return false;
                }

                platform.files[path] = bytes.ToArray();
                platform.pathMetadata[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
            }

            platform.RecordAfterEffect(operation);
            return true;
        }

        public void FlushFile(string path)
        {
            var operation = $"file.flush:{path}";
            platform.Record(operation);
            if (!platform.files.ContainsKey(path))
            {
                throw new FileNotFoundException("File does not exist.");
            }

            platform.RecordAfterEffect(operation);
        }

        public void ReplaceFile(string sourcePath, string destinationPath)
        {
            var operation = $"file.replace:{sourcePath}->{destinationPath}";
            platform.Record(operation);
            if (!platform.files.ContainsKey(sourcePath) || !platform.files.ContainsKey(destinationPath))
            {
                throw new IOException("Source and destination files must exist.");
            }

            platform.files[destinationPath] = platform.files[sourcePath];
            platform.files.Remove(sourcePath);
            platform.pathMetadata[destinationPath] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
            platform.pathMetadata.Remove(sourcePath);
            platform.RecordAfterEffect(operation);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            var operation = $"file.move:{sourcePath}->{destinationPath}";
            platform.Record(operation);
            if (!platform.files.ContainsKey(sourcePath))
            {
                throw new IOException("Source does not exist.");
            }

            if (platform.pathMetadata.TryGetValue(destinationPath, out var destinationMetadata) &&
                (destinationMetadata.Kind != SetupPathKind.File || !overwrite))
            {
                throw new IOException("Destination already exists.");
            }

            platform.files[destinationPath] = platform.files[sourcePath];
            platform.files.Remove(sourcePath);
            platform.pathMetadata[destinationPath] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
            platform.pathMetadata.Remove(sourcePath);
            platform.RecordAfterEffect(operation);
        }

        public void DeleteFile(string path)
        {
            platform.Record($"file.delete:{path}");
            if (platform.pathMetadata.TryGetValue(path, out var metadata) && metadata.Kind == SetupPathKind.Directory)
            {
                throw new UnauthorizedAccessException("Path is a directory.");
            }

            platform.files.Remove(path);
            platform.pathMetadata.Remove(path);
            platform.RecordAfterEffect($"file.delete:{path}");
        }

        public SetupPathMetadata GetPathMetadata(string path)
        {
            platform.Record($"file.metadata:{path}");
            return platform.pathMetadata.TryGetValue(path, out var metadata)
                ? metadata
                : SetupPathMetadata.Missing;
        }

        public ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path)
        {
            platform.Record($"file.lock:{path}");
            lock (platform.gate)
            {
                platform.files.TryAdd(path, []);
                return platform.exclusiveLocks.Add(path)
                    ? new SetupTestExclusiveFileLock(platform, path)
                    : null;
            }
        }
    }

    private sealed class SetupTestExclusiveFileLock(SetupTestPlatform platform, string path) : ISetupExclusiveFileLock
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                platform.ReleaseExclusiveFileLock(path);
            }
        }
    }

    private sealed class SetupTestUserEnvironment(SetupTestPlatform platform) : ISetupUserEnvironment
    {
        public string? Get(string name)
        {
            platform.Record($"environment.get:{name}");
            lock (platform.gate)
            {
                return platform.environment.TryGetValue(name, out var value) ? value : null;
            }
        }

        public void Set(string name, string? value)
        {
            var operation = $"environment.set:{name}";
            platform.Record(operation);
            lock (platform.gate)
            {
                if (value is null)
                {
                    platform.environment.Remove(name);
                }
                else
                {
                    platform.environment[name] = value;
                }
            }

            platform.RecordAfterEffect(operation);
        }

        public void NotifyChange()
        {
            const string operation = "environment.notify";
            platform.Record(operation);
            platform.RecordAfterEffect(operation);
        }
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

    private sealed class SetupTestOperatingSystem(
        SetupPlanningOs current,
        string applicationData,
        string userProfile) : ISetupOperatingSystem
    {
        public SetupPlanningOs Current => current;

        public string ApplicationData => applicationData;

        public string UserProfile => userProfile;
    }

    private sealed class SetupTestProcessRunner(SetupTestPlatform platform) : ISetupProcessRunner
    {
        public SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments)
        {
            platform.Record($"process.run:{fileName}:{string.Join(' ', arguments)}");
            var key = ProcessKey(fileName, arguments);
            return platform.processObservations.TryGetValue(key, out var observations) && observations.TryDequeue(out var observation)
                ? observation
                : new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty);
        }
    }

    private sealed class SetupTestManagedSettingsSource(SetupTestPlatform platform) : ISetupManagedSettingsSource
    {
        public SetupManagedObservation Read(SetupManagedLocation location)
        {
            platform.Record($"managed.read:{location}");
            return platform.managedObservations.TryGetValue(location, out var observation)
                ? observation
                : SetupManagedObservation.Absent;
        }
    }

    private sealed class SetupTestHttpProbe(SetupTestPlatform platform) : ISetupHttpProbe
    {
        public SetupHttpProbeObservation Get(
            string origin,
            string path,
            int totalBudgetMilliseconds,
            int maxBodyBytes)
        {
            platform.Record($"http.get:{origin}:{path}:{totalBudgetMilliseconds}:{maxBodyBytes}");
            return platform.httpProbeObservations.TryDequeue(out var observation)
                ? observation
                : SetupHttpProbeObservation.TransportFailure;
        }
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

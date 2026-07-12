using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

internal sealed class SetupTestPlatform : ISetupPlatform
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> environment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Exception>> faults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SetupTestBarrier> barriers = new(StringComparer.Ordinal);
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

    public IReadOnlyList<string> Operations => operations;

    public void InjectFault(string operation, Exception exception)
    {
        if (!faults.TryGetValue(operation, out var queuedFaults))
        {
            queuedFaults = new Queue<Exception>();
            faults.Add(operation, queuedFaults);
        }

        queuedFaults.Enqueue(exception);
    }

    public SetupTestBarrier AddBarrier(string operation)
    {
        var barrier = new SetupTestBarrier();
        barriers.Add(operation, barrier);
        return barrier;
    }

    public void SeedFile(string path, byte[] bytes) => files[path] = bytes.ToArray();

    public byte[] ReadSeededFile(string path) => files[path].ToArray();

    public void SeedUserEnvironment(string name, string? value) => environment[name] = value;

    public string? ReadUserEnvironment(string name) => environment.TryGetValue(name, out var value) ? value : null;

    private void Record(string operation)
    {
        operations.Add(operation);
        if (faults.TryGetValue(operation, out var queuedFaults) && queuedFaults.TryDequeue(out var exception))
        {
            throw exception;
        }

        if (barriers.TryGetValue(operation, out var barrier))
        {
            barrier.ArriveAndWait();
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

internal sealed class SetupTestBarrier
{
    private readonly ManualResetEventSlim reached = new(false);
    private readonly ManualResetEventSlim release = new(false);

    public void WaitUntilReached() => reached.Wait();

    public void Release() => release.Set();

    internal void ArriveAndWait()
    {
        reached.Set();
        release.Wait();
    }
}

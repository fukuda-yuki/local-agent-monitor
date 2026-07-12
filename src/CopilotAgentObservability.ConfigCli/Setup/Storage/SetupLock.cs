using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

public sealed class SetupLock : IDisposable
{
    private readonly ISetupExclusiveFileLock fileLock;
    private readonly ISetupPlatform platform;
    private readonly string runtimeRoot;
    private int disposed;

    private SetupLock(ISetupExclusiveFileLock fileLock, ISetupPlatform platform, string runtimeRoot)
    {
        this.fileLock = fileLock;
        this.platform = platform;
        this.runtimeRoot = runtimeRoot;
    }

    public static SetupLockAcquireResult TryAcquire(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        paths.EnsureRoot();
        var fileLock = platform.FileSystem.TryAcquireExclusiveFileLock(paths.Lock);
        return fileLock is null
            ? SetupLockAcquireResult.Busy
            : SetupLockAcquireResult.Success(new SetupLock(fileLock, platform, paths.Root));
    }

    internal void AssertHeld(ISetupPlatform expectedPlatform, SetupRuntimePaths expectedPaths)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            !ReferenceEquals(platform, expectedPlatform) ||
            !string.Equals(runtimeRoot, expectedPaths.Root, StringComparison.Ordinal))
        {
            throw new SetupStorageException(SetupStorageCodes.LockRequired);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            fileLock.Dispose();
        }
    }
}

public sealed record SetupLockAcquireResult(SetupLock? Lock, string? Code) : IDisposable
{
    public static SetupLockAcquireResult Busy { get; } = new(null, SetupCodes.SetupBusy);

    public bool Acquired => Lock is not null;

    public static SetupLockAcquireResult Success(SetupLock setupLock) => new(setupLock, null);

    public void Dispose() => Lock?.Dispose();
}

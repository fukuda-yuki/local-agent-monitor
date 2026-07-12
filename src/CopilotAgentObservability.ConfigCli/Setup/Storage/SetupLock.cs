using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

public sealed class SetupLock : IDisposable
{
    private readonly ISetupExclusiveFileLock fileLock;

    private SetupLock(ISetupExclusiveFileLock fileLock)
    {
        this.fileLock = fileLock;
    }

    public static SetupLockAcquireResult TryAcquire(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        paths.EnsureRoot();
        var fileLock = platform.FileSystem.TryAcquireExclusiveFileLock(paths.Lock);
        return fileLock is null
            ? SetupLockAcquireResult.Busy
            : SetupLockAcquireResult.Success(new SetupLock(fileLock));
    }

    public void Dispose() => fileLock.Dispose();
}

public sealed record SetupLockAcquireResult(SetupLock? Lock, string? Code) : IDisposable
{
    public static SetupLockAcquireResult Busy { get; } = new(null, SetupCodes.SetupBusy);

    public bool Acquired => Lock is not null;

    public static SetupLockAcquireResult Success(SetupLock setupLock) => new(setupLock, null);

    public void Dispose() => Lock?.Dispose();
}

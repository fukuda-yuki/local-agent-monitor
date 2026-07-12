using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

public sealed class SetupLock : IDisposable
{
    private const int Held = 0;
    private const int DisposeRequested = 1;
    private const int Disposed = 2;

    private readonly ISetupExclusiveFileLock fileLock;
    private readonly ISetupPlatform platform;
    private readonly string runtimeRoot;
    private readonly object operationGate = new();
    private int lifecycle = Held;
    private int operationDepth;
    private bool releaseOnOperationExit;

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
        // Transitional only: Lock-B replaces caller-side checks with operation-scoped execution.
        ExecuteWhileHeld(expectedPlatform, expectedPaths, static () => { });
    }

    internal void ExecuteWhileHeld(
        ISetupPlatform expectedPlatform,
        SetupRuntimePaths expectedPaths,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ExecuteWhileHeld<object?>(expectedPlatform, expectedPaths, () =>
        {
            action();
            return null;
        });
    }

    internal T ExecuteWhileHeld<T>(
        ISetupPlatform expectedPlatform,
        SetupRuntimePaths expectedPaths,
        Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (operationGate)
        {
            if (Volatile.Read(ref lifecycle) != Held ||
                !ReferenceEquals(platform, expectedPlatform) ||
                !string.Equals(runtimeRoot, expectedPaths.Root, StringComparison.Ordinal))
            {
                throw new SetupStorageException(SetupStorageCodes.LockRequired);
            }

            operationDepth++;
            try
            {
                return action();
            }
            finally
            {
                operationDepth--;
                if (operationDepth == 0 && releaseOnOperationExit)
                {
                    ReleaseUnderGate();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref lifecycle, DisposeRequested, Held) != Held)
        {
            return;
        }

        if (Monitor.IsEntered(operationGate))
        {
            releaseOnOperationExit = true;
            return;
        }

        lock (operationGate)
        {
            ReleaseUnderGate();
        }
    }

    private void ReleaseUnderGate()
    {
        Volatile.Write(ref lifecycle, Disposed);
        fileLock.Dispose();
    }
}

public sealed record SetupLockAcquireResult(SetupLock? Lock, string? Code) : IDisposable
{
    public static SetupLockAcquireResult Busy { get; } = new(null, SetupCodes.SetupBusy);

    public bool Acquired => Lock is not null;

    public static SetupLockAcquireResult Success(SetupLock setupLock) => new(setupLock, null);

    public void Dispose() => Lock?.Dispose();
}

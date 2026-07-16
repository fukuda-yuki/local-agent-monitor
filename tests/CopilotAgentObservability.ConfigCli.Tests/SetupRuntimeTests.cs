using System.Diagnostics;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupRuntimeTests
{
    [Fact]
    public void RuntimePaths_UseTheExactLocalApplicationDataLayoutAndCanonicalUuidPaths()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000001");
        var recordId = Guid.Parse("00000000-0000-7000-8000-000000000002");

        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup", paths.Root);
        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\ownership-ledger.v1.json", paths.OwnershipLedger);
        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\setup.lock", paths.Lock);
        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\plans\\00000000-0000-7000-8000-000000000001.json", paths.GetPlan(changeSetId));
        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\backups\\00000000-0000-7000-8000-000000000001\\00000000-0000-7000-8000-000000000002.backup", paths.GetBackup(changeSetId, recordId));
        Assert.Equal("C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\transactions\\00000000-0000-7000-8000-000000000001.journal.json", paths.GetTransactionJournal(changeSetId));
    }

    [Fact]
    public void RuntimePaths_EnsureRootCreatesOnlyThePrivateRuntimeRoot()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);

        paths.EnsureRoot();

        Assert.Equal(["directory.create:C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup"], platform.Operations);
    }

    [Fact]
    public void SetupLock_FirstAcquireCreatesRuntimeDirectoryAndLockFile()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);

        using var result = SetupLock.TryAcquire(platform, paths);

        Assert.True(result.Acquired);
        Assert.Null(result.Code);
        Assert.NotNull(result.Lock);
        Assert.Equal(
        [
            "directory.create:C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup",
            "file.lock:C:\\runtime\\local-app-data\\CopilotAgentObservability\\LocalMonitor\\setup\\setup.lock",
        ],
        platform.Operations);
    }

    [Fact]
    public async Task SetupLock_ContendedAcquireReturnsFixedBusyCodeWithoutRetryOrCheckpoint()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        using var first = SetupLock.TryAcquire(platform, paths);
        Assert.True(first.Acquired);

        var secondTask = Task.Run(() => SetupLock.TryAcquire(platform, paths));
        using var second = await secondTask;

        Assert.False(second.Acquired);
        Assert.Equal(SetupCodes.SetupBusy, second.Code);
        Assert.Null(second.Lock);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("checkpoint:", StringComparison.Ordinal));
        Assert.Equal(2, platform.Operations.Count(operation => operation.StartsWith("file.lock:", StringComparison.Ordinal)));
    }

    [Fact]
    public void SetupLock_DisposeReleasesFakeLockForAReacquire()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var first = SetupLock.TryAcquire(platform, paths);
        Assert.True(first.Acquired);

        first.Dispose();
        using var second = SetupLock.TryAcquire(platform, paths);

        Assert.True(second.Acquired);
    }

    [Fact]
    public async Task SetupLock_ExecuteWhileHeldSerializesCallbacksAndDisposeWaitsForTheActiveCallback()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        using var secondCallbackEntered = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();

        var first = Task.Run(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            callbackEntered.Set();
            WaitOrFail(releaseCallback, "the first setup callback release");
        }));
        WaitOrFail(callbackEntered, "the first setup callback entry");

        var second = Task.Run(() =>
        {
            secondAttempted.Set();
            setupLock.ExecuteWhileHeld(platform, paths, secondCallbackEntered.Set);
        });
        WaitOrFail(secondAttempted, "the second setup callback attempt");
        Assert.False(secondCallbackEntered.IsSet);
        using var contended = SetupLock.TryAcquire(platform, paths);
        Assert.False(contended.Acquired);
        Assert.Equal(SetupCodes.SetupBusy, contended.Code);

        releaseCallback.Set();
        await AwaitOrFail(first, "the first setup callback completion");
        await AwaitOrFail(second, "the second setup callback completion");
        Assert.True(secondCallbackEntered.IsSet);

        callbackEntered.Reset();
        releaseCallback.Reset();
        var third = Task.Run(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            callbackEntered.Set();
            WaitOrFail(releaseCallback, "the third setup callback release");
        }));
        WaitOrFail(callbackEntered, "the third setup callback entry");
        var dispose = Task.Factory.StartNew(
            () =>
            {
                disposeStarted.Set();
                setupLock.Dispose();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        WaitOrFail(disposeStarted, "the external Dispose start");
        Assert.False(dispose.IsCompleted);

        releaseCallback.Set();
        await AwaitOrFail(third, "the third setup callback completion");
        await AwaitOrFail(dispose, "the external Dispose completion");
        AssertLockRequired(() => setupLock.ExecuteWhileHeld(platform, paths, () => { }));
        using var reacquired = SetupLock.TryAcquire(platform, paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public async Task SetupLock_DisposeRequestRejectsAnAlreadyQueuedExecuteBeforeReleasingTheHandle()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        using var disposeRequested = new ManualResetEventSlim();
        var handle = new CountingSetupExclusiveFileLock();
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths, disposeRequested.Set);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var queuedAttempted = new ManualResetEventSlim();

        var active = Task.Run(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            callbackEntered.Set();
            WaitOrFail(releaseCallback, "the active setup callback release");
        }));
        WaitOrFail(callbackEntered, "the active setup callback entry");
        var queued = Task.Run(() =>
        {
            queuedAttempted.Set();
            setupLock.ExecuteWhileHeld(platform, paths, () => { });
        });
        WaitOrFail(queuedAttempted, "the queued setup callback attempt");

        var dispose = Task.Run(setupLock.Dispose);
        WaitOrFail(disposeRequested, "the DisposeRequested lifecycle transition");
        Assert.Equal(0, handle.DisposeCount);

        releaseCallback.Set();
        await AwaitOrFail(active, "the active setup callback completion");
        var rejected = await Assert.ThrowsAsync<SetupStorageException>(
            () => AwaitOrFail(queued, "the queued setup callback rejection"));
        Assert.Equal(SetupStorageCodes.LockRequired, rejected.Code);
        await AwaitOrFail(dispose, "the external Dispose completion");
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task SetupLock_ConcurrentAndRepeatedDisposeCallsReleaseANonIdempotentHandleExactlyOnce()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var handle = new CountingSetupExclusiveFileLock();
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths);

        var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(setupLock.Dispose)).ToArray();
        await AwaitOrFail(Task.WhenAll(disposals), "all concurrent Dispose calls");
        setupLock.Dispose();

        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task SetupLock_ExternalDisposeOwnsTheHandleReleaseExceptionAfterTheCallbackCompletes()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var releaseFailure = new IOException("synthetic handle release failure");
        var handle = new CountingSetupExclusiveFileLock(releaseFailure);
        using var disposeRequested = new ManualResetEventSlim();
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths, disposeRequested.Set);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();

        var callback = Task.Run(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            callbackEntered.Set();
            WaitOrFail(releaseCallback, "the callback release before throwing-handle disposal");
        }));
        WaitOrFail(callbackEntered, "the callback entry before throwing-handle disposal");
        var dispose = Task.Run(setupLock.Dispose);
        WaitOrFail(disposeRequested, "the throwing-handle DisposeRequested transition");

        releaseCallback.Set();
        await AwaitOrFail(callback, "the callback completion before external release failure");
        var actual = await Assert.ThrowsAsync<IOException>(
            () => AwaitOrFail(dispose, "the external throwing-handle Dispose completion"));
        setupLock.Dispose();

        Assert.Same(releaseFailure, actual);
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public void SetupLock_ReentrantDisposeReleasesANonIdempotentHandleOnceAtTheOutermostExit()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var handle = new CountingSetupExclusiveFileLock();
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths);

        setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            setupLock.ExecuteWhileHeld(platform, paths, () =>
            {
                setupLock.Dispose();
                Assert.Equal(0, handle.DisposeCount);
            });
            Assert.Equal(0, handle.DisposeCount);
        });
        setupLock.Dispose();

        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public void SetupLock_NestedReentrantDisposeSurfacesTheReleaseExceptionAtTheOutermostSuccessfulExit()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var releaseFailure = new IOException("synthetic handle release failure");
        var handle = new CountingSetupExclusiveFileLock(releaseFailure);
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths);
        var outerContinued = false;

        var actual = Assert.Throws<IOException>(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            setupLock.ExecuteWhileHeld(platform, paths, () =>
            {
                setupLock.Dispose();
                Assert.Equal(0, handle.DisposeCount);
            });
            outerContinued = true;
        }));
        setupLock.Dispose();

        Assert.Same(releaseFailure, actual);
        Assert.True(outerContinued);
        Assert.Equal(1, handle.DisposeCount);
        AssertLockRequired(() => setupLock.ExecuteWhileHeld(platform, paths, () => { }));
    }

    [Fact]
    public void SetupLock_CallbackExceptionTakesPrecedenceOverADeferredHandleReleaseException()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var callbackFailure = new InvalidOperationException("synthetic callback failure");
        var releaseFailure = new IOException("synthetic handle release failure");
        var handle = new CountingSetupExclusiveFileLock(releaseFailure);
        var setupLock = SetupLock.CreateForTesting(handle, platform, paths);

        var actual = Assert.Throws<InvalidOperationException>(() =>
            setupLock.ExecuteWhileHeld(platform, paths, () =>
            {
                setupLock.Dispose();
                throw callbackFailure;
            }));
        setupLock.Dispose();

        Assert.Same(callbackFailure, actual);
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public void SetupLock_ExecuteWhileHeldAllowsNestedCallbacksOnTheSameThread()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        using var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);

        var actual = setupLock.ExecuteWhileHeld(
            platform,
            paths,
            () => setupLock.ExecuteWhileHeld(platform, paths, () => 42));

        Assert.Equal(42, actual);
    }

    [Fact]
    public void SetupLock_ReentrantDisposeDefersReleaseAndRejectsNestedExecute()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);

        setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            setupLock.Dispose();
            using var contended = SetupLock.TryAcquire(platform, paths);
            Assert.False(contended.Acquired);
            var rejected = Assert.Throws<SetupStorageException>(
                () => setupLock.ExecuteWhileHeld(platform, paths, () => { }));
            Assert.Equal(SetupStorageCodes.LockRequired, rejected.Code);
        });

        using var reacquired = SetupLock.TryAcquire(platform, paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public void SetupLock_CallbackExceptionUnwindsWithoutReleasingAnOtherwiseHeldLock()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        using var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        var expected = new InvalidOperationException("synthetic callback failure");

        var actual = Assert.Throws<InvalidOperationException>(
            () => setupLock.ExecuteWhileHeld(platform, paths, () => throw expected));

        Assert.Same(expected, actual);
        Assert.Equal(7, setupLock.ExecuteWhileHeld(platform, paths, () => 7));
        using var contended = SetupLock.TryAcquire(platform, paths);
        Assert.False(contended.Acquired);
    }

    [Fact]
    public void SetupLock_ReentrantDisposeWithCallbackExceptionStillReleasesAfterUnwind()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        var expected = new InvalidOperationException("synthetic callback failure");

        var actual = Assert.Throws<InvalidOperationException>(() => setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            setupLock.Dispose();
            throw expected;
        }));

        Assert.Same(expected, actual);
        using var reacquired = SetupLock.TryAcquire(platform, paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public void SetupLock_ExecuteWhileHeldRequiresTheAcquiringPlatformRootAndHeldLifecycle()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        var otherPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var otherRootPlatform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\other");

        Assert.Equal(1, setupLock.ExecuteWhileHeld(platform, paths, () => 1));
        AssertLockRequired(() => setupLock.ExecuteWhileHeld(otherPlatform, new SetupRuntimePaths(otherPlatform), () => { }));
        AssertLockRequired(() => setupLock.ExecuteWhileHeld(platform, new SetupRuntimePaths(otherRootPlatform), () => { }));
        setupLock.Dispose();
        AssertLockRequired(() => setupLock.ExecuteWhileHeld(platform, paths, () => { }));
    }

    [Fact]
    public async Task SetupLock_PublicObjectMonitorDoesNotGateThePrivateOperationLease()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        using var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        using var publicMonitorEntered = new ManualResetEventSlim();
        using var releasePublicMonitor = new ManualResetEventSlim();
        var monitorHolder = Task.Run(() =>
        {
            lock (setupLock)
            {
                publicMonitorEntered.Set();
                WaitOrFail(releasePublicMonitor, "the public monitor release");
            }
        });
        WaitOrFail(publicMonitorEntered, "the public monitor entry");

        var operation = Task.Run(() => setupLock.ExecuteWhileHeld(platform, paths, () => 11));
        Assert.Equal(11, await AwaitOrFail(operation, "the privately gated setup operation"));

        releasePublicMonitor.Set();
        await AwaitOrFail(monitorHolder, "the public monitor holder completion");
    }

    [Fact]
    public void SetupLock_PropagatesFakeNonContentionLockFailures()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, "C:\\runtime\\local-app-data");
        var paths = new SetupRuntimePaths(platform);
        var exception = new IOException("synthetic disk failure", unchecked((int)0x80070070));
        platform.InjectFault($"file.lock:{paths.Lock}", exception);

        var actual = Assert.Throws<IOException>(() => SetupLock.TryAcquire(platform, paths));

        Assert.Same(exception, actual);
    }

    [Fact]
    public void SetupLock_SystemPlatformReturnsBusyWhileHandleIsHeldAndReacquiresAfterDispose()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-runtime-{Guid.NewGuid():N}");
        var platform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
        var paths = new SetupRuntimePaths(platform);
        try
        {
            var first = SetupLock.TryAcquire(platform, paths);
            Assert.True(first.Acquired);

            using var second = SetupLock.TryAcquire(platform, paths);
            Assert.False(second.Acquired);
            Assert.Equal(SetupCodes.SetupBusy, second.Code);

            first.Dispose();
            using var third = SetupLock.TryAcquire(platform, paths);
            Assert.True(third.Acquired);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SetupLock_SystemPlatformCoordinatesExclusiveFileShareAcrossProcesses()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-runtime-{Guid.NewGuid():N}");
        var platform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
        var paths = new SetupRuntimePaths(platform);
        Process? child = null;
        try
        {
            paths.EnsureRoot();
            child = StartLockHoldingPowerShell(paths.Lock);
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            using var contended = SetupLock.TryAcquire(platform, paths);
            Assert.False(contended.Acquired);
            Assert.Equal(SetupCodes.SetupBusy, contended.Code);

            await child.StandardInput.WriteLineAsync();
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);

            using var reacquired = SetupLock.TryAcquire(platform, paths);
            Assert.True(reacquired.Acquired);
        }
        finally
        {
            if (child is not null && !child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            child?.Dispose();
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SetupLock_ReentrantDisposeKeepsTheSystemLockBusyUntilTheCallbackExits()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-runtime-{Guid.NewGuid():N}");
        var platform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        var setupLock = Assert.IsType<SetupLock>(acquired.Lock);
        Process? child = null;
        try
        {
            setupLock.ExecuteWhileHeld(platform, paths, () =>
            {
                setupLock.Dispose();
                child = StartTwoAttemptLockPowerShell(paths.Lock);
                var first = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Assert.Equal("BUSY", first);
            });

            Assert.NotNull(child);
            await child.StandardInput.WriteLineAsync();
            child.StandardInput.Close();
            var second = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ACQUIRED", second);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            if (child is not null && !child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            child?.Dispose();
            acquired.Dispose();
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static void AssertLockRequired(Action action)
    {
        var exception = Assert.Throws<SetupStorageException>(action);
        Assert.Equal(SetupStorageCodes.LockRequired, exception.Code);
    }

    private static void WaitOrFail(ManualResetEventSlim signal, string description) =>
        Assert.True(signal.Wait(TimeSpan.FromSeconds(10)), $"Timed out waiting for {description}.");

    private static async Task AwaitOrFail(Task task, string description)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException($"Timed out waiting for {description}.", exception);
        }
    }

    private static async Task<T> AwaitOrFail<T>(Task<T> task, string description)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException($"Timed out waiting for {description}.", exception);
        }
    }

    private static Process StartLockHoldingPowerShell(string lockPath)
    {
        const string command = "${stream} = [System.IO.File]::Open($env:CAO_SETUP_LOCK_TEST_PATH, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); try { [Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null } finally { ${stream}.Dispose() }";
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CAO_SETUP_LOCK_TEST_PATH"] = lockPath;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
    }

    private static Process StartTwoAttemptLockPowerShell(string lockPath)
    {
        const string command = "$p=$env:CAO_SETUP_LOCK_TEST_PATH; function Try-Lock { try { return [System.IO.File]::Open($p,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None) } catch [System.IO.IOException] { return $null } }; $first=Try-Lock; if ($null -eq $first) { [Console]::Out.WriteLine('BUSY') } else { $first.Dispose(); [Console]::Out.WriteLine('UNEXPECTED_ACQUIRED') }; [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; $second=Try-Lock; if ($null -eq $second) { [Console]::Out.WriteLine('UNEXPECTED_BUSY'); exit 2 }; try { [Console]::Out.WriteLine('ACQUIRED'); [Console]::Out.Flush() } finally { $second.Dispose() }";
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CAO_SETUP_LOCK_TEST_PATH"] = lockPath;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
    }

    private sealed class CountingSetupExclusiveFileLock(Exception? disposeFailure = null) : ISetupExclusiveFileLock
    {
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }
}

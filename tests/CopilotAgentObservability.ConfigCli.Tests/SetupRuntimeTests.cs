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
}

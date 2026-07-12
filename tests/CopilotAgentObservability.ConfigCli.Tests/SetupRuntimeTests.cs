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
}

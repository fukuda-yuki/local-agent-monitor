using CopilotAgentObservability.ConfigCli.Setup.Platform;
using System.Diagnostics;
using System.Net.Sockets;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupPlatformTests
{
    [Fact]
    public void SystemPlatform_UsesTheConfiguredLocalApplicationDataRoot()
    {
        var platform = new SystemSetupPlatform(localApplicationData: "C:\\runtime\\local-app-data");

        Assert.Equal("C:\\runtime\\local-app-data", platform.LocalApplicationData);
    }

    [Fact]
    public void SystemPlatform_RecognizesOnlyNativeExclusiveFileLockContention()
    {
        var contentionCodes = OperatingSystem.IsWindows()
            ? new[] { unchecked((int)0x80070020), unchecked((int)0x80070021) }
            : OperatingSystem.IsMacOS()
                ? new[] { 35 }
                : new[] { 11 };
        var nonContentionCodes = OperatingSystem.IsWindows()
            ? new[] { unchecked((int)0x80070005), unchecked((int)0x80070008), unchecked((int)0x80070070) }
            : new[] { 13, 24, 28 };

        Assert.All(contentionCodes, code => Assert.True(SystemSetupPlatform.IsExclusiveFileLockContention(new IOException("synthetic", code))));
        Assert.All(nonContentionCodes, code => Assert.False(SystemSetupPlatform.IsExclusiveFileLockContention(new IOException("synthetic", code))));
    }

    [Fact]
    public void SystemPlatform_PropagatesNonContentionExclusiveLockErrors()
    {
        var exception = new IOException("synthetic disk failure", unchecked((int)0x80070070));
        var platform = new SystemSetupPlatform(exclusiveFileLockAttempt: _ => throw exception);

        var actual = Assert.Throws<IOException>(() => platform.FileSystem.TryAcquireExclusiveFileLock("setup.lock"));

        Assert.Same(exception, actual);
    }

    [Fact]
    public void SystemPlatform_ProvidesUtcClockAndUuidV7Identifiers()
    {
        var platform = new SystemSetupPlatform();

        var before = DateTimeOffset.UtcNow;
        var now = platform.Clock.UtcNow;
        var identifier = platform.Identifiers.CreateUuidV7();
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.InRange(now, before, after);
        Assert.Equal('7', identifier.ToString("D")[14]);
        Assert.Contains(identifier.ToString("D")[19], "89ab");
    }

    [Fact]
    public void SystemPlatform_ReportsPathKindsAndCreatesNewFilesOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cao-platform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "file.bin");
            var platform = new SystemSetupPlatform();

            platform.FileSystem.WriteNewAllBytes(file, [1, 2, 3]);

            Assert.Equal(SetupPathKind.Directory, platform.FileSystem.GetPathMetadata(directory).Kind);
            Assert.Equal(SetupPathKind.File, platform.FileSystem.GetPathMetadata(file).Kind);
            Assert.Equal(SetupPathKind.Missing, platform.FileSystem.GetPathMetadata(Path.Combine(directory, "missing")).Kind);
            Assert.Throws<IOException>(() => platform.FileSystem.WriteNewAllBytes(file, [4]));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SystemPlatform_UnixClassifiesFifoAndSocketAsNonRegularWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(SetupPathStyle.Windows, new SystemSetupPlatform().PathStyle);
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"cao-platform-special-{Guid.NewGuid():N}");
        var regular = Path.Combine(directory, "regular.bin");
        var readOnly = Path.Combine(directory, "read-only.bin");
        var childDirectory = Path.Combine(directory, "child");
        var link = Path.Combine(directory, "regular-link");
        var danglingLink = Path.Combine(directory, "dangling-link");
        var fifo = Path.Combine(directory, "input.fifo");
        var socketPath = Path.Combine(directory, "input.socket");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllBytes(regular, [1]);
        File.WriteAllBytes(readOnly, [2]);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead);
        File.CreateSymbolicLink(link, regular);
        File.CreateSymbolicLink(danglingLink, Path.Combine(directory, "absent"));
        Socket? socket = null;
        try
        {
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "mkfifo",
                    UseShellExecute = false,
                },
            })
            {
                process.StartInfo.ArgumentList.Add(fifo);
                Assert.True(process.Start());
                Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, process.ExitCode);
            }

            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            var platform = new SystemSetupPlatform();

            Assert.Equal(SetupPathKind.File, platform.FileSystem.GetPathMetadata(regular).Kind);
            Assert.Equal(SetupPathKind.File, platform.FileSystem.GetPathMetadata(readOnly).Kind);
            Assert.Equal(SetupPathKind.Directory, platform.FileSystem.GetPathMetadata(childDirectory).Kind);
            Assert.False(platform.FileSystem.GetPathMetadata(Path.Combine(directory, "missing")).Exists);
            AssertNoFollowReparse(platform.FileSystem.GetPathMetadata(link));
            AssertNoFollowReparse(platform.FileSystem.GetPathMetadata(danglingLink));
            Assert.Equal(SetupPathKind.Other, platform.FileSystem.GetPathMetadata(fifo).Kind);
            Assert.Equal(SetupPathKind.Other, platform.FileSystem.GetPathMetadata(socketPath).Kind);
            Assert.Equal(SetupPathKind.Other, platform.FileSystem.GetPathMetadata("/dev/null").Kind);
        }
        finally
        {
            socket?.Dispose();
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(link);
            File.Delete(danglingLink);
            File.Delete(fifo);
            File.Delete(socketPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SystemPlatform_WindowsUsesNoFollowMetadataForRegularDirectoryAndLinks()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotEqual(SetupPathStyle.Windows, new SystemSetupPlatform().PathStyle);
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"cao-platform-native-{Guid.NewGuid():N}");
        var regular = Path.Combine(directory, "regular.bin");
        var readOnly = Path.Combine(directory, "read-only.bin");
        var childDirectory = Path.Combine(directory, "child");
        var fileLink = Path.Combine(directory, "file-link");
        var directoryLink = Path.Combine(directory, "directory-link");
        var danglingLink = Path.Combine(directory, "dangling-link");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllBytes(regular, [1]);
        File.WriteAllBytes(readOnly, [2]);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            File.CreateSymbolicLink(fileLink, regular);
            Directory.CreateSymbolicLink(directoryLink, childDirectory);
            File.CreateSymbolicLink(danglingLink, Path.Combine(directory, "absent"));
            var platform = new SystemSetupPlatform();

            Assert.Equal(SetupPathKind.File, platform.FileSystem.GetPathMetadata(regular).Kind);
            Assert.Equal(SetupPathKind.File, platform.FileSystem.GetPathMetadata(readOnly).Kind);
            Assert.Equal(SetupPathKind.Directory, platform.FileSystem.GetPathMetadata(childDirectory).Kind);
            Assert.False(platform.FileSystem.GetPathMetadata(Path.Combine(directory, "missing")).Exists);
            AssertNoFollowReparse(platform.FileSystem.GetPathMetadata(fileLink));
            AssertNoFollowReparse(platform.FileSystem.GetPathMetadata(directoryLink));
            AssertNoFollowReparse(platform.FileSystem.GetPathMetadata(danglingLink));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
            File.Delete(fileLink);
            Directory.Delete(directoryLink);
            File.Delete(danglingLink);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SystemPlatform_MapsFailedEnvironmentNotificationToFixedSafeError()
    {
        var platform = new SystemSetupPlatform(notificationAttempt: () => false);

        var exception = Assert.Throws<InvalidOperationException>(() => platform.UserEnvironment.NotifyChange());

        Assert.Equal("setup_environment_notification_failed", exception.Message);
    }

    [Fact]
    public void SystemPlatform_MapsEnvironmentNotificationExceptionToFixedSafeError()
    {
        var platform = new SystemSetupPlatform(notificationAttempt: () => throw new InvalidOperationException("raw operating system message"));

        var exception = Assert.Throws<InvalidOperationException>(() => platform.UserEnvironment.NotifyChange());

        Assert.Equal("setup_environment_notification_failed", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void TestPlatform_RecordsFilesystemEnvironmentAndCheckpointOperationsInOrder()
    {
        var platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        platform.SeedFile("plan.json", []);

        platform.FileSystem.WriteAllBytes("plan.tmp", [1, 2, 3]);
        platform.FileSystem.FlushFile("plan.tmp");
        platform.FileSystem.ReplaceFile("plan.tmp", "plan.json");
        platform.UserEnvironment.Set("COPILOT_OTEL_ENABLED", "true");
        platform.Execution.Checkpoint("after-environment-write");
        platform.UserEnvironment.NotifyChange();

        Assert.Equal(
        [
            "file.write:plan.tmp",
            "file.flush:plan.tmp",
            "file.replace:plan.tmp->plan.json",
            "environment.set:COPILOT_OTEL_ENABLED",
            "checkpoint:after-environment-write",
            "environment.notify",
        ],
        platform.Operations);
        Assert.Equal(new byte[] { 1, 2, 3 }, platform.ReadSeededFile("plan.json"));
        Assert.Equal("true", platform.ReadUserEnvironment("COPILOT_OTEL_ENABLED"));
    }

    [Fact]
    public void TestPlatform_ThrowsInjectedFaultBeforeMutatingState()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", "false");
        platform.InjectFault("environment.set:COPILOT_OTEL_ENABLED", new IOException("synthetic"));

        Assert.Throws<IOException>(() => platform.UserEnvironment.Set("COPILOT_OTEL_ENABLED", "true"));

        Assert.Equal("false", platform.ReadUserEnvironment("COPILOT_OTEL_ENABLED"));
        Assert.Equal(["environment.set:COPILOT_OTEL_ENABLED"], platform.Operations);
    }

    [Fact]
    public async Task TestPlatform_BarrierBlocksUntilExplicitReleaseWithoutSleep()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var barrier = platform.AddBarrier("checkpoint:before-final-verification");

        var task = Task.Run(() => platform.Execution.Checkpoint("before-final-verification"));
        try
        {
            await Task.Run(() => barrier.WaitUntilReached(CancellationToken.None));
            Assert.False(task.IsCompleted);
            barrier.Release();
            await task;
        }
        finally
        {
            barrier.Release();
            await ObserveWorkersAsync(task);
        }

        Assert.Equal(["checkpoint:before-final-verification"], platform.Operations);
    }

    [Fact]
    public void TestPlatform_UserEnvironmentIsolatedInMemory()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", "false");

        platform.UserEnvironment.Set("COPILOT_OTEL_ENABLED", "true");

        Assert.Equal("true", platform.UserEnvironment.Get("COPILOT_OTEL_ENABLED"));
        Assert.Equal(
        [
            "environment.set:COPILOT_OTEL_ENABLED",
            "environment.get:COPILOT_OTEL_ENABLED",
        ],
        platform.Operations);
    }

    [Fact]
    public void TestPlatform_BarrierIgnoresWrongCheckpoint()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var barrier = platform.AddBarrier("checkpoint:expected");

        try
        {
            platform.Execution.Checkpoint("other");

            Assert.False(barrier.HasReached);
        }
        finally
        {
            barrier.Release();
        }
    }

    [Fact]
    public async Task TestPlatform_BarrierDoesNotBlockAWorkerFault()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var barrier = platform.AddBarrier("checkpoint:faulting");
        platform.InjectFault("checkpoint:faulting", new IOException("synthetic"));

        var task = Task.Run(() => platform.Execution.Checkpoint("faulting"));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => task);
            Assert.False(barrier.HasReached);
        }
        finally
        {
            barrier.Release();
            await ObserveWorkersAsync(task);
        }
    }

    [Fact]
    public async Task TestPlatform_BarrierCancellationReleasesWorker()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        using var barrier = platform.AddBarrier("checkpoint:cancellable", cancellation.Token);
        using var hostGuard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Task.Run(() => platform.Execution.Checkpoint("cancellable"));

        try
        {
            await Task.Run(() => barrier.WaitUntilReached(hostGuard.Token));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        finally
        {
            barrier.Release();
            await ObserveWorkersAsync(task);
        }
    }

    [Fact]
    public async Task TestPlatform_DisposedBarrierNameCanBeReusedByAFreshBarrier()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var previous = platform.AddBarrier("checkpoint:reused");
        previous.Dispose();
        using var current = platform.AddBarrier("checkpoint:reused");
        var task = Task.Run(() => platform.Execution.Checkpoint("reused"));

        try
        {
            await Task.Run(() => current.WaitUntilReached(CancellationToken.None));
            Assert.False(task.IsCompleted);
            current.Release();
            await task;
        }
        finally
        {
            current.Release();
            previous.Release();
            await ObserveWorkersAsync(task);
        }
    }

    [Fact]
    public async Task TestPlatform_PreDisposeLeaseRemainsWithOldBarrierAfterNameReuse()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var previous = platform.AddBarrier("checkpoint:reused");
        SetupTestBarrier? current = null;
        var previousTask = Task.Run(() => platform.Execution.Checkpoint("reused"));
        Task? currentTask = null;

        try
        {
            await Task.Run(() => previous.WaitUntilReached(CancellationToken.None));
            previous.Dispose();
            current = platform.AddBarrier("checkpoint:reused");
            currentTask = Task.Run(() => platform.Execution.Checkpoint("reused"));

            await previousTask;
            await Task.Run(() => current.WaitUntilReached(CancellationToken.None));
            Assert.False(currentTask.IsCompleted);
            current.Release();
            await currentTask;
        }
        finally
        {
            current?.Release();
            previous.Release();
            await ObserveWorkersAsync(previousTask, currentTask);
        }
    }

    [Fact]
    public async Task TestPlatform_BarrierWaitsForEveryArrivalBeforeRelease()
    {
        const int workerCount = 4;
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var barrier = platform.AddBarrier("checkpoint:many");
        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => platform.Execution.Checkpoint("many")))
            .ToArray();

        try
        {
            await Task.Run(() => barrier.WaitUntilArrivals(workerCount, CancellationToken.None));
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
            barrier.Release();
            await Task.WhenAll(tasks);
        }
        finally
        {
            barrier.Release();
            await ObserveWorkersAsync(tasks);
        }
    }

    [Fact]
    public async Task TestPlatform_ConcurrentDisposeReleasesAllWorkersWithoutObjectDisposedExceptions()
    {
        const int workerCount = 4;
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        using var barrier = platform.AddBarrier("checkpoint:dispose");
        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => platform.Execution.Checkpoint("dispose")))
            .ToArray();

        try
        {
            await Task.Run(() => barrier.WaitUntilArrivals(workerCount, CancellationToken.None));
            barrier.Dispose();
            await Task.WhenAll(tasks);
        }
        finally
        {
            barrier.Release();
            await ObserveWorkersAsync(tasks);
        }
    }

    [Fact]
    public void TestPlatform_CreatesDeterministicUuidV7Identifiers()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);

        var first = platform.Identifiers.CreateUuidV7();
        var second = platform.Identifiers.CreateUuidV7();

        Assert.Equal("00000000-0000-7000-8000-000000000001", first.ToString("D"));
        Assert.Equal("00000000-0000-7000-8000-000000000002", second.ToString("D"));
    }

    [Fact]
    public void TestPlatform_CreatesDirectoriesAndCoordinatesExclusiveFileLocks()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);

        platform.FileSystem.CreateDirectory("runtime");
        var first = platform.FileSystem.TryAcquireExclusiveFileLock("runtime\\setup.lock");
        var contended = platform.FileSystem.TryAcquireExclusiveFileLock("runtime\\setup.lock");
        first!.Dispose();
        var reacquired = platform.FileSystem.TryAcquireExclusiveFileLock("runtime\\setup.lock");
        reacquired!.Dispose();

        Assert.NotNull(first);
        Assert.Null(contended);
        Assert.NotNull(reacquired);
        Assert.Equal(
        [
            "directory.create:runtime",
            "file.lock:runtime\\setup.lock",
            "file.lock:runtime\\setup.lock",
            "file.lock:runtime\\setup.lock",
        ],
        platform.Operations);
    }

    [Fact]
    public void TestPlatform_WindowsPathsAndLocksUseOneCaseInsensitiveComparer()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, pathStyle: SetupPathStyle.Windows);
        platform.SeedDirectory("C:\\ROOT");
        platform.SeedFile("C:\\ROOT\\File.bin", [1]);

        Assert.Equal(new byte[] { 1 }, platform.FileSystem.ReadAllBytes("c:\\root\\file.BIN"));
        Assert.Throws<IOException>(() => platform.FileSystem.WriteNewAllBytes("c:\\root\\FILE.bin", [2]));
        var first = platform.FileSystem.TryAcquireExclusiveFileLock("C:\\ROOT\\Setup.lock");
        var collision = platform.FileSystem.TryAcquireExclusiveFileLock("c:\\root\\setup.LOCK");

        Assert.NotNull(first);
        Assert.Null(collision);
        first!.Dispose();
    }

    [Fact]
    public void TestPlatform_UnixPathsAndLocksUseOneCaseSensitiveComparer()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch, pathStyle: SetupPathStyle.Unix);
        platform.SeedDirectory("/root");
        platform.SeedFile("/root/File.bin", [1]);

        platform.FileSystem.WriteNewAllBytes("/root/file.bin", [2]);
        var first = platform.FileSystem.TryAcquireExclusiveFileLock("/root/Setup.lock");
        var distinct = platform.FileSystem.TryAcquireExclusiveFileLock("/root/setup.lock");

        Assert.Equal(new byte[] { 1 }, platform.FileSystem.ReadAllBytes("/root/File.bin"));
        Assert.Equal(new byte[] { 2 }, platform.FileSystem.ReadAllBytes("/root/file.bin"));
        Assert.NotNull(first);
        Assert.NotNull(distinct);
        first!.Dispose();
        distinct!.Dispose();
    }

    [Fact]
    public void TestPlatform_ReplaceAndMoveMatchSystemExistenceAndOverwriteSemantics()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.SeedFile("source", [1]);

        Assert.Throws<IOException>(() => platform.FileSystem.CreateDirectory("source"));
        Assert.Throws<IOException>(() => platform.FileSystem.ReplaceFile("source", "missing-destination"));
        Assert.Equal(new byte[] { 1 }, platform.ReadSeededFile("source"));
        Assert.Throws<IOException>(() => platform.FileSystem.MoveFile("missing-source", "destination", overwrite: false));

        platform.SeedFile("destination", [2]);
        Assert.Throws<IOException>(() => platform.FileSystem.MoveFile("source", "destination", overwrite: false));
        Assert.Equal(new byte[] { 1 }, platform.ReadSeededFile("source"));
        Assert.Equal(new byte[] { 2 }, platform.ReadSeededFile("destination"));

        platform.FileSystem.MoveFile("source", "destination", overwrite: true);
        Assert.False(platform.FileSystem.FileExists("source"));
        Assert.Equal(new byte[] { 1 }, platform.ReadSeededFile("destination"));

        platform.SeedDirectory("directory");
        Assert.Throws<UnauthorizedAccessException>(() => platform.FileSystem.DeleteFile("directory"));
        platform.FileSystem.DeleteFile("missing");
    }

    [Fact]
    public void TestPlatform_DoubleDisposingAStaleLockDoesNotReleaseTheNewOwner()
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        var first = platform.FileSystem.TryAcquireExclusiveFileLock("setup.lock");
        first!.Dispose();
        var second = platform.FileSystem.TryAcquireExclusiveFileLock("setup.lock");
        first.Dispose();

        var contended = platform.FileSystem.TryAcquireExclusiveFileLock("setup.lock");
        second!.Dispose();
        var reacquired = platform.FileSystem.TryAcquireExclusiveFileLock("setup.lock");

        Assert.NotNull(second);
        Assert.Null(contended);
        Assert.NotNull(reacquired);
        reacquired!.Dispose();
    }

    private static async Task ObserveWorkersAsync(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task;
            }
            catch (Exception)
            {
            }
        }
    }

    private static void AssertNoFollowReparse(SetupPathMetadata metadata)
    {
        Assert.True(metadata.Exists);
        Assert.Equal(SetupPathKind.Other, metadata.Kind);
        Assert.True((metadata.Attributes & FileAttributes.ReparsePoint) != 0);
    }
}

using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupPlatformTests
{
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
        }
        finally
        {
            barrier.Release();
        }

        await task;

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
}

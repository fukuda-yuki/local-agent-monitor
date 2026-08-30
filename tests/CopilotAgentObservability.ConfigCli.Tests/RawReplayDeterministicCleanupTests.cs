using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayStagedFileDeterministicTests
{
    [Fact]
    public async Task FirstDeleteFailureRetainsOwnershipUntilManualRetryDeletesExactPath()
    {
        using var directory = new RawReplayAuthorizedServiceTerminalTests.TempDirectory();
        var stagedPath = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        File.WriteAllBytes(stagedPath, [1, 2, 3]);
        var deleteAttempts = 0;
        var secondDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new ManualRetryTimeProvider();
        var staged = new RawReplayStagedFile(
            stagedPath,
            Path.Combine(directory.Path, "raw-local-replay.zip"),
            path =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1)
                    throw new IOException("synthetic delete contention");
                File.Delete(path);
                secondDelete.TrySetResult();
            },
            timeProvider);

        staged.Dispose();

        Assert.Equal(1, deleteAttempts);
        Assert.True(File.Exists(stagedPath));
        Assert.True(timeProvider.Armed);

        timeProvider.Fire();
        await secondDelete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(timeProvider.Fired);
        Assert.Equal(2, deleteAttempts);
        Assert.False(File.Exists(stagedPath));
    }
}

public sealed class RawReplayAuthorizedServiceTerminalFastTests
{
    [Theory]
    [InlineData((int)RawReplaySnapshotTerminalResult.Lost, "snapshot_read_denied")]
    [InlineData((int)RawReplaySnapshotTerminalResult.Busy, "snapshot_store_busy")]
    public async Task LostAndBusyRetainCleanupAndPreserveExactErrorMapping(
        int terminalResultValue,
        string expectedError)
    {
        using var directory = new RawReplayAuthorizedServiceTerminalTests.TempDirectory();
        var stagedPath = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        var deleteAttempts = 0;
        var secondDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new ManualRetryTimeProvider();
        var snapshot = RawReplayAuthorizedServiceTerminalTests.ValidSnapshot();
        var archiveService = RawReplayAuthorizedServiceTerminalTests.ArchiveService(
            stagedPath,
            deleteFile: path =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1)
                    throw new UnauthorizedAccessException("synthetic delete contention");
                File.Delete(path);
                secondDelete.TrySetResult();
            },
            timeProvider: timeProvider);
        var provider = new RawReplayAuthorizedServiceTerminalTests.TerminalProvider(
            snapshot,
            (RawReplaySnapshotTerminalResult)terminalResultValue);

        var result = await new RawReplayAuthorizedService(provider, archiveService).CreateAndPublishAsync(
            RawReplayAuthorizedServiceTerminalTests.ConfirmedControl(snapshot),
            Path.Combine(directory.Path, "raw-local-replay.zip"));

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal(1, deleteAttempts);
        Assert.True(File.Exists(stagedPath));
        Assert.True(timeProvider.Armed);

        timeProvider.Fire();
        await secondDelete.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(timeProvider.Fired);
        Assert.Equal(2, deleteAttempts);
        Assert.False(File.Exists(stagedPath));
    }
}

internal sealed class ManualRetryTimeProvider : TimeProvider
{
    private ManualTimer? timer;

    internal bool Armed => timer?.Armed ?? false;
    internal bool Fired => timer?.Fired ?? false;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        Assert.Null(timer);
        timer = new ManualTimer(callback, state, dueTime);
        return timer;
    }

    internal void Fire()
    {
        Assert.NotNull(timer);
        timer.Fire();
    }

    private sealed class ManualTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime) : ITimer
    {
        private readonly object sync = new();
        private bool armed = dueTime != Timeout.InfiniteTimeSpan;
        private bool disposed;
        private bool fired;

        internal bool Armed { get { lock (sync) return armed; } }
        internal bool Fired { get { lock (sync) return fired; } }

        public bool Change(TimeSpan nextDueTime, TimeSpan period)
        {
            lock (sync)
            {
                if (disposed) return false;
                armed = nextDueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
        }

        internal void Fire()
        {
            lock (sync)
            {
                Assert.False(disposed);
                Assert.True(armed);
                armed = false;
                fired = true;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                armed = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

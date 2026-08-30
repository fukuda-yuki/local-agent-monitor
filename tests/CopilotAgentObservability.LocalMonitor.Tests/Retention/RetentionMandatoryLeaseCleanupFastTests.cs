using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMandatoryLeaseCleanupFastTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StandingDispatcherRetainsReleaseOwnershipUntilRetryCompletes()
    {
        var time = new ManualRetryTimeProvider();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [Grant(time.GetUtcNow())],
            time,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    return false;
                released.TrySetResult();
                return true;
            },
            beforeWaitingForReleaseForTesting: null);

        try
        {
            await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(CoordinationTimeout);
            Assert.True(time.Timer.IsScheduled);
            time.Timer.Fire();
            await released.Task.WaitAsync(CoordinationTimeout);
            await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(CoordinationTimeout);

            Assert.Equal(2, Volatile.Read(ref attempts));
        }
        finally
        {
            cleanup.Abandon();
        }
    }

    [Fact]
    public async Task TimerRearmFailureKeepsReleaseOwnershipInStandingDispatcher()
    {
        var time = new ManualRetryTimeProvider();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [Grant(time.GetUtcNow())],
            time,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    return false;
                released.TrySetResult();
                return true;
            },
            beforeWaitingForReleaseForTesting: null);
        time.Timer.ThrowOnChange = true;

        try
        {
            await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(CoordinationTimeout);
            await released.Task.WaitAsync(CoordinationTimeout);
            await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(CoordinationTimeout);

            Assert.Equal(2, Volatile.Read(ref attempts));
        }
        finally
        {
            cleanup.Abandon();
        }
    }

    private static RetentionReadGrant Grant(DateTimeOffset now) => new(
        new("store", RetentionStoreKind.RawRecord, "source"),
        "item",
        1,
        RetentionLeaseKind.Operation,
        "owner",
        1,
        now.AddMinutes(2),
        Enumerable.Repeat((byte)0x11, 32).ToArray());

    private sealed class ManualRetryTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        internal ManualTimer Timer { get; private set; } = null!;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(callback, state);
            Timer.Change(dueTime, period);
            return Timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool IsScheduled { get; private set; }
        internal bool ThrowOnChange { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (ThrowOnChange)
                throw new InvalidOperationException("synthetic_retry_rearm_failure");
            IsScheduled = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        internal void Fire()
        {
            IsScheduled = false;
            callback(state);
        }

        public void Dispose() => IsScheduled = false;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

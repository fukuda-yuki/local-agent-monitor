using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionWorkerRaceTests
{
    [Fact]
    public async Task OldExpiryGeneration_AfterRenewalIsStaleWhilePublicationScopeIsContended()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var grant = CreateGrant("first", 0x11, now.AddMinutes(2));
        var handle = new RetentionCommittedReadHandle([grant], time, _ => true);
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        var oldNotification = time.Timers[0];
        using (var publications = RetentionGrantPublicationSet.EnterInOrder([new(grant, 0)]))
        using (var renewal = publications.PrepareExpiryNotificationRenewal([0], now.AddMinutes(3)))
            Assert.True(renewal.Publish());
        oldNotification.ThrowOnChange = true;

        var publicationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var publication = grant.EnterLeasePublication();
            publicationHeld.SetResult();
            releasePublication.Wait();
        });
        try
        {
            await publicationHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldNotification.Fire();
            Assert.True(handle.IsPublished);
        }
        finally
        {
            releasePublication.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task DormantReplacement_DueBeforeActivationLosesCompleteComposite()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var grants = new[]
        {
            CreateGrant("first", 0x11, now.AddMinutes(2)),
            CreateGrant("second", 0x22, now.AddMinutes(2)),
        };
        var releases = 0;
        var handle = new RetentionCommittedReadHandle(grants, time, _ =>
        {
            Interlocked.Increment(ref releases);
            return true;
        });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        using (var publications = RetentionGrantPublicationSet.EnterInOrder([new(grants[0], 0), new(grants[1], 1)]))
        using (var renewal = publications.PrepareExpiryNotificationRenewal([0, 1], now.AddMinutes(3)))
        {
            time.Timers[2].Fire();
            Assert.False(renewal.Publish());
            Assert.Equal(now.AddMinutes(3), publications.LeaseExpiresAt(0));
            Assert.Equal(now.AddMinutes(3), publications.LeaseExpiresAt(1));
        }
        Assert.False(handle.IsPublished);
        await WaitUntilAsync(() => Volatile.Read(ref releases) == 1);
        await handle.DisposeAsync();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RenewalNotificationPreparationFailure_LeavesCompositePublicationAndOldNotificationUnchanged(
        bool throwOnConstruction,
        bool rejectArm)
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now)
        {
            ThrowOnCreateOrdinal = throwOnConstruction ? 3 : 0,
            RejectChangeOrdinal = rejectArm ? 3 : 0,
        };
        var grants = new[]
        {
            CreateGrant("first", 0x11, now.AddMinutes(2)),
            CreateGrant("second", 0x22, now.AddMinutes(2)),
        };
        var handle = new RetentionCommittedReadHandle(grants, time, _ => true);
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        using var publications = RetentionGrantPublicationSet.EnterInOrder([new(grants[0], 0), new(grants[1], 1)]);

        Assert.ThrowsAny<Exception>(() =>
            publications.PrepareExpiryNotificationRenewal([0, 1], now.AddMinutes(3)));

        Assert.Equal(now.AddMinutes(2), publications.LeaseExpiresAt(0));
        Assert.Equal(now.AddMinutes(2), publications.LeaseExpiresAt(1));
        Assert.True(handle.IsPublished);
        Assert.False(time.Timers[0].IsDisposed);
        publications.Dispose();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task CurrentExpiryNotification_BeforePublishedExpiryIsANoOpUntilExactExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var releases = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                Interlocked.Increment(ref releases);
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());

        time.Timers[0].Fire();

        Assert.True(handle.IsPublished);
        Assert.Equal(0, Volatile.Read(ref releases));

        time.Advance(TimeSpan.FromMinutes(2));
        time.Timers[0].Fire();
        Assert.False(handle.IsPublished);
        time.Timers[1].Fire();
        await WaitUntilAsync(() => Volatile.Read(ref releases) == 1);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task CurrentExpiryNotification_PreExpiryRearmFailureLosesHandleWithoutPropagating()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var releases = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                Interlocked.Increment(ref releases);
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        time.Timers[0].ThrowOnChange = true;

        var exception = Record.Exception(time.Timers[0].Fire);

        Assert.Null(exception);
        Assert.False(handle.IsPublished);
        await WaitUntilAsync(() => Volatile.Read(ref releases) == 1);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task StaleExpiryNotification_RearmFailureAfterRenewalDoesNotLoseCurrentHandle()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var grant = CreateGrant("first", 0x11, now.AddMinutes(2));
        var handle = new RetentionCommittedReadHandle([grant], time, _ => true);
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        var oldNotification = time.Timers[0];
        oldNotification.RejectChange = true;
        oldNotification.BeforeChange = () =>
        {
            using var publications = RetentionGrantPublicationSet.EnterInOrder([new(grant, 0)]);
            using var renewal = publications.PrepareExpiryNotificationRenewal([0], now.AddMinutes(3));
            Assert.True(renewal.Publish());
        };

        oldNotification.Fire();

        Assert.True(handle.IsPublished);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task MandatoryCleanup_ReleaseExceptionDoesNotEscapeAndLeavesRetryScheduled()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var attempts = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("synthetic_release_failure");
                return true;
            });

        var exception = await Record.ExceptionAsync(async () => await handle.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.True(time.Timers[1].IsScheduled);
        time.Timers[1].Fire();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 2);
    }

    [Fact]
    public async Task MandatoryCleanup_TimerCallbackReleaseExceptionDoesNotEscapeAndRetainsRetry()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var attempts = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ => Interlocked.Increment(ref attempts) switch
            {
                1 => false,
                2 => throw new InvalidOperationException("synthetic_callback_release_failure"),
                _ => true,
            });
        await handle.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.True(time.Timers[1].IsScheduled);

        var exception = Record.Exception(time.Timers[1].Fire);

        Assert.Null(exception);
        await WaitUntilAsync(() =>
            Volatile.Read(ref attempts) == 2
            && time.Timers[1].IsScheduled);
        Assert.True(time.Timers[1].IsScheduled);
        time.Timers[1].Fire();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 3);
    }

    [Fact]
    public async Task MandatoryCleanup_TimerFailuresDoNotEscapeOrAbandonReleaseAuthority()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var attempts = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ => Interlocked.Increment(ref attempts) > 1);
        time.Timers[1].ThrowOnChange = true;
        time.Timers[1].ThrowOnDispose = true;

        var exception = await Record.ExceptionAsync(async () => await handle.DisposeAsync());

        Assert.Null(exception);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 2);
    }

    [Fact]
    public async Task MandatoryCleanup_TimerCallbackSignalsWorkerWithoutWaitingForRelease()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        time.Advance(TimeSpan.FromMinutes(2));
        time.Timers[0].Fire();

        var callback = Task.Run(time.Timers[1].Fire);
        try
        {
            await callback.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            allowRelease.Set();
        }
        await handle.DisposeAsync();
    }

    [Fact]
    public void MandatoryCleanup_TimerCallbackReturnsWithoutSpinningWhenRetryRearmFails()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var attempts = 0;
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ => Interlocked.Increment(ref attempts) > 1,
            beforeWaitingForReleaseForTesting: null);
        cleanup.ReleaseOrOwn();
        var retry = Assert.Single(time.Timers);
        retry.ThrowOnChange = true;
        using var callbackReturned = new ManualResetEventSlim();
        var callback = new Thread(() =>
        {
            retry.Fire();
            callbackReturned.Set();
        })
        {
            IsBackground = true,
        };

        try
        {
            callback.Start();
            Assert.True(
                callbackReturned.Wait(TimeSpan.FromSeconds(1)),
                "The cleanup timer callback spun after both retry scheduling and worker signaling failed.");
        }
        finally
        {
            cleanup.Abandon();
            Assert.True(callback.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task MandatoryCleanup_StandingDispatcherRetainsExactLeaseReleaseOwnership()
    {
        using var fixture = CreateFixture();
        var expiry = fixture.Time.GetUtcNow().AddMinutes(2);
        InsertOperationLease(fixture.Path, fixture.ItemId, expiry);
        var time = new ManualNotificationTimeProvider(fixture.Time.GetUtcNow());
        var attempts = 0;
        var grant = new RetentionReadGrant(
            new(fixture.Context.StoreInstanceId, RetentionStoreKind.RawRecord, "1"),
            fixture.ItemId,
            1,
            RetentionLeaseKind.Operation,
            "reader",
            1,
            expiry,
            Enumerable.Repeat((byte)0x11, 32).ToArray());
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [grant],
            time,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1) return false;
                Execute(
                    fixture.Path,
                    "DELETE FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
                    ("$item", fixture.ItemId));
                return true;
            },
            beforeWaitingForReleaseForTesting: null);

        await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1L, Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId));
        Assert.True(Assert.Single(time.Timers).IsScheduled);
        time.Timers[0].Fire();
        await WaitUntilAsync(() => Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId) == 0L);
        await cleanup.ReleaseOrOwnAsync();

        Assert.Equal(0L, Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task MandatoryCleanup_TimerRearmFailureStillRetainsExactLeaseReleaseOwnership()
    {
        using var fixture = CreateFixture();
        var expiry = fixture.Time.GetUtcNow().AddMinutes(2);
        InsertOperationLease(fixture.Path, fixture.ItemId, expiry);
        var time = new ManualNotificationTimeProvider(fixture.Time.GetUtcNow());
        var attempts = 0;
        var grant = new RetentionReadGrant(
            new(fixture.Context.StoreInstanceId, RetentionStoreKind.RawRecord, "1"),
            fixture.ItemId,
            1,
            RetentionLeaseKind.Operation,
            "reader",
            1,
            expiry,
            Enumerable.Repeat((byte)0x11, 32).ToArray());
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [grant],
            time,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1) return false;
                Execute(
                    fixture.Path,
                    "DELETE FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
                    ("$item", fixture.ItemId));
                return true;
            },
            beforeWaitingForReleaseForTesting: null);
        Assert.False(Assert.Single(time.Timers).IsScheduled);
        time.Timers[0].ThrowOnChange = true;

        await cleanup.ReleaseOrOwnAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId) == 0L);
        await cleanup.ReleaseOrOwnAsync();

        Assert.Equal(0L, Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public void MandatoryCleanup_SynchronousHandoffDoesNotWaitForAnActiveRelease()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        using var handoffReturned = new ManualResetEventSlim();
        var cleanup = new RetentionMandatoryLeaseCleanup(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
                return true;
            },
            beforeWaitingForReleaseForTesting: null);
        cleanup.Own();
        Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));
        var handoff = new Thread(() =>
        {
            cleanup.ReleaseOrOwn();
            handoffReturned.Set();
        })
        {
            IsBackground = true,
        };

        try
        {
            handoff.Start();
            Assert.True(
                handoffReturned.Wait(TimeSpan.FromSeconds(1)),
                "Synchronous cleanup handoff waited without a bound for the active release.");
        }
        finally
        {
            allowRelease.Set();
            Assert.True(handoff.Join(TimeSpan.FromSeconds(5)));
            cleanup.Abandon();
        }
    }

    [Fact]
    public async Task DisposeAsync_InFlightAsynchronousReleaseWaitsUntilLeaseRowIsDeleted()
    {
        using var fixture = CreateFixture();
        var expiry = fixture.Time.GetUtcNow().AddMinutes(2);
        InsertOperationLease(fixture.Path, fixture.ItemId, expiry);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        var callerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new RetentionReadGrant(
            new(fixture.Context.StoreInstanceId, RetentionStoreKind.RawRecord, "1"),
            fixture.ItemId,
            1,
            RetentionLeaseKind.Operation,
            "reader",
            1,
            expiry,
            Enumerable.Repeat((byte)0x11, 32).ToArray());
        var handle = new RetentionCommittedReadHandle(
            [grant],
            fixture.Time,
            _ =>
            {
                releaseEntered.Set();
                if (!allowRelease.Wait(TimeSpan.FromSeconds(5))) return false;
                Execute(
                    fixture.Path,
                    "DELETE FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
                    ("$item", fixture.ItemId));
                return true;
            },
            () => callerWaiting.TrySetResult());
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        handle.LoseAsynchronously();
        Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));

        var dispose = Task.Run(async () => await handle.DisposeAsync());
        try
        {
            await callerWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1L, Number(
                fixture.Path,
                "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
                fixture.ItemId));
        }
        finally
        {
            allowRelease.Set();
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0L, Number(
            fixture.Path,
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation' AND owner='reader' AND generation=1;",
            fixture.ItemId));
    }

    [Fact]
    public async Task DisposeAsync_InFlightWorkerReturnsIncompleteValueTaskWithoutBlockingCallerThread()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        handle.LoseAsynchronously();
        Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));
        var disposeReturned = new TaskCompletionSource<ValueTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(
            _ => disposeReturned.TrySetResult(handle.DisposeAsync()),
            state: (object?)null,
            preferLocal: false);

        var dispose = await disposeReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(dispose.IsCompleted);
        allowRelease.Set();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallerWaitsForTheSameLeaseRelease()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        var first = handle.DisposeAsync();
        Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));

        var second = handle.DisposeAsync();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        allowRelease.Set();
        await Task.WhenAll(first.AsTask(), second.AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentFinalReleaseRunsExactlyOnce()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualNotificationTimeProvider(now);
        var releases = 0;
        var handle = new RetentionCommittedReadHandle(
            [CreateGrant("first", 0x11, now.AddMinutes(2))],
            time,
            _ =>
            {
                Interlocked.Increment(ref releases);
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => handle.DisposeAsync().AsTask())
            .ToArray();

        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref releases));
    }

    [Fact]
    public async Task HiddenHandle_PartiallyRenewedCompositeExpiresAtEarliestPublishedMemberExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var admissionExpiry = now.AddMinutes(2);
        var time = new MutableTimeProvider(now);
        var grants = new[]
        {
            CreateGrant("first", 0x11, admissionExpiry),
            CreateGrant("second", 0x22, admissionExpiry),
        };
        var releaseCount = 0;
        var handle = new RetentionCommittedReadHandle(
            grants,
            time,
            _ =>
            {
                Interlocked.Increment(ref releaseCount);
                return true;
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        grants[0].AdvanceExpiry(admissionExpiry.AddMinutes(1));

        time.Advance(admissionExpiry - now);
        time.Advance(TimeSpan.Zero);

        Assert.False(handle.IsPublished);
        await WaitUntilAsync(() => Volatile.Read(ref releaseCount) == 1);
        Assert.Equal(1, Volatile.Read(ref releaseCount));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task MandatoryHiddenLeaseCleanup_RetriesTheCompleteOwnedFrontierAfterInitialContention()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var grants = new[]
        {
            CreateGrant("first", 0x11, now.AddMinutes(2)),
            CreateGrant("second", 0x22, now.AddMinutes(2)),
        };
        var attempts = new List<string[]>();
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new RetentionCommittedReadHandle(
            grants,
            time,
            frontier =>
            {
                int count;
                lock (attempts)
                {
                    attempts.Add(frontier.Select(grant => grant.ItemId).ToArray());
                    count = attempts.Count;
                }
                if (count == 2) secondAttempt.TrySetResult();
                return count > 1;
            });

        await handle.DisposeAsync();

        lock (attempts)
            Assert.Equal(["first", "second"], Assert.Single(attempts));
        time.Advance(TimeSpan.FromMilliseconds(10));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (attempts)
        {
            Assert.Equal(2, attempts.Count);
            Assert.All(attempts, attempt => Assert.Equal(["first", "second"], attempt));
        }
    }

    [Fact]
    public async Task CoalescedWakeAndStopDuringQuiescence_DoNotReclaimOrInvokeAdapter()
    {
        using var fixture = CreateFixture();
        InsertOperationLease(fixture.Path, fixture.ItemId, fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        var raw = new CountingAdapter(RetentionStoreKind.RawRecord);
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time), fixture.Time);

        await worker.StartAsync();
        await DrainAsync();
        worker.Wake();
        worker.Wake();
        worker.Wake();
        await worker.StopAsync();

        Assert.Equal(0, raw.Calls);
        Assert.Equal("deletion_queued", Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", fixture.ItemId));
        Assert.Equal(0L, Number(fixture.Path, "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion'", fixture.ItemId));
    }

    [Fact]
    public async Task CatalogBusyClaim_LeavesCatalogByteForByteUnchanged()
    {
        using var fixture = CreateFixture();
        var before = Snapshot(fixture.Path, fixture.ItemId);
        using var blocker = Open(fixture.Path);
        using var command = blocker.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();

        var result = await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "worker", fixture.Time.GetUtcNow(), CancellationToken.None);

        Assert.Equal(RetentionClaimDisposition.CatalogBusy, result.Disposition);
        command.CommandText = "ROLLBACK;";
        command.ExecuteNonQuery();
        Assert.Equal(before, Snapshot(fixture.Path, fixture.ItemId));
    }

    [Fact]
    public async Task CoverageMismatch_BlocksEntireCycleBeforeAnyAdapterInvocation()
    {
        using var fixture = CreateFixture();
        AddQueuedRawItem(fixture.Path, fixture.Context, fixture.Time, "second");
        Execute(fixture.Path, "DELETE FROM retention_adapter_coverage WHERE store_kind='analysis_sdk_directory';");
        Execute(fixture.Path, "CREATE TABLE coverage_block_audit(value INTEGER NOT NULL); CREATE TRIGGER coverage_block_observed AFTER UPDATE OF worker_error_code ON retention_worker_state BEGIN INSERT INTO coverage_block_audit VALUES (1); END;");
        var raw = new CountingAdapter(RetentionStoreKind.RawRecord);

        await new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time).RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.Equal(0, raw.Calls);
        Assert.Equal(1L, ScalarNumber(fixture.Path, "SELECT COUNT(*) FROM coverage_block_audit"));
        Assert.Equal("deletion_queued", Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", fixture.ItemId));
        Assert.Equal("deletion_queued", Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", "second"));
    }

    [Fact]
    public async Task CoverageMismatch_PreflightPreservesAllCleanupStateBeforeTwoConsumersCanClaim()
    {
        using var fixture = CreateFixture();
        AddQueuedRawItem(fixture.Path, fixture.Context, fixture.Time, "second");
        AddQueuedRawItem(fixture.Path, fixture.Context, fixture.Time, "abandoned");
        AddQueuedRawItem(fixture.Path, fixture.Context, fixture.Time, "expired-pending");
        Execute(fixture.Path, "UPDATE retention_items SET state='expired_pending_deletion',revision=7 WHERE item_id='expired-pending';");
        Execute(fixture.Path, "UPDATE retention_items SET state='deleting',revision=9 WHERE item_id='abandoned'; INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('abandoned','deletion','lost',$expired,1);", ("$expired", fixture.Time.GetUtcNow().AddMinutes(-1).ToString("O")));
        Execute(fixture.Path, "DELETE FROM retention_adapter_coverage WHERE store_kind='analysis_sdk_directory';");
        var itemsBefore = TableSnapshot(fixture.Path, "retention_items", "item_id,state,revision,attempt_count,read_denied_at,queued_at,deletion_started_at,next_retry_at,error_code,retry_exhausted");
        var leasesBefore = TableSnapshot(fixture.Path, "retention_leases", "item_id,lease_kind,owner,expires_at,generation");
        var journalBefore = TableSnapshot(fixture.Path, "retention_delete_journal", "item_id,durable_cursor,expected_revision,intent_at");
        var tombstonesBefore = TableSnapshot(fixture.Path, "retention_tombstones", "item_id");
        var workerBefore = Text(fixture.Path, "SELECT COALESCE(last_successful_run_at,'') || ':' || COALESCE(maintenance_due_at,'') || ':' || COALESCE(maintenance_error_code,'') || ':' || maintenance_generation FROM retention_worker_state WHERE id=1", fixture.ItemId);
        var raw = new CountingAdapter(RetentionStoreKind.RawRecord);

        var result = await new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time).RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.Equal(0, raw.Calls);
        Assert.False(result.Clean);
        Assert.Equal(0, result.Dispatched);
        Assert.Equal(0, result.Completed);
        Assert.Equal(itemsBefore, TableSnapshot(fixture.Path, "retention_items", "item_id,state,revision,attempt_count,read_denied_at,queued_at,deletion_started_at,next_retry_at,error_code,retry_exhausted"));
        Assert.Equal(leasesBefore, TableSnapshot(fixture.Path, "retention_leases", "item_id,lease_kind,owner,expires_at,generation"));
        Assert.Equal(journalBefore, TableSnapshot(fixture.Path, "retention_delete_journal", "item_id,durable_cursor,expected_revision,intent_at"));
        Assert.Equal(tombstonesBefore, TableSnapshot(fixture.Path, "retention_tombstones", "item_id"));
        Assert.Equal(workerBefore, Text(fixture.Path, "SELECT COALESCE(last_successful_run_at,'') || ':' || COALESCE(maintenance_due_at,'') || ':' || COALESCE(maintenance_error_code,'') || ':' || maintenance_generation FROM retention_worker_state WHERE id=1", fixture.ItemId));
        Assert.Equal("retention_adapter_coverage_mismatch", Text(fixture.Path, "SELECT worker_error_code FROM retention_worker_state WHERE id=1", fixture.ItemId));

        Execute(fixture.Path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('analysis_sdk_directory',1);");
        var recovered = await new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time).RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.True(recovered.Dispatched > 0);
        Assert.True(raw.Calls > 0);
    }

    [Fact]
    public async Task DueWinnerDoesNotLeaveAWakeLoserToStealTheNextCoalescedWake()
    {
        using var fixture = CreateFixture();
        var due = fixture.Time.GetUtcNow() + TimeSpan.FromSeconds(5);
        Execute(fixture.Path, "UPDATE retention_items SET state='deletion_failed',attempt_count=1,next_retry_at=$due WHERE item_id=$item", ("$due", due.ToString("O")), ("$item", fixture.ItemId));
        var timers = 0;
        var nextWaitReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Time.TimerCreated = () => { if (Interlocked.Increment(ref timers) == 4) nextWaitReady.TrySetResult(); };
        var raw = new CountingAdapter(RetentionStoreKind.RawRecord);
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time), fixture.Time);

        int calls;
        long cycles;
        try
        {
            await worker.StartAsync();
            fixture.Time.Advance(TimeSpan.FromSeconds(5));
            await raw.FirstCall.Task;
            await nextWaitReady.Task;
            AddQueuedRawItem(fixture.Path, fixture.Context, fixture.Time, "second");
            Execute(fixture.Path, "CREATE TABLE wake_cycle_audit(value INTEGER NOT NULL); CREATE TRIGGER wake_cycle_observed AFTER UPDATE OF last_successful_run_at ON retention_worker_state BEGIN INSERT INTO wake_cycle_audit VALUES (1); END;");

            worker.Wake();
            worker.Wake();
            worker.Wake();
            await raw.SecondCall.Task;
            calls = Volatile.Read(ref raw.Calls);
        }
        finally
        {
            await worker.StopAsync();
            await DrainAsync();
        }

        cycles = ScalarNumber(fixture.Path, "SELECT COUNT(*) FROM wake_cycle_audit");
        Assert.True(calls == 2, $"Expected second adapter call; calls={calls}, cycles={cycles}, timers={timers}, second={Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", "second")}.");
        Assert.Equal(1L, cycles);
    }

    [Fact]
    public async Task StopCancelsFutureDueWake()
    {
        using var fixture = CreateFixture();
        Execute(fixture.Path, "UPDATE retention_items SET state='deletion_failed',attempt_count=1,next_retry_at=$due WHERE item_id=$item", ("$due", (fixture.Time.GetUtcNow() + TimeSpan.FromSeconds(5)).ToString("O")), ("$item", fixture.ItemId));
        var raw = new CountingAdapter(RetentionStoreKind.RawRecord);
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(fixture.Catalog, Registry(raw), fixture.Time), fixture.Time);

        await worker.StartAsync();
        await worker.StopAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync();

        Assert.Equal(0, Volatile.Read(ref raw.Calls));
    }

    private static Fixture CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-race-{Guid.NewGuid():N}.db");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
        var source = new RawTelemetryStore(path, context, time);
        source.CreateMonitorSchema();
        var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, time.GetUtcNow(), null, "{}"));
        var item = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source", rawId.ToString());
        SeedCoverage(path);
        Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item", ("$now", time.GetUtcNow().ToString("O")), ("$item", item));
        return new(path, context, time, new RetentionCatalogStore(context, time), item);
    }

    private static void AddQueuedRawItem(string path, RetentionCatalogContext context, MutableTimeProvider time, string item)
    {
        var source = new RawTelemetryStore(path, context, time);
        var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, time.GetUtcNow(), null, "{}"));
        Execute(path, "UPDATE retention_items SET item_id=$item,state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE store_kind='raw_record' AND source_item_id=$source", ("$item", item), ("$now", time.GetUtcNow().ToString("O")), ("$source", rawId.ToString()));
    }

    private static RetentionAdapterRegistry Registry(CountingAdapter raw) => new(new IRetentionDeletionAdapter[]
    {
        new CountingAdapter(RetentionStoreKind.SessionEventContent), raw, new CountingAdapter(RetentionStoreKind.AnalysisRunRaw), new CountingAdapter(RetentionStoreKind.SensitiveBundle), new CountingAdapter(RetentionStoreKind.AnalysisSdkDirectory)
    });
    private static void SeedCoverage(string path) => Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
    private static void InsertOperationLease(string path, string item, DateTimeOffset expiry) => Execute(path, "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,'operation','reader',$expiry,1)", ("$item", item), ("$expiry", expiry.ToString("O")));
    private static RetentionReadGrant CreateGrant(string itemId, byte tokenByte, DateTimeOffset expiry) =>
        new(
            new("store", RetentionStoreKind.RawRecord, itemId == "first" ? "1" : "2"),
            itemId,
            1,
            RetentionLeaseKind.Operation,
            "owner",
            1,
            expiry,
            Enumerable.Repeat(tokenByte, 32).ToArray());
    private static string Snapshot(string path, string item) => Text(path, "SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(read_denied_at,'') || ':' || COALESCE(queued_at,'') FROM retention_items WHERE item_id=$item", item);
    private static long Number(string path, string sql, string item) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$item", item); return Convert.ToInt64(q.ExecuteScalar()); }
    private static long ScalarNumber(string path, string sql) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
    private static string TableSnapshot(string path, string table, string columns) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = $"SELECT COALESCE(group_concat(row_text, '|'), '') FROM (SELECT {string.Join(" || ':' || ", columns.Split(',').Select(column => $"COALESCE(CAST({column} AS TEXT),'')"))} AS row_text FROM {table} ORDER BY {columns.Split(',')[0]});"; return (string)q.ExecuteScalar()!; }
    private static string Text(string path, string sql, string value) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue(sql.Contains("$source", StringComparison.Ordinal) ? "$source" : "$item", value); return (string)q.ExecuteScalar()!; }
    private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    private static async Task DrainAsync() { for (var i = 0; i < 8; i++) await Task.Yield(); }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class ManualNotificationTimeProvider(DateTimeOffset initialNow) : TimeProvider
    {
        private DateTimeOffset now = initialNow;
        internal List<ManualTimer> Timers { get; } = [];
        internal int ThrowOnCreateOrdinal { get; init; }
        internal int RejectChangeOrdinal { get; init; }
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var ordinal = Timers.Count + 1;
            if (ordinal == ThrowOnCreateOrdinal)
                throw new InvalidOperationException("synthetic_notification_construction_failure");
            var timer = new ManualTimer(callback, state)
            {
                RejectChange = ordinal == RejectChangeOrdinal,
            };
            timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        internal bool IsScheduled { get; private set; }
        internal bool IsDisposed => disposed;
        internal bool RejectChange { get; set; }
        internal Action? BeforeChange { get; set; }
        internal bool ThrowOnChange { get; set; }
        internal bool ThrowOnDispose { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            BeforeChange?.Invoke();
            if (ThrowOnChange) throw new InvalidOperationException("synthetic_notification_change_failure");
            if (RejectChange) return false;
            if (disposed) return false;
            IsScheduled = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }
        internal void Fire()
        {
            IsScheduled = false;
            callback(state);
        }
        public void Dispose()
        {
            disposed = true;
            IsScheduled = false;
            if (ThrowOnDispose) throw new InvalidOperationException("synthetic_notification_dispose_failure");
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed record Fixture(string Path, RetentionCatalogContext Context, MutableTimeProvider Time, RetentionCatalogStore Catalog, string ItemId) : IDisposable
    {
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    }
    private sealed class CountingAdapter(RetentionStoreKind kind) : IRetentionDeletionAdapter
    {
        internal int Calls;
        internal readonly TaskCompletionSource FirstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RetentionStoreKind StoreKind => kind;
        public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) { var calls = Interlocked.Increment(ref Calls); FirstCall.TrySetResult(); if (calls >= 2) SecondCall.TrySetResult(); return ValueTask.FromResult(RetentionAdapterResult.Deleted); }
    }
}

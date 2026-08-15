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
        var handle = new RetentionCommittedReadHandle(
            grants,
            time,
            frontier =>
            {
                attempts.Add(frontier.Select(grant => grant.ItemId).ToArray());
                return attempts.Count > 1;
            });

        await handle.DisposeAsync();

        Assert.Equal(["first", "second"], Assert.Single(attempts));
        time.Advance(TimeSpan.FromMilliseconds(10));
        await WaitUntilAsync(() => attempts.Count == 2);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(["first", "second"], attempt));
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
        internal bool RejectChange { get; init; }
        internal bool ThrowOnChange { get; set; }
        internal bool ThrowOnDispose { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
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

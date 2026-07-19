using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionWorkerRaceTests
{
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
    private static string Snapshot(string path, string item) => Text(path, "SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(read_denied_at,'') || ':' || COALESCE(queued_at,'') FROM retention_items WHERE item_id=$item", item);
    private static long Number(string path, string sql, string item) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$item", item); return Convert.ToInt64(q.ExecuteScalar()); }
    private static long ScalarNumber(string path, string sql) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
    private static string TableSnapshot(string path, string table, string columns) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = $"SELECT COALESCE(group_concat(row_text, '|'), '') FROM (SELECT {string.Join(" || ':' || ", columns.Split(',').Select(column => $"COALESCE(CAST({column} AS TEXT),'')"))} AS row_text FROM {table} ORDER BY {columns.Split(',')[0]});"; return (string)q.ExecuteScalar()!; }
    private static string Text(string path, string sql, string value) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue(sql.Contains("$source", StringComparison.Ordinal) ? "$source" : "$item", value); return (string)q.ExecuteScalar()!; }
    private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    private static async Task DrainAsync() { for (var i = 0; i < 8; i++) await Task.Yield(); }

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

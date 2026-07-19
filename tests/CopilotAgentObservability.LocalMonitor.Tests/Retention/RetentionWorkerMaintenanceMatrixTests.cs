using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionWorkerMaintenanceMatrixTests
{
    [Fact]
    public async Task CleanEmptyCycles_AdvanceLastSuccessMonotonicallyWithoutMaintenance()
    {
        using var fixture = NewFixture();
        await fixture.Store.RecordCleanCycleAsync(false, fixture.Now, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        await fixture.Store.RecordCleanCycleAsync(false, fixture.Time.GetUtcNow(), CancellationToken.None);
        await fixture.Store.RecordCleanCycleAsync(false, fixture.Now, CancellationToken.None);
        Assert.Equal(fixture.Now + TimeSpan.FromMinutes(2), LastSuccess(fixture.Path));
        Assert.Equal(0, fixture.Protocol.Calls);
    }

    [Fact]
    public async Task TwoSqliteCompletions_InvokeMaintenanceOnce()
    {
        using var fixture = NewFixture();
        AddQueuedRaw(fixture.Path, "one", fixture.Now); AddQueuedRaw(fixture.Path, "two", fixture.Now);
        await Coordinator(fixture).RunOneCycleAsync(CancellationToken.None, CancellationToken.None);
        Assert.Equal(1, fixture.Protocol.Calls); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_due_at FROM retention_worker_state"));
    }

    [Fact]
    public async Task EmptyFailedCancelledAndFileOnlyCycles_DoNotInvokeMaintenance()
    {
        using var empty = NewFixture(); await Coordinator(empty).RunOneCycleAsync(CancellationToken.None, CancellationToken.None); Assert.Equal(0, empty.Protocol.Calls);
        using var failed = NewFixture(RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy)); AddQueuedRaw(failed.Path, "failed", failed.Now); await Coordinator(failed).RunOneCycleAsync(CancellationToken.None, CancellationToken.None); Assert.Equal(0, failed.Protocol.Calls);
        using var cancelled = NewFixture(); AddQueuedRaw(cancelled.Path, "cancelled", cancelled.Now); using var token = new CancellationTokenSource(); token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator(cancelled).RunOneCycleAsync(token.Token, token.Token).AsTask()); Assert.Equal(0, cancelled.Protocol.Calls);
        using var file = NewFixture(); AddQueuedFile(file.Path, file.Now); await Coordinator(file).RunOneCycleAsync(CancellationToken.None, CancellationToken.None); Assert.Equal(0, file.Protocol.Calls);
    }

    [Fact]
    public async Task DueOnlyRetry_AtExactlyOneMinute_InvokesOnceWithoutNewCompletion()
    {
        using var fixture = NewFixture(protocolResult: false);
        await fixture.Store.RecordCleanCycleAsync(true, fixture.Now, CancellationToken.None);
        Assert.False(await fixture.Store.TryRunMaintenanceAsync(fixture.Now, CancellationToken.None)); Assert.Equal(1, fixture.Protocol.Calls);
        fixture.Time.Advance(TimeSpan.FromSeconds(59)); await Coordinator(fixture).RunOneCycleAsync(CancellationToken.None, CancellationToken.None); Assert.Equal(1, fixture.Protocol.Calls);
        fixture.Time.Advance(TimeSpan.FromSeconds(1)); fixture.Protocol.Result = true; await Coordinator(fixture).RunOneCycleAsync(CancellationToken.None, CancellationToken.None);
        Assert.Equal(2, fixture.Protocol.Calls); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_due_at FROM retention_worker_state"));
    }

    [Theory]
    [InlineData("access")]
    [InlineData("operation")]
    [InlineData("deletion")]
    public async Task LiveLease_PreventsMaintenanceAndPreservesItemsTombstonesAndJournal(string leaseKind)
    {
        using var fixture = NewFixture(); AddSnapshotItem(fixture.Path, fixture.Now); await fixture.Store.RecordCleanCycleAsync(true, fixture.Now, CancellationToken.None);
        Execute(fixture.Path, "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('snapshot',$kind,'live',$expires,1)", ("$kind", leaseKind), ("$expires", fixture.Now.AddMinutes(1).ToString("O")));
        var before = Snapshot(fixture.Path);
        Assert.False(await fixture.Store.TryRunMaintenanceAsync(fixture.Now, CancellationToken.None));
        Assert.Equal(0, fixture.Protocol.Calls); Assert.Equal(before, Snapshot(fixture.Path)); AssertBusyRetry(fixture, fixture.Now);
    }

    [Fact]
    public async Task SuccessClearsSingletonFieldsAndLaterBusyPreservesLastSuccess()
    {
        using var fixture = NewFixture(); await fixture.Store.RecordCleanCycleAsync(true, fixture.Now, CancellationToken.None);
        Assert.True(await fixture.Store.TryRunMaintenanceAsync(fixture.Now, CancellationToken.None));
        Assert.Null(Scalar(fixture.Path, "SELECT maintenance_due_at FROM retention_worker_state")); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_error_code FROM retention_worker_state")); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_owner FROM retention_worker_state")); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_lease_expires_at FROM retention_worker_state"));
        var success = LastSuccess(fixture.Path); fixture.Time.Advance(TimeSpan.FromMinutes(1)); Execute(fixture.Path, "UPDATE retention_worker_state SET maintenance_due_at=$due WHERE id=1", ("$due", fixture.Time.GetUtcNow().ToString("O"))); fixture.Protocol.Result = false;
        Assert.False(await fixture.Store.TryRunMaintenanceAsync(fixture.Time.GetUtcNow(), CancellationToken.None)); Assert.Equal(success, LastSuccess(fixture.Path)); AssertBusyRetry(fixture, fixture.Time.GetUtcNow());
    }

    [Fact]
    public async Task CancellationCheckpointFailureAndSqliteBusyProtocol_PersistOnlyFixedBusyRetry()
    {
        using var cancellation = NewFixture(); await cancellation.Store.RecordCleanCycleAsync(true, cancellation.Now, CancellationToken.None); using var token = new CancellationTokenSource(); token.Cancel(); Assert.False(await cancellation.Store.TryRunMaintenanceAsync(cancellation.Now, token.Token)); AssertBusyRetry(cancellation, cancellation.Now);
        using var checkpoint = NewFixture(checkpoint: static () => throw new InvalidOperationException()); await checkpoint.Store.RecordCleanCycleAsync(true, checkpoint.Now, CancellationToken.None); Assert.False(await checkpoint.Store.TryRunMaintenanceAsync(checkpoint.Now, CancellationToken.None)); AssertBusyRetry(checkpoint, checkpoint.Now);
        using var busy = NewFixture(); busy.Protocol.ThrowSqliteBusy = true; await busy.Store.RecordCleanCycleAsync(true, busy.Now, CancellationToken.None); Assert.False(await busy.Store.TryRunMaintenanceAsync(busy.Now, CancellationToken.None)); Assert.Equal(1, busy.Protocol.Calls); AssertBusyRetry(busy, busy.Now);
    }

    private static RetentionCleanupCoordinator Coordinator(Fixture fixture) => new(fixture.Store, Registry(fixture.Result), fixture.Time);
    private static RetentionAdapterRegistry Registry(RetentionAdapterResult? rawResult) => new(new IRetentionDeletionAdapter[] { new Adapter(RetentionStoreKind.SessionEventContent), new Adapter(RetentionStoreKind.RawRecord, rawResult), new Adapter(RetentionStoreKind.AnalysisRunRaw), new Adapter(RetentionStoreKind.SensitiveBundle), new Adapter(RetentionStoreKind.AnalysisSdkDirectory) });
    private static Fixture NewFixture(RetentionAdapterResult? result = null, bool protocolResult = true, Action? checkpoint = null) { var path = Path.Combine(Path.GetTempPath(), $"retention-maintenance-matrix-{Guid.NewGuid():N}.db"); var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero); var time = new MutableTimeProvider(now); var protocol = new Protocol { Result = protocolResult }; var store = new RetentionCatalogStore(path, time, checkpoint, protocol.Run); store.CreateSchema(); Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1)"); return new(path, now, time, store, protocol, result); }
    private static void AddQueuedRaw(string path, string item, DateTimeOffset now) { var source = new RawTelemetryStore(path); source.CreateMonitorSchema(); var id = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}")); Execute(path, "UPDATE retention_items SET item_id=$item,state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now,expires_at=$now WHERE store_kind='raw_record' AND source_item_id=$source", ("$item", item), ("$now", now.ToString("O")), ("$source", id.ToString())); }
    private static void AddQueuedFile(string path, DateTimeOffset now) { Execute(path, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES('file',(SELECT store_instance_id FROM retention_store_instances WHERE id=1),'sensitive_bundle','file-source',1,zeroblob(32),$now,$now,'sensitive-bundle-7d',1,'deletion_queued',1,$now,$now,1)", ("$now", now.ToString("O"))); }
    private static void AddSnapshotItem(string path, DateTimeOffset now) { Execute(path, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES('snapshot',(SELECT store_instance_id FROM retention_store_instances WHERE id=1),'raw_record','snapshot-source',1,zeroblob(32),$now,$now,'raw-default-90d',1,'deleted',2,$now,$now,1); INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('snapshot',$now,$now); INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES('snapshot',0,$now,2)", ("$now", now.ToString("O"))); }
    private static void AssertBusyRetry(Fixture fixture, DateTimeOffset now) { Assert.Equal("retention_maintenance_busy", Scalar(fixture.Path, "SELECT maintenance_error_code FROM retention_worker_state")); Assert.Equal((now + TimeSpan.FromMinutes(1)).ToString("O"), Scalar(fixture.Path, "SELECT maintenance_due_at FROM retention_worker_state")); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_owner FROM retention_worker_state")); Assert.Null(Scalar(fixture.Path, "SELECT maintenance_lease_expires_at FROM retention_worker_state")); }
    private static DateTimeOffset LastSuccess(string path) => DateTimeOffset.Parse(Scalar(path, "SELECT last_successful_run_at FROM retention_worker_state")!);
    private static string Snapshot(string path) => string.Join("|", new[] { Scalar(path, "SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(error_code,'') || ':' || COALESCE(next_retry_at,'') FROM retention_items WHERE item_id='snapshot'"), Scalar(path, "SELECT receipt_at || ':' || deleted_at FROM retention_tombstones WHERE item_id='snapshot'"), Scalar(path, "SELECT durable_cursor || ':' || expected_revision FROM retention_delete_journal WHERE item_id='snapshot'") });
    private static string? Scalar(string path, string sql) { using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar() is { } value and not DBNull ? Convert.ToString(value) : null; }
    private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
    private sealed class Adapter(RetentionStoreKind kind, RetentionAdapterResult? result = null) : IRetentionDeletionAdapter { public RetentionStoreKind StoreKind => kind; public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) => ValueTask.FromResult(result ?? RetentionAdapterResult.Deleted); }
    private sealed class Protocol { internal int Calls; internal bool Result; internal bool ThrowSqliteBusy; internal bool Run(SqliteConnection connection, CancellationToken cancellationToken) { Calls++; cancellationToken.ThrowIfCancellationRequested(); if (ThrowSqliteBusy) throw new SqliteException("sqlite busy", 5); return Result; } }
    private sealed record Fixture(string Path, DateTimeOffset Now, MutableTimeProvider Time, RetentionCatalogStore Store, Protocol Protocol, RetentionAdapterResult? Result) : IDisposable { public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); } }
}

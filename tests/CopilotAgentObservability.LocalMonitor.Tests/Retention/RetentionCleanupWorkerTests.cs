using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCleanupWorkerTests
{
    [Fact]
    public async Task PrepareCleanupBatch_ReturnsEarliestFutureEligibility()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var now=time.GetUtcNow();
        ExecuteAt(db.Path,"UPDATE retention_items SET state='deletion_failed',attempt_count=1,next_retry_at=$at WHERE item_id=$id",item,now+TimeSpan.FromMinutes(3));
        AddQueuedItem(db.Path,"leased",now);ExecuteAt(db.Path,"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('leased','operation','reader',$at,1)",null,now+TimeSpan.FromMinutes(2));
        ExecuteAt(db.Path,"UPDATE retention_worker_state SET maintenance_due_at=$at WHERE id=1",null,now+TimeSpan.FromMinutes(1));

        Assert.Equal(now+TimeSpan.FromMinutes(1),(await store.PrepareCleanupBatchAsync(now,100,100,TimeSpan.FromSeconds(30),CancellationToken.None)).NextEligibleAt);
        Execute(db.Path,"UPDATE retention_worker_state SET maintenance_due_at=NULL WHERE id=1");
        Assert.Equal(now+TimeSpan.FromMinutes(2),(await store.PrepareCleanupBatchAsync(now,100,100,TimeSpan.FromSeconds(30),CancellationToken.None)).NextEligibleAt);
        Execute(db.Path,"DELETE FROM retention_leases WHERE item_id='leased'");
        Assert.Equal(now+TimeSpan.FromMinutes(3),(await store.PrepareCleanupBatchAsync(now,100,100,TimeSpan.FromSeconds(30),CancellationToken.None)).NextEligibleAt);
    }

    [Fact]
    public async Task PrepareCleanupBatch_StopsPromotionAtElapsedBudget()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);Execute(db.Path,"UPDATE retention_items SET state='expired_pending_deletion' WHERE item_id='item'");time.TimestampAdvancePerRead=TimeSpan.FromMilliseconds(50);

        var batch=await store.PrepareCleanupBatchAsync(time.GetUtcNow(),100,100,TimeSpan.FromMilliseconds(50),CancellationToken.None);

        Assert.True(batch.HitElapsedBudget);
        Assert.Empty(batch.Work);
        Assert.Equal("expired_pending_deletion",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));
    }

    [Fact]
    public async Task Worker_DueWakeRunsExactlyAtEarliestEligibility()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var due=time.GetUtcNow()+TimeSpan.FromSeconds(5);
        ExecuteAt(db.Path,"UPDATE retention_items SET state='deletion_failed',attempt_count=1,next_retry_at=$at WHERE item_id=$id",item,due);
        var adapter=new StrictAdapter(RetentionStoreKind.RawRecord);var worker=new RetentionCleanupWorker(new RetentionCleanupCoordinator(store,Registry(adapter),time),time);
        await worker.StartAsync();await DrainAsync();Assert.Equal(0,adapter.Calls);
        time.Advance(TimeSpan.FromSeconds(4));await DrainAsync();Assert.Equal(0,adapter.Calls);
        time.Advance(TimeSpan.FromSeconds(1));await DrainAsync();Assert.Equal(1,adapter.Calls);
        await adapter.Completed.Task;await worker.StopAsync();await DrainAsync();
    }

    [Fact]
    public async Task Worker_StopCancelsFutureDueWake()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var adapter=new StrictAdapter(RetentionStoreKind.RawRecord);
        ExecuteAt(db.Path,"UPDATE retention_items SET state='deletion_failed',attempt_count=1,next_retry_at=$at WHERE item_id=$id",item,time.GetUtcNow()+TimeSpan.FromSeconds(5));
        var worker=new RetentionCleanupWorker(new RetentionCleanupCoordinator(store,Registry(adapter),time),time);
        await worker.StartAsync();await DrainAsync();await worker.StopAsync();time.Advance(TimeSpan.FromSeconds(5));await DrainAsync();
        Assert.Equal(0,adapter.Calls);
    }

    [Fact]
    public async Task TwoWorkers_ClaimAndDeleteExactlyOnce()
    {
        using var db=NewDb(); var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var adapter=new StrictAdapter(RetentionStoreKind.RawRecord,gate.Task);var (store,time,item)=Setup(db.Path);var registry=Registry(adapter);var first=new RetentionCleanupCoordinator(store,registry,time);var second=new RetentionCleanupCoordinator(new RetentionCatalogStore(db.Path,time),registry,time);
        var run=first.RunOneCycleAsync(CancellationToken.None,CancellationToken.None).AsTask(); await adapter.Entered.Task; await second.RunOneCycleAsync(CancellationToken.None,CancellationToken.None); gate.SetResult();await run;
        Assert.Equal(1,adapter.Calls);Assert.Equal("deleted",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));Assert.Equal(1L,Number(db.Path,"SELECT attempt_count FROM retention_items WHERE item_id=$id",item));Assert.Equal(1L,Number(db.Path,"SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id",item));
    }

    [Fact]
    public async Task CancellationBeforeIntent_RequeuesWithoutAttempt()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);using var drain=new CancellationTokenSource();drain.Cancel();await new RetentionCleanupCoordinator(store,Registry(new StrictAdapter(RetentionStoreKind.RawRecord)),time).RunOneCycleAsync(CancellationToken.None,drain.Token);
        Assert.Equal("deletion_queued",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));Assert.Equal(0L,Number(db.Path,"SELECT attempt_count FROM retention_items WHERE item_id=$id",item));Assert.Equal(0L,Number(db.Path,"SELECT COUNT(*) FROM retention_leases WHERE item_id=$id",item));Assert.Equal(0L,Number(db.Path,"SELECT COUNT(*) FROM retention_delete_journal WHERE item_id=$id",item));
    }

    [Fact]
    public async Task LossAfterIntent_RecoversForwardOnly()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var prepared=await store.PrepareCleanupBatchAsync(time.GetUtcNow(),100,100,TimeSpan.FromSeconds(30),CancellationToken.None);var claim=(await store.TryClaimDeletionAsync(prepared.Work.Single(),"owner",time.GetUtcNow(),CancellationToken.None)).Claim!;await store.EnsureDeleteIntentAsync(claim.Fence,0,time.GetUtcNow(),CancellationToken.None);var revision=claim.Fence.ExpectedRevision;time.Advance(TimeSpan.FromMinutes(2));SqliteConnection.ClearAllPools();var reopened=new RetentionCatalogStore(db.Path,time);var recovery=(await reopened.TryClaimDeletionAsync(new(item,revision,RetentionWorkKind.IntentRecovery),"recovery",time.GetUtcNow(),CancellationToken.None)).Claim!;
        Assert.True(recovery.Fence.LeaseGeneration>claim.Fence.LeaseGeneration);Assert.Equal(revision,recovery.Fence.ExpectedRevision);Assert.True(recovery.HasCurrentIntent);Assert.Equal(1L,Number(db.Path,"SELECT attempt_count FROM retention_items WHERE item_id=$id",item));Assert.Equal("deleting",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));
    }

    [Fact]
    public async Task RetrySchedule_FifthFailureIsTerminal()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var adapter=new StrictAdapter(RetentionStoreKind.RawRecord,result:RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy));var worker=new RetentionCleanupCoordinator(store,Registry(adapter),time);var delays=RetentionV1Constants.RetryDelays;
        for(var attempt=1;attempt<=5;attempt++){await worker.RunOneCycleAsync(CancellationToken.None,CancellationToken.None);Assert.Equal(attempt,Number(db.Path,"SELECT attempt_count FROM retention_items WHERE item_id=$id",item));if(attempt<5){Assert.Equal(time.GetUtcNow()+delays[attempt-1],Date(db.Path,"SELECT next_retry_at FROM retention_items WHERE item_id=$id",item));time.Advance(delays[attempt-1]);}}
        Assert.Equal(1L,Number(db.Path,"SELECT retry_exhausted FROM retention_items WHERE item_id=$id",item));Assert.Equal(1L,Number(db.Path,"SELECT next_retry_at IS NULL FROM retention_items WHERE item_id=$id",item));var calls=adapter.Calls;time.Advance(TimeSpan.FromDays(1));await worker.RunOneCycleAsync(CancellationToken.None,CancellationToken.None);Assert.Equal(calls,adapter.Calls);
    }

    [Fact]
    public async Task StopAsync_DrainsWithinPinnedBound()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var adapter=new StrictAdapter(RetentionStoreKind.RawRecord,gate.Task,ignoreCancellation:true);var coordinator=new RetentionCleanupCoordinator(store,Registry(adapter),time);var worker=new RetentionCleanupWorker(coordinator,time);await worker.StartAsync();await adapter.Entered.Task;var stopping=worker.StopAsync();Assert.False(stopping.IsCompleted);
        try { time.Advance(RetentionV1Constants.LeaseRenewalDeadline);await Task.Yield();time.Advance(RetentionV1Constants.LeaseRenewalDeadline);await stopping;
            Assert.Equal(time.GetUtcNow(),Date(db.Path,"SELECT expires_at FROM retention_leases WHERE item_id=$id",item));Assert.Equal("deleting",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));Assert.Equal(1,adapter.Calls); }
        finally { gate.SetResult();time.Advance(RetentionV1Constants.LeaseDuration);await adapter.Completed.Task;await worker.StopAsync(); }
        Assert.Equal("deleting",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));
    }

    [Fact]
    public async Task SuccessfulBatch_CheckpointsWalOnce()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);AddQueuedItem(db.Path,"second",time.GetUtcNow());await new RetentionCleanupCoordinator(store,Registry(new StrictAdapter(RetentionStoreKind.RawRecord)),time).RunOneCycleAsync(CancellationToken.None,CancellationToken.None);
        Assert.Equal(1L,Number(db.Path,"SELECT maintenance_due_at IS NULL FROM retention_worker_state WHERE id=1",item));Assert.Equal(1L,Number(db.Path,"SELECT maintenance_generation FROM retention_worker_state WHERE id=1",item));Assert.Equal(1L,Number(db.Path,"SELECT maintenance_owner IS NULL AND maintenance_lease_expires_at IS NULL FROM retention_worker_state WHERE id=1",item));Assert.NotNull(Scalar(db.Path,"SELECT last_successful_run_at FROM retention_worker_state WHERE id=1"));Assert.Equal("deleted",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id",item));Assert.Equal("deleted",Text(db.Path,"SELECT state FROM retention_items WHERE item_id=$id","second"));
    }

    [Fact]
    public async Task DueMaintenance_IsRetriedWithoutANewSqliteCompletion()
    {
        using var db=NewDb();var (store,time,item)=Setup(db.Path);var coordinator=new RetentionCleanupCoordinator(store,Registry(new StrictAdapter(RetentionStoreKind.RawRecord)),time);
        await store.RecordCleanCycleAsync(true,time.GetUtcNow(),CancellationToken.None);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.False(await store.TryRunMaintenanceAsync(time.GetUtcNow(),cancelled.Token));
        time.Advance(RetentionV1Constants.WalMaintenanceRetryDelay);

        await coordinator.RunOneCycleAsync(CancellationToken.None,CancellationToken.None);

        Assert.Equal(1L,Number(db.Path,"SELECT maintenance_due_at IS NULL FROM retention_worker_state WHERE id=1",item));
        Assert.Equal(1L,Number(db.Path,"SELECT maintenance_error_code IS NULL FROM retention_worker_state WHERE id=1",item));
    }

    private static (RetentionCatalogStore Store,MutableTimeProvider Time,string Item) Setup(string path){var time=new MutableTimeProvider(new DateTimeOffset(2026,7,19,0,0,0,TimeSpan.Zero));var store=new RetentionCatalogStore(path,time);store.CreateSchema();SeedExactAdapterCoverage(path);AddQueuedItem(path,"item",time.GetUtcNow());return(store,time,"item");}
    private static void SeedExactAdapterCoverage(string path){using var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";q.ExecuteNonQuery();}
    private static void AddQueuedItem(string path,string item,DateTimeOffset now){var source=new RawTelemetryStore(path);source.CreateMonitorSchema();var rawId=source.Insert(new RawTelemetryRecord(null,RawTelemetrySources.RawOtlp,null,now,null,"{}"));using var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="UPDATE retention_items SET item_id=$item,state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now,expires_at=$now WHERE store_kind='raw_record' AND source_item_id=$source;";q.Parameters.AddWithValue("$item",item);q.Parameters.AddWithValue("$now",now.ToString("O"));q.Parameters.AddWithValue("$source",rawId.ToString());q.ExecuteNonQuery();}
    private static RetentionAdapterRegistry Registry(StrictAdapter raw)=>new(new IRetentionDeletionAdapter[]{new StrictAdapter(RetentionStoreKind.SessionEventContent),raw,new StrictAdapter(RetentionStoreKind.AnalysisRunRaw),new StrictAdapter(RetentionStoreKind.SensitiveBundle),new StrictAdapter(RetentionStoreKind.AnalysisSdkDirectory)});
    private static string Text(string p,string sql,string id){return (string)Scalar(p,sql,id)!;}private static long Number(string p,string sql,string id)=>Convert.ToInt64(Scalar(p,sql,id));private static DateTimeOffset Date(string p,string sql,string id)=>DateTimeOffset.Parse((string)Scalar(p,sql,id)!);private static object? Scalar(string p,string sql,string? id=null){using var c=new SqliteConnection($"Data Source={p};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText=sql;if(id is not null)q.Parameters.AddWithValue("$id",id);return q.ExecuteScalar();}private static void Exec(string p,string sql,string id,string now){using var c=new SqliteConnection($"Data Source={p};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText=sql;q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();}private static void Execute(string p,string sql){using var c=new SqliteConnection($"Data Source={p};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery();}private static void ExecuteAt(string p,string sql,string? id,DateTimeOffset at){using var c=new SqliteConnection($"Data Source={p};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText=sql;if(id is not null)q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$at",at.ToString("O"));q.ExecuteNonQuery();}private static async Task DrainAsync(){for(var i=0;i<8;i++)await Task.Yield();}
    private static TempDb NewDb()=>new(Path.Combine(Path.GetTempPath(),$"retention-worker-{Guid.NewGuid():N}.db"));private sealed class TempDb(string path):IDisposable{internal string Path=>path;public void Dispose(){SqliteConnection.ClearAllPools();foreach(var f in new[]{path,path+"-wal",path+"-shm"})if(File.Exists(f))File.Delete(f);}}
    private sealed class StrictAdapter(RetentionStoreKind kind,Task? gate=null,RetentionAdapterResult? result=null,bool ignoreCancellation=false):IRetentionDeletionAdapter{internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);internal readonly TaskCompletionSource Completed=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Calls;public RetentionStoreKind StoreKind=>kind;public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context){Interlocked.Increment(ref Calls);Entered.TrySetResult();if(gate is not null){if(ignoreCancellation)await gate;else await gate.WaitAsync(context.CancellationToken);}Completed.TrySetResult();return result??RetentionAdapterResult.Deleted;}}
}

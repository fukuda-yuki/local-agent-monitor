namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionLifecycleIntegrationTests
{
    [Fact]
    public async Task CancelledMaintenance_RecordsOnlyFixedRetryWithoutLifecycleMutation()
    {
        var path=Path.Combine(Path.GetTempPath(),$"retention-maintenance-{Guid.NewGuid():N}.db");
        try
        {
            var now=new DateTimeOffset(2026,7,19,0,0,0,TimeSpan.Zero);var time=new MutableTimeProvider(now);var store=new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(path,time);store.CreateSchema();
            using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")){c.Open();using var q=c.CreateCommand();q.CommandText="INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES('item',(SELECT store_instance_id FROM retention_store_instances WHERE id=1),'raw_record','source',1,zeroblob(32),$now,$now,'raw-default-90d',1,'deleted',2,$now,$now,1); INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('item',$now,$now);";q.Parameters.AddWithValue("$now",now.ToString("O"));q.ExecuteNonQuery();}
            await store.RecordCleanCycleAsync(true,now,CancellationToken.None);var before=Snapshot(path);using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Assert.False(await store.TryRunMaintenanceAsync(now,cancelled.Token));
            Assert.Equal(before,Snapshot(path));
            using var inspect=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");inspect.Open();Assert.Equal("retention_maintenance_busy",Text(inspect,"SELECT maintenance_error_code FROM retention_worker_state WHERE id=1"));Assert.Equal(now+TimeSpan.FromMinutes(1),DateTimeOffset.Parse(Text(inspect,"SELECT maintenance_due_at FROM retention_worker_state WHERE id=1")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var file in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(file))File.Delete(file); }
    }

    [Fact]
    public void Migration_AddsWorkerFieldsAndKeepsV1AcrossReopens()
    {
        var path=Path.Combine(Path.GetTempPath(),$"retention-migration-{Guid.NewGuid():N}.db");
        try
        {
            var store=new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(path);store.CreateSchema();store.CreateSchema();
            using var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");c.Open();
            Assert.Equal(1L,Scalar(c,"SELECT version FROM retention_component_versions WHERE component='retention'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM pragma_table_info('retention_items') WHERE name='deletion_started_at'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM retention_worker_state WHERE id=1"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_items_worker_order'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_leases_kind_expiry'"));
        }
        finally { foreach(var file in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(file))File.Delete(file); }
    }
    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());}
    private static string Text(Microsoft.Data.Sqlite.SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return (string)q.ExecuteScalar()!;}
    private static string Snapshot(string path){using var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(error_code,'') || ':' || COALESCE(next_retry_at,'') FROM retention_items WHERE item_id='item';";return (string)q.ExecuteScalar()!;}
}

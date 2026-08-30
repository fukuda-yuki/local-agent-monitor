using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalAiComponentTests
{
    [Theory]
    [InlineData("UPDATE schema_version SET version=2 WHERE component='local_ai_analysis';")]
    [InlineData("DROP TABLE local_ai_results;")]
    [InlineData("CREATE TABLE local_ai_unknown(value TEXT);")]
    public void Preflight_RejectsNewerPartialOrInvalidLocalAiNamespace(string mutation)
    {
        var root=Path.Combine(Path.GetTempPath(),$"runtime-backup-local-ai-invalid-{Guid.NewGuid():N}");Directory.CreateDirectory(root);
        try{var source=Path.Combine(root,"source.db");using(var connection=Open(source)){using var transaction=connection.BeginTransaction();MonitorSchemaMigrator.ApplyBaseSchema(connection,transaction);transaction.Commit();LocalAiAnalysisSchemaV1.Ensure(connection);using var command=connection.CreateCommand();command.CommandText=mutation;command.ExecuteNonQuery();}var result=new SqliteRuntimeBackupService().PreflightForMigration(source);Assert.False(result.Success);Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible,result.ErrorCode);}
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Fact]
    public void Backup_ExcludesNodeRowsFromStagingWithoutMutatingSourceAndRestoresSessionRows()
    {
        var root=Path.Combine(Path.GetTempPath(),$"runtime-backup-local-ai-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(root,"source.db"); using(var connection=Open(source)){using var transaction=connection.BeginTransaction();MonitorSchemaMigrator.ApplyBaseSchema(connection,transaction);transaction.Commit();}
            var time=new MutableTimeProvider(new DateTimeOffset(2026,8,30,1,0,0,TimeSpan.Zero));var context=RetentionCatalogContext.InitializeNewOwnedDatabase(source,time);var catalog=new RetentionCatalogStore(context,time);using(var connection=Open(source))LocalAiAnalysisSchemaV1.Ensure(connection);var store=new LocalAiAnalysisStoreV1(source,catalog,time);store.InsertSnapshot(new(SessionSnapshot,"session",SessionId,null,SessionId,"{}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray()));store.InsertSnapshot(new(NodeSnapshot,"node",SessionId,"node-1","node-1","{}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray()));
            var archive=Path.Combine(root,"backup.zip"); var service=new SqliteRuntimeBackupService(); var created=service.CreateAndPublish(source,archive); Assert.True(created.Success,created.ErrorCode);
            using(var unchanged=Open(source)) Assert.Equal(2L,Scalar(unchanged,"SELECT COUNT(*) FROM local_ai_snapshots;"));
            var restored=Path.Combine(root,"restored.db"); var result=service.Restore(archive,restored,new RuntimeRestoreOptions()); Assert.True(result.Success,result.ErrorCode);
            using var read=Open(restored); Assert.Equal(1L,Scalar(read,"SELECT version FROM schema_version WHERE component='local_ai_analysis';")); Assert.Equal(1L,Scalar(read,"SELECT COUNT(*) FROM local_ai_snapshots WHERE scope_kind='session';")); Assert.Equal(0L,Scalar(read,"SELECT COUNT(*) FROM local_ai_snapshots WHERE scope_kind='node';"));
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData(false,"succeeded")]
    [InlineData(true,"zero_findings")]
    public void Backup_RoundTripsRetainedSessionReportsWithExactFindingCardinality(bool zero,string expectedState)
    {
        var root=Path.Combine(Path.GetTempPath(),$"runtime-backup-local-ai-report-{Guid.NewGuid():N}");Directory.CreateDirectory(root);
        try{var source=Path.Combine(root,"source.db");var time=Time();var catalog=Initialize(source,time);var store=new LocalAiAnalysisStoreV1(source,catalog,time);var run=Complete(store,zero);using(var check=Open(source)){using var transaction=check.BeginTransaction();Assert.True(SqliteRuntimeBackupService.ValidateLocalAiRows(check,transaction,time.GetUtcNow()));transaction.Commit();}var archive=Path.Combine(root,"backup.zip");var service=new SqliteRuntimeBackupService(time);var created=service.CreateAndPublish(source,archive);Assert.True(created.Success,created.ErrorCode);var restored=Path.Combine(root,"restored.db");var restore=service.Restore(archive,restored,new RuntimeRestoreOptions());Assert.True(restore.Success,restore.ErrorCode);var restoredCatalog=new RetentionCatalogStore(RetentionCatalogContext.InitializeNewOwnedDatabase(restored,time),time);var report=Assert.Single(new LocalAiAnalysisStoreV1(restored,restoredCatalog,time).GetSessionReports(SessionId,null,null).Items);Assert.Equal(expectedState,report.State==LocalAiRunStateV1.Succeeded?"succeeded":"zero_findings");Assert.NotNull(report.CanonicalResult);Assert.Equal(run.RunId,report.RunId);}
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Backup_ScrubsDeniedSessionBytesOnlyInStagingAndRestoresExpiredMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-local-ai-denied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.db");
            var time = Time();
            var catalog = Initialize(source, time);
            var store = new LocalAiAnalysisStoreV1(source, catalog, time);
            Complete(store, false);
            using (var coverage = Open(source))
            {
                using var command = coverage.CreateCommand();
                command.CommandText = "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";
                command.ExecuteNonQuery();
            }
            using (var expire = Open(source))
            {
                using var command = expire.CreateCommand();
                command.CommandText = "UPDATE retention_items SET expires_at=$expired WHERE source_item_id LIKE 'local_ai:%';";
                command.Parameters.AddWithValue("$expired", time.GetUtcNow().AddSeconds(3).ToString("O"));
                Assert.Equal(2, command.ExecuteNonQuery());
            }
            time.Advance(TimeSpan.FromSeconds(4));
            var prepared = await catalog.PrepareCleanupBatchAsync(time.GetUtcNow(), 10, 0, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.False(prepared.CoverageBlocked);
            using (var denied = Open(source))
                Assert.Equal(2L, Scalar(denied, "SELECT COUNT(*) FROM retention_items WHERE source_item_id LIKE 'local_ai:%' AND state='deletion_queued' AND read_denied_at IS NOT NULL;"));
            using (var preflight = Open(source))
            {
                using var transaction = preflight.BeginTransaction();
                Assert.True(SqliteRuntimeBackupService.ValidateLocalAiRows(preflight, transaction, time.GetUtcNow(), allowDeniedContent: true));
                RetentionCatalogStore.ValidateCurrentV1Authority(preflight, transaction);
            }
            var archive = Path.Combine(root, "backup.zip");
            var service = new SqliteRuntimeBackupService(time);
            var created = service.CreateAndPublish(source, archive);
            Assert.True(created.Success, $"create: {created.ErrorCode}");
            using (var unchanged = Open(source))
                Assert.Equal(2L, Scalar(unchanged, "SELECT (SELECT COUNT(*) FROM local_ai_snapshots WHERE payload_json IS NOT NULL)+(SELECT COUNT(*) FROM local_ai_results WHERE result_json IS NOT NULL);"));
            var restored = Path.Combine(root, "restored.db");
            var restore = service.Restore(archive, restored, new RuntimeRestoreOptions());
            Assert.True(restore.Success, $"restore: {restore.ErrorCode}");
            using (var read = Open(restored))
                Assert.Equal(0L, Scalar(read, "SELECT (SELECT COUNT(*) FROM local_ai_snapshots WHERE payload_json IS NOT NULL)+(SELECT COUNT(*) FROM local_ai_results WHERE result_json IS NOT NULL);"));
            var restoredCatalog = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(restored), time);
            var report = Assert.Single(new LocalAiAnalysisStoreV1(restored, restoredCatalog, time).GetSessionReports(SessionId, null, null).Items);
            Assert.Equal("expired", report.ContentState);
            Assert.Null(report.CanonicalResult);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("snapshot", "retained")]
    [InlineData("result", "expired")]
    public async Task Backup_PreservesIndependentSnapshotAndResultRetentionLifecycles(string deniedKind, string expectedContentState)
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-local-ai-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.db");
            var time = Time();
            var catalog = Initialize(source, time);
            var store = new LocalAiAnalysisStoreV1(source, catalog, time);
            Complete(store, false);
            using (var setup = Open(source))
            {
                using var coverage = setup.CreateCommand();
                coverage.CommandText = "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";
                coverage.ExecuteNonQuery();
                using var expire = setup.CreateCommand();
                expire.CommandText = "UPDATE retention_items SET expires_at=$expired WHERE source_item_id LIKE $source;";
                expire.Parameters.AddWithValue("$expired", time.GetUtcNow().AddSeconds(3).ToString("O"));
                expire.Parameters.AddWithValue("$source", $"local_ai:{deniedKind}:%");
                Assert.Equal(1, expire.ExecuteNonQuery());
            }
            time.Advance(TimeSpan.FromSeconds(4));
            var prepared = await catalog.PrepareCleanupBatchAsync(time.GetUtcNow(), 10, 0, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.False(prepared.CoverageBlocked);
            var archive = Path.Combine(root, "backup.zip");
            var service = new SqliteRuntimeBackupService(time);
            Assert.True(service.CreateAndPublish(source, archive).Success);
            using (var unchanged = Open(source))
                Assert.Equal(2L, Scalar(unchanged, "SELECT (SELECT COUNT(*) FROM local_ai_snapshots WHERE payload_json IS NOT NULL)+(SELECT COUNT(*) FROM local_ai_results WHERE result_json IS NOT NULL);"));
            var restored = Path.Combine(root, "restored.db");
            Assert.True(service.Restore(archive, restored, new RuntimeRestoreOptions()).Success);
            using (var read = Open(restored))
            {
                Assert.Equal(deniedKind == "snapshot" ? 0L : 1L, Scalar(read, "SELECT COUNT(*) FROM local_ai_snapshots WHERE payload_json IS NOT NULL;"));
                Assert.Equal(deniedKind == "result" ? 0L : 1L, Scalar(read, "SELECT COUNT(*) FROM local_ai_results WHERE result_json IS NOT NULL;"));
                Assert.Equal(64L, Scalar(read, "SELECT length(payload_sha256) FROM local_ai_snapshots;"));
                Assert.Equal(64L, Scalar(read, "SELECT length(evidence_index_sha256) FROM local_ai_snapshots;"));
                Assert.Equal(64L, Scalar(read, "SELECT length(result_sha256) FROM local_ai_results;"));
            }
            var restoredCatalog = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(restored), time);
            var report = Assert.Single(new LocalAiAnalysisStoreV1(restored, restoredCatalog, time).GetSessionReports(SessionId, null, null).Items);
            Assert.Equal(expectedContentState, report.ContentState);
            Assert.Equal(expectedContentState == "retained", report.CanonicalResult is not null);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static MutableTimeProvider Time()=>new(new DateTimeOffset(2026,8,30,1,0,0,TimeSpan.Zero));
    private static RetentionCatalogStore Initialize(string path,MutableTimeProvider time){using(var connection=Open(path)){using var transaction=connection.BeginTransaction();MonitorSchemaMigrator.ApplyBaseSchema(connection,transaction);transaction.Commit();}var context=RetentionCatalogContext.InitializeNewOwnedDatabase(path,time);using(var connection=Open(path))LocalAiAnalysisSchemaV1.Ensure(connection);return new(context,time);}
    private static LocalAiRunV1 Complete(LocalAiAnalysisStoreV1 store,bool zero){store.InsertSnapshot(new(SessionSnapshot,"session",SessionId,null,SessionId,"{}"u8.ToArray(),"{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray()));var request=new LocalAiRunRequestV1(SessionSnapshot,"session",SessionId,null,"github_copilot_sdk","model",new string('a',64),"template",DateTimeOffset.Parse("2026-08-30T01:00:00Z"),60);var run=store.CreateRun(request);store.TransitionRun(run.RunId,LocalAiRunStateV1.Running,null,DateTimeOffset.Parse("2026-08-30T01:00:01Z"));Assert.Equal(zero?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded,store.Complete(run.RunId,Result(zero),DateTimeOffset.Parse("2026-08-30T01:00:02Z")));return run;}
    private static byte[] Result(bool zero){var hash=Convert.ToHexStringLower(SHA256.HashData("{}"u8));var findings=zero?"[]":"[{\"evidence_refs\":[\"ev-1\"],\"evidence_state\":\"supported\",\"explanation\":\"e\",\"finding_id\":\"f\",\"limitation\":\"none\",\"title\":\"t\"}]";return Encoding.UTF8.GetBytes("{\"findings\":"+findings+",\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":{\"completed_at\":\"2026-08-30T01:00:02.0000000+00:00\",\"configuration_sha256\":\""+new string('a',64)+"\",\"coverage\":{\"content_available\":true,\"excluded\":0,\"included\":1},\"model\":\"model\",\"prompt_template_version\":\"template\",\"provider\":\"github_copilot_sdk\",\"requested_at\":\"2026-08-30T01:00:00.0000000+00:00\",\"snapshot_id\":\""+SessionSnapshot+"\",\"snapshot_sha256\":\""+hash+"\",\"started_at\":\"2026-08-30T01:00:01.0000000+00:00\"},\"scope\":{\"anchor_id\":\""+SessionId+"\",\"kind\":\"session\",\"node_id\":null,\"session_id\":\""+SessionId+"\"},\"snapshot\":{\"payload_sha256\":\""+hash+"\",\"snapshot_id\":\""+SessionSnapshot+"\"},\"summary\":\"s\"}");}

    private static SqliteConnection Open(string path){var connection=new SqliteConnection($"Data Source={path};Pooling=False");connection.Open();return connection;}
    private static long Scalar(SqliteConnection connection,string sql){using var command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
    private const string SessionSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54420",NodeSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54421",SessionId="0198f5c0-1b89-7d41-8c2f-4ecba0b54411";
}

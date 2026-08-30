using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalAiComponentTests
{
    [Fact]
    public void Backup_AcceptsWriterMaximumSnapshotDocuments()
    {
        var root=Path.Combine(Path.GetTempPath(),$"runtime-backup-local-ai-max-{Guid.NewGuid():N}");Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(root,"source.db");var time=Time();var catalog=Initialize(source,time);var store=new LocalAiAnalysisStoreV1(source,catalog,time);
            var payload=Encoding.UTF8.GetBytes("{\"value\":\""+new string('x',1_048_564)+"\"}");var evidence=Encoding.UTF8.GetBytes("{\"evidence_refs\":[\""+new string('x',1_048_554)+"\"]}");
            store.InsertSnapshot(new(SessionSnapshot,"session",SessionId,null,SessionId,payload,evidence));
            var archive=Path.Combine(root,"backup.zip");Assert.True(new SqliteRuntimeBackupService(time).CreateAndPublish(source,archive).Success);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Fact]
    public async Task BackupAndRestore_RejectExpiredSessionSnapshotWithForgedAnchor()
    {
        var root=Path.Combine(Path.GetTempPath(),$"runtime-backup-local-ai-expired-anchor-{Guid.NewGuid():N}");Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(root,"source.db");var sourceTime=Time();var catalog=Initialize(source,sourceTime);var store=new LocalAiAnalysisStoreV1(source,catalog,sourceTime);
            Complete(store,false);
            await ExpireSnapshotBytes(source,catalog,sourceTime,true);
            using(var forged=Open(source)){Execute(forged,"DROP TRIGGER local_ai_snapshots_update_rejected; UPDATE local_ai_snapshots SET anchor_id='forged';");using var transaction=forged.BeginTransaction();Assert.False(SqliteRuntimeBackupService.ValidateLocalAiRows(forged,transaction,sourceTime.GetUtcNow(),allowDeniedContent:true));transaction.Commit();}
            SqliteConnection.ClearAllPools();File.Delete(source);

            var valid=Path.Combine(root,"valid.db");var validTime=Time();var validCatalog=Initialize(valid,validTime);Complete(new LocalAiAnalysisStoreV1(valid,validCatalog,validTime),false);
            var archive=Path.Combine(root,"backup.zip");var service=new SqliteRuntimeBackupService(validTime);var created=service.CreateAndPublish(valid,archive);Assert.True(created.Success,created.ErrorCode);
            var unchanged=Path.Combine(root,"unchanged.zip");RewriteArchiveDatabase(archive,unchanged,_=>{});
            var unchangedRestore=service.Restore(unchanged,Path.Combine(root,"unchanged-restored.db"),new RuntimeRestoreOptions());Assert.True(unchangedRestore.Success,unchangedRestore.ErrorCode);
            var corrupt=Path.Combine(root,"corrupt.zip");RewriteArchiveDatabase(archive,corrupt,path=>{using var connection=Open(path);connection.CreateFunction<string,string,long>("local_ai_retention_delete_authorized",static(_,_)=>1L);using var command=connection.CreateCommand();command.CommandText="UPDATE retention_items SET state='deletion_queued',revision=revision+1,read_denied_at=$now,queued_at=$now WHERE source_item_id LIKE 'local_ai:snapshot:%'; UPDATE local_ai_snapshots SET payload_json=NULL,evidence_index_json=NULL; DROP TRIGGER local_ai_snapshots_update_rejected; UPDATE local_ai_snapshots SET anchor_id='forged'; CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots WHEN NOT (local_ai_retention_delete_authorized('snapshot',OLD.snapshot_id)=1 AND OLD.scope_kind='session' AND OLD.payload_json IS NOT NULL AND OLD.evidence_index_json IS NOT NULL AND NEW.payload_json IS NULL AND NEW.evidence_index_json IS NULL AND NEW.snapshot_id=OLD.snapshot_id AND NEW.scope_kind=OLD.scope_kind AND NEW.session_id=OLD.session_id AND NEW.node_id IS OLD.node_id AND NEW.anchor_id=OLD.anchor_id AND NEW.payload_sha256=OLD.payload_sha256 AND NEW.evidence_index_sha256=OLD.evidence_index_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at) BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END;";command.Parameters.AddWithValue("$now",validTime.GetUtcNow().ToString("O"));command.ExecuteNonQuery();using var transaction=connection.BeginTransaction();Assert.False(SqliteRuntimeBackupService.ValidateLocalAiRows(connection,transaction,validTime.GetUtcNow(),allowDeniedContent:true));transaction.Commit();});
            var restore=service.Restore(corrupt,Path.Combine(root,"restored.db"),new RuntimeRestoreOptions());Assert.False(restore.Success);Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible,restore.ErrorCode);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
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
    public void Restore_CreatesSafetyBackupWithLocalAiValidationAtItsSampledStagingTime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-local-ai-safety-time-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var time = Time();
            var wallTime = DateTimeOffset.UtcNow;
            var mutableTime = time.GetUtcNow();
            var boundaryBase = wallTime > mutableTime ? wallTime : mutableTime;
            var expiryTime = boundaryBase.AddDays(1);
            var validationTime = boundaryBase.AddDays(2);
            var source = Path.Combine(root, "source.db");
            var sourceCatalog = Initialize(source, time);
            new LocalAiAnalysisStoreV1(source, sourceCatalog, time).InsertSnapshot(
                new(SessionSnapshot, "session", SessionId, null, SessionId, "{}"u8.ToArray(), "{\"evidence_refs\":[]}"u8.ToArray()));
            var archive = Path.Combine(root, "backup.zip");
            var service = new SqliteRuntimeBackupService(time);
            Assert.True(service.CreateAndPublish(source, archive).Success);

            var target = Path.Combine(root, "target.db");
            Assert.True(service.Restore(archive, target, new RuntimeRestoreOptions()).Success);
            using (var connection = Open(target))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE retention_items SET state='expiring',expires_at=$expiry WHERE source_item_id LIKE 'local_ai:%';";
                command.Parameters.AddWithValue("$expiry", expiryTime.ToString("O"));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            time.Advance(validationTime - mutableTime);

            var safetyBackup = Path.Combine(root, "safety.zip");
            var restored = service.Restore(
                archive,
                target,
                new RuntimeRestoreOptions(PreRestoreOutputPath: safetyBackup));

            Assert.True(File.Exists(safetyBackup), restored.ErrorCode);
            Assert.True(restored.PreRestoreBackupCreated);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
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
    private static async Task ExpireSnapshotBytes(string path,RetentionCatalogStore catalog,MutableTimeProvider time,bool scrubSource){using(var connection=Open(path)){Execute(connection,"INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");using var expire=connection.CreateCommand();expire.CommandText="UPDATE retention_items SET expires_at=$expired WHERE source_item_id LIKE 'local_ai:snapshot:%';";expire.Parameters.AddWithValue("$expired",time.GetUtcNow().AddSeconds(3).ToString("O"));expire.ExecuteNonQuery();}time.Advance(TimeSpan.FromSeconds(4));var prepared=await catalog.PrepareCleanupBatchAsync(time.GetUtcNow(),10,0,TimeSpan.FromSeconds(1),CancellationToken.None);Assert.False(prepared.CoverageBlocked);if(scrubSource){using var scrub=Open(path);scrub.CreateFunction<string,string,long>("local_ai_retention_delete_authorized",static(_,_)=>1L);Execute(scrub,"UPDATE local_ai_snapshots SET payload_json=NULL,evidence_index_json=NULL;");}}
    private static void RewriteArchiveDatabase(string source,string output,Action<string> mutate){byte[] manifest,database;using(var archive=ZipFile.OpenRead(source)){manifest=Read(archive.GetEntry("manifest.json")!);database=Read(archive.GetEntry("database.sqlite")!);}var path=Path.Combine(Path.GetDirectoryName(output)!,Guid.NewGuid().ToString("N")+".sqlite");File.WriteAllBytes(path,database);mutate(path);database=File.ReadAllBytes(path);File.Delete(path);var parsed=RuntimeBackupJson.ParseManifest(manifest);manifest=RuntimeBackupJson.WriteManifest(parsed with{DatabaseSha256=Convert.ToHexStringLower(SHA256.HashData(database)),DatabaseSize=database.LongLength});using var target=ZipFile.Open(output,ZipArchiveMode.Create);Write(target,"manifest.json",manifest);Write(target,"database.sqlite",database);}
    private static byte[] Read(ZipArchiveEntry entry){using var input=entry.Open();using var output=new MemoryStream();input.CopyTo(output);return output.ToArray();}
    private static void Write(ZipArchive archive,string name,byte[] bytes){var entry=archive.CreateEntry(name,CompressionLevel.NoCompression);entry.LastWriteTime=new DateTimeOffset(1980,1,1,0,0,0,TimeSpan.Zero);entry.ExternalAttributes=0;using var output=entry.Open();output.Write(bytes);}
    private static void Execute(SqliteConnection connection,string sql){using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static long Scalar(SqliteConnection connection,string sql){using var command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
    private const string SessionSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54420",NodeSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54421",SessionId="0198f5c0-1b89-7d41-8c2f-4ecba0b54411";
}

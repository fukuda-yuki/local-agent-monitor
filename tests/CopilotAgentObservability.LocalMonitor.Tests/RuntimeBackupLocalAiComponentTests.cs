using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

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

    private static SqliteConnection Open(string path){var connection=new SqliteConnection($"Data Source={path};Pooling=False");connection.Open();return connection;}
    private static long Scalar(SqliteConnection connection,string sql){using var command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
    private const string SessionSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54420",NodeSnapshot="0198f5c0-1b89-7d41-8c2f-4ecba0b54421",SessionId="0198f5c0-1b89-7d41-8c2f-4ecba0b54411";
}

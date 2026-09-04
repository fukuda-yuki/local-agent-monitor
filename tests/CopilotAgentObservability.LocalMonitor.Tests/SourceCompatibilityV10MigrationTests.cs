using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityV10MigrationTests
{
    private const string TraceId = "11111111111111111111111111111111";

    [Theory]
    [InlineData("null")]
    [InlineData("blob")]
    public void DirectMigration_PresentRawProjectionInputWithNonTextPayloadFailsBeforeMutation(
        string storageClass)
    {
        using var database = new TestDatabase();
        CreateV10SourceFixture(database.Path);
        if (storageClass == "blob")
        {
            using var connection = Open(database.Path);
            Execute(connection, "UPDATE raw_records SET payload_json=zeroblob(1) WHERE id=1;");
        }
        else
        {
            SetRawPayloadToNullWithoutChangingStoredSchema(database.Path);
        }
        using (var connection = Open(database.Path))
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        using (var connection = Open(database.Path))
        using (var transaction = connection.BeginTransaction())
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => SourceCompatibilitySchemaV11.Ensure(
                    connection,
                    transaction,
                    predecessorVersion: 10));
            Assert.Equal("source_projection_input_authority_invalid", error.Message);
            Assert.Equal(
                0,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('source_schema_observations') WHERE name IN ('input_evidence_kind','raw_payload_sha256');"));
            Assert.Equal(
                0,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'source_trace_version_interpretation_%' OR name='source_compatibility_reconciliation_receipts';"));
            transaction.Rollback();
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
    }

    private static void CreateV10SourceFixture(string path)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        using var connection = Open(path);
        Execute(
            connection,
            $"""
            INSERT INTO raw_records(
                id,source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES(
                1,'raw-otlp','{TraceId}','2026-07-31T00:00:00.0000000+00:00',
                NULL,'[]',1,randomblob(32));
            INSERT INTO source_schema_observations(
                id,observation_id,raw_record_id,raw_payload_sha256,input_evidence_kind,
                ingest_batch_id,source_surface,source_application_version,source_adapter,
                adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                reason_code,next_action,capture_content_state,unknown_span_count,
                unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                overflow_occurrence_count,observed_at)
            VALUES(
                1,'v10-invalid-raw',1,
                '44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a',
                'payload_sha256','v10-invalid-raw-batch','github-copilot-cli','1.0.74',
                'github-copilot-otel','adapter-1',NULL,NULL,'supported',NULL,'none',
                'available',0,0,0,0,0,'2026-07-31T00:00:00.0000000+00:00');
            INSERT INTO source_trace_version_observations(
                source_observation_id,trace_id,resolution_state,source_application_version)
            VALUES(1,'{TraceId}','resolved','1.0.74');
            DROP TABLE source_semantic_capture_keys;
            DROP TABLE source_semantic_captures;
            DROP TABLE source_compatibility_reconciliation_receipts;
            DROP TABLE source_trace_version_interpretation_heads;
            DROP TABLE source_trace_version_interpretation_supersessions;
            DROP TABLE source_trace_compatibility_revisions;
            DROP TRIGGER source_schema_observations_insert_no_replace;
            DROP TRIGGER source_trace_version_observations_update_rejected;
            DROP TRIGGER source_trace_version_observations_insert_no_replace;
            DROP TRIGGER source_trace_version_observations_delete_rejected;
            DROP TRIGGER source_schema_observations_trace_version_child_delete_rejected;
            DROP TRIGGER source_schema_observations_projection_input_update_rejected;
            ALTER TABLE source_schema_observations DROP COLUMN input_evidence_kind;
            ALTER TABLE source_schema_observations DROP COLUMN raw_payload_sha256;
            UPDATE schema_version SET version=10 WHERE component='monitor';
            """);
    }

    private static void SetRawPayloadToNullWithoutChangingStoredSchema(string path)
    {
        RewriteRawPayloadNullability(path, nullable: true);
        using (var connection = Open(path))
            Execute(connection, "UPDATE raw_records SET payload_json=NULL WHERE id=1;");
        RewriteRawPayloadNullability(path, nullable: false);
    }

    private static void RewriteRawPayloadNullability(string path, bool nullable)
    {
        using var connection = Open(path);
        var schemaVersion = ScalarLong(connection, "PRAGMA schema_version;");
        var from = nullable ? "payload_json TEXT NOT NULL" : "payload_json TEXT";
        var to = nullable ? "payload_json TEXT" : "payload_json TEXT NOT NULL";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql=replace(sql,$from,$to)
            WHERE type='table' AND name='raw_records';
            PRAGMA schema_version={schemaVersion + 1};
            PRAGMA writable_schema=OFF;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"source-v10-migration-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

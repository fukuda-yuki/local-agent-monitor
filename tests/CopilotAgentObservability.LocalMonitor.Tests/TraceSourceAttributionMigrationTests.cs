using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class TraceSourceAttributionMigrationTests
{
    private const string TraceId = "11111111111111111111111111111111";
    private const string ProjectedAt = "2026-07-30T00:00:01.0000000+00:00";

    [Fact]
    public void V10Migration_ReattributesFullyRetainedRowsOnlyAndSecondStartupIsIdempotent()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var rawRecordId = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"));
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            Execute(
                connection,
                $"""
                INSERT INTO monitor_skill_invocations(
                    raw_record_id,trace_id,span_id,span_ordinal,session_id,skill_name,
                    skill_source,invocation_trigger,source_application_version,projected_at)
                VALUES({rawRecordId},'{TraceId}','1111111111111111',0,NULL,'synthetic-skill',
                    'available_inventory','explicit','1.0.75','{ProjectedAt}');
                INSERT INTO monitor_skill_inventories(
                    raw_record_id,trace_id,session_id,observed_name_count,retained_name_count,
                    names_truncated,source_application_version,projected_at)
                VALUES({rawRecordId},'{TraceId}',NULL,1,1,0,'1.0.75','{ProjectedAt}');
                INSERT INTO monitor_skill_inventory_names(
                    raw_record_id,trace_id,name_ordinal,skill_name)
                VALUES({rawRecordId},'{TraceId}',0,'synthetic-skill');
                INSERT INTO sessions(
                    session_id,status,completeness,repository,workspace,started_at,ended_at,
                    last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES(
                    'migration-session','active','rich','owner/repo','C:\workspace',
                    '2026-07-30T00:00:00.0000000+00:00',NULL,
                    '2026-07-30T00:00:01.0000000+00:00','expiring',
                    '2026-07-30T00:00:00.0000000+00:00',
                    '2026-07-30T00:00:01.0000000+00:00');
                INSERT INTO session_native_ids(
                    session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES(
                    'migration-session','vscode','native-session','native',
                    '2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO session_runs(
                    run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,
                    started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
                VALUES(
                    'migration-run','migration-session','vscode','native-run','{TraceId}',NULL,
                    'legacy-model','2026-07-30T00:00:00.0000000+00:00',NULL,3,5,8,'active');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                    source_adapter,source_event_id,type,occurred_at,content_state,
                    source_application_version,adapter_version,schema_fingerprint,
                    normalization_version,match_kind)
                VALUES(
                    'migration-event','migration-session','migration-run','vscode',NULL,
                    '{TraceId}','ok','otel-exact','migration-source-event','otel.span',
                    '2026-07-30T00:00:00.0000000+00:00','not_captured',
                    '1.0.75','legacy-adapter','legacy-fingerprint','legacy-normalization',
                    'exact_native');
                """);
            PrepareAsV9(connection);
        }
        var beforeInvariant = ReadInvariantSnapshot(temp.DatabasePath);
        var beforeSessionInvariant = ReadSessionInvariantSnapshot(temp.DatabasePath);

        store.CreateSchema();

        using (var connection = Open(temp.DatabasePath))
        {
            Assert.Equal(10L, Scalar<long>(
                connection,
                "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Equal("copilot-cli", Scalar<string>(
                connection,
                $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
            Assert.Equal("copilot-cli", Scalar<string>(
                connection,
                "SELECT client_kind FROM monitor_ingestions;"));
            Assert.Equal(1L, Scalar<long>(
                connection,
                $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}' AND cli_candidate_observed=1 AND vscode_candidate_observed=0 AND unknown_candidate_observed=0 AND relevant_evidence_observed=1;"));
            Assert.Equal("vscode", Scalar<string>(
                connection,
                "SELECT source_surface FROM session_events WHERE event_id='migration-event';"));
        }
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
        Assert.Equal(beforeSessionInvariant, ReadSessionInvariantSnapshot(temp.DatabasePath));
        var firstStartupState = ReadAttributionState(temp.DatabasePath);
        var firstStartupHash = ReadCanonicalDatabaseHash(temp.DatabasePath);

        store.CreateSchema();

        Assert.Equal(firstStartupState, ReadAttributionState(temp.DatabasePath));
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
        Assert.Equal(beforeSessionInvariant, ReadSessionInvariantSnapshot(temp.DatabasePath));
        Assert.Equal(firstStartupHash, ReadCanonicalDatabaseHash(temp.DatabasePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V10Migration_IncompleteContributingRawEvidenceLeavesAttributionUntouched(
        bool deleteSecondRaw)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var firstRaw = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"));
            var secondRaw = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "2222222222222222", "github-copilot"));
            InsertProjectedTrace(
                connection,
                firstRaw,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            InsertProjectedTraceContribution(
                connection,
                secondRaw,
                TraceId,
                "2222222222222222",
                "legacy-family",
                spanOrdinal: 0,
                spanProjectionComplete: deleteSecondRaw);
            Execute(
                connection,
                $"UPDATE monitor_traces SET span_count=2 WHERE trace_id='{TraceId}';");
            if (deleteSecondRaw)
            {
                Execute(connection, $"DELETE FROM raw_records WHERE id={secondRaw};");
            }
            PrepareAsV9(connection);
        }
        var beforeInvariant = ReadInvariantSnapshot(temp.DatabasePath);

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal(2L, Scalar<long>(
            verification,
            "SELECT COUNT(*) FROM monitor_ingestions WHERE client_kind='legacy-family';"));
        Assert.Equal(0L, Scalar<long>(
            verification,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
    }

    [Fact]
    public void V10Migration_RawSpanMembershipMismatchLeavesAttributionUntouched()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var rawRecordId = InsertRaw(
                connection,
                TraceId,
                PayloadWithSpans(
                    TraceId,
                    "github-copilot",
                    "1111111111111111",
                    "2222222222222222"));
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            PrepareAsV9(connection);
        }
        var beforeInvariant = ReadInvariantSnapshot(temp.DatabasePath);

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            "SELECT client_kind FROM monitor_ingestions;"));
        Assert.Equal(0L, Scalar<long>(
            verification,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
    }

    [Fact]
    public void V10Migration_SameCountDifferentSpanIdentityLeavesAttributionUntouched()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var rawRecordId = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"));
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "9999999999999999",
                "legacy-family",
                spanOrdinal: 0);
            PrepareAsV9(connection);
        }
        var beforeInvariant = ReadInvariantSnapshot(temp.DatabasePath);

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            "SELECT client_kind FROM monitor_ingestions;"));
        Assert.Equal(0L, Scalar<long>(
            verification,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
    }

    [Theory]
    [InlineData("expired_pending_deletion")]
    [InlineData("deletion_queued")]
    [InlineData("deleting")]
    [InlineData("deletion_failed")]
    public void V10Migration_ReadDeniedRawEvidenceLeavesAttributionUntouched(
        string lifecycle)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var rawRecordId = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"));
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            Execute(
                connection,
                $"""
                UPDATE retention_items
                SET state='{lifecycle}',
                    read_denied_at='2026-07-30T00:00:02.0000000+00:00',
                    queued_at='2026-07-30T00:00:02.0000000+00:00'
                WHERE store_kind='raw_record'
                  AND source_item_id='{rawRecordId}';
                """);
            PrepareAsV9(connection);
        }

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            "SELECT client_kind FROM monitor_ingestions;"));
        Assert.Equal(0L, Scalar<long>(
            verification,
            "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
    }

    [Fact]
    public void V10Migration_AuthorizesRawMetadataBeforeMaterializingPayload()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using var connection = Open(temp.DatabasePath);
        var rawRecordId = InsertRaw(
            connection,
            TraceId,
            Payload(TraceId, "1111111111111111", "github-copilot"));
        Execute(
            connection,
            $"""
            UPDATE retention_items
            SET state='expired_pending_deletion',
                read_denied_at='2026-07-30T00:00:02.0000000+00:00',
                queued_at='2026-07-30T00:00:02.0000000+00:00'
            WHERE store_kind='raw_record'
              AND source_item_id='{rawRecordId}';
            ALTER TABLE raw_records RENAME TO migration_raw_records;
            CREATE VIEW raw_records AS
            SELECT id,
                   source,
                   trace_id,
                   received_at,
                   resource_attributes_json,
                   migration_payload_probe(payload_json) AS payload_json,
                   schema_version,
                   retention_owner_token
            FROM migration_raw_records;
            """);
        var payloadReadCount = 0;
        connection.CreateFunction<string, string>(
            "migration_payload_probe",
            payload =>
            {
                payloadReadCount++;
                return payload;
            });
        using var transaction = connection.BeginTransaction();

        SqliteSourceCompatibilityStore.TransitionRetainedTraceSourceAttribution(
            connection,
            transaction);
        transaction.Commit();

        Assert.Equal(0, payloadReadCount);
        Assert.Equal(0L, Scalar<long>(
            connection,
            "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
    }

    [Fact]
    public void V10Migration_RawWithoutRetentionCatalogEntryLeavesAttributionUntouched()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        {
            var rawRecordId = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"),
                registerRetention: false);
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            PrepareAsV9(connection);
        }

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal("legacy-family", Scalar<string>(
            verification,
            "SELECT client_kind FROM monitor_ingestions;"));
        Assert.Equal(0L, Scalar<long>(
            verification,
            "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
    }

    private static void InsertProjectedTrace(
        SqliteConnection connection,
        long rawRecordId,
        string traceId,
        string spanId,
        string clientKind,
        int spanOrdinal)
    {
        InsertProjectedTraceContribution(
            connection,
            rawRecordId,
            traceId,
            spanId,
            clientKind,
            spanOrdinal,
            spanProjectionComplete: true);
        Execute(
            connection,
            $"""
            INSERT INTO monitor_traces(
                trace_id,client_kind,span_count,tool_call_count,error_count,first_seen_at,
                last_seen_at,projected_at,input_tokens,output_tokens,total_tokens,duration_ms,trace_status)
            VALUES('{traceId}','{clientKind}',1,0,0,'2026-07-30T00:00:00.0000000+00:00',
                '2026-07-30T00:00:00.0000000+00:00','{ProjectedAt}',3,5,8,42.5,'ok');
            """);
    }

    private static void InsertProjectedTraceContribution(
        SqliteConnection connection,
        long rawRecordId,
        string traceId,
        string spanId,
        string clientKind,
        int spanOrdinal,
        bool spanProjectionComplete)
    {
        var spanProjectedAt = spanProjectionComplete ? $"'{ProjectedAt}'" : "NULL";
        Execute(
            connection,
            $"""
            INSERT INTO monitor_ingestions(
                raw_record_id,received_at,source,trace_id,client_kind,span_count,projected_at,span_projected_at)
            VALUES({rawRecordId},'2026-07-30T00:00:00.0000000+00:00','raw-otlp',
                '{traceId}','{clientKind}',1,'{ProjectedAt}',{spanProjectedAt});
            INSERT INTO monitor_spans(
                raw_record_id,trace_id,span_id,span_ordinal,operation,total_tokens,duration_ms,projected_at)
            VALUES({rawRecordId},'{traceId}','{spanId}',{spanOrdinal},'chat',8,42.5,'{ProjectedAt}');
            INSERT INTO monitor_projection_dispositions(raw_record_id,state,revision,updated_at)
            VALUES({rawRecordId},'completed',7,'{ProjectedAt}');
            """);
    }

    private static long InsertRaw(
        SqliteConnection connection,
        string traceId,
        string payloadJson,
        bool registerRetention = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_records(
                source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES('raw-otlp',$trace_id,'2026-07-30T00:00:00.0000000+00:00',
                NULL,$payload_json,1,randomblob(32));
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        var rawRecordId = (long)command.ExecuteScalar()!;
        if (registerRetention)
        {
            new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(
                connection.DataSource).CreateSchema();
        }
        return rawRecordId;
    }

    private static void PrepareAsV9(SqliteConnection connection) =>
        Execute(
            connection,
            """
            DROP INDEX IX_source_trace_attribution_observations_trace_id;
            DROP TABLE source_trace_attribution_observations;
            DROP TABLE source_trace_attribution_reconciliation_queue;
            UPDATE schema_version SET version=9 WHERE component='monitor';
            """);

    private static string ReadInvariantSnapshot(string path)
    {
        using var connection = Open(path);
        return string.Join(
            "\n",
            ReadRows(
                connection,
                "SELECT raw_record_id,received_at,source,trace_id,span_count,projected_at,span_projected_at FROM monitor_ingestions ORDER BY raw_record_id;"),
            ReadRows(
                connection,
                "SELECT trace_id,span_count,tool_call_count,error_count,first_seen_at,last_seen_at,projected_at,input_tokens,output_tokens,total_tokens,duration_ms,trace_status FROM monitor_traces ORDER BY trace_id;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,trace_id,span_id,span_ordinal,operation,total_tokens,duration_ms,projected_at FROM monitor_spans ORDER BY raw_record_id,span_ordinal;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,state,revision,updated_at FROM monitor_projection_dispositions ORDER BY raw_record_id;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,trace_id,span_id,span_ordinal,skill_name,source_application_version,projected_at FROM monitor_skill_invocations ORDER BY raw_record_id,span_ordinal;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,trace_id,session_id,observed_name_count,retained_name_count,names_truncated,source_application_version,projected_at FROM monitor_skill_inventories ORDER BY raw_record_id,trace_id;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,trace_id,name_ordinal,skill_name FROM monitor_skill_inventory_names ORDER BY raw_record_id,trace_id,name_ordinal;"));
    }

    private static string ReadAttributionState(string path)
    {
        using var connection = Open(path);
        return string.Join(
            "\n",
            ReadRows(
                connection,
                "SELECT raw_record_id,trace_id,cli_candidate_observed,vscode_candidate_observed,unknown_candidate_observed,relevant_evidence_observed FROM source_trace_attribution_observations ORDER BY raw_record_id,trace_id;"),
            ReadRows(
                connection,
                "SELECT raw_record_id,client_kind FROM monitor_ingestions ORDER BY raw_record_id;"),
            ReadRows(
                connection,
                "SELECT trace_id,client_kind FROM monitor_traces ORDER BY trace_id;"));
    }

    private static string ReadSessionInvariantSnapshot(string path)
    {
        using var connection = Open(path);
        return string.Join(
            "\n",
            ReadRows(
                connection,
                "SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at FROM sessions ORDER BY session_id;"),
            ReadRows(
                connection,
                "SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids ORDER BY source_surface,native_session_id;"),
            ReadRows(
                connection,
                "SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status FROM session_runs ORDER BY run_id;"),
            ReadRows(
                connection,
                "SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind FROM session_events ORDER BY event_id;"));
    }

    private static byte[] ReadCanonicalDatabaseHash(string path)
    {
        using (var connection = Open(path))
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        }
        return SHA256.HashData(File.ReadAllBytes(path));
    }

    private static string ReadRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index)))));
        }
        return string.Join("\n", rows);
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static string Payload(string traceId, string spanId, string serviceName) =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"service.name","value":{"stringValue":"SERVICE_NAME"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID","spanId":"SPAN_ID","name":"chat gpt-4o"}
        ]}]}]}
        """
        .Replace("TRACE_ID", traceId, StringComparison.Ordinal)
        .Replace("SPAN_ID", spanId, StringComparison.Ordinal)
        .Replace("SERVICE_NAME", serviceName, StringComparison.Ordinal);

    private static string PayloadWithSpans(
        string traceId,
        string serviceName,
        params string[] spanIds) =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"service.name","value":{"stringValue":"SERVICE_NAME"}}
        ]},"scopeSpans":[{"spans":[SPANS]}]}]}
        """
        .Replace("SERVICE_NAME", serviceName, StringComparison.Ordinal)
        .Replace(
            "SPANS",
            string.Join(
                ",",
                spanIds.Select(spanId =>
                    "{\"traceId\":\"" + traceId +
                    "\",\"spanId\":\"" + spanId +
                    "\",\"name\":\"chat gpt-4o\"}")),
            StringComparison.Ordinal);
}

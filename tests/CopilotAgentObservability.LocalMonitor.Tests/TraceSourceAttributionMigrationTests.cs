using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
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
            CreateObsoleteSkillTables(connection);
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
                    '01900000-0000-7000-8000-00000000a001','active','partial','owner/repo','C:\workspace',
                    '2026-07-30T00:00:00.0000000+00:00',NULL,
                    '2026-07-30T00:00:01.0000000+00:00','expiring',
                    '2026-07-30T00:00:00.0000000+00:00',
                    '2026-07-30T00:00:01.0000000+00:00');
                INSERT INTO session_native_ids(
                    session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES(
                    '01900000-0000-7000-8000-00000000a001','vscode','native-session','native',
                    '2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO session_runs(
                    run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,
                    started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
                VALUES(
                    'migration-run','01900000-0000-7000-8000-00000000a001','vscode','native-run','{TraceId}',NULL,
                    'legacy-model','2026-07-30T00:00:00.0000000+00:00',NULL,3,5,8,'active');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                    source_adapter,source_event_id,type,occurred_at,content_state,
                    source_application_version,adapter_version,schema_fingerprint,
                    normalization_version,match_kind)
                VALUES(
                    'migration-event','01900000-0000-7000-8000-00000000a001','migration-run','vscode',NULL,
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
        new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(
            temp.DatabasePath).CreateSchema();

        using (var connection = Open(temp.DatabasePath))
        {
            Assert.Equal(11L, Scalar<long>(
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
            Assert.Equal(0L, Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'monitor_skill_%' OR name LIKE 'IX_monitor_skill_%';"));
        }
        Assert.Equal(beforeInvariant, ReadInvariantSnapshot(temp.DatabasePath));
        Assert.Equal(beforeSessionInvariant, ReadSessionInvariantSnapshot(temp.DatabasePath));
        var firstStartupState = ReadAttributionState(temp.DatabasePath);
        var firstStartupHash = ReadCanonicalDatabaseHash(temp.DatabasePath);

        store.CreateSchema();
        new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(
            temp.DatabasePath).CreateSchema();

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
    public void V10Migration_PinnedRawRowPastHistoricalExpiryStillAuthorizesAttribution()
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
                receivedAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
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
                SET state='retained_by_policy',
                    revision=revision+1
                WHERE store_kind='raw_record'
                  AND source_item_id='{rawRecordId}';
                """);
            PrepareAsV9(connection);
        }

        store.CreateSchema();

        using var verification = Open(temp.DatabasePath);
        Assert.Equal("copilot-cli", Scalar<string>(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal(1L, Scalar<long>(
            verification,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
    }

    [Fact]
    public void V10Migration_ExpiringRawRowPastExpiryRemainsUnauthorizedDuringMigration()
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
                receivedAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
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
        Assert.Equal(0L, Scalar<long>(
            verification,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
    }

    [Fact]
    public void V10Migration_ExpiringRawRowAtExactMigrationNowIsNotMaterializedAndRemainsUnavailableAfterReopen()
    {
        var migrationNow = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        using var temp = new MonitorTempDirectory();
        new SqliteSourceCompatibilityStore(temp.DatabasePath).CreateSchema();
        long rawRecordId;
        using (var connection = Open(temp.DatabasePath))
        {
            rawRecordId = InsertRaw(
                connection,
                TraceId,
                Payload(TraceId, "1111111111111111", "github-copilot"),
                receivedAt: migrationNow.AddDays(-90));
            InsertProjectedTrace(
                connection,
                rawRecordId,
                TraceId,
                "1111111111111111",
                "legacy-family",
                spanOrdinal: 0);
            Assert.Equal(
                migrationNow.ToString("O"),
                Scalar<string>(
                    connection,
                    $"SELECT expires_at FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{rawRecordId}';"));
            PrepareAsV9(connection);
        }
        var authorityBefore = ReadRawAndRetentionAuthoritySnapshot(temp.DatabasePath);

        var payloadReadCount = 0;
        using (var connection = Open(temp.DatabasePath))
        {
            using (var transaction = connection.BeginTransaction())
            {
                SqliteSourceCompatibilityStore.EnsureTraceSourceAttributionSchema(
                    connection,
                    transaction);
                SqliteSourceCompatibilityStore.TransitionRetainedTraceSourceAttribution(
                    connection,
                    transaction,
                    new MutableTimeProvider(migrationNow),
                    _ => payloadReadCount++);
                Execute(
                    connection,
                    transaction,
                    "UPDATE schema_version SET version=10 WHERE component='monitor';");
                transaction.Commit();
            }
        }

        Assert.Equal(0, payloadReadCount);
        Assert.Equal(authorityBefore, ReadRawAndRetentionAuthoritySnapshot(temp.DatabasePath));
        AssertUnavailableAttribution(temp.DatabasePath);
        using (var verification = Open(temp.DatabasePath))
        {
            Assert.Equal(10L, Scalar<long>(
                verification,
                "SELECT version FROM schema_version WHERE component='monitor';"));
        }

        new SqliteSourceCompatibilityStore(temp.DatabasePath).CreateSchema();

        Assert.Equal(authorityBefore, ReadRawAndRetentionAuthoritySnapshot(temp.DatabasePath));
        AssertUnavailableAttribution(temp.DatabasePath);
        using (var verification = Open(temp.DatabasePath))
        {
            Assert.Equal(11L, Scalar<long>(
                verification,
                "SELECT version FROM schema_version WHERE component='monitor';"));
        }
        var firstReopenHash = ReadCanonicalDatabaseHash(temp.DatabasePath);

        new SqliteSourceCompatibilityStore(temp.DatabasePath).CreateSchema();

        Assert.Equal(authorityBefore, ReadRawAndRetentionAuthoritySnapshot(temp.DatabasePath));
        AssertUnavailableAttribution(temp.DatabasePath);
        Assert.Equal(firstReopenHash, ReadCanonicalDatabaseHash(temp.DatabasePath));
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
        bool registerRetention = true,
        DateTimeOffset? receivedAt = null)
    {
        var capturedAt = receivedAt
            ?? new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_records(
                source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES('raw-otlp',$trace_id,$received_at,
                NULL,$payload_json,1,randomblob(32));
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        command.Parameters.AddWithValue("$received_at", capturedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        var rawRecordId = (long)command.ExecuteScalar()!;
        if (registerRetention)
        {
            var catalog = receivedAt is null
                ? new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(connection.DataSource)
                : new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(
                    connection.DataSource,
                    new MutableTimeProvider(capturedAt));
            catalog.CreateSchema();
        }
        return rawRecordId;
    }

    private static void PrepareAsV9(SqliteConnection connection)
    {
        CreateObsoleteSkillTables(connection);
        Execute(
            connection,
            """
            DROP TABLE IF EXISTS skill_projection_inventory_names;
            DROP TABLE IF EXISTS skill_projection_inventories;
            DROP TABLE IF EXISTS skill_projection_invocations;
            DROP TABLE IF EXISTS skill_projection_sdk_claims;
            DROP TABLE IF EXISTS skill_projection_operation_receipts;
            DROP TABLE IF EXISTS skill_projection_queue;
            DROP TABLE IF EXISTS skill_projection_trace_heads;
            DROP TABLE IF EXISTS skill_projection_generation_inputs;
            DROP TABLE IF EXISTS skill_projection_generations;
            DELETE FROM schema_version WHERE component='skill_projection';
            DROP TABLE source_compatibility_reconciliation_receipts;
            DROP TABLE source_trace_version_interpretation_heads;
            DROP TABLE source_trace_version_interpretation_supersessions;
            DROP TABLE source_trace_compatibility_revisions;
            DROP TRIGGER IF EXISTS source_schema_observations_insert_no_replace;
            DROP TRIGGER IF EXISTS source_trace_version_observations_insert_no_replace;
            DROP TRIGGER source_trace_version_observations_update_rejected;
            DROP TRIGGER source_trace_version_observations_delete_rejected;
            DROP TRIGGER source_schema_observations_trace_version_child_delete_rejected;
            DROP TRIGGER source_schema_observations_projection_input_update_rejected;
            ALTER TABLE source_schema_observations
            DROP COLUMN input_evidence_kind;
            ALTER TABLE source_schema_observations
            DROP COLUMN raw_payload_sha256;
            DROP INDEX IX_source_trace_attribution_observations_trace_id;
            DROP TABLE source_trace_attribution_observations;
            DROP TABLE source_trace_attribution_reconciliation_queue;
            UPDATE schema_version SET version=9 WHERE component='monitor';
            """);
    }

    private static void CreateObsoleteSkillTables(SqliteConnection connection) =>
        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_skill_invocations(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                span_id TEXT NULL,
                span_ordinal INTEGER NOT NULL,
                session_id TEXT NULL,
                skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                skill_source TEXT NULL CHECK (skill_source IS NULL OR length(skill_source) BETWEEN 1 AND 256),
                invocation_trigger TEXT NULL CHECK (invocation_trigger IS NULL OR length(invocation_trigger) BETWEEN 1 AND 256),
                source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                projected_at TEXT NOT NULL,
                UNIQUE(raw_record_id, span_ordinal),
                UNIQUE(trace_id, span_id)
            );
            CREATE TABLE IF NOT EXISTS monitor_skill_inventories(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                session_id TEXT NULL,
                observed_name_count INTEGER NOT NULL CHECK (observed_name_count >= 0),
                retained_name_count INTEGER NOT NULL CHECK (retained_name_count BETWEEN 0 AND 100),
                names_truncated INTEGER NOT NULL CHECK (names_truncated IN (0, 1)),
                source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                projected_at TEXT NOT NULL,
                UNIQUE(raw_record_id, trace_id)
            );
            CREATE TABLE IF NOT EXISTS monitor_skill_inventory_names(
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                name_ordinal INTEGER NOT NULL CHECK (name_ordinal BETWEEN 0 AND 99),
                skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                PRIMARY KEY (raw_record_id, trace_id, name_ordinal),
                FOREIGN KEY (raw_record_id, trace_id)
                    REFERENCES monitor_skill_inventories(raw_record_id, trace_id)
                    ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_monitor_skill_invocations_trace_id
                ON monitor_skill_invocations(trace_id,id);
            CREATE INDEX IF NOT EXISTS IX_monitor_skill_invocations_session_id
                ON monitor_skill_invocations(session_id,id);
            CREATE INDEX IF NOT EXISTS IX_monitor_skill_inventories_trace_id
                ON monitor_skill_inventories(trace_id,id);
            CREATE INDEX IF NOT EXISTS IX_monitor_skill_inventories_session_id
                ON monitor_skill_inventories(session_id,id);
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
                "SELECT raw_record_id,state,revision,updated_at FROM monitor_projection_dispositions ORDER BY raw_record_id;"));
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

    private static void AssertUnavailableAttribution(string path)
    {
        using var connection = Open(path);
        Assert.Equal("legacy-family", Scalar<string>(
            connection,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{TraceId}';"));
        Assert.Equal("legacy-family", Scalar<string>(
            connection,
            $"SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=(SELECT id FROM raw_records WHERE trace_id='{TraceId}');"));
        Assert.Equal(0L, Scalar<long>(
            connection,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE trace_id='{TraceId}';"));
        Assert.Equal(0L, Scalar<long>(
            connection,
            "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
        Assert.Equal("expiring", Scalar<string>(
            connection,
            $"SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id=(SELECT id FROM raw_records WHERE trace_id='{TraceId}');"));
    }

    private static string ReadRawAndRetentionAuthoritySnapshot(string path)
    {
        using var connection = Open(path);
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            """
            SELECT name
            FROM sqlite_schema
            WHERE type='table'
              AND (name='raw_records' OR name LIKE 'retention_%')
            ORDER BY name;
            """;
        var tables = new List<string>();
        using (var reader = tableCommand.ExecuteReader())
        {
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var snapshots = new List<string>();
        using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText =
                """
                SELECT type,name,tbl_name,sql
                FROM sqlite_schema
                WHERE sql IS NOT NULL
                  AND (
                      name='raw_records'
                      OR tbl_name='raw_records'
                      OR name LIKE 'retention_%'
                      OR tbl_name LIKE 'retention_%')
                ORDER BY type,name;
                """;
            using var reader = schemaCommand.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join(
                    "|",
                    Enumerable.Range(0, reader.FieldCount)
                        .Select(index => SnapshotValue(reader.GetValue(index)))));
            }
            snapshots.Add($"sqlite_schema\n{string.Join("\n", rows)}");
        }
        foreach (var table in tables)
        {
            var quotedTable = $"\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            using var rowCommand = connection.CreateCommand();
            rowCommand.CommandText = $"SELECT * FROM {quotedTable};";
            using var reader = rowCommand.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join(
                    "|",
                    Enumerable.Range(0, reader.FieldCount)
                        .Select(index => SnapshotValue(reader.GetValue(index)))));
            }
            rows.Sort(StringComparer.Ordinal);
            snapshots.Add($"{table}\n{string.Join("\n", rows)}");
        }
        return string.Join("\n", snapshots);
    }

    private static string SnapshotValue(object value) => value switch
    {
        DBNull => "null",
        byte[] bytes => $"blob:{Convert.ToHexString(bytes)}",
        string text => $"text:{Convert.ToHexString(Encoding.UTF8.GetBytes(text))}",
        long integer => $"integer:{integer.ToString(CultureInfo.InvariantCulture)}",
        double real => $"real:{BitConverter.DoubleToInt64Bits(real).ToString(CultureInfo.InvariantCulture)}",
        _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().FullName}."),
    };

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

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

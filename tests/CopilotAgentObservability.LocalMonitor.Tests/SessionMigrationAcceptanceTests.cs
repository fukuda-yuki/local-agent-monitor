using System.Globalization;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionMigrationAcceptanceTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-09T00:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("sdk-failed")]
    [InlineData("sdk-neutral")]
    [InlineData("sdk-other-tuple")]
    [InlineData("claude-failed")]
    [InlineData("claude-otel-fact")]
    public void CurrentSchemaValidation_RejectsEachIllegalTerminalTupleOutcome(string corruption)
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        if (corruption == "claude-failed")
        {
            Normalize(store, temp, "claude-code-hook", "claude-code", "SessionEnd", "{\"reason\":\"clear\"}");
        }
        else
        {
            Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        }

        using var connection = Open(temp.DatabasePath);
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        switch (corruption)
        {
            case "sdk-failed":
                Execute(connection, "UPDATE session_events SET terminal_outcome='failed'; UPDATE sessions SET status='failed';");
                break;
            case "sdk-neutral":
                Execute(connection, "UPDATE session_events SET terminal_outcome='neutral'; UPDATE sessions SET status='unknown';");
                break;
            case "sdk-other-tuple":
                Execute(connection, "UPDATE session_events SET type='Stop';");
                break;
            case "claude-failed":
                Execute(connection, "UPDATE session_events SET terminal_outcome='failed'; UPDATE sessions SET status='failed';");
                break;
            case "claude-otel-fact":
                {
                    var sessionId = Scalar<string>(connection, "SELECT session_id FROM sessions;");
                    var occurredAt = Scalar<string>(connection, "SELECT occurred_at FROM session_events;");
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                    INSERT INTO session_events(
                        event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,type,
                        occurred_at,content_state,terminal_outcome,terminal_policy_version)
                    VALUES(
                        $event,$session,'claude-code','11111111111111111111111111111111',
                        'claude-code-otel','11111111111111111111111111111111/2222222222222222','otel.span',
                        $occurred,'not_captured','clean',1);
                    """;
                    command.Parameters.AddWithValue("$event", Guid.CreateVersion7().ToString("D"));
                    command.Parameters.AddWithValue("$session", sessionId);
                    command.Parameters.AddWithValue("$occurred", occurredAt);
                    command.ExecuteNonQuery();
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("ended_at")]
    [InlineData("completeness")]
    public void CreateSchema_RejectsAggregateAndCompletenessDriftWithoutRepair(string corruption)
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        using (var connection = Open(temp.DatabasePath))
        {
            Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
            Execute(
                connection,
                corruption switch
                {
                    "status" => "UPDATE sessions SET status='active';",
                    "ended_at" => "UPDATE sessions SET ended_at='2026-08-09T00:00:00.0000001+00:00';",
                    "completeness" => "UPDATE sessions SET completeness=CASE completeness WHEN 'full' THEN 'partial' ELSE 'full' END;",
                    _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
                });
            Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        }
        SqliteConnection.ClearAllPools();
        var before = CaptureDatabaseFiles(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(() =>
            new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema());

        SqliteConnection.ClearAllPools();
        AssertDatabaseFilesEqual(before, CaptureDatabaseFiles(temp.DatabasePath));
    }

    [Fact]
    public void CurrentSchemaValidation_ReadsFactsButNeverContentOrRawAndNeverMutates()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        using var connection = Open(temp.DatabasePath);
        var before = SnapshotTables(connection, "session");
        var reads = new List<(string? Table, string? Column)>();
        var deniedActions = new List<int>();
        strdelegate_authorizer authorizer = (_, action, firstArgument, secondArgument, _, _) =>
        {
            if (action == raw.SQLITE_READ)
                reads.Add((firstArgument, secondArgument));
            if (!IsMutationAction(action)) return raw.SQLITE_OK;
            deniedActions.Add(action);
            return raw.SQLITE_DENY;
        };
        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_set_authorizer(connection.Handle, authorizer, null));
        try
        {
            Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        }
        finally
        {
            raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer?)null, null);
        }

        Assert.Empty(deniedActions);
        Assert.Contains(reads, item => item == ("session_events", "terminal_outcome"));
        Assert.Contains(reads, item => item == ("session_events", "terminal_policy_version"));
        Assert.DoesNotContain(reads, item => item.Table == "session_event_content");
        Assert.DoesNotContain(reads, item => item.Table == "raw_records");
        Assert.Equal(before, SnapshotTables(connection, "session"));
    }

    [Fact]
    public void CreateSchema_ReopensValidV14ByteIdentically()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        using (var connection = Open(temp.DatabasePath))
            Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
        SqliteConnection.ClearAllPools();
        var before = CaptureDatabaseFiles(temp.DatabasePath);

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        SqliteConnection.ClearAllPools();
        AssertDatabaseFilesEqual(before, CaptureDatabaseFiles(temp.DatabasePath));
    }

    [Fact]
    public void Version13Migration_PreservesRetentionCatalogAndReceiptDescendantsByteForByte()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(
            store,
            temp,
            "copilot-compatible-hook",
            "copilot-cli",
            "SessionEnd",
            "{\"reason\":\"error\"}");
        string before;
        using (var connection = Open(temp.DatabasePath))
        {
            EnsureRetentionCoverage(connection);
            SeedRetentionReceiptDescendants(connection);
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            before = SnapshotTables(connection, "retention_");
        }

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(before, SnapshotTables(migrated, "retention_"));
        Assert.Equal(
            1L,
            Scalar<long>(
                migrated,
                "SELECT COUNT(*) FROM retention_confirmation_bindings b JOIN retention_mutation_previews p ON p.preview_id=b.preview_id JOIN retention_operation_receipts r ON r.operation_id=b.operation_id;"));
        Assert.Equal(
            1L,
            Scalar<long>(
                migrated,
                "SELECT COUNT(*) FROM retention_items i JOIN session_event_content c ON c.event_id=i.source_item_id WHERE i.store_kind='session_event_content';"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void Version13Migration_PreservesInstalledSkillProjectionDescendantsByteForByte()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        string before;
        using (var connection = Open(temp.DatabasePath))
        {
            SeedSkillProjectionDescendants(connection);
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            before = SnapshotTables(connection, "skill_projection_");
        }

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(before, SnapshotTables(migrated, "skill_projection_"));
        Assert.Equal(
            1L,
            Scalar<long>(
                migrated,
                "SELECT COUNT(*) FROM skill_projection_generations g JOIN skill_projection_invocations i ON i.generation_id=g.generation_id JOIN skill_projection_inventories v ON v.generation_id=g.generation_id JOIN skill_projection_inventory_names n ON n.inventory_id=v.inventory_id;"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void Version13Migration_PreservesInstalledSkillClaimDescendantsByteForByte()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp, "copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}");
        string before;
        using (var connection = Open(temp.DatabasePath))
        {
            SeedSkillSdkClaim(connection);
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            before = SnapshotTables(connection, "skill_projection_");
        }

        new SqliteSessionStore(temp.DatabasePath, temp.TimeProvider).CreateSchema();

        using var migrated = Open(temp.DatabasePath);
        Assert.Equal(before, SnapshotTables(migrated, "skill_projection_"));
        Assert.Equal(
            1L,
            Scalar<long>(
                migrated,
                "SELECT COUNT(*) FROM skill_projection_sdk_claims c JOIN session_events e ON e.event_id=c.event_id AND e.session_id=c.session_id JOIN sessions s ON s.session_id=c.session_id;"));
        Assert.Equal(0L, Scalar<long>(migrated, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static void Normalize(
        SqliteSessionStore store,
        MonitorTempDirectory temp,
        string adapter,
        string surface,
        string type,
        string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var envelope = new SessionIngestEnvelope(
            1,
            adapter,
            surface,
            "migration-acceptance-session",
            [new("migration-acceptance-event", type, ObservedAt.ToString("O"), document.RootElement.Clone())],
            SourceApplicationVersion: adapter == "claude-code-hook" ? "2.1.207" : "1.0.0",
            AdapterVersion: adapter == "claude-code-hook" ? "claude-hook-v1" : "sdk-v1",
            NormalizationVersion: "session-normalization-v1");
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(envelope);
    }

    private static bool IsMutationAction(int action) => action is
        raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE
        or raw.SQLITE_CREATE_INDEX or raw.SQLITE_CREATE_TABLE or raw.SQLITE_CREATE_TEMP_INDEX or raw.SQLITE_CREATE_TEMP_TABLE
        or raw.SQLITE_CREATE_TEMP_TRIGGER or raw.SQLITE_CREATE_TEMP_VIEW or raw.SQLITE_CREATE_TRIGGER or raw.SQLITE_CREATE_VIEW or raw.SQLITE_CREATE_VTABLE
        or raw.SQLITE_DROP_INDEX or raw.SQLITE_DROP_TABLE or raw.SQLITE_DROP_TEMP_INDEX or raw.SQLITE_DROP_TEMP_TABLE
        or raw.SQLITE_DROP_TEMP_TRIGGER or raw.SQLITE_DROP_TEMP_VIEW or raw.SQLITE_DROP_TRIGGER or raw.SQLITE_DROP_VIEW or raw.SQLITE_DROP_VTABLE
        or raw.SQLITE_ALTER_TABLE or raw.SQLITE_ATTACH or raw.SQLITE_DETACH or raw.SQLITE_REINDEX or raw.SQLITE_ANALYZE;

    private static void EnsureRetentionCoverage(SqliteConnection connection) => Execute(
        connection,
        """
        INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
        VALUES
            ('session_event_content',1),
            ('raw_record',1),
            ('analysis_run_raw',1),
            ('sensitive_bundle',1),
            ('analysis_sdk_directory',1);
        """);

    private static void SeedRetentionReceiptDescendants(SqliteConnection connection)
    {
        var sessionId = Scalar<string>(connection, "SELECT session_id FROM sessions;");
        var itemId = Scalar<string>(connection, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';");
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE retention_items SET state='retained_by_policy' WHERE item_id=$item;
            INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
            VALUES($item,'operation','migration-acceptance-owner','2026-08-09T00:05:00.0000000+00:00',7);
            INSERT INTO retention_mutation_previews(
                preview_id,schema_version,target_kind,target_id,operation,scope,preview_json,
                expected_state_version,target_item_set_digest,preview_digest,workflow_key_digest,
                created_at,expires_at,rejection_code,active_conflict_snapshot,conflict_version,
                reason_code,comment_sha256,comment)
            VALUES(
                'migration-preview',1,'session',$session,'pin','session_items','{}',
                'expected-version','target-set-digest','preview-digest',zeroblob(32),
                '2026-08-09T00:00:00.0000000+00:00','2026-08-09T00:10:00.0000000+00:00',
                NULL,'[]','conflict-v1','synthetic_reason',zeroblob(32),'synthetic note');
            INSERT INTO retention_confirmation_bindings(
                confirmation_id,preview_id,schema_version,token_sha256,nonce,target_kind,target_id,
                operation,scope,preview_digest,expected_state_version,target_item_set_digest,
                active_conflict_snapshot,conflict_version,confirmation_expires_at,
                workflow_idempotency_key,reason_code,comment_sha256,created_at,consumed_at,
                invalidated_at,operation_id)
            VALUES(
                'migration-confirmation','migration-preview',1,zeroblob(32),zeroblob(16),
                'session',$session,'pin','session_items','preview-digest','expected-version',
                'target-set-digest','[]','conflict-v1','2026-08-09T00:10:00.0000000+00:00',
                'migration-workflow','synthetic_reason',zeroblob(32),
                '2026-08-09T00:00:01.0000000+00:00','2026-08-09T00:00:02.0000000+00:00',
                NULL,'migration-operation');
            INSERT INTO retention_mutation_idempotency(
                key_digest,step,request_fingerprint,result_json,completion_code,created_at,expires_at)
            VALUES(
                zeroblob(32),'mutation',randomblob(32),'{}','retention_pin_applied',
                '2026-08-09T00:00:00.0000000+00:00','2026-08-10T00:00:00.0000000+00:00');
            INSERT INTO retention_operation_receipts(
                operation_id,schema_version,result_code,target_kind,target_id,operation,scope,
                target_item_count,result_json,completion_code,expected_version,result_version,
                target_item_set_digest,created_at,completed_at,last_replayed_at)
            VALUES(
                'migration-operation',1,'retention_pin_applied','session',$session,'pin',
                'session_items',1,'{}','retention_pin_applied','expected-version','result-version',
                'target-set-digest','2026-08-09T00:00:00.0000000+00:00',
                '2026-08-09T00:00:02.0000000+00:00','2026-08-09T00:00:03.0000000+00:00');
            INSERT INTO retention_audit_events(
                event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,
                actor_label,operation,reason_code,comment,previous_pin_state,new_pin_state,
                previous_operation_state,new_operation_state,request_idempotency_key,
                expected_version,result_version,target_item_set_digest,completion_code,error_code)
            VALUES(
                'migration-audit','migration-operation','retention_mutation','session',$session,$session,
                '2026-08-09T00:00:02.0000000+00:00','local-user','pin','synthetic_reason',
                'synthetic note','unpinned','pinned','ready','complete','migration-workflow',
                'expected-version','result-version','target-set-digest','retention_pin_applied',NULL);
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
    }

    private static void SeedSkillProjectionDescendants(SqliteConnection connection)
    {
        var sessionId = Scalar<string>(connection, "SELECT session_id FROM sessions;");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO skill_projection_generations(
                generation_id,trace_id,compatibility_revision,input_frontier_sha256,
                projector_version,lifecycle,created_at,updated_at)
            VALUES(
                41,'11111111111111111111111111111111',7,$frontier,'skill-projector-v1','current',
                '2026-08-09T00:00:00.0000000+00:00','2026-08-09T00:00:01.0000000+00:00');
            INSERT INTO skill_projection_generation_inputs(
                generation_id,input_ordinal,source_observation_id,raw_record_id,
                input_evidence_kind,raw_payload_sha256)
            VALUES(41,0,101,201,'payload_sha256',$payload);
            INSERT INTO skill_projection_trace_heads(
                trace_id,desired_generation_id,current_generation_id,updated_at)
            VALUES(
                '11111111111111111111111111111111',41,41,
                '2026-08-09T00:00:01.0000000+00:00');
            INSERT INTO skill_projection_queue(
                generation_id,trace_id,compatibility_revision,input_frontier_sha256,
                projector_version,state,attempt_count,lease_owner,lease_generation,
                lease_expires_at,next_attempt_at,error_code)
            VALUES(
                41,'11111111111111111111111111111111',7,$frontier,'skill-projector-v1',
                'completed',1,NULL,1,NULL,NULL,NULL);
            INSERT INTO skill_projection_operation_receipts(
                operation_key,semantic_fingerprint,outcome,generation_id,created_at)
            VALUES('migration-operation',$semantic,'changed',41,'2026-08-09T00:00:01.0000000+00:00');
            INSERT INTO skill_projection_invocations(
                invocation_id,generation_id,source_arm,raw_record_id,trace_id,span_id,
                span_ordinal,session_id,skill_name,skill_source,invocation_trigger,
                source_application_version,projected_at)
            VALUES(
                51,41,'otel_trace_span',201,'11111111111111111111111111111111',
                '2222222222222222',0,$session,'synthetic-skill','synthetic-source',
                'synthetic-trigger','1.0.0','2026-08-09T00:00:01.0000000+00:00');
            INSERT INTO skill_projection_inventories(
                inventory_id,generation_id,source_arm,raw_record_id,trace_id,session_id,
                observed_name_count,retained_name_count,names_truncated,
                source_application_version,projected_at)
            VALUES(
                61,41,'otel_trace_span',201,'11111111111111111111111111111111',$session,
                1,1,0,'1.0.0','2026-08-09T00:00:01.0000000+00:00');
            INSERT INTO skill_projection_inventory_names(inventory_id,name_ordinal,skill_name)
            VALUES(61,0,'synthetic-skill');
            """;
        command.Parameters.AddWithValue("$frontier", new string('a', 64));
        command.Parameters.AddWithValue("$payload", new string('b', 64));
        command.Parameters.AddWithValue("$semantic", new string('c', 64));
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
    }

    private static void SeedSkillSdkClaim(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO skill_projection_sdk_claims(
                claim_id,session_id,event_id,source_event_id,source_adapter,source_surface,
                source_application_version,adapter_version,normalization_version,payload_schema,
                schema_fingerprint,payload_sha256,producer_trace_id,producer_span_id,
                skill_name,skill_source,invocation_trigger,created_at)
            SELECT
                $claim,event.session_id,event.event_id,event.source_event_id,event.source_adapter,
                event.source_surface,'1.0.0','sdk-v1','session-normalization-v1',
                'skill-invocation-v1',$schema,$payload,NULL,NULL,'synthetic-skill',
                'synthetic-source','synthetic-trigger','2026-08-09T00:00:01.0000000+00:00'
            FROM session_events AS event;
            """;
        command.Parameters.AddWithValue("$claim", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$schema", new string('d', 64));
        command.Parameters.AddWithValue("$payload", new string('e', 64));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyDictionary<string, byte[]> CaptureDatabaseFiles(string databasePath) =>
        new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToDictionary(path => System.IO.Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertDatabaseFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var name in expected.Keys)
            Assert.Equal(expected[name], actual[name]);
    }

    private static string SnapshotTables(SqliteConnection connection, string prefix)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE $prefix ORDER BY name;";
            command.Parameters.AddWithValue("$prefix", prefix + "%");
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        return string.Join("\n--\n", tables.Select(table => table + "\n" + SnapshotTable(connection, table)));
    }

    private static string SnapshotTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index)
                        ? "null"
                        : reader.GetValue(index) is byte[] bytes
                            ? "blob:" + Convert.ToHexString(bytes)
                            : reader.GetFieldType(index).Name + ":" + Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return string.Join("\n", rows);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}

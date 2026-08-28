using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Runtime.InteropServices;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceProjectionSchemaTests
{
    [Fact]
    public void EnsureCreatesExactVersionAndOwnedTables()
    {
        using var connection = OpenSessionDatabase();
        using var transaction = connection.BeginTransaction();

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        transaction.Commit();

        Assert.Equal(
            ["local_workspace_projection:5"],
            Strings(connection, "SELECT component || ':' || version FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(
            ["local_workspace_content_tombstones", "local_workspace_execution_headers", "local_workspace_node_content_refs", "local_workspace_node_edges", "local_workspace_node_source_references", "local_workspace_nodes", "local_workspace_projection_state", "local_workspace_semantic_receipts", "local_workspace_session_activity", "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_sources", "local_workspace_sessions", "local_workspace_skill_metadata", "local_workspace_span_facts", "local_workspace_subagent_lifecycle", "local_workspace_token_observations", "local_workspace_tool_metadata"],
            Strings(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'local_workspace_%' ORDER BY name;"));
    }

    [Fact]
    public void EnsureMigratesExactV2AtomicallyAndReruns()
    {
        using var connection = OpenSessionDatabase();
        foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV2SchemaSql) Execute(connection, sql);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',2);");
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, DateTimeOffset.UnixEpoch);
            transaction.Rollback();
        }
        Assert.Equal(["2"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name='local_workspace_session_search_facts';"));

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal(["5"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(["local_workspace_span_facts"], Strings(connection, "SELECT name FROM sqlite_schema WHERE name='local_workspace_span_facts';"));
    }

    [Fact]
    public void EnsureComposesExactV1ThroughV2ToV3AtomicallyAndReruns()
    {
        using var connection = OpenSessionDatabase();
        foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV1SchemaSql) Execute(connection, sql);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',1);");

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, DateTimeOffset.UnixEpoch);
            Assert.Equal(["5"], Strings(transaction, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
            transaction.Rollback();
        }

        Assert.Equal(["1"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name IN ('local_workspace_span_facts','local_workspace_session_search_facts');"));

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Equal(["5"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(
            ["local_workspace_session_search_facts", "local_workspace_span_facts"],
            Strings(connection, "SELECT name FROM sqlite_schema WHERE name IN ('local_workspace_span_facts','local_workspace_session_search_facts') ORDER BY name;"));
    }

    [Fact]
    public void EnsureRejectsDriftedCurrentShape()
    {
        using var connection = OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Execute(connection, "ALTER TABLE local_workspace_sessions ADD COLUMN drift TEXT;");

        Assert.Throws<InvalidOperationException>(() => LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch));
        Assert.Equal(["5"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void EnsureMigratesExactV3ToV4AtomicallyAndBackfillsStableExactIdentities()
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'active'); INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000003','copilot-sdk',NULL,NULL,NULL,'synthetic','source-1','tool.execution_start','invalid-time','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var execution = Strings(connection, "SELECT execution_id FROM local_workspace_execution_headers;").Single();
        var node = Strings(connection, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='session_event';").Single();
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", execution);
        Assert.Matches("^node-[0-9a-f]{32}$", node);
        Assert.Equal(["invalid"], Strings(connection, "SELECT time_authority FROM local_workspace_nodes WHERE source_kind='session_event';"));

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal([execution], Strings(connection, "SELECT execution_id FROM local_workspace_execution_headers;"));
        Assert.Equal([node], Strings(connection, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='session_event';"));
    }

    [Fact]
    public void EnsureMigratesLiteralExactV3AndRollbackRemovesEveryV4Object()
    {
        using var connection = OpenSessionDatabase();
        foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV3SchemaSql) Execute(connection, sql);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',3);");

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, DateTimeOffset.UnixEpoch);
            Assert.Equal(["5"], Strings(transaction, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
            transaction.Rollback();
        }

        Assert.Equal(["3"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name='local_workspace_nodes';"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyProjectionMigratesWithInstalledRetentionWithoutReadingV5TombstoneColumns(int version)
    {
        using var connection = OpenSessionDatabase();
        using (var retention = connection.BeginTransaction())
        {
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, retention);
            retention.Commit();
        }
        var statements = version switch
        {
            1 => LocalWorkspaceProjectionSchemaV1.ExactV1SchemaSql,
            2 => LocalWorkspaceProjectionSchemaV1.ExactV2SchemaSql,
            3 => LocalWorkspaceProjectionSchemaV1.ExactV3SchemaSql,
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };
        foreach (var sql in statements) Execute(connection, sql);
        Execute(connection, $"INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',{version});");

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Equal(["5"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DataBearingLegacyProjectionMigratesAvailableRetentionContentWithoutReadingV5TombstoneColumns(int version)
    {
        using var connection = OpenAvailableRetentionFixture(version);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["5"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(["available:item-event-a:1"], Strings(connection, """
            SELECT availability_state||':'||retention_item_id||':'||retention_revision
            FROM local_workspace_node_content_refs;
            """));
    }

    [Theory]
    [InlineData("execution_id")]
    [InlineData("node_id")]
    public void ExactV4SemanticIdentityDriftFailsBeforeDestructiveRebuild(string corruption)
    {
        using var connection = OpenSessionDatabase();
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        Execute(connection, "INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');");
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, transaction, ["session-a"], DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        Execute(connection, corruption == "execution_id"
            ? "UPDATE local_workspace_execution_headers SET execution_id='018f0000-0000-7000-8000-000000000099';"
            : "UPDATE local_workspace_nodes SET node_id='node-00000000000000000000000000000000' WHERE source_kind='execution_root';");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        var before = WorkspaceDigest(connection);

        Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch));

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4OrphanSpanFactFailsBeforeV5Stamp()
    {
        using var connection = OpenSessionDatabase();
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4); INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens) VALUES(999,0,1,2);");
        var before = WorkspaceDigest(connection);

        Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch));

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4StaleOwnedSpanFactIsClearedWhenItsRawPayloadNoLongerProjectsTheFact()
    {
        using var connection = OpenSessionDatabase();
        InstallRawProjectionTables(connection);
        InstallExactV4(connection);
        Execute(connection, """
            INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);
            INSERT INTO raw_records VALUES(1,'otlp',NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,'{"resourceSpans":[]}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,span_ordinal) VALUES(1,0);
            INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens) VALUES(1,0,7,11);
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Empty(Strings(connection, "SELECT CAST(raw_record_id AS TEXT) FROM local_workspace_span_facts;"));
        Assert.Equal(["5"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4StaleSpanFactWithAProjectedSpanButNoRawOwnerIsCleared()
    {
        using var connection = OpenSessionDatabase();
        InstallRawProjectionTables(connection);
        InstallExactV4(connection);
        Execute(connection, """
            INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);
            INSERT INTO monitor_spans(raw_record_id,span_ordinal) VALUES(1,0);
            INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens) VALUES(1,0,7,11);
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Empty(Strings(connection, "SELECT CAST(raw_record_id AS TEXT) FROM local_workspace_span_facts;"));
        Assert.Equal(["5"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4OversizedRawPayloadFailsBeforeMutation()
    {
        using var connection = OpenSessionDatabase();
        InstallRawProjectionTables(connection);
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
                VALUES(1,'otlp',NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,$payload,1,NULL);
                """;
            insert.Parameters.AddWithValue("$payload",
                $"{{\"resourceSpans\":[],\"padding\":\"{new string('x', CopilotAgentObservability.RawReplay.RawReplayLimits.MaximumRawRecordBytes)}\"}}");
            insert.ExecuteNonQuery();
        }
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_raw_payload_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4AvailableRetentionReferenceMigratesWithItsExactAuthorityProof()
    {
        using var connection = OpenAvailableRetentionFixture();

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["5"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(["session_event_content:0198f5b8-0c00-7000-8000-000000000011:event_content:available:item-event-a:1:1:1"], Strings(connection, """
            SELECT store_kind||':'||source_item_id||':'||part||':'||availability_state||':'||retention_item_id||':'||retention_revision||':'||
                   (retention_ownership_receipt IS NOT NULL)||':'||(retention_owner_token IS NOT NULL)
            FROM local_workspace_node_content_refs;
            """));
    }

    [Fact]
    public void ExactV4DeletedRetentionReferenceMigratesWithTheConstantSessionContentTombstoneStore()
    {
        using var connection = OpenAvailableRetentionFixture();
        Execute(connection, """
            UPDATE retention_items SET state='deleted',deleted_at='2026-08-25T00:00:00.0000000+00:00',revision=2
              WHERE item_id='item-event-a';
            INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
              VALUES('item-event-a','2026-08-25T00:00:00.0000000+00:00','2026-08-25T00:00:00.0000000+00:00');
            INSERT INTO local_workspace_content_tombstones(source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision)
              SELECT source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,'2026-08-25T00:00:00.0000000+00:00','item-event-a',2
              FROM local_workspace_node_content_refs;
            UPDATE local_workspace_node_content_refs SET
              revision_input=revision_input||'|deleted|2',retention_store_instance_id=NULL,source_captured_at=NULL,source_expires_at=NULL,
              retention_revision=2,retention_ownership_receipt=NULL,retention_owner_token=NULL,availability_state='deleted';
            DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:01Z"));

        Assert.Equal(["session_event_content:0198f5b8-0c00-7000-8000-000000000011:event_content:deleted:2"], Strings(connection, """
            SELECT store_kind||':'||source_item_id||':'||part||':'||availability_state||':'||retention_revision
            FROM local_workspace_node_content_refs;
            """));
        Assert.Equal(["session_event_content:0198f5b8-0c00-7000-8000-000000000011:event_content:2"], Strings(connection, """
            SELECT store_kind||':'||source_item_id||':'||part||':'||retention_revision
            FROM local_workspace_content_tombstones;
            """));
    }

    [Theory]
    [InlineData("UPDATE local_workspace_node_content_refs SET retention_item_id='missing-item';")]
    [InlineData("UPDATE local_workspace_node_content_refs SET retention_ownership_receipt=zeroblob(32);")]
    [InlineData("UPDATE local_workspace_node_content_refs SET retention_owner_token=zeroblob(32);")]
    [InlineData("UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE item_id='item-event-a';")]
    [InlineData("DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE local_workspace_node_content_refs SET availability_state='read_denied',retention_owner_token=NULL,retention_revision=NULL;")]
    public void ExactV4RetentionAuthorityDriftFailsBeforeMutationAndLeavesBytesIdentical(string mutation)
    {
        using var connection = OpenAvailableRetentionFixture();
        Execute(connection, mutation);
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"))).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4SourceAbsentReadDeniedReferenceMigratesWithItsAuthenticatedAuthority()
    {
        using var connection = OpenAvailableRetentionFixture();
        Execute(connection, """
            UPDATE retention_items SET
              state='expired_pending_deletion',revision=2,
              read_denied_at='2026-08-25T00:00:00.0000000+00:00',
              queued_at='2026-08-25T00:00:00.0000000+00:00'
            WHERE item_id='item-event-a';
            UPDATE local_workspace_node_content_refs SET
              retention_revision=2,retention_owner_token=NULL,
              availability_state='read_denied',
              revision_input=(
                SELECT e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||
                       i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|'
                FROM session_events e
                JOIN retention_items i ON i.item_id='item-event-a'
                WHERE e.event_id=local_workspace_node_content_refs.source_item_id);
            DELETE FROM session_event_content
            WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:01Z"));

        Assert.Equal(["read_denied:item-event-a:2:1:0"], Strings(connection, """
            SELECT availability_state||':'||COALESCE(retention_item_id,'null')||':'||COALESCE(retention_revision,'null')||':'||
                   (retention_ownership_receipt IS NOT NULL)||':'||(retention_owner_token IS NOT NULL)
            FROM local_workspace_node_content_refs;
            """));
        Assert.Empty(Strings(connection, """
            SELECT event_id FROM session_event_content
            WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """));
    }

    [Fact]
    public void ExactV4SourceAbsentReadDeniedWithStaleRevisionEvidenceFailsBeforeMutation()
    {
        using var connection = OpenAvailableRetentionFixture();
        Execute(connection, """
            UPDATE retention_items SET
              state='expired_pending_deletion',revision=2,
              read_denied_at='2026-08-25T00:00:00.0000000+00:00',
              queued_at='2026-08-25T00:00:00.0000000+00:00'
            WHERE item_id='item-event-a';
            UPDATE local_workspace_node_content_refs SET
              retention_revision=2,retention_owner_token=NULL,
              availability_state='read_denied';
            DELETE FROM session_event_content
            WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """);
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:01Z"))).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4CoherentLookingButUnauthenticatedReadDeniedFailsBeforeMutation()
    {
        using var connection = OpenAvailableRetentionFixture();
        Execute(connection, """
            UPDATE local_workspace_node_content_refs SET
              retention_owner_token=NULL,availability_state='read_denied';
            DELETE FROM session_event_content
            WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """);
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:01Z"))).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void SemanticValidationRejectsParentEdgeWhenTheNodeHasNoParent()
    {
        using var connection = OpenPopulatedCurrentProjection();
        Execute(connection, """
            INSERT INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
            SELECT node_id,node_id,'parent','exact',0 FROM local_workspace_nodes WHERE source_kind='execution_root';
            """);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.ValidateSemanticRows(connection, transaction)).Message);
    }

    [Fact]
    public void ExactV4MissingExactParentEdgeFailsBeforeMutation()
    {
        using var connection = OpenPopulatedExactV4Projection();
        Execute(connection, "DELETE FROM local_workspace_node_edges WHERE relation_kind='parent';");
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Theory]
    [InlineData("local_workspace_execution_headers", "end_utc_ticks=start_utc_ticks+10000,duration_ms=NULL")]
    [InlineData("local_workspace_execution_headers", "end_utc_ticks=NULL,duration_ms=1")]
    [InlineData("local_workspace_nodes", "end_utc_ticks=start_utc_ticks+10000,duration_ms=NULL")]
    [InlineData("local_workspace_nodes", "end_utc_ticks=NULL,duration_ms=1")]
    public void CurrentSchemaRejectsHalfPopulatedTimingPairs(string table, string assignment)
    {
        using var connection = OpenPopulatedCurrentProjection();

        Assert.Throws<SqliteException>(() => Execute(connection,
            $"UPDATE {table} SET {assignment} WHERE time_authority='recorded';"));
    }

    [Theory]
    [InlineData("label_state='unsupported'")]
    [InlineData("timing_state='unsupported'")]
    [InlineData("timing_state='recorded',started_at=NULL,ended_at=NULL,duration_ms=NULL")]
    [InlineData("timing_state='recorded',ended_at='2026-08-24T00:00:01.0000000+00:00',duration_ms=NULL")]
    [InlineData("timing_state='recorded',ended_at=NULL,duration_ms=1")]
    [InlineData("timing_state='not_observed',duration_ms=1")]
    [InlineData("status='active',timing_state='recorded',started_at='2026-08-24T00:00:00.0000000+00:00',ended_at='2026-08-24T00:00:01.0000000+00:00',last_seen_at='2026-08-24T00:00:01.0000000+00:00',last_seen_epoch_ms=1787529601000,duration_ms=1000")]
    [InlineData("status='completed',timing_state='recorded',started_at='2026-08-24T00:00:00.0000000+00:00',ended_at=NULL,last_seen_at='2026-08-24T00:00:01.0000000+00:00',last_seen_epoch_ms=1787529601000,duration_ms=NULL")]
    public void CurrentSessionSchemaRejectsClosedStateAndTimingCorruption(string assignment)
    {
        using var connection = OpenPopulatedCurrentProjection();

        Assert.Throws<SqliteException>(() => Execute(connection,
            $"UPDATE local_workspace_sessions SET {assignment} WHERE session_id='session-a';"));
    }

    [Theory]
    [InlineData("status='active',timing_state='recorded',started_at='2026-08-24T00:00:00.0000000+00:00',ended_at='2026-08-24T00:00:01.0000000+00:00',last_seen_at='2026-08-24T00:00:01.0000000+00:00',last_seen_epoch_ms=1787529601000,duration_ms=1000")]
    [InlineData("status='completed',timing_state='recorded',started_at='2026-08-24T00:00:00.0000000+00:00',ended_at=NULL,last_seen_at='2026-08-24T00:00:01.0000000+00:00',last_seen_epoch_ms=1787529601000,duration_ms=NULL")]
    public void CurrentSemanticValidationRejectsStatusTimingContradictions(string assignment)
    {
        using var connection = OpenPopulatedCurrentProjection();
        Execute(connection, "PRAGMA ignore_check_constraints=ON;");
        Execute(connection, $"UPDATE local_workspace_sessions SET {assignment} WHERE session_id='session-a';");
        Execute(connection, "PRAGMA ignore_check_constraints=OFF;");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.ValidateSemanticRows(connection, transaction)).Message);
    }

    [Fact]
    public void CurrentSessionSchemaAllowsRecordedActiveTimingWithoutAnEndOrDuration()
    {
        using var connection = OpenPopulatedCurrentProjection();

        Execute(connection, """
            UPDATE local_workspace_sessions SET
              timing_state='recorded',
              started_at='2026-08-24T00:00:00.0000000+00:00',
              ended_at=NULL,
              last_seen_at='2026-08-24T00:00:01.0000000+00:00',
              last_seen_epoch_ms=1787529601000,
              duration_ms=NULL
            WHERE session_id='session-a';
            """);

        Assert.Equal(["recorded:1:0:0"], Strings(connection, """
            SELECT timing_state||':'||(started_at IS NOT NULL)||':'||
                   (ended_at IS NOT NULL)||':'||(duration_ms IS NOT NULL)
            FROM local_workspace_sessions WHERE session_id='session-a';
            """));
    }

    [Theory]
    [InlineData("label_state='unsupported'")]
    [InlineData("timing_state='unsupported'")]
    [InlineData("timing_state='recorded',started_at=NULL,ended_at=NULL,duration_ms=NULL")]
    public void ExactV4SessionStateCorruptionFailsBeforeMutation(string assignment)
    {
        using var connection = OpenPopulatedExactV4Projection();
        Execute(connection,
            $"UPDATE local_workspace_sessions SET {assignment} WHERE session_id='session-a';");
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Theory]
    [InlineData("run_owner")]
    [InlineData("event_session_owner")]
    [InlineData("event_run_owner")]
    public void ExactV4SourceRowsMustBelongToThePersistedSessionAndExecution(string corruption)
    {
        using var connection = OpenPopulatedExactV4Projection();
        Execute(connection, """
            INSERT INTO sessions VALUES('session-b','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-b','session-a','copilot-sdk','native-run-b',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            PRAGMA foreign_keys=OFF;
            """);
        Execute(connection, corruption switch
        {
            "run_owner" => "UPDATE session_runs SET session_id='session-b' WHERE run_id='run-a';",
            "event_session_owner" => "UPDATE session_events SET session_id='session-b' WHERE event_id='event-a';",
            "event_run_owner" => "UPDATE session_events SET run_id='run-b' WHERE event_id='event-a';",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        });
        Execute(connection, "PRAGMA foreign_keys=ON;");
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ExactV4ExecutionRootMustBindToItsExactHeaderSourceRun()
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES
              ('run-a','session-a','copilot-sdk','native-run-a',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('run-b','session-a','copilot-sdk','native-run-b',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            """);
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(
                connection, transaction, ["session-a"], DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }
        var executionA = LocalWorkspaceProjectionStore.StableExecutionId("session-a", "session_run", "run-a");
        var executionB = LocalWorkspaceProjectionStore.StableExecutionId("session-a", "session_run", "run-b");
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        Execute(connection, $"""
            UPDATE local_workspace_nodes SET execution_id='swap'
              WHERE source_kind='execution_root' AND execution_id='{executionA}';
            UPDATE local_workspace_nodes SET execution_id='{executionA}'
              WHERE source_kind='execution_root' AND execution_id='{executionB}';
            UPDATE local_workspace_nodes SET execution_id='{executionB}'
              WHERE source_kind='execution_root' AND execution_id='swap';
            """);
        Execute(connection, "PRAGMA foreign_keys=ON;");
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Theory]
    [InlineData("local_workspace_execution_headers", "end_utc_ticks=start_utc_ticks+10000,duration_ms=NULL")]
    [InlineData("local_workspace_execution_headers", "end_utc_ticks=NULL,duration_ms=1")]
    [InlineData("local_workspace_nodes", "end_utc_ticks=start_utc_ticks+10000,duration_ms=NULL")]
    [InlineData("local_workspace_nodes", "end_utc_ticks=NULL,duration_ms=1")]
    public void ExactV4HalfPopulatedTimingFailsBeforeMutation(string table, string assignment)
    {
        using var connection = OpenPopulatedExactV4Projection();
        Execute(connection, $"UPDATE {table} SET {assignment} WHERE time_authority='recorded';");
        var before = WorkspaceDigest(connection);

        Assert.Equal("local_workspace_projection_semantic_validation_failed", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);

        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection,
            "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void DataBearingExactV3MigrationRollsBackBeforeStampAndRetriesWithStableCanonicalRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-v3-v4-{Guid.NewGuid():N}.sqlite");
        try
        {
            string[] first;
            using (var connection = OpenSessionDatabase(path))
            {
                Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-exact','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'active'); INSERT INTO session_events VALUES('event-exact','0198f5b8-0c00-7000-8000-000000000001','run-exact','copilot-sdk',NULL,NULL,NULL,'synthetic','source-exact','event','2026-08-24T00:00:01.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);");
                foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV3SchemaSql) Execute(connection, sql);
                Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',3);");

                Assert.Equal("injected_before_v4_stamp", Assert.Throws<InvalidOperationException>(() =>
                    LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch,
                        beforeV4Stamp: static () => throw new InvalidOperationException("injected_before_v4_stamp"))).Message);
                Assert.Equal(["3"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
                Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name IN ('local_workspace_execution_headers','local_workspace_nodes','local_workspace_node_edges','local_workspace_node_content_refs');"));

                LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
                first = Strings(connection, "SELECT execution_id||'|'||node_id||'|'||source_kind||'|'||source_identity FROM local_workspace_nodes ORDER BY node_id;");
            }

            using (var reopened = OpenExisting(path))
            {
                LocalWorkspaceProjectionSchemaV1.Ensure(reopened, DateTimeOffset.UnixEpoch);
                Assert.Equal(first, Strings(reopened, "SELECT execution_id||'|'||node_id||'|'||source_kind||'|'||source_identity FROM local_workspace_nodes ORDER BY node_id;"));
                Assert.Equal(["5"], Strings(reopened, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureMigratesDataBearingExactV4ToV5AndPreservesOnlyAuthorizedCompositeTombstones()
    {
        using var connection = OpenSessionDatabase();
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        Execute(connection, """
            INSERT INTO local_workspace_content_tombstones(source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision) VALUES
              ('event-a','tool_input','json_pointer','/tool_input',11,'2026-08-25T00:00:00.0000000+00:00','retention-a',7),
              ('event-b','subagent_input','json_pointer','/agent_id',12,'2026-08-25T00:00:00.0000000+00:00','retention-b',8);
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        Execute(connection, "INSERT INTO local_workspace_content_tombstones(store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision) VALUES('session_event_content','event-a','error_message','json_pointer','/error',13,'2026-08-25T00:00:00.0000000+00:00','retention-a',9);");

        Assert.Equal(["5"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal([
            "session_event_content:event-a:error_message:/error:9",
            "session_event_content:event-a:tool_input:/tool_input:7",
        ], Strings(connection,
            "SELECT store_kind||':'||source_item_id||':'||part||':'||json_pointer||':'||retention_revision FROM local_workspace_content_tombstones ORDER BY source_item_id,part;"));
        Assert.Equal(["store_kind", "source_item_id", "part"], Strings(connection,
            "SELECT name FROM pragma_table_info('local_workspace_content_tombstones') WHERE pk>0 ORDER BY pk;"));
        Assert.Empty(Strings(connection,
            "SELECT sql FROM sqlite_schema WHERE name IN ('local_workspace_content_tombstones','local_workspace_node_content_refs') AND instr(sql,'/agent_id')>0;"));
    }

    [Theory]
    [InlineData("after_validate")]
    [InlineData("after_rebuild")]
    [InlineData("after_backfill")]
    [InlineData("after_refresh")]
    [InlineData("after_semantic_validation")]
    [InlineData("before_stamp")]
    [InlineData("after_stamp_before_commit")]
    public void ExactV4MigrationFailureRollsBackAndRetryIsByteIdentical(string checkpoint)
    {
        using var connection = OpenSessionDatabase();
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('session-a','copilot-sdk','native-session-a','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk','native-run-a',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-start','session-a','run-a','copilot-sdk',NULL,'copilot-sdk-stream','source-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','not_captured'),
              ('event-complete','session-a','run-a','copilot-sdk','event-start','copilot-sdk-stream','source-complete','tool.execution_complete','2026-08-24T00:00:01.0000000+00:00','not_captured');
            INSERT INTO local_workspace_content_tombstones(source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision)
              VALUES('event-start','tool_input','json_pointer','/tool_input',11,'2026-08-25T00:00:00.0000000+00:00','retention-a',7);
            """);
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(
                connection, transaction, ["session-a"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }
        Assert.NotEmpty(Strings(connection, "SELECT execution_id FROM local_workspace_execution_headers;"));
        Assert.NotEmpty(Strings(connection, "SELECT node_id FROM local_workspace_nodes;"));
        Assert.NotEmpty(Strings(connection, "SELECT node_id FROM local_workspace_node_edges;"));
        var before = WorkspaceDigest(connection);

        var ensure = typeof(LocalWorkspaceProjectionSchemaV1).GetMethod(
            "Ensure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(SqliteConnection), typeof(DateTimeOffset), typeof(Action<string>)],
            modifiers: null);
        Assert.NotNull(ensure);
        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => ensure.Invoke(null,
            [connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"), (Action<string>)(name =>
            {
                if (name == checkpoint) throw new InvalidOperationException("injected_" + checkpoint);
            })]));
        Assert.Equal("injected_" + checkpoint, failure.InnerException!.Message);
        Assert.Equal(before, WorkspaceDigest(connection));
        Assert.Equal(["4"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var first = WorkspaceDigest(connection);
        Assert.Equal(["1:2:1"], Strings(connection, """
            SELECT (SELECT COUNT(*) FROM local_workspace_semantic_receipts)||':'||
                   (SELECT COUNT(*) FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts))||':'||
                   (SELECT COUNT(*) FROM local_workspace_tool_metadata);
            """));
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        Assert.Equal(first, WorkspaceDigest(connection));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void ExecutionOverflowIsPersistedAsBoundedEvidenceForDetailRejection(int count, bool succeeds)
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        for (var index = 0; index < count; index++)
            Execute(connection, $"INSERT INTO session_runs VALUES('run-{index:D4}','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal(count, Strings(connection, "SELECT execution_id FROM local_workspace_execution_headers;").Length);
        Assert.Equal(succeeds ? "0" : "1", Strings(connection, "SELECT EXISTS(SELECT 1 FROM local_workspace_execution_headers LIMIT 1 OFFSET 256);").Single());
    }

    [Fact]
    public void ExactSourceExecutionIdentityIsInvariantAcrossSessions()
    {
        var first = LocalWorkspaceProjectionStore.StableExecutionId("session-a", "session_run", "run-1");
        var second = LocalWorkspaceProjectionStore.StableExecutionId("session-b", "session_run", "run-1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void StableIdentitiesExcludeEveryForbiddenDisplayAndContextInput()
    {
        var execution = LocalWorkspaceProjectionStore.StableExecutionId("session-a", "session_run", "source-run-1");
        var node = LocalWorkspaceProjectionStore.StableNodeId("session_event", "source-event-1");

        var forbiddenContextChanges = new[]
        {
            (DisplayName: "renamed", Timestamp: "2099-12-31T23:59:59Z", Ordinal: 9999,
                NearbyRows: 0, Cardinality: 1, ToolName: "other-tool", SkillName: "other-skill"),
            (DisplayName: "", Timestamp: "invalid", Ordinal: -1,
                NearbyRows: 4096, Cardinality: 4097, ToolName: "", SkillName: ""),
        };

        Assert.All(forbiddenContextChanges, _ =>
        {
            Assert.Equal(execution,
                LocalWorkspaceProjectionStore.StableExecutionId("session-b", "session_run", "source-run-1"));
            Assert.Equal(node, LocalWorkspaceProjectionStore.StableNodeId("session_event", "source-event-1"));
        });
    }

    [Fact]
    public void CrossExecutionParentUsesOneDeterministicUnknownRelationGroup()
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown'); INSERT INTO session_runs VALUES('run-b','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown'); INSERT INTO session_events VALUES('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic','source-a','event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL); INSERT INTO session_events VALUES('event-b','0198f5b8-0c00-7000-8000-000000000001','run-b','copilot-sdk','event-a',NULL,NULL,'synthetic','source-b','event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,'explicit_link',NULL,NULL);");

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Equal(["unknown_relation_group"], Strings(connection, "SELECT kind FROM local_workspace_nodes WHERE source_kind='unknown_relation_group';"));
        Assert.Equal(["unknown"], Strings(connection, "SELECT relationship_authority FROM local_workspace_nodes WHERE source_identity='event-b';"));
        Assert.Equal(
            Strings(connection, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='unknown_relation_group';"),
            Strings(connection, "SELECT parent_node_id FROM local_workspace_nodes WHERE source_identity='event-b';"));
    }

    [Theory]
    [InlineData(4095, true)]
    [InlineData(4096, false)]
    public void NodeOverflowIsPersistedAsBoundedEvidenceForDetailRejection(int eventCount, bool succeeds)
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO session_events VALUES($id,'0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic',$id,'event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            for (var index = 0; index < eventCount; index++) { id.Value = $"event-{index:D4}"; command.ExecuteNonQuery(); }
            transaction.Commit();
        }

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal(eventCount + 1, Strings(connection, "SELECT node_id FROM local_workspace_nodes;").Length);
        Assert.Equal(succeeds ? "0" : "1", Strings(connection, "SELECT EXISTS(SELECT 1 FROM local_workspace_nodes LIMIT 1 OFFSET 4096);").Single());
    }

    [Fact]
    public void DetailProjectionRefreshStatementCountIsIndependentOfOneOrFourThousandNinetySixNodes()
    {
        Assert.Equal(MeasureRefreshStatementCount(0), MeasureRefreshStatementCount(4095));
    }

    [Fact]
    public void DetailProjectionNeverMaterializesPastLimitPlusOneForTenThousandEvents()
    {
        using var connection = OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO session_events VALUES($id,'0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic',$id,'event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            for (var index = 0; index < 10_000; index++) { id.Value = $"event-{index:D5}"; command.ExecuteNonQuery(); }
            transaction.Commit();
        }
        Execute(connection, "CREATE TRIGGER local_workspace_test_limit_before_insert BEFORE INSERT ON local_workspace_nodes WHEN (SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=NEW.session_id)>=4097 BEGIN SELECT RAISE(ABORT,'workspace_intermediate_node_overflow'); END;");

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(
                connection,
                transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"],
                DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }

        Assert.Equal(4097, Strings(connection, "SELECT node_id FROM local_workspace_nodes;").Length);
        Assert.Equal("1", Strings(connection, "SELECT node_overflow FROM local_workspace_sessions;").Single());
    }

    [Fact]
    public void TargetedRefreshBatchesTwoHundredOneSessionsUnderOnePinnedAuthorityAndSqliteLengthLimit()
    {
        using var connection = OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Execute(connection, """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<201)
            INSERT INTO sessions
            SELECT printf('0198f5b8-0c00-7000-8000-%012d',value),'active','partial',NULL,NULL,NULL,NULL,
                   '2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'
            FROM n;
            """);
        var ids = Strings(connection, "SELECT session_id FROM sessions ORDER BY session_id COLLATE BINARY;");
        var authority = new CountingGenerationAuthority();
        var previousLimit = sqlite3_limit(connection.Handle!.DangerousGetHandle(), 0, 7820);
        Assert.True(previousLimit > 7820);

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, ids, DateTimeOffset.UnixEpoch, authority);
            transaction.Commit();
        }

        Assert.Equal(201, Strings(connection, "SELECT session_id FROM local_workspace_sessions;").Length);
        Assert.Equal(1, authority.CaptureCount);
        Assert.Equal(1, authority.LeaseCount);
        Assert.Equal(1, authority.DisposeCount);
    }

    [Fact]
    public void NativeStatementObserverCountsEveryRepeatedTopLevelExecution()
    {
        using var connection = OpenSessionDatabase();
        using var observer = new NativeStatementExecutionObserver(connection);
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            for (var index = 0; index < 10_000; index++) command.ExecuteScalar();
        }
        Assert.Equal(10_000, observer.ExecutionCount);
    }

    [Fact]
    public void DetailLimitPlusOneBoundsUseExactSessionIndexesWithoutFullTableScans()
    {
        using var connection = OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {LocalWorkspaceSessionDetailSnapshotContributor.BoundsSql}";
        command.Parameters.AddWithValue("$session_id", "018f0000-0000-7000-8000-000000000001");
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, detail => detail.Contains("SEARCH local_workspace_execution_headers USING COVERING INDEX", StringComparison.Ordinal)
            && detail.Contains("session_id=?", StringComparison.Ordinal));
        Assert.Contains(plan, detail => detail.Contains("SEARCH local_workspace_nodes USING COVERING INDEX", StringComparison.Ordinal)
            && detail.Contains("session_id=?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.StartsWith("SCAN local_workspace_execution_headers", StringComparison.Ordinal)
            || detail.StartsWith("SCAN local_workspace_nodes", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactParentProofDoesNotUseSessionMatchKind()
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown'); INSERT INTO session_events VALUES('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic','source-a','event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL); INSERT INTO session_events VALUES('event-b','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','event-a',NULL,NULL,'synthetic','source-b','event','2026-08-24T00:00:01.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,'explicit_link',NULL,NULL);");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Assert.Equal(["exact"], Strings(connection, "SELECT relationship_authority FROM local_workspace_nodes WHERE source_identity='event-b';"));
    }

    [Fact]
    public void EnsureRejectsMalformedOwnedSchemaWithoutCommittingPartialInstall()
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "CREATE TABLE local_workspace_sessions(session_id TEXT PRIMARY KEY);");
        using var transaction = connection.BeginTransaction();

        Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, DateTimeOffset.UnixEpoch));
        transaction.Rollback();

        Assert.Empty(Strings(connection, "SELECT component FROM schema_version WHERE component='local_workspace_projection';"));
    }

    internal static SqliteConnection OpenSessionDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore.InitializeSchema(connection, transaction, DateTimeOffset.UnixEpoch);
        transaction.Commit();
        return connection;
    }

    private static SqliteConnection OpenSessionDatabase(string path)
    {
        var connection = OpenExisting(path);
        using var transaction = connection.BeginTransaction();
        CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore.InitializeSchema(connection, transaction, DateTimeOffset.UnixEpoch);
        transaction.Commit();
        return connection;
    }

    private static SqliteConnection OpenExisting(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string[] Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private static void InstallExactV4(SqliteConnection connection)
    {
        var property = typeof(LocalWorkspaceProjectionSchemaV1).GetProperty(
            "ExactV4SchemaSql",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(property);
        var statements = Assert.IsAssignableFrom<IEnumerable<string>>(property.GetValue(null));
        foreach (var sql in statements) Execute(connection, sql);
    }

    private static SqliteConnection OpenPopulatedCurrentProjection()
    {
        var connection = OpenSessionDatabase();
        Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk','native-run-a',NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('event-a','session-a','run-a','copilot-sdk','copilot-sdk-stream','source-a','event','2026-08-24T00:00:01.0000000+00:00','not_captured');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        return connection;
    }

    private static SqliteConnection OpenPopulatedExactV4Projection()
    {
        var connection = OpenSessionDatabase();
        Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk','native-run-a',NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00',NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('event-a','session-a','run-a','copilot-sdk','copilot-sdk-stream','source-a','event','2026-08-24T00:00:01.0000000+00:00','not_captured');
            """);
        InstallExactV4(connection);
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);");
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, transaction, ["session-a"], DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }
        return connection;
    }

    private static SqliteConnection OpenAvailableRetentionFixture(int projectionVersion = 4)
    {
        var connection = OpenSessionDatabase();
        var capturedAt = "2026-08-24T00:00:00.0000000+00:00";
        var expiresAt = "2026-09-01T00:00:00.0000000+00:00";
        var ownerToken = Enumerable.Repeat((byte)0x2a, 32).ToArray();
        Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run-a',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream','source-a','event','2026-08-24T00:00:00.0000000+00:00','available');
            """);
        using (var content = connection.CreateCommand())
        {
            content.CommandText = "INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000011','application/json','{}',$captured,$expires,$token);";
            content.Parameters.AddWithValue("$captured", capturedAt);
            content.Parameters.AddWithValue("$expires", expiresAt);
            content.Parameters.AddWithValue("$token", ownerToken);
            content.ExecuteNonQuery();
        }
        string storeInstanceId;
        using (var transaction = connection.BeginTransaction())
        {
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            using var store = connection.CreateCommand();
            store.Transaction = transaction;
            store.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;";
            storeInstanceId = Assert.IsType<string>(store.ExecuteScalar());
            var receipt = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOwnershipReceipt.CreateSession(new(
                storeInstanceId, "0198f5b8-0c00-7000-8000-000000000011", "application/json", capturedAt,
                DateTimeOffset.Parse(capturedAt).UtcTicks, expiresAt, DateTimeOffset.Parse(expiresAt).UtcTicks,
                "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010", "copilot-sdk-stream", "source-a", ownerToken));
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version)
                VALUES('item-event-a',$store,'session_event_content','0198f5b8-0c00-7000-8000-000000000011',1,$receipt,$captured,$expires,'raw-default-90d',1,'expiring',1,1);
                """;
            item.Parameters.AddWithValue("$store", storeInstanceId);
            item.Parameters.AddWithValue("$receipt", receipt);
            item.Parameters.AddWithValue("$captured", capturedAt);
            item.Parameters.AddWithValue("$expires", expiresAt);
            item.ExecuteNonQuery();
            transaction.Commit();
        }
        var statements = projectionVersion switch
        {
            1 => LocalWorkspaceProjectionSchemaV1.ExactV1SchemaSql,
            2 => LocalWorkspaceProjectionSchemaV1.ExactV2SchemaSql,
            3 => LocalWorkspaceProjectionSchemaV1.ExactV3SchemaSql,
            4 => LocalWorkspaceProjectionSchemaV1.ExactV4SchemaSql,
            _ => throw new ArgumentOutOfRangeException(nameof(projectionVersion)),
        };
        foreach (var sql in statements) Execute(connection, sql);
        Execute(connection, $"INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',{projectionVersion});");
        if (projectionVersion != 4) return connection;
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }
        Execute(connection, """
            UPDATE local_workspace_node_content_refs SET
              retention_item_id=(SELECT item_id FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'),
              retention_store_instance_id=(SELECT store_instance_id FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'),
              source_captured_at=(SELECT captured_at FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'),
              source_expires_at=(SELECT expires_at FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'),
              retention_revision=(SELECT revision FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'),
              retention_ownership_receipt=(SELECT ownership_receipt FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'),
              retention_owner_token=(SELECT retention_owner_token FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'),
              availability_state='available';
            """);
        Assert.Equal(["available"], Strings(connection, "SELECT availability_state FROM local_workspace_node_content_refs;"));
        return connection;
    }

    private static void InstallRawProjectionTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE raw_records(
          id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,
          resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,
          retention_owner_token BLOB);
        CREATE TABLE monitor_spans(
          raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,
          span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,
          tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,
          request_model TEXT,response_model TEXT,input_tokens INTEGER,
          output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,
          cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,
          error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,
          start_time TEXT,end_time TEXT,projected_at TEXT);
        """);

    private static string WorkspaceDigest(SqliteConnection connection)
    {
        var rows = new List<string>();
        rows.AddRange(Strings(connection,
            "SELECT type||':'||name||':'||COALESCE(sql,'') FROM sqlite_schema WHERE name LIKE 'local_workspace_%' ORDER BY type,name;"));
        rows.AddRange(Strings(connection,
            "SELECT component||':'||version FROM schema_version WHERE component='local_workspace_projection';"));
        foreach (var table in Strings(connection,
                     "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'local_workspace_%' ORDER BY name;"))
        {
            var columns = Strings(connection,
                $"SELECT name FROM pragma_table_xinfo('{table.Replace("'", "''", StringComparison.Ordinal)}') WHERE hidden=0 ORDER BY cid;");
            var projection = columns.Select(static column =>
                $"COALESCE(quote(\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\"),'NULL')").ToArray();
            rows.AddRange(Strings(connection, $"SELECT '{table.Replace("'", "''", StringComparison.Ordinal)}|'||{string.Join("||'|'||", projection)} FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY {string.Join(',', columns.Select(static column => $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""))};"));
        }
        return string.Join('\n', rows);
    }

    private static int MeasureRefreshStatementCount(int eventCount)
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO session_events VALUES($id,'0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic',$id,'event','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            for (var index = 0; index < eventCount; index++)
            {
                id.Value = $"event-{index:D4}";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        using var observer = new NativeStatementExecutionObserver(connection);
        {
            using var observedTransaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(
                connection,
                observedTransaction,
                ["0198f5b8-0c00-7000-8000-000000000001"],
                DateTimeOffset.UnixEpoch);
            observedTransaction.Rollback();
        }
        return observer.ExecutionCount;
    }

    private static string[] Strings(SqliteTransaction transaction, string sql)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    internal sealed class NativeStatementExecutionObserver : IDisposable
    {
        private const uint Statement = 0x01;
        private const uint Profile = 0x02;
        private readonly IntPtr database;
        private readonly TraceCallback callback;
        private readonly HashSet<IntPtr> topLevel = [];

        internal NativeStatementExecutionObserver(SqliteConnection connection)
        {
            database = connection.Handle.DangerousGetHandle();
            callback = Observe;
            Assert.Equal(0, sqlite3_trace_v2(database, Statement | Profile, callback, IntPtr.Zero));
        }

        internal int ExecutionCount { get; private set; }

        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr detail)
        {
            if (kind == Statement)
            {
                var sql = Marshal.PtrToStringUTF8(detail) ?? string.Empty;
                if (!sql.TrimStart().StartsWith("-- ", StringComparison.Ordinal)) topLevel.Add(statement);
            }
            else if (kind == Profile && topLevel.Remove(statement))
            {
                ExecutionCount++;
            }
            return 0;
        }

        public void Dispose()
        {
            sqlite3_trace_v2(database, 0, null, IntPtr.Zero);
            GC.KeepAlive(callback);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TraceCallback(uint kind, IntPtr context, IntPtr statement, IntPtr detail);

        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_trace_v2(IntPtr database, uint mask, TraceCallback? callback, IntPtr context);
    }

    private sealed class CountingGenerationAuthority : ISkillRegistryGenerationAuthority
    {
        private readonly Capture generation = new();
        internal int CaptureCount { get; private set; }
        internal int LeaseCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public ISkillRegistryGenerationCapture CaptureGeneration()
        {
            CaptureCount++;
            return generation;
        }

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            if (!ReferenceEquals(capture, generation))
            {
                lease = null;
                return false;
            }
            LeaseCount++;
            lease = new Lease(this);
            return true;
        }

        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) =>
            ReferenceEquals(capture, generation) && lease is Lease;

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple) => false;

        private sealed class Capture : ISkillRegistryGenerationCapture { }
        private sealed class Lease(CountingGenerationAuthority owner) : ISkillRegistryGenerationLease
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) owner.DisposeCount++;
            }
        }
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_limit(IntPtr database, int id, int newValue);
}

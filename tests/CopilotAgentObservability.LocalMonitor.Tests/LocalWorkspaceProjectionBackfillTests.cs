namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceProjectionBackfillTests
{
    private static readonly LocalWorkspaceProjectionTransactionParticipant StructuralParticipant =
        new(new StructuralRegistryAuthority());

    [Fact]
    public void RawRetentionSelectedValueUsesExactOneMiBBoundAndIgnoresHugeSibling()
    {
        using var connection = OpenRawRetentionFixture(1_048_576, siblingBytes: 1_048_577);

        Assert.Equal(
            ["0198f5b8-0c00-7000-8000-000000000011:instruction:1048576:available:0", "0198f5b8-0c00-7000-8000-000000000012:instruction:1048577:oversized:1"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT source_item_id||':'||part||':'||selected_utf8_bytes||':'||availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs ORDER BY source_item_id;"));
    }

    [Fact]
    public async Task RetentionExpiryPublicationUpdatesContentReferenceInsideOwningTransaction()
    {
        using var connection = OpenRawRetentionFixture(32);
        using var gate = new LocalWorkspacePublicationGate();
        await using var publication = await gate.AcquireReadAsync(CancellationToken.None);
        using (var transaction = connection.BeginTransaction())
        {
            using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText = "UPDATE session_event_content SET expires_at=$now WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE retention_items SET expires_at=$now WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';";
            mutation.Parameters.AddWithValue("$now", "2026-08-25T00:00:00.0000000+00:00");
            mutation.ExecuteNonQuery();
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            using var inside = connection.CreateCommand();
            inside.Transaction = transaction;
            inside.CommandText = "SELECT availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';";
            Assert.Equal("expired:1", inside.ExecuteScalar());
            transaction.Commit();
        }

        Assert.Equal(["expired:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"));
    }

    [Theory]
    [InlineData("store_instance", "invalid")]
    [InlineData("captured_at", "invalid")]
    [InlineData("expiry_equal_now", "expired")]
    [InlineData("catalog_receipt", "invalid")]
    [InlineData("owner_token", "invalid")]
    [InlineData("missing_item", "invalid")]
    [InlineData("missing_source", "not_captured")]
    [InlineData("deleted", "invalid")]
    [InlineData("read_denied", "read_denied")]
    [InlineData("error", "invalid")]
    [InlineData("tombstone", "invalid")]
    public void RawRetentionBindingDriftFailsClosedAndClearsCapability(string mutation, string expectedState)
    {
        using var connection = OpenRawRetentionFixture(32);
        var sql = mutation switch
        {
            "store_instance" => "PRAGMA foreign_keys=OFF; UPDATE retention_items SET store_instance_id='other-store' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; PRAGMA foreign_keys=ON;",
            "captured_at" => "UPDATE session_event_content SET captured_at='2026-08-24T00:00:01.0000000+00:00' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';",
            "expiry_equal_now" => "UPDATE session_event_content SET expires_at='2026-08-25T00:00:00.0000000+00:00' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE retention_items SET expires_at='2026-08-25T00:00:00.0000000+00:00' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "catalog_receipt" => "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "owner_token" => "DROP TRIGGER retention_session_event_content_token_immutable; UPDATE session_event_content SET retention_owner_token=zeroblob(32) WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';",
            "missing_item" => "DELETE FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "missing_source" => "DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';",
            "deleted" => "UPDATE retention_items SET state='deleted',deleted_at='2026-08-25T00:00:00.0000000+00:00' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "read_denied" => "UPDATE retention_items SET read_denied_at='2026-08-25T00:00:00.0000000+00:00' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "error" => "UPDATE retention_items SET error_code='delete_io_failed' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            "tombstone" => "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'2026-08-25T00:00:00.0000000+00:00','2026-08-25T00:00:00.0000000+00:00' FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        LocalWorkspaceProjectionSchemaTests.Execute(connection, sql);
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }

        Assert.Equal([$"{expectedState}:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"));
    }

    [Fact]
    public void RetentionOnlyDeletionCannotInferExactSemanticPartAfterPhysicalContentRemoval()
    {
        using var connection = OpenRawRetentionFixture(32);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE retention_items SET state='deleted',deleted_at='2026-08-25T00:00:00.0000000+00:00' WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';
            INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
              SELECT item_id,'2026-08-25T00:00:00.0000000+00:00','2026-08-25T00:00:00.0000000+00:00' FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';
            DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            """);
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }

        Assert.Equal(["event_content:not_captured:1:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT part||':'||availability_state||':'||(retention_owner_token IS NULL)||':'||(retention_item_id IS NOT NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"));
    }

    [Fact]
    public void DetailProjectionClassifiesAllSixExactRawCarriersWithCurrentRetentionAdmission()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            PRAGMA foreign_keys=OFF;
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-1','UserPromptSubmit','2026-08-24T00:00:00.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-2','PreToolUse','2026-08-24T00:00:01.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000013','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-3','PostToolUse','2026-08-24T00:00:02.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000014','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-4','PostToolUseFailure','2026-08-24T00:00:03.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000015','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-5','SubagentStart','2026-08-24T00:00:04.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000016','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','synthetic','source-6','event','2026-08-24T00:00:05.0000000+00:00','available');
            UPDATE session_events SET source_adapter='claude-code-hook',schema_fingerprint=printf('%064d',0);
            INSERT INTO session_event_content SELECT event_id,'application/json',CASE type
              WHEN 'UserPromptSubmit' THEN '{"prompt":"hello"}' WHEN 'PreToolUse' THEN '{"tool_input":{}}'
              WHEN 'PostToolUse' THEN '{"tool_response":{"ok":true}}' WHEN 'PostToolUseFailure' THEN '{"error":"failed"}'
              WHEN 'SubagentStart' THEN '{"agent_id":"agent-1"}' ELSE '{}' END,
              '2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32) FROM session_events;
            PRAGMA foreign_keys=ON;
            """);
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(
            ["error_message:available", "event_content:available", "event_content:available", "instruction:available", "tool_input:available", "tool_result:available"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT part||':'||availability_state FROM local_workspace_node_content_refs ORDER BY part;"));
        Assert.Equal(["/error", "/prompt", "/tool_input", "/tool_response", "whole", "whole"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT COALESCE(json_pointer,'whole') FROM local_workspace_node_content_refs ORDER BY COALESCE(json_pointer,'whole');"));

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';");
        using (var transaction = connection.BeginTransaction()) { StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z")); transaction.Commit(); }
        Assert.Equal(["invalid:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"));
    }

    [Fact]
    public void PermissionRequestProjectsAsPermissionWithItsExactSourceReference()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','claude-code',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('permission-event','session-a','run-a','claude-code','claude-code-hook','permission-source','PermissionRequest','2026-08-24T00:00:00.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["permission:session_event:permission-event:permission-event"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT node.kind||':'||reference.source_kind||':'||reference.source_identity||':'||reference.event_id
            FROM local_workspace_nodes node
            JOIN local_workspace_node_source_references reference ON reference.node_id=node.node_id
            WHERE node.source_identity='permission-event';
            """));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_semantic_receipts;"));
    }

    [Fact]
    public void ClaudeHookWellFormedVersionAndFingerprintRemainRawWithoutSemanticPromotion()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('0198f5b8-0c00-7000-8000-000000000001','claude-code','native-session-a','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run-a',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','claude-code-hook','0198f5b8-0c00-7000-8000-000000000021','PreToolUse','2026-08-24T00:00:00.0000000+00:00','available','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1'),
              ('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','claude-code-hook','0198f5b8-0c00-7000-8000-000000000022','PostToolUse','2026-08-24T00:00:01.0000000+00:00','available','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1');
            INSERT INTO session_event_content VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"raw-carrier-must-not-persist"}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32)),
              ('0198f5b8-0c00-7000-8000-000000000012','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"raw-carrier-must-not-persist","tool_response":{"ok":true}}','2026-08-24T00:00:01.0000000+00:00','2026-09-01T00:00:01.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["event", "event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event' ORDER BY source_identity;"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_semantic_receipts;"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind IN ('semantic_tool','semantic_subagent');"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT node_id FROM local_workspace_tool_metadata
            UNION ALL SELECT node_id FROM local_workspace_subagent_lifecycle
            UNION ALL SELECT content.node_id FROM local_workspace_node_content_refs content
              JOIN local_workspace_nodes node ON node.node_id=content.node_id
              WHERE node.source_kind IN ('semantic_tool','semantic_subagent');
            """));
    }

    [Fact]
    public void ExactRawHookStartsCountObservedActivityWithoutCreatingSemanticObjects()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('session-hook','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-hook','session-hook','claude-code','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version) VALUES
              ('hook-tool','session-hook','run-hook','claude-code','claude-code-hook','tool-source','PreToolUse','2026-08-24T00:00:00.0000000+00:00','not_captured','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1'),
              ('hook-agent','session-hook','run-hook','claude-code','claude-code-hook','agent-source','SubagentStart','2026-08-24T00:00:01.0000000+00:00','not_captured','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["subagent:recorded:1", "tool:recorded:1"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT kind||':'||state||':'||COALESCE(CAST(count AS TEXT),'null')
                FROM local_workspace_session_activity
                WHERE session_id='session-hook' AND kind IN ('tool','subagent') ORDER BY kind;
                """));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind IN ('semantic_tool','semantic_subagent');"));
    }

    [Theory]
    [InlineData("copilot-sdk", "claude-code-hook", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("claude-code", "synthetic", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("claude-code", "claude-code-hook", null, null)]
    public void NonAuthoritativeHookShapedRowsDoNotRecordActivity(
        string surface, string adapter, string? sourceVersion, string? fingerprint)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('session-hook','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-hook','session-hook','{surface}','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version)
              VALUES('hook-tool','session-hook','run-hook','{surface}','{adapter}','tool-source','PreToolUse','2026-08-24T00:00:00.0000000+00:00','not_captured',
                {(sourceVersion is null ? "NULL" : $"'{sourceVersion}'")},'hook-adapter-v1',{(fingerprint is null ? "NULL" : $"'{fingerprint}'")},'hook-normalization-v1');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.DoesNotContain("recorded", LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state FROM local_workspace_session_activity WHERE session_id='session-hook' AND kind='tool';"));
        Assert.DoesNotContain("recorded", LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT tool_activity_state FROM local_workspace_execution_headers WHERE session_id='session-hook';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_identity='hook-tool';"));
    }

    [Theory]
    [InlineData("PreToolUse", "2.1.145", null, "run-a")]
    [InlineData("PreToolUse", null, "0000000000000000000000000000000000000000000000000000000000000000", "run-a")]
    [InlineData("PreToolUse", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000", "run-a")]
    [InlineData("PreToolUse", null, null, "run-a")]
    [InlineData("PreToolUse", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000", null)]
    [InlineData("SubagentStart", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000", "run-a")]
    [InlineData("SubagentStop", "2.1.145", "0000000000000000000000000000000000000000000000000000000000000000", "run-a")]
    public void ClaudeHookAuthorityShapesRemainOrdinaryRawEvidenceWithoutSemanticObject(
        string eventType,
        string? sourceApplicationVersion,
        string? schemaFingerprint,
        string? runId)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('0198f5b8-0c00-7000-8000-000000000001','claude-code','native-session','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001',{(runId is null ? "NULL" : "'0198f5b8-0c00-7000-8000-000000000010'")},'claude-code','claude-code-hook','source-a','{eventType}','2026-08-24T00:00:00.0000000+00:00','available',
                     {(sourceApplicationVersion is null ? "NULL" : $"'{sourceApplicationVersion}'")},'hook-adapter-v1',
                     {(schemaFingerprint is null ? "NULL" : $"'{schemaFingerprint}'")},'hook-normalization-v1');
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000011','application/json',json_object('tool_name','Read','tool_input',json(char(123)||char(125)),'tool_use_id','carrier'),'2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_semantic_receipts;"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind IN ('semantic_tool','semantic_subagent');"));
        Assert.Equal(runId is null ? [] : ["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
        Assert.Equal(["0198f5b8-0c00-7000-8000-000000000011"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT event_id FROM session_events;"));
        var exactObservedStart = eventType is "PreToolUse" or "SubagentStart"
            && (sourceApplicationVersion is not null || schemaFingerprint is not null);
        Assert.Equal(exactObservedStart ? ["recorded:1"] : ["not_observed:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                $"SELECT state||':'||COALESCE(CAST(count AS TEXT),'null') FROM local_workspace_session_activity WHERE kind='{(eventType.StartsWith("Subagent", StringComparison.Ordinal) ? "subagent" : "tool")}';"));
    }

    [Fact]
    public void BackupValidationRejectsLegacySyntaxOnlyClaudeSemanticGraph()
    {
        using var connection = OpenSdkToolFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run", "sdk-start");
        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "UPDATE local_workspace_semantic_receipts SET source_family='claude_hook';");
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
                connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:00Z"))).Message);
    }

    [Fact]
    public void SessionSdkToolUsesOnlyExactAuthoredParentEventInsideTheSameRun()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk',NULL,'copilot-sdk-stream','sdk-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','not_captured'),
              ('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','0198f5b8-0c00-7000-8000-000000000011','copilot-sdk-stream','sdk-complete','tool.execution_complete','2026-08-24T00:00:01.0000000+00:00','not_captured'),
              ('0198f5b8-0c00-7000-8000-000000000013','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk',NULL,'copilot-sdk-stream','sdk-orphan','tool.execution_complete','2026-08-24T00:00:02.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["completed:unknown:2"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT n.lifecycle||':'||n.status||':'||COUNT(r.source_ordinal) FROM local_workspace_nodes n
            JOIN local_workspace_node_source_references r ON r.node_id=n.node_id
            WHERE n.source_kind='semantic_tool' GROUP BY n.node_id;
            """));
        Assert.Equal(["recorded:recorded:not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT started_state||':'||completed_state||':'||failed_state FROM local_workspace_tool_metadata;"));
        Assert.Equal(["event", "event", "event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event' ORDER BY source_identity;"));
    }

    [Fact]
    public void StandaloneSessionSdkToolStartRemainsAnOrdinaryEvent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream','sdk-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
    }

    [Fact]
    public void SessionSdkToolRefreshRejectsAStalePreservedNodeWithoutItsExactCompletionParentEdge()
    {
        using var connection = OpenSdkToolFixture(
            "session-a", "run-a", "event-start", "native-run", "sdk-source-start");
        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "DELETE FROM session_events WHERE event_id='event-start-complete';");

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(
                connection, transaction, ["session-a"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "recorded")]
    [InlineData("0123456789abcdef", "not_observed")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "not_observed")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "not_observed")]
    public void OtelToolUsesExactSpanIdentityAndAcceptsOnlyLowercaseSha256McpServerIdentity(
        string mcpServerHash,
        string expectedMcpServerIdentityState)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,start_time,end_time)
              VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',NULL,0,'execute_tool','tool_call','Read','Read','{{mcpServerHash}}','ok','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["otel:otel_span:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT r.source_family||':'||r.scope_kind||':'||COUNT(s.source_ordinal)
            FROM local_workspace_semantic_receipts r JOIN local_workspace_node_source_references s ON s.node_id=r.node_id
            WHERE r.semantic_kind='tool' GROUP BY r.node_id;
            """));
        Assert.Equal([$"{expectedMcpServerIdentityState}:{(expectedMcpServerIdentityState == "recorded" ? mcpServerHash : string.Empty)}:source_unsupported:recorded:Read"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT mcp_server_identity_state||':'||COALESCE(mcp_server_identity,'')||':'||mcp_server_name_state||':'||mcp_tool_name_state||':'||mcp_tool_name FROM local_workspace_tool_metadata;"));
        Assert.Equal(["recorded:recorded:not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT started_state||':'||completed_state||':'||failed_state FROM local_workspace_tool_metadata;"));
        Assert.Equal(["recorded:1:recorded:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT header.tool_activity_state||':'||COALESCE(CAST(header.tool_activity_count AS TEXT),'null')||':'||root.tool_activity_state||':'||COALESCE(CAST(root.tool_activity_count AS TEXT),'null')
            FROM local_workspace_execution_headers header
            JOIN local_workspace_nodes root ON root.execution_id=header.execution_id AND root.source_kind='execution_root';
            """));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "bbbbbbbbbbbbbbbb")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "BBBBBBBBBBBBBBBB")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbb")]
    public void OtelToolRejectsNonCanonicalTraceOrSpanIdentity(string traceId, string spanId)
    {
        using var connection = OpenOtelToolAdmissionFixture(traceId, spanId, duplicateSpanOwner: false);

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
        Assert.Equal(["not_observed:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state||':'||COALESCE(CAST(count AS TEXT),'null') FROM local_workspace_session_activity WHERE kind='tool';"));
    }

    [Fact]
    public void OtelToolRejectsConflictingDuplicateNormalizedSpanOwner()
    {
        using var connection = OpenOtelToolAdmissionFixture(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", duplicateSpanOwner: true);

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["not_observed:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state||':'||COALESCE(CAST(count AS TEXT),'null') FROM local_workspace_session_activity WHERE kind='tool';"));
    }

    [Fact]
    public void OtelToolRefreshRejectsAConflictingCaseVariantOfTheNormalizedSpanOwner()
    {
        using var connection = OpenOtelToolAdmissionFixture(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", duplicateSpanOwner: false);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO raw_records VALUES(2,'otlp','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','2026-08-24T00:00:01.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,status,start_time,end_time)
              VALUES(2,'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','BBBBBBBBBBBBBBBB',NULL,0,'execute_tool','tool_call','Other','ok','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');
            """);

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["not_observed:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state||':'||COALESCE(CAST(count AS TEXT),'null') FROM local_workspace_session_activity WHERE kind='tool';"));
    }

    [Fact]
    public void OtelToolRefreshRequiresTheExactOtelSpanEventType()
    {
        using var connection = OpenOtelToolAdmissionFixture(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", duplicateSpanOwner: false);
        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "UPDATE session_events SET type='event' WHERE source_adapter='otel-exact';");

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
        Assert.Equal(["not_observed:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state||':'||COALESCE(CAST(count AS TEXT),'null') FROM local_workspace_session_activity WHERE kind='tool';"));
    }

    [Fact]
    public void OtelToolDoesNotExposeGenericToolNameAsMcpToolIdentity()
    {
        using var connection = OpenOtelToolAdmissionFixture(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", duplicateSpanOwner: false);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE monitor_spans SET tool_name='GenericTool',mcp_tool_name=NULL
            WHERE trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';
            """);

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Equal(["recorded:GenericTool:not_observed:"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node.name_state||':'||COALESCE(node.name_text,'')||':'||
                       metadata.mcp_tool_name_state||':'||COALESCE(metadata.mcp_tool_name,'')
                FROM local_workspace_nodes node
                JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
                WHERE node.source_kind='semantic_tool';
                """));
    }

    [Fact]
    public void OtelToolWithUnresolvedExactParentUsesTheExecutionUnknownRelationGroup()
    {
        using var connection = OpenOtelToolAdmissionFixture(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", duplicateSpanOwner: false);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE monitor_spans SET parent_span_id='cccccccccccccccc'
            WHERE trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND span_id='bbbbbbbbbbbbbbbb';
            """);

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Equal(["unknown:unknown_relation_group:0:not_observed:"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT tool.relationship_authority||':'||parent.source_kind||':'||
                       (SELECT COUNT(*) FROM local_workspace_node_edges edge WHERE edge.node_id=tool.node_id)||':'||
                       metadata.caller_state||':'||COALESCE(metadata.caller_node_id,'')
                FROM local_workspace_nodes tool
                JOIN local_workspace_nodes parent ON parent.node_id=tool.parent_node_id
                JOIN local_workspace_tool_metadata metadata ON metadata.node_id=tool.node_id
                WHERE tool.source_kind='semantic_tool';
                """));
    }

    [Theory]
    [InlineData("error", null, "error", "2026-08-24T00:00:00.0000000+00:00", "2026-08-24T00:00:01.0000000+00:00", "failed", "failed", "recorded", "recorded", "not_observed", "recorded", 1000)]
    [InlineData("ok", "must-not-control-status", "tool_call", "2026-08-24T00:00:00.0000000+00:00", "2026-08-24T00:00:01.0000000+00:00", "completed", "completed", "recorded", "recorded", "recorded", "not_observed", 1000)]
    [InlineData(null, "must-not-control-status", "tool_call", "2026-08-24T00:00:00.0000000+00:00", "2026-08-24T00:00:01.0000000+00:00", "completed", "unknown", "recorded", "recorded", "recorded", "not_observed", 1000)]
    [InlineData(null, "must-not-control-status", "tool_call", "2026-08-24T00:00:00.0000000+00:00", null, "started", "unknown", "recorded", "recorded", "not_observed", "not_observed", null)]
    [InlineData(null, null, "tool_call", null, null, "unknown", "unknown", "missing", "not_observed", "not_observed", "not_observed", null)]
    [InlineData(null, null, "tool_call", "invalid", "2026-08-24T00:00:01.0000000+00:00", "completed", "unknown", "invalid", "not_observed", "recorded", "not_observed", null)]
    [InlineData("failed", "must-not-control-status", "error", "2026-08-24T00:00:00.0000000+00:00", "2026-08-24T00:00:01.0000000+00:00", "completed", "unknown", "recorded", "recorded", "recorded", "not_observed", 1000)]
    public void OtelToolUsesExactSpanTimingAndStatusCodeOnlyLifecycle(
        string? status,
        string? errorType,
        string category,
        string? startTime,
        string? endTime,
        string expectedLifecycle,
        string expectedStatus,
        string expectedTimeAuthority,
        string expectedStarted,
        string expectedCompleted,
        string expectedFailed,
        int? expectedDuration)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T09:00:00.0000000+00:00','not_captured');
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,status,error_type,start_time,end_time)
              VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',NULL,0,'execute_tool','{{category}}','Read',
                {{(status is null ? "NULL" : $"'{status}'")}},{{(errorType is null ? "NULL" : $"'{errorType}'")}},
                {{(startTime is null ? "NULL" : $"'{startTime}'")}},{{(endTime is null ? "NULL" : $"'{endTime}'")}});
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        var expectedStartTicks = expectedTimeAuthority == "recorded"
            ? DateTimeOffset.Parse(startTime!).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        var expectedEndTicks = expectedTimeAuthority == "recorded" && endTime is not null
            ? DateTimeOffset.Parse(endTime).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        Assert.Equal(
            [$"{expectedLifecycle}:{expectedStatus}:{expectedTimeAuthority}:{expectedStartTicks}:{expectedEndTicks}:{expectedDuration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}:{expectedStarted}:{expectedCompleted}:{expectedFailed}"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT node.lifecycle||':'||node.status||':'||node.time_authority||':'||COALESCE(CAST(node.start_utc_ticks AS TEXT),'')||':'||
                       COALESCE(CAST(node.end_utc_ticks AS TEXT),'')||':'||COALESCE(CAST(node.duration_ms AS TEXT),'')||':'||
                       metadata.started_state||':'||metadata.completed_state||':'||metadata.failed_state
                FROM local_workspace_nodes node JOIN local_workspace_tool_metadata metadata ON metadata.node_id=node.node_id
                WHERE node.source_kind='semantic_tool';
                """));
    }

    [Fact]
    public void OtelGenericErrorWithoutExactToolOperationRemainsOrdinaryEvidence()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name,status,start_time,end_time)
              VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','error','Read','error','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
        Assert.Equal(["event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event';"));
    }

    [Fact]
    public void SessionSdkSubagentPersistsFiveLifecycleFactsIndependently()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-session','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-child','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-child',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-selected','0198f5b8-0c00-7000-8000-000000000001','run-child','copilot-sdk','copilot-sdk-stream','source-selected','subagent.selected','2026-08-24T00:00:00.0000000+00:00','not_captured'),
              ('event-started','0198f5b8-0c00-7000-8000-000000000001','run-child','copilot-sdk','copilot-sdk-stream','source-started','subagent.started','2026-08-24T00:00:01.0000000+00:00','not_captured'),
              ('event-completed','0198f5b8-0c00-7000-8000-000000000001','run-child','copilot-sdk','copilot-sdk-stream','source-completed','subagent.completed','2026-08-24T00:00:02.0000000+00:00','not_captured'),
              ('event-failed','0198f5b8-0c00-7000-8000-000000000001','run-child','copilot-sdk','copilot-sdk-stream','source-failed','subagent.failed','2026-08-24T00:00:03.0000000+00:00','not_captured'),
              ('event-deselected','0198f5b8-0c00-7000-8000-000000000001','run-child','copilot-sdk','copilot-sdk-stream','source-deselected','subagent.deselected','2026-08-24T00:00:04.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Contains("local_workspace_subagent_lifecycle", LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'local_workspace_%';"));
        Assert.Equal(["recorded:recorded:inconsistent:inconsistent:recorded:5"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT l.selected_state||':'||l.started_state||':'||l.completed_state||':'||l.failed_state||':'||l.deselected_state||':'||
                   (SELECT COUNT(*) FROM local_workspace_node_source_references r WHERE r.node_id=l.node_id)
            FROM local_workspace_subagent_lifecycle l;
            """));
        Assert.Equal(["event", "event", "error", "event", "event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event' ORDER BY source_identity;"));
        Assert.Single(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent' AND kind='subagent';"));
    }

    [Fact]
    public void SessionSdkSubagentUsesEventSpecificChildRunInsideExactNativeSessionScope()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES
              ('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'),
              ('session-b','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES
              ('session-a','copilot-sdk','native-session-a','native','2026-08-24T00:00:00.0000000+00:00'),
              ('session-b','copilot-sdk','native-session-b','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES
              ('run-a1','session-a','copilot-sdk','child-1',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('run-a2','session-a','copilot-sdk','child-2',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('run-b1','session-b','copilot-sdk','child-1',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-a1-selected','session-a','run-a1','copilot-sdk','copilot-sdk-stream','source-a1-selected','subagent.selected','2026-08-24T00:00:00.0000000+00:00','not_captured'),
              ('event-a1-completed','session-a','run-a1','copilot-sdk','copilot-sdk-stream','source-a1-completed','subagent.completed','2026-08-24T00:00:01.0000000+00:00','not_captured'),
              ('event-a2-started','session-a','run-a2','copilot-sdk','copilot-sdk-stream','source-a2-started','subagent.started','2026-08-24T00:00:02.0000000+00:00','not_captured'),
              ('event-b1-selected','session-b','run-b1','copilot-sdk','copilot-sdk-stream','source-b1-selected','subagent.selected','2026-08-24T00:00:03.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(3, LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent';").Length);
        Assert.Equal(["1:1", "1:1", "1:2"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT COUNT(DISTINCT event.session_id)||':'||COUNT(*)
            FROM local_workspace_node_source_references reference
            JOIN local_workspace_nodes node ON node.node_id=reference.node_id
            JOIN session_events event ON event.event_id=reference.event_id
            WHERE node.source_kind='semantic_subagent'
            GROUP BY node.node_id ORDER BY COUNT(*),node.node_id;
            """));
    }

    [Fact]
    public void RetentionRefreshRecomputesConflictingOverflowAfterPreservedAndNewReferenceMerge()
    {
        using var connection = OpenSdkToolContentFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000999", "native-run");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE retention_items SET read_denied_at='2026-08-25T00:00:00.0000000+00:00',revision=2
              WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000999';
            """);
        var terminalIds = new List<string>();
        for (var index = 0; index < 16; index++)
        {
            var eventId = $"0198f5b8-0c00-7000-8000-{index + 12:D12}";
            terminalIds.Add(eventId);
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$$"""
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('{{{eventId}}}','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','0198f5b8-0c00-7000-8000-000000000999','copilot-sdk-stream','source-complete-{{{index:D2}}}','tool.execution_complete','2026-08-24T00:01:{{{index:D2}}}.0000000+00:00','available');
                INSERT INTO session_event_content VALUES('{{{eventId}}}','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"stable-carrier","tool_response":{"ok":true}}','2026-08-24T00:01:{{{index:D2}}}.0000000+00:00','2026-09-01T00:01:{{{index:D2}}}.0000000+00:00',randomblob(32));
                """);
        }
        InstallCurrentRetentionRows(connection, [.. terminalIds]);

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        var first = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT receipt.authority_receipt||':'||metadata.started_state||':'||metadata.completed_state||':'||metadata.failed_state||':'||node.lifecycle||':'||node.status||':'||COUNT(reference.source_ordinal)
            FROM local_workspace_tool_metadata metadata
            JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=metadata.node_id
            JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
            JOIN local_workspace_node_source_references reference ON reference.node_id=metadata.node_id
            GROUP BY metadata.node_id;
            """);
        Assert.Single(first);
        Assert.EndsWith(":inconsistent:inconsistent:inconsistent:unknown:unknown:16", first[0], StringComparison.Ordinal);
        var firstReferences = LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT event_id||':'||revision_input FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts) ORDER BY source_ordinal;");
        Assert.Contains(firstReferences, reference =>
            reference.StartsWith("0198f5b8-0c00-7000-8000-000000000999:", StringComparison.Ordinal));

        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:02Z"));
            transaction.Commit();
        }

        Assert.Equal(first, LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT receipt.authority_receipt||':'||metadata.started_state||':'||metadata.completed_state||':'||metadata.failed_state||':'||node.lifecycle||':'||node.status||':'||COUNT(reference.source_ordinal)
            FROM local_workspace_tool_metadata metadata
            JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=metadata.node_id
            JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
            JOIN local_workspace_node_source_references reference ON reference.node_id=metadata.node_id
            GROUP BY metadata.node_id;
            """));
        Assert.Equal(firstReferences, LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT event_id||':'||revision_input FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts) ORDER BY source_ordinal;"));
        using var validation = connection.BeginTransaction(deferred: true);
        CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
            connection, validation, DateTimeOffset.Parse("2026-08-25T00:00:02Z"));
    }

    [Fact]
    public void SubagentReferenceOverflowAndContradictoryLifecycleRemainExplicitlyInconsistent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('session-a','copilot-sdk','native-session-a','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-child','session-a','copilot-sdk','child-1',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            """);
        for (var index = 0; index < 17; index++)
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $"INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('event-selected-{index:D2}','session-a','run-child','copilot-sdk','copilot-sdk-stream','source-selected-{index:D2}','subagent.selected','2026-08-24T00:00:{index:D2}.0000000+00:00','not_captured');");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-completed','session-a','run-child','copilot-sdk','copilot-sdk-stream','source-completed','subagent.completed','2026-08-24T00:01:00.0000000+00:00','not_captured'),
              ('event-failed','session-a','run-child','copilot-sdk','copilot-sdk-stream','source-failed','subagent.failed','2026-08-24T00:01:01.0000000+00:00','not_captured');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["inconsistent:not_observed:inconsistent:inconsistent:not_observed:16"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT lifecycle.selected_state||':'||lifecycle.started_state||':'||lifecycle.completed_state||':'||
                       lifecycle.failed_state||':'||lifecycle.deselected_state||':'||COUNT(reference.source_ordinal)
                FROM local_workspace_subagent_lifecycle lifecycle
                JOIN local_workspace_node_source_references reference ON reference.node_id=lifecycle.node_id
                GROUP BY lifecycle.node_id;
                """));
    }

    [Fact]
    public void CompetingExactToolInputsPersistInvalidAvailabilityWithoutSelectingAWinner()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run-a',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk',NULL,'copilot-sdk-stream','source-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','available'),
              ('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','0198f5b8-0c00-7000-8000-000000000011','copilot-sdk-stream','source-complete','tool.execution_complete','2026-08-24T00:00:01.0000000+00:00','available');
            INSERT INTO session_event_content VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','application/json','{"tool_name":"Read","tool_input":{"value":1},"tool_use_id":"carrier"}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32)),
              ('0198f5b8-0c00-7000-8000-000000000012','application/json','{"tool_name":"Read","tool_input":{"value":2},"tool_use_id":"carrier","tool_response":{"ok":true}}','2026-08-24T00:00:01.0000000+00:00','2026-09-01T00:00:01.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["recorded:recorded:event_content:invalid"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT metadata.started_state||':'||metadata.completed_state||':'||content.part||':'||content.availability_state
            FROM local_workspace_tool_metadata metadata
            JOIN local_workspace_node_content_refs content ON content.node_id=metadata.node_id
            WHERE content.part='event_content';
            """));
    }

    [Fact]
    public void SessionSdkToolIdentityUsesNativeRunAndSourceEventInsteadOfLocalIds()
    {
        using var first = OpenSdkToolFixture("session-a", "run-a", "event-a", "native-run", "sdk-source-start");
        using var second = OpenSdkToolFixture("session-b", "run-b", "event-b", "native-run", "sdk-source-start");

        Assert.Equal(
            LocalWorkspaceProjectionSchemaTests.Strings(first, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"),
            LocalWorkspaceProjectionSchemaTests.Strings(second, "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool';"));
    }

    [Theory]
    [InlineData("copilot-sdk", false)]
    [InlineData("claude-code", true)]
    public void ClaudeHookSubagentRejectsWrongRunSurfaceAndAmbiguousNativeRun(string runSurface, bool duplicateNativeRun)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('session-a','claude-code','native-session','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','{runSurface}','native-child',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            {(duplicateNativeRun ? "INSERT INTO session_runs VALUES('run-b','session-a','claude-code','native-child',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');" : string.Empty)}
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version)
              VALUES('event-a','session-a','run-a','claude-code','claude-code-hook','source-a','SubagentStart','2026-08-24T00:00:00.0000000+00:00','not_captured','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_subagent';"));
    }

    [Fact]
    public void ConcurrentSameNameClaudeToolsRemainRawWithoutSemanticCarrierInference()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('0198f5b8-0c00-7000-8000-000000000001','claude-code','native-session','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version) VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','claude-code-hook','source-a','PreToolUse','2026-08-24T00:00:00.0000000+00:00','available','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1'),
              ('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','claude-code-hook','source-b','PreToolUse','2026-08-24T00:00:00.0000000+00:00','available','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1');
            INSERT INTO session_event_content VALUES
              ('0198f5b8-0c00-7000-8000-000000000011','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"carrier-a"}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32)),
              ('0198f5b8-0c00-7000-8000-000000000012','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"carrier-b"}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE source_kind='semantic_tool' ORDER BY node_id;"));
        Assert.Equal(["event", "event"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind FROM local_workspace_nodes WHERE source_kind='session_event' ORDER BY source_identity;"));
    }

    [Fact]
    public void DuplicateLifecycleAndInputFactsRemainBoundedAndFailClosedWithoutSelectingAContentWinner()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000999','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream','sdk-source-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','available');
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000999','application/json','{"tool_input":{}}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        for (var index = 0; index < 17; index++)
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('0198f5b8-0c00-7000-8000-{{index + 100:D12}}','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','0198f5b8-0c00-7000-8000-000000000999','copilot-sdk-stream','sdk-source-complete-{{index:D2}}','tool.execution_complete','2026-08-24T00:00:{{index:D2}}.0000000+00:00','available');
                INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-{{index + 100:D12}}','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"same-carrier"}','2026-08-24T00:00:{{index:D2}}.0000000+00:00','2026-09-01T00:00:{{index:D2}}.0000000+00:00',randomblob(32));
                """);
        }
        InstallCurrentRetentionRows(connection);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["16:inconsistent:inconsistent:1:invalid"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT (SELECT COUNT(*) FROM local_workspace_node_source_references r WHERE r.node_id=m.node_id)||':'||m.started_state||':'||
                   m.completed_state||':'||
                   (SELECT COUNT(*) FROM local_workspace_node_content_refs c WHERE c.node_id=m.node_id AND c.part='event_content')||':'||
                   (SELECT availability_state FROM local_workspace_node_content_refs c WHERE c.node_id=m.node_id AND c.part='event_content')
            FROM local_workspace_tool_metadata m;
            """));
        Assert.Contains("0198f5b8-0c00-7000-8000-000000000999", LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT event_id FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_tool_metadata);"));
    }

    [Fact]
    public void UnknownRelationGroupIsBoundedBeforeTheFourThousandNinetyEighthNode()
    {
        using var connection = OpenNodeCapacityFixture("unknown_relation");

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, transaction, ["session-a"], DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }

        AssertNodeCapacityClosed(connection, "unknown_relation_group");
        Assert.Equal(["1:unknown"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT (parent_node_id IS NULL)||':'||relationship_authority
            FROM local_workspace_nodes WHERE source_kind='session_event' AND source_identity='event-0001';
            """));
    }

    [Fact]
    public void SemanticToolIsBoundedBeforeTheFourThousandNinetyEighthNode()
    {
        using var connection = OpenNodeCapacityFixture("semantic_tool");

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, transaction, ["session-a"], DateTimeOffset.UnixEpoch);
            transaction.Commit();
        }

        AssertNodeCapacityClosed(connection, "semantic_tool");
    }

    [Fact]
    public void SkillInvocationIsBoundedBeforeTheFourThousandNinetyEighthNode()
    {
        using var connection = OpenNodeCapacityFixture("skill");
        using (var seed = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshSessionsStructural(connection, seed, ["session-a"], DateTimeOffset.UnixEpoch);
            seed.Commit();
        }
        var invocation = new SkillProjectionCanonicalInvocation(
            "capacity-skill", "session-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb",
            "otel-source", "skill", "sdk-source", "skill", null,
            "session_run", "run-a", "event-skill-otel", "event-skill-sdk");
        var projections = new Dictionary<string, SkillProjectionCurrentInvocationProjection>(StringComparer.Ordinal)
        {
            ["session-a"] = new("current", [invocation]),
        };
        var method = typeof(LocalWorkspaceProjectionStore).GetMethod("RefreshDetailProjection",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        using (var transaction = connection.BeginTransaction())
        {
            method.Invoke(null, [connection, transaction, new[] { "session-a" }, projections,
                DateTimeOffset.UnixEpoch, "registry"]);
            transaction.Commit();
        }

        AssertNodeCapacityClosed(connection, "skill_invocation");
    }

    [Theory]
    [InlineData("read_denied")]
    [InlineData("deleted")]
    public void RetentionUnavailabilityPreservesSemanticReceiptIdentityAndExactReferences(string state)
    {
        using var connection = OpenSdkToolContentFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run");
        var before = LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT n.node_id||':'||r.carrier_digest||':'||(SELECT COUNT(*) FROM local_workspace_node_source_references x WHERE x.node_id=n.node_id)
            FROM local_workspace_nodes n JOIN local_workspace_semantic_receipts r ON r.node_id=n.node_id;
            """);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, state == "read_denied"
            ? "UPDATE retention_items SET read_denied_at='2026-08-25T00:00:00.0000000+00:00',revision=2 WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"
            : "UPDATE retention_items SET state='deleted',deleted_at='2026-08-25T00:00:00.0000000+00:00',revision=2 WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'2026-08-25T00:00:00.0000000+00:00','2026-08-25T00:00:00.0000000+00:00' FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; INSERT INTO local_workspace_content_tombstones(store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision) SELECT 'session_event_content','0198f5b8-0c00-7000-8000-000000000011','event_content','whole_event',NULL,17,'2026-08-25T00:00:00.0000000+00:00',item_id,revision FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';");
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        Assert.Equal(before, LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT n.node_id||':'||r.carrier_digest||':'||(SELECT COUNT(*) FROM local_workspace_node_source_references x WHERE x.node_id=n.node_id)
            FROM local_workspace_nodes n JOIN local_workspace_semantic_receipts r ON r.node_id=n.node_id;
            """));
        Assert.Equal([state], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT availability_state FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.source_kind='semantic_tool' AND c.part='event_content';"));
    }

    [Fact]
    public void OrdinaryRefreshPreservesAuthenticatedReadDeniedReferencesAfterSourceBytesAreRemoved()
    {
        const string sessionId = "0198f5b8-0c00-7000-8000-000000000001";
        const string eventId = "0198f5b8-0c00-7000-8000-000000000011";
        using var connection = OpenSdkToolContentFixture(
            sessionId, "0198f5b8-0c00-7000-8000-000000000010", eventId, "native-run");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE retention_items
            SET read_denied_at='2026-08-25T00:00:00.0000000+00:00',revision=2
            WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';
            """);
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(
                connection, transaction, [sessionId], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }

        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';");
        foreach (var refreshedAt in new[]
                 {
                     DateTimeOffset.Parse("2026-08-25T00:00:02Z"),
                     DateTimeOffset.Parse("2026-08-25T00:00:03Z"),
                 })
        {
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(
                connection, transaction, refreshedAt, new StructuralRegistryAuthority());
            transaction.Commit();
        }

        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT event_id FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';"));
        Assert.Equal(
            [
                "semantic_tool:event_content:whole_event:<null>:17:read_denied:1:1:1:1:1:1:1:1:1",
                "session_event:event_content:whole_event:<null>:17:read_denied:1:1:1:1:1:1:1:1:1",
            ],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT n.source_kind||':'||c.part||':'||c.locator_kind||':'||COALESCE(c.json_pointer,'<null>')||':'||
                       CAST(c.selected_utf8_bytes AS TEXT)||':'||c.availability_state||':'||
                       (c.retention_item_id=i.item_id)||':'||(c.retention_store_instance_id=i.store_instance_id)||':'||
                       (c.source_captured_at=i.captured_at)||':'||(c.source_expires_at=i.expires_at)||':'||
                       (c.retention_revision=i.revision)||':'||(c.retention_ownership_receipt=i.ownership_receipt)||':'||
                       (c.retention_owner_token IS NULL)||':'||
                       (c.revision_input=e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|')||':'||
                       EXISTS(SELECT 1 FROM local_workspace_node_source_references r
                         WHERE r.node_id=n.node_id AND r.source_kind='session_event' AND r.source_identity=e.event_id AND r.event_id=e.event_id)
                FROM local_workspace_node_content_refs c
                JOIN local_workspace_nodes n ON n.node_id=c.node_id
                JOIN session_events e ON e.event_id=c.source_item_id
                JOIN retention_items i ON i.item_id=c.retention_item_id
                WHERE c.source_item_id='0198f5b8-0c00-7000-8000-000000000011'
                ORDER BY n.source_kind COLLATE BINARY;
                """));
    }

    [Theory]
    [InlineData("DELETE FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE retention_items SET revision=3 WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE retention_items SET ownership_receipt=randomblob(32) WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("DELETE FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind='session_event' AND source_identity='0198f5b8-0c00-7000-8000-000000000011');")]
    [InlineData("UPDATE local_workspace_node_content_refs SET availability_state='invalid' WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE source_kind='session_event' AND source_identity='0198f5b8-0c00-7000-8000-000000000011');")]
    public void OrdinaryRefreshDoesNotPreserveReadDeniedWithoutExactCurrentAuthority(string authorityMutation)
    {
        const string sessionId = "0198f5b8-0c00-7000-8000-000000000001";
        using var connection = OpenSdkToolContentFixture(
            sessionId, "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE retention_items
            SET read_denied_at='2026-08-25T00:00:00.0000000+00:00',revision=2
            WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';
            """);
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(
                connection, transaction, [sessionId], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            DELETE FROM session_event_content
            WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';
            {authorityMutation}
            """);

        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(
                connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:02Z"),
                new StructuralRegistryAuthority());
            transaction.Commit();
        }

        Assert.Equal(
            ["semantic_tool:not_captured:1:1", "session_event:not_captured:1:1"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT n.source_kind||':'||c.availability_state||':'||
                       (c.retention_item_id IS NULL)||':'||(c.retention_owner_token IS NULL)
                FROM local_workspace_node_content_refs c
                JOIN local_workspace_nodes n ON n.node_id=c.node_id
                WHERE c.source_item_id='0198f5b8-0c00-7000-8000-000000000011'
                ORDER BY n.source_kind COLLATE BINARY;
                """));
    }

    [Fact]
    public void PairedSkillInvocationPersistsBothExactSourceReferenceArms()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk','native-run','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-otel','session-a','run-a','copilot-sdk','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','skill.invoked','2026-08-24T00:00:00.0000000+00:00','not_captured'),
              ('event-sdk','session-a','run-a','copilot-sdk','copilot-sdk-stream','sdk-source','skill.invoked','2026-08-24T00:00:00.0000000+00:00','not_captured');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var invocation = new SkillProjectionCanonicalInvocation(
            "paired-canonical", "session-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb",
            "otel-source", "skill", "sdk-source", "skill", null,
            "session_run", "run-a", "event-otel", "event-sdk");
        var projections = new Dictionary<string, SkillProjectionCurrentInvocationProjection>(StringComparer.Ordinal)
        {
            ["session-a"] = new("current", [invocation]),
        };
        var method = typeof(LocalWorkspaceProjectionStore).GetMethod("RefreshDetailProjection",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        using (var transaction = connection.BeginTransaction())
        {
            method.Invoke(null, [connection, transaction, new[] { "session-a" }, projections,
                DateTimeOffset.Parse("2026-08-25T00:00:00Z"), "registry"]);
            transaction.Commit();
        }

        Assert.Equal(["otel-source:event-otel", "sdk-source:event-sdk"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT r.source_identity||':'||r.event_id FROM local_workspace_node_source_references r
            JOIN local_workspace_nodes n ON n.node_id=r.node_id WHERE n.source_kind='skill_invocation' ORDER BY r.source_ordinal;
            """));
    }

    [Fact]
    public async Task SemanticReceiptMutationFailsClosedWhileRawCarrierBytesRemainExcludedFromRevision()
    {
        using var connection = OpenSdkToolContentFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run");
        InstallDetailRevisionSupportTables(connection);
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load(),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")));
        var request = new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Summary,
            "0198f5b8-0c00-7000-8000-000000000001");
        var before = await contributor.ReadAsync(new TestReadTransaction(connection), request, CancellationToken.None);

        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "UPDATE session_event_content SET content_json=json_set(content_json,'$.raw_only_revision',1);");
        var rawChanged = await contributor.ReadAsync(new TestReadTransaction(connection), request, CancellationToken.None);
        Assert.Equal(before.CanonicalRevisionInput, rawChanged.CanonicalRevisionInput);

        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "UPDATE local_workspace_semantic_receipts SET authority_receipt=authority_receipt||'|revision-2';");
        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new TestReadTransaction(connection), request, CancellationToken.None));
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_semantic_receipts SET authority_receipt=authority_receipt||'|tampered';")]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input=revision_input||'|tampered' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("DELETE FROM local_workspace_semantic_receipts;")]
    public void BackupValidationRejectsSemanticReceiptTamperingAndDeletion(string mutation)
    {
        using var connection = OpenSdkToolContentFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
                connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:00Z"))).Message);
    }

    public static TheoryData<string, string> DurableSemanticProvenanceTamperCases => new()
    {
        { "read_denied", "UPDATE session_events SET source_adapter='other' WHERE type='tool.execution_start';" },
        { "read_denied", "UPDATE session_events SET source_surface='claude-code' WHERE type='tool.execution_start';" },
        { "read_denied", "UPDATE session_events SET source_event_id='' WHERE type='tool.execution_start';" },
        { "read_denied", "UPDATE session_runs SET native_run_id=NULL;" },
        { "read_denied", "UPDATE session_runs SET source_surface='claude-code';" },
        { "deleted", "UPDATE session_events SET source_adapter='other' WHERE type='tool.execution_start';" },
        { "deleted", "UPDATE session_events SET source_surface='claude-code' WHERE type='tool.execution_start';" },
        { "deleted", "UPDATE session_events SET source_event_id='' WHERE type='tool.execution_start';" },
        { "deleted", "UPDATE session_runs SET native_run_id=NULL;" },
        { "deleted", "UPDATE session_runs SET source_surface='claude-code';" },
    };

    [Theory]
    [MemberData(nameof(DurableSemanticProvenanceTamperCases))]
    public void BackupValidationRejectsUnavailableSemanticNativeScopeAndRequiredProvenanceTampering(
        string availability,
        string mutation)
    {
        using var connection = OpenUnavailableSdkToolFixture(availability);
        using (var control = connection.BeginTransaction(deferred: true))
            CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
                connection, control, DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
                connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:01Z"))).Message);
    }

    [Theory]
    [InlineData("other-adapter", "0000000000000000000000000000000000000000000000000000000000000000", "UserPromptSubmit", "{\"prompt\":\"hello\"}")]
    [InlineData("claude-code-hook", null, "UserPromptSubmit", "{\"prompt\":\"hello\"}")]
    [InlineData("claude-code-hook", "short", "UserPromptSubmit", "{\"prompt\":\"hello\"}")]
    [InlineData("claude-code-hook", "0000000000000000000000000000000000000000000000000000000000000000", "OtherEvent", "{\"prompt\":\"hello\"}")]
    [InlineData("claude-code-hook", "0000000000000000000000000000000000000000000000000000000000000000", "UserPromptSubmit", "{\"other\":\"hello\"}")]
    [InlineData("claude-code-hook", "0000000000000000000000000000000000000000000000000000000000000000", "UserPromptSubmit", "{\"prompt\":{}}")]
    [InlineData("claude-code-hook", "0000000000000000000000000000000000000000000000000000000000000000", "UserPromptSubmit", "not-json")]
    public void UnacceptedRawCarrierShapesRemainWholeEventContent(
        string adapter,
        string? fingerprint,
        string eventKind,
        string content)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,schema_fingerprint,type,occurred_at,content_state)
              VALUES('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','{adapter}','source-a',{(fingerprint is null ? "NULL" : $"'{fingerprint}'")},'{eventKind}','2026-08-24T00:00:00.0000000+00:00','available');
            INSERT INTO session_event_content VALUES('event-a','application/json','{content.Replace("'", "''", StringComparison.Ordinal)}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["event_content:whole_event:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT part||':'||locator_kind||':'||COALESCE(json_pointer,'null') FROM local_workspace_node_content_refs;"));
    }

    [Fact]
    public void UnconfiguredParticipantIsNoOpOnlyWhenProjectionIsAbsent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        using (var absent = connection.BeginTransaction())
        {
            UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance.RefreshSessions(
                connection, absent, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            absent.Rollback();
        }

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using var installed = connection.BeginTransaction();
        Assert.Equal("local_workspace_projection_authority_unavailable", Assert.Throws<InvalidOperationException>(() =>
            UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance.RefreshSessions(
                connection, installed, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"))).Message);
        installed.Rollback();
    }

    [Theory]
    [InlineData("refresh", "capture", "skill_registry_generation_unavailable", 0)]
    [InlineData("refresh", "lease", "skill_registry_generation_unavailable", 0)]
    [InlineData("refresh", "verify", "skill_registry_generation_not_current", 1)]
    [InlineData("sessions", "capture", "skill_registry_generation_unavailable", 0)]
    [InlineData("batches", "capture", "skill_registry_generation_unavailable", 0)]
    public void RegistryAwareRefreshFailsBeforeChangingWorkspaceRows(
        string entryPoint,
        string failure,
        string expectedError,
        int expectedDisposedLeases)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var before = WorkspaceProjectionDigest(connection);
        var authority = new FailingRegistryAuthority(failure);

        using (var transaction = connection.BeginTransaction())
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (entryPoint)
                {
                    case "refresh":
                        LocalWorkspaceProjectionStore.Refresh(connection, transaction,
                            DateTimeOffset.Parse("2026-08-25T00:00:01Z"), authority);
                        break;
                    case "sessions":
                        LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction,
                            ["0198f5b8-0c00-7000-8000-000000000001"],
                            DateTimeOffset.Parse("2026-08-25T00:00:01Z"), authority);
                        break;
                    case "batches":
                        LocalWorkspaceProjectionStore.RefreshSessionBatches(connection, transaction,
                            [["0198f5b8-0c00-7000-8000-000000000001"]],
                            DateTimeOffset.Parse("2026-08-25T00:00:01Z"), authority);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entryPoint));
                }
            });
            Assert.Equal(expectedError, error.Message);
        }

        Assert.Equal(expectedDisposedLeases, authority.DisposedLeaseCount);
        Assert.Equal(before, WorkspaceProjectionDigest(connection));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void ProjectionParticipantsRejectInstalledNonV5BeforeMutation(int installedVersion)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            $"UPDATE schema_version SET version={installedVersion} WHERE component='local_workspace_projection';");

        using var transaction = connection.BeginTransaction();
        var configured = new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.Load());
        Assert.Equal("local_workspace_projection_schema_unsupported", Assert.Throws<InvalidOperationException>(() =>
            configured.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.UnixEpoch)).Message);
        Assert.Equal("local_workspace_projection_schema_unsupported", Assert.Throws<InvalidOperationException>(() =>
            UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.UnixEpoch)).Message);
        transaction.Rollback();

        Assert.Equal([installedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void ProjectionParticipantsRejectCurrentStampWithPartialShapeBeforeMutation()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "DROP TABLE local_workspace_node_edges;");

        using var transaction = connection.BeginTransaction();
        var configured = new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.Load());
        Assert.Equal("local_workspace_projection_schema_unsupported", Assert.Throws<InvalidOperationException>(() =>
            configured.RefreshSessions(connection, transaction,
                ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.UnixEpoch)).Message);
        transaction.Rollback();
    }
    [Fact]
    public void BackfillUsesFirstValidStartedCreatedLastSeenInstantAndCanonicalUtc()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,'malformed',NULL,'2026-08-24T03:00:00.0000000+03:00','not_captured','2026-08-23T20:00:00.0000000-04:00','2026-08-24T03:00:00.0000000+03:00');
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000002','active','partial',NULL,NULL,NULL,NULL,'malformed','not_captured','also-malformed','malformed');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(
            ["0:1787529600000:2026-08-24T00:00:00.0000000+00:00:1787529600000", "1:0:null:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT sort_group||':'||sort_epoch_ms||':'||COALESCE(last_seen_at,'null')||':'||COALESCE(last_seen_epoch_ms,'null') FROM local_workspace_sessions ORDER BY session_id;"));
    }

    [Fact]
    public void BackfillAcceptsOnlyExplicitOffsetInstantsAndFallsThroughInvalidCandidates()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000011','active','partial',NULL,NULL,'malformed',NULL,'2026-08-24T09:00:00.0000000+09:00','not_captured','2026-08-24T01:00:00.0000000+00:00','2026-08-24T09:00:00.0000000+09:00');
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000012','active','partial',NULL,NULL,'2026-08-24T00:00:00',NULL,'2026-08-23T20:00:00.0000000-04:00','not_captured','malformed','2026-08-23T20:00:00.0000000-04:00');
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000013','active','partial',NULL,NULL,' 2026-08-24T00:00:00.0000000+00:00 ',NULL,'2026-08-24','not_captured','2026-08-24T00:00:00','2026-08-24');
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000014','active','partial',NULL,NULL,'2026-08-24T14:00:00.1234567+14:00',NULL,'2026-08-24T00:00:00.1234567+00:00','not_captured','2026-08-24T00:00:01.0000000+00:00','2026-08-24T00:00:00.1234567+00:00');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(
            ["0:1787533200000", "0:1787529600000", "1:0", "0:1787529600123"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT sort_group||':'||sort_epoch_ms FROM local_workspace_sessions ORDER BY session_id;"));
        Assert.Equal(
            ["2026-08-24T00:00:00.1234567+00:00"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT started_at FROM local_workspace_sessions WHERE session_id='0198f5b8-0c00-7000-8000-000000000014';"));
    }

    [Fact]
    public void BackfillUsesTwoStartedAtGroupsAndPersistsAllClosedSourceTokens()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','completed','partial',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:02:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:02:00.0000000+00:00');
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000002','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:03:00.0000000+00:00','not_captured','2026-08-24T00:03:00.0000000+00:00','2026-08-24T00:03:00.0000000+00:00');
            INSERT INTO session_runs(run_id,session_id,source_surface,status) VALUES
              ('0198f5b8-0c01-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','completed'),
              ('0198f5b8-0c01-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','copilot-cli','completed'),
              ('0198f5b8-0c01-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','vscode','completed'),
              ('0198f5b8-0c01-7000-8000-000000000004','0198f5b8-0c00-7000-8000-000000000001','hook-unknown','completed'),
              ('0198f5b8-0c01-7000-8000-000000000005','0198f5b8-0c00-7000-8000-000000000001','claude-code','completed');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        Assert.Equal(["0:0:2026-08-24T00:02:00.0000000+00:00", "0:0:2026-08-24T00:03:00.0000000+00:00"], LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT sort_group||':'||(sort_epoch_ms=0)||':'||last_seen_at FROM local_workspace_sessions ORDER BY session_id;"));
        Assert.Equal(["claude-code","copilot-cli","copilot-sdk","hook-unknown","vscode"], LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT source FROM local_workspace_session_sources ORDER BY source;"));
    }

    [Fact]
    public void ModelOverflowFailsClosedInsteadOfTruncating()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<17)
            INSERT INTO session_runs SELECT printf('0198f5b8-0c02-7000-8000-%012d',v),'0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,printf('model-%02d',v),NULL,NULL,NULL,NULL,NULL,'active' FROM n;
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["projection_invalid:0:projection_invalid,raw_content_not_captured"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT model_state||':'||(SELECT COUNT(*) FROM local_workspace_session_models)||':'||capture_notes FROM local_workspace_sessions;"));
        using var validation = connection.BeginTransaction(deferred: true);
        CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(connection, validation);
    }

    [Fact]
    public void BackfillDerivesTenThousandLabelsWithOneSetBasedUpdate()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<10000)
            INSERT INTO sessions SELECT printf('0198f5b8-0c00-7000-8000-%012d',v),'active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00' FROM n;
            WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<10000)
            INSERT INTO session_events SELECT printf('0198f5b8-0c01-7000-8000-%012d',v),printf('0198f5b8-0c00-7000-8000-%012d',v),NULL,NULL,NULL,NULL,NULL,'synthetic',printf('prompt-%d',v),'user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL FROM n;
            WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<10000)
            INSERT INTO session_event_content SELECT printf('0198f5b8-0c01-7000-8000-%012d',v),'application/json',printf('{"value":"instruction %d"}',v),'2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32) FROM n;
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["10000"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_sessions WHERE label_state='recorded';"));
    }

    [Fact]
    public void EnsureBackfillsCurrentSessionsAndRerunDoesNotDrift()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','completed','partial',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00'); INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00',10,3,NULL,'completed');");

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var before = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT session_id || ':' || revision_seed FROM local_workspace_sessions;");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T01:00:00Z"));

        Assert.Equal(before, LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT session_id || ':' || revision_seed FROM local_workspace_sessions;"));
        Assert.Equal(["session_run:10:3:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT authority || ':' || input_tokens || ':' || output_tokens || ':' || COALESCE(CAST(total_tokens AS TEXT),'null') FROM local_workspace_token_observations;"));
    }

    [Fact]
    public void ParticipantDerivesUnicodeLabelAndClearsItAfterExactContentDeletion()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"  ＨＥＬＬＯ\r\nWorld\u2028Next  "}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        Assert.Equal(["ＨＥＬＬＯ World Next|hello world next"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT p.label_text||'|'||f.normalized_text FROM local_workspace_sessions p JOIN local_workspace_session_search_facts f ON f.session_id=p.session_id AND f.kind='label';"));

        using (var transaction = connection.BeginTransaction())
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM session_event_content;";
            delete.ExecuteNonQuery();
            StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }
        Assert.Equal(["not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT label_state FROM local_workspace_sessions;"));
        Assert.Equal(["1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(label_text IS NULL AND NOT EXISTS(SELECT 1 FROM local_workspace_session_search_facts) AS TEXT) FROM local_workspace_sessions;"));
    }

    [Fact]
    public async Task ParticipantKeepsPinnedLabelAndToolSearchFactsPastHistoricalExpiryUntilUnpinned()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,input_tokens INTEGER,output_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            CREATE TABLE retention_items(store_kind TEXT,source_item_id TEXT,state TEXT,read_denied_at TEXT,deleted_at TEXT,error_code TEXT,expires_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_events VALUES
              ('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL),
              ('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:01.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"Pinned label"}','2026-08-24T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00',randomblob(32));
            INSERT INTO raw_records VALUES(41,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00',NULL,'{}',1);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name) VALUES(41,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',3,'execute_tool','tool_call','PinnedTool');
            INSERT INTO retention_items VALUES
              ('session_event_content','0198f5b8-0c00-7000-8000-000000000002','retained_by_policy',NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00'),
              ('raw_record','41','retained_by_policy',NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00');
            PRAGMA foreign_keys=ON;
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-27T00:00:00Z"));

        Assert.Equal(["label:pinned label:null", "tool:pinnedtool:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT kind||':'||normalized_text||':'||COALESCE(expires_at,'null') FROM local_workspace_session_search_facts ORDER BY kind;"));
        Assert.Equal(["recorded:Pinned label:2026-08-26T00:00:00.0000000+00:00"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT label_state||':'||label_text||':'||COALESCE(label_expires_at,'null') FROM local_workspace_sessions;"));
        var pinned = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single((await new LocalWorkspaceSessionSnapshotContributor(
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"))).ReadAsync(
                new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None)).Sessions));
        Assert.Equal("recorded", pinned.LabelState);
        Assert.Equal("Pinned label", pinned.LabelText);
        Assert.Equal(["pinned label", "pinnedtool"], pinned.SearchTexts);

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE retention_items SET state='expiring';");
        using (var transaction = connection.BeginTransaction())
        {
            StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
            transaction.Commit();
        }

        Assert.Equal(["not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT label_state FROM local_workspace_sessions;"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT normalized_text FROM local_workspace_session_search_facts;"));
    }

    [Fact]
    public void ExactLlmSpanFallbackIsStoredButSessionRunWinsSameExecution()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,10,3,NULL,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000002',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','llm_call',99,88,187,7,20,4);
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO local_workspace_span_facts VALUES(1,0,2,181);");
        using (var transaction = connection.BeginTransaction()) { StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z")); transaction.Commit(); }

        Assert.Equal(["llm_span:1", "session_run:0"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT authority||':'||authority_rank FROM local_workspace_token_observations ORDER BY authority;"));
        Assert.Equal(["10"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "WITH ranked AS (SELECT input_tokens,row_number() OVER(PARTITION BY execution_id ORDER BY authority_rank) n FROM local_workspace_token_observations) SELECT CAST(input_tokens AS TEXT) FROM ranked WHERE n=1;"));
        Assert.Equal(["recorded:2"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT state||':'||count FROM local_workspace_session_activity WHERE kind='retry';"));
        Assert.Equal(["recorded:10:not_observed:not_observed:not_observed:not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT token_state||':'||input_tokens||':'||skill_activity_state||':'||tool_activity_state||':'||retry_relation_state||':'||recovery_relation_state FROM local_workspace_nodes WHERE source_kind='execution_root';"));
        Assert.Equal(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:bbbbbbbbbbbbbbbb"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT trace_id||':'||span_id FROM local_workspace_nodes WHERE source_kind='session_event';"));
    }

    [Fact]
    public void DetailFactsPersistClosedActivityTimingAndTokenAuthority()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','completed','full',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'gpt-5','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00',10,3,13,'completed');
            INSERT INTO session_events VALUES('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,NULL,NULL,'synthetic','source-a','PreToolUse','2026-08-24T00:00:01.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["not_observed:null|not_observed:null|not_observed:null|not_observed:null|not_observed:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT skill_activity_state||':'||COALESCE(skill_activity_count,'null')||'|'||tool_activity_state||':'||COALESCE(tool_activity_count,'null')||'|'||subagent_activity_state||':'||COALESCE(subagent_activity_count,'null')||'|'||error_activity_state||':'||COALESCE(error_activity_count,'null')||'|'||retry_activity_state||':'||COALESCE(retry_activity_count,'null') FROM local_workspace_execution_headers;"));
        Assert.Equal(["session_run:recorded:10:3:13:not_observed:null:null:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT token_authority||':'||token_state||':'||input_tokens||':'||output_tokens||':'||total_tokens||':'||reasoning_token_state||':'||COALESCE(reasoning_tokens,'null')||':'||COALESCE(cache_read_tokens,'null')||':'||COALESCE(cache_creation_tokens,'null') FROM local_workspace_execution_headers;"));
        Assert.Equal(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT trace_id FROM local_workspace_execution_headers;"));
        Assert.Equal(["recorded:639231264010000000:639231264010000000:0"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT time_authority||':'||start_utc_ticks||':'||end_utc_ticks||':'||duration_ms FROM local_workspace_nodes WHERE source_identity='event-a';"));
    }

    [Fact]
    public void ExecutionTokenArithmeticViolationIsInconsistentWithoutExposingInvalidTotal()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,10,3,99,'active');
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["inconsistent:0|recorded:10|recorded:3|inconsistent:1"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT token_state||':'||available_execution_count||'|'||input_token_state||':'||input_tokens||'|'||output_token_state||':'||output_tokens||'|'||total_token_state||':'||(total_tokens IS NULL) FROM local_workspace_execution_headers;"));
    }

    [Fact]
    public void CacheReadGreaterThanInputIsInconsistentAndDerivedValuesAreNull()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events VALUES('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','llm_call',10,3,13,2,11,1);
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["inconsistent|inconsistent:null|inconsistent:null|inconsistent:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection,
                "SELECT token_state||'|'||cache_read_token_state||':'||COALESCE(cache_read_tokens,'null')||'|'||new_input_token_state||':'||COALESCE(new_input_tokens,'null')||'|'||cache_read_ratio_state||':'||COALESCE(cache_read_ratio_basis_points,'null') FROM local_workspace_execution_headers;"));
    }

    [Fact]
    public void RetryTotalsRemainScopedToTheirExactExecution()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES
              ('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('run-b','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,'cccccccccccccccccccccccccccccccc',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events VALUES
              ('event-a','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk',NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL),
              ('event-b','0198f5b8-0c00-7000-8000-000000000001','run-b','copilot-sdk',NULL,'cccccccccccccccccccccccccccccccc',NULL,'otel-exact','cccccccccccccccccccccccccccccccc/dddddddddddddddd','otel.span','2026-08-24T00:00:01.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO monitor_spans VALUES
              (1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','llm_call',NULL,NULL,NULL,NULL,NULL,NULL),
              (2,'cccccccccccccccccccccccccccccccc','dddddddddddddddd',0,'chat','llm_call',NULL,NULL,NULL,NULL,NULL,NULL);
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO local_workspace_span_facts VALUES(1,0,2,NULL),(2,0,5,NULL);");
        using (var transaction = connection.BeginTransaction()) { StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z")); transaction.Commit(); }

        Assert.Equal(["run-a:2", "run-b:5"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT source_identity||':'||retry_activity_count FROM local_workspace_execution_headers ORDER BY source_identity;"));
    }

    [Theory]
    [InlineData("gap_before_capture", "not_captured", "capture_gap")]
    [InlineData(null, "unsupported", "source_unsupported")]
    public void ExactRetryObservationDoesNotOverrideIncompleteCapture(string? status, string contentState, string expected)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000002',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',{(status is null ? "NULL" : $"'{status}'")},'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','{contentState}',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','error',NULL,NULL,NULL,NULL,NULL,NULL);
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO local_workspace_span_facts VALUES(1,0,2,NULL);");
        using (var transaction = connection.BeginTransaction()) { StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z")); transaction.Commit(); }
        Assert.Equal([$"{expected}:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT state||':'||(count IS NULL) FROM local_workspace_session_activity WHERE kind='retry';"));
    }

    private sealed class StructuralRegistryAuthority : ISkillRegistryGenerationAuthority
    {
        public ISkillRegistryGenerationCapture CaptureGeneration() => new Capture();
        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = new Lease();
            return true;
        }
        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) =>
            capture is Capture && lease is Lease;
        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) => "structural-registry";
        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple) => false;
        private sealed class Capture : ISkillRegistryGenerationCapture { }
        private sealed class Lease : ISkillRegistryGenerationLease { public void Dispose() { } }
    }

    private sealed class FailingRegistryAuthority(string failure) : ISkillRegistryGenerationAuthority
    {
        internal int DisposedLeaseCount { get; private set; }

        public ISkillRegistryGenerationCapture? CaptureGeneration() =>
            failure == "capture" ? null : new Capture();

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = failure == "lease" ? null : new Lease(this);
            return lease is not null;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) => failure != "verify";

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) => "failing-registry";

        public bool IsProducerTupleAccepted(
            ISkillRegistryGenerationLease lease,
            SkillRegistryProducerTuple tuple) => false;

        private sealed class Capture : ISkillRegistryGenerationCapture { }

        private sealed class Lease(FailingRegistryAuthority owner) : ISkillRegistryGenerationLease
        {
            public void Dispose() => owner.DisposedLeaseCount++;
        }
    }

    private static string WorkspaceProjectionDigest(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var table in LocalWorkspaceProjectionSchemaTests.Strings(connection,
                     "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'local_workspace_%' ORDER BY name;"))
        {
            var escapedTable = table.Replace("'", "''", StringComparison.Ordinal);
            var quotedTable = table.Replace("\"", "\"\"", StringComparison.Ordinal);
            var columns = LocalWorkspaceProjectionSchemaTests.Strings(connection,
                $"SELECT name FROM pragma_table_xinfo('{escapedTable}') WHERE hidden=0 ORDER BY cid;");
            var projection = columns.Select(static column =>
                $"COALESCE(quote(\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\"),'NULL')");
            rows.AddRange(LocalWorkspaceProjectionSchemaTests.Strings(connection,
                $"SELECT '{escapedTable}|'||{string.Join("||'|'||", projection)} FROM \"{quotedTable}\" ORDER BY {string.Join(',', columns.Select(static column => $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""))};"));
        }
        return string.Join('\n', rows);
    }

    private static void InstallCurrentRetentionRows(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        params string[] eventIds)
    {
        using var transaction = connection.BeginTransaction();
        CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
        using var storeCommand = connection.CreateCommand(); storeCommand.Transaction = transaction; storeCommand.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;";
        var store = Assert.IsType<string>(storeCommand.ExecuteScalar());
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id ORDER BY c.event_id;";
        using var reader = read.ExecuteReader();
        var rows = new List<(string EventId,string Kind,string Captured,string Expires,string Session,string? Run,string Adapter,string SourceEvent,byte[] Token)>();
        while (reader.Read())
        {
            var eventId = reader.GetString(0);
            if (eventIds.Length == 0 || eventIds.Contains(eventId, StringComparer.Ordinal))
                rows.Add((eventId,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetString(6),reader.GetString(7),(byte[])reader.GetValue(8)));
        }
        reader.Close();
        foreach (var row in rows)
        {
            var captured = DateTimeOffset.ParseExact(row.Captured,"O",System.Globalization.CultureInfo.InvariantCulture);
            var expires = DateTimeOffset.ParseExact(row.Expires,"O",System.Globalization.CultureInfo.InvariantCulture);
            var receipt = CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionOwnershipReceipt.CreateSession(new(store,row.EventId,row.Kind,row.Captured,captured.UtcDateTime.Ticks,row.Expires,expires.UtcDateTime.Ticks,row.Session,row.Run,row.Adapter,row.SourceEvent,row.Token));
            using var insert = connection.CreateCommand(); insert.Transaction=transaction;
            insert.CommandText="INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($item,$store,'session_event_content',$source,1,$receipt,$captured,$expires,'raw-default-90d',1,'expiring',1,1);";
            insert.Parameters.AddWithValue("$item","item-"+row.EventId); insert.Parameters.AddWithValue("$store",store); insert.Parameters.AddWithValue("$source",row.EventId); insert.Parameters.AddWithValue("$receipt",receipt); insert.Parameters.AddWithValue("$captured",row.Captured); insert.Parameters.AddWithValue("$expires",row.Expires); insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static Microsoft.Data.Sqlite.SqliteConnection OpenUnavailableSdkToolFixture(string state)
    {
        var connection = OpenSdkToolContentFixture(
            "0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000010",
            "0198f5b8-0c00-7000-8000-000000000011", "native-run");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, state == "read_denied"
            ? "UPDATE retention_items SET read_denied_at='2026-08-25T00:00:00.0000000+00:00',revision=2 WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"
            : "UPDATE retention_items SET state='deleted',deleted_at='2026-08-25T00:00:00.0000000+00:00',revision=2 WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,'2026-08-25T00:00:00.0000000+00:00','2026-08-25T00:00:00.0000000+00:00' FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; INSERT INTO local_workspace_content_tombstones(store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision) SELECT 'session_event_content','0198f5b8-0c00-7000-8000-000000000011','event_content','whole_event',NULL,17,'2026-08-25T00:00:00.0000000+00:00',item_id,revision FROM retention_items WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011'; DELETE FROM session_event_content WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';");
        using var transaction = connection.BeginTransaction();
        StructuralParticipant.RefreshSessions(connection, transaction,
            ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:01Z"));
        transaction.Commit();
        return connection;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenSdkToolContentFixture(
        string sessionId,
        string runId,
        string eventId,
        string nativeRunId)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
            INSERT INTO sessions VALUES('{{sessionId}}','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('{{runId}}','{{sessionId}}','copilot-sdk','{{nativeRunId}}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES
                ('{{eventId}}','{{sessionId}}','{{runId}}','copilot-sdk','copilot-sdk-stream','sdk-source-start','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','available'),
                ('{{eventId}}-complete','{{sessionId}}','{{runId}}','copilot-sdk','copilot-sdk-stream','sdk-source-complete','tool.execution_complete','2026-08-24T00:00:01.0000000+00:00','not_captured');
            UPDATE session_events SET parent_event_id='{{eventId}}' WHERE event_id='{{eventId}}-complete';
            INSERT INTO session_event_content VALUES('{{eventId}}','application/json',json_object('tool_input',json(char(123)||char(125))),'2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        return connection;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenClaudeToolFixture(
        string sessionId,
        string runId,
        string eventId,
        string nativeSessionId,
        string nativeRunId,
        bool includeNativeBinding)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
            INSERT INTO sessions VALUES('{{sessionId}}','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            {{(includeNativeBinding ? $"INSERT INTO session_native_ids VALUES('{sessionId}','claude-code','{nativeSessionId}','native','2026-08-24T00:00:00.0000000+00:00');" : string.Empty)}}
            INSERT INTO session_runs VALUES('{{runId}}','{{sessionId}}','claude-code','{{nativeRunId}}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version)
              VALUES('{{eventId}}','{{sessionId}}','{{runId}}','claude-code','claude-code-hook','source-start','PreToolUse','2026-08-24T00:00:00.0000000+00:00','available','2.1.145','hook-adapter-v1',printf('%064d',1),'hook-normalization-v1');
            INSERT INTO session_event_content VALUES('{{eventId}}','application/json','{"tool_name":"Read","tool_input":{},"tool_use_id":"stable-carrier"}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        InstallCurrentRetentionRows(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        return connection;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenOtelToolAdmissionFixture(
        string traceId,
        string spanId,
        bool duplicateSpanOwner)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','{{traceId}}','otel-exact','{{traceId}}/{{spanId}}','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
            INSERT INTO raw_records VALUES(1,'otlp','{{traceId}}','2026-08-24T00:00:00.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,status,start_time,end_time)
              VALUES(1,'{{traceId}}','{{spanId}}',NULL,0,'execute_tool','tool_call','Read','ok','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');
            {{(duplicateSpanOwner ? $"INSERT INTO raw_records VALUES(2,'otlp','{traceId}','2026-08-24T00:00:01.0000000+00:00','{{}}','{{}}',1,NULL); INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,status,start_time,end_time) VALUES(2,'{traceId}','{spanId}',NULL,0,'execute_tool','tool_call','Other','ok','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');" : string.Empty)}}
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        return connection;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenNodeCapacityFixture(string scenario)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('session-a','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','session-a','copilot-sdk','native-run-a','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            {(scenario == "unknown_relation" ? "INSERT INTO session_runs VALUES('run-b','session-a','copilot-sdk','native-run-b',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'); INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('event-parent','session-a','run-b','copilot-sdk','synthetic','source-parent','event','2026-08-24T00:00:00.0000000+00:00','not_captured');" : string.Empty)}
            """);
        var ordinaryCount = scenario == "unknown_relation" ? 4094 : 4094;
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<{ordinaryCount})
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state)
            SELECT printf('event-%04d',value),'session-a','run-a','copilot-sdk',
                   {(scenario == "unknown_relation" ? "CASE value WHEN 1 THEN 'event-parent' END" : "NULL")},
                   'synthetic',printf('source-%04d',value),'event','2026-08-24T00:00:00.0000000+00:00','not_captured'
            FROM n;
            """);
        if (scenario == "semantic_tool")
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
                  ('event-sdk-start','session-a','run-a','copilot-sdk',NULL,'copilot-sdk-stream','sdk-start','tool.execution_start','2026-08-24T00:00:01.0000000+00:00','not_captured'),
                  ('event-sdk-complete','session-a','run-a','copilot-sdk','event-sdk-start','copilot-sdk-stream','sdk-complete','tool.execution_complete','2026-08-24T00:00:02.0000000+00:00','not_captured');
                """);
        }
        else if (scenario == "skill")
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
                  ('event-skill-otel','session-a','run-a','copilot-sdk','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','skill.invoked','2026-08-24T00:00:01.0000000+00:00','not_captured'),
                  ('event-skill-sdk','session-a','run-a','copilot-sdk',NULL,'copilot-sdk-stream','sdk-source','skill.invoked','2026-08-24T00:00:02.0000000+00:00','not_captured');
                """);
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TRIGGER local_workspace_capacity_before_insert BEFORE INSERT ON local_workspace_nodes
            WHEN (SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=NEW.session_id)>=4097
            BEGIN SELECT RAISE(ABORT,'workspace_intermediate_node_overflow'); END;
            """);
        return connection;
    }

    private static void AssertNodeCapacityClosed(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string excludedSourceKind)
    {
        Assert.Equal(4097, LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT node_id FROM local_workspace_nodes WHERE session_id='session-a';").Length);
        Assert.Equal(["1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT CAST(node_overflow AS TEXT) FROM local_workspace_sessions WHERE session_id='session-a';"));
        Assert.Empty(LocalWorkspaceProjectionSchemaTests.Strings(connection,
            $"SELECT node_id FROM local_workspace_nodes WHERE source_kind='{excludedSourceKind}';"));
    }

    private static void InstallDetailRevisionSupportTables(Microsoft.Data.Sqlite.SqliteConnection connection) =>
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE IF NOT EXISTS monitor_spans(raw_record_id INTEGER,span_ordinal INTEGER,trace_id TEXT,span_id TEXT);
            CREATE TABLE IF NOT EXISTS raw_records(id INTEGER,source TEXT,trace_id TEXT,received_at TEXT,schema_version INTEGER,retention_owner_token BLOB);
            """);

    private static Microsoft.Data.Sqlite.SqliteConnection OpenSdkToolFixture(
        string sessionId,
        string runId,
        string eventId,
        string nativeRunId,
        string sourceEventId)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('{sessionId}','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('{runId}','{sessionId}','copilot-sdk','{nativeRunId}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES
                ('{eventId}','{sessionId}','{runId}','copilot-sdk','copilot-sdk-stream','{sourceEventId}','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','not_captured'),
                ('{eventId}-complete','{sessionId}','{runId}','copilot-sdk','copilot-sdk-stream','{sourceEventId}-complete','tool.execution_complete','2026-08-24T00:00:01.0000000+00:00','not_captured');
            UPDATE session_events SET parent_event_id='{eventId}' WHERE event_id='{eventId}-complete';
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        return connection;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenRawRetentionFixture(int selectedBytes, int? siblingBytes = null)
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,schema_fingerprint,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','claude-code-hook','0198f5b8-0c00-7000-8000-000000000021','0000000000000000000000000000000000000000000000000000000000000000','UserPromptSubmit','2026-08-24T00:00:00.0000000+00:00','available');
            """);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000011','application/json',$json,'2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));";
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(new { prompt = new string('x', selectedBytes) }));
            command.ExecuteNonQuery();
        }
        if (siblingBytes is not null)
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,schema_fingerprint,type,occurred_at,content_state)
                  VALUES('0198f5b8-0c00-7000-8000-000000000012','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','claude-code-hook','0198f5b8-0c00-7000-8000-000000000022','0000000000000000000000000000000000000000000000000000000000000000','UserPromptSubmit','2026-08-24T00:00:01.0000000+00:00','available');
                """);
            using var sibling = connection.CreateCommand();
            sibling.CommandText = "INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000012','application/json',$json,'2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));";
            sibling.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(new { prompt = new string('y', siblingBytes.Value) }));
            sibling.ExecuteNonQuery();
        }
        InstallCurrentRetentionRows(connection);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        return connection;
    }

    private sealed class TestReadTransaction(Microsoft.Data.Sqlite.SqliteConnection connection) : ILocalRepositoryReadTransaction
    {
        public async ValueTask<T> ReadAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            return await read(connection, transaction, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

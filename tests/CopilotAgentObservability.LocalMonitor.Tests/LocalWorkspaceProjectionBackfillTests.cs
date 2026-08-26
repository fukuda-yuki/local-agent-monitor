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
    [InlineData("deleted", "deleted")]
    [InlineData("read_denied", "read_denied")]
    [InlineData("error", "invalid")]
    [InlineData("tombstone", "deleted")]
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
    public void CommittedDeletionSurvivesPhysicalContentRemovalAsExactTombstoneFact()
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

        Assert.Equal(["event_content:deleted:1:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
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
            ["error_message:available", "event_content:available", "instruction:available", "subagent_input:available", "tool_input:available", "tool_result:available"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT part||':'||availability_state FROM local_workspace_node_content_refs ORDER BY part;"));
        Assert.Equal(["/agent_id", "/error", "/prompt", "/tool_input", "/tool_response", "whole"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT COALESCE(json_pointer,'whole') FROM local_workspace_node_content_refs ORDER BY COALESCE(json_pointer,'whole');"));

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';");
        using (var transaction = connection.BeginTransaction()) { StructuralParticipant.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z")); transaction.Commit(); }
        Assert.Equal(["invalid:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT availability_state||':'||(retention_owner_token IS NULL) FROM local_workspace_node_content_refs WHERE source_item_id='0198f5b8-0c00-7000-8000-000000000011';"));
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
              ('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'trace-1','span-1',NULL,'otel-exact','trace-1/span-1','tool.execution_start','2026-08-24T00:00:01.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"Pinned label"}','2026-08-24T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00',randomblob(32));
            INSERT INTO raw_records VALUES(41,'otlp','trace-1','2026-08-24T00:00:00.0000000+00:00',NULL,'{}',1);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name) VALUES(41,'trace-1','span-1',3,'execute_tool','tool_call','PinnedTool');
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

        Assert.Equal(["not_observed:null|recorded:1|recorded:0|recorded:0|not_observed:null"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT skill_activity_state||':'||COALESCE(skill_activity_count,'null')||'|'||tool_activity_state||':'||tool_activity_count||'|'||subagent_activity_state||':'||subagent_activity_count||'|'||error_activity_state||':'||error_activity_count||'|'||retry_activity_state||':'||COALESCE(retry_activity_count,'null') FROM local_workspace_execution_headers;"));
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
        public ISkillRegistryGenerationCapture? CaptureGeneration() => null;
        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = null;
            return false;
        }
        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) => false;
        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple) => false;
    }

    private static void InstallCurrentRetentionRows(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
        using var storeCommand = connection.CreateCommand(); storeCommand.Transaction = transaction; storeCommand.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;";
        var store = Assert.IsType<string>(storeCommand.ExecuteScalar());
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id ORDER BY c.event_id;";
        using var reader = read.ExecuteReader();
        var rows = new List<(string EventId,string Kind,string Captured,string Expires,string Session,string? Run,string Adapter,string SourceEvent,byte[] Token)>();
        while (reader.Read()) rows.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetString(6),reader.GetString(7),(byte[])reader.GetValue(8)));
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

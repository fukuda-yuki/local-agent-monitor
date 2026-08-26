namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceProjectionBackfillTests
{
    private static readonly LocalWorkspaceProjectionTransactionParticipant StructuralParticipant =
        new(new StructuralRegistryAuthority());

    [Fact]
    public void DetailProjectionClassifiesAllSixExactRawCarriersWithCurrentRetentionAdmission()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE retention_items(store_kind TEXT,source_item_id TEXT,state TEXT,read_denied_at TEXT,deleted_at TEXT,error_code TEXT,expires_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-a','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
              ('event-1','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-1','user.message','2026-08-24T00:00:00.0000000+00:00','available'),
              ('event-2','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-2','PreToolUse','2026-08-24T00:00:01.0000000+00:00','available'),
              ('event-3','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-3','PostToolUse','2026-08-24T00:00:02.0000000+00:00','available'),
              ('event-4','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-4','StopFailure','2026-08-24T00:00:03.0000000+00:00','available'),
              ('event-5','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-5','SubagentStart','2026-08-24T00:00:04.0000000+00:00','available'),
              ('event-6','0198f5b8-0c00-7000-8000-000000000001','run-a','copilot-sdk','synthetic','source-6','event','2026-08-24T00:00:05.0000000+00:00','available');
            INSERT INTO session_event_content SELECT event_id,'application/json','{}','2026-08-24T00:00:00.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32) FROM session_events;
            INSERT INTO retention_items SELECT 'session_event_content',event_id,'expiring',NULL,NULL,NULL,'2026-09-01T00:00:00.0000000+00:00' FROM session_events;
            PRAGMA foreign_keys=ON;
            """);

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(
            ["error_message:available", "event_content:available", "instruction:available", "subagent_input:available", "tool_input:available", "tool_result:available"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT part||':'||availability_state FROM local_workspace_node_content_refs ORDER BY part;"));
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

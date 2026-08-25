namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceProjectionBackfillTests
{
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
        Assert.Equal(["0:0:2026-08-24T00:02:00.0000000+00:00", "1:1:2026-08-24T00:03:00.0000000+00:00"], LocalWorkspaceProjectionSchemaTests.Strings(connection,"SELECT sort_group||':'||(sort_epoch_ms=0)||':'||last_seen_at FROM local_workspace_sessions ORDER BY session_id;"));
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
        Assert.Equal(["ＨＥＬＬＯ World Next|hello world next"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT label_text||'|'||label_search_text FROM local_workspace_sessions;"));

        using (var transaction = connection.BeginTransaction())
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM session_event_content;";
            delete.ExecuteNonQuery();
            LocalWorkspaceProjectionTransactionParticipant.Instance.RefreshSessions(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }
        Assert.Equal(["not_observed"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT label_state FROM local_workspace_sessions;"));
        Assert.Equal(["1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT CAST(label_text IS NULL AND label_search_text IS NULL AS TEXT) FROM local_workspace_sessions;"));
    }

    [Fact]
    public void ExactLlmSpanFallbackIsStoredButSessionRunWinsSameExecution()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,retry_count INTEGER,producer_total_tokens INTEGER);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,10,3,NULL,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000002',NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','llm_call',99,88,187,7,20,4,2,181);
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(["llm_span:1", "session_run:0"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT authority||':'||authority_rank FROM local_workspace_token_observations ORDER BY authority;"));
        Assert.Equal(["10"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "WITH ranked AS (SELECT input_tokens,row_number() OVER(PARTITION BY execution_id ORDER BY authority_rank) n FROM local_workspace_token_observations) SELECT CAST(input_tokens AS TEXT) FROM ranked WHERE n=1;"));
        Assert.Equal(["recorded:2"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT state||':'||count FROM local_workspace_session_activity WHERE kind='retry';"));
    }
}

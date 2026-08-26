namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionSnapshotContributorTests
{
    [Fact]
    public async Task ContributorReturnsTypedImmutableRowsInStableOrder()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000002','active','unbound',NULL,NULL,NULL,NULL,'2026-08-24T00:01:00.0000000+00:00','not_captured','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00'),('0198f5b8-0c00-7000-8000-000000000001','completed','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var contributor = new LocalWorkspaceSessionSnapshotContributor();
        var capability = new TestReadTransaction(connection);

        var result = await contributor.ReadAsync(capability, new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<ILocalRepositorySessionSnapshotRow>>(result.Sessions);
        Assert.Equal(["0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000002"], rows.Select(row => row.SessionId));
        Assert.All(rows, row => Assert.IsType<LocalWorkspaceProjectionRow>(row));
        Assert.Throws<NotSupportedException>(() => ((IList<ILocalRepositorySessionSnapshotRow>)rows)[0] = rows[0]);
    }

    [Fact]
    public async Task ContributorReadsTenThousandCandidatesWithSetBasedBatchQueries()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<10000)
            INSERT INTO sessions
            SELECT printf('0198f5b8-0c00-7000-8000-%012d',value),'completed','partial',NULL,NULL,NULL,NULL,
                   '2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'
            FROM n;
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        var statements = new List<string>();
        var result = await new LocalWorkspaceSessionSnapshotContributor(statementObserver: statements.Add).ReadAsync(
            new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(10_000, result.Sessions.Count);
        Assert.Equal(["sessions", "sources", "models", "search", "activity", "skills", "tokens"], statements);
        Assert.All(result.Sessions, row => Assert.Equal("not_observed", ((LocalWorkspaceProjectionRow)row).Activity.Skill.State));
    }

    [Fact]
    public void CandidateReadPlanUsesProjectionAndExactLabelJoinIndexesWithoutPerRowSubqueries()
    {
        using var connection=LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection,DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using var command=connection.CreateCommand(); command.CommandText="""
            EXPLAIN QUERY PLAN SELECT p.session_id,c.event_id
            FROM local_workspace_sessions p
            LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
            LEFT JOIN session_event_content c ON c.event_id=e.event_id AND c.expires_at=p.label_expires_at
            ORDER BY p.session_id COLLATE BINARY LIMIT 10001;
            """;
        using var reader=command.ExecuteReader(); var details=new List<string>(); while(reader.Read())details.Add(reader.GetString(3));
        Assert.Contains(details,detail=>detail.Contains("local_workspace_sessions",StringComparison.Ordinal)&&detail.Contains("INDEX",StringComparison.Ordinal));
        Assert.Contains(details,detail=>detail.Contains("session_events",StringComparison.Ordinal)&&detail.Contains("INDEX",StringComparison.Ordinal));
        Assert.Contains(details,detail=>detail.Contains("session_event_content",StringComparison.Ordinal)&&detail.Contains("INDEX",StringComparison.Ordinal));
        Assert.DoesNotContain(details,detail=>detail.Contains("CORRELATED",StringComparison.Ordinal)||detail.Contains("TEMP B-TREE",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-08-25T23:59:59.9999999+00:00", "recorded", "hello")]
    [InlineData("2026-08-26T00:00:00.0000000+00:00", "expired", null)]
    [InlineData("2026-08-26T00:00:00.0000001+00:00", "expired", null)]
    public async Task ContributorMasksLabelAtAndAfterExactContentExpiry(string now, string state, string? text)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"hello"}','2026-08-24T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse(now)))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var row = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions));
        Assert.Equal(state, row.LabelState);
        Assert.Equal(text, row.LabelText);
    }

    [Fact]
    public async Task ContributorExcludesExpiredLabelAndToolSearchFactsAtReadTime()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,tool_name TEXT);
            CREATE TABLE retention_items(store_kind TEXT,source_item_id TEXT,state TEXT,read_denied_at TEXT,deleted_at TEXT,error_code TEXT,expires_at TEXT);
            INSERT INTO local_workspace_session_search_facts VALUES
              ('0198f5b8-0c00-7000-8000-000000000001','label','label-1','expired label','2026-08-26T00:00:00.0000000+00:00'),
              ('0198f5b8-0c00-7000-8000-000000000001','tool','1:0','expired tool','2026-08-26T00:00:00.0000000+00:00');
            """);

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Empty(Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).SearchTexts);
    }

    [Fact]
    public async Task WorkspaceRefreshRejectsToolNameOnNonToolStructuralSpan()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            PRAGMA foreign_keys=OFF;
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'trace-1','span-1',NULL,'otel-exact','trace-1/span-1','tool.execution_start','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            PRAGMA foreign_keys=ON;
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,input_tokens INTEGER,output_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER);
            CREATE TABLE retention_items(store_kind TEXT,source_item_id TEXT,state TEXT,read_denied_at TEXT,deleted_at TEXT,error_code TEXT,expires_at TEXT);
            INSERT INTO raw_records VALUES(1);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name) VALUES(1,'trace-1','span-1',0,'chat','llm_call','misleading-tool');
            INSERT INTO retention_items VALUES('raw_record','1','expiring',NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00');
            """);
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.DoesNotContain("misleading-tool", Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).SearchTexts);
    }

    [Fact]
    public void OtelCurrentSkillSearchFactRequiresReadableRetainedRawAtAcceptedInstant()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO schema_version(component,version) VALUES('skill_projection',1);
            CREATE TABLE skill_projection_generations(generation_id INTEGER,compatibility_revision INTEGER,lifecycle TEXT);
            CREATE TABLE skill_projection_trace_heads(trace_id TEXT,current_generation_id INTEGER);
            CREATE TABLE source_trace_compatibility_revisions(trace_id TEXT,current_revision INTEGER,current_effective_state TEXT,current_exact_version TEXT);
            CREATE TABLE skill_projection_generation_inputs(generation_id INTEGER,input_evidence_kind TEXT);
            CREATE TABLE skill_projection_invocations(invocation_id INTEGER,generation_id INTEGER,source_arm TEXT,raw_record_id INTEGER,span_ordinal INTEGER,trace_id TEXT,session_id TEXT,skill_name TEXT,source_application_version TEXT);
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY);
            CREATE TABLE retention_items(store_kind TEXT,source_item_id TEXT,state TEXT,read_denied_at TEXT,deleted_at TEXT,error_code TEXT,expires_at TEXT);
            INSERT INTO skill_projection_generations VALUES(1,7,'current');
            INSERT INTO skill_projection_trace_heads VALUES('trace-1',1);
            INSERT INTO source_trace_compatibility_revisions VALUES('trace-1',7,'resolved','1.0.0');
            INSERT INTO skill_projection_invocations VALUES(11,1,'otel_trace_span',41,3,'trace-1','0198f5b8-0c00-7000-8000-000000000001','retained-skill','1.0.0');
            INSERT INTO raw_records VALUES(41);
            INSERT INTO retention_items VALUES('raw_record','41','expiring',NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00');
            """);
        using var transaction = connection.BeginTransaction();

        var beforeExpiry = SkillProjectionReadService.ReadCurrentOtelSearchFacts(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var atExpiry = SkillProjectionReadService.ReadCurrentOtelSearchFacts(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM raw_records WHERE id=41;";
            delete.ExecuteNonQuery();
        }
        var afterDelete = SkillProjectionReadService.ReadCurrentOtelSearchFacts(connection, transaction, ["0198f5b8-0c00-7000-8000-000000000001"], DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal("2026-08-26T00:00:00.0000000+00:00", Assert.Single(beforeExpiry).ExpiresAt);
        Assert.Equal("41:3", Assert.Single(beforeExpiry).SourceIdentity);
        Assert.Empty(atExpiry);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task ContributorDoesNotExposePartialTokenSumsAndFailsOverflowClosed()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','full',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('run-1','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,NULL,NULL,9223372036854775807,2,NULL,'active');
            INSERT INTO session_runs VALUES('run-2','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,NULL,NULL,1,NULL,NULL,'active');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var tokens = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).Tokens;
        Assert.Equal("oversized", tokens.State);
        Assert.Equal("oversized", tokens.Input.State);
        Assert.Null(tokens.Input.Value);
        Assert.Equal("capture_gap", tokens.Output.State);
        Assert.Null(tokens.Output.Value);
        Assert.Equal(2, tokens.AvailableExecutionCount);
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

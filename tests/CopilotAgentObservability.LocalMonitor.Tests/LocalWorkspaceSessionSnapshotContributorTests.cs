namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionSnapshotContributorTests
{
    [Fact]
    public async Task ExactComparisonReadIsolatesTypedInitialProjectionFailurePerTarget()
    {
        const string unavailable = "0198f5b8-0c00-7000-8000-000000000001";
        const string available = "0198f5b8-0c00-7000-8000-000000000002";
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"""
            INSERT INTO sessions VALUES('{unavailable}','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO sessions VALUES('{available}','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"PRAGMA ignore_check_constraints=ON; UPDATE local_workspace_sessions SET label_state='invalid_owner_state' WHERE session_id='{unavailable}'; PRAGMA ignore_check_constraints=OFF;");
        var contributor = new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")));

        var result = await contributor.ReadAsync(
            new TestReadTransaction(connection),
            new(LocalRepositoryScopeKind.Repository, "01900000-0000-7000-8000-000000000001", ExactTargetSessionIds: [unavailable, available]),
            CancellationToken.None);

        Assert.Equal([unavailable, available], result.Sessions.Select(static row => row.SessionId));
        Assert.IsType<LocalUnavailableRepositorySessionSnapshotRow>(result.Sessions[0]);
        Assert.IsType<LocalWorkspaceProjectionRow>(result.Sessions[1]);
        var failure = Assert.Single(result.ProjectionErrors!);
        Assert.Equal(unavailable, failure.Key);
        Assert.Equal("local_monitor_ui_unavailable", failure.Value);
    }

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
        Assert.Equal(["sessions", "sources", "models", "search", "activity", "tokens", "capture"], statements);
        Assert.All(result.Sessions, row => Assert.Equal("not_observed", ((LocalWorkspaceProjectionRow)row).Activity.Skill.State));
    }

    [Fact]
    public async Task ContributorWithInstalledSkillProjectionReadsTenThousandCandidatesInEightSetBasedStatements()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<10000)
            INSERT INTO sessions
            SELECT printf('0198f5b8-0c00-7000-8000-%012d',value),'completed','partial',NULL,NULL,NULL,NULL,
                   '2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'
            FROM n;
            """);
        using (var transaction = connection.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        var statements = new List<string>();
        var result = await new LocalWorkspaceSessionSnapshotContributor(statementObserver: statements.Add).ReadAsync(
            new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(10_000, result.Sessions.Count);
        Assert.Equal(["sessions", "sources", "models", "search", "activity", "tokens", "capture"], statements);
        Assert.All(result.Sessions, row => Assert.Equal("not_observed", ((LocalWorkspaceProjectionRow)row).Activity.Skill.State));

        using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT invocation.session_id,COUNT(*) FROM skill_projection_invocations invocation WHERE invocation.source_arm='otel_trace_span' AND invocation.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) GROUP BY invocation.session_id;";
        plan.Parameters.AddWithValue("$ids", "[\"0198f5b8-0c00-7000-8000-000000000001\"]");
        using var reader = plan.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains("invocation", StringComparison.Ordinal));
        Assert.Contains(details, detail => detail.Contains("json_each", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("CORRELATED", StringComparison.Ordinal));
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
        using (var transaction = connection.BeginTransaction())
        {
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,tool_name TEXT,status TEXT);
            INSERT INTO local_workspace_session_search_facts VALUES
              ('0198f5b8-0c00-7000-8000-000000000001','label','label-1','expired label','2026-08-26T00:00:00.0000000+00:00'),
              ('0198f5b8-0c00-7000-8000-000000000001','tool','1:0','expired tool','2026-08-26T00:00:00.0000000+00:00');
            """);

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Empty(Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).SearchTexts);
    }

    [Theory]
    [InlineData("chat", "llm_call", false)]
    [InlineData("execute_tool", "tool_call", true)]
    public async Task WorkspaceRefreshAcceptsToolSearchFactOnlyForExactToolSpanKind(string operation, string category, bool expected)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            PRAGMA foreign_keys=OFF;
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state)
            VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,'otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','available');
            PRAGMA foreign_keys=ON;
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using (var transaction = connection.BeginTransaction())
        {
            CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(connection, transaction);
            transaction.Commit();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,input_tokens INTEGER,output_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,parent_span_id TEXT);
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00',NULL,'{}',1,randomblob(32));
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name) VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,$operation,$category,'exact-tool');
            """;
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$category", category);
            command.ExecuteNonQuery();
        }
        using (var rawOwner = connection.CreateCommand())
        {
            rawOwner.CommandText = "SELECT retention_owner_token FROM raw_records WHERE id=1;";
            var ownerToken = Assert.IsType<byte[]>(rawOwner.ExecuteScalar());
            using var transaction = connection.BeginTransaction();
            new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(
                "fixture", new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")))
                .RegisterRawRecord(connection, transaction, 1, DateTimeOffset.Parse("2026-08-24T00:00:00Z"), 1, ownerToken);
            transaction.Commit();
        }
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RefreshStructural(connection, transaction, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            transaction.Commit();
        }

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        var searchTexts = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).SearchTexts;
        if (expected) Assert.Contains("exact-tool", searchTexts);
        else Assert.DoesNotContain("exact-tool", searchTexts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributorUsesLlmCoverageAndRetainsExactSpanCacheWithoutDoubleCountingRunUsage(bool missingCallUsage)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs(run_id,session_id,input_tokens,output_tokens,status) VALUES
              ('llm','0198f5b8-0c00-7000-8000-000000000001',100,7,'active'),
              ('tool','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','llm','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,input_tokens INTEGER,output_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,parent_span_id TEXT);
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00',NULL,'{}',1);
            INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'chat','llm_call',NULL,100,7,NULL,60,NULL,NULL,NULL);
            """);
        if (missingCallUsage)
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, """
                UPDATE monitor_spans SET cache_read_tokens=NULL;
                INSERT INTO session_runs(run_id,session_id,status) VALUES('missing-llm','0198f5b8-0c00-7000-8000-000000000001','active');
                INSERT INTO session_events(event_id,session_id,run_id,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','missing-llm','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cccccccccccccccc','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
                INSERT INTO monitor_spans VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','cccccccccccccccc',1,'chat','llm_call',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
                """);
        }
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z")))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var tokens = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions)).Tokens;
        if (missingCallUsage)
        {
            Assert.Equal("session_run", tokens.Authority);
            Assert.Equal(2, tokens.TotalExecutionCount);
            Assert.Equal(1, tokens.AvailableExecutionCount);
            Assert.Equal(new LocalWorkspaceFact<long>("capture_gap", null), tokens.Input);
            Assert.Equal(new LocalWorkspaceFact<long>("capture_gap", null), tokens.Output);
            Assert.Equal(new LocalWorkspaceFact<long>("not_observed", null), tokens.CacheRead);
            Assert.Equal(new LocalWorkspaceFact<long>("not_observed", null), tokens.Total);
            return;
        }
        Assert.Equal("mixed", tokens.Authority);
        Assert.Equal(1, tokens.TotalExecutionCount);
        Assert.Equal(1, tokens.AvailableExecutionCount);
        Assert.Equal(new LocalWorkspaceFact<long>("recorded", 100), tokens.Input);
        Assert.Equal(new LocalWorkspaceFact<long>("recorded", 7), tokens.Output);
        Assert.Equal(new LocalWorkspaceFact<long>("recorded", 60), tokens.CacheRead);
        Assert.Equal(new LocalWorkspaceFact<long>("not_observed", null), tokens.Total);
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
        Assert.Equal(new LocalWorkspaceFact<long>("recorded", 2), tokens.Observations!["output"].Subtotal);
        Assert.Equal(1, tokens.Observations["output"].ObservedCallCount);
        Assert.Equal(2, tokens.Observations["output"].ApplicableCallCount);
        Assert.Equal("oversized", tokens.Observations["input"].Subtotal.State);
        Assert.Equal(2, tokens.AvailableExecutionCount);
    }

    [Theory]
    [InlineData(0, "recorded", 0L)]
    [InlineData(60, "recorded", 6000L)]
    [InlineData(101, "inconsistent", null)]
    public void PartialCacheUsesItsObservedCallsAndPairedInput(long cache, string ratioState, long? ratio)
    {
        var missing = new LocalWorkspaceFact<long>("not_observed", null);
        LocalWorkspaceTokenFacts Call(long input, long? read) => new("llm_span", "recorded", 1, 1,
            new("recorded", input), missing, missing, missing,
            read is null ? missing : new("recorded", read), missing, missing, missing);
        var tokens = LocalWorkspaceSessionSnapshotContributor.AggregateCalls([Call(100, cache), Call(500, null)]);
        Assert.Equal(600, tokens.Input.Value);
        Assert.Equal(new LocalWorkspaceFact<long>("capture_gap", null), tokens.CacheRead);
        var observed = tokens.Observations!["cache_read"];
        Assert.Equal(cache, observed.Subtotal.Value);
        Assert.Equal(1, observed.ObservedCallCount);
        Assert.Equal(2, observed.ApplicableCallCount);
        var observedRatio = tokens.Observations["cache_read_ratio_basis_points"];
        Assert.Equal(100, observedRatio.PairedInput);
        Assert.Equal(new LocalWorkspaceFact<long>(ratioState, ratio), observedRatio.Subtotal);
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

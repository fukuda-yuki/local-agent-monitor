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
        Assert.Equal(["sessions", "sources", "models", "activity", "skills", "tokens"], statements);
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
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user_prompt','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"message":"hello"}','2026-08-24T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        var result = await new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(DateTimeOffset.Parse(now)))
            .ReadAsync(new TestReadTransaction(connection), new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var row = Assert.IsType<LocalWorkspaceProjectionRow>(Assert.Single(result.Sessions));
        Assert.Equal(state, row.LabelState);
        Assert.Equal(text, row.LabelText);
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

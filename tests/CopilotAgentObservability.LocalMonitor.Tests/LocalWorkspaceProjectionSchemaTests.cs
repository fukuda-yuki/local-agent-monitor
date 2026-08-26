using Microsoft.Data.Sqlite;

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
            ["local_workspace_projection:4"],
            Strings(connection, "SELECT component || ':' || version FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(
            ["local_workspace_execution_headers", "local_workspace_node_content_refs", "local_workspace_node_edges", "local_workspace_nodes", "local_workspace_projection_state", "local_workspace_session_activity", "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_sources", "local_workspace_sessions", "local_workspace_span_facts", "local_workspace_token_observations"],
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
        Assert.Equal(["4"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
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
            Assert.Equal(["4"], Strings(transaction, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
            transaction.Rollback();
        }

        Assert.Equal(["1"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name IN ('local_workspace_span_facts','local_workspace_session_search_facts');"));

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);

        Assert.Equal(["4"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
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
        Assert.Equal(["4"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
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
            Assert.Equal(["4"], Strings(transaction, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
            transaction.Rollback();
        }

        Assert.Equal(["3"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Empty(Strings(connection, "SELECT name FROM sqlite_schema WHERE name='local_workspace_nodes';"));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void ExecutionBoundIsExactAndNeverTruncates(int count, bool succeeds)
    {
        using var connection = OpenSessionDatabase();
        Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        for (var index = 0; index < count; index++)
            Execute(connection, $"INSERT INTO session_runs VALUES('run-{index:D4}','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");

        if (succeeds)
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
            Assert.Equal(count, Strings(connection, "SELECT execution_id FROM local_workspace_execution_headers;").Length);
        }
        else
        {
            Assert.Equal("local_workspace_projection_workspace_too_large", Assert.Throws<InvalidOperationException>(() =>
                LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch)).Message);
            Assert.Empty(Strings(connection, "SELECT component FROM schema_version WHERE component='local_workspace_projection';"));
        }
    }

    [Fact]
    public void ExactSourceExecutionIdentityIsInvariantAcrossSessions()
    {
        var first = LocalWorkspaceProjectionStore.StableExecutionId("session-a", "session_run", "run-1");
        var second = LocalWorkspaceProjectionStore.StableExecutionId("session-b", "session_run", "run-1");

        Assert.Equal(first, second);
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
}

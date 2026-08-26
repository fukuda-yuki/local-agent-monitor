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
            ["local_workspace_projection:3"],
            Strings(connection, "SELECT component || ':' || version FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(
            ["local_workspace_projection_state", "local_workspace_session_activity", "local_workspace_session_models", "local_workspace_session_search_facts", "local_workspace_session_sources", "local_workspace_sessions", "local_workspace_span_facts", "local_workspace_token_observations"],
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
        Assert.Equal(["3"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(["local_workspace_span_facts"], Strings(connection, "SELECT name FROM sqlite_schema WHERE name='local_workspace_span_facts';"));
    }

    [Fact]
    public void EnsureRejectsDriftedCurrentShape()
    {
        using var connection = OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        Execute(connection, "ALTER TABLE local_workspace_sessions ADD COLUMN drift TEXT;");

        Assert.Throws<InvalidOperationException>(() => LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch));
        Assert.Equal(["3"], Strings(connection, "SELECT CAST(version AS TEXT) FROM schema_version WHERE component='local_workspace_projection';"));
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
}

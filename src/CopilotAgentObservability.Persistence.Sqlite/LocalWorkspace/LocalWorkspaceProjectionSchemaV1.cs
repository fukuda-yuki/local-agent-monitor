using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionSchemaV1
{
    internal const string ComponentName = "local_workspace_projection";
    internal const int Version = 1;
    internal static readonly string[] TableNames =
    [
        "local_workspace_sessions", "local_workspace_session_sources",
        "local_workspace_session_models", "local_workspace_session_activity",
        "local_workspace_token_observations", "local_workspace_projection_state",
    ];

    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
    [
        new("table", "local_workspace_sessions", "local_workspace_sessions", """
            CREATE TABLE local_workspace_sessions (
                session_id TEXT PRIMARY KEY,
                sort_group INTEGER NOT NULL CHECK(sort_group BETWEEN 0 AND 2),
                sort_epoch_ms INTEGER NOT NULL CHECK(sort_epoch_ms >= 0),
                label_state TEXT NOT NULL,
                label_text TEXT NULL,
                label_search_text TEXT NULL,
                label_source_identity TEXT NULL,
                label_expires_at TEXT NULL,
                status TEXT NOT NULL CHECK(status IN ('active','completed','failed','unknown')),
                completeness TEXT NOT NULL CHECK(completeness IN ('unbound','partial','rich','full')),
                timing_state TEXT NOT NULL,
                started_at TEXT NULL,
                ended_at TEXT NULL,
                duration_ms INTEGER NULL CHECK(duration_ms IS NULL OR duration_ms >= 0),
                capture_notes TEXT NOT NULL,
                revision_seed TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((label_state='recorded' AND label_text IS NOT NULL AND label_search_text IS NOT NULL AND label_source_identity IS NOT NULL AND label_expires_at IS NOT NULL) OR (label_state<>'recorded' AND label_text IS NULL AND label_search_text IS NULL AND label_source_identity IS NULL AND label_expires_at IS NULL))
            );
            """),
        new("table", "local_workspace_session_sources", "local_workspace_session_sources", """
            CREATE TABLE local_workspace_session_sources (
                session_id TEXT NOT NULL,
                source TEXT NOT NULL,
                PRIMARY KEY(session_id,source),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_session_models", "local_workspace_session_models", """
            CREATE TABLE local_workspace_session_models (
                session_id TEXT NOT NULL,
                model TEXT NOT NULL,
                PRIMARY KEY(session_id,model),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_session_activity", "local_workspace_session_activity", """
            CREATE TABLE local_workspace_session_activity (
                session_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('skill','tool','subagent','error','retry')),
                state TEXT NOT NULL,
                count INTEGER NULL CHECK(count IS NULL OR count >= 0),
                PRIMARY KEY(session_id,kind),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE,
                CHECK((state='recorded')=(count IS NOT NULL))
            );
            """),
        new("table", "local_workspace_token_observations", "local_workspace_token_observations", """
            CREATE TABLE local_workspace_token_observations (
                session_id TEXT NOT NULL,
                execution_id TEXT NOT NULL,
                authority TEXT NOT NULL CHECK(authority IN ('session_run','llm_span')),
                authority_rank INTEGER NOT NULL CHECK((authority='session_run' AND authority_rank=0) OR (authority='llm_span' AND authority_rank=1)),
                source_identity TEXT NOT NULL,
                input_tokens INTEGER NULL CHECK(input_tokens IS NULL OR input_tokens >= 0),
                output_tokens INTEGER NULL CHECK(output_tokens IS NULL OR output_tokens >= 0),
                total_tokens INTEGER NULL CHECK(total_tokens IS NULL OR total_tokens >= 0),
                reasoning_tokens INTEGER NULL CHECK(reasoning_tokens IS NULL OR reasoning_tokens >= 0),
                cache_read_tokens INTEGER NULL CHECK(cache_read_tokens IS NULL OR cache_read_tokens >= 0),
                cache_creation_tokens INTEGER NULL CHECK(cache_creation_tokens IS NULL OR cache_creation_tokens >= 0),
                PRIMARY KEY(session_id,execution_id,authority,source_identity),
                FOREIGN KEY(session_id) REFERENCES local_workspace_sessions(session_id) ON UPDATE RESTRICT ON DELETE CASCADE
            );
            """),
        new("table", "local_workspace_projection_state", "local_workspace_projection_state", """
            CREATE TABLE local_workspace_projection_state (
                projector_key TEXT PRIMARY KEY CHECK(projector_key='local-workspace-projection-v1'),
                session_frontier TEXT NULL,
                refreshed_at TEXT NOT NULL
            );
            """),
    ];

    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ExpectedObjects { get; } =
        SqliteOwnedSchemaAuthority.Compile(Definitions);
    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects => ExpectedObjects.Values;

    internal static void Ensure(SqliteConnection connection, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction, now);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!HasSessionV14(connection, transaction))
            throw new InvalidOperationException("local_workspace_projection_component_dependency_invalid");
        var version = ReadVersion(connection, transaction);
        var owned = ReadOwnedObjects(connection, transaction);
        if (version is not null || owned.Count != 0)
        {
            Validate(connection, transaction);
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, now);
            return;
        }
        foreach (var definition in Definitions) Execute(connection, transaction, definition.Sql);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',1);");
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, now);
        Validate(connection, transaction);
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ReadVersion(connection, transaction) != Version
            || !SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects))
            throw new InvalidOperationException("Unsupported incomplete local_workspace_projection schema version 1.");
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase));

    private static long? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version,typeof(version) FROM schema_version WHERE component='local_workspace_projection';";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(1) != "integer") return long.MinValue;
        var value = reader.GetInt64(0);
        return reader.Read() ? long.MinValue : value;
    }

    private static bool HasSessionV14(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (SELECT version FROM schema_version WHERE component='session'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='sessions'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_runs'),
                   EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='session_events');
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && reader.GetInt64(0) == 14
            && reader.GetInt64(1) == 1 && reader.GetInt64(2) == 1 && reader.GetInt64(3) == 1;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

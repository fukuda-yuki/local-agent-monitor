namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SemanticAttributeCaptureSchema
{
    internal static readonly (string Name, string Sql)[] Tables =
    [
        ("source_semantic_captures", """
        CREATE TABLE source_semantic_captures (
            source_family TEXT NOT NULL CHECK(source_family IN ('copilot-cli','vscode-copilot-chat')),
            state TEXT NOT NULL CHECK(state IN ('active','completed')),
            capture_id TEXT NOT NULL UNIQUE,
            baseline_id TEXT NOT NULL CHECK(baseline_id='issue-129-ae02f8a7-v1'),
            started_at TEXT NOT NULL,
            completed_at TEXT NULL,
            expires_at TEXT NOT NULL,
            incomplete INTEGER NOT NULL CHECK(incomplete IN (0,1)),
            observation_count INTEGER NOT NULL CHECK(observation_count BETWEEN 0 AND 1000000),
            PRIMARY KEY(source_family,state),
            CHECK((state='active' AND completed_at IS NULL) OR (state='completed' AND completed_at IS NOT NULL))
        )
        """),
        ("source_semantic_capture_keys", """
        CREATE TABLE source_semantic_capture_keys (
            capture_id TEXT NOT NULL,
            key_hash TEXT NOT NULL CHECK(length(key_hash)=71 AND substr(key_hash,1,7)='sha256:' AND substr(key_hash,8) NOT GLOB '*[^0-9a-f]*'),
            occurrence_count INTEGER NOT NULL CHECK(occurrence_count BETWEEN 1 AND 1000000),
            PRIMARY KEY(capture_id,key_hash),
            FOREIGN KEY(capture_id) REFERENCES source_semantic_captures(capture_id) ON DELETE CASCADE
        )
        """)
    ];

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction, long? version)
    {
        var actual = SqliteOwnedSchemaAuthority.Read(connection, transaction,
            (name, table) => name.StartsWith("source_semantic_", StringComparison.OrdinalIgnoreCase)
                || Tables.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, table)));
        if (version < 12 || version is null)
        {
            if (actual.Count != 0) throw new InvalidOperationException("Colliding semantic capture authority.");
            return;
        }
        var expected = SqliteOwnedSchemaAuthority.Compile(Tables.Select(table =>
            new SqliteOwnedSchemaDefinition("table", table.Name, table.Name, table.Sql)).ToArray());
        if (!SqliteOwnedSchemaAuthority.Equal(actual, expected))
            throw new InvalidOperationException("Unsupported incomplete monitor schema version 12.");
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction, long? version)
    {
        if (version >= 12) return;
        foreach (var table in Tables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = table.Sql;
            command.ExecuteNonQuery();
        }
    }
}

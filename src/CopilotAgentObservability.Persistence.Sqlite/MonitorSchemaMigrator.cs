namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class MonitorSchemaMigrator
{
    public const int BaseSchemaVersion = 4;

    public static void EnsureRawRecordsSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS raw_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL CHECK (source IN ('raw-otlp', 'collector-output', 'langfuse-export')),
                trace_id TEXT NULL,
                received_at TEXT NOT NULL,
                resource_attributes_json TEXT NULL,
                payload_json TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version = 1)
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_raw_records_trace_id ON raw_records(trace_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_raw_records_received_at ON raw_records(received_at);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_raw_records_source ON raw_records(source);");
    }

    public static void ApplyBaseSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureRawRecordsSchema(connection, transaction);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_ingestions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL UNIQUE,
                received_at TEXT NOT NULL,
                source TEXT NOT NULL,
                trace_id TEXT NULL,
                client_kind TEXT NULL,
                span_count INTEGER NULL,
                projected_at TEXT NOT NULL,
                span_projected_at TEXT NULL
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL UNIQUE,
                client_kind TEXT NULL,
                experiment_id TEXT NULL,
                task_id TEXT NULL,
                task_category TEXT NULL,
                agent_variant TEXT NULL,
                prompt_version TEXT NULL,
                span_count INTEGER NULL,
                tool_call_count INTEGER NULL,
                error_count INTEGER NULL,
                first_seen_at TEXT NULL,
                last_seen_at TEXT NULL,
                projected_at TEXT NOT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                total_tokens INTEGER NULL,
                turn_count INTEGER NULL,
                agent_invocation_count INTEGER NULL,
                duration_ms REAL NULL,
                primary_model TEXT NULL,
                repository_name TEXT NULL,
                workspace_label TEXT NULL,
                repo_snapshot TEXT NULL,
                cache_read_tokens INTEGER NULL,
                cache_creation_tokens INTEGER NULL,
                trace_status TEXT NULL
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_spans (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                span_id TEXT NULL,
                parent_span_id TEXT NULL,
                span_ordinal INTEGER NOT NULL,
                operation TEXT NULL,
                category TEXT NULL,
                tool_name TEXT NULL,
                tool_type TEXT NULL,
                mcp_tool_name TEXT NULL,
                mcp_server_hash TEXT NULL,
                agent_name TEXT NULL,
                request_model TEXT NULL,
                response_model TEXT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                total_tokens INTEGER NULL,
                reasoning_tokens INTEGER NULL,
                cache_read_tokens INTEGER NULL,
                cache_creation_tokens INTEGER NULL,
                status TEXT NULL,
                error_type TEXT NULL,
                finish_reasons TEXT NULL,
                conversation_id TEXT NULL,
                duration_ms REAL NULL,
                start_time TEXT NULL,
                end_time TEXT NULL,
                projected_at TEXT NOT NULL,
                UNIQUE(raw_record_id, span_ordinal)
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_monitor_spans_trace_id ON monitor_spans(trace_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_monitor_spans_raw_record_id ON monitor_spans(raw_record_id);");

        AddColumnIfMissing(connection, transaction, "monitor_ingestions", "span_projected_at", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "input_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "output_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "total_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "turn_count", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "agent_invocation_count", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "duration_ms", "REAL NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "primary_model", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "repository_name", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "workspace_label", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "repo_snapshot", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "cache_read_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "cache_creation_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "monitor_traces", "trace_status", "TEXT NULL");

        if (ReadMonitorSchemaVersion(connection, transaction) is not { } currentVersion || currentVersion <= BaseSchemaVersion)
        {
            SetMonitorSchemaVersion(connection, transaction, BaseSchemaVersion);
        }
    }

    public static void EnsureProjectionDispositionSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_projection_dispositions (
                raw_record_id INTEGER PRIMARY KEY,
                state TEXT NOT NULL CHECK (state IN ('not_started', 'pending', 'completed', 'failed')),
                revision INTEGER NOT NULL CHECK (revision > 0),
                updated_at TEXT NOT NULL
            );
            """);
        Execute(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO monitor_projection_dispositions (raw_record_id, state, revision, updated_at)
            SELECT projected.raw_record_id,
                   'completed',
                   1,
                   projected.projected_at
            FROM monitor_ingestions AS projected;
            """);
    }

    public static void EnsureRuntimeStateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_runtime_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                raw_access TEXT NOT NULL CHECK (raw_access IN ('available', 'sanitized_only')),
                updated_at TEXT NOT NULL
            );
            """);
    }

    public static long? ReadMonitorSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        if ((long)tableCommand.ExecuteScalar()! == 0)
        {
            return null;
        }

        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT version FROM schema_version WHERE component = 'monitor';";
        var value = versionCommand.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static void SetMonitorSchemaVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO schema_version (component, version)
            VALUES ('monitor', $version)
            ON CONFLICT (component) DO UPDATE SET version = excluded.version;
            """;
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string columnDdl)
    {
        var exists = false;
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {columnDdl};");
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

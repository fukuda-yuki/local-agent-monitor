namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class AlertLifecycleSchemaV1
{
    public const int Version = 1;
    public const string Component = "alert_lifecycle";

    internal const string TableSql =
        """
        CREATE TABLE alert_lifecycle_events (
            event_id TEXT NOT NULL PRIMARY KEY CHECK(length(event_id)=64 AND event_id=lower(event_id) AND event_id NOT GLOB '*[^0-9a-f]*'),
            alert_id TEXT NOT NULL CHECK(length(alert_id)=64 AND alert_id=lower(alert_id) AND alert_id NOT GLOB '*[^0-9a-f]*'),
            revision INTEGER NOT NULL CHECK(revision>=1),
            expected_revision INTEGER NOT NULL CHECK(expected_revision=revision-1),
            action TEXT NOT NULL CHECK(action IN ('acknowledge','dismiss','resolve','reopen','supersede','source_deleted')),
            previous_state TEXT NOT NULL CHECK(previous_state IN ('open','acknowledged','dismissed','resolved','superseded')),
            state TEXT NOT NULL CHECK(state IN ('open','acknowledged','dismissed','resolved','superseded')),
            occurred_at TEXT NOT NULL,
            actor TEXT NOT NULL CHECK(actor='local_user'),
            reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 64 AND reason_code NOT GLOB '*[^a-z0-9._-]*'),
            comment TEXT NULL CHECK(comment IS NULL OR length(comment) BETWEEN 1 AND 256),
            idempotency_key TEXT NOT NULL UNIQUE CHECK(length(idempotency_key)=48 AND substr(idempotency_key,1,5)='aid1_' AND substr(idempotency_key,6) NOT GLOB '*[^A-Za-z0-9_-]*'),
            request_hash TEXT NOT NULL CHECK(length(request_hash)=64 AND request_hash=lower(request_hash) AND request_hash NOT GLOB '*[^0-9a-f]*'),
            old_alert_id TEXT NULL CHECK(old_alert_id IS NULL OR (length(old_alert_id)=64 AND old_alert_id=lower(old_alert_id) AND old_alert_id NOT GLOB '*[^0-9a-f]*')),
            new_alert_id TEXT NULL CHECK(new_alert_id IS NULL OR (length(new_alert_id)=64 AND new_alert_id=lower(new_alert_id) AND new_alert_id NOT GLOB '*[^0-9a-f]*')),
            result_code TEXT NOT NULL CHECK(result_code='alert_lifecycle_updated'),
            FOREIGN KEY(alert_id) REFERENCES alert_receipts(alert_id),
            UNIQUE(alert_id,revision)
        );
        """;

    internal const string UpdateTriggerSql =
        "CREATE TRIGGER alert_lifecycle_events_no_update BEFORE UPDATE ON alert_lifecycle_events BEGIN SELECT RAISE(ABORT,'alert_lifecycle_append_only'); END;";
    internal const string DeleteTriggerSql =
        "CREATE TRIGGER alert_lifecycle_events_no_delete BEFORE DELETE ON alert_lifecycle_events BEGIN SELECT RAISE(ABORT,'alert_lifecycle_append_only'); END;";

    public static long? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!Exists(connection, transaction, "table", "schema_version")) return null;
        using var command = Command(connection, transaction, "SELECT version FROM schema_version WHERE component='alert_lifecycle';");
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static bool AnyObjectsExist(SqliteConnection connection, SqliteTransaction? transaction) =>
        Exists(connection, transaction, "table", "alert_lifecycle_events")
        || Exists(connection, transaction, "trigger", "alert_lifecycle_events_no_update")
        || Exists(connection, transaction, "trigger", "alert_lifecycle_events_no_delete");

    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        Execute(connection, transaction, TableSql);
        Execute(connection, transaction, UpdateTriggerSql);
        Execute(connection, transaction, DeleteTriggerSql);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('alert_lifecycle',1);");
    }

    public static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction) =>
        ReadVersion(connection, transaction) == Version
        && Definition(connection, transaction, "table", "alert_lifecycle_events") is { } table && Normalize(table) == Normalize(TableSql)
        && Definition(connection, transaction, "trigger", "alert_lifecycle_events_no_update") is { } update && Normalize(update) == Normalize(UpdateTriggerSql)
        && Definition(connection, transaction, "trigger", "alert_lifecycle_events_no_delete") is { } delete && Normalize(delete) == Normalize(DeleteTriggerSql);

    private static string? Definition(SqliteConnection connection, SqliteTransaction? transaction, string type, string name)
    {
        using var command = Command(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type=$type AND name=$name;", ("$type", type), ("$name", name));
        return command.ExecuteScalar() as string;
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string type, string name)
    {
        using var command = Command(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE type=$type AND name=$name;", ("$type", type), ("$name", name));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = Command(connection, transaction, sql); command.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}

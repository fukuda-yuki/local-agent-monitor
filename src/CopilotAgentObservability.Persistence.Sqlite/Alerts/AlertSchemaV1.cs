namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class AlertSchemaV1
{
    public const int Version = 1;
    public const string Component = "alert_engine";

    private static readonly IReadOnlyDictionary<string, string> TableSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alert_evaluations"] =
            """
            CREATE TABLE alert_evaluations (
                evaluation_id TEXT NOT NULL PRIMARY KEY CHECK(length(evaluation_id)=64 AND evaluation_id=lower(evaluation_id) AND evaluation_id NOT GLOB '*[^0-9a-f]*'),
                schema_version TEXT NOT NULL CHECK(schema_version='alert.evaluation.v1'),
                input_hash TEXT NOT NULL CHECK(length(input_hash)=64 AND input_hash=lower(input_hash) AND input_hash NOT GLOB '*[^0-9a-f]*'),
                configuration_version TEXT NOT NULL CHECK(length(configuration_version) BETWEEN 1 AND 128 AND configuration_version NOT GLOB '*[^a-z0-9._-]*'),
                configuration_hash TEXT NOT NULL CHECK(length(configuration_hash)=64 AND configuration_hash=lower(configuration_hash) AND configuration_hash NOT GLOB '*[^0-9a-f]*'),
                canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id)
            );
            """,
        ["alert_receipts"] =
            """
            CREATE TABLE alert_receipts (
                alert_id TEXT NOT NULL PRIMARY KEY CHECK(length(alert_id)=64 AND alert_id=lower(alert_id) AND alert_id NOT GLOB '*[^0-9a-f]*'),
                evaluation_id TEXT NOT NULL,
                receipt_ordinal INTEGER NOT NULL CHECK(receipt_ordinal>=0),
                schema_version TEXT NOT NULL CHECK(schema_version='alert.receipt.v1'),
                canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.alert_id')=alert_id AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id),
                FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id),
                UNIQUE(evaluation_id,receipt_ordinal)
            );
            """,
        ["alert_suppressions"] =
            """
            CREATE TABLE alert_suppressions (
                evaluation_id TEXT NOT NULL,
                suppression_ordinal INTEGER NOT NULL CHECK(suppression_ordinal>=0),
                rule_id TEXT NOT NULL CHECK(length(rule_id) BETWEEN 1 AND 128 AND rule_id NOT GLOB '*[^a-z0-9._-]*'),
                rule_version TEXT NOT NULL CHECK(length(rule_version) BETWEEN 1 AND 128 AND rule_version NOT GLOB '*[^a-z0-9._-]*'),
                code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 128 AND code NOT GLOB '*[^a-z0-9._-]*'),
                canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id),
                PRIMARY KEY(evaluation_id,suppression_ordinal),
                FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id)
            );
            """,
    };

    public static long? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!TableExists(connection, transaction, "schema_version")) return null;
        using var command = Command(connection, transaction, "SELECT version FROM schema_version WHERE component='alert_engine';");
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static bool AnyEngineTableExists(SqliteConnection connection, SqliteTransaction? transaction) =>
        TableSql.Keys.Any(table => TableExists(connection, transaction, table));

    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        foreach (var sql in TableSql.Values) Execute(connection, transaction, sql);
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('alert_engine',1);");
    }

    public static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ReadVersion(connection, transaction) != Version) return false;

        foreach (var table in TableSql)
        {
            using var command = Command(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table.Key));
            if (command.ExecuteScalar() is not string actual || NormalizeSql(actual) != NormalizeSql(table.Value)) return false;
        }

        return true;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = Command(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", name));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string NormalizeSql(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}

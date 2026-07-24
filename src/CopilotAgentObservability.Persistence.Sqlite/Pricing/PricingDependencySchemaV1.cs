using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal static class PricingDependencySchemaV1
{
    private const string SessionsTableSql = """
        CREATE TABLE sessions (
            session_id TEXT PRIMARY KEY,
            status TEXT NOT NULL CHECK (status IN ('active','completed','failed','unknown')),
            completeness TEXT NOT NULL CHECK (completeness IN ('unbound','partial','rich','full')),
            repository TEXT NULL,
            workspace TEXT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            last_seen_at TEXT NOT NULL,
            raw_retention_state TEXT NOT NULL CHECK (raw_retention_state IN ('expiring','expired_pending_deletion','not_captured')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private static readonly IReadOnlyDictionary<string, string> AlertV2TableSql =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alert_evaluations"] =
                """
                CREATE TABLE alert_evaluations (
                    evaluation_id TEXT NOT NULL PRIMARY KEY CHECK(length(evaluation_id)=64 AND evaluation_id=lower(evaluation_id) AND evaluation_id NOT GLOB '*[^0-9a-f]*'),
                    schema_version TEXT NOT NULL CHECK(schema_version IN ('alert.evaluation.v1','alert.evaluation.v2')),
                    input_hash TEXT NOT NULL CHECK(length(input_hash)=64 AND input_hash=lower(input_hash) AND input_hash NOT GLOB '*[^0-9a-f]*'),
                    configuration_version TEXT NOT NULL CHECK(length(configuration_version) BETWEEN 1 AND 128 AND substr(configuration_version,1,1) GLOB '[a-z0-9]' AND configuration_version NOT GLOB '*[^a-z0-9._-]*'),
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
                    schema_version TEXT NOT NULL CHECK(schema_version IN ('alert.receipt.v1','alert.receipt.v2')),
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
                    rule_id TEXT NOT NULL CHECK(length(rule_id) BETWEEN 1 AND 128 AND substr(rule_id,1,1) GLOB '[a-z0-9]' AND rule_id NOT GLOB '*[^a-z0-9._-]*'),
                    rule_version TEXT NOT NULL CHECK(length(rule_version) BETWEEN 1 AND 128 AND substr(rule_version,1,1) GLOB '[a-z0-9]' AND rule_version NOT GLOB '*[^a-z0-9._-]*'),
                    code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 128 AND substr(code,1,1) GLOB '[a-z0-9]' AND code NOT GLOB '*[^a-z0-9._-]*'),
                    canonical_json TEXT NOT NULL CHECK(json_valid(canonical_json) AND json_extract(canonical_json,'$.evaluation_id')=evaluation_id),
                    PRIMARY KEY(evaluation_id,suppression_ordinal),
                    FOREIGN KEY(evaluation_id) REFERENCES alert_evaluations(evaluation_id)
                );
                """,
        };

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!HasVersion(connection, transaction, "session", 13)
            || !HasVersion(connection, transaction, "alert_engine", 2)
            || !RuntimeBackupSchemaV1.IsValid(connection, transaction)
            || !DefinitionMatches(connection, transaction, "sessions", SessionsTableSql))
            return false;

        foreach (var table in AlertV2TableSql)
            if (!DefinitionMatches(connection, transaction, table.Key, table.Value))
                return false;

        using var inventory = Command(
            connection,
            transaction,
            """
            SELECT type,name,tbl_name FROM sqlite_schema
            WHERE (type='table' AND name IN ('alert_evaluations','alert_receipts','alert_suppressions'))
               OR (type IN ('index','trigger') AND sql IS NOT NULL
                   AND tbl_name IN ('alert_evaluations','alert_receipts','alert_suppressions'))
            ORDER BY type,name;
            """);
        using var reader = inventory.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names.SequenceEqual(AlertV2TableSql.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool HasVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string component,
        int version)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM schema_version WHERE component=$component AND version=$version;");
        command.Parameters.AddWithValue("$component", component);
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool DefinitionMatches(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string name,
        string expected)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;");
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is string actual
            && Normalize(actual) == Normalize(expected);
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd(';');

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}

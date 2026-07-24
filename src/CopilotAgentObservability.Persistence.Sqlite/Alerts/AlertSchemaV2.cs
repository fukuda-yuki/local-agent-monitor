using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class AlertSchemaV2
{
    public const int Version = 2;

    private static readonly IReadOnlyDictionary<string, string> TableSql =
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

    public static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (AlertSchemaV1.ReadVersion(connection, transaction) != Version) return false;
        foreach (var table in TableSql)
        {
            if (Definition(connection, transaction, "table", table.Key) is not { } actual
                || Normalize(actual) != Normalize(table.Value))
            {
                return false;
            }
        }
        return ExactOwnedInventory(connection, transaction);
    }

    public static bool IsRecognized(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        AlertSchemaV1.IsValid(connection, transaction)
        || IsValid(connection, transaction);

    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        foreach (var sql in TableSql.Values) Execute(connection, transaction, sql);
        Execute(
            connection,
            transaction,
            "INSERT INTO schema_version(component,version) VALUES('alert_engine',2);");
    }

    public static void MigrateFromV1(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!AlertSchemaV1.IsValid(connection, transaction)
            || !ExactOwnedInventory(connection, transaction)
            || TemporaryObjectsExist(connection, transaction))
        {
            throw new InvalidOperationException();
        }

        ValidateV1Rows(connection, transaction);
        var lifecycleDefinition = Definition(
            connection,
            transaction,
            "table",
            "alert_lifecycle_events");
        if (lifecycleDefinition is not null
            && !lifecycleDefinition.Contains(
                "REFERENCES alert_receipts(alert_id)",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        var evaluations = Rows(
            connection,
            transaction,
            "SELECT evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json FROM alert_evaluations ORDER BY evaluation_id;");
        var receipts = Rows(
            connection,
            transaction,
            "SELECT alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json FROM alert_receipts ORDER BY alert_id;");
        var suppressions = Rows(
            connection,
            transaction,
            "SELECT evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json FROM alert_suppressions ORDER BY evaluation_id,suppression_ordinal;");

        Execute(connection, transaction, "ALTER TABLE alert_suppressions RENAME TO alert_suppressions_alert_v1;");
        Execute(connection, transaction, "ALTER TABLE alert_receipts RENAME TO alert_receipts_alert_v1;");
        Execute(connection, transaction, "ALTER TABLE alert_evaluations RENAME TO alert_evaluations_alert_v1;");
        foreach (var sql in TableSql.Values) Execute(connection, transaction, sql);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO alert_evaluations
            SELECT evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json
            FROM alert_evaluations_alert_v1;
            INSERT INTO alert_receipts
            SELECT alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json
            FROM alert_receipts_alert_v1;
            INSERT INTO alert_suppressions
            SELECT evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json
            FROM alert_suppressions_alert_v1;
            """);

        if (!Rows(
                connection,
                transaction,
                "SELECT evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json FROM alert_evaluations ORDER BY evaluation_id;")
                .SequenceEqual(evaluations, StringComparer.Ordinal)
            || !Rows(
                connection,
                transaction,
                "SELECT alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json FROM alert_receipts ORDER BY alert_id;")
                .SequenceEqual(receipts, StringComparer.Ordinal)
            || !Rows(
                connection,
                transaction,
                "SELECT evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json FROM alert_suppressions ORDER BY evaluation_id,suppression_ordinal;")
                .SequenceEqual(suppressions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException();
        }

        Execute(connection, transaction, "DROP TABLE alert_suppressions_alert_v1;");
        Execute(connection, transaction, "DROP TABLE alert_receipts_alert_v1;");
        Execute(connection, transaction, "DROP TABLE alert_evaluations_alert_v1;");
        Execute(
            connection,
            transaction,
            "UPDATE schema_version SET version=2 WHERE component='alert_engine' AND version=1;");

        if (lifecycleDefinition != Definition(
                connection,
                transaction,
                "table",
                "alert_lifecycle_events")
            || ForeignKeyViolations(connection, transaction) != 0
            || !IsValid(connection, transaction))
        {
            throw new InvalidOperationException();
        }
    }

    private static void ValidateV1Rows(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var command = Command(
            connection,
            transaction,
            """
            SELECT evaluation_id,input_hash,configuration_version,configuration_hash,canonical_json,
                   (SELECT count(*) FROM alert_receipts r WHERE r.evaluation_id=e.evaluation_id),
                   (SELECT count(*) FROM alert_suppressions s WHERE s.evaluation_id=e.evaluation_id)
            FROM alert_evaluations e ORDER BY evaluation_id;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(4));
                var projection = AlertEvaluationConsumerV1.Validate(bytes);
                if (projection.EvaluationId != reader.GetString(0)
                    || projection.InputHash != reader.GetString(1)
                    || projection.ConfigurationVersion != reader.GetString(2)
                    || projection.ConfigurationHash != reader.GetString(3)
                    || projection.ReceiptCount != reader.GetInt64(5)
                    || projection.SuppressionCount != reader.GetInt64(6))
                {
                    throw new InvalidOperationException();
                }
            }
        }

        using (var command = Command(
            connection,
            transaction,
            "SELECT alert_id,evaluation_id,canonical_json FROM alert_receipts ORDER BY alert_id;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var projection = AlertCenterReceiptConsumerV1.Validate(
                    Encoding.UTF8.GetBytes(reader.GetString(2)));
                if (projection.AlertId != reader.GetString(0)
                    || projection.EvaluationId != reader.GetString(1))
                {
                    throw new InvalidOperationException();
                }
            }
        }

        using (var command = Command(
            connection,
            transaction,
            "SELECT evaluation_id,rule_id,rule_version,code,canonical_json FROM alert_suppressions ORDER BY evaluation_id,suppression_ordinal;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var projection = AlertSuppressionConsumerV1.Validate(
                    Encoding.UTF8.GetBytes(reader.GetString(4)));
                if (projection.EvaluationId != reader.GetString(0)
                    || projection.RuleId != reader.GetString(1)
                    || projection.RuleVersion != reader.GetString(2)
                    || projection.Code != reader.GetString(3))
                {
                    throw new InvalidOperationException();
                }
            }
        }
    }

    private static bool ExactOwnedInventory(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT name FROM sqlite_schema
            WHERE (type='table' AND name IN ('alert_evaluations','alert_receipts','alert_suppressions'))
               OR (type IN ('index','trigger')
                   AND sql IS NOT NULL
                   AND tbl_name IN ('alert_evaluations','alert_receipts','alert_suppressions'))
            ORDER BY type,name;
            """);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.SequenceEqual(
            ["alert_evaluations", "alert_receipts", "alert_suppressions"],
            StringComparer.Ordinal);
    }

    private static bool TemporaryObjectsExist(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        new[]
        {
            "alert_evaluations_alert_v1",
            "alert_receipts_alert_v1",
            "alert_suppressions_alert_v1",
        }.Any(name => Definition(connection, transaction, "table", name) is not null);

    private static int ForeignKeyViolations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "PRAGMA foreign_key_check;");
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read()) count++;
        return count;
    }

    private static IReadOnlyList<string> Rows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = Command(connection, transaction, sql);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(string.Join(
                '\u001f',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(
                        reader.GetValue(index),
                        CultureInfo.InvariantCulture))));
        }
        return values;
    }

    private static string? Definition(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string type,
        string name)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT sql FROM sqlite_schema WHERE type=$type AND name=$name;",
            ("$type", type),
            ("$name", name));
        return command.ExecuteScalar() as string;
    }

    private static string Normalize(string value) =>
        string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd(';');

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return command;
    }
}

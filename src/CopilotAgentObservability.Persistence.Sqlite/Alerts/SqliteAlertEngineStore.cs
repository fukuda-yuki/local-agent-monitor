using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed class SqliteAlertEngineStore : IAlertEngineStore
{
    private readonly string _connectionString;

    public SqliteAlertEngineStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public AlertStoreResult Initialize()
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = AlertSchemaV1.ReadVersion(connection, transaction);
            if (version is null)
            {
                if (AlertSchemaV1.AnyEngineTableExists(connection, transaction)) return Unavailable();
                AlertSchemaV1.Create(connection, transaction);
            }
            if (!AlertSchemaV1.IsValid(connection, transaction)) return Unavailable();
            transaction.Commit();
            return Success();
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return Busy(); }
        catch (SqliteException) { return Unavailable(); }
        catch (InvalidOperationException) { return Unavailable(); }
        catch (FormatException) { return Unavailable(); }
    }

    public AlertStoreResult Append(AlertEvaluationResult evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!ValidEvaluation(evaluation)) return new(AlertStoreStatus.Conflict, "alert_store_conflict");
        var evaluationJson = Text(AlertCanonicalJson.SerializeEvaluation(evaluation));
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!AlertSchemaV1.IsValid(connection, transaction)) return Unavailable();

            var existing = ReadScalar(connection, transaction, "SELECT canonical_json FROM alert_evaluations WHERE evaluation_id=$id;", ("$id", evaluation.EvaluationId));
            if (existing is not null)
            {
                var exact = existing == evaluationJson && ChildrenMatch(connection, transaction, evaluation);
                transaction.Commit();
                return exact ? Success() : new(AlertStoreStatus.Conflict, "alert_store_conflict");
            }

            Execute(connection, transaction,
                "INSERT INTO alert_evaluations(evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json) VALUES($id,$schema,$input,$version,$hash,$json);",
                ("$id", evaluation.EvaluationId), ("$schema", evaluation.SchemaVersion), ("$input", evaluation.InputHash),
                ("$version", evaluation.ConfigurationVersion), ("$hash", evaluation.ConfigurationHash), ("$json", evaluationJson));
            for (var index = 0; index < evaluation.Receipts.Count; index++)
            {
                var receipt = evaluation.Receipts[index];
                Execute(connection, transaction,
                    "INSERT INTO alert_receipts(alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json) VALUES($alert,$evaluation,$ordinal,$schema,$json);",
                    ("$alert", receipt.AlertId), ("$evaluation", evaluation.EvaluationId), ("$ordinal", index), ("$schema", receipt.SchemaVersion),
                    ("$json", Text(AlertCanonicalJson.SerializeReceipt(receipt))));
            }
            for (var index = 0; index < evaluation.Suppressions.Count; index++)
            {
                var suppression = evaluation.Suppressions[index];
                Execute(connection, transaction,
                    "INSERT INTO alert_suppressions(evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json) VALUES($evaluation,$ordinal,$rule,$rule_version,$code,$json);",
                    ("$evaluation", evaluation.EvaluationId), ("$ordinal", index), ("$rule", suppression.RuleId), ("$rule_version", suppression.RuleVersion),
                    ("$code", suppression.Code), ("$json", Text(AlertCanonicalJson.SerializeSuppression(suppression))));
            }
            transaction.Commit();
            return Success();
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return Busy(); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { return new(AlertStoreStatus.Conflict, "alert_store_conflict"); }
        catch (SqliteException) { return Unavailable(); }
    }

    public AlertStoreReadResult GetEvaluation(string evaluationId) => ReadOne("alert_evaluations", "evaluation_id", evaluationId);

    public AlertStoreReadResult GetReceipt(string alertId) => ReadOne("alert_receipts", "alert_id", alertId);

    public AlertStoreListResult ListSuppressions(string evaluationId)
    {
        if (!CanonicalHash(evaluationId)) return new(AlertStoreStatus.NotFound, [], "alert_not_found");
        try
        {
            using var connection = Open();
            if (!AlertSchemaV1.IsValid(connection, null)) return new(AlertStoreStatus.Unavailable, [], "alert_store_unavailable");
            using var command = Command(connection, null, "SELECT canonical_json FROM alert_suppressions WHERE evaluation_id=$id ORDER BY suppression_ordinal;", ("$id", evaluationId));
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            if (values.Count == 0 && ReadScalar(connection, null, "SELECT evaluation_id FROM alert_evaluations WHERE evaluation_id=$id;", ("$id", evaluationId)) is null)
                return new(AlertStoreStatus.NotFound, [], "alert_not_found");
            return new(AlertStoreStatus.Success, values);
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return new(AlertStoreStatus.Busy, [], "alert_store_busy"); }
        catch (SqliteException) { return new(AlertStoreStatus.Unavailable, [], "alert_store_unavailable"); }
    }

    private AlertStoreReadResult ReadOne(string table, string idColumn, string id)
    {
        if (!CanonicalHash(id)) return new(AlertStoreStatus.NotFound, null, "alert_not_found");
        try
        {
            using var connection = Open();
            if (!AlertSchemaV1.IsValid(connection, null)) return new(AlertStoreStatus.Unavailable, null, "alert_store_unavailable");
            var value = ReadScalar(connection, null, $"SELECT canonical_json FROM {table} WHERE {idColumn}=$id;", ("$id", id));
            return value is null ? new(AlertStoreStatus.NotFound, null, "alert_not_found") : new(AlertStoreStatus.Success, value);
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return new(AlertStoreStatus.Busy, null, "alert_store_busy"); }
        catch (SqliteException) { return new(AlertStoreStatus.Unavailable, null, "alert_store_unavailable"); }
    }

    private static bool ChildrenMatch(SqliteConnection connection, SqliteTransaction transaction, AlertEvaluationResult evaluation)
    {
        var receipts = ReadMany(connection, transaction, "SELECT canonical_json FROM alert_receipts WHERE evaluation_id=$id ORDER BY receipt_ordinal;", evaluation.EvaluationId);
        var suppressions = ReadMany(connection, transaction, "SELECT canonical_json FROM alert_suppressions WHERE evaluation_id=$id ORDER BY suppression_ordinal;", evaluation.EvaluationId);
        return receipts.SequenceEqual(evaluation.Receipts.Select(item => Text(AlertCanonicalJson.SerializeReceipt(item))), StringComparer.Ordinal)
            && suppressions.SequenceEqual(evaluation.Suppressions.Select(item => Text(AlertCanonicalJson.SerializeSuppression(item))), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ReadMany(SqliteConnection connection, SqliteTransaction transaction, string sql, string id)
    {
        using var command = Command(connection, transaction, sql, ("$id", id));
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static bool ValidEvaluation(AlertEvaluationResult value) =>
        value.SchemaVersion == AlertContractVersions.Evaluation && CanonicalHash(value.EvaluationId) && CanonicalHash(value.InputHash) && CanonicalHash(value.ConfigurationHash)
        && value.Receipts.All(item => item.SchemaVersion == AlertContractVersions.Receipt && item.EvaluationId == value.EvaluationId && CanonicalHash(item.AlertId))
        && value.Receipts.Select(item => item.AlertId).Distinct(StringComparer.Ordinal).Count() == value.Receipts.Count
        && value.Suppressions.All(item => item.EvaluationId == value.EvaluationId);

    private static bool CanonicalHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString) { DefaultTimeout = 1 }; connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=100;"; pragma.ExecuteNonQuery(); return connection; }
    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;
    private static AlertStoreResult Success() => new(AlertStoreStatus.Success);
    private static AlertStoreResult Busy() => new(AlertStoreStatus.Busy, "alert_store_busy");
    private static AlertStoreResult Unavailable() => new(AlertStoreStatus.Unavailable, "alert_store_unavailable");

    private static string? ReadScalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar() as string;
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}

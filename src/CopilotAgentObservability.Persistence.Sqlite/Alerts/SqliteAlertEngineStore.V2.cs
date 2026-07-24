using System.Data;
using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed partial class SqliteAlertEngineStore
{
    public AlertEngineStoreResultV2 InitializeV2()
    {
        try
        {
            using var connection = Open();
            try
            {
                SetPragma(connection, "foreign_keys", false);
                SetPragma(connection, "legacy_alter_table", true);
                using var transaction = connection.BeginTransaction(deferred: false);
                var version = AlertSchemaV1.ReadVersion(connection, transaction);
                if (version is null)
                {
                    if (AlertSchemaV1.AnyEngineTableExists(connection, transaction))
                    {
                        return V2Unavailable();
                    }
                    AlertSchemaV2.Create(connection, transaction);
                }
                else if (version == AlertSchemaV1.Version)
                {
                    AlertSchemaV2.MigrateFromV1(connection, transaction);
                }

                if (!AlertSchemaV2.IsValid(connection, transaction))
                {
                    return V2Unavailable();
                }
                transaction.Commit();
            }
            finally
            {
                SetPragma(connection, "legacy_alter_table", false);
                SetPragma(connection, "foreign_keys", true);
                if (!ReadPragma(connection, "foreign_keys"))
                {
                    throw new InvalidOperationException();
                }
            }
            return new(AlertEngineStoreStatusV2.Success);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(AlertEngineStoreStatusV2.Busy, "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return V2Unavailable();
        }
    }

    public AlertEngineStoreResultV2 Append(AlertEvaluationResultV2 evaluation)
    {
        if (!ValidV2Evaluation(evaluation))
        {
            return new(AlertEngineStoreStatusV2.ContractRejected, "alert_contract_rejected");
        }

        var initialized = InitializeV2();
        if (initialized.Status != AlertEngineStoreStatusV2.Success)
        {
            return initialized;
        }

        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = AppendEvaluationCore(connection, transaction, evaluation);
            if (result.Status == AlertEngineTransactionAppendStatusV2.Success)
            {
                transaction.Commit();
                return new(AlertEngineStoreStatusV2.Success);
            }
            return result.Status switch
            {
                AlertEngineTransactionAppendStatusV2.Conflict =>
                    new(AlertEngineStoreStatusV2.Conflict, "alert_store_conflict"),
                AlertEngineTransactionAppendStatusV2.Busy =>
                    new(AlertEngineStoreStatusV2.Busy, "alert_store_busy"),
                AlertEngineTransactionAppendStatusV2.ContractRejected =>
                    new(AlertEngineStoreStatusV2.ContractRejected, "alert_contract_rejected"),
                _ => V2Unavailable(),
            };
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(AlertEngineStoreStatusV2.Busy, "alert_store_busy");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(AlertEngineStoreStatusV2.Conflict, "alert_store_conflict");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return V2Unavailable();
        }
    }

    public AlertEngineStoreReadResultV2 GetEvaluationV2(string evaluationId) =>
        ReadOneV2("alert_evaluations", "evaluation_id", evaluationId, AlertContractVersionsV2.Evaluation);

    public AlertEngineStoreReadResultV2 GetReceiptV2(string alertId) =>
        ReadOneV2("alert_receipts", "alert_id", alertId, AlertContractVersionsV2.Receipt);

    public AlertEngineStoreListResultV2 ListSuppressionsV2(string evaluationId)
    {
        if (!CanonicalHash(evaluationId))
        {
            return new(AlertEngineQueryStatus.NotFound, [], "alert_not_found");
        }
        try
        {
            using var connection = Open();
            if (!AlertSchemaV2.IsValid(connection, null))
            {
                return new(AlertEngineQueryStatus.Unavailable, [], "alert_store_unavailable");
            }
            var schema = ReadScalar(
                connection,
                null,
                "SELECT schema_version FROM alert_evaluations WHERE evaluation_id=$id;",
                ("$id", evaluationId));
            if (schema is null)
            {
                return new(AlertEngineQueryStatus.NotFound, [], "alert_not_found");
            }
            if (schema != AlertContractVersionsV2.Evaluation)
            {
                return new(AlertEngineQueryStatus.NotFound, [], "alert_not_found");
            }

            using var command = Command(
                connection,
                null,
                "SELECT suppression_ordinal,rule_id,rule_version,code,canonical_json FROM alert_suppressions WHERE evaluation_id=$id ORDER BY suppression_ordinal;",
                ("$id", evaluationId));
            using var reader = command.ExecuteReader();
            var items = new List<IReadOnlyList<byte>>();
            while (reader.Read())
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(4));
                var projection = AlertEvaluationConsumerV2.ValidateSuppression(bytes);
                if (reader.GetInt64(0) != items.Count
                    || projection.EvaluationId != evaluationId
                    || projection.RuleId != reader.GetString(1)
                    || projection.RuleVersion != reader.GetString(2)
                    || projection.Code != reader.GetString(3))
                {
                    return new(AlertEngineQueryStatus.Unavailable, [], "alert_store_unavailable");
                }
                items.Add(Array.AsReadOnly(bytes));
            }
            return new(AlertEngineQueryStatus.Success, Array.AsReadOnly(items.ToArray()));
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(AlertEngineQueryStatus.Busy, [], "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return new(AlertEngineQueryStatus.Unavailable, [], "alert_store_unavailable");
        }
    }

    public AlertEngineTransactionAppendResultV2 AppendEvaluation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlertEvaluationResultV2 evaluation)
    {
        if (connection is null
            || transaction is null
            || connection.State != ConnectionState.Open
            || transaction.Connection != connection
            || SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) != 0)
        {
            return new(AlertEngineTransactionAppendStatusV2.InvalidTransaction);
        }
        if (!ValidV2Evaluation(evaluation))
        {
            return new(AlertEngineTransactionAppendStatusV2.ContractRejected);
        }

        try
        {
            return AppendEvaluationCore(connection, transaction, evaluation);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(AlertEngineTransactionAppendStatusV2.Busy);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(AlertEngineTransactionAppendStatusV2.Conflict);
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return new(AlertEngineTransactionAppendStatusV2.Unavailable);
        }
    }

    private static AlertEngineTransactionAppendResultV2 AppendEvaluationCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlertEvaluationResultV2 evaluation)
    {
        if (!AlertSchemaV2.IsValid(connection, transaction))
        {
            return new(AlertEngineTransactionAppendStatusV2.Unavailable);
        }

        var evaluationJson = Encoding.UTF8.GetString(
            AlertCanonicalJsonV2.SerializeEvaluation(evaluation));
        var existing = ReadScalar(
            connection,
            transaction,
            "SELECT canonical_json FROM alert_evaluations WHERE evaluation_id=$id;",
            ("$id", evaluation.EvaluationId));
        if (existing is not null)
        {
            return existing == evaluationJson
                && V2ChildrenMatch(connection, transaction, evaluation)
                ? SuccessParticipant(evaluation)
                : new(AlertEngineTransactionAppendStatusV2.Conflict);
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO alert_evaluations(
                evaluation_id,schema_version,input_hash,configuration_version,configuration_hash,canonical_json)
            VALUES($id,$schema,$input,$version,$hash,$json);
            """,
            ("$id", evaluation.EvaluationId),
            ("$schema", evaluation.SchemaVersion),
            ("$input", evaluation.InputHash),
            ("$version", evaluation.ConfigurationVersion),
            ("$hash", evaluation.ConfigurationHash),
            ("$json", evaluationJson));
        for (var index = 0; index < evaluation.Receipts.Count; index++)
        {
            var receipt = evaluation.Receipts[index];
            Execute(
                connection,
                transaction,
                """
                INSERT INTO alert_receipts(
                    alert_id,evaluation_id,receipt_ordinal,schema_version,canonical_json)
                VALUES($alert,$evaluation,$ordinal,$schema,$json);
                """,
                ("$alert", receipt.AlertId),
                ("$evaluation", evaluation.EvaluationId),
                ("$ordinal", index),
                ("$schema", receipt.SchemaVersion),
                ("$json", Encoding.UTF8.GetString(AlertCanonicalJsonV2.SerializeReceipt(receipt))));
        }
        for (var index = 0; index < evaluation.Suppressions.Count; index++)
        {
            var suppression = evaluation.Suppressions[index];
            Execute(
                connection,
                transaction,
                """
                INSERT INTO alert_suppressions(
                    evaluation_id,suppression_ordinal,rule_id,rule_version,code,canonical_json)
                VALUES($evaluation,$ordinal,$rule,$rule_version,$code,$json);
                """,
                ("$evaluation", evaluation.EvaluationId),
                ("$ordinal", index),
                ("$rule", suppression.RuleId),
                ("$rule_version", suppression.RuleVersion),
                ("$code", suppression.Code),
                ("$json", Encoding.UTF8.GetString(AlertCanonicalJsonV2.SerializeSuppression(suppression))));
        }
        return SuccessParticipant(evaluation);
    }

    private AlertEngineStoreReadResultV2 ReadOneV2(
        string table,
        string idColumn,
        string id,
        string schemaVersion)
    {
        if (!CanonicalHash(id))
        {
            return new(AlertEngineQueryStatus.NotFound, [], "alert_not_found");
        }
        try
        {
            using var connection = Open();
            if (!AlertSchemaV2.IsValid(connection, null))
            {
                return new(AlertEngineQueryStatus.Unavailable, [], "alert_store_unavailable");
            }
            using var command = Command(
                connection,
                null,
                $"SELECT schema_version,canonical_json FROM {table} WHERE {idColumn}=$id;",
                ("$id", id));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != schemaVersion)
            {
                return new(AlertEngineQueryStatus.NotFound, [], "alert_not_found");
            }
            var bytes = Encoding.UTF8.GetBytes(reader.GetString(1));
            if (schemaVersion == AlertContractVersionsV2.Evaluation)
            {
                AlertEvaluationConsumerV2.Validate(bytes);
            }
            else
            {
                AlertReceiptConsumerV2.Validate(bytes);
            }
            return new(AlertEngineQueryStatus.Success, Array.AsReadOnly(bytes));
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(AlertEngineQueryStatus.Busy, [], "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return new(AlertEngineQueryStatus.Unavailable, [], "alert_store_unavailable");
        }
    }

    private static bool ValidV2Evaluation(AlertEvaluationResultV2? evaluation)
    {
        if (evaluation is null) return false;
        try
        {
            AlertEvaluationConsumerV2.Validate(
                AlertCanonicalJsonV2.SerializeEvaluation(evaluation));
            return true;
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return false;
        }
    }

    private static bool V2ChildrenMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlertEvaluationResultV2 evaluation)
    {
        using (var command = Command(
            connection,
            transaction,
            """
            SELECT alert_id,receipt_ordinal,schema_version,canonical_json
            FROM alert_receipts
            WHERE evaluation_id=$id
            ORDER BY receipt_ordinal;
            """,
            ("$id", evaluation.EvaluationId)))
        using (var reader = command.ExecuteReader())
        {
            var index = 0;
            while (reader.Read())
            {
                if (index >= evaluation.Receipts.Count) return false;
                var expected = evaluation.Receipts[index];
                if (reader.GetString(0) != expected.AlertId
                    || reader.GetInt64(1) != index
                    || reader.GetString(2) != AlertContractVersionsV2.Receipt
                    || reader.GetString(3) != Encoding.UTF8.GetString(
                        AlertCanonicalJsonV2.SerializeReceipt(expected)))
                {
                    return false;
                }
                index++;
            }
            if (index != evaluation.Receipts.Count) return false;
        }

        using (var command = Command(
            connection,
            transaction,
            """
            SELECT suppression_ordinal,rule_id,rule_version,code,canonical_json
            FROM alert_suppressions
            WHERE evaluation_id=$id
            ORDER BY suppression_ordinal;
            """,
            ("$id", evaluation.EvaluationId)))
        using (var reader = command.ExecuteReader())
        {
            var index = 0;
            while (reader.Read())
            {
                if (index >= evaluation.Suppressions.Count) return false;
                var expected = evaluation.Suppressions[index];
                if (reader.GetInt64(0) != index
                    || reader.GetString(1) != expected.RuleId
                    || reader.GetString(2) != expected.RuleVersion
                    || reader.GetString(3) != expected.Code
                    || reader.GetString(4) != Encoding.UTF8.GetString(
                        AlertCanonicalJsonV2.SerializeSuppression(expected)))
                {
                    return false;
                }
                index++;
            }
            return index == evaluation.Suppressions.Count;
        }
    }

    private static AlertEngineTransactionAppendResultV2 SuccessParticipant(
        AlertEvaluationResultV2 evaluation) =>
        new(
            AlertEngineTransactionAppendStatusV2.Success,
            evaluation.EvaluationId,
            Array.AsReadOnly(evaluation.Receipts.Select(item => item.AlertId).ToArray()),
            Array.AsReadOnly(evaluation.Suppressions
                .Select((_, index) => new AlertEngineSuppressionIdentityV2(
                    evaluation.EvaluationId,
                    index))
                .ToArray()));

    private static void SetPragma(
        SqliteConnection connection,
        string name,
        bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}={(enabled ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }

    private static bool ReadPragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static AlertEngineStoreResultV2 V2Unavailable() =>
        new(AlertEngineStoreStatusV2.Unavailable, "alert_store_unavailable");

    private static bool IsNonFatalV2(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}

using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed partial class SqliteAlertEngineStore
{
    public AlertReceiptQueryPage ListReceipts(string? afterAlertId, int limit)
    {
        if (!ValidPage(limit) || afterAlertId is not null && !CanonicalHash(afterAlertId))
        {
            return InvalidReceipts();
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV1.IsValid(connection, null)) return UnavailableReceipts();
            using var command = Command(
                connection,
                null,
                "SELECT alert_id,evaluation_id,schema_version,canonical_json FROM alert_receipts WHERE ($after IS NULL OR alert_id>$after) ORDER BY alert_id COLLATE BINARY LIMIT $take;",
                ("$after", afterAlertId is null ? DBNull.Value : afterAlertId),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertReceiptQueryItem>();
            var returnedBytes = 0;
            var hasMore = false;
            while (reader.Read())
            {
                var alertId = reader.GetString(0);
                var evaluationId = reader.GetString(1);
                var schemaVersion = reader.GetString(2);
                var canonicalBytes = Encoding.UTF8.GetBytes(reader.GetString(3));
                if (canonicalBytes.Length > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return UnavailableReceipts();
                }

                var receipt = AlertCenterReceiptConsumerV1.Validate(canonicalBytes);
                if (receipt.AlertId != alertId
                    || receipt.EvaluationId != evaluationId
                    || schemaVersion != AlertContractVersions.Receipt)
                {
                    return UnavailableReceipts();
                }

                if (items.Count == limit || returnedBytes > AlertEngineQueryLimits.MaximumPageBytes - canonicalBytes.Length)
                {
                    hasMore = true;
                    break;
                }

                items.Add(new AlertReceiptQueryItem(canonicalBytes, receipt));
                returnedBytes += canonicalBytes.Length;
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? items[^1].Receipt.AlertId : null);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return BusyReceipts();
        }
        catch (Exception exception) when (IsNonFatalQueryFailure(exception))
        {
            return UnavailableReceipts();
        }
    }

    public AlertEvaluationQueryPage ListEvaluations(string? afterEvaluationId, int limit)
    {
        if (!ValidPage(limit) || afterEvaluationId is not null && !CanonicalHash(afterEvaluationId))
        {
            return InvalidEvaluations();
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV1.IsValid(connection, null)) return UnavailableEvaluations();
            using var command = Command(
                connection,
                null,
                """
                SELECT e.evaluation_id,e.schema_version,e.input_hash,e.configuration_version,e.configuration_hash,e.canonical_json,
                       (SELECT COUNT(*) FROM alert_receipts r WHERE r.evaluation_id=e.evaluation_id),
                       (SELECT COUNT(*) FROM alert_suppressions s WHERE s.evaluation_id=e.evaluation_id)
                FROM alert_evaluations e
                WHERE ($after IS NULL OR e.evaluation_id>$after)
                ORDER BY e.evaluation_id COLLATE BINARY
                LIMIT $take;
                """,
                ("$after", afterEvaluationId is null ? DBNull.Value : afterEvaluationId),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertEvaluationProjectionV1>();
            var hasMore = false;
            while (reader.Read())
            {
                var evaluationId = reader.GetString(0);
                var schemaVersion = reader.GetString(1);
                var inputHash = reader.GetString(2);
                var configurationVersion = reader.GetString(3);
                var configurationHash = reader.GetString(4);
                var evaluation = AlertEvaluationConsumerV1.Validate(Encoding.UTF8.GetBytes(reader.GetString(5)));
                var receiptCount = reader.GetInt64(6);
                var suppressionCount = reader.GetInt64(7);
                if (evaluation.EvaluationId != evaluationId
                    || schemaVersion != AlertContractVersions.Evaluation
                    || evaluation.InputHash != inputHash
                    || evaluation.ConfigurationVersion != configurationVersion
                    || evaluation.ConfigurationHash != configurationHash
                    || evaluation.ReceiptCount != receiptCount
                    || evaluation.SuppressionCount != suppressionCount)
                {
                    return UnavailableEvaluations();
                }

                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                items.Add(evaluation);
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? items[^1].EvaluationId : null);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return BusyEvaluations();
        }
        catch (Exception exception) when (IsNonFatalQueryFailure(exception))
        {
            return UnavailableEvaluations();
        }
    }

    public AlertSuppressionQueryPage ListSuppressions(
        string evaluationId,
        long? afterSuppressionOrdinal,
        int limit)
    {
        if (!ValidPage(limit)
            || !CanonicalHash(evaluationId)
            || afterSuppressionOrdinal is < 0)
        {
            return InvalidSuppressions();
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV1.IsValid(connection, null)) return UnavailableSuppressions();
            if (ReadScalar(
                    connection,
                    null,
                    "SELECT evaluation_id FROM alert_evaluations WHERE evaluation_id=$id;",
                    ("$id", evaluationId)) is null)
            {
                return NotFoundSuppressions();
            }

            using var command = Command(
                connection,
                null,
                """
                SELECT suppression_ordinal,rule_id,rule_version,code,canonical_json
                FROM alert_suppressions
                WHERE evaluation_id=$evaluation AND ($after IS NULL OR suppression_ordinal>$after)
                ORDER BY suppression_ordinal
                LIMIT $take;
                """,
                ("$evaluation", evaluationId),
                ("$after", afterSuppressionOrdinal is null ? DBNull.Value : afterSuppressionOrdinal.Value),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertSuppressionQueryItem>();
            var returnedBytes = 0;
            var hasMore = false;
            while (reader.Read())
            {
                var ordinal = reader.GetInt64(0);
                var ruleId = reader.GetString(1);
                var ruleVersion = reader.GetString(2);
                var code = reader.GetString(3);
                var canonicalBytes = Encoding.UTF8.GetBytes(reader.GetString(4));
                if (ordinal < 0 || canonicalBytes.Length > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return UnavailableSuppressions();
                }

                var suppression = AlertSuppressionConsumerV1.Validate(canonicalBytes);
                if (suppression.EvaluationId != evaluationId
                    || suppression.RuleId != ruleId
                    || suppression.RuleVersion != ruleVersion
                    || suppression.Code != code)
                {
                    return UnavailableSuppressions();
                }

                if (items.Count == limit || returnedBytes > AlertEngineQueryLimits.MaximumPageBytes - canonicalBytes.Length)
                {
                    hasMore = true;
                    break;
                }

                items.Add(new AlertSuppressionQueryItem(ordinal, canonicalBytes, suppression));
                returnedBytes += canonicalBytes.Length;
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? items[^1].SuppressionOrdinal : null);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return BusySuppressions();
        }
        catch (Exception exception) when (IsNonFatalQueryFailure(exception))
        {
            return UnavailableSuppressions();
        }
    }

    private static bool ValidPage(int limit) => limit is >= 1 and <= AlertEngineQueryLimits.MaximumPageSize;

    private static bool IsNonFatalQueryFailure(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static AlertReceiptQueryPage InvalidReceipts() =>
        new(AlertEngineQueryStatus.Invalid, [], Code: "invalid_alert_query");

    private static AlertReceiptQueryPage BusyReceipts() =>
        new(AlertEngineQueryStatus.Busy, [], Code: "alert_store_busy");

    private static AlertReceiptQueryPage UnavailableReceipts() =>
        new(AlertEngineQueryStatus.Unavailable, [], Code: "alert_store_unavailable");

    private static AlertEvaluationQueryPage InvalidEvaluations() =>
        new(AlertEngineQueryStatus.Invalid, [], Code: "invalid_alert_query");

    private static AlertEvaluationQueryPage BusyEvaluations() =>
        new(AlertEngineQueryStatus.Busy, [], Code: "alert_store_busy");

    private static AlertEvaluationQueryPage UnavailableEvaluations() =>
        new(AlertEngineQueryStatus.Unavailable, [], Code: "alert_store_unavailable");

    private static AlertSuppressionQueryPage InvalidSuppressions() =>
        new(AlertEngineQueryStatus.Invalid, [], Code: "invalid_alert_query");

    private static AlertSuppressionQueryPage NotFoundSuppressions() =>
        new(AlertEngineQueryStatus.NotFound, [], Code: "alert_not_found");

    private static AlertSuppressionQueryPage BusySuppressions() =>
        new(AlertEngineQueryStatus.Busy, [], Code: "alert_store_busy");

    private static AlertSuppressionQueryPage UnavailableSuppressions() =>
        new(AlertEngineQueryStatus.Unavailable, [], Code: "alert_store_unavailable");
}

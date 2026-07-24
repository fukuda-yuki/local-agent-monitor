using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed partial class SqliteAlertEngineStore
{
    public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
        string? afterAlertId,
        int limit)
    {
        if (!ValidVersionedPage(limit)
            || afterAlertId is not null && !CanonicalHash(afterAlertId))
        {
            return new(
                AlertEngineQueryStatus.Invalid,
                [],
                Code: "invalid_alert_query");
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV2.IsValid(connection, null))
            {
                return VersionedReceiptsUnavailable();
            }
            using var command = Command(
                connection,
                null,
                """
                SELECT alert_id,evaluation_id,schema_version,canonical_json
                FROM alert_receipts
                WHERE ($after IS NULL OR alert_id>$after)
                ORDER BY alert_id COLLATE BINARY
                LIMIT $take;
                """,
                ("$after", afterAlertId is null ? DBNull.Value : afterAlertId),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertVersionedReceiptQueryItem>();
            var bytesRead = 0;
            var hasMore = false;
            while (reader.Read())
            {
                var alertId = reader.GetString(0);
                var evaluationId = reader.GetString(1);
                var schema = reader.GetString(2);
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(3));
                if (bytes.Length > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return VersionedReceiptsUnavailable();
                }

                AlertVersionedReceiptQueryItem item;
                if (schema == AlertContractVersions.Receipt)
                {
                    var projection = AlertCenterReceiptConsumerV1.Validate(bytes);
                    if (projection.AlertId != alertId
                        || projection.EvaluationId != evaluationId)
                    {
                        return VersionedReceiptsUnavailable();
                    }
                    item = new(AlertContractKind.V1, bytes, projection, null);
                }
                else if (schema == AlertContractVersionsV2.Receipt)
                {
                    var projection = AlertCenterReceiptConsumerV2.Validate(bytes);
                    if (projection.AlertId != alertId
                        || projection.EvaluationId != evaluationId)
                    {
                        return VersionedReceiptsUnavailable();
                    }
                    item = new(AlertContractKind.V2, bytes, null, projection);
                }
                else
                {
                    return VersionedReceiptsUnavailable();
                }

                if (items.Count == limit
                    || bytesRead > AlertEngineQueryLimits.MaximumPageBytes - bytes.Length)
                {
                    hasMore = true;
                    break;
                }
                items.Add(item);
                bytesRead += bytes.Length;
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? ReceiptId(items[^1]) : null,
                Exhausted: !hasMore,
                CanonicalByteCount: bytesRead);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(
                AlertEngineQueryStatus.Busy,
                [],
                Code: "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return VersionedReceiptsUnavailable();
        }
    }

    public AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(
        string? afterEvaluationId,
        int limit)
    {
        if (!ValidVersionedPage(limit)
            || afterEvaluationId is not null && !CanonicalHash(afterEvaluationId))
        {
            return new(
                AlertEngineQueryStatus.Invalid,
                [],
                Code: "invalid_alert_query");
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV2.IsValid(connection, null))
            {
                return VersionedEvaluationsUnavailable();
            }
            using var command = Command(
                connection,
                null,
                """
                SELECT evaluation_id,schema_version,input_hash,configuration_version,
                       configuration_hash,canonical_json,
                       (SELECT count(*) FROM alert_receipts r WHERE r.evaluation_id=e.evaluation_id),
                       (SELECT count(*) FROM alert_suppressions s WHERE s.evaluation_id=e.evaluation_id)
                FROM alert_evaluations e
                WHERE ($after IS NULL OR evaluation_id>$after)
                ORDER BY evaluation_id COLLATE BINARY
                LIMIT $take;
                """,
                ("$after", afterEvaluationId is null ? DBNull.Value : afterEvaluationId),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertVersionedEvaluationQueryItem>();
            var bytesRead = 0;
            var hasMore = false;
            while (reader.Read())
            {
                var evaluationId = reader.GetString(0);
                var schema = reader.GetString(1);
                var inputHash = reader.GetString(2);
                var configurationVersion = reader.GetString(3);
                var configurationHash = reader.GetString(4);
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(5));
                var receiptCount = reader.GetInt64(6);
                var suppressionCount = reader.GetInt64(7);
                if (bytes.Length > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return VersionedEvaluationsUnavailable();
                }

                AlertVersionedEvaluationQueryItem item;
                if (schema == AlertContractVersions.Evaluation)
                {
                    var projection = AlertEvaluationConsumerV1.Validate(bytes);
                    if (projection.EvaluationId != evaluationId
                        || projection.InputHash != inputHash
                        || projection.ConfigurationVersion != configurationVersion
                        || projection.ConfigurationHash != configurationHash
                        || projection.ReceiptCount != receiptCount
                        || projection.SuppressionCount != suppressionCount)
                    {
                        return VersionedEvaluationsUnavailable();
                    }
                    item = new(
                        AlertContractKind.V1,
                        bytes,
                        new(
                            projection.EvaluationId,
                            projection.InputHash,
                            projection.ConfigurationVersion,
                            projection.ConfigurationHash,
                            projection.ReceiptCount,
                            projection.SuppressionCount),
                        null);
                }
                else if (schema == AlertContractVersionsV2.Evaluation)
                {
                    var projection = AlertEvaluationConsumerV2.Validate(bytes);
                    if (projection.EvaluationId != evaluationId
                        || projection.InputHash != inputHash
                        || projection.ConfigurationVersion != configurationVersion
                        || projection.ConfigurationHash != configurationHash
                        || projection.ReceiptCount != receiptCount
                        || projection.SuppressionCount != suppressionCount)
                    {
                        return VersionedEvaluationsUnavailable();
                    }
                    item = new(AlertContractKind.V2, bytes, null, projection);
                }
                else
                {
                    return VersionedEvaluationsUnavailable();
                }

                if (items.Count == limit
                    || bytesRead > AlertEngineQueryLimits.MaximumPageBytes - bytes.Length)
                {
                    hasMore = true;
                    break;
                }
                items.Add(item);
                bytesRead += bytes.Length;
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? EvaluationId(items[^1]) : null,
                Exhausted: !hasMore,
                CanonicalByteCount: bytesRead);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(
                AlertEngineQueryStatus.Busy,
                [],
                Code: "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return VersionedEvaluationsUnavailable();
        }
    }

    public AlertVersionedSuppressionQueryPage ListSuppressionsVersioned(
        string evaluationId,
        long? afterSuppressionOrdinal,
        int limit)
    {
        if (!ValidVersionedPage(limit)
            || !CanonicalHash(evaluationId)
            || afterSuppressionOrdinal is < 0)
        {
            return new(
                AlertEngineQueryStatus.Invalid,
                [],
                Code: "invalid_alert_query");
        }

        try
        {
            using var connection = Open();
            if (!AlertSchemaV2.IsValid(connection, null))
            {
                return VersionedSuppressionsUnavailable();
            }
            var parentSchema = ReadScalar(
                connection,
                null,
                "SELECT schema_version FROM alert_evaluations WHERE evaluation_id=$id;",
                ("$id", evaluationId));
            if (parentSchema is null)
            {
                return new(
                    AlertEngineQueryStatus.NotFound,
                    [],
                    Code: "alert_not_found");
            }
            if (parentSchema is not AlertContractVersions.Evaluation
                and not AlertContractVersionsV2.Evaluation)
            {
                return VersionedSuppressionsUnavailable();
            }

            using var command = Command(
                connection,
                null,
                """
                SELECT suppression_ordinal,rule_id,rule_version,code,canonical_json
                FROM alert_suppressions
                WHERE evaluation_id=$evaluation
                  AND ($after IS NULL OR suppression_ordinal>$after)
                ORDER BY suppression_ordinal
                LIMIT $take;
                """,
                ("$evaluation", evaluationId),
                ("$after", afterSuppressionOrdinal is null
                    ? DBNull.Value
                    : afterSuppressionOrdinal.Value),
                ("$take", limit + 1));
            using var reader = command.ExecuteReader();
            var items = new List<AlertVersionedSuppressionQueryItem>();
            var bytesRead = 0;
            var hasMore = false;
            while (reader.Read())
            {
                var ordinal = reader.GetInt64(0);
                var ruleId = reader.GetString(1);
                var ruleVersion = reader.GetString(2);
                var code = reader.GetString(3);
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(4));
                if (ordinal < 0
                    || bytes.Length > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return VersionedSuppressionsUnavailable();
                }

                AlertVersionedSuppressionQueryItem item;
                if (parentSchema == AlertContractVersions.Evaluation)
                {
                    var projection = AlertSuppressionConsumerV1.Validate(bytes);
                    if (projection.EvaluationId != evaluationId
                        || projection.RuleId != ruleId
                        || projection.RuleVersion != ruleVersion
                        || projection.Code != code)
                    {
                        return VersionedSuppressionsUnavailable();
                    }
                    item = new(
                        AlertContractKind.V1,
                        ordinal,
                        bytes,
                        projection,
                        null);
                }
                else
                {
                    var projection = AlertEvaluationConsumerV2.ValidateSuppression(bytes);
                    if (projection.EvaluationId != evaluationId
                        || projection.RuleId != ruleId
                        || projection.RuleVersion != ruleVersion
                        || projection.Code != code)
                    {
                        return VersionedSuppressionsUnavailable();
                    }
                    item = new(
                        AlertContractKind.V2,
                        ordinal,
                        bytes,
                        null,
                        projection);
                }

                if (items.Count == limit
                    || bytesRead > AlertEngineQueryLimits.MaximumPageBytes - bytes.Length)
                {
                    hasMore = true;
                    break;
                }
                items.Add(item);
                bytesRead += bytes.Length;
            }

            return new(
                AlertEngineQueryStatus.Success,
                Array.AsReadOnly(items.ToArray()),
                hasMore ? items[^1].SuppressionOrdinal : null,
                Exhausted: !hasMore,
                CanonicalByteCount: bytesRead);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            return new(
                AlertEngineQueryStatus.Busy,
                [],
                Code: "alert_store_busy");
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return VersionedSuppressionsUnavailable();
        }
    }

    private static string ReceiptId(AlertVersionedReceiptQueryItem item) =>
        item.ReceiptV1?.AlertId ?? item.ReceiptV2!.AlertId;

    private static string EvaluationId(AlertVersionedEvaluationQueryItem item) =>
        item.EvaluationV1?.EvaluationId ?? item.EvaluationV2!.EvaluationId;

    private static bool ValidVersionedPage(int limit) =>
        limit is >= 1 and <= AlertEngineQueryLimits.MaximumPageSize;

    private static AlertVersionedReceiptQueryPage VersionedReceiptsUnavailable() =>
        new(
            AlertEngineQueryStatus.Unavailable,
            [],
            Code: "alert_store_unavailable");

    private static AlertVersionedEvaluationQueryPage VersionedEvaluationsUnavailable() =>
        new(
            AlertEngineQueryStatus.Unavailable,
            [],
            Code: "alert_store_unavailable");

    private static AlertVersionedSuppressionQueryPage VersionedSuppressionsUnavailable() =>
        new(
            AlertEngineQueryStatus.Unavailable,
            [],
            Code: "alert_store_unavailable");
}

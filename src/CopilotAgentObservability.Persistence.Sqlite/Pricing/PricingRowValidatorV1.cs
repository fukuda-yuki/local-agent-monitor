using System.Globalization;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal static class PricingRowValidatorV1
{
    internal static bool Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try
        {
            return ValidateCore(connection, transaction);
        }
        catch (Exception exception) when (exception is
            SqliteException or
            InvalidCastException or
            InvalidOperationException or
            FormatException or
            OverflowException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool ValidateCore(SqliteConnection connection, SqliteTransaction? transaction) =>
        PricingSchemaV1.IsValid(connection, transaction)
        && ScalarInt64(connection, transaction, "SELECT COUNT(*) FROM pricing_configuration_previews;") <= 32
        && !HasRows(connection, transaction, "PRAGMA foreign_key_check;")
        && ValidateCatalogs(connection, transaction)
        && ValidatePreviews(connection, transaction)
        && ValidateConfigurations(connection, transaction)
        && ValidateConfigurationHeads(connection, transaction)
        && ValidateConfigurationCommits(connection, transaction)
        && ValidateRuns(connection, transaction)
        && ValidateTargets(connection, transaction)
        && ValidateEvents(connection, transaction)
        && ValidateEstimates(connection, transaction)
        && ValidateTargetResults(connection, transaction)
        && ValidateAttempts(connection, transaction)
        && ValidateEstimateHeads(connection, transaction)
        && ValidateBaseReferences(connection, transaction)
        && ValidateBudgetResults(connection, transaction)
        && ValidateRunStates(connection, transaction);

    private static bool ValidateCatalogs(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT catalog_sha256,schema_version,canonical_blob,document_count,first_recorded_at_utc
            FROM pricing_catalog_snapshots
            ORDER BY catalog_sha256;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sha = reader.GetString(0);
            var bytes = (byte[])reader[2];
            PricingCatalog catalog;
            try
            {
                catalog = PricingCatalogSnapshotConsumer.Deserialize(bytes);
            }
            catch (PricingRegistryValidationException)
            {
                return false;
            }

            if (!IsLowerSha(sha)
                || reader.GetString(1) != PricingContractVersions.CatalogSnapshot
                || bytes.Length is < 1 or > 4_194_304
                || catalog.CatalogSha256 != sha
                || catalog.Documents.Count != reader.GetInt32(3)
                || Sha256(bytes) != sha
                || !IsTimestamp(reader.GetString(4)))
                return false;
        }
        return true;
    }

    private static bool ValidatePreviews(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT preview_digest,canonical_sha256,canonical_blob,configuration_id,
                   expected_head_revision,expected_configuration_id,catalog_sha256,
                   selection_digest,created_at_utc,expires_at_utc
            FROM pricing_configuration_previews
            ORDER BY expires_at_utc,preview_digest;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader[2];
            var consumed = CostConfigurationPreviewConsumerV1.Consume(bytes);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } preview
                || preview.PreviewDigest != reader.GetString(0)
                || Sha256(bytes) != reader.GetString(1)
                || preview.Configuration.ConfigurationId != reader.GetString(3)
                || preview.ExpectedHeadRevision != reader.GetInt64(4)
                || preview.ExpectedConfigurationId != NullableString(reader, 5)
                || preview.CatalogSha256 != reader.GetString(6)
                || preview.SelectionDigest != reader.GetString(7)
                || Format(preview.Configuration.CreatedAtUtc) != reader.GetString(8)
                || Format(preview.Configuration.CreatedAtUtc.AddMinutes(15)) != reader.GetString(9))
                return false;
        }
        return true;
    }

    private static bool ValidateConfigurations(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT c.configuration_id,c.predecessor_configuration_id,c.schema_version,
                   c.catalog_sha256,c.canonical_sha256,c.canonical_blob,c.created_at_utc,
                   c.source_count,c.budget_count,s.catalog_sha256
            FROM pricing_configurations c
            LEFT JOIN pricing_catalog_snapshots s ON s.catalog_sha256=c.catalog_sha256
            ORDER BY c.configuration_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader[5];
            var consumed = CostConfigurationConsumerV1.Consume(bytes);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } value
                || value.ConfigurationId != reader.GetString(0)
                || value.PredecessorConfigurationId != NullableString(reader, 1)
                || reader.GetString(2) != "cost.configuration.v1"
                || value.CatalogSha256 != reader.GetString(3)
                || reader.IsDBNull(9)
                || Sha256(bytes) != reader.GetString(4)
                || Format(value.CreatedAtUtc) != reader.GetString(6)
                || value.SourceEntries.Count != reader.GetInt32(7)
                || value.BudgetEntries.Count != reader.GetInt32(8))
                return false;
        }
        return true;
    }

    private static bool ValidateConfigurationHeads(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT h.head_revision,h.configuration_id,h.previous_head_revision,
                   h.previous_configuration_id,h.committed_at_utc,c.canonical_blob
            FROM pricing_configuration_heads h
            LEFT JOIN pricing_configurations c ON c.configuration_id=h.configuration_id
            ORDER BY h.head_revision;
            """);
        using var reader = command.ExecuteReader();
        long expectedRevision = 1;
        string? previousConfigurationId = null;
        while (reader.Read())
        {
            if (reader.IsDBNull(5)) return false;
            var consumed = CostConfigurationConsumerV1.Consume((byte[])reader[5]);
            var revision = reader.GetInt64(0);
            var configurationId = reader.GetString(1);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } configuration
                || revision != expectedRevision
                || configuration.ConfigurationId != configurationId
                || NullableInt64(reader, 2) != (revision == 1 ? null : revision - 1)
                || NullableString(reader, 3) != previousConfigurationId
                || configuration.PredecessorConfigurationId != previousConfigurationId
                || !IsTimestamp(reader.GetString(4)))
                return false;
            previousConfigurationId = configurationId;
            expectedRevision++;
        }
        return expectedRevision - 1
            == ScalarInt64(connection, transaction, "SELECT COUNT(*) FROM pricing_configurations;");
    }

    private static bool ValidateConfigurationCommits(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT m.head_revision,m.configuration_id,m.preview_digest,m.request_sha256,
                   m.canonical_request_blob,m.canonical_result_blob,
                   h.previous_configuration_id,c.canonical_blob,c.catalog_sha256
            FROM pricing_configuration_commits m
            LEFT JOIN pricing_configuration_heads h
              ON h.head_revision=m.head_revision AND h.configuration_id=m.configuration_id
            LEFT JOIN pricing_configurations c ON c.configuration_id=m.configuration_id
            ORDER BY m.head_revision;
            """);
        using var reader = command.ExecuteReader();
        long expectedRevision = 1;
        while (reader.Read())
        {
            if (reader.IsDBNull(7)) return false;
            var revision = reader.GetInt64(0);
            var configurationId = reader.GetString(1);
            var requestBytes = (byte[])reader[4];
            var resultBytes = (byte[])reader[5];
            var request = CostConfigurationCommitConsumerV1.ConsumeRequest(requestBytes);
            var result = CostConfigurationCommitConsumerV1.ConsumeResult(resultBytes);
            var configuration = CostConfigurationConsumerV1.Consume((byte[])reader[7]);
            if (revision != expectedRevision
                || request.Status != CostConsumerStatus.Success
                || request.Value is not { } preview
                || result.Status != CostConsumerStatus.Success
                || result.Value is not { } committed
                || configuration.Status != CostConsumerStatus.Success
                || configuration.Value is not { } stored
                || !CostConfigurationCanonicalJsonV1.Serialize(preview.Configuration)
                    .AsSpan().SequenceEqual(CostConfigurationCanonicalJsonV1.Serialize(stored))
                || preview.ExpectedHeadRevision != revision - 1
                || preview.ExpectedConfigurationId != NullableString(reader, 6)
                || preview.PreviewDigest != reader.GetString(2)
                || Sha256(requestBytes) != reader.GetString(3)
                || committed.ConfigurationId != configurationId
                || committed.HeadRevision != revision
                || committed.CatalogSha256 != reader.GetString(8))
                return false;
            expectedRevision++;
        }
        return expectedRevision - 1
            == ScalarInt64(connection, transaction, "SELECT COUNT(*) FROM pricing_configuration_heads;");
    }

    private static bool ValidateRuns(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT r.run_id,r.request_schema_version,r.idempotency_key,r.request_digest,
                   r.canonical_request_blob,r.configuration_id,r.configuration_head_revision,
                   r.catalog_sha256,r.calculation_time_utc,r.target_count,r.scope_count,
                   r.created_at_utc,c.catalog_sha256,h.configuration_id
            FROM pricing_recalculation_runs r
            LEFT JOIN pricing_configurations c ON c.configuration_id=r.configuration_id
            LEFT JOIN pricing_configuration_heads h
              ON h.head_revision=r.configuration_head_revision
             AND h.configuration_id=r.configuration_id
            ORDER BY r.calculation_time_utc,r.run_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            var bytes = (byte[])reader[4];
            var consumed = CostRecalculationRequestCanonicalJsonV1.Consume(bytes);
            if (!IsCanonicalUuidV7(runId)
                || reader.GetString(1) != CostRecalculationRequestCanonicalJsonV1.SchemaVersion
                || consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } request
                || request.IdempotencyKey != reader.GetString(2)
                || CostIdentityV1.Hash("cost-recalculation-request/v1", bytes) != reader.GetString(3)
                || request.ConfigurationId != reader.GetString(5)
                || request.ExpectedHeadRevision != reader.GetInt64(6)
                || request.CatalogSha256 != reader.GetString(7)
                || reader.IsDBNull(12)
                || reader.GetString(12) != request.CatalogSha256
                || reader.IsDBNull(13)
                || request.SessionIds.Count != reader.GetInt32(9)
                || request.BudgetScopes.Count != reader.GetInt32(10)
                || !IsTimestamp(reader.GetString(8))
                || reader.GetString(8) != reader.GetString(11))
                return false;
        }
        return true;
    }

    private static bool ValidateTargets(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT t.run_id,t.target_ordinal,t.session_id,t.session_status,
                   t.session_effective_at_utc,t.session_updated_at_utc,
                   t.source_partition_state,t.source_partition_count,
                   t.source_partition_digest,t.source_surface,t.source_application_version,
                   t.base_head_revision,t.base_estimate_id,t.base_attempt_revision,
                   r.target_count,r.canonical_request_blob
            FROM pricing_recalculation_targets t
            LEFT JOIN pricing_recalculation_runs r ON r.run_id=t.run_id
            ORDER BY t.run_id,t.target_ordinal;
            """);
        using var reader = command.ExecuteReader();
        string? previousRunId = null;
        var expectedOrdinal = 0;
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            if (runId != previousRunId)
            {
                previousRunId = runId;
                expectedOrdinal = 0;
            }
            if (reader.IsDBNull(15)) return false;
            var consumed = CostRecalculationRequestCanonicalJsonV1.Consume((byte[])reader[15]);
            var ordinal = reader.GetInt32(1);
            var sessionId = reader.GetString(2);
            var baseHead = NullableInt64(reader, 11);
            var baseEstimate = NullableString(reader, 12);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } request
                || ordinal != expectedOrdinal
                || ordinal >= reader.GetInt32(14)
                || request.SessionIds[ordinal] != sessionId
                || !IsCanonicalUuid(sessionId)
                || reader.GetString(3) is not ("completed" or "failed")
                || !IsTimestamp(reader.GetString(4))
                || !IsTimestamp(reader.GetString(5))
                || !IsPartitionShape(
                    reader.GetString(6),
                    reader.GetInt32(7),
                    NullableString(reader, 9),
                    NullableString(reader, 10))
                || !IsLowerSha(reader.GetString(8))
                || (baseHead is null) != (baseEstimate is null)
                || baseHead is < 1
                || baseEstimate is not null && !IsPrefixedSha(baseEstimate, "pricing-estimate-")
                || reader.GetInt64(13) < 0)
                return false;
            expectedOrdinal++;
        }
        return !HasRows(
            connection,
            transaction,
            """
            SELECT 1 FROM pricing_recalculation_runs r
            WHERE (SELECT COUNT(*) FROM pricing_recalculation_targets t WHERE t.run_id=r.run_id)
                  <> r.target_count
            LIMIT 1;
            """);
    }

    private static bool ValidateEvents(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT e.run_id,e.event_sequence,e.event_kind,e.occurred_at_utc,e.failure_phase,
                   e.failure_ordinal_kind,e.failure_ordinal,e.failure_code,
                   r.calculation_time_utc,r.target_count,r.scope_count
            FROM pricing_recalculation_events e
            LEFT JOIN pricing_recalculation_runs r ON r.run_id=e.run_id
            ORDER BY e.run_id,e.event_sequence;
            """);
        using var reader = command.ExecuteReader();
        string? previousRunId = null;
        string? previousOccurredAt = null;
        var expectedSequence = 0;
        string? firstKind = null;
        string? secondKind = null;
        string? thirdKind = null;
        string? secondFailurePhase = null;
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            if (runId != previousRunId)
            {
                if (previousRunId is not null
                    && !IsEventSequenceValid(
                        expectedSequence,
                        firstKind!,
                        secondKind,
                        thirdKind,
                        secondFailurePhase))
                    return false;
                previousRunId = runId;
                previousOccurredAt = null;
                expectedSequence = 0;
                firstKind = null;
                secondKind = null;
                thirdKind = null;
                secondFailurePhase = null;
            }

            var value = new EventRow(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                NullableInt32(reader, 6),
                NullableString(reader, 7));
            if (reader.IsDBNull(8)
                || value.Sequence != expectedSequence
                || !IsTimestamp(value.OccurredAtUtc)
                || previousOccurredAt is not null
                    && string.CompareOrdinal(value.OccurredAtUtc, previousOccurredAt) < 0
                || value.Sequence == 0 && value.OccurredAtUtc != reader.GetString(8)
                || value.FailureOrdinalKind == "target"
                    && value.FailureOrdinal is { } targetOrdinal
                    && targetOrdinal >= reader.GetInt32(9)
                || value.FailureOrdinalKind == "scope"
                    && value.FailureOrdinal is { } scopeOrdinal
                    && scopeOrdinal >= reader.GetInt32(10)
                || !IsEventShapeValid(value))
                return false;

            if (expectedSequence == 0) firstKind = value.Kind;
            if (expectedSequence == 1)
            {
                secondKind = value.Kind;
                secondFailurePhase = value.FailurePhase;
            }
            if (expectedSequence == 2) thirdKind = value.Kind;
            previousOccurredAt = value.OccurredAtUtc;
            expectedSequence++;
        }
        if (previousRunId is not null
            && !IsEventSequenceValid(
                expectedSequence,
                firstKind!,
                secondKind,
                thirdKind,
                secondFailurePhase))
            return false;
        return !HasRows(
            connection,
            transaction,
            """
            SELECT 1 FROM pricing_recalculation_runs r
            WHERE NOT EXISTS(
                SELECT 1 FROM pricing_recalculation_events e
                WHERE e.run_id=r.run_id AND e.event_sequence=0 AND e.event_kind='requested')
            LIMIT 1;
            """);
    }

    private static bool ValidateEstimates(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT e.estimate_id,e.supersedes_estimate_id,e.schema_version,e.session_id,
                   e.catalog_sha256,e.configuration_id,e.source_entry_ordinal,e.run_id,
                   e.target_ordinal,e.calculation_time_utc,e.session_effective_at_utc,
                   e.status,e.source_surface,e.source_application_version,e.provider,e.model,
                   e.billing_mode,e.pricing_route,e.registry_version,e.registry_source_kind,
                   e.currency,e.amount_text,e.canonical_sha256,e.canonical_blob,
                   s.canonical_blob,c.canonical_blob,r.canonical_request_blob,
                   t.session_id,t.session_effective_at_utc,r.calculation_time_utc
            FROM pricing_estimates e
            LEFT JOIN pricing_catalog_snapshots s ON s.catalog_sha256=e.catalog_sha256
            LEFT JOIN pricing_configurations c ON c.configuration_id=e.configuration_id
            LEFT JOIN pricing_recalculation_runs r ON r.run_id=e.run_id
            LEFT JOIN pricing_recalculation_targets t
              ON t.run_id=e.run_id AND t.target_ordinal=e.target_ordinal
            ORDER BY e.estimate_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(24)
                || reader.IsDBNull(25)
                || reader.IsDBNull(26)
                || reader.IsDBNull(27))
                return false;
            PricingCatalog catalog;
            PricingEstimateRecord estimate;
            try
            {
                catalog = PricingCatalogSnapshotConsumer.Deserialize((byte[])reader[24]);
                estimate = PricingEstimateConsumer.Deserialize((byte[])reader[23], catalog);
            }
            catch (Exception exception) when (exception is
                PricingRegistryValidationException or PricingEstimateValidationException)
            {
                return false;
            }
            var configurationResult = CostConfigurationConsumerV1.Consume((byte[])reader[25]);
            var requestResult = CostRecalculationRequestCanonicalJsonV1.Consume((byte[])reader[26]);
            var sourceOrdinal = reader.GetInt32(6);
            if (reader.GetString(2) != PricingContractVersions.Estimate
                || configurationResult.Status != CostConsumerStatus.Success
                || configurationResult.Value is not { } configuration
                || requestResult.Status != CostConsumerStatus.Success
                || requestResult.Value is not { } request
                || sourceOrdinal < 0
                || sourceOrdinal >= configuration.SourceEntries.Count
                || configuration.ConfigurationId != reader.GetString(5)
                || configuration.CatalogSha256 != reader.GetString(4)
                || request.ConfigurationId != reader.GetString(5)
                || request.CatalogSha256 != reader.GetString(4)
                || estimate.EstimateId != reader.GetString(0)
                || estimate.SupersedesEstimateId != NullableString(reader, 1)
                || estimate.Source.SessionId != reader.GetString(3)
                || reader.GetString(27) != estimate.Source.SessionId
                || estimate.CatalogSha256 != reader.GetString(4)
                || Format(estimate.CalculationTimeUtc) != reader.GetString(9)
                || reader.GetString(29) != reader.GetString(9)
                || Format(estimate.Source.SessionObservedAtUtc) != reader.GetString(10)
                || reader.GetString(28) != reader.GetString(10)
                || estimate.Status != reader.GetString(11)
                || estimate.Source.SourceSurface != reader.GetString(12)
                || estimate.Source.SourceVersion != reader.GetString(13)
                || estimate.Source.Provider != reader.GetString(14)
                || estimate.Source.ModelId != reader.GetString(15)
                || estimate.Source.BillingMode != reader.GetString(16)
                || estimate.Source.PricingRoute != reader.GetString(17)
                || estimate.Registry?.RegistryVersion != NullableString(reader, 18)
                || estimate.Registry?.SourceKind != NullableString(reader, 19)
                || estimate.Currency != NullableString(reader, 20)
                || estimate.Amount?.ToString(CultureInfo.InvariantCulture) != NullableString(reader, 21)
                || Sha256((byte[])reader[23]) != reader.GetString(22)
                || !ConfigurationSourceMatches(configuration, sourceOrdinal, estimate))
                return false;
        }
        return true;
    }

    private static bool ValidateTargetResults(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT x.result_kind,x.estimate_status,x.estimate_id,x.result_code,
                   x.run_id,x.target_ordinal,e.run_id,e.target_ordinal,e.status,t.run_id
            FROM pricing_recalculation_target_results x
            LEFT JOIN pricing_recalculation_targets t
              ON t.run_id=x.run_id AND t.target_ordinal=x.target_ordinal
            LEFT JOIN pricing_estimates e ON e.estimate_id=x.estimate_id
            ORDER BY x.run_id,x.target_ordinal;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = new ResultRow(
                reader.GetString(0),
                NullableString(reader, 1),
                NullableString(reader, 2),
                NullableString(reader, 3));
            if (reader.IsDBNull(9)
                || !IsResultShapeValid(value)
                || value.Kind == "estimate"
                    && (reader.IsDBNull(6)
                        || reader.GetString(6) != reader.GetString(4)
                        || reader.GetInt32(7) != reader.GetInt32(5)
                        || reader.GetString(8) != value.EstimateStatus))
                return false;
        }
        return true;
    }

    private static bool ValidateAttempts(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT a.session_id,a.attempt_revision,a.run_id,a.target_ordinal,
                   a.result_kind,a.estimate_status,a.estimate_id,a.result_code,
                   t.session_id,t.base_attempt_revision,
                   x.result_kind,x.estimate_status,x.estimate_id,x.result_code
            FROM pricing_session_attempts a
            LEFT JOIN pricing_recalculation_targets t
              ON t.run_id=a.run_id AND t.target_ordinal=a.target_ordinal
            LEFT JOIN pricing_recalculation_target_results x
              ON x.run_id=a.run_id AND x.target_ordinal=a.target_ordinal
            ORDER BY a.session_id,a.attempt_revision;
            """);
        using var reader = command.ExecuteReader();
        string? previousSessionId = null;
        long previousRevision = 0;
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            if (sessionId != previousSessionId)
            {
                previousSessionId = sessionId;
                previousRevision = 0;
            }
            var revision = reader.GetInt64(1);
            if (reader.IsDBNull(8)
                || reader.IsDBNull(10)
                || revision != previousRevision + 1
                || reader.GetString(8) != sessionId
                || reader.GetInt64(9) + 1 != revision
                || reader.GetString(4) != reader.GetString(10)
                || NullableString(reader, 5) != NullableString(reader, 11)
                || NullableString(reader, 6) != NullableString(reader, 12)
                || NullableString(reader, 7) != NullableString(reader, 13))
                return false;
            previousRevision = revision;
        }
        return true;
    }

    private static bool ValidateEstimateHeads(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT h.session_id,h.head_revision,h.estimate_id,h.previous_head_revision,
                   h.previous_estimate_id,e.session_id,e.supersedes_estimate_id
            FROM pricing_estimate_heads h
            LEFT JOIN pricing_estimates e ON e.estimate_id=h.estimate_id
            ORDER BY h.session_id,h.head_revision;
            """);
        using var reader = command.ExecuteReader();
        string? previousSessionId = null;
        string? previousEstimateId = null;
        long previousRevision = 0;
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            if (sessionId != previousSessionId)
            {
                previousSessionId = sessionId;
                previousEstimateId = null;
                previousRevision = 0;
            }
            var revision = reader.GetInt64(1);
            var estimateId = reader.GetString(2);
            if (reader.IsDBNull(5)
                || revision != previousRevision + 1
                || reader.GetString(5) != sessionId
                || NullableInt64(reader, 3) != (revision == 1 ? null : revision - 1)
                || NullableString(reader, 4) != previousEstimateId
                || NullableString(reader, 6) != previousEstimateId)
                return false;
            previousRevision = revision;
            previousEstimateId = estimateId;
        }
        return true;
    }

    private static bool ValidateBaseReferences(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        !HasRows(
            connection,
            transaction,
            """
            SELECT 1 FROM pricing_recalculation_targets t
            WHERE t.base_attempt_revision>0
              AND NOT EXISTS(
                  SELECT 1 FROM pricing_session_attempts a
                  WHERE a.session_id=t.session_id
                    AND a.attempt_revision=t.base_attempt_revision)
            LIMIT 1;
            """)
        && !HasRows(
            connection,
            transaction,
            """
            SELECT 1 FROM pricing_estimates e
            JOIN pricing_recalculation_targets t
              ON t.run_id=e.run_id AND t.target_ordinal=e.target_ordinal
            WHERE NOT EXISTS(
                SELECT 1 FROM pricing_estimate_heads h
                WHERE h.session_id=t.session_id
                  AND h.head_revision=COALESCE(t.base_head_revision,0)+1
                  AND h.estimate_id=e.estimate_id)
            LIMIT 1;
            """);

    private static bool ValidateBudgetResults(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT b.scope_ordinal,b.scope_kind,b.scope_id,b.scope_start_utc,b.scope_end_utc,
                   b.rule_id,b.rule_version,b.evaluation_id,b.outcome_kind,b.alert_id,
                   b.suppression_ordinal,b.suppression_code,r.scope_count,
                   r.canonical_request_blob,e.schema_version,
                   ar.alert_id,ar.evaluation_id,ar.schema_version,
                   s.evaluation_id,s.suppression_ordinal,s.rule_id,s.rule_version,s.code
            FROM pricing_recalculation_budget_results b
            LEFT JOIN pricing_recalculation_runs r ON r.run_id=b.run_id
            LEFT JOIN alert_evaluations e ON e.evaluation_id=b.evaluation_id
            LEFT JOIN alert_receipts ar ON ar.alert_id=b.alert_id
            LEFT JOIN alert_suppressions s
              ON s.evaluation_id=b.evaluation_id
             AND s.suppression_ordinal=b.suppression_ordinal
            ORDER BY b.run_id,b.scope_ordinal;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(13)) return false;
            var requestResult = CostRecalculationRequestCanonicalJsonV1.Consume((byte[])reader[13]);
            var ordinal = reader.GetInt32(0);
            var value = new BudgetRow(
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                NullableString(reader, 9),
                NullableInt32(reader, 10),
                NullableString(reader, 11));
            if (requestResult.Status != CostConsumerStatus.Success
                || requestResult.Value is not { } request
                || ordinal < 0
                || ordinal >= reader.GetInt32(12)
                || !IsBudgetShapeValid(value, request.BudgetScopes[ordinal])
                || reader.IsDBNull(14)
                || reader.GetString(14) != "alert.evaluation.v2")
                return false;
            if (value.OutcomeKind == "receipt"
                && (reader.IsDBNull(15)
                    || reader.GetString(16) != value.EvaluationId
                    || reader.GetString(17) != "alert.receipt.v2"))
                return false;
            if (value.OutcomeKind == "suppression"
                && (reader.IsDBNull(18)
                    || reader.GetInt32(19) != value.SuppressionOrdinal
                    || reader.GetString(20) != value.RuleId
                    || reader.GetString(21) != value.RuleVersion
                    || reader.GetString(22) != value.SuppressionCode))
                return false;
        }
        return true;
    }

    private static bool ValidateRunStates(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT r.target_count,r.scope_count,
                   (SELECT COUNT(*) FROM pricing_recalculation_events e WHERE e.run_id=r.run_id),
                   (SELECT event_kind FROM pricing_recalculation_events e
                    WHERE e.run_id=r.run_id ORDER BY event_sequence DESC LIMIT 1),
                   (SELECT failure_code FROM pricing_recalculation_events e
                    WHERE e.run_id=r.run_id ORDER BY event_sequence DESC LIMIT 1),
                   (SELECT COUNT(*) FROM pricing_recalculation_target_results x WHERE x.run_id=r.run_id),
                   (SELECT COUNT(*) FROM pricing_session_attempts a WHERE a.run_id=r.run_id),
                   (SELECT COUNT(*) FROM pricing_estimates p WHERE p.run_id=r.run_id),
                   (SELECT COUNT(*) FROM pricing_recalculation_budget_results b WHERE b.run_id=r.run_id),
                   (SELECT COUNT(*) FROM pricing_recalculation_target_results x
                    WHERE x.run_id=r.run_id AND x.result_kind='failed'),
                   (SELECT COUNT(DISTINCT x.result_code) FROM pricing_recalculation_target_results x
                    WHERE x.run_id=r.run_id AND x.result_kind='failed'),
                   (SELECT MIN(x.result_code) FROM pricing_recalculation_target_results x
                    WHERE x.run_id=r.run_id AND x.result_kind='failed'),
                   (SELECT failure_phase FROM pricing_recalculation_events e
                    WHERE e.run_id=r.run_id ORDER BY event_sequence DESC LIMIT 1),
                   (SELECT failure_ordinal FROM pricing_recalculation_events e
                    WHERE e.run_id=r.run_id ORDER BY event_sequence DESC LIMIT 1),
                   (SELECT COUNT(*) FROM pricing_recalculation_target_results x
                    WHERE x.run_id=r.run_id AND x.result_kind='failed'
                      AND x.target_ordinal=(
                        SELECT failure_ordinal FROM pricing_recalculation_events e
                        WHERE e.run_id=r.run_id ORDER BY event_sequence DESC LIMIT 1))
            FROM pricing_recalculation_runs r
            ORDER BY r.run_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var targetCount = reader.GetInt32(0);
            var scopeCount = reader.GetInt32(1);
            var eventCount = reader.GetInt64(2);
            var terminalKind = reader.GetString(3);
            var resultCount = reader.GetInt64(5);
            var attemptCount = reader.GetInt64(6);
            var estimateCount = reader.GetInt64(7);
            var budgetCount = reader.GetInt64(8);
            var failedCount = reader.GetInt64(9);
            var failurePhase = NullableString(reader, 12);
            var terminal = terminalKind is "succeeded" or "failed";
            if (!terminal)
            {
                if (eventCount is < 1 or > 2
                    || resultCount != 0
                    || attemptCount != 0
                    || estimateCount != 0
                    || budgetCount != 0)
                    return false;
                continue;
            }
            if (resultCount != targetCount || attemptCount != targetCount)
                return false;
            if (terminalKind == "succeeded")
            {
                if (failedCount != 0 || budgetCount != scopeCount)
                    return false;
            }
            else if (estimateCount != 0
                || budgetCount != 0
                || (failedCount > 0
                    && (reader.GetInt64(10) != 1
                        || reader.GetString(11) != NullableString(reader, 4)))
                || ((failurePhase is "head_input" or "recovery")
                    && failedCount != targetCount)
                || ((failurePhase is "adapter" or "estimate_validation" or "pricing_store")
                    && reader.GetInt64(14) != 1))
                return false;
        }
        return true;
    }

    private static bool IsEventShapeValid(EventRow value)
    {
        if (value.Kind is "requested" or "running" or "succeeded")
            return value.FailurePhase is null
                && value.FailureOrdinalKind is null
                && value.FailureOrdinal is null
                && value.FailureCode is null;
        if (value.Kind != "failed" || value.FailurePhase is null || value.FailureCode is null)
            return false;
        return value.FailurePhase switch
        {
            "head_input" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode is "stale_recalculation_input" or "stale_active_estimate",
            "adapter" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode == "source_adapter_failed",
            "estimate_validation" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode is "invalid_estimate_source" or "pricing_estimation_failed",
            "budget_payload" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "budget_payload_too_large",
            "pricing_store" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode == "pricing_store_failed",
            "alert_evaluation" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "alert_evaluation_failed",
            "alert_store" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "alert_store_failed",
            "recovery" => value.FailureOrdinalKind is null
                && value.FailureOrdinal is null
                && value.FailureCode == "recalculation_interrupted",
            _ => false,
        };
    }

    private static bool IsEventSequenceValid(
        int count,
        string first,
        string? second,
        string? third,
        string? secondFailurePhase) =>
        count == 1 && first == "requested"
        || count == 2
            && first == "requested"
            && (second == "running"
                || second == "failed" && secondFailurePhase == "recovery")
        || count == 3
            && first == "requested"
            && second == "running"
            && third is "succeeded" or "failed";

    private static bool IsResultShapeValid(ResultRow value) =>
        value.Kind switch
        {
            "estimate" => value.EstimateStatus is "estimated" or "partial" or "not-estimable"
                && IsPrefixedSha(value.EstimateId, "pricing-estimate-")
                && value.ResultCode is null,
            "unavailable" => value.EstimateStatus is null
                && value.EstimateId is null
                && value.ResultCode is
                    "source_mapping_unavailable"
                    or "source_adapter_unavailable"
                    or "codex_adapter_unavailable",
            "failed" => value.EstimateStatus is null
                && value.EstimateId is null
                && IsFailureCode(value.ResultCode),
            _ => false,
        };

    private static bool IsBudgetShapeValid(BudgetRow value, CostBudgetScopeV1 requestScope)
    {
        if (value.ScopeKind != requestScope.ScopeKind
            || !IsPrefixedSha(value.ScopeId, "cost-scope-")
            || value.RuleVersion != "1"
            || !IsLowerSha(value.EvaluationId))
            return false;
        var scopeValid = requestScope.ScopeKind switch
        {
            "session" => value.RuleId == "session-estimated-cost-threshold"
                && value.ScopeStartUtc is null
                && value.ScopeEndUtc is null,
            "utc_day" => value.RuleId == "daily-estimated-cost-threshold"
                && DateOnly.TryParseExact(
                    requestScope.UtcDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day)
                && value.ScopeStartUtc == Format(
                    new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
                && value.ScopeEndUtc == Format(
                    new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            "rolling_period" => value.RuleId == "period-estimated-cost-threshold"
                && value.ScopeEndUtc == Format(requestScope.CutoffUtc!.Value)
                && value.ScopeStartUtc == Format(
                    requestScope.CutoffUtc.Value.AddDays(-requestScope.WindowDays!.Value)),
            _ => false,
        };
        if (!scopeValid) return false;
        return value.OutcomeKind switch
        {
            "receipt" => IsLowerSha(value.AlertId)
                && value.SuppressionOrdinal is null
                && value.SuppressionCode is null,
            "suppression" => value.AlertId is null
                && value.SuppressionOrdinal is >= 0
                && IsSuppressionCode(value.SuppressionCode),
            "no_match" => value.AlertId is null
                && value.SuppressionOrdinal is null
                && value.SuppressionCode is null,
            _ => false,
        };
    }

    private static bool ConfigurationSourceMatches(
        CostConfigurationV1 configuration,
        int sourceOrdinal,
        PricingEstimateRecord estimate)
    {
        var source = configuration.SourceEntries[sourceOrdinal];
        var provenanceId = configuration.ConfigurationId + $".source-entry-{sourceOrdinal:000}";
        return source.SourceSurface == estimate.Source.SourceSurface
            && source.ApplicationVersion == estimate.Source.SourceVersion
            && source.Provider == estimate.Source.Provider
            && source.BillingMode == estimate.Source.BillingMode
            && source.PricingRoute == estimate.Source.PricingRoute
            && ProvenanceMatches(estimate.Source.BillingModeProvenance, provenanceId)
            && ProvenanceMatches(estimate.Source.PricingRouteProvenance, provenanceId);
    }

    private static bool ProvenanceMatches(PricingValueProvenance value, string eventId) =>
        value.SourceAdapter == "local-monitor-cost-configuration"
        && value.SourceVersionOrSchemaFingerprint == "cost.configuration.v1"
        && value.SourceEventOrTraceSpanId == eventId
        && value.CaptureContentState == "not_captured"
        && value.NormalizationVersion == "cost-configuration-provenance.v1";

    private static bool IsPartitionShape(string state, int count, string? surface, string? version) =>
        state switch
        {
            "resolved" => count is >= 1 and <= 256
                && IsLowerToken(surface)
                && IsSafeVersion(version),
            "missing" or "incomplete" or "mixed" => count is >= 0 and <= 257
                && surface is null
                && version is null,
            _ => false,
        };

    private static bool IsTimestamp(string value) =>
        value.Length == 33
        && value.EndsWith("+00:00", StringComparison.Ordinal)
        && DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
        && parsed.Offset == TimeSpan.Zero
        && Format(parsed) == value;

    private static bool IsCanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && parsed.ToString("D") == value;

    private static bool IsCanonicalUuidV7(string value) =>
        IsCanonicalUuid(value)
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerToken(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    private static bool IsSafeVersion(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(character => character is >= '!' and <= '~');

    private static bool IsLowerSha(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrefixedSha(string? value, string prefix) =>
        value is not null
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && IsLowerSha(value[prefix.Length..]);

    private static bool IsFailureCode(string? value) =>
        value is
            "source_adapter_failed"
            or "invalid_estimate_source"
            or "pricing_estimation_failed"
            or "budget_payload_too_large"
            or "stale_recalculation_input"
            or "stale_active_estimate"
            or "pricing_store_failed"
            or "alert_evaluation_failed"
            or "alert_store_failed"
            or "recalculation_interrupted";

    private static bool IsSuppressionCode(string? value) =>
        value is
            "rule_disabled"
            or "no_eligible_sessions"
            or "eligible_set_incomplete"
            or "no_covered_estimate"
            or "aggregate_amount_not_representable"
            or "insufficient_estimate_coverage";

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long ScalarInt64(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = Command(connection, transaction, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool HasRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = Command(connection, transaction, sql);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

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

    private sealed record EventRow(
        int Sequence,
        string Kind,
        string OccurredAtUtc,
        string? FailurePhase,
        string? FailureOrdinalKind,
        int? FailureOrdinal,
        string? FailureCode);

    private sealed record ResultRow(
        string Kind,
        string? EstimateStatus,
        string? EstimateId,
        string? ResultCode);

    private sealed record BudgetRow(
        string ScopeKind,
        string ScopeId,
        string? ScopeStartUtc,
        string? ScopeEndUtc,
        string RuleId,
        string RuleVersion,
        string EvaluationId,
        string OutcomeKind,
        string? AlertId,
        int? SuppressionOrdinal,
        string? SuppressionCode);
}

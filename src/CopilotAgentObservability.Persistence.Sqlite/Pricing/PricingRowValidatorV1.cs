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

    private static bool ValidateCore(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!PricingSchemaV1.IsValid(connection, transaction)
            || ScalarInt64(connection, transaction, "SELECT COUNT(*) FROM pricing_configuration_previews;") > 32
            || HasRows(connection, transaction, "PRAGMA foreign_key_check;"))
            return false;

        var catalogs = ReadCatalogs(connection, transaction);
        if (catalogs is null) return false;
        if (!ValidatePreviews(connection, transaction)) return false;
        var configurations = ReadConfigurations(connection, transaction, catalogs);
        if (configurations is null) return false;
        var configurationHeads = ReadConfigurationHeads(connection, transaction, configurations);
        if (configurationHeads is null
            || configurationHeads.Count != configurations.Count
            || !ValidateConfigurationCommits(connection, transaction, configurations, configurationHeads))
            return false;
        var runs = ReadRuns(connection, transaction, configurations, configurationHeads);
        if (runs is null) return false;
        var targets = ReadTargets(connection, transaction, runs);
        if (targets is null) return false;
        var events = ReadEvents(connection, transaction, runs);
        if (events is null) return false;
        var estimates = ReadEstimates(connection, transaction, catalogs, configurations, runs, targets);
        if (estimates is null) return false;
        var results = ReadTargetResults(connection, transaction, targets, estimates);
        if (results is null) return false;
        var attempts = ReadAttempts(connection, transaction, targets, results);
        if (attempts is null) return false;
        var estimateHeads = ReadEstimateHeads(connection, transaction, estimates);
        if (estimateHeads is null
            || !ValidateTargetBasesAndEstimateHeads(targets, attempts, estimates, estimateHeads))
            return false;
        var budgetResults = ReadBudgetResults(connection, transaction, runs);
        return budgetResults is not null
            && ValidateRunStates(runs, targets, events, results, attempts, estimates, estimateHeads, budgetResults);
    }

    private static Dictionary<string, PricingCatalog>? ReadCatalogs(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var values = new Dictionary<string, PricingCatalog>(StringComparer.Ordinal);
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
                return null;
            }
            if (!IsLowerSha(sha)
                || reader.GetString(1) != PricingContractVersions.CatalogSnapshot
                || bytes.Length is < 1 or > 4_194_304
                || catalog.CatalogSha256 != sha
                || catalog.Documents.Count != reader.GetInt32(3)
                || !SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(sha))
                || !IsTimestamp(reader.GetString(4))
                || !values.TryAdd(sha, catalog))
                return null;
        }
        return values;
    }

    private static bool ValidatePreviews(SqliteConnection connection, SqliteTransaction? transaction)
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

    private static Dictionary<string, CostConfigurationV1>? ReadConfigurations(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, PricingCatalog> catalogs)
    {
        var values = new Dictionary<string, CostConfigurationV1>(StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT configuration_id,predecessor_configuration_id,schema_version,catalog_sha256,
                   canonical_sha256,canonical_blob,created_at_utc,source_count,budget_count
            FROM pricing_configurations
            ORDER BY configuration_id;
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
                || !catalogs.ContainsKey(value.CatalogSha256)
                || Sha256(bytes) != reader.GetString(4)
                || Format(value.CreatedAtUtc) != reader.GetString(6)
                || value.SourceEntries.Count != reader.GetInt32(7)
                || value.BudgetEntries.Count != reader.GetInt32(8)
                || !values.TryAdd(value.ConfigurationId, value))
                return null;
        }
        return values;
    }

    private static Dictionary<long, ConfigurationHeadRow>? ReadConfigurationHeads(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, CostConfigurationV1> configurations)
    {
        var values = new Dictionary<long, ConfigurationHeadRow>();
        using var command = Command(
            connection,
            transaction,
            """
            SELECT head_revision,configuration_id,previous_head_revision,
                   previous_configuration_id,committed_at_utc
            FROM pricing_configuration_heads
            ORDER BY head_revision;
            """);
        using var reader = command.ExecuteReader();
        long expectedRevision = 1;
        string? previousConfigurationId = null;
        while (reader.Read())
        {
            var revision = reader.GetInt64(0);
            var configurationId = reader.GetString(1);
            var previousRevision = NullableInt64(reader, 2);
            var storedPreviousConfiguration = NullableString(reader, 3);
            if (revision != expectedRevision
                || !configurations.TryGetValue(configurationId, out var configuration)
                || previousRevision != (revision == 1 ? null : revision - 1)
                || storedPreviousConfiguration != previousConfigurationId
                || configuration.PredecessorConfigurationId != previousConfigurationId
                || !IsTimestamp(reader.GetString(4)))
                return null;
            values.Add(
                revision,
                new(revision, configurationId, previousRevision, storedPreviousConfiguration));
            previousConfigurationId = configurationId;
            expectedRevision++;
        }
        return values;
    }

    private static bool ValidateConfigurationCommits(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, CostConfigurationV1> configurations,
        IReadOnlyDictionary<long, ConfigurationHeadRow> heads)
    {
        var count = 0;
        using var command = Command(
            connection,
            transaction,
            """
            SELECT head_revision,configuration_id,preview_digest,request_sha256,
                   canonical_request_blob,canonical_result_blob
            FROM pricing_configuration_commits
            ORDER BY head_revision;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            count++;
            var revision = reader.GetInt64(0);
            var configurationId = reader.GetString(1);
            var requestBytes = (byte[])reader[4];
            var resultBytes = (byte[])reader[5];
            var request = CostConfigurationCommitConsumerV1.ConsumeRequest(requestBytes);
            var result = CostConfigurationCommitConsumerV1.ConsumeResult(resultBytes);
            if (!heads.TryGetValue(revision, out var head)
                || head.ConfigurationId != configurationId
                || !configurations.TryGetValue(configurationId, out var configuration)
                || request.Status != CostConsumerStatus.Success
                || request.Value is not { } preview
                || result.Status != CostConsumerStatus.Success
                || result.Value is not { } committed
                || !CostConfigurationCanonicalJsonV1.Serialize(preview.Configuration)
                    .AsSpan()
                    .SequenceEqual(CostConfigurationCanonicalJsonV1.Serialize(configuration))
                || preview.ExpectedHeadRevision != revision - 1
                || preview.ExpectedConfigurationId != head.PreviousConfigurationId
                || preview.PreviewDigest != reader.GetString(2)
                || Sha256(requestBytes) != reader.GetString(3)
                || committed.ConfigurationId != configurationId
                || committed.HeadRevision != revision
                || committed.CatalogSha256 != configuration.CatalogSha256)
                return false;
        }
        return count == heads.Count;
    }

    private static Dictionary<string, RunRow>? ReadRuns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, CostConfigurationV1> configurations,
        IReadOnlyDictionary<long, ConfigurationHeadRow> configurationHeads)
    {
        var values = new Dictionary<string, RunRow>(StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id,request_schema_version,idempotency_key,request_digest,
                   canonical_request_blob,configuration_id,configuration_head_revision,
                   catalog_sha256,calculation_time_utc,target_count,scope_count,created_at_utc
            FROM pricing_recalculation_runs
            ORDER BY calculation_time_utc,run_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            var bytes = (byte[])reader[4];
            var consumed = CostRecalculationRequestCanonicalJsonV1.Consume(bytes);
            var configurationId = reader.GetString(5);
            var headRevision = reader.GetInt64(6);
            if (!IsCanonicalUuidV7(runId)
                || reader.GetString(1) != CostRecalculationRequestCanonicalJsonV1.SchemaVersion
                || consumed.Status != CostConsumerStatus.Success
                || consumed.Value is not { } request
                || request.IdempotencyKey != reader.GetString(2)
                || CostIdentityV1.Hash("cost-recalculation-request/v1", bytes) != reader.GetString(3)
                || request.ConfigurationId != configurationId
                || request.ExpectedHeadRevision != headRevision
                || request.CatalogSha256 != reader.GetString(7)
                || !configurations.TryGetValue(configurationId, out var configuration)
                || configuration.CatalogSha256 != request.CatalogSha256
                || !configurationHeads.TryGetValue(headRevision, out var head)
                || head.ConfigurationId != configurationId
                || request.SessionIds.Count != reader.GetInt32(9)
                || request.BudgetScopes.Count != reader.GetInt32(10)
                || !IsTimestamp(reader.GetString(8))
                || reader.GetString(8) != reader.GetString(11)
                || !values.TryAdd(
                    runId,
                    new(
                        runId,
                        request,
                        reader.GetString(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10))))
                return null;
        }
        return values;
    }

    private static Dictionary<(string RunId, int Ordinal), TargetRow>? ReadTargets(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, RunRow> runs)
    {
        var values = new Dictionary<(string RunId, int Ordinal), TargetRow>();
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id,target_ordinal,session_id,session_status,session_effective_at_utc,
                   session_updated_at_utc,source_partition_state,source_partition_count,
                   source_partition_digest,source_surface,source_application_version,
                   base_head_revision,base_estimate_id,base_attempt_revision
            FROM pricing_recalculation_targets
            ORDER BY run_id,target_ordinal;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            var ordinal = reader.GetInt32(1);
            var sessionId = reader.GetString(2);
            var state = reader.GetString(6);
            var count = reader.GetInt32(7);
            var surface = NullableString(reader, 9);
            var version = NullableString(reader, 10);
            var baseHead = NullableInt64(reader, 11);
            var baseEstimate = NullableString(reader, 12);
            if (!runs.TryGetValue(runId, out var run)
                || ordinal < 0
                || ordinal >= run.TargetCount
                || run.Request.SessionIds[ordinal] != sessionId
                || !IsCanonicalUuid(sessionId)
                || reader.GetString(3) is not ("completed" or "failed")
                || !IsTimestamp(reader.GetString(4))
                || !IsTimestamp(reader.GetString(5))
                || !IsPartitionShape(state, count, surface, version)
                || !IsLowerSha(reader.GetString(8))
                || (baseHead is null) != (baseEstimate is null)
                || baseHead is < 1
                || baseEstimate is not null && !IsPrefixedSha(baseEstimate, "pricing-estimate-")
                || reader.GetInt64(13) < 0
                || !values.TryAdd(
                    (runId, ordinal),
                    new(
                        runId,
                        ordinal,
                        sessionId,
                        reader.GetString(4),
                        baseHead,
                        baseEstimate,
                        reader.GetInt64(13))))
                return null;
        }
        return runs.Values.All(run => values.Keys.Count(key => key.RunId == run.RunId) == run.TargetCount)
            ? values
            : null;
    }

    private static Dictionary<string, IReadOnlyList<EventRow>>? ReadEvents(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, RunRow> runs)
    {
        var mutable = runs.Keys.ToDictionary(key => key, _ => new List<EventRow>(), StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id,event_sequence,event_kind,occurred_at_utc,failure_phase,
                   failure_ordinal_kind,failure_ordinal,failure_code
            FROM pricing_recalculation_events
            ORDER BY run_id,event_sequence;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            if (!mutable.TryGetValue(runId, out var events)
                || !runs.TryGetValue(runId, out var run))
                return null;
            var value = new EventRow(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                NullableInt32(reader, 6),
                NullableString(reader, 7));
            if (value.Sequence != events.Count
                || !IsTimestamp(value.OccurredAtUtc)
                || events.Count > 0
                    && string.CompareOrdinal(value.OccurredAtUtc, events[^1].OccurredAtUtc) < 0
                || value.FailureOrdinalKind == "target"
                    && value.FailureOrdinal is { } targetFailureOrdinal
                    && targetFailureOrdinal >= run.TargetCount
                || value.FailureOrdinalKind == "scope"
                    && value.FailureOrdinal is { } scopeFailureOrdinal
                    && scopeFailureOrdinal >= run.ScopeCount
                || !IsEventShapeValid(value))
                return null;
            events.Add(value);
        }
        foreach (var run in runs.Values)
        {
            var values = mutable[run.RunId];
            if (values.Count is < 1 or > 3
                || values[0].Kind != "requested"
                || values[0].OccurredAtUtc != run.CalculationTimeUtc
                || !IsEventSequenceValid(values))
                return null;
        }
        return mutable.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<EventRow>)item.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, EstimateRow>? ReadEstimates(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, PricingCatalog> catalogs,
        IReadOnlyDictionary<string, CostConfigurationV1> configurations,
        IReadOnlyDictionary<string, RunRow> runs,
        IReadOnlyDictionary<(string RunId, int Ordinal), TargetRow> targets)
    {
        var values = new Dictionary<string, EstimateRow>(StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT estimate_id,supersedes_estimate_id,schema_version,session_id,catalog_sha256,
                   configuration_id,source_entry_ordinal,run_id,target_ordinal,calculation_time_utc,
                   session_effective_at_utc,status,source_surface,source_application_version,
                   provider,model,billing_mode,pricing_route,registry_version,
                   registry_source_kind,currency,amount_text,canonical_sha256,canonical_blob
            FROM pricing_estimates
            ORDER BY estimate_id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader[23];
            var catalogSha = reader.GetString(4);
            if (!catalogs.TryGetValue(catalogSha, out var catalog)) return null;
            PricingEstimateRecord estimate;
            try
            {
                estimate = PricingEstimateConsumer.Deserialize(bytes, catalog);
            }
            catch (PricingEstimateValidationException)
            {
                return null;
            }
            var runId = reader.GetString(7);
            var targetOrdinal = reader.GetInt32(8);
            var configurationId = reader.GetString(5);
            var sourceOrdinal = reader.GetInt32(6);
            if (reader.GetString(2) != PricingContractVersions.Estimate
                || !runs.TryGetValue(runId, out var run)
                || !targets.TryGetValue((runId, targetOrdinal), out var target)
                || !configurations.TryGetValue(configurationId, out var configuration)
                || sourceOrdinal < 0
                || sourceOrdinal >= configuration.SourceEntries.Count
                || run.Request.ConfigurationId != configurationId
                || run.Request.CatalogSha256 != catalogSha
                || estimate.EstimateId != reader.GetString(0)
                || estimate.SupersedesEstimateId != NullableString(reader, 1)
                || estimate.Source.SessionId != reader.GetString(3)
                || target.SessionId != estimate.Source.SessionId
                || estimate.CatalogSha256 != catalogSha
                || Format(estimate.CalculationTimeUtc) != reader.GetString(9)
                || run.CalculationTimeUtc != reader.GetString(9)
                || Format(estimate.Source.SessionObservedAtUtc) != reader.GetString(10)
                || target.SessionEffectiveAtUtc != reader.GetString(10)
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
                || Sha256(bytes) != reader.GetString(22)
                || !ConfigurationSourceMatches(configuration, sourceOrdinal, estimate)
                || !values.TryAdd(
                    estimate.EstimateId,
                    new(
                        estimate,
                        runId,
                        targetOrdinal,
                        sourceOrdinal,
                        configurationId)))
                return null;
        }
        return values;
    }

    private static Dictionary<(string RunId, int Ordinal), ResultRow>? ReadTargetResults(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<(string RunId, int Ordinal), TargetRow> targets,
        IReadOnlyDictionary<string, EstimateRow> estimates)
    {
        var values = new Dictionary<(string RunId, int Ordinal), ResultRow>();
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id,target_ordinal,result_kind,estimate_status,estimate_id,result_code
            FROM pricing_recalculation_target_results
            ORDER BY run_id,target_ordinal;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            var ordinal = reader.GetInt32(1);
            var value = new ResultRow(
                reader.GetString(2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                NullableString(reader, 5));
            if (!targets.ContainsKey((runId, ordinal))
                || !IsResultShapeValid(value)
                || value.Kind == "estimate"
                    && (!estimates.TryGetValue(value.EstimateId!, out var estimate)
                        || estimate.RunId != runId
                        || estimate.TargetOrdinal != ordinal
                        || estimate.Estimate.Status != value.EstimateStatus)
                || !values.TryAdd((runId, ordinal), value))
                return null;
        }
        return values;
    }

    private static Dictionary<(string SessionId, long Revision), AttemptRow>? ReadAttempts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<(string RunId, int Ordinal), TargetRow> targets,
        IReadOnlyDictionary<(string RunId, int Ordinal), ResultRow> results)
    {
        var values = new Dictionary<(string SessionId, long Revision), AttemptRow>();
        var expectedBySession = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT session_id,attempt_revision,run_id,target_ordinal,result_kind,
                   estimate_status,estimate_id,result_code
            FROM pricing_session_attempts
            ORDER BY session_id,attempt_revision;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var revision = reader.GetInt64(1);
            var runId = reader.GetString(2);
            var ordinal = reader.GetInt32(3);
            var result = new ResultRow(
                reader.GetString(4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7));
            var expected = expectedBySession.GetValueOrDefault(sessionId) + 1;
            if (revision != expected
                || !targets.TryGetValue((runId, ordinal), out var target)
                || target.SessionId != sessionId
                || !results.TryGetValue((runId, ordinal), out var targetResult)
                || targetResult != result
                || target.BaseAttemptRevision + 1 != revision
                || !values.TryAdd(
                    (sessionId, revision),
                    new(sessionId, revision, runId, ordinal, result)))
                return null;
            expectedBySession[sessionId] = revision;
        }
        return values;
    }

    private static Dictionary<(string SessionId, long Revision), EstimateHeadRow>? ReadEstimateHeads(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, EstimateRow> estimates)
    {
        var values = new Dictionary<(string SessionId, long Revision), EstimateHeadRow>();
        var expectedBySession = new Dictionary<string, long>(StringComparer.Ordinal);
        var previousBySession = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT session_id,head_revision,estimate_id,previous_head_revision,previous_estimate_id
            FROM pricing_estimate_heads
            ORDER BY session_id,head_revision;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var revision = reader.GetInt64(1);
            var estimateId = reader.GetString(2);
            var previousRevision = NullableInt64(reader, 3);
            var previousEstimate = NullableString(reader, 4);
            var expected = expectedBySession.GetValueOrDefault(sessionId) + 1;
            var expectedPrevious = previousBySession.GetValueOrDefault(sessionId);
            if (revision != expected
                || !estimates.TryGetValue(estimateId, out var estimate)
                || estimate.Estimate.Source.SessionId != sessionId
                || previousRevision != (revision == 1 ? null : revision - 1)
                || previousEstimate != expectedPrevious
                || estimate.Estimate.SupersedesEstimateId != previousEstimate
                || !values.TryAdd(
                    (sessionId, revision),
                    new(sessionId, revision, estimateId, previousRevision, previousEstimate)))
                return null;
            expectedBySession[sessionId] = revision;
            previousBySession[sessionId] = estimateId;
        }
        return values;
    }

    private static bool ValidateTargetBasesAndEstimateHeads(
        IReadOnlyDictionary<(string RunId, int Ordinal), TargetRow> targets,
        IReadOnlyDictionary<(string SessionId, long Revision), AttemptRow> attempts,
        IReadOnlyDictionary<string, EstimateRow> estimates,
        IReadOnlyDictionary<(string SessionId, long Revision), EstimateHeadRow> heads)
    {
        foreach (var target in targets.Values)
        {
            if (target.BaseHeadRevision is { } baseRevision
                && (!heads.TryGetValue((target.SessionId, baseRevision), out var baseHead)
                    || baseHead.EstimateId != target.BaseEstimateId))
                return false;
            if (target.BaseAttemptRevision > 0
                && !attempts.ContainsKey((target.SessionId, target.BaseAttemptRevision)))
                return false;
        }
        foreach (var estimate in estimates.Values)
        {
            var target = targets[(estimate.RunId, estimate.TargetOrdinal)];
            var revision = (target.BaseHeadRevision ?? 0) + 1;
            if (!heads.TryGetValue((target.SessionId, revision), out var head)
                || head.EstimateId != estimate.Estimate.EstimateId)
                return false;
        }
        return true;
    }

    private static Dictionary<(string RunId, int Ordinal), BudgetRow>? ReadBudgetResults(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, RunRow> runs)
    {
        var values = new Dictionary<(string RunId, int Ordinal), BudgetRow>();
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id,scope_ordinal,scope_kind,scope_id,scope_start_utc,scope_end_utc,
                   rule_id,rule_version,evaluation_id,outcome_kind,alert_id,
                   suppression_ordinal,suppression_code
            FROM pricing_recalculation_budget_results
            ORDER BY run_id,scope_ordinal;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var runId = reader.GetString(0);
            var ordinal = reader.GetInt32(1);
            var value = new BudgetRow(
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                NullableString(reader, 10),
                NullableInt32(reader, 11),
                NullableString(reader, 12));
            if (!runs.TryGetValue(runId, out var run)
                || ordinal < 0
                || ordinal >= run.ScopeCount
                || !IsBudgetShapeValid(value, run.Request.BudgetScopes[ordinal])
                || !BudgetParentsAreValid(connection, transaction, value)
                || !values.TryAdd((runId, ordinal), value))
                return null;
        }
        return values;
    }

    private static bool ValidateRunStates(
        IReadOnlyDictionary<string, RunRow> runs,
        IReadOnlyDictionary<(string RunId, int Ordinal), TargetRow> targets,
        IReadOnlyDictionary<string, IReadOnlyList<EventRow>> events,
        IReadOnlyDictionary<(string RunId, int Ordinal), ResultRow> results,
        IReadOnlyDictionary<(string SessionId, long Revision), AttemptRow> attempts,
        IReadOnlyDictionary<string, EstimateRow> estimates,
        IReadOnlyDictionary<(string SessionId, long Revision), EstimateHeadRow> heads,
        IReadOnlyDictionary<(string RunId, int Ordinal), BudgetRow> budgets)
    {
        foreach (var run in runs.Values)
        {
            var runEvents = events[run.RunId];
            var terminal = runEvents[^1].Kind is "succeeded" or "failed";
            var runResults = results.Where(item => item.Key.RunId == run.RunId).ToArray();
            var runAttempts = attempts.Values.Where(item => item.RunId == run.RunId).ToArray();
            var runEstimates = estimates.Values.Where(item => item.RunId == run.RunId).ToArray();
            var runBudgets = budgets.Keys.Count(key => key.RunId == run.RunId);
            if (!terminal)
            {
                if (runResults.Length != 0
                    || runAttempts.Length != 0
                    || runEstimates.Length != 0
                    || runBudgets != 0)
                    return false;
                continue;
            }
            if (runResults.Length != run.TargetCount
                || runAttempts.Length != run.TargetCount
                || runResults.Any(item => !targets.ContainsKey(item.Key)))
                return false;
            if (runEvents[^1].Kind == "succeeded")
            {
                if (runResults.Any(item => item.Value.Kind == "failed")
                    || runBudgets != run.ScopeCount)
                    return false;
            }
            else
            {
                var failureCode = runEvents[^1].FailureCode;
                if (runEstimates.Length != 0
                    || runBudgets != 0
                    || runResults.All(item => item.Value.Kind != "failed")
                    || runResults.Where(item => item.Value.Kind == "failed")
                        .Any(item => item.Value.ResultCode != failureCode))
                    return false;
            }
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

    private static bool IsEventSequenceValid(IReadOnlyList<EventRow> values) =>
        values.Select(item => item.Kind).SequenceEqual(["requested"], StringComparer.Ordinal)
        || values.Select(item => item.Kind).SequenceEqual(["requested", "running"], StringComparer.Ordinal)
        || values.Select(item => item.Kind).SequenceEqual(["requested", "failed"], StringComparer.Ordinal)
            && values[1].FailurePhase == "recovery"
        || values.Select(item => item.Kind).SequenceEqual(["requested", "running", "succeeded"], StringComparer.Ordinal)
        || values.Select(item => item.Kind).SequenceEqual(["requested", "running", "failed"], StringComparer.Ordinal);

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

    private static bool BudgetParentsAreValid(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        BudgetRow value)
    {
        using (var evaluation = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM alert_evaluations WHERE evaluation_id=$id AND schema_version='alert.evaluation.v2';",
            ("$id", value.EvaluationId)))
            if (Convert.ToInt64(evaluation.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
        if (value.OutcomeKind == "receipt")
        {
            using var receipt = Command(
                connection,
                transaction,
                "SELECT COUNT(*) FROM alert_receipts WHERE alert_id=$alert AND evaluation_id=$evaluation;",
                ("$alert", value.AlertId!),
                ("$evaluation", value.EvaluationId));
            return Convert.ToInt64(receipt.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        if (value.OutcomeKind == "suppression")
        {
            using var suppression = Command(
                connection,
                transaction,
                "SELECT COUNT(*) FROM alert_suppressions WHERE evaluation_id=$evaluation AND suppression_ordinal=$ordinal;",
                ("$evaluation", value.EvaluationId),
                ("$ordinal", value.SuppressionOrdinal!.Value));
            return Convert.ToInt64(suppression.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        return true;
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
        IsCanonicalUuid(value) && value[14] == '7';

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
            or "scope_not_applicable"
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
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private sealed record ConfigurationHeadRow(
        long Revision,
        string ConfigurationId,
        long? PreviousRevision,
        string? PreviousConfigurationId);

    private sealed record RunRow(
        string RunId,
        CostRecalculationRequestV1 Request,
        string CalculationTimeUtc,
        int TargetCount,
        int ScopeCount);

    private sealed record TargetRow(
        string RunId,
        int Ordinal,
        string SessionId,
        string SessionEffectiveAtUtc,
        long? BaseHeadRevision,
        string? BaseEstimateId,
        long BaseAttemptRevision);

    private sealed record EventRow(
        int Sequence,
        string Kind,
        string OccurredAtUtc,
        string? FailurePhase,
        string? FailureOrdinalKind,
        int? FailureOrdinal,
        string? FailureCode);

    private sealed record EstimateRow(
        PricingEstimateRecord Estimate,
        string RunId,
        int TargetOrdinal,
        int SourceEntryOrdinal,
        string ConfigurationId);

    private sealed record ResultRow(
        string Kind,
        string? EstimateStatus,
        string? EstimateId,
        string? ResultCode);

    private sealed record AttemptRow(
        string SessionId,
        long Revision,
        string RunId,
        int TargetOrdinal,
        ResultRow Result);

    private sealed record EstimateHeadRow(
        string SessionId,
        long Revision,
        string EstimateId,
        long? PreviousRevision,
        string? PreviousEstimateId);

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

using System.Globalization;
using System.Security.Cryptography;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal sealed class SqliteCostRecalculationCoordinatorV1
{
    private readonly string databasePath;
    private readonly IPricingEstimateSourceAdapterV1 sourceAdapter;
    private readonly TimeProvider timeProvider;
    private readonly SqlitePricingStore store;
    private readonly SqliteCostRecalculationUnitOfWork unitOfWork;
    private readonly ISqliteAlertEngineTransactionParticipantV2? alertParticipant;

    internal SqliteCostRecalculationCoordinatorV1(
        string databasePath,
        IPricingEstimateSourceAdapterV1? sourceAdapter = null,
        TimeProvider? timeProvider = null,
        ISqliteAlertEngineTransactionParticipantV2? alertParticipant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.sourceAdapter = sourceAdapter
            ?? DefaultPricingEstimateSourceAdapterV1.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        store = new(databasePath, this.timeProvider);
        this.alertParticipant = alertParticipant;
        unitOfWork = alertParticipant is null
            ? new(databasePath, this.timeProvider)
            : new(databasePath, alertParticipant, this.timeProvider);
    }

    internal PricingStoreResult<string> Start(
        string runId,
        CostRecalculationRequestV1 request,
        ReadOnlyMemory<byte> currentProviderCatalogBytes,
        DateTimeOffset calculationTimeUtc) =>
        unitOfWork.Start(
            runId,
            request,
            currentProviderCatalogBytes,
            calculationTimeUtc);

    internal PricingCompletionResult Execute(string runId)
    {
        var running = store.MarkRecalculationRunning(runId);
        if (running.Status != PricingStoreStatus.Success)
            return new(PricingCompletionStatus.ContractRejected);

        CoordinatorRun run;
        try
        {
            run = ReadRun(runId);
        }
        catch (Exception)
        {
            return FailAll(
                runId,
                [],
                "pricing_store",
                0,
                "pricing_store_failed",
                preserveUnavailable: false);
        }

        var results = new PricingTargetCompletionWrite[run.Targets.Count];
        var failures = new List<PricingRunFailureWrite>();
        foreach (var target in run.Targets)
        {
            PricingEstimateSourceAdapterResultV1 adapterResult;
            try
            {
                adapterResult = sourceAdapter.Acquire(new(
                    target.SessionId,
                    target.SessionEffectiveAtUtc,
                    target.SourceSurface ?? string.Empty,
                    target.SourceApplicationVersion ?? string.Empty));
            }
            catch (Exception)
            {
                adapterResult = PricingEstimateSourceAdapterResultV1.Failed();
            }

            if (adapterResult.Status == PricingEstimateSourceAdapterStatusV1.Unavailable)
            {
                results[target.Ordinal] = PricingTargetCompletionWrite.Unavailable(
                    target.Ordinal,
                    adapterResult.ReasonCode!);
                continue;
            }
            if (adapterResult.Status == PricingEstimateSourceAdapterStatusV1.Failed)
            {
                results[target.Ordinal] = PricingTargetCompletionWrite.Failed(
                    target.Ordinal,
                    "source_adapter_failed");
                failures.Add(new("adapter", "target", target.Ordinal, "source_adapter_failed"));
                continue;
            }

            try
            {
                results[target.Ordinal] = Estimate(run, target, adapterResult.Facts!);
            }
            catch (PricingSourceMappingUnavailableException)
            {
                results[target.Ordinal] = PricingTargetCompletionWrite.Unavailable(
                    target.Ordinal,
                    "source_mapping_unavailable");
            }
            catch (Exception exception) when (
                exception is PricingEstimateValidationException
                    or PricingRegistryValidationException
                    or ArgumentException
                    or InvalidOperationException)
            {
                results[target.Ordinal] = PricingTargetCompletionWrite.Failed(
                    target.Ordinal,
                    "invalid_estimate_source");
                failures.Add(new(
                    "estimate_validation",
                    "target",
                    target.Ordinal,
                    "invalid_estimate_source"));
            }
            catch (Exception)
            {
                results[target.Ordinal] = PricingTargetCompletionWrite.Failed(
                    target.Ordinal,
                    "pricing_estimation_failed");
                failures.Add(new(
                    "estimate_validation",
                    "target",
                    target.Ordinal,
                    "pricing_estimation_failed"));
            }
        }

        var winner = SelectTargetFailure(failures);
        if (winner is not null)
            return unitOfWork.Fail(runId, results, winner);
        if (run.Request.BudgetScopes.Count == 0)
            return unitOfWork.Complete(runId, results, [], []);
        if (alertParticipant is null)
        {
            return unitOfWork.Fail(
                runId,
                results,
                new("alert_store", "scope", 0, "alert_store_failed"));
        }

        BudgetBuild preflight;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            preflight = BuildBudget(run, results, connection, transaction);
            transaction.Rollback();
        }
        catch (Exception)
        {
            return unitOfWork.Fail(
                runId,
                results,
                new("alert_evaluation", "scope", 0, "alert_evaluation_failed"));
        }
        if (preflight.CanonicalByteCount > 16_777_216)
        {
            return unitOfWork.Fail(
                runId,
                results,
                new("budget_payload", "scope", preflight.OverflowOrdinal, "budget_payload_too_large"));
        }

        var plan = new PricingBudgetEvaluationPlanV1(
            preflight.Candidate.Evaluations,
            preflight.Candidate.BudgetResults,
            (connection, transaction) =>
            {
                var rebuilt = BuildBudget(run, results, connection, transaction);
                if (!SnapshotBytesEqual(preflight.Snapshots, rebuilt.Snapshots))
                    throw new PricingRecalculationInputChangedException();
                return rebuilt.Candidate;
            });
        return unitOfWork.Complete(runId, results, plan);
    }

    internal static PricingRunFailureWrite? SelectTargetFailure(
        IEnumerable<PricingRunFailureWrite> failures) =>
        failures
            .OrderBy(item => FailurePhaseRank(item.FailurePhase))
            .ThenBy(item => item.FailureOrdinal)
            .ThenBy(item => FailureCodeRank(item.FailureCode))
            .FirstOrDefault();

    private BudgetBuild BuildBudget(
        CoordinatorRun run,
        IReadOnlyList<PricingTargetCompletionWrite> targetResults,
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var pending = PendingEstimates(run, targetResults);
        var pendingEvidence = pending.Values
            .OrderBy(item => item.TargetOrdinal)
            .Select(item => new StrictPendingPricingEvidenceV2(
                item.Estimate.EstimateId,
                item.Estimate.Source.SessionId,
                item.Estimate.CalculationTimeUtc,
                item.Estimate.CatalogSha256,
                Convert.ToHexString(SHA256.HashData(item.Bytes)).ToLowerInvariant(),
                run.RunId,
                item.TargetOrdinal))
            .ToArray();
        var resolver = new CostAlertEvidenceResolverV1();
        var engine = new AlertEvaluationEngine(new AlertRuleRegistryV2(), resolver);
        var alertConfiguration = AlertConfiguration(run);
        var evaluations = new List<AlertEvaluationResultV2>();
        var budgetResults = new List<PricingBudgetResultWrite>();
        var snapshots = new List<byte[]>();
        long canonicalBytes = 0;
        var overflowOrdinal = -1;
        for (var ordinal = 0; ordinal < run.Request.BudgetScopes.Count; ordinal++)
        {
            var requestedScope = run.Request.BudgetScopes[ordinal];
            var eligible = ReadEligibleMembers(
                run,
                targetResults,
                pending,
                requestedScope,
                connection,
                transaction,
                out _);
            var readView = new CostAlertEvidenceReadViewV1(
                eligible.ToDictionary(
                    item => item.Member.SessionId,
                    item => item.Member.SessionEffectiveAtUtc,
                    StringComparer.Ordinal),
                eligible
                    .Where(item => item.Member.EstimateId is not null)
                    .ToDictionary(
                        item => item.Member.EstimateId!,
                        item => (
                            item.Member.SessionId,
                            item.Member.EstimateCalculationTimeUtc!.Value),
                        StringComparer.Ordinal));
            var snapshot = CostBudgetSnapshotProducerV1.Create(
                requestedScope,
                eligible,
                new(
                    run.Configuration.ConfigurationId,
                    run.Request.ExpectedHeadRevision,
                    run.Configuration.CatalogSha256));
            var rule = Rule(requestedScope.ScopeKind);
            var evaluated = engine.Evaluate(
                rule,
                snapshot,
                alertConfiguration,
                new(readView, pendingEvidence));
            if (evaluated.Status != AlertEvaluationEngineStatusV2.Success
                || evaluated.Evaluation is null)
                throw new InvalidOperationException("Budget evaluation failed.");
            var evaluation = evaluated.Evaluation;
            var snapshotBytes = AlertCanonicalJsonV2.SerializeSnapshot(snapshot);
            canonicalBytes = checked(
                canonicalBytes
                + CanonicalCandidateByteCount(snapshot, evaluation));
            if (canonicalBytes > 16_777_216 && overflowOrdinal < 0)
                overflowOrdinal = ordinal;
            snapshots.Add(snapshotBytes);
            evaluations.Add(evaluation);
            budgetResults.Add(BudgetResult(
                ordinal,
                requestedScope,
                snapshot,
                evaluation));
        }
        return new(
            new(evaluations, budgetResults),
            snapshots,
            canonicalBytes,
            overflowOrdinal);
    }

    internal static long CanonicalCandidateByteCount(
        AlertNormalizedSnapshotV2 snapshot,
        AlertEvaluationResultV2 evaluation) =>
        checked(
            (long)AlertCanonicalJsonV2.SerializeSnapshot(snapshot).Length
            + AlertCanonicalJsonV2.SerializeEvaluation(evaluation).Length
            + evaluation.Receipts.Sum(receipt =>
                (long)AlertCanonicalJsonV2.SerializeReceipt(receipt).Length)
            + evaluation.Suppressions.Sum(suppression =>
                (long)AlertCanonicalJsonV2.SerializeSuppression(suppression).Length));

    private IReadOnlyList<CostBudgetEligibleMemberV1> ReadEligibleMembers(
        CoordinatorRun run,
        IReadOnlyList<PricingTargetCompletionWrite> targetResults,
        IReadOnlyDictionary<string, PendingEstimate> pending,
        CostBudgetScopeV1 requestedScope,
        SqliteConnection connection,
        SqliteTransaction transaction,
        out bool overflow)
    {
        var pendingByOrdinal = targetResults.ToDictionary(item => item.TargetOrdinal);
        var targetBySession = run.Targets.ToDictionary(
            item => item.SessionId,
            item => pendingByOrdinal[item.Ordinal],
            StringComparer.Ordinal);
        var values = new List<CostBudgetEligibleMemberV1>();
        using var command = EligibleSessionsCommand(
            connection,
            transaction,
            requestedScope);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var effective = Parse(reader.GetString(1));
            var updated = Parse(reader.GetString(2));
            var source = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                connection,
                transaction,
                sessionId);
            if (source.State != CostSessionSourcePartitionStateV1.Resolved
                || !run.Configuration.SourceEntries.Any(entry =>
                    entry.SourceSurface == source.SourceSurface
                    && entry.ApplicationVersion == source.SourceApplicationVersion))
                continue;
            AlertCostMemberV2 member;
            if (targetBySession.TryGetValue(sessionId, out var targetResult))
            {
                var target = run.Targets.Single(item => item.SessionId == sessionId);
                member = targetResult.ResultKind != "estimate"
                        && target.BaseHeadRevision is not null
                    ? ExistingMember(
                        run,
                        connection,
                        transaction,
                        sessionId,
                        effective,
                        updated,
                        source.SourceSurface!,
                        source.SourceApplicationVersion!,
                        target.BaseAttemptRevision + 1,
                        targetResult.ResultKind,
                        targetResult.ResultCode)
                    : PendingMember(
                        run,
                        sessionId,
                        effective,
                        updated,
                        source.SourceSurface!,
                        source.SourceApplicationVersion!,
                        targetResult,
                        pending);
            }
            else
            {
                member = ExistingMember(
                    run,
                    connection,
                    transaction,
                    sessionId,
                    effective,
                    updated,
                    source.SourceSurface!,
                    source.SourceApplicationVersion!);
            }
            values.Add(new(member, source.Digest));
            if (values.Count == 2_001)
            {
                overflow = true;
                return values;
            }
        }
        overflow = false;
        return values;
    }

    private static SqliteCommand EligibleSessionsCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CostBudgetScopeV1 scope)
    {
        const string select =
            """
            SELECT session_id,last_seen_at,updated_at,status
            FROM sessions
            WHERE status IN ('completed','failed')
            """;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        switch (scope.ScopeKind)
        {
            case "session":
                command.CommandText =
                    select + " AND session_id=$session ORDER BY last_seen_at,session_id;";
                command.Parameters.AddWithValue("$session", scope.SessionId!);
                break;
            case "utc_day":
                var day = UtcDay(scope.UtcDate!);
                command.CommandText =
                    select
                    + " AND last_seen_at >= $start AND last_seen_at < $end"
                    + " ORDER BY last_seen_at,session_id;";
                command.Parameters.AddWithValue("$start", Timestamp(day.Start));
                command.Parameters.AddWithValue("$end", Timestamp(day.End));
                break;
            case "rolling_period":
                command.CommandText =
                    select
                    + " AND last_seen_at >= $start AND last_seen_at < $end"
                    + " ORDER BY last_seen_at,session_id;";
                command.Parameters.AddWithValue(
                    "$start",
                    Timestamp(scope.CutoffUtc!.Value.AddDays(-scope.WindowDays!.Value)));
                command.Parameters.AddWithValue("$end", Timestamp(scope.CutoffUtc.Value));
                break;
            default:
                command.Dispose();
                throw new InvalidOperationException("Budget scope is invalid.");
        }
        return command;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) UtcDay(string utcDate)
    {
        var date = DateOnly.ParseExact(utcDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return (start, start.AddDays(1));
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture);

    private static AlertCostMemberV2 PendingMember(
        CoordinatorRun run,
        string sessionId,
        DateTimeOffset effective,
        DateTimeOffset updated,
        string sourceSurface,
        string sourceVersion,
        PricingTargetCompletionWrite result,
        IReadOnlyDictionary<string, PendingEstimate> pending)
    {
        var target = run.Targets.Single(item => item.SessionId == sessionId);
        if (result.ResultKind == "estimate")
        {
            var value = pending[sessionId].Estimate;
            return EstimateMember(
                sessionId,
                effective,
                updated,
                sourceSurface,
                sourceVersion,
                target.BaseAttemptRevision + 1,
                AlertCostAttemptResultKindV2.Estimate,
                null,
                (target.BaseHeadRevision ?? 0) + 1,
                value);
        }
        var unavailable = result.ResultKind == "unavailable";
        return new(
            sessionId,
            effective,
            updated,
            sourceSurface,
            sourceVersion,
            unavailable
                ? AlertCostMemberStateV2.Unavailable
                : AlertCostMemberStateV2.Failed,
            target.BaseAttemptRevision + 1,
            unavailable
                ? AlertCostAttemptResultKindV2.Unavailable
                : AlertCostAttemptResultKindV2.Failed,
            result.ResultCode,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private AlertCostMemberV2 ExistingMember(
        CoordinatorRun run,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset effective,
        DateTimeOffset updated,
        string sourceSurface,
        string sourceVersion,
        long? pendingAttemptRevision = null,
        string? pendingResultKind = null,
        string? pendingResultCode = null)
    {
        var attemptRevision = pendingAttemptRevision ?? 0;
        var resultKind = pendingResultKind;
        var resultCode = pendingResultCode;
        if (pendingAttemptRevision is null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT attempt_revision,result_kind,result_code
                FROM pricing_session_attempts
                WHERE session_id=$session
                ORDER BY attempt_revision DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                attemptRevision = reader.GetInt64(0);
                resultKind = reader.GetString(1);
                resultCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }
        if (attemptRevision == 0)
        {
            return new(
                sessionId, effective, updated, sourceSurface, sourceVersion,
                AlertCostMemberStateV2.Missing, 0, null, null,
                null, null, null, null, null, null, null, null, null, null);
        }
        using var estimateCommand = connection.CreateCommand();
        estimateCommand.Transaction = transaction;
        estimateCommand.CommandText =
            """
            SELECT h.head_revision,e.canonical_blob,c.canonical_blob
            FROM pricing_estimate_heads h
            JOIN pricing_estimates e ON e.estimate_id=h.estimate_id
            JOIN pricing_catalog_snapshots c ON c.catalog_sha256=e.catalog_sha256
            WHERE h.session_id=$session
            ORDER BY h.head_revision DESC LIMIT 1;
            """;
        estimateCommand.Parameters.AddWithValue("$session", sessionId);
        using var estimateReader = estimateCommand.ExecuteReader();
        if (!estimateReader.Read())
        {
            if (resultKind is "failed" or "unavailable")
            {
                return new(
                    sessionId, effective, updated, sourceSurface, sourceVersion,
                    resultKind == "failed"
                        ? AlertCostMemberStateV2.Failed
                        : AlertCostMemberStateV2.Unavailable,
                    attemptRevision,
                    AttemptKind(resultKind),
                    resultCode,
                    null, null, null, null, null, null, null, null, null, null);
            }
            throw new InvalidOperationException("Estimate attempt has no head.");
        }
        var headRevision = estimateReader.GetInt64(0);
        var catalog = PricingCatalogSnapshotConsumer.Deserialize((byte[])estimateReader[2]);
        var estimate = PricingEstimateConsumer.Deserialize((byte[])estimateReader[1], catalog);
        if (!SqlitePricingReadStore.IsEstimateFreshForBudget(
                connection,
                transaction,
                sessionId,
                estimate.EstimateId,
                run.Catalog))
        {
            return EstimateMember(
                sessionId, effective, updated, sourceSurface, sourceVersion,
                attemptRevision, AttemptKind(resultKind), resultCode, headRevision, estimate) with
            {
                State = AlertCostMemberStateV2.Stale,
                Amount = null,
                Currency = null,
            };
        }
        return EstimateMember(
            sessionId, effective, updated, sourceSurface, sourceVersion,
            attemptRevision, AttemptKind(resultKind), resultCode, headRevision, estimate);
    }

    private static AlertCostMemberV2 EstimateMember(
        string sessionId,
        DateTimeOffset effective,
        DateTimeOffset updated,
        string sourceSurface,
        string sourceVersion,
        long attemptRevision,
        AlertCostAttemptResultKindV2 attemptResultKind,
        string? attemptResultCode,
        long headRevision,
        PricingEstimateRecord estimate) =>
        new(
            sessionId,
            effective,
            updated,
            sourceSurface,
            sourceVersion,
            estimate.Status switch
            {
                "estimated" => AlertCostMemberStateV2.Estimated,
                "partial" => AlertCostMemberStateV2.Partial,
                "not-estimable" => AlertCostMemberStateV2.NotEstimable,
                _ => throw new InvalidOperationException("Estimate status is invalid."),
            },
            attemptRevision,
            attemptResultKind,
            attemptResultCode,
            headRevision,
            estimate.EstimateId,
            estimate.CalculationTimeUtc,
            estimate.CatalogSha256,
            estimate.Registry?.RegistryVersion,
            estimate.Source.Provider,
            estimate.Source.ModelId,
            estimate.Source.BillingMode,
            estimate.Amount,
            estimate.Currency);

    private static AlertCostAttemptResultKindV2 AttemptKind(string? resultKind) =>
        resultKind switch
        {
            "estimate" => AlertCostAttemptResultKindV2.Estimate,
            "unavailable" => AlertCostAttemptResultKindV2.Unavailable,
            "failed" => AlertCostAttemptResultKindV2.Failed,
            _ => throw new InvalidOperationException("Attempt result kind is invalid."),
        };

    private static IReadOnlyDictionary<string, PendingEstimate> PendingEstimates(
        CoordinatorRun run,
        IReadOnlyList<PricingTargetCompletionWrite> results)
    {
        var values = new Dictionary<string, PendingEstimate>(StringComparer.Ordinal);
        foreach (var result in results.Where(item => item.ResultKind == "estimate"))
        {
            var target = run.Targets.Single(item => item.Ordinal == result.TargetOrdinal);
            var estimate = PricingEstimateConsumer.Deserialize(
                result.CanonicalEstimateBytes.Span,
                run.Catalog);
            values.Add(
                target.SessionId,
                new(result.TargetOrdinal, estimate, result.CanonicalEstimateBytes.ToArray()));
        }
        return values;
    }

    private static AlertEngineConfigurationV2 AlertConfiguration(CoordinatorRun run) =>
        new(
            AlertContractVersionsV2.Configuration,
            "cost.configuration.v1",
            run.Configuration.ConfigurationId,
            run.Request.ExpectedHeadRevision,
            run.Configuration.CatalogSha256,
            run.Configuration.BudgetEntries.Select(entry => new AlertBudgetRuleConfigurationV2(
                entry.RuleId,
                entry.RuleVersion,
                entry.Enabled,
                entry.Currency,
                decimal.Parse(entry.WarningThreshold, CultureInfo.InvariantCulture),
                decimal.Parse(entry.CriticalThreshold, CultureInfo.InvariantCulture),
                entry.MinimumCoverageBasisPoints,
                entry.ScopeKind switch
                {
                    "session" => AlertCostScopeKindV2.Session,
                    "utc_day" => AlertCostScopeKindV2.UtcDay,
                    "rolling_period" => AlertCostScopeKindV2.RollingPeriod,
                    _ => throw new InvalidOperationException(),
                },
                entry.WindowDays)).ToArray());

    private static AlertRuleIdentityV2 Rule(string scopeKind) =>
        new(
            scopeKind switch
            {
                "session" => "session-estimated-cost-threshold",
                "utc_day" => "daily-estimated-cost-threshold",
                "rolling_period" => "period-estimated-cost-threshold",
                _ => throw new InvalidOperationException(),
            },
            "1");

    private static PricingBudgetResultWrite BudgetResult(
        int ordinal,
        CostBudgetScopeV1 requestedScope,
        AlertNormalizedSnapshotV2 snapshot,
        AlertEvaluationResultV2 evaluation)
    {
        var receipt = evaluation.Receipts.SingleOrDefault();
        var suppression = evaluation.Suppressions.SingleOrDefault();
        return new(
            ordinal,
            requestedScope.ScopeKind,
            snapshot.Scope.ScopeId,
            snapshot.EligibilityDigest,
            snapshot.Scope.SessionIds,
            snapshot.Scope.WindowStartUtc,
            snapshot.Scope.WindowEndUtc,
            evaluation.SelectedRuleId,
            evaluation.SelectedRuleVersion,
            evaluation.EvaluationId,
            receipt is not null ? "receipt" : suppression is not null ? "suppression" : "no_match",
            receipt?.AlertId,
            suppression is null ? null : 0,
            suppression?.Code);
    }

    private static bool SnapshotBytesEqual(
        IReadOnlyList<byte[]> expected,
        IReadOnlyList<byte[]> actual) =>
        expected.Count == actual.Count
        && expected.Zip(actual).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second));

    private PricingTargetCompletionWrite Estimate(
        CoordinatorRun run,
        CoordinatorTarget target,
        PricingEstimateSourceAdapterFactsV1 facts)
    {
        var source = facts.Source;
        if (target.SourceSurface is null
            || target.SourceApplicationVersion is null
            || facts.AdapterCapabilityVersion.Length is < 1 or > 64
            || source.SessionId != target.SessionId
            || source.SessionObservedAtUtc != target.SessionEffectiveAtUtc
            || source.SourceSurface != target.SourceSurface
            || source.SourceVersion != target.SourceApplicationVersion)
            throw new ArgumentException("Adapter source identity is invalid.", nameof(facts));

        var selectedOrdinal = SelectSourceEntry(
            run.Configuration,
            target.SourceSurface,
            target.SourceApplicationVersion,
            facts.AdapterCapabilityVersion,
            source.Provider);
        if (selectedOrdinal is null)
            throw new PricingSourceMappingUnavailableException();
        var selected = (
            entry: run.Configuration.SourceEntries[selectedOrdinal.Value],
            ordinal: selectedOrdinal.Value);
        var provenance = new PricingValueProvenance(
            "local-monitor-cost-configuration",
            "cost.configuration.v1",
            run.Configuration.ConfigurationId
                + ".source-entry-"
                + selected.ordinal.ToString("D3", CultureInfo.InvariantCulture),
            "not_captured",
            "cost-configuration-provenance.v1");
        var configuredSource = source with
        {
            BillingMode = selected.entry.BillingMode,
            PricingRoute = selected.entry.PricingRoute,
            BillingModeProvenance = provenance,
            PricingRouteProvenance = provenance,
        };
        var request = new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            run.CalculationTimeUtc,
            target.BaseEstimateId,
            configuredSource,
            facts.Usage);
        var estimate = new PricingEstimationEngine(run.Catalog).Estimate(request);
        var bytes = PricingCanonicalJson.Serialize(estimate);
        var reloaded = PricingEstimateConsumer.Deserialize(bytes, run.Catalog);
        if (reloaded.EstimateId != estimate.EstimateId
            || reloaded.CatalogSha256 != run.Catalog.CatalogSha256)
            throw new PricingEstimateValidationException("Estimate reload is inconsistent.");
        return PricingTargetCompletionWrite.Estimate(
            target.Ordinal,
            selected.ordinal,
            request,
            bytes);
    }

    internal static int? SelectSourceEntry(
        CostConfigurationV1 configuration,
        string sourceSurface,
        string sourceApplicationVersion,
        string adapterCapabilityVersion,
        string provider)
    {
        if (!IsPricingSafeToken(sourceSurface)
            || !IsPricingSafeToken(sourceApplicationVersion))
            return null;
        var matches = configuration.SourceEntries
            .Select((entry, ordinal) => (entry, ordinal))
            .Where(item =>
                item.entry.SourceSurface == sourceSurface
                && item.entry.ApplicationVersion == sourceApplicationVersion
                && item.entry.AdapterCapabilityVersion == adapterCapabilityVersion
                && item.entry.Provider == provider)
            .ToArray();
        return matches.Length == 1 ? matches[0].ordinal : null;
    }

    private static bool IsPricingSafeToken(string value) =>
        value.Length is >= 1 and <= 256
        && value is not ("." or "..")
        && IsAsciiAlphaNumeric(value[0])
        && value.All(character =>
            IsAsciiAlphaNumeric(character)
            || character is '.' or '_' or ':' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    private CoordinatorRun ReadRun(string runId)
    {
        using var connection = Open();
        CostRecalculationRequestV1 request;
        CostConfigurationV1 configuration;
        PricingCatalog catalog;
        DateTimeOffset calculationTimeUtc;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT r.canonical_request_blob,r.calculation_time_utc,
                       c.canonical_blob,s.canonical_blob
                FROM pricing_recalculation_runs r
                JOIN pricing_configurations c ON c.configuration_id=r.configuration_id
                JOIN pricing_catalog_snapshots s ON s.catalog_sha256=r.catalog_sha256
                WHERE r.run_id=$run;
                """;
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException();
            request = CostRecalculationRequestCanonicalJsonV1
                .Consume((byte[])reader[0]).Value
                ?? throw new InvalidOperationException();
            calculationTimeUtc = Parse(reader.GetString(1));
            configuration = CostConfigurationConsumerV1
                .Consume((byte[])reader[2]).Value
                ?? throw new InvalidOperationException();
            catalog = PricingCatalogSnapshotConsumer.Deserialize((byte[])reader[3]);
        }

        var targets = new List<CoordinatorTarget>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT target_ordinal,session_id,session_effective_at_utc,
                       source_surface,source_application_version,base_head_revision,
                       base_estimate_id,base_attempt_revision
                FROM pricing_recalculation_targets
                WHERE run_id=$run ORDER BY target_ordinal;
                """;
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                targets.Add(new(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7)));
        }
        return new(runId, request, configuration, catalog, calculationTimeUtc, targets);
    }

    private PricingCompletionResult FailAll(
        string runId,
        IReadOnlyList<CoordinatorTarget> targets,
        string phase,
        int ordinal,
        string code,
        bool preserveUnavailable)
    {
        var results = targets.Select(target =>
            PricingTargetCompletionWrite.Failed(target.Ordinal, code)).ToArray();
        if (results.Length == 0)
            return new(PricingCompletionStatus.PricingStoreFailed, code);
        return unitOfWork.Fail(
            runId,
            results,
            new(phase, phase == "alert_evaluation" ? "scope" : "target", ordinal, code),
            preserveUnavailable);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static DateTimeOffset Parse(string value)
    {
        var parsed = DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        if (parsed.Offset != TimeSpan.Zero)
            throw new FormatException("Cost timestamp is not UTC.");
        return parsed;
    }

    private static int FailurePhaseRank(string phase) => phase switch
    {
        "adapter" => 0,
        "estimate_validation" => 1,
        _ => int.MaxValue,
    };

    private static int FailureCodeRank(string code) => code switch
    {
        "source_adapter_failed" => 0,
        "invalid_estimate_source" => 0,
        "pricing_estimation_failed" => 1,
        _ => int.MaxValue,
    };

    private sealed record CoordinatorRun(
        string RunId,
        CostRecalculationRequestV1 Request,
        CostConfigurationV1 Configuration,
        PricingCatalog Catalog,
        DateTimeOffset CalculationTimeUtc,
        IReadOnlyList<CoordinatorTarget> Targets);

    private sealed record CoordinatorTarget(
        int Ordinal,
        string SessionId,
        DateTimeOffset SessionEffectiveAtUtc,
        string? SourceSurface,
        string? SourceApplicationVersion,
        long? BaseHeadRevision,
        string? BaseEstimateId,
        long BaseAttemptRevision);

    private sealed record PendingEstimate(
        int TargetOrdinal,
        PricingEstimateRecord Estimate,
        byte[] Bytes);

    private sealed record BudgetBuild(
        PricingBudgetTransactionCandidateV1 Candidate,
        IReadOnlyList<byte[]> Snapshots,
        long CanonicalByteCount,
        int OverflowOrdinal);

    private sealed class PricingSourceMappingUnavailableException : Exception;
}

internal sealed class CostAlertEvidenceReadViewV1(
    IReadOnlyDictionary<string, DateTimeOffset> sessions,
    IReadOnlyDictionary<string, (string SessionId, DateTimeOffset ObservedAtUtc)> estimates)
    : IAlertEvidenceReadViewV2
{
    internal IReadOnlyDictionary<string, DateTimeOffset> Sessions { get; } = sessions;
    internal IReadOnlyDictionary<string, (string SessionId, DateTimeOffset ObservedAtUtc)> Estimates { get; } = estimates;
}

internal sealed class CostAlertEvidenceResolverV1 : IAlertEvidenceResolverV2
{
    public AlertEvidenceResolutionStatusV2 Resolve(
        AlertEvidenceReferenceV2 reference,
        AlertEvidenceResolutionScopeV2 scope)
    {
        if (scope.ExistingEvidenceReadView is not CostAlertEvidenceReadViewV1 view)
            return AlertEvidenceResolutionStatusV2.ContractRejected;
        if (reference.Kind == AlertEvidenceKindV2.Session)
        {
            return view.Sessions.TryGetValue(reference.SessionId, out var observedAt)
                && reference.EvidenceId == reference.SessionId
                && reference.ObservedAtUtc == observedAt
                    ? AlertEvidenceResolutionStatusV2.Resolved
                    : AlertEvidenceResolutionStatusV2.Unresolved;
        }

        var pending = scope.PendingPricingEvidence.SingleOrDefault(item =>
            item.EstimateId == reference.EvidenceId);
        if (pending is not null)
        {
            return pending.SessionId == reference.SessionId
                && pending.CalculationTimeUtc == reference.ObservedAtUtc
                    ? AlertEvidenceResolutionStatusV2.Resolved
                    : AlertEvidenceResolutionStatusV2.ContractRejected;
        }
        return view.Estimates.TryGetValue(reference.EvidenceId, out var estimate)
            && estimate.SessionId == reference.SessionId
            && estimate.ObservedAtUtc == reference.ObservedAtUtc
                ? AlertEvidenceResolutionStatusV2.Resolved
                : AlertEvidenceResolutionStatusV2.Unresolved;
    }
}

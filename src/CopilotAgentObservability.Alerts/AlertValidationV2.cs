namespace CopilotAgentObservability.Alerts;

internal static class AlertValidationV2
{
    internal static readonly string[] RuleOrder =
    [
        "session-estimated-cost-threshold",
        "daily-estimated-cost-threshold",
        "period-estimated-cost-threshold",
    ];

    private static readonly HashSet<string> SuppressionCodes = new(
    [
        "rule_disabled",
        "scope_not_applicable",
        "no_eligible_sessions",
        "eligible_set_incomplete",
        "no_covered_estimate",
        "aggregate_amount_not_representable",
        "insufficient_estimate_coverage",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> UnavailableAttemptCodes = new(
    [
        "source_mapping_unavailable",
        "source_adapter_unavailable",
        "codex_adapter_unavailable",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> FailedAttemptCodes = new(
    [
        "source_adapter_failed",
        "invalid_estimate_source",
        "pricing_estimation_failed",
        "budget_payload_too_large",
        "stale_recalculation_input",
        "stale_active_estimate",
        "pricing_store_failed",
        "alert_evaluation_failed",
        "alert_store_failed",
        "recalculation_interrupted",
    ], StringComparer.Ordinal);

    public static void ValidateSnapshot(AlertNormalizedSnapshotV2 snapshot)
    {
        if (snapshot is null
            || snapshot.SchemaVersion != AlertContractVersionsV2.Snapshot
            || snapshot.ContextKind != "estimated_cost"
            || snapshot.SourceSurface != "local-monitor-cost-analytics"
            || snapshot.SourceVersion != "1"
            || snapshot.Scope is null
            || snapshot.AcquisitionReasons is null
            || snapshot.Members is null
            || snapshot.Evidence is null
            || snapshot.CompletenessReasons is null
            || !Hash(snapshot.EligibilityDigest)
            || snapshot.Members.Count > 2_000
            || snapshot.Evidence.Count > 4_000
            || !ValidScope(snapshot.Scope, snapshot.EligibilityDigest))
        {
            throw InvalidSnapshot();
        }

        if (snapshot.AcquisitionState == AlertCostAcquisitionStateV2.Incomplete)
        {
            if (snapshot.AcquisitionReasons.Count != 1
                || snapshot.AcquisitionReasons[0] != "eligible_set_incomplete"
                || snapshot.Completeness != AlertCostCompletenessV2.Partial
                || snapshot.CompletenessReasons.Count != 1
                || snapshot.CompletenessReasons[0] != "eligible_set_incomplete"
                || snapshot.EligibleCount is not null
                || snapshot.EligibleLowerBound != 2_001
                || snapshot.AggregateState != AlertCostAggregateStateV2.NotApplicable
                || snapshot.Currency is not null
                || snapshot.Amount is not null
                || AnyCountPresent(snapshot)
                || snapshot.Members.Count != 0
                || snapshot.Evidence.Count != 0
                || snapshot.FirstObservedAt is not null
                || snapshot.LastObservedAt is not null
                || snapshot.Scope.Kind == AlertCostScopeKindV2.Session)
            {
                throw InvalidSnapshot();
            }

            return;
        }

        if (snapshot.AcquisitionState != AlertCostAcquisitionStateV2.Complete
            || snapshot.AcquisitionReasons.Count != 0
            || snapshot.Completeness != AlertCostCompletenessV2.Full
            || snapshot.CompletenessReasons.Count != 0
            || snapshot.EligibleCount is null or < 0
            || snapshot.EligibleCount != snapshot.Members.Count
            || snapshot.EligibleLowerBound is not null
            || !AllCountsPresent(snapshot)
            || snapshot.CoverageNumerator != snapshot.EstimatedCount
            || snapshot.CoverageDenominator != snapshot.EligibleCount
            || SumStateCounts(snapshot) != snapshot.EligibleCount
            || !ValidCoverage(snapshot)
            || !ValidAggregate(snapshot))
        {
            throw InvalidSnapshot();
        }

        if (snapshot.Members.Count == 0)
        {
            if (snapshot.Scope.Kind == AlertCostScopeKindV2.Session
                || snapshot.Scope.SessionIds.Count != 0
                || snapshot.Evidence.Count != 0
                || snapshot.FirstObservedAt is not null
                || snapshot.LastObservedAt is not null)
            {
                throw InvalidSnapshot();
            }
            return;
        }

        var orderedMembers = snapshot.Members
            .OrderBy(item => item.SessionEffectiveAtUtc)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ToArray();
        if (!snapshot.Members.SequenceEqual(orderedMembers)
            || snapshot.FirstObservedAt != orderedMembers[0].SessionEffectiveAtUtc
            || snapshot.LastObservedAt != orderedMembers[^1].SessionEffectiveAtUtc
            || !snapshot.Scope.SessionIds.SequenceEqual(orderedMembers.Select(item => item.SessionId), StringComparer.Ordinal)
            || orderedMembers.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != orderedMembers.Length
            || orderedMembers
                .Where(item => item.EstimateId is not null)
                .Select(item => item.EstimateId)
                .Distinct(StringComparer.Ordinal)
                .Count() != orderedMembers.Count(item => item.EstimateId is not null)
            || orderedMembers.Any(item => !ValidMember(item)))
        {
            throw InvalidSnapshot();
        }

        var orderedEvidence = snapshot.Evidence
            .OrderBy(item => AlertWireV2.EvidenceRank(item.Kind))
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ThenBy(item => item.ObservedAtUtc)
            .ToArray();
        if (!snapshot.Evidence.SequenceEqual(orderedEvidence)
            || orderedEvidence.Distinct().Count() != orderedEvidence.Length
            || !EvidenceMatchesMembers(orderedMembers, orderedEvidence))
        {
            throw InvalidSnapshot();
        }
    }

    public static IReadOnlyDictionary<(string RuleId, string RuleVersion), AlertBudgetRuleConfigurationV2>
        ValidateConfiguration(AlertRuleRegistryV2 registry, AlertEngineConfigurationV2 configuration)
    {
        if (configuration is null
            || configuration.SchemaVersion != AlertContractVersionsV2.Configuration
            || configuration.ConfigurationVersion != "cost.configuration.v1"
            || !CostConfigurationId(configuration.SourceCostConfigurationId)
            || configuration.SourceConfigurationHeadRevision < 1
            || !Hash(configuration.SourceConfigurationCatalogSha256)
            || configuration.Rules is null
            || configuration.Rules.Count > 3
            || configuration.Rules.Any(item => item is null)
            || configuration.Rules.GroupBy(item => (item.RuleId, item.RuleVersion)).Any(group => group.Count() != 1))
        {
            throw InvalidConfiguration();
        }

        var descriptorIdentities = registry.Rules.Select(item => (item.Descriptor.RuleId, item.Descriptor.RuleVersion)).ToHashSet();
        if (configuration.Rules.Any(item => !descriptorIdentities.Contains((item.RuleId, item.RuleVersion)))
            || !configuration.Rules.Select(item => item.RuleId).SequenceEqual(
                configuration.Rules.Select(item => item.RuleId).OrderBy(item => Array.IndexOf(RuleOrder, item))))
        {
            throw InvalidConfiguration();
        }

        foreach (var rule in configuration.Rules)
        {
            if (rule.RuleVersion != "1"
                || rule.Currency != "USD"
                || rule.WarningThreshold < 0
                || rule.CriticalThreshold < 0
                || rule.WarningThreshold > rule.CriticalThreshold
                || rule.MinimumCoverageBasisPoints is < 0 or > 10_000
                || (rule.ScopeKind == AlertCostScopeKindV2.RollingPeriod
                    ? rule.WindowDays is < 2 or > 366
                    : rule.WindowDays is not null)
                || ExpectedScope(rule.RuleId) != rule.ScopeKind)
            {
                throw InvalidConfiguration();
            }
        }

        return configuration.Rules.ToDictionary(item => (item.RuleId, item.RuleVersion));
    }

    public static void ValidateEvaluation(AlertEvaluationResultV2 evaluation)
    {
        if (evaluation is null
            || evaluation.SchemaVersion != AlertContractVersionsV2.Evaluation
            || !Hash(evaluation.EvaluationId)
            || !Hash(evaluation.InputHash)
            || !Hash(evaluation.ConfigurationHash)
            || evaluation.EvaluationId != AlertHashing.Identifier(
                "alert-evaluation/v2",
                evaluation.InputHash,
                evaluation.ConfigurationHash,
                evaluation.SelectedRuleId,
                evaluation.SelectedRuleVersion)
            || evaluation.ConfigurationVersion != "cost.configuration.v1"
            || !RuleOrder.Contains(evaluation.SelectedRuleId, StringComparer.Ordinal)
            || evaluation.SelectedRuleVersion != "1"
            || !CostConfigurationId(evaluation.SourceCostConfigurationId)
            || evaluation.SourceConfigurationHeadRevision < 1
            || !Hash(evaluation.SourceConfigurationCatalogSha256)
            || !CostScopeId(evaluation.ScopeId)
            || !Hash(evaluation.EligibilityDigest)
            || evaluation.Receipts is null
            || evaluation.Suppressions is null
            || evaluation.RejectedMatches is null
            || evaluation.RejectedMatches.Count != 0
            || evaluation.Receipts.Count + evaluation.Suppressions.Count > 1
            || !ValidEvaluationContext(evaluation)
            || evaluation.Receipts.Any(receipt => !ValidReceipt(receipt, evaluation))
            || evaluation.Suppressions.Any(suppression => !ValidSuppression(suppression, evaluation)))
        {
            throw new AlertContractException("invalid_evaluation", "Alert evaluation is invalid.");
        }
    }

    public static void ValidateReceipt(AlertReceiptV2 receipt)
    {
        if (receipt is null
            || receipt.SchemaVersion != AlertContractVersionsV2.Receipt
            || receipt.SanitizedExportProfile != AlertContractVersionsV2.SanitizedReceiptProfile
            || !Hash(receipt.AlertId)
            || !Hash(receipt.EvaluationId)
            || !RuleOrder.Contains(receipt.RuleId, StringComparer.Ordinal)
            || receipt.RuleVersion != "1"
            || receipt.InitialState != AlertInitialState.Open
            || receipt.SourceSurface != "local-monitor-cost-analytics"
            || receipt.SourceVersion != "1"
            || receipt.Scope is null
            || !Hash(receipt.ConfigurationHash)
            || !Hash(receipt.InputHash)
            || !CostConfigurationId(receipt.SourceCostConfigurationId)
            || receipt.SourceConfigurationHeadRevision < 1
            || !Hash(receipt.SourceConfigurationCatalogSha256)
            || receipt.ConfigurationVersion != "cost.configuration.v1"
            || receipt.Currency != "USD"
            || receipt.AggregateState != AlertCostAggregateStateV2.Available
            || receipt.ObservedAmount < 0
            || receipt.WarningThreshold < 0
            || receipt.CriticalThreshold < receipt.WarningThreshold
            || receipt.EligibleCount <= 0
            || receipt.EstimatedCount <= 0
            || receipt.CoverageNumerator != receipt.EstimatedCount
            || receipt.CoverageDenominator != receipt.EligibleCount
            || receipt.CoverageBasisPoints is < 0 or > 10_000
            || receipt.FirstObservedAt > receipt.LastObservedAt
            || receipt.Completeness != AlertCostCompletenessV2.Full
            || receipt.CompletenessReasons is null
            || receipt.CompletenessReasons.Count != 0
            || receipt.Evidence is null
            || receipt.Members is null
            || receipt.Members.Count != receipt.EligibleCount
            || !ValidReceiptFacts(receipt)
            || receipt.AlertId != AlertReceiptIdentityV2.Create(receipt)
            || AlertCanonicalJsonV2.SerializeReceipt(receipt).Length > 8_388_608)
        {
            throw new AlertContractException("invalid_alert_receipt", "Alert receipt is invalid.");
        }
    }

    public static void ValidateSuppression(AlertSuppressionV2 suppression)
    {
        if (suppression is null
            || suppression.SchemaVersion != AlertContractVersionsV2.Suppression
            || !Hash(suppression.EvaluationId)
            || !RuleOrder.Contains(suppression.RuleId, StringComparer.Ordinal)
            || suppression.RuleVersion != "1"
            || !IsSuppressionCode(suppression.Code)
            || !CostConfigurationId(suppression.SourceCostConfigurationId)
            || suppression.SourceConfigurationHeadRevision < 1
            || !Hash(suppression.SourceConfigurationCatalogSha256)
            || suppression.ConfigurationVersion != "cost.configuration.v1"
            || !Hash(suppression.ConfigurationHash)
            || !CostScopeId(suppression.ScopeId)
            || !Hash(suppression.EligibilityDigest)
            || !ValidSuppressionContext(suppression))
        {
            throw new AlertContractException("invalid_alert_suppression", "Alert suppression is invalid.");
        }
    }

    public static bool IsSuppressionCode(string? value) =>
        value is not null && SuppressionCodes.Contains(value);

    public static AlertCostScopeKindV2 ExpectedScope(string ruleId) => ruleId switch
    {
        "session-estimated-cost-threshold" => AlertCostScopeKindV2.Session,
        "daily-estimated-cost-threshold" => AlertCostScopeKindV2.UtcDay,
        "period-estimated-cost-threshold" => AlertCostScopeKindV2.RollingPeriod,
        _ => throw new AlertContractException("invalid_rule_registry", "Alert rule registry is invalid."),
    };

    public static bool Hash(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool Token(string? value) => AlertValidation.IsToken(value);

    public static bool CostScopeId(string? value) =>
        value is { Length: 75 } && value.StartsWith("cost-scope-", StringComparison.Ordinal) && Hash(value[11..]);

    private static bool ValidScope(AlertCostScopeV2 scope, string eligibilityDigest)
    {
        if (!CostScopeId(scope.ScopeId)
            || scope.SessionIds is null
            || scope.SessionIds.Count > 2_000
            || scope.SessionIds.Distinct(StringComparer.Ordinal).Count() != scope.SessionIds.Count
            || scope.SessionIds.Any(sessionId => !SessionId(sessionId))
            || scope.ScopeId != AlertCostScopeIdentityV2.Create(
                scope.Kind,
                scope.WindowStartUtc,
                scope.WindowEndUtc,
                eligibilityDigest,
                scope.SessionIds))
        {
            return false;
        }

        if (scope.Kind == AlertCostScopeKindV2.Session)
        {
            return scope.WindowStartUtc is null
                && scope.WindowEndUtc is null
                && scope.SessionIds.Count == 1;
        }

        if (scope.WindowStartUtc is null
            || scope.WindowEndUtc is null
            || scope.WindowStartUtc >= scope.WindowEndUtc
            || !Midnight(scope.WindowStartUtc.Value)
            || !Midnight(scope.WindowEndUtc.Value))
        {
            return false;
        }

        var days = (scope.WindowEndUtc.Value - scope.WindowStartUtc.Value).TotalDays;
        return scope.Kind == AlertCostScopeKindV2.UtcDay
            ? days == 1
            : scope.Kind == AlertCostScopeKindV2.RollingPeriod && days is >= 2 and <= 366 && days == Math.Truncate(days);
    }

    private static bool ValidMember(AlertCostMemberV2 member)
    {
        if (!SessionId(member.SessionId)
            || !Token(member.SourceSurface)
            || !Token(member.SourceApplicationVersion)
            || member.SourceApplicationVersion.Length > 64
            || member.SessionUpdatedAtUtc < member.SessionEffectiveAtUtc
            || member.AttemptRevision < 0
            || member.AttemptResultCode is not null && !Token(member.AttemptResultCode)
            || member.EstimateId is not null && !OpaqueId(member.EstimateId)
            || member.CatalogSha256 is not null && !Hash(member.CatalogSha256)
            || member.RegistryVersion is not null && !Token(member.RegistryVersion)
            || member.Provider is not null && !Token(member.Provider)
            || member.Model is not null && !Token(member.Model)
            || member.BillingMode is not null && !Token(member.BillingMode)
            || member.Currency is not null && member.Currency != "USD")
        {
            return false;
        }

        var estimateIdentityComplete = member.HeadRevision is > 0
            && member.EstimateId is not null
            && member.EstimateCalculationTimeUtc is not null
            && member.CatalogSha256 is not null
            && member.Provider is not null
            && member.Model is not null
            && member.BillingMode is not null;
        var estimateIdentityAbsent = member.HeadRevision is null
            && member.EstimateId is null
            && member.EstimateCalculationTimeUtc is null
            && member.CatalogSha256 is null
            && member.RegistryVersion is null
            && member.Provider is null
            && member.Model is null
            && member.BillingMode is null;
        var validAttempt = ValidAttempt(member);

        return member.State switch
        {
            AlertCostMemberStateV2.Estimated =>
                member.AttemptRevision > 0
                && validAttempt
                && estimateIdentityComplete
                && member.RegistryVersion is not null
                && member.Amount is >= 0
                && member.Currency == "USD",
            AlertCostMemberStateV2.Partial =>
                member.AttemptRevision > 0
                && validAttempt
                && estimateIdentityComplete
                && member.RegistryVersion is not null
                && member.Amount is >= 0
                && member.Currency == "USD",
            AlertCostMemberStateV2.NotEstimable =>
                member.AttemptRevision > 0
                && validAttempt
                && estimateIdentityComplete
                && member.Amount is null
                && member.Currency is null,
            AlertCostMemberStateV2.Missing =>
                member.AttemptRevision == 0
                && member.AttemptResultKind is null
                && member.AttemptResultCode is null
                && estimateIdentityAbsent
                && member.Amount is null
                && member.Currency is null,
            AlertCostMemberStateV2.Failed =>
                member.AttemptRevision > 0
                && ValidFailedAttempt(member)
                && estimateIdentityAbsent
                && member.Amount is null
                && member.Currency is null,
            AlertCostMemberStateV2.Unavailable =>
                member.AttemptRevision > 0
                && ValidUnavailableAttempt(member)
                && estimateIdentityAbsent
                && member.Amount is null
                && member.Currency is null,
            AlertCostMemberStateV2.Stale =>
                member.AttemptRevision > 0
                && (estimateIdentityComplete && validAttempt
                    || estimateIdentityAbsent
                        && (ValidFailedAttempt(member) || ValidUnavailableAttempt(member)))
                && member.Amount is null
                && member.Currency is null,
            _ => false,
        };
    }

    private static bool ValidAttempt(AlertCostMemberV2 member) =>
        member.AttemptResultKind switch
        {
            AlertCostAttemptResultKindV2.Estimate => member.AttemptResultCode is null,
            AlertCostAttemptResultKindV2.Unavailable =>
                member.AttemptResultCode is not null
                && UnavailableAttemptCodes.Contains(member.AttemptResultCode),
            AlertCostAttemptResultKindV2.Failed =>
                member.AttemptResultCode is not null
                && FailedAttemptCodes.Contains(member.AttemptResultCode),
            _ => false,
        };

    private static bool ValidFailedAttempt(AlertCostMemberV2 member) =>
        member.AttemptResultKind == AlertCostAttemptResultKindV2.Failed
        && member.AttemptResultCode is not null
        && FailedAttemptCodes.Contains(member.AttemptResultCode);

    private static bool ValidUnavailableAttempt(AlertCostMemberV2 member) =>
        member.AttemptResultKind == AlertCostAttemptResultKindV2.Unavailable
        && member.AttemptResultCode is not null
        && UnavailableAttemptCodes.Contains(member.AttemptResultCode);

    private static bool EvidenceMatchesMembers(
        IReadOnlyList<AlertCostMemberV2> members,
        IReadOnlyList<AlertEvidenceReferenceV2> evidence)
    {
        foreach (var member in members)
        {
            var sessionEvidence = evidence.Where(item =>
                item.Kind == AlertEvidenceKindV2.Session
                && item.EvidenceId == member.SessionId
                && item.SessionId == member.SessionId
                && item.ObservedAtUtc == member.SessionEffectiveAtUtc);
            if (sessionEvidence.Count() != 1) return false;

            var estimateEvidence = evidence.Where(item =>
                item.Kind == AlertEvidenceKindV2.PricingEstimate
                && item.SessionId == member.SessionId);
            if (member.EstimateId is null)
            {
                if (estimateEvidence.Any()) return false;
            }
            else if (estimateEvidence.Count() != 1
                || estimateEvidence.Single().EvidenceId != member.EstimateId
                || estimateEvidence.Single().ObservedAtUtc != member.EstimateCalculationTimeUtc)
            {
                return false;
            }
        }

        return evidence.All(item =>
            SessionId(item.SessionId)
            && OpaqueId(item.EvidenceId)
            && members.Any(member => member.SessionId == item.SessionId));
    }

    private static bool ValidAggregate(AlertNormalizedSnapshotV2 snapshot)
    {
        if (snapshot.EstimatedCount == 0)
        {
            return snapshot.Currency is null
                && snapshot.Amount is null
                && snapshot.AggregateState == AlertCostAggregateStateV2.NotApplicable;
        }

        if (snapshot.Currency != "USD") return false;
        var amounts = snapshot.Members
            .Where(item => item.State == AlertCostMemberStateV2.Estimated)
            .Select(item => item.Amount!.Value);
        try
        {
            var expected = amounts.Aggregate(0m, checked((sum, amount) => sum + amount));
            return snapshot.AggregateState == AlertCostAggregateStateV2.Available
                && snapshot.Amount == expected;
        }
        catch (OverflowException)
        {
            return snapshot.AggregateState == AlertCostAggregateStateV2.Unrepresentable
                && snapshot.Amount is null;
        }
    }

    private static bool ValidCoverage(AlertNormalizedSnapshotV2 snapshot)
    {
        if (snapshot.CoverageDenominator == 0) return snapshot.CoverageBasisPoints is null;
        var expected = checked((int)(snapshot.CoverageNumerator!.Value * 10_000 / snapshot.CoverageDenominator!.Value));
        return snapshot.CoverageBasisPoints == expected;
    }

    private static bool AllCountsPresent(AlertNormalizedSnapshotV2 snapshot) =>
        snapshot.EstimatedCount is >= 0
        && snapshot.PartialCount is >= 0
        && snapshot.NotEstimableCount is >= 0
        && snapshot.MissingCount is >= 0
        && snapshot.FailedCount is >= 0
        && snapshot.UnavailableCount is >= 0
        && snapshot.StaleCount is >= 0
        && snapshot.CoverageNumerator is >= 0
        && snapshot.CoverageDenominator is >= 0;

    private static bool AnyCountPresent(AlertNormalizedSnapshotV2 snapshot) =>
        snapshot.EstimatedCount is not null
        || snapshot.PartialCount is not null
        || snapshot.NotEstimableCount is not null
        || snapshot.MissingCount is not null
        || snapshot.FailedCount is not null
        || snapshot.UnavailableCount is not null
        || snapshot.StaleCount is not null
        || snapshot.CoverageNumerator is not null
        || snapshot.CoverageDenominator is not null
        || snapshot.CoverageBasisPoints is not null;

    private static long SumStateCounts(AlertNormalizedSnapshotV2 snapshot) =>
        checked(
            snapshot.EstimatedCount!.Value
            + snapshot.PartialCount!.Value
            + snapshot.NotEstimableCount!.Value
            + snapshot.MissingCount!.Value
            + snapshot.FailedCount!.Value
            + snapshot.UnavailableCount!.Value
            + snapshot.StaleCount!.Value);

    private static bool ValidReceiptFacts(AlertReceiptV2 receipt)
    {
        if (!ValidReceiptScope(receipt.Scope)
            || receipt.Scope.Kind != ExpectedScope(receipt.RuleId)
            || receipt.Members.Count is 0 or > 2_000
            || receipt.Evidence.Count > 4_000
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Estimated) != receipt.EstimatedCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Partial) != receipt.PartialCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.NotEstimable) != receipt.NotEstimableCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Missing) != receipt.MissingCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Failed) != receipt.FailedCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Unavailable) != receipt.UnavailableCount
            || receipt.Members.Count(item => item.State == AlertCostMemberStateV2.Stale) != receipt.StaleCount
            || receipt.EstimatedCount
                + receipt.PartialCount
                + receipt.NotEstimableCount
                + receipt.MissingCount
                + receipt.FailedCount
                + receipt.UnavailableCount
                + receipt.StaleCount != receipt.EligibleCount
            || receipt.CoverageBasisPoints != checked((int)(
                receipt.CoverageNumerator * 10_000 / receipt.CoverageDenominator)))
        {
            return false;
        }

        var orderedMembers = receipt.Members
            .OrderBy(item => item.SessionEffectiveAtUtc)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ToArray();
        if (!receipt.Members.SequenceEqual(orderedMembers)
            || orderedMembers.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != orderedMembers.Length
            || orderedMembers
                .Where(item => item.EstimateId is not null)
                .Select(item => item.EstimateId)
                .Distinct(StringComparer.Ordinal)
                .Count() != orderedMembers.Count(item => item.EstimateId is not null)
            || orderedMembers.Any(item => !ValidMember(item))
            || receipt.FirstObservedAt != orderedMembers[0].SessionEffectiveAtUtc
            || receipt.LastObservedAt != orderedMembers[^1].SessionEffectiveAtUtc
            || !receipt.Scope.SessionIds.SequenceEqual(orderedMembers.Select(item => item.SessionId), StringComparer.Ordinal))
        {
            return false;
        }

        var orderedEvidence = receipt.Evidence
            .OrderBy(item => AlertWireV2.EvidenceRank(item.Kind))
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ThenBy(item => item.ObservedAtUtc)
            .ToArray();
        if (!receipt.Evidence.SequenceEqual(orderedEvidence)
            || orderedEvidence.Distinct().Count() != orderedEvidence.Length
            || !EvidenceMatchesMembers(orderedMembers, orderedEvidence))
        {
            return false;
        }

        try
        {
            var expectedAmount = orderedMembers
                .Where(item => item.State == AlertCostMemberStateV2.Estimated)
                .Select(item => item.Amount!.Value)
                .Aggregate(0m, checked((sum, amount) => sum + amount));
            var expectedSeverity = expectedAmount >= receipt.CriticalThreshold
                ? AlertSeverity.Critical
                : expectedAmount >= receipt.WarningThreshold
                    ? AlertSeverity.Warning
                    : (AlertSeverity?)null;
            var expectedSummary = new AlertRuleRegistryV2().Rules
                .Single(item => item.Descriptor.RuleId == receipt.RuleId)
                .Descriptor.Title;
            return receipt.ObservedAmount == expectedAmount
                && receipt.Severity == expectedSeverity
                && receipt.Summary == expectedSummary;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidReceiptScope(AlertCostScopeV2 scope)
    {
        if (!CostScopeId(scope.ScopeId)
            || scope.SessionIds is null
            || scope.SessionIds.Count is 0 or > 2_000
            || scope.SessionIds.Distinct(StringComparer.Ordinal).Count() != scope.SessionIds.Count
            || scope.SessionIds.Any(sessionId => !SessionId(sessionId)))
        {
            return false;
        }

        if (scope.Kind == AlertCostScopeKindV2.Session)
        {
            return scope.WindowStartUtc is null
                && scope.WindowEndUtc is null
                && scope.SessionIds.Count == 1;
        }

        if (scope.WindowStartUtc is null
            || scope.WindowEndUtc is null
            || scope.WindowStartUtc >= scope.WindowEndUtc
            || !Midnight(scope.WindowStartUtc.Value)
            || !Midnight(scope.WindowEndUtc.Value))
        {
            return false;
        }

        var days = (scope.WindowEndUtc.Value - scope.WindowStartUtc.Value).TotalDays;
        return scope.Kind == AlertCostScopeKindV2.UtcDay
            ? days == 1
            : scope.Kind == AlertCostScopeKindV2.RollingPeriod
                && days is >= 2 and <= 366
                && days == Math.Truncate(days);
    }

    private static bool ValidEvaluationContext(AlertEvaluationResultV2 evaluation)
    {
        if (!ValidBoundedScope(
                evaluation.ScopeKind,
                evaluation.ScopeStartUtc,
                evaluation.ScopeEndUtc)
            || evaluation.EligibleCount is < 0
            || evaluation.EstimatedCount is < 0
            || evaluation.PartialCount is < 0
            || evaluation.NotEstimableCount is < 0
            || evaluation.MissingCount is < 0
            || evaluation.FailedCount is < 0
            || evaluation.UnavailableCount is < 0
            || evaluation.StaleCount is < 0
            || evaluation.CoverageBasisPoints is < 0 or > 10_000)
        {
            return false;
        }

        if (evaluation.EligibleCount is null)
        {
            return evaluation.AggregateState == AlertCostAggregateStateV2.NotApplicable
                && evaluation.Currency is null
                && evaluation.EstimatedCount is null
                && evaluation.PartialCount is null
                && evaluation.NotEstimableCount is null
                && evaluation.MissingCount is null
                && evaluation.FailedCount is null
                && evaluation.UnavailableCount is null
                && evaluation.StaleCount is null
                && evaluation.CoverageBasisPoints is null
                && evaluation.FirstObservedAt is null
                && evaluation.LastObservedAt is null;
        }

        if (evaluation.EstimatedCount is null
            || evaluation.PartialCount is null
            || evaluation.NotEstimableCount is null
            || evaluation.MissingCount is null
            || evaluation.FailedCount is null
            || evaluation.UnavailableCount is null
            || evaluation.StaleCount is null
            || checked(
                evaluation.EstimatedCount.Value
                + evaluation.PartialCount.Value
                + evaluation.NotEstimableCount.Value
                + evaluation.MissingCount.Value
                + evaluation.FailedCount.Value
                + evaluation.UnavailableCount.Value
                + evaluation.StaleCount.Value) != evaluation.EligibleCount)
        {
            return false;
        }

        if (evaluation.EligibleCount == 0)
        {
            return evaluation.ScopeKind != AlertCostScopeKindV2.Session
                && evaluation.Currency is null
                && evaluation.AggregateState == AlertCostAggregateStateV2.NotApplicable
                && evaluation.CoverageBasisPoints is null
                && evaluation.FirstObservedAt is null
                && evaluation.LastObservedAt is null;
        }

        var expectedCoverage = checked((int)(
            evaluation.EstimatedCount.Value * 10_000 / evaluation.EligibleCount.Value));
        return evaluation.CoverageBasisPoints == expectedCoverage
            && evaluation.FirstObservedAt is not null
            && evaluation.LastObservedAt is not null
            && evaluation.FirstObservedAt <= evaluation.LastObservedAt
            && (evaluation.EstimatedCount == 0
                ? evaluation.Currency is null
                    && evaluation.AggregateState == AlertCostAggregateStateV2.NotApplicable
                : evaluation.Currency == "USD"
                    && evaluation.AggregateState is AlertCostAggregateStateV2.Available
                        or AlertCostAggregateStateV2.Unrepresentable);
    }

    private static bool ValidSuppressionContext(AlertSuppressionV2 suppression)
    {
        var evaluation = new AlertEvaluationResultV2(
            AlertContractVersionsV2.Evaluation,
            suppression.EvaluationId,
            new string('0', 64),
            suppression.ConfigurationVersion,
            suppression.ConfigurationHash,
            suppression.RuleId,
            suppression.RuleVersion,
            suppression.SourceCostConfigurationId,
            suppression.SourceConfigurationHeadRevision,
            suppression.SourceConfigurationCatalogSha256,
            suppression.ScopeKind,
            suppression.ScopeId,
            suppression.ScopeStartUtc,
            suppression.ScopeEndUtc,
            suppression.EligibilityDigest,
            suppression.Currency,
            suppression.AggregateState,
            suppression.EligibleCount,
            suppression.EstimatedCount,
            suppression.PartialCount,
            suppression.NotEstimableCount,
            suppression.MissingCount,
            suppression.FailedCount,
            suppression.UnavailableCount,
            suppression.StaleCount,
            suppression.CoverageBasisPoints,
            suppression.FirstObservedAt,
            suppression.LastObservedAt,
            [],
            [],
            []);
        if (!ValidEvaluationContext(evaluation)) return false;

        return suppression.Code switch
        {
            "scope_not_applicable" => ExpectedScope(suppression.RuleId) != suppression.ScopeKind,
            "eligible_set_incomplete" => suppression.EligibleCount is null,
            "no_eligible_sessions" => suppression.EligibleCount == 0,
            "no_covered_estimate" =>
                suppression.EligibleCount > 0 && suppression.EstimatedCount == 0,
            "aggregate_amount_not_representable" =>
                suppression.AggregateState == AlertCostAggregateStateV2.Unrepresentable,
            "insufficient_estimate_coverage" =>
                suppression.AggregateState == AlertCostAggregateStateV2.Available
                && suppression.EstimatedCount > 0,
            "rule_disabled" => true,
            _ => false,
        };
    }

    private static bool ValidBoundedScope(
        AlertCostScopeKindV2 kind,
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        if (kind == AlertCostScopeKindV2.Session)
        {
            return start is null && end is null;
        }
        if (start is null
            || end is null
            || start >= end
            || !Midnight(start.Value)
            || !Midnight(end.Value))
        {
            return false;
        }

        var days = (end.Value - start.Value).TotalDays;
        return kind == AlertCostScopeKindV2.UtcDay
            ? days == 1
            : kind == AlertCostScopeKindV2.RollingPeriod
                && days is >= 2 and <= 366
                && days == Math.Truncate(days);
    }

    private static bool ValidReceipt(AlertReceiptV2 receipt, AlertEvaluationResultV2 evaluation)
    {
        try
        {
            ValidateReceipt(receipt);
        }
        catch (Exception exception) when (exception is AlertContractException or OverflowException)
        {
            return false;
        }

        return receipt.EvaluationId == evaluation.EvaluationId
            && receipt.RuleId == evaluation.SelectedRuleId
            && receipt.RuleVersion == evaluation.SelectedRuleVersion
            && receipt.SourceCostConfigurationId == evaluation.SourceCostConfigurationId
            && receipt.SourceConfigurationHeadRevision == evaluation.SourceConfigurationHeadRevision
            && receipt.SourceConfigurationCatalogSha256 == evaluation.SourceConfigurationCatalogSha256
            && receipt.ConfigurationVersion == evaluation.ConfigurationVersion
            && receipt.ConfigurationHash == evaluation.ConfigurationHash
            && receipt.InputHash == evaluation.InputHash
            && receipt.Scope.ScopeId == evaluation.ScopeId
            && receipt.Scope.Kind == evaluation.ScopeKind
            && receipt.Scope.WindowStartUtc == evaluation.ScopeStartUtc
            && receipt.Scope.WindowEndUtc == evaluation.ScopeEndUtc
            && receipt.Currency == evaluation.Currency
            && receipt.AggregateState == evaluation.AggregateState
            && receipt.EligibleCount == evaluation.EligibleCount
            && receipt.EstimatedCount == evaluation.EstimatedCount
            && receipt.PartialCount == evaluation.PartialCount
            && receipt.NotEstimableCount == evaluation.NotEstimableCount
            && receipt.MissingCount == evaluation.MissingCount
            && receipt.FailedCount == evaluation.FailedCount
            && receipt.UnavailableCount == evaluation.UnavailableCount
            && receipt.StaleCount == evaluation.StaleCount
            && receipt.CoverageBasisPoints == evaluation.CoverageBasisPoints
            && receipt.FirstObservedAt == evaluation.FirstObservedAt
            && receipt.LastObservedAt == evaluation.LastObservedAt;
    }

    private static bool ValidSuppression(
        AlertSuppressionV2 suppression,
        AlertEvaluationResultV2 evaluation)
    {
        try
        {
            ValidateSuppression(suppression);
        }
        catch (Exception exception) when (exception is AlertContractException or OverflowException)
        {
            return false;
        }

        return suppression.EvaluationId == evaluation.EvaluationId
            && suppression.RuleId == evaluation.SelectedRuleId
            && suppression.RuleVersion == evaluation.SelectedRuleVersion
            && suppression.SourceCostConfigurationId == evaluation.SourceCostConfigurationId
            && suppression.SourceConfigurationHeadRevision == evaluation.SourceConfigurationHeadRevision
            && suppression.SourceConfigurationCatalogSha256 == evaluation.SourceConfigurationCatalogSha256
            && suppression.ConfigurationVersion == evaluation.ConfigurationVersion
            && suppression.ConfigurationHash == evaluation.ConfigurationHash
            && suppression.ScopeKind == evaluation.ScopeKind
            && suppression.ScopeId == evaluation.ScopeId
            && suppression.ScopeStartUtc == evaluation.ScopeStartUtc
            && suppression.ScopeEndUtc == evaluation.ScopeEndUtc
            && suppression.EligibilityDigest == evaluation.EligibilityDigest
            && suppression.Currency == evaluation.Currency
            && suppression.AggregateState == evaluation.AggregateState
            && suppression.EligibleCount == evaluation.EligibleCount
            && suppression.EstimatedCount == evaluation.EstimatedCount
            && suppression.PartialCount == evaluation.PartialCount
            && suppression.NotEstimableCount == evaluation.NotEstimableCount
            && suppression.MissingCount == evaluation.MissingCount
            && suppression.FailedCount == evaluation.FailedCount
            && suppression.UnavailableCount == evaluation.UnavailableCount
            && suppression.StaleCount == evaluation.StaleCount
            && suppression.CoverageBasisPoints == evaluation.CoverageBasisPoints
            && suppression.FirstObservedAt == evaluation.FirstObservedAt
            && suppression.LastObservedAt == evaluation.LastObservedAt;
    }

    private static bool SessionId(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out _)
        && value == value.ToLowerInvariant();

    private static bool CostConfigurationId(string? value) =>
        value is { Length: 83 }
        && value.StartsWith("cost-configuration-", StringComparison.Ordinal)
        && Hash(value[19..]);

    private static bool OpaqueId(string? value) =>
        value is { Length: > 0 and <= 256 }
        && !value.Any(character => char.IsWhiteSpace(character)
            || char.IsControl(character)
            || character is '/' or '\\' or '?' or '#');

    private static bool Midnight(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero && value.TimeOfDay == TimeSpan.Zero;

    private static AlertContractException InvalidSnapshot() =>
        new("invalid_snapshot", "Normalized alert snapshot is invalid.");

    private static AlertContractException InvalidConfiguration() =>
        new("invalid_configuration", "Alert engine configuration is invalid.");
}

internal static class AlertReceiptIdentityV2
{
    public static string Create(AlertReceiptV2 receipt) =>
        AlertHashing.Sha256(AlertHashing.Frame(
            System.Text.Encoding.UTF8.GetBytes("alert-receipt/v2"),
            AlertCanonicalJsonV2.SerializeReceiptIdentityProjection(receipt)));
}

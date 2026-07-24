using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostRecalculationCoordinatorTests
{
    [Theory]
    [InlineData("codex-app", "codex_adapter_unavailable")]
    [InlineData("github-copilot-vscode", "source_adapter_unavailable")]
    [InlineData("claude-code", "source_adapter_unavailable")]
    public void DefaultSourceAdapter_IsFrozenUnavailable(
        string sourceSurface,
        string expectedReason)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var request = new PricingEstimateSourceAdapterRequestV1(
            sessionId,
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero),
            sourceSurface,
            "1.2.3");

        var result = DefaultPricingEstimateSourceAdapterV1.Instance.Acquire(request);

        Assert.Equal(PricingEstimateSourceAdapterStatusV1.Unavailable, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Null(result.Facts);
    }

    [Fact]
    public void DefaultSourceAdapter_DoesNotRetainCallerOwnedValues()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var request = new PricingEstimateSourceAdapterRequestV1(
            sessionId,
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero),
            "github-copilot-vscode",
            "1.2.3");

        var first = DefaultPricingEstimateSourceAdapterV1.Instance.Acquire(request);
        var second = DefaultPricingEstimateSourceAdapterV1.Instance.Acquire(request);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BudgetSnapshot_UsesExactWindowAndDoesNotCountPartialAsZero()
    {
        var day = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var estimated = Member(
            Guid.NewGuid().ToString("D"),
            day.AddHours(1),
            AlertCostMemberStateV2.Estimated,
            2m,
            EstimateId('a'));
        var partial = Member(
            Guid.NewGuid().ToString("D"),
            day.AddHours(2),
            AlertCostMemberStateV2.Partial,
            5m,
            EstimateId('b'));
        var outside = Member(
            Guid.NewGuid().ToString("D"),
            day.AddDays(1),
            AlertCostMemberStateV2.Estimated,
            11m,
            EstimateId('c'));

        var snapshot = CostBudgetSnapshotProducerV1.Create(
            new CostBudgetScopeV1("utc_day", null, "2026-07-24", null, null),
            Facts(outside, partial, estimated),
            Context());

        Assert.Equal(AlertCostScopeKindV2.UtcDay, snapshot.Scope.Kind);
        Assert.Equal(day, snapshot.Scope.WindowStartUtc);
        Assert.Equal(day.AddDays(1), snapshot.Scope.WindowEndUtc);
        Assert.Equal(2, snapshot.EligibleCount);
        Assert.Equal(1, snapshot.EstimatedCount);
        Assert.Equal(1, snapshot.PartialCount);
        Assert.Equal(2m, snapshot.Amount);
        Assert.Equal(5000, snapshot.CoverageBasisPoints);
        Assert.Equal(
            new[] { estimated.SessionId, partial.SessionId },
            snapshot.Members.Select(item => item.SessionId));
        Assert.Matches("^[0-9a-f]{64}$", snapshot.EligibilityDigest);
        Assert.Equal(4, snapshot.Evidence.Count);
    }

    [Fact]
    public void BudgetSnapshot_OverflowIsIncompleteAndCarriesNoMembers()
    {
        var snapshot = CostBudgetSnapshotProducerV1.CreateIncomplete(
            new CostBudgetScopeV1(
                "rolling_period",
                null,
                null,
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
                7),
            new string('a', 64));

        Assert.Equal(AlertCostAcquisitionStateV2.Incomplete, snapshot.AcquisitionState);
        Assert.Equal(2001, snapshot.EligibleLowerBound);
        Assert.Null(snapshot.EligibleCount);
        Assert.Empty(snapshot.Members);
        Assert.Empty(snapshot.Evidence);
        Assert.Equal(new[] { "eligible_set_incomplete" }, snapshot.AcquisitionReasons);
    }

    [Fact]
    public void BudgetSnapshot_RollingPeriodUsesHalfOpenWindowAndPreservesUnknownStates()
    {
        var cutoff = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        var atStart = Member(
            Guid.NewGuid().ToString("D"),
            cutoff.AddDays(-7),
            AlertCostMemberStateV2.Stale,
            0m,
            EstimateId('d')) with
        {
            Amount = null,
            Currency = null,
        };
        var inside = Member(
            Guid.NewGuid().ToString("D"),
            cutoff.AddTicks(-1),
            AlertCostMemberStateV2.Estimated,
            3m,
            EstimateId('e'));
        var atEnd = Member(
            Guid.NewGuid().ToString("D"),
            cutoff,
            AlertCostMemberStateV2.Estimated,
            5m,
            EstimateId('f'));

        var snapshot = CostBudgetSnapshotProducerV1.Create(
            new CostBudgetScopeV1("rolling_period", null, null, cutoff, 7),
            Facts(atEnd, inside, atStart),
            Context());

        Assert.Equal(cutoff.AddDays(-7), snapshot.Scope.WindowStartUtc);
        Assert.Equal(cutoff, snapshot.Scope.WindowEndUtc);
        Assert.Equal(2, snapshot.EligibleCount);
        Assert.Equal(1, snapshot.EstimatedCount);
        Assert.Equal(1, snapshot.StaleCount);
        Assert.Equal(3m, snapshot.Amount);
        Assert.Equal(5000, snapshot.CoverageBasisPoints);
        Assert.DoesNotContain(atEnd.SessionId, snapshot.Scope.SessionIds);
    }

    [Fact]
    public void BudgetSnapshot_SessionScopeRequiresTheExactEligibleSession()
    {
        var sessionId = Guid.NewGuid().ToString("D");

        Assert.Throws<ArgumentException>(() =>
            CostBudgetSnapshotProducerV1.Create(
                new CostBudgetScopeV1("session", sessionId, null, null, null),
                [],
                Context()));
    }

    [Fact]
    public void BudgetSnapshot_ActiveEstimateSurvivesALaterUnavailableAttempt()
    {
        var effective = new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero);
        var member = Member(
            Guid.NewGuid().ToString("D"),
            effective,
            AlertCostMemberStateV2.Estimated,
            4m,
            EstimateId('9')) with
        {
            AttemptRevision = 2,
            AttemptResultKind = AlertCostAttemptResultKindV2.Unavailable,
            AttemptResultCode = "source_adapter_unavailable",
        };

        var snapshot = CostBudgetSnapshotProducerV1.Create(
            new CostBudgetScopeV1("session", member.SessionId, null, null, null),
            Facts(member),
            Context());

        Assert.Equal(AlertCostMemberStateV2.Estimated, snapshot.Members[0].State);
        Assert.Equal(AlertCostAttemptResultKindV2.Unavailable, snapshot.Members[0].AttemptResultKind);
        Assert.Equal(4m, snapshot.Amount);
        Assert.Equal(10_000, snapshot.CoverageBasisPoints);
    }

    [Fact]
    public void BudgetSnapshot_EligibilityDigestBindsScopeConfigurationAndResolver()
    {
        var member = Member(
            Guid.NewGuid().ToString("D"),
            new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero),
            AlertCostMemberStateV2.Estimated,
            1m,
            EstimateId('8'));
        var scope = new CostBudgetScopeV1("utc_day", null, "2026-07-24", null, null);
        var baseline = CostBudgetSnapshotProducerV1.Create(
            scope,
            Facts(member),
            Context());
        var changedResolver = CostBudgetSnapshotProducerV1.Create(
            scope,
            [new(member, new string('d', 64))],
            Context());
        var changedConfiguration = CostBudgetSnapshotProducerV1.Create(
            scope,
            Facts(member),
            Context() with { ConfigurationHeadRevision = 2 });

        Assert.NotEqual(baseline.EligibilityDigest, changedResolver.EligibilityDigest);
        Assert.NotEqual(baseline.EligibilityDigest, changedConfiguration.EligibilityDigest);
        Assert.NotEqual(baseline.Scope.ScopeId, changedConfiguration.Scope.ScopeId);
    }

    [Fact]
    public void CompletionPlan_RequiresByteEqualTransactionEvaluation()
    {
        var snapshot = CostBudgetSnapshotProducerV1.Create(
            new CostBudgetScopeV1("utc_day", null, "2026-07-24", null, null),
            [],
            Context());
        var configuration = new AlertEngineConfigurationV2(
            AlertContractVersionsV2.Configuration,
            "cost.configuration.v1",
            "cost-configuration-" + new string('e', 64),
            1,
            new string('f', 64),
            []);
        var engine = new AlertEvaluationEngine(
            new AlertRuleRegistryV2(),
            new AlwaysResolvedEvidenceResolver());
        var evaluation = Assert.IsType<AlertEvaluationResultV2>(
            engine.Evaluate(
                new("daily-estimated-cost-threshold", "1"),
                snapshot,
                configuration,
                new(AlertEvidenceReadViewV2.Instance, [])).Evaluation);

        Assert.True(PricingBudgetEvaluationPlanV1.ByteEquivalent(
            [evaluation],
            [evaluation with { }]));
        Assert.False(PricingBudgetEvaluationPlanV1.ByteEquivalent(
            [evaluation],
            [evaluation with { EligibilityDigest = new string('0', 64) }]));
        Assert.Equal(
            (long)AlertCanonicalJsonV2.SerializeSnapshot(snapshot).Length
            + AlertCanonicalJsonV2.SerializeEvaluation(evaluation).Length
            + evaluation.Receipts.Sum(item =>
                AlertCanonicalJsonV2.SerializeReceipt(item).Length)
            + evaluation.Suppressions.Sum(item =>
                AlertCanonicalJsonV2.SerializeSuppression(item).Length),
            SqliteCostRecalculationCoordinatorV1.CanonicalCandidateByteCount(
                snapshot,
                evaluation));
    }

    [Fact]
    public void TargetFailureSelection_UsesPhaseThenOrdinalThenCodePrecedence()
    {
        var selected = SqliteCostRecalculationCoordinatorV1.SelectTargetFailure(
        [
            new("estimate_validation", "target", 0, "pricing_estimation_failed"),
            new("adapter", "target", 2, "source_adapter_failed"),
            new("adapter", "target", 1, "source_adapter_failed"),
            new("estimate_validation", "target", 0, "invalid_estimate_source"),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("adapter", selected.FailurePhase);
        Assert.Equal(1, selected.FailureOrdinal);
        Assert.Equal("source_adapter_failed", selected.FailureCode);
    }

    [Fact]
    public void SourceEntrySelection_RequiresOneExactConfiguredMapping()
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            new string('a', 64),
            [new(
                "github-copilot-vscode",
                "1.2.3",
                "pricing-capability.v1",
                "github_copilot",
                "github_ai_credits",
                "credit_consuming_interaction")],
            [],
            new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            0,
            SqliteCostRecalculationCoordinatorV1.SelectSourceEntry(
                configuration,
                "github-copilot-vscode",
                "1.2.3",
                "pricing-capability.v1",
                "github_copilot"));
        Assert.Null(SqliteCostRecalculationCoordinatorV1.SelectSourceEntry(
            configuration,
            "github-copilot-vscode",
            "1.2.3",
            "other-capability.v1",
            "github_copilot"));
        Assert.Null(SqliteCostRecalculationCoordinatorV1.SelectSourceEntry(
            configuration with
            {
                SourceEntries =
                [
                    configuration.SourceEntries[0] with
                    {
                        ApplicationVersion = "1.2.3+build",
                    },
                ],
            },
            "github-copilot-vscode",
            "1.2.3+build",
            "pricing-capability.v1",
            "github_copilot"));
    }

    [Fact]
    public void EvidenceResolver_DistinguishesExistingIdentityMismatchFromAbsentId()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var otherSessionId = Guid.NewGuid().ToString("D");
        var observedAt = new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero);
        var estimateId = EstimateId('7');
        var resolver = new CostAlertEvidenceResolverV1();
        var scope = new AlertEvidenceResolutionScopeV2(
            new CostAlertEvidenceReadViewV1(
                new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
                {
                    [sessionId] = observedAt,
                },
                new Dictionary<string, (string SessionId, DateTimeOffset ObservedAtUtc)>(
                    StringComparer.Ordinal)
                {
                    [estimateId] = (sessionId, observedAt),
                }),
            []);

        Assert.Equal(
            AlertEvidenceResolutionStatusV2.Resolved,
            resolver.Resolve(
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    estimateId,
                    sessionId,
                    observedAt),
                scope));
        Assert.Equal(
            AlertEvidenceResolutionStatusV2.ContractRejected,
            resolver.Resolve(
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    estimateId,
                    otherSessionId,
                    observedAt),
                scope));
        Assert.Equal(
            AlertEvidenceResolutionStatusV2.ContractRejected,
            resolver.Resolve(
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    estimateId,
                    sessionId,
                    observedAt.AddTicks(1)),
                scope));
        Assert.Equal(
            AlertEvidenceResolutionStatusV2.Unresolved,
            resolver.Resolve(
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    EstimateId('6'),
                    sessionId,
                    observedAt),
                scope));
    }

    private static AlertCostMemberV2 Member(
        string sessionId,
        DateTimeOffset effectiveAt,
        AlertCostMemberStateV2 state,
        decimal amount,
        string estimateId) =>
        new(
            sessionId,
            effectiveAt,
            effectiveAt.AddSeconds(1),
            "github-copilot-vscode",
            "1.2.3",
            state,
            1,
            AlertCostAttemptResultKindV2.Estimate,
            null,
            1,
            estimateId,
            effectiveAt.AddSeconds(2),
            new string('d', 64),
            "pricing-registry-v1",
            "github-copilot",
            "gpt-5",
            "github-ai-credits",
            amount,
            "USD");

    private static string EstimateId(char value) =>
        "pricing-estimate-" + new string(value, 64);

    private static IReadOnlyList<CostBudgetEligibleMemberV1> Facts(
        params AlertCostMemberV2[] members) =>
        members.Select(member => new CostBudgetEligibleMemberV1(
            member,
            new string('c', 64))).ToArray();

    private static CostBudgetEligibilityContextV1 Context() =>
        new(
            "cost-configuration-" + new string('a', 64),
            1,
            new string('b', 64));

    private sealed class AlwaysResolvedEvidenceResolver : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }
}

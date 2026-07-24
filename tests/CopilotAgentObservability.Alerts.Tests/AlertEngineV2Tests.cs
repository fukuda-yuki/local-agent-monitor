using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertEngineV2Tests
{
    internal static AlertEvaluationResultV2 Evaluation()
    {
        var fixture = Fixture();
        return Assert.IsType<AlertEvaluationResultV2>(fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            fixture.Snapshot,
            fixture.Configuration,
            fixture.EvidenceScope).Evaluation);
    }

    internal static (
        AlertNormalizedSnapshotV2 Snapshot,
        AlertEngineConfigurationV2 Configuration) Inputs()
    {
        var fixture = Fixture();
        return (fixture.Snapshot, fixture.Configuration);
    }

    [Fact]
    public void Evaluate_SessionBudgetRule_IsDisabledUntilExplicitConfiguration()
    {
        var fixture = Fixture();
        var configuration = fixture.Configuration with { Rules = [] };

        var result = fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            fixture.Snapshot,
            configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        Assert.Empty(result.Evaluation!.Receipts);
        Assert.Equal("rule_disabled", Assert.Single(result.Evaluation.Suppressions).Code);
        Assert.Empty(result.Evaluation.RejectedMatches);
    }

    [Fact]
    public void Evaluate_AmountEqualToCriticalThreshold_EmitsCanonicalCriticalReceipt()
    {
        var fixture = Fixture();

        var result = fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            fixture.Snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        var evaluation = Assert.IsType<AlertEvaluationResultV2>(result.Evaluation);
        var receipt = Assert.Single(evaluation.Receipts);
        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(2m, receipt.ObservedAmount);
        Assert.Equal(10_000, receipt.CoverageBasisPoints);
        Assert.Empty(evaluation.Suppressions);

        var evaluationBytes = AlertCanonicalJsonV2.SerializeEvaluation(evaluation);
        var receiptBytes = AlertCanonicalJsonV2.SerializeReceipt(receipt);
        Assert.Equal(evaluation.EvaluationId, AlertEvaluationConsumerV2.Validate(evaluationBytes).EvaluationId);
        Assert.Equal(receipt.AlertId, AlertReceiptConsumerV2.Validate(receiptBytes).AlertId);
        Assert.Equal(receipt.AlertId, AlertCenterReceiptConsumerV2.Validate(receiptBytes).AlertId);
        Assert.Throws<AlertReceiptConsumerException>(() => AlertReceiptConsumerV1.Validate(receiptBytes));
    }

    [Fact]
    public void Evaluate_PeriodRuleWithNonUsdConfiguration_IsContractRejected()
    {
        var fixture = Fixture();
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(5);
        var scope = fixture.Snapshot.Scope with
        {
            Kind = AlertCostScopeKindV2.RollingPeriod,
            WindowStartUtc = start,
            WindowEndUtc = end,
        };
        scope = scope with
        {
            ScopeId = AlertCostScopeIdentityV2.Create(
                scope.Kind,
                scope.WindowStartUtc,
                scope.WindowEndUtc,
                fixture.Snapshot.EligibilityDigest,
                scope.SessionIds),
        };
        var snapshot = fixture.Snapshot with { Scope = scope };
        var configuration = fixture.Configuration with
        {
            Rules =
            [
                new(
                    "period-estimated-cost-threshold",
                    "1",
                    true,
                    "EUR",
                    1m,
                    2m,
                    10_000,
                    AlertCostScopeKindV2.RollingPeriod,
                    5),
            ],
        };

        var result = fixture.Engine.Evaluate(
            new("period-estimated-cost-threshold", "1"),
            snapshot,
            configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.ContractRejected, result.Status);
        Assert.Equal("invalid_configuration", result.Code);
        Assert.Null(result.Evaluation);
    }

    [Fact]
    public void Evaluate_EstimatedActiveHeadWithLaterFailedAttempt_RemainsValid()
    {
        var fixture = Fixture();
        var member = fixture.Snapshot.Members[0] with
        {
            AttemptRevision = 2,
            AttemptResultKind = AlertCostAttemptResultKindV2.Failed,
            AttemptResultCode = "pricing_store_failed",
        };
        var snapshot = fixture.Snapshot with { Members = [member] };

        var result = fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        Assert.Single(result.Evaluation!.Receipts);
    }

    [Fact]
    public void Evaluate_CanonicalPricingModelLabelWithSpaces_IsAcceptedExactly()
    {
        var fixture = Fixture();
        var member = fixture.Snapshot.Members[0] with
        {
            Provider = "github_copilot",
            Model = "GPT-5 mini",
            BillingMode = "github_ai_credits",
        };
        var snapshot = fixture.Snapshot with { Members = [member] };

        var result = fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        var receipt = Assert.Single(result.Evaluation!.Receipts);
        Assert.Equal("github_copilot", Assert.Single(receipt.Members).Provider);
        Assert.Equal("GPT-5 mini", Assert.Single(receipt.Members).Model);
        Assert.Equal("github_ai_credits", Assert.Single(receipt.Members).BillingMode);

        var projection = AlertCenterReceiptConsumerV2.Validate(
            AlertCanonicalJsonV2.SerializeReceipt(receipt));
        Assert.Equal("GPT-5 mini", Assert.Single(projection.Members).Model);
    }

    [Fact]
    public void Evaluate_UnsafeOrUnboundedPricingModelLabels_AreRejected()
    {
        var fixture = Fixture();
        var unsafeLabels = new[]
        {
            "",
            " ",
            ".",
            "..",
            "model\u0000label",
            "https://example.test/model",
            "../private/model",
            @"C:\private\model",
            "person@example.test",
            "Authorization: Bearer secret",
            new string('m', 257),
            "\ud800",
        };

        foreach (var model in unsafeLabels)
        {
            var member = fixture.Snapshot.Members[0] with { Model = model };
            var snapshot = fixture.Snapshot with { Members = [member] };

            var result = fixture.Engine.Evaluate(
                new("session-estimated-cost-threshold", "1"),
                snapshot,
                fixture.Configuration,
                fixture.EvidenceScope);

            Assert.Equal(AlertEvaluationEngineStatusV2.ContractRejected, result.Status);
            Assert.Equal("invalid_snapshot", result.Code);
            Assert.Null(result.Evaluation);
        }
    }

    [Fact]
    public void Evaluate_ProviderAndBillingModeRemainTokenBounded()
    {
        var fixture = Fixture();
        var invalidMembers = new[]
        {
            fixture.Snapshot.Members[0] with { Provider = "github copilot" },
            fixture.Snapshot.Members[0] with { BillingMode = "github ai credits" },
        };

        foreach (var member in invalidMembers)
        {
            var result = fixture.Engine.Evaluate(
                new("session-estimated-cost-threshold", "1"),
                fixture.Snapshot with { Members = [member] },
                fixture.Configuration,
                fixture.EvidenceScope);

            Assert.Equal(AlertEvaluationEngineStatusV2.ContractRejected, result.Status);
            Assert.Equal("invalid_snapshot", result.Code);
            Assert.Null(result.Evaluation);
        }
    }

    [Fact]
    public void ReceiptConsumer_RejectsCanonicalSemanticMismatch()
    {
        var receipt = Assert.Single(Evaluation().Receipts);
        var tampered = receipt with
        {
            Members = [receipt.Members[0] with { Amount = 1m }],
        };
        var identityType = typeof(AlertEvaluationEngine).Assembly.GetType(
            "CopilotAgentObservability.Alerts.AlertReceiptIdentityV2",
            throwOnError: true)!;
        var create = identityType.GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        tampered = tampered with { AlertId = Assert.IsType<string>(create.Invoke(null, [tampered])) };

        Assert.Throws<AlertReceiptConsumerException>(() =>
            AlertReceiptConsumerV2.Validate(AlertCanonicalJsonV2.SerializeReceipt(tampered)));
    }

    [Fact]
    public void Evaluate_ClaimedStateCountsMustEqualMemberStates()
    {
        var fixture = Fixture();
        var snapshot = fixture.Snapshot with
        {
            AggregateState = AlertCostAggregateStateV2.NotApplicable,
            Currency = null,
            Amount = null,
            EstimatedCount = 0,
            PartialCount = 1,
            CoverageNumerator = 0,
            CoverageBasisPoints = 0,
        };

        var result = fixture.Engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.ContractRejected, result.Status);
        Assert.Equal("invalid_snapshot", result.Code);
    }

    [Fact]
    public void Evaluate_IncompleteAcquisitionRequiresEmptyScopeSessionIds()
    {
        var fixture = Fixture();
        var start = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var scope = new AlertCostScopeV2(
            string.Empty,
            AlertCostScopeKindV2.UtcDay,
            start,
            start.AddDays(1),
            fixture.Snapshot.Scope.SessionIds);
        scope = scope with
        {
            ScopeId = AlertCostScopeIdentityV2.Create(
                scope.Kind,
                scope.WindowStartUtc,
                scope.WindowEndUtc,
                fixture.Snapshot.EligibilityDigest,
                scope.SessionIds),
        };
        var snapshot = fixture.Snapshot with
        {
            AcquisitionState = AlertCostAcquisitionStateV2.Incomplete,
            AcquisitionReasons = ["eligible_set_incomplete"],
            AggregateState = AlertCostAggregateStateV2.NotApplicable,
            EligibleCount = null,
            EligibleLowerBound = 2_001,
            Scope = scope,
            Currency = null,
            Amount = null,
            EstimatedCount = null,
            PartialCount = null,
            NotEstimableCount = null,
            MissingCount = null,
            FailedCount = null,
            UnavailableCount = null,
            StaleCount = null,
            CoverageNumerator = null,
            CoverageDenominator = null,
            CoverageBasisPoints = null,
            Members = [],
            Evidence = [],
            Completeness = AlertCostCompletenessV2.Partial,
            CompletenessReasons = ["eligible_set_incomplete"],
            FirstObservedAt = null,
            LastObservedAt = null,
        };

        var result = fixture.Engine.Evaluate(
            new("daily-estimated-cost-threshold", "1"),
            snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.ContractRejected, result.Status);
        Assert.Equal("invalid_snapshot", result.Code);
    }

    [Fact]
    public void Evaluate_PeriodWindowMismatchProducesScopeNotApplicable()
    {
        var fixture = Fixture();
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var scope = fixture.Snapshot.Scope with
        {
            Kind = AlertCostScopeKindV2.RollingPeriod,
            WindowStartUtc = start,
            WindowEndUtc = start.AddDays(5),
        };
        scope = scope with
        {
            ScopeId = AlertCostScopeIdentityV2.Create(
                scope.Kind,
                scope.WindowStartUtc,
                scope.WindowEndUtc,
                fixture.Snapshot.EligibilityDigest,
                scope.SessionIds),
        };
        var snapshot = fixture.Snapshot with { Scope = scope };
        var configuration = fixture.Configuration with
        {
            Rules =
            [
                new(
                    "period-estimated-cost-threshold",
                    "1",
                    true,
                    "USD",
                    1m,
                    2m,
                    10_000,
                    AlertCostScopeKindV2.RollingPeriod,
                    7),
            ],
        };

        var result = fixture.Engine.Evaluate(
            new("period-estimated-cost-threshold", "1"),
            snapshot,
            configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        Assert.Equal("scope_not_applicable", Assert.Single(result.Evaluation!.Suppressions).Code);
    }

    [Fact]
    public void EvaluationConsumer_BindsEligibilityDigestToReceiptScopeMembers()
    {
        var evaluation = Evaluation() with { EligibilityDigest = new string('f', 64) };

        Assert.Throws<AlertEvaluationConsumerException>(() =>
            AlertEvaluationConsumerV2.Validate(
                AlertCanonicalJsonV2.SerializeEvaluation(evaluation)));
    }

    [Fact]
    public void PendingEvidenceScope_RejectsInvalidBoundsFieldsDuplicatesAndOrder()
    {
        var valid = PendingEvidence(0);
        var invalidSets = new IReadOnlyList<StrictPendingPricingEvidenceV2>[]
        {
            Enumerable.Range(0, 101).Select(PendingEvidence).ToArray(),
            [valid with { EstimateId = "bad" }],
            [valid, PendingEvidence(1) with { EstimateId = valid.EstimateId }],
            [valid, PendingEvidence(1) with { SessionId = valid.SessionId }],
            [valid, PendingEvidence(1) with { TargetOrdinal = valid.TargetOrdinal }],
            [PendingEvidence(1), valid],
            [null!],
        };

        foreach (var invalid in invalidSets)
        {
            Assert.Throws<AlertContractException>(() =>
                new AlertEvidenceResolutionScopeV2(AlertEvidenceReadViewV2.Instance, invalid));
        }
    }

    [Theory]
    [InlineData(AlertEvidenceResolutionStatusV2.Unresolved, AlertEvaluationEngineStatusV2.UnresolvedEvidence, "unresolved_evidence")]
    [InlineData(AlertEvidenceResolutionStatusV2.StoreFailure, AlertEvaluationEngineStatusV2.StoreFailure, "alert_store_unavailable")]
    [InlineData(AlertEvidenceResolutionStatusV2.ContractRejected, AlertEvaluationEngineStatusV2.ContractRejected, "alert_contract_rejected")]
    public void Evaluate_PreservesResolverOutcome(
        AlertEvidenceResolutionStatusV2 resolverStatus,
        AlertEvaluationEngineStatusV2 expectedStatus,
        string expectedCode)
    {
        var fixture = Fixture();
        var engine = new AlertEvaluationEngine(
            new AlertRuleRegistryV2(),
            new FixedResolver(resolverStatus));

        var result = engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            fixture.Snapshot,
            fixture.Configuration,
            fixture.EvidenceScope);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.Evaluation);
    }

    [Theory]
    [InlineData("session-estimated-cost-threshold", AlertCostScopeKindV2.Session, 0, 1, 2, AlertSeverity.Critical)]
    [InlineData("daily-estimated-cost-threshold", AlertCostScopeKindV2.UtcDay, 1, 2, 3, AlertSeverity.Warning)]
    [InlineData("period-estimated-cost-threshold", AlertCostScopeKindV2.RollingPeriod, 5, 1, 2, AlertSeverity.Critical)]
    public void Evaluate_AllRegisteredRulesHonorInclusiveThresholdBoundaries(
        string ruleId,
        AlertCostScopeKindV2 scopeKind,
        int windowDays,
        decimal warning,
        decimal critical,
        AlertSeverity expected)
    {
        var fixture = Fixture();
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var scope = fixture.Snapshot.Scope with
        {
            Kind = scopeKind,
            WindowStartUtc = scopeKind == AlertCostScopeKindV2.Session ? null : start,
            WindowEndUtc = scopeKind == AlertCostScopeKindV2.Session
                ? null
                : start.AddDays(windowDays),
        };
        scope = scope with
        {
            ScopeId = AlertCostScopeIdentityV2.Create(
                scope.Kind,
                scope.WindowStartUtc,
                scope.WindowEndUtc,
                fixture.Snapshot.EligibilityDigest,
                scope.SessionIds),
        };
        var snapshot = fixture.Snapshot with { Scope = scope };
        var configuration = fixture.Configuration with
        {
            Rules =
            [
                new(
                    ruleId,
                    "1",
                    true,
                    "USD",
                    warning,
                    critical,
                    10_000,
                    scopeKind,
                    scopeKind == AlertCostScopeKindV2.RollingPeriod
                        ? windowDays
                        : null),
            ],
        };

        var result = fixture.Engine.Evaluate(
            new(ruleId, "1"),
            snapshot,
            configuration,
            fixture.EvidenceScope);

        Assert.Equal(AlertEvaluationEngineStatusV2.Success, result.Status);
        Assert.Equal(expected, Assert.Single(result.Evaluation!.Receipts).Severity);
    }

    [Fact]
    public void Rules_ApplyTheFixedSuppressionPrecedence()
    {
        var fixture = Fixture();
        var rule = new AlertRuleRegistryV2().Resolve(
            new("session-estimated-cost-threshold", "1"));
        var enabled = fixture.Configuration.Rules[0];
        var cases = new[]
        {
            (
                Snapshot: fixture.Snapshot with
                {
                    Scope = fixture.Snapshot.Scope with
                    {
                        Kind = AlertCostScopeKindV2.UtcDay,
                        WindowStartUtc = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
                        WindowEndUtc = new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
                    },
                    AcquisitionState = AlertCostAcquisitionStateV2.Incomplete,
                    EligibleCount = 0,
                    EstimatedCount = 0,
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: (AlertBudgetRuleConfigurationV2?)null,
                Code: "scope_not_applicable"),
            (
                Snapshot: fixture.Snapshot with
                {
                    AcquisitionState = AlertCostAcquisitionStateV2.Incomplete,
                    EligibleCount = 0,
                    EstimatedCount = 0,
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: enabled with { Enabled = false },
                Code: "rule_disabled"),
            (
                Snapshot: fixture.Snapshot with
                {
                    AcquisitionState = AlertCostAcquisitionStateV2.Incomplete,
                    EligibleCount = 0,
                    EstimatedCount = 0,
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: enabled,
                Code: "eligible_set_incomplete"),
            (
                Snapshot: fixture.Snapshot with
                {
                    EligibleCount = 0,
                    EstimatedCount = 0,
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: enabled,
                Code: "no_eligible_sessions"),
            (
                Snapshot: fixture.Snapshot with
                {
                    EstimatedCount = 0,
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: enabled,
                Code: "no_covered_estimate"),
            (
                Snapshot: fixture.Snapshot with
                {
                    AggregateState = AlertCostAggregateStateV2.Unrepresentable,
                    CoverageBasisPoints = 0,
                },
                Configuration: enabled,
                Code: "aggregate_amount_not_representable"),
            (
                Snapshot: fixture.Snapshot with { CoverageBasisPoints = 9_999 },
                Configuration: enabled,
                Code: "insufficient_estimate_coverage"),
        };

        foreach (var item in cases)
        {
            var outcome = rule.Evaluate(new(item.Snapshot, item.Configuration, rule.Descriptor));
            Assert.Equal(item.Code, outcome.SuppressionCode);
            Assert.Null(outcome.Severity);
        }
    }

    private static StrictPendingPricingEvidenceV2 PendingEvidence(int ordinal)
    {
        var suffix = ordinal.ToString("x12", System.Globalization.CultureInfo.InvariantCulture);
        return new(
            "pricing-estimate-" + ordinal.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
            "01984045-9d80-7000-8000-" + suffix,
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero),
            new string('a', 64),
            new string('b', 64),
            "01984045-9d80-7000-8000-000000000099",
            ordinal);
    }

    private static TestFixture Fixture()
    {
        var observedAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var sessionId = "01984045-9d80-7000-8000-000000000001";
        var estimateId = "pricing-estimate-" + new string('a', 64);
        var eligibilityDigest = new string('b', 64);
        var scope = new AlertCostScopeV2(
            AlertCostScopeIdentityV2.Create(AlertCostScopeKindV2.Session, null, null, eligibilityDigest, [sessionId]),
            AlertCostScopeKindV2.Session,
            null,
            null,
            [sessionId]);
        var member = new AlertCostMemberV2(
            sessionId,
            observedAt,
            observedAt.AddSeconds(1),
            "github-copilot",
            "1.2.3",
            AlertCostMemberStateV2.Estimated,
            1,
            AlertCostAttemptResultKindV2.Estimate,
            null,
            1,
            estimateId,
            observedAt.AddSeconds(2),
            new string('c', 64),
            "pricing-registry-v1",
            "github",
            "gpt-5",
            "api",
            2m,
            "USD");
        var evidence = new AlertEvidenceReferenceV2[]
        {
            new(AlertEvidenceKindV2.Session, sessionId, sessionId, observedAt),
            new(AlertEvidenceKindV2.PricingEstimate, estimateId, sessionId, observedAt.AddSeconds(2)),
        };
        var snapshot = new AlertNormalizedSnapshotV2(
            AlertContractVersionsV2.Snapshot,
            "estimated_cost",
            "local-monitor-cost-analytics",
            "1",
            AlertCostAcquisitionStateV2.Complete,
            [],
            AlertCostAggregateStateV2.Available,
            eligibilityDigest,
            1,
            null,
            scope,
            "USD",
            2m,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            1,
            10_000,
            [member],
            evidence,
            AlertCostCompletenessV2.Full,
            [],
            observedAt,
            observedAt);
        var configuration = new AlertEngineConfigurationV2(
            AlertContractVersionsV2.Configuration,
            "cost.configuration.v1",
            "cost-configuration-" + new string('d', 64),
            1,
            new string('e', 64),
            [
                new(
                    "session-estimated-cost-threshold",
                    "1",
                    true,
                    "USD",
                    1m,
                    2m,
                    10_000,
                    AlertCostScopeKindV2.Session,
                    null),
            ]);
        var registry = new AlertRuleRegistryV2();
        var scopeView = new AlertEvidenceResolutionScopeV2(AlertEvidenceReadViewV2.Instance, []);
        return new(new AlertEvaluationEngine(registry, new Resolver()), snapshot, configuration, scopeView);
    }

    private sealed record TestFixture(
        AlertEvaluationEngine Engine,
        AlertNormalizedSnapshotV2 Snapshot,
        AlertEngineConfigurationV2 Configuration,
        AlertEvidenceResolutionScopeV2 EvidenceScope);

    private sealed class Resolver : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }

    private sealed class FixedResolver(AlertEvidenceResolutionStatusV2 status)
        : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) => status;
    }
}

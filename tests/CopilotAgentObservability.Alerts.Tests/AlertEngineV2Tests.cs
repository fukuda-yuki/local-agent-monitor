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
}

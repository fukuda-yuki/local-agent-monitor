using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class SanitizedExportAlertFixture
{
    internal static byte[] Bytes(params AlertEvidenceReference[] evidence)
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        if (evidence.Length == 0)
            evidence = [new(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed)];
        var primary = evidence[0];
        var signals = evidence
            .Select((item, index) => new AlertSignal(
                $"signal-{index + 1}",
                AlertSignalKind.SessionEvent,
                index + 1,
                item.ObservedAt,
                null,
                AlertSignalStatus.Success,
                [],
                [],
                item))
            .ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot, "github-copilot", "1.2.3", primary.SessionId, primary.TraceId, AlertCompleteness.Partial,
            ["ingest_gap"], observed, observed.AddSeconds(1), [new("tool-events", AlertCapabilityAvailability.Available)],
            signals);
        var descriptor = new AlertRuleDescriptor(
            "fixture-rule", "1", "Fixture summary", "Fixture description", ["tool-events"], AlertRuleScope.Session, [], "session",
            [new("count", "calls", AlertThresholdDirection.HigherIsWorse, 0, 10, 1, 2)],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"], ["github-copilot"]);
        var match = new AlertRuleMatch(AlertSeverity.Warning, [new("count", "calls", 2)], evidence, observed, observed.AddSeconds(1));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([new FixedRule(descriptor, match)]), new ExistingEvidenceResolver());
        return AlertCanonicalJson.SerializeReceipt(Assert.Single(engine.Evaluate(
            snapshot, new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", [])).Receipts));
    }

    internal static AlertEvaluationResultV2 EvaluationV2()
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
        var engine = new AlertEvaluationEngine(new AlertRuleRegistryV2(), new ExistingEvidenceResolverV2());
        return Assert.IsType<AlertEvaluationResultV2>(engine.Evaluate(
            new("session-estimated-cost-threshold", "1"),
            snapshot,
            configuration,
            new(AlertEvidenceReadViewV2.Instance, [])).Evaluation);
    }

    private sealed class FixedRule(AlertRuleDescriptor descriptor, AlertRuleMatch match) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => new([match], []);
    }

    private sealed class ExistingEvidenceResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }

    private sealed class ExistingEvidenceResolverV2 : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }
}

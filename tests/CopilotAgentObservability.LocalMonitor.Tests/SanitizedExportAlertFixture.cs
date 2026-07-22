using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class SanitizedExportAlertFixture
{
    internal static byte[] Bytes()
    {
        var observed = new DateTimeOffset(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot, "github-copilot", "1.2.3", "session-1", "trace-1", AlertCompleteness.Partial,
            ["ingest_gap"], observed, observed.AddSeconds(1), [new("tool-events", AlertCapabilityAvailability.Available)],
            [new("signal-1", AlertSignalKind.SessionEvent, 1, observed, null, AlertSignalStatus.Success, [], [], evidence)]);
        var descriptor = new AlertRuleDescriptor(
            "fixture-rule", "1", "Fixture summary", "Fixture description", ["tool-events"], AlertRuleScope.Session, [], "session",
            [new("count", "calls", AlertThresholdDirection.HigherIsWorse, 0, 10, 1, 2)],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"], ["github-copilot"]);
        var match = new AlertRuleMatch(AlertSeverity.Warning, [new("count", "calls", 2)], [evidence], observed, observed.AddSeconds(1));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([new FixedRule(descriptor, match)]), new ExistingEvidenceResolver());
        return AlertCanonicalJson.SerializeReceipt(Assert.Single(engine.Evaluate(
            snapshot, new AlertEngineConfiguration(AlertContractVersions.Configuration, "fixture-v1", [])).Receipts));
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
}

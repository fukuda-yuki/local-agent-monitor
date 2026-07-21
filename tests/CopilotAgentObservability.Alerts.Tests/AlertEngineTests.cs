using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertEngineTests
{
    [Fact]
    public void Evaluate_EquivalentOrdering_ProducesIdenticalCanonicalBytes()
    {
        var first = Fixture.Snapshot(
            capabilities: [new("tool-events", AlertCapabilityAvailability.Available), new("token-usage", AlertCapabilityAvailability.Available)],
            reasons: ["ingest_gap", "historical_summary_only"]);
        var second = first with
        {
            Capabilities = first.Capabilities.Reverse().ToArray(),
            CompletenessReasons = first.CompletenessReasons.Reverse().ToArray(),
        };
        var rule = new TestRule(Fixture.Descriptor(required: ["tool-events"]), Fixture.Match(first.Signals[0].Evidence));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true));
        var config = Fixture.Configuration();

        var firstBytes = AlertCanonicalJson.SerializeEvaluation(engine.Evaluate(first, config));
        var secondBytes = AlertCanonicalJson.SerializeEvaluation(engine.Evaluate(second, config));

        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public void Evaluate_MissingCapability_SuppressesWithoutInvokingRule()
    {
        var rule = new TestRule(Fixture.Descriptor(required: ["token-usage"]), Fixture.Match(Fixture.Evidence()));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true));

        var result = engine.Evaluate(Fixture.Snapshot(capabilities: [new("token-usage", AlertCapabilityAvailability.Unknown)]), Fixture.Configuration());

        Assert.Empty(result.Receipts);
        var suppression = Assert.Single(result.Suppressions);
        Assert.Equal("missing_required_capability", suppression.Code);
        Assert.Equal(["token-usage"], suppression.MissingCapabilities);
        Assert.Equal(0, rule.InvocationCount);
    }

    [Fact]
    public void Evaluate_UnresolvedEvidence_RejectsReceiptWithoutLeakingResolverDetails()
    {
        var snapshot = Fixture.Snapshot(capabilities: [new("tool-events", AlertCapabilityAvailability.Available)]);
        var rule = new TestRule(Fixture.Descriptor(required: ["tool-events"]), Fixture.Match(snapshot.Signals[0].Evidence));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(false));

        var result = engine.Evaluate(snapshot, Fixture.Configuration());

        Assert.Empty(result.Receipts);
        Assert.Empty(result.Suppressions);
        Assert.Equal(new AlertRejectedMatch("test-rule", "1", "unresolved_evidence"), Assert.Single(result.RejectedMatches));
    }

    [Fact]
    public void Evaluate_DisabledAndSourceExcludedRules_ReturnBoundedSuppressions()
    {
        var snapshot = Fixture.Snapshot(capabilities: [new("tool-events", AlertCapabilityAvailability.Available)]);
        var disabled = new TestRule(Fixture.Descriptor("disabled-rule", required: ["tool-events"]), Fixture.Match(snapshot.Signals[0].Evidence));
        var excludedDescriptor = Fixture.Descriptor("excluded-rule", required: ["tool-events"]) with { ApplicableSourceSurfaces = ["claude-code"] };
        var excluded = new TestRule(excludedDescriptor, Fixture.Match(snapshot.Signals[0].Evidence));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([excluded, disabled]), new Resolver(true));
        var config = Fixture.Configuration([
            new("disabled-rule", "1", false, new Dictionary<string, decimal>(), null),
        ]);

        var result = engine.Evaluate(snapshot, config);

        Assert.Equal(["rule_disabled", "source_not_applicable"], result.Suppressions.Select(item => item.Code));
        Assert.Equal(0, disabled.InvocationCount);
        Assert.Equal(0, excluded.InvocationCount);
    }

    [Fact]
    public void Evaluate_InvalidThresholdRelationship_FailsClosed()
    {
        var descriptor = Fixture.Descriptor() with
        {
            Thresholds = [new("count", "calls", AlertThresholdDirection.HigherIsWorse, 0, 100, 10, 20)],
        };
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([new TestRule(descriptor, Fixture.Match(Fixture.Evidence()))]), new Resolver(true));
        var config = Fixture.Configuration([
            new("test-rule", "1", true, new Dictionary<string, decimal> { ["count.warning"] = 30, ["count.critical"] = 20 }, null),
        ]);

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(Fixture.Snapshot(), config));

        Assert.Equal("invalid_configuration", error.Code);
    }

    [Fact]
    public void Registry_DuplicateRuleVersion_FailsClosed()
    {
        var first = new TestRule(Fixture.Descriptor(), Fixture.Match(Fixture.Evidence()));
        var duplicate = new TestRule(Fixture.Descriptor(), Fixture.Match(Fixture.Evidence()));

        var error = Assert.Throws<AlertContractException>(() => new AlertRuleRegistry([first, duplicate]));

        Assert.Equal("invalid_rule_registry", error.Code);
    }

    [Fact]
    public void Evaluate_DuplicateConfiguration_FailsClosed()
    {
        var rule = new TestRule(Fixture.Descriptor(), Fixture.Match(Fixture.Evidence()));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true));
        var entry = new AlertRuleConfiguration("test-rule", "1", true, new Dictionary<string, decimal>(), null);

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration([entry, entry])));

        Assert.Equal("invalid_configuration", error.Code);
        Assert.Equal(0, rule.InvocationCount);
    }

    [Fact]
    public void Evaluate_DuplicateSnapshotEvidence_FailsClosed()
    {
        var snapshot = Fixture.Snapshot();
        var duplicate = snapshot.Signals[0] with { SignalId = "signal-2", Sequence = 2 };
        snapshot = snapshot with { Signals = [snapshot.Signals[0], duplicate] };
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([]), new Resolver(true));

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(snapshot, Fixture.Configuration()));

        Assert.Equal("invalid_snapshot", error.Code);
    }

    [Fact]
    public void Evaluate_HistoricalPartialReason_RemainsExplicitAndCanonical()
    {
        var snapshot = Fixture.Snapshot(
            capabilities: [new("tool-events", AlertCapabilityAvailability.Available)],
            reasons: ["historical_summary_only", "ingest_gap"]);
        var rule = new TestRule(Fixture.Descriptor(required: ["tool-events"]), Fixture.Match(snapshot.Signals[0].Evidence));
        var receipt = Assert.Single(new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true)).Evaluate(snapshot, Fixture.Configuration()).Receipts);

        Assert.Equal(AlertCompleteness.Partial, receipt.Completeness);
        Assert.Equal(["ingest_gap", "historical_summary_only"], receipt.CompletenessReasons);
        Assert.Equal(snapshot.Signals[0].Evidence, Assert.Single(receipt.Evidence));
    }

    [Fact]
    public void Evaluate_ResolverFailure_IsBoundedAsUnresolvedEvidence()
    {
        var snapshot = Fixture.Snapshot();
        var rule = new TestRule(Fixture.Descriptor(), Fixture.Match(snapshot.Signals[0].Evidence));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new ThrowingResolver());

        var rejected = Assert.Single(engine.Evaluate(snapshot, Fixture.Configuration()).RejectedMatches);

        Assert.Equal("unresolved_evidence", rejected.Code);
    }

    [Fact]
    public void Registry_FreezesDescriptorCollectionsAtRegistration()
    {
        var required = new List<string> { "token-usage" };
        var rule = new TestRule(Fixture.Descriptor(required: required), Fixture.Match(Fixture.Evidence()));
        var registry = new AlertRuleRegistry([rule]);
        required.Clear();

        var result = new AlertEvaluationEngine(registry, new Resolver(true)).Evaluate(Fixture.Snapshot(capabilities: []), Fixture.Configuration());

        Assert.Equal(["token-usage"], Assert.Single(result.Suppressions).MissingCapabilities);
        Assert.Equal(0, rule.InvocationCount);
    }

    [Fact]
    public void Evaluate_EquivalentConfigurationOrdering_ProducesIdenticalConfigHashAndBytes()
    {
        var firstRule = new TestRule(Fixture.Descriptor("a-rule"), Fixture.Match(Fixture.Evidence()));
        var secondRule = new TestRule(Fixture.Descriptor("b-rule"), Fixture.Match(Fixture.Evidence()));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([secondRule, firstRule]), new Resolver(true));
        var firstEntry = new AlertRuleConfiguration("a-rule", "1", false, new Dictionary<string, decimal>(), null);
        var secondEntry = new AlertRuleConfiguration("b-rule", "1", false, new Dictionary<string, decimal>(), null);

        var first = engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration([firstEntry, secondEntry]));
        var second = engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration([secondEntry, firstEntry]));

        Assert.Equal(first.ConfigurationHash, second.ConfigurationHash);
        Assert.Equal(AlertCanonicalJson.SerializeEvaluation(first), AlertCanonicalJson.SerializeEvaluation(second));
    }

    [Fact]
    public void Evaluate_RawSensitiveComparableKey_FailsClosed()
    {
        var snapshot = Fixture.Snapshot();
        snapshot = snapshot with
        {
            Signals = [snapshot.Signals[0] with { ComparableKeys = [new("argument", AlertComparableKeyKind.SensitiveHmac, "true")] }],
        };

        var error = Assert.Throws<AlertContractException>(() => new AlertEvaluationEngine(new AlertRuleRegistry([]), new Resolver(true)).Evaluate(snapshot, Fixture.Configuration()));

        Assert.Equal("invalid_snapshot", error.Code);
    }

    [Fact]
    public void Evaluate_HmacComparableKey_IsAcceptedButNeverCopiedToReceipt()
    {
        var snapshot = Fixture.Snapshot();
        var label = AlertSensitiveValueHasher.Hash(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(), "session-1", "argument", "true");
        snapshot = snapshot with
        {
            Signals = [snapshot.Signals[0] with { ComparableKeys = [new("argument", AlertComparableKeyKind.SensitiveHmac, label)] }],
        };
        var rule = new TestRule(Fixture.Descriptor(), Fixture.Match(snapshot.Signals[0].Evidence));

        var receipt = Assert.Single(new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true)).Evaluate(snapshot, Fixture.Configuration()).Receipts);

        Assert.DoesNotContain(label, Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(receipt)), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_StructurallyInvalidRuleMatch_FailsWithBoundedCode()
    {
        var invalid = Fixture.Match(Fixture.Evidence()) with { ObservedValues = [] };
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([new TestRule(Fixture.Descriptor(), invalid)]), new Resolver(true));

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration()));

        Assert.Equal("invalid_rule_output", error.Code);
    }

    [Fact]
    public void CanonicalSerializer_NormalizesCallerCollectionOrdering()
    {
        var first = new AlertSuppression(new string('e', 64), "a-rule", "1", "missing_required_capability", ["z-capability", "a-capability"]);
        var second = new AlertSuppression(new string('e', 64), "b-rule", "1", "rule_disabled", []);
        var forward = new AlertEvaluationResult(AlertContractVersions.Evaluation, new string('e', 64), new string('b', 64), "fixture-v1", new string('c', 64), [], [first, second], []);
        var reversed = forward with { Suppressions = [second, first with { MissingCapabilities = ["a-capability", "z-capability"] }] };

        Assert.Equal(AlertCanonicalJson.SerializeEvaluation(forward), AlertCanonicalJson.SerializeEvaluation(reversed));
    }

    [Fact]
    public void Evaluate_EquivalentDecimalScales_ProduceIdenticalHashesAndCanonicalBytes()
    {
        var firstSnapshot = Fixture.Snapshot() with
        {
            Signals = [Fixture.Snapshot().Signals[0] with { Metrics = [new("count", "calls", 1.0m)] }],
        };
        var secondSnapshot = firstSnapshot with
        {
            Signals = [firstSnapshot.Signals[0] with { Metrics = [new("count", "calls", 1.00m)] }],
        };
        var descriptor = Fixture.Descriptor() with
        {
            Thresholds = [new("count", "calls", AlertThresholdDirection.HigherIsWorse, 0m, 10m, 1m, 2m)],
        };
        var firstConfig = Fixture.Configuration([new("test-rule", "1", true, new Dictionary<string, decimal> { ["count.warning"] = 1.0m }, null)]);
        var secondConfig = Fixture.Configuration([new("test-rule", "1", true, new Dictionary<string, decimal> { ["count.warning"] = 1.00m }, null)]);
        var firstEngine = new AlertEvaluationEngine(new AlertRuleRegistry([new TestRule(descriptor, Fixture.Match(firstSnapshot.Signals[0].Evidence) with { ObservedValues = [new("count", "calls", 1.0m)] })]), new Resolver(true));
        var secondEngine = new AlertEvaluationEngine(new AlertRuleRegistry([new TestRule(descriptor, Fixture.Match(secondSnapshot.Signals[0].Evidence) with { ObservedValues = [new("count", "calls", 1.00m)] })]), new Resolver(true));

        var first = firstEngine.Evaluate(firstSnapshot, firstConfig);
        var second = secondEngine.Evaluate(secondSnapshot, secondConfig);

        Assert.Equal(first.InputHash, second.InputHash);
        Assert.Equal(first.ConfigurationHash, second.ConfigurationHash);
        Assert.Equal(AlertCanonicalJson.SerializeEvaluation(first), AlertCanonicalJson.SerializeEvaluation(second));
    }

    [Fact]
    public void Evaluate_ZeroMatchesWithDeclaredRuleSuppression_EmitsOneBoundedSuppression()
    {
        var descriptor = Fixture.Descriptor() with
        {
            SuppressionCodes = ["missing_required_capability", "rule_disabled", "source_not_applicable", "minimum_sample_unmet"],
        };
        var rule = new OutcomeRule(descriptor, new([], [new("minimum_sample_unmet"), new("minimum_sample_unmet")]));

        var result = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true)).Evaluate(Fixture.Snapshot(), Fixture.Configuration());

        Assert.Empty(result.Receipts);
        var suppression = Assert.Single(result.Suppressions);
        Assert.Equal("minimum_sample_unmet", suppression.Code);
        Assert.Empty(suppression.MissingCapabilities);
        Assert.Equal(1, rule.InvocationCount);
    }

    [Fact]
    public void Evaluate_UndeclaredRuleSuppression_FailsWithBoundedCode()
    {
        var rule = new OutcomeRule(Fixture.Descriptor(), new([], [new("private-path-c-users-secret")]));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true));

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration()));

        Assert.Equal("invalid_rule_output", error.Code);
        Assert.DoesNotContain("private-path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RuleCannotEmitEngineOwnedSuppressionCode()
    {
        var rule = new OutcomeRule(Fixture.Descriptor(), new([], [new("missing_required_capability")]));
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true));

        var error = Assert.Throws<AlertContractException>(() => engine.Evaluate(Fixture.Snapshot(), Fixture.Configuration()));

        Assert.Equal("invalid_rule_output", error.Code);
    }

    [Fact]
    public void CanonicalReceipt_ContainsOnlySanitizedRegisteredContent()
    {
        var snapshot = Fixture.Snapshot(capabilities: [new("tool-events", AlertCapabilityAvailability.Available)]);
        var rule = new TestRule(Fixture.Descriptor(required: ["tool-events"]), Fixture.Match(snapshot.Signals[0].Evidence));
        var receipt = Assert.Single(new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true)).Evaluate(snapshot, Fixture.Configuration()).Receipts);

        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(receipt));

        Assert.Contains("sanitized-alert-receipt.v1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\\\Users", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestRule(AlertRuleDescriptor descriptor, AlertRuleMatch match) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public int InvocationCount { get; private set; }

        public AlertRuleOutcome Evaluate(AlertRuleContext context)
        {
            InvocationCount++;
            return new([match], []);
        }
    }

    private sealed class OutcomeRule(AlertRuleDescriptor descriptor, AlertRuleOutcome outcome) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public int InvocationCount { get; private set; }

        public AlertRuleOutcome Evaluate(AlertRuleContext context)
        {
            InvocationCount++;
            return outcome;
        }
    }

    private sealed class Resolver(bool exists) : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => exists;
    }

    private sealed class ThrowingResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => throw new InvalidOperationException("private path: C:\\\\Users\\secret");
    }

    private static class Fixture
    {
        private static readonly DateTimeOffset ObservedAt = new(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);

        public static AlertEvidenceReference Evidence() =>
            new(AlertEvidenceKind.ToolCall, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", "tool-call-1", ObservedAt);

        public static AlertNormalizedSnapshot Snapshot(
            IReadOnlyList<AlertCapabilityFact>? capabilities = null,
            IReadOnlyList<string>? reasons = null)
        {
            var evidence = Evidence();
            return new(
                AlertContractVersions.Snapshot,
                "github-copilot",
                "1.2.3",
                "session-1",
                "trace-1",
                AlertCompleteness.Partial,
                reasons ?? ["ingest_gap"],
                ObservedAt,
                ObservedAt.AddSeconds(1),
                capabilities ?? [new("tool-events", AlertCapabilityAvailability.Available)],
                [new("signal-1", AlertSignalKind.ToolCall, 1, ObservedAt, null, AlertSignalStatus.Error, [new("duration", "milliseconds", 12)], [], evidence)]);
        }

        public static AlertRuleDescriptor Descriptor(string ruleId = "test-rule", IReadOnlyList<string>? required = null) =>
            new(
                ruleId,
                "1",
                "Registered summary",
                "Registered description",
                required ?? [],
                AlertRuleScope.Session,
                [],
                "session",
                [],
                ["missing_required_capability", "rule_disabled", "source_not_applicable"],
                ["github-copilot", "claude-code"]);

        public static AlertRuleMatch Match(AlertEvidenceReference evidence) =>
            new(AlertSeverity.Warning, [new("count", "calls", 1)], [evidence], ObservedAt, ObservedAt.AddSeconds(1));

        public static AlertEngineConfiguration Configuration(IReadOnlyList<AlertRuleConfiguration>? rules = null) =>
            new(AlertContractVersions.Configuration, "fixture-v1", rules ?? []);
    }
}

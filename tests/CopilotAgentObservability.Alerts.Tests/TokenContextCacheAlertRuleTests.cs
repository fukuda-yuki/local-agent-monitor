using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class TokenContextCacheAlertRuleTests
{
    [Fact]
    public void Registry_FreezesFiveVersionOneRulesAndStableThresholds()
    {
        var descriptors = TokenContextCacheAlertRulePack.CreateRules().Select(rule => rule.Descriptor).ToArray();

        Assert.Equal(
            ["context-growth-with-output-collapse", "high-initial-context-utilization", "low-cache-read-ratio", "monotonic-context-growth", "near-context-limit-turn"],
            descriptors.Select(item => item.RuleId).Order(StringComparer.Ordinal));
        Assert.All(descriptors, descriptor => Assert.Equal("1", descriptor.RuleVersion));
        Assert.Equal((0.50m, 0.80m), Defaults(descriptors, "high-initial-context-utilization", "initial-context-utilization"));
        Assert.Equal((1.75m, 2.50m), Defaults(descriptors, "monotonic-context-growth", "context-growth-ratio"));
        Assert.Equal((1.50m, 2.00m), Defaults(descriptors, "context-growth-with-output-collapse", "context-half-growth-ratio"));
        Assert.Equal((0.50m, 0.30m), Defaults(descriptors, "context-growth-with-output-collapse", "output-input-collapse-ratio"));
        Assert.Equal((0.20m, 0.05m), Defaults(descriptors, "low-cache-read-ratio", "cache-read-ratio"));
        Assert.Equal((0.75m, 0.90m), Defaults(descriptors, "near-context-limit-turn", "context-limit-utilization"));
    }

    [Theory]
    [InlineData(49, null)]
    [InlineData(50, AlertSeverity.Warning)]
    [InlineData(80, AlertSeverity.Critical)]
    public void HighInitialContextUtilization_UsesInclusiveBoundaries(int input, AlertSeverity? expected)
    {
        var result = Evaluate("high-initial-context-utilization", [Turn(1, input: input, limit: 100)]);

        AssertSeverity(result, expected);
    }

    [Fact]
    public void HighInitialContextUtilization_SplitsModelAndLimitRevisionGroups()
    {
        var result = Evaluate("high-initial-context-utilization",
        [
            Turn(1, input: 10, limit: 100, model: "model-a", limitVersion: "v1"),
            Turn(2, input: 80, limit: 100, model: "model-b", limitVersion: "v1"),
            Turn(3, input: 90, limit: 100, model: "model-a", limitVersion: "v2"),
        ]);

        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, receipt => Assert.Single(receipt.Evidence));
        Assert.Equal(["turn-2", "turn-3"], result.Receipts.Select(receipt => receipt.Evidence.Single().TurnId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HighInitialContextUtilization_DoesNotReplaceMissingFirstInputWithALaterTurn()
    {
        var result = Evaluate("high-initial-context-utilization", [Turn(1, input: null, limit: 100), Turn(2, input: 90, limit: 100)]);

        Assert.Empty(result.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void LimitRules_UnknownCapabilityAndMissingMetricNeverBecomeZero()
    {
        var unavailable = Evaluate("near-context-limit-turn", [Turn(1, input: 95, limit: 100)],
            capabilityOverrides: new Dictionary<string, AlertCapabilityAvailability> { ["effective-context-limit"] = AlertCapabilityAvailability.Unknown });
        var missing = Evaluate("near-context-limit-turn", [Turn(1, input: 95, limit: null)]);

        Assert.Equal("missing_required_capability", Assert.Single(unavailable.Suppressions).Code);
        Assert.Equal(["effective-context-limit"], unavailable.Suppressions.Single().MissingCapabilities);
        Assert.Empty(missing.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(missing.Suppressions).Code);
    }

    [Theory]
    [InlineData(74, null)]
    [InlineData(75, AlertSeverity.Warning)]
    [InlineData(90, AlertSeverity.Critical)]
    public void NearContextLimitTurn_EvaluatesEachExactTurnAtInclusiveBoundaries(int input, AlertSeverity? expected)
    {
        var result = Evaluate("near-context-limit-turn", [Turn(1, input: input, limit: 100)]);

        AssertSeverity(result, expected);
        if (expected is not null) Assert.Equal("turn-1", Assert.Single(result.Receipts).Evidence.Single().TurnId);
    }

    [Theory]
    [InlineData(174, null)]
    [InlineData(175, AlertSeverity.Warning)]
    [InlineData(250, AlertSeverity.Critical)]
    public void MonotonicContextGrowth_RequiresThreeContiguousTurnsAndInclusiveRatio(int last, AlertSeverity? expected)
    {
        var result = Evaluate("monotonic-context-growth", [Turn(1, input: 100), Turn(2, input: 100), Turn(3, input: last)]);

        AssertSeverity(result, expected);
    }

    [Fact]
    public void MonotonicContextGrowth_DoesNotCrossDecreaseOrToolSchemaChange()
    {
        var decrease = Evaluate("monotonic-context-growth",
            [Turn(1, input: 100), Turn(2, input: 200), Turn(3, input: 90), Turn(4, input: 250)]);
        var schemaChange = Evaluate("monotonic-context-growth",
            [Turn(1, input: 100, toolSchema: "schema-a"), Turn(2, input: 150, toolSchema: "schema-a"), Turn(3, input: 250, toolSchema: "schema-b")]);

        Assert.Empty(decrease.Receipts);
        Assert.Empty(schemaChange.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(schemaChange.Suppressions).Code);
    }

    [Fact]
    public void MonotonicContextGrowth_TwoTurnsCannotMeetMinimumSample()
    {
        var result = Evaluate("monotonic-context-growth", [Turn(1, input: 100), Turn(2, input: 300)]);

        Assert.Empty(result.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void OutputCollapse_UsesEvenHalfMediansAtWarningBoundary()
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100), Turn(2, input: 100, output: 100),
            Turn(3, input: 150, output: 75), Turn(4, input: 150, output: 75),
        ]);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(1.50m, Observed(receipt, "context-half-growth-ratio"));
        Assert.Equal(0.50m, Observed(receipt, "output-input-collapse-ratio"));
    }

    [Fact]
    public void OutputCollapse_OmitsOddMiddleAndUsesMedianOfPerTurnRatios()
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100), Turn(2, input: 300, output: 300),
            Turn(3, input: 9999, output: 9999),
            Turn(4, input: 300, output: 60), Turn(5, input: 300, output: 60),
        ]);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(4m, Observed(receipt, "included-turn-count"));
        Assert.Equal(1m, Observed(receipt, "excluded-turn-count"));
        Assert.DoesNotContain(receipt.Evidence, item => item.TurnId == "turn-3");
        Assert.Equal(0.20m, Observed(receipt, "output-input-collapse-ratio"));
    }

    [Fact]
    public void OutputCollapse_ObservedZeroIsEligibleButMissingAndFailedCallsAreExcluded()
    {
        var zero = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100), Turn(2, input: 100, output: 100),
            Turn(3, input: 200, output: 0), Turn(4, input: 200, output: 0),
        ]);
        var excluded = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100),
            Turn(2, input: 100, output: null),
            Turn(3, input: 200, output: 10, status: AlertSignalStatus.Error),
            Turn(4, input: 200, output: 10, status: AlertSignalStatus.Cancelled),
            Turn(5, input: 200, output: 10, status: AlertSignalStatus.Unknown),
            Turn(6, input: 200, output: 10),
        ]);

        Assert.Equal(AlertSeverity.Critical, Assert.Single(zero.Receipts).Severity);
        Assert.Empty(excluded.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(excluded.Suppressions).Code);
    }

    [Fact]
    public void OutputCollapse_UsesInclusiveCriticalBoundaries()
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100), Turn(2, input: 100, output: 100),
            Turn(3, input: 200, output: 60), Turn(4, input: 200, output: 60),
        ]);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(2.00m, Observed(receipt, "context-half-growth-ratio"));
        Assert.Equal(0.30m, Observed(receipt, "output-input-collapse-ratio"));
    }

    [Fact]
    public void OutputCollapse_EvaluatesWhenFourTurnsRemainAfterMissingAndCancelledExclusions()
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100),
            Turn(2, input: 100, output: null),
            Turn(3, input: 100, output: 100),
            Turn(4, input: 200, output: 0, status: AlertSignalStatus.Cancelled),
            Turn(5, input: 200, output: 0),
            Turn(6, input: 200, output: 0),
        ]);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(4m, Observed(receipt, "eligible-turn-count"));
        Assert.Equal(2m, Observed(receipt, "excluded-turn-count"));
    }

    [Theory]
    [InlineData("model-b", "tool-schema-a")]
    [InlineData("model-a", "tool-schema-b")]
    public void OutputCollapse_DoesNotCombineModelOrToolSchemaGroups(string laterModel, string laterSchema)
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 100), Turn(2, input: 100, output: 100),
            Turn(3, input: 200, output: 0, model: laterModel, toolSchema: laterSchema),
            Turn(4, input: 200, output: 0, model: laterModel, toolSchema: laterSchema),
        ]);

        Assert.Empty(result.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void OutputCollapse_HighInputWithStablePerTurnRatioDoesNotAlert()
    {
        var result = Evaluate("context-growth-with-output-collapse",
        [
            Turn(1, input: 100, output: 50), Turn(2, input: 100, output: 50),
            Turn(3, input: 250, output: 125), Turn(4, input: 250, output: 125),
        ]);

        Assert.Empty(result.Receipts);
    }

    [Theory]
    [InlineData(2000, null)]
    [InlineData(1990, AlertSeverity.Warning)]
    [InlineData(500, AlertSeverity.Warning)]
    [InlineData(490, AlertSeverity.Critical)]
    [InlineData(0, AlertSeverity.Critical)]
    public void LowCacheReadRatio_ExcludesFirstAndUsesStrictBoundaries(int includedCache, AlertSeverity? expected)
    {
        var result = Evaluate("low-cache-read-ratio",
        [
            Turn(1, input: 5000, cache: 5000),
            Turn(2, input: 5000, cache: includedCache / 2),
            Turn(3, input: 5000, cache: includedCache - includedCache / 2),
        ]);

        AssertSeverity(result, expected);
        if (expected is not null)
        {
            var receipt = Assert.Single(result.Receipts);
            Assert.DoesNotContain(receipt.Evidence, item => item.TurnId == "turn-1");
            Assert.Equal(2m, Observed(receipt, "included-turn-count"));
            Assert.Equal(1m, Observed(receipt, "excluded-turn-count"));
        }
    }

    [Fact]
    public void LowCacheReadRatio_DistinguishesUnsupportedMissingAndObservedZero()
    {
        var unsupported = Evaluate("low-cache-read-ratio", [Turn(1, input: 1, cache: 0)],
            capabilityOverrides: new Dictionary<string, AlertCapabilityAvailability> { ["cache-read-token-count"] = AlertCapabilityAvailability.Unavailable });
        var missing = Evaluate("low-cache-read-ratio",
            [Turn(1, input: 5000, cache: 0), Turn(2, input: 5000, cache: null), Turn(3, input: 5000, cache: 0)]);
        var zero = Evaluate("low-cache-read-ratio",
            [Turn(1, input: 1, cache: 0), Turn(2, input: 5000, cache: 0), Turn(3, input: 5000, cache: 0)]);

        Assert.Equal("missing_required_capability", Assert.Single(unsupported.Suppressions).Code);
        Assert.Equal("minimum-sample-unmet", Assert.Single(missing.Suppressions).Code);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(zero.Receipts).Severity);
    }

    [Theory]
    [InlineData(4999, "minimum-sample-unmet")]
    [InlineData(5000, null)]
    public void LowCacheReadRatio_RequiresTenThousandPostExclusionInput(int eachIncludedInput, string? suppression)
    {
        var result = Evaluate("low-cache-read-ratio",
            [Turn(1, input: 1, cache: 0), Turn(2, input: eachIncludedInput, cache: 0), Turn(3, input: eachIncludedInput, cache: 0)]);

        if (suppression is null) Assert.Equal(AlertSeverity.Critical, Assert.Single(result.Receipts).Severity);
        else Assert.Equal(suppression, Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData(AlertCompleteness.Partial, false, "incomplete-snapshot")]
    [InlineData(AlertCompleteness.Rich, true, "historical-input")]
    public void Rules_SuppressPartialAndHistoricalSnapshots(AlertCompleteness completeness, bool historical, string expectedCode)
    {
        var result = Evaluate("near-context-limit-turn", [Turn(1, input: 95, limit: 100)], completeness,
            historical ? ["historical_summary_only"] : ["ingest_gap"]);

        Assert.Empty(result.Receipts);
        Assert.Equal(expectedCode, Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void CurrentRealSourceFixtures_RemainCapabilitySuppressed(string sourceSurface)
    {
        var result = Evaluate("low-cache-read-ratio", [Turn(1, input: 5000, cache: 0), Turn(2, input: 5000, cache: 0), Turn(3, input: 5000, cache: 0)],
            sourceSurface: sourceSurface,
            capabilityOverrides: new Dictionary<string, AlertCapabilityAvailability>
            {
                ["input-token-count"] = AlertCapabilityAvailability.Unknown,
                ["cache-read-token-count"] = AlertCapabilityAvailability.Unknown,
            });

        Assert.Empty(result.Receipts);
        Assert.Equal(["cache-read-token-count", "input-token-count"], Assert.Single(result.Suppressions).MissingCapabilities);
    }

    [Fact]
    public void Receipts_UseExactEvidenceRemainPrivateAndAreDeterministic()
    {
        var turns = new[] { Turn(1, input: 100, model: "private-model-a"), Turn(2, input: 175, model: "private-model-a"), Turn(3, input: 250, model: "private-model-a") };
        var first = Evaluate("monotonic-context-growth", turns);
        var second = Evaluate("monotonic-context-growth", turns.Reverse().ToArray());
        var receipt = Assert.Single(first.Receipts);
        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeReceipt(receipt));

        Assert.Equal(["turn-1", "turn-2", "turn-3"], receipt.Evidence.Select(item => item.TurnId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("private-model-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tool-schema-a", json, StringComparison.Ordinal);
        Assert.Equal(AlertCanonicalJson.SerializeEvaluation(first), AlertCanonicalJson.SerializeEvaluation(second));
    }

    private static (decimal Warning, decimal Critical) Defaults(IEnumerable<AlertRuleDescriptor> descriptors, string ruleId, string threshold)
    {
        var definition = descriptors.Single(item => item.RuleId == ruleId).Thresholds.Single(item => item.Name == threshold);
        return (definition.WarningDefault, definition.CriticalDefault);
    }

    private static decimal Observed(AlertReceipt receipt, string name) => receipt.ObservedValues.Single(item => item.Name == name).Value;

    private static void AssertSeverity(AlertEvaluationResult result, AlertSeverity? expected)
    {
        if (expected is null) Assert.Empty(result.Receipts);
        else Assert.Equal(expected, Assert.Single(result.Receipts).Severity);
    }

    private static AlertEvaluationResult Evaluate(
        string ruleId,
        IReadOnlyList<AlertSignal> signals,
        AlertCompleteness completeness = AlertCompleteness.Rich,
        IReadOnlyList<string>? reasons = null,
        string sourceSurface = "github-copilot-vscode",
        IReadOnlyDictionary<string, AlertCapabilityAvailability>? capabilityOverrides = null)
    {
        var rule = TokenContextCacheAlertRulePack.CreateRules().Single(item => item.Descriptor.RuleId == ruleId);
        var capabilities = rule.Descriptor.RequiredCapabilities
            .Select(name => new AlertCapabilityFact(name, capabilityOverrides?.GetValueOrDefault(name, AlertCapabilityAvailability.Available) ?? AlertCapabilityAvailability.Available))
            .ToArray();
        var ordered = signals.OrderBy(item => item.ObservedAt).ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            sourceSurface,
            "fixture-v1",
            "session-1",
            "trace-1",
            completeness,
            reasons ?? [],
            ordered.Min(item => item.ObservedAt),
            ordered.Max(item => item.ObservedAt),
            capabilities,
            signals);
        var engine = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new ExistsResolver());
        return engine.Evaluate(snapshot, new(AlertContractVersions.Configuration, "token-rules-v1", []));
    }

    private static AlertSignal Turn(
        int sequence,
        decimal? input = null,
        decimal? output = null,
        decimal? cache = null,
        decimal? limit = null,
        string model = "model-a",
        string toolSchema = "tool-schema-a",
        string limitVersion = "limit-v1",
        AlertSignalStatus status = AlertSignalStatus.Success)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero).AddSeconds(sequence);
        var metrics = new List<AlertMetric>();
        if (input is not null) metrics.Add(new("input-tokens", "tokens", input.Value));
        if (output is not null) metrics.Add(new("output-tokens", "tokens", output.Value));
        if (cache is not null) metrics.Add(new("cache-read-tokens", "tokens", cache.Value));
        if (limit is not null) metrics.Add(new("effective-context-limit", "tokens", limit.Value));
        var keys = new[]
        {
            new AlertComparableKey("model-id", AlertComparableKeyKind.MetadataToken, model),
            new AlertComparableKey("input-token-semantics-version", AlertComparableKeyKind.MetadataToken, "input-v1"),
            new AlertComparableKey("output-token-semantics-version", AlertComparableKeyKind.MetadataToken, "output-v1"),
            new AlertComparableKey("cache-read-token-semantics-version", AlertComparableKeyKind.MetadataToken, "cache-v1"),
            new AlertComparableKey("tool-schema-generation", AlertComparableKeyKind.MetadataToken, toolSchema),
            new AlertComparableKey("effective-context-limit-authority", AlertComparableKeyKind.MetadataToken, "authority-v1"),
            new AlertComparableKey("effective-context-limit-version", AlertComparableKeyKind.MetadataToken, limitVersion),
        };
        var id = $"turn-{sequence}";
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Turn, $"evidence-{sequence}", "session-1", "trace-1", $"span-{sequence}", id, null, null, observedAt);
        return new(id, AlertSignalKind.LlmCall, sequence, observedAt, null, status, metrics, keys, evidence);
    }

    private sealed class ExistsResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }
}

using System.Text;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class ToolAlertRuleTests
{
    [Fact]
    public void RulePack_Descriptors_FreezeRegistryHandoffContract()
    {
        var registry = new AlertRuleRegistry(ToolAlertRulePack.CreateRules());

        Assert.Equal(
        [
            "excessive-permission-wait",
            "high-tool-failure-ratio",
            "repeated-file-read-or-search",
            "repeated-identical-tool-call",
            "unrecovered-retry-chain",
        ], registry.Rules.Select(rule => rule.Descriptor.RuleId));
        Assert.All(registry.Rules, rule => Assert.Equal("1", rule.Descriptor.RuleVersion));
        Assert.All(registry.Rules, rule => Assert.Equal(
            ["claude-code", "codex-app", "codex-cli", "github-copilot-cli", "github-copilot-vscode"],
            rule.Descriptor.ApplicableSourceSurfaces));

        var repeated = Descriptor(registry, "repeated-identical-tool-call");
        Assert.Equal(["canonical-tool-arguments", "explicit-retry-classification", "stable-tool-ordering", "tool-name", "tool-ownership"], repeated.RequiredCapabilities);
        Assert.Equal(["argument-hash", "ownership-key", "tool-name"], repeated.GroupingKeys);
        AssertThreshold(repeated, "identical-call-count", "calls", 1, 100_000, 3, 5);

        var retry = Descriptor(registry, "unrecovered-retry-chain");
        Assert.Equal(["terminal-run-status", "tool-call-key", "tool-call-ordering", "tool-call-status", "tool-retry-attempt"], retry.RequiredCapabilities);
        Assert.Equal(["tool-key", "retry-chain-key"], retry.GroupingKeys);
        AssertThreshold(retry, "retry-chain-length", "attempts", 2, 100_000, 2, 3);

        var ratio = Descriptor(registry, "high-tool-failure-ratio");
        Assert.Equal(["tool-call-status"], ratio.RequiredCapabilities);
        AssertThreshold(ratio, "failure-ratio", "ratio", 0, 1, 0.40m, 0.70m);

        var permission = Descriptor(registry, "excessive-permission-wait");
        Assert.Equal(["explicit-permission-duration"], permission.RequiredCapabilities);
        AssertThreshold(permission, "individual-wait", "seconds", 0, 86_400, 30, 120);
        AssertThreshold(permission, "total-wait", "seconds", 0, 604_800, 60, 300);

        var file = Descriptor(registry, "repeated-file-read-or-search");
        Assert.Equal(["file-access-key", "file-access-ordering", "file-access-ownership", "file-operation-type"], file.RequiredCapabilities);
        Assert.Equal(["file-key", "operation-type", "ownership-key", "range-key"], file.GroupingKeys);
        AssertThreshold(file, "access-count", "accesses", 1, 100_000, 3, 5);
        Assert.All(registry.Rules, rule => Assert.Contains("trace-scope-unavailable", rule.Descriptor.SuppressionCodes));
    }

    [Theory]
    [InlineData("repeated-identical-tool-call")]
    [InlineData("unrecovered-retry-chain")]
    [InlineData("high-tool-failure-ratio")]
    [InlineData("excessive-permission-wait")]
    [InlineData("repeated-file-read-or-search")]
    public void TraceScopedRule_MissingTraceId_SuppressesWithoutMatch(string ruleId)
    {
        var rule = CreateRule(ruleId);
        var signals = ruleId switch
        {
            "repeated-identical-tool-call" => RepeatedToolCalls(3),
            "unrecovered-retry-chain" => RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error),
            "high-tool-failure-ratio" => StatusCalls(5, 0),
            "excessive-permission-wait" => [Permission(1, 30)],
            "repeated-file-read-or-search" => FileAccesses(3),
            _ => throw new InvalidOperationException(),
        };
        var snapshot = WithoutTrace(Snapshot(signals: signals));

        var result = Evaluate(rule, snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("trace-scope-unavailable", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void RepeatedIdenticalToolCall_SourceNeutralBoundaryFixtures_EmitExpectedSeverity(string source)
    {
        var warning = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(source, RepeatedToolCalls(3)));
        var critical = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(source, RepeatedToolCalls(5)));

        Assert.Equal(AlertSeverity.Warning, Assert.Single(warning.Receipts).Severity);
        Assert.Equal(3m, Observed(Assert.Single(warning.Receipts), "identical-call-count"));
        Assert.Equal(AlertSeverity.Critical, Assert.Single(critical.Receipts).Severity);
        Assert.Equal(5, Assert.Single(critical.Receipts).Evidence.Count);
    }

    [Fact]
    public void RepeatedIdenticalToolCall_BelowBoundary_DoesNotMatch()
    {
        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(signals: RepeatedToolCalls(2)));

        Assert.Empty(result.Receipts);
    }

    [Fact]
    public void RepeatedIdenticalToolCall_DifferentArgumentsExplicitRetriesAndParallelOwners_DoNotCombine()
    {
        var signals = new[]
        {
            Tool(1, tool: Hmac('a'), owner: "branch-a"),
            Tool(2, tool: Hmac('b'), owner: "branch-a"),
            Tool(3, tool: Hmac('a'), owner: "branch-a", retryKind: "explicit"),
            Tool(4, tool: Hmac('a'), owner: "branch-b"),
            Tool(5, tool: Hmac('a'), owner: "branch-b"),
        };

        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
    }

    [Fact]
    public void RepeatedIdenticalToolCall_PartialHistoryWithExactPositiveEvidence_StillMatches()
    {
        var snapshot = Snapshot(signals: RepeatedToolCalls(3), reasons: ["historical_summary_only", "ingest_gap"]);

        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), snapshot);

        Assert.Single(result.Receipts);
        Assert.Empty(result.Suppressions);
    }

    [Fact]
    public void RepeatedIdenticalToolCall_PartialHistoryBelowThreshold_ReportsIncompleteFacts()
    {
        var snapshot = Snapshot(signals: RepeatedToolCalls(2), reasons: ["ingest_gap"]);

        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void RepeatedIdenticalToolCall_MissingSignalFact_UsesBoundedSuppression()
    {
        var signals = RepeatedToolCalls(3).ToArray();
        signals[2] = signals[2] with { ComparableKeys = signals[2].ComparableKeys.Where(key => key.Name != "ownership-key").ToArray() };

        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void UnrecoveredRetryChain_SourceNeutralPositiveFixtures_RespectLengthBoundaries(string source)
    {
        var warning = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(source, RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error)));
        var critical = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(source, RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error, AlertSignalStatus.Error)));

        Assert.Equal(AlertSeverity.Warning, Assert.Single(warning.Receipts).Severity);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(critical.Receipts).Severity);
    }

    [Fact]
    public void UnrecoveredRetryChain_RecoveredRetry_DoesNotAlert()
    {
        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Success)));

        Assert.Empty(result.Receipts);
        Assert.Empty(result.Suppressions);
    }

    [Fact]
    public void RulePack_ExplicitRetryChain_IsOwnedOnlyByRetryRule()
    {
        var result = Evaluate(
            ToolAlertRulePack.CreateRules(),
            Snapshot(signals: RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error, AlertSignalStatus.Error)));

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("unrecovered-retry-chain", receipt.RuleId);
    }

    [Theory]
    [InlineData(AlertSignalStatus.Unknown)]
    [InlineData(AlertSignalStatus.Cancelled)]
    public void UnrecoveredRetryChain_UnknownOrCancelledTerminal_NeverBecomesFailure(AlertSignalStatus terminal)
    {
        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: RetryChain(AlertSignalStatus.Error, terminal)));

        Assert.Empty(result.Receipts);
        Assert.Equal("unknown-terminal-status", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void UnrecoveredRetryChain_ExactLinkedTerminalRunFailure_RaisesCritical()
    {
        var chain = RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error).ToList();
        chain.Add(SessionEvent(3, AlertSignalStatus.Error, chain[^1].SignalId, terminalRunStatus: true));

        var receipt = Assert.Single(Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain)).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(3, receipt.Evidence.Count);
    }

    [Fact]
    public void UnrecoveredRetryChain_ExactLinkedRunFailure_RemainsCriticalWhenLengthThresholdIsRaised()
    {
        var rule = new UnrecoveredRetryChainAlertRule();
        var chain = RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error).ToList();
        chain.Add(SessionEvent(3, AlertSignalStatus.Error, chain[^1].SignalId, terminalRunStatus: true));
        var configuration = new AlertEngineConfiguration(
            AlertContractVersions.Configuration,
            "raised-retry-threshold-v1",
            [new(rule.Descriptor.RuleId, rule.Descriptor.RuleVersion, true, new Dictionary<string, decimal>
            {
                ["retry-chain-length.warning"] = 4,
                ["retry-chain-length.critical"] = 5,
            }, null)]);

        var result = new AlertEvaluationEngine(new AlertRuleRegistry([rule]), new Resolver(true)).Evaluate(Snapshot(signals: chain), configuration);

        Assert.Equal(AlertSeverity.Critical, Assert.Single(result.Receipts).Severity);
    }

    [Fact]
    public void UnrecoveredRetryChain_GenericLinkedError_DoesNotEscalateAsTerminalRunFailure()
    {
        var chain = RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error).ToList();
        chain.Add(SessionEvent(3, AlertSignalStatus.Error, chain[^1].SignalId));

        var receipt = Assert.Single(Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain)).Receipts);

        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(2, receipt.Evidence.Count);
    }

    [Fact]
    public void UnrecoveredRetryChain_MissingTerminalRunStatusCapability_SuppressesRule()
    {
        var capabilities = AllCapabilities().Where(capability => capability.Name != "terminal-run-status").ToArray();
        var chain = RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error).ToList();
        chain.Add(SessionEvent(3, AlertSignalStatus.Error, chain[^1].SignalId, terminalRunStatus: true));

        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain, capabilities: capabilities));

        Assert.Empty(result.Receipts);
        var suppression = Assert.Single(result.Suppressions);
        Assert.Equal("missing_required_capability", suppression.Code);
        Assert.Equal(["terminal-run-status"], suppression.MissingCapabilities);
    }

    [Fact]
    public void UnrecoveredRetryChain_IncompleteInterval_DoesNotInferUnrecoveredTerminal()
    {
        var snapshot = Snapshot(signals: RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error), reasons: ["ingest_gap"]);

        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void UnrecoveredRetryChain_ChainSignalWithoutAttempt_MakesTerminalityIncomplete(int missingSequence)
    {
        var chain = RetryChain(AlertSignalStatus.Error, AlertSignalStatus.Error).ToList();
        if (missingSequence == 2)
        {
            chain[1] = chain[1] with { Sequence = 3, SignalId = "signal-3", Evidence = chain[1].Evidence with { EvidenceId = "evidence-3", SpanId = "span-3", EventId = "event-3", ToolCallId = "tool-3", ObservedAt = At.AddSeconds(3) }, ObservedAt = At.AddSeconds(3) };
            chain.Insert(1, Tool(2, status: AlertSignalStatus.Error, retryChain: Hmac('r')));
        }
        else
        {
            chain.Add(Tool(3, status: AlertSignalStatus.Error, retryChain: Hmac('r')));
        }

        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void UnrecoveredRetryChain_AttemptsStartingAfterOne_AreIncomplete()
    {
        var chain = RetryChainAttempts(
            (AlertSignalStatus.Error, 2),
            (AlertSignalStatus.Error, 3));

        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void UnrecoveredRetryChain_AttemptGap_IsIncomplete()
    {
        var chain = RetryChainAttempts(
            (AlertSignalStatus.Error, 1),
            (AlertSignalStatus.Error, 3));

        var result = Evaluate(new UnrecoveredRetryChainAlertRule(), Snapshot(signals: chain));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void HighToolFailureRatio_SourceNeutralFixtures_UseKnownStatusDenominator(string source)
    {
        var signals = StatusCalls(2, 3).Concat([Tool(6, status: AlertSignalStatus.Unknown), Tool(7, status: AlertSignalStatus.Cancelled)]);

        var receipt = Assert.Single(Evaluate(new HighToolFailureRatioAlertRule(), Snapshot(source, signals)).Receipts);

        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(2m, Observed(receipt, "failure-count"));
        Assert.Equal(5m, Observed(receipt, "known-call-count"));
        Assert.Equal(7m, Observed(receipt, "total-tool-call-count"));
        Assert.Equal(5m / 7m, Observed(receipt, "status-coverage"));
        Assert.Equal(0.4m, Observed(receipt, "failure-ratio"));
        Assert.Equal(7, receipt.Evidence.Count);
    }

    [Fact]
    public void HighToolFailureRatio_UnresolvedUnknownStatusContributor_RejectsReceipt()
    {
        var signals = StatusCalls(2, 3).Concat([Tool(6, status: AlertSignalStatus.Unknown)]);
        var engine = new AlertEvaluationEngine(
            new AlertRuleRegistry([new HighToolFailureRatioAlertRule()]),
            new Resolver(reference => reference.ToolCallId != "tool-6"));

        var result = engine.Evaluate(
            Snapshot(signals: signals),
            new AlertEngineConfiguration(AlertContractVersions.Configuration, "tool-rules-test-v1", []));

        Assert.Empty(result.Receipts);
        Assert.Equal("unresolved_evidence", Assert.Single(result.RejectedMatches).Code);
    }

    [Fact]
    public void HighToolFailureRatio_CriticalBoundary_IsInclusive()
    {
        var receipt = Assert.Single(Evaluate(new HighToolFailureRatioAlertRule(), Snapshot(signals: StatusCalls(7, 3))).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(0.7m, Observed(receipt, "failure-ratio"));
    }

    [Fact]
    public void HighToolFailureRatio_MinimumKnownSample_ExcludesUnknownAndCancelled()
    {
        var signals = StatusCalls(3, 1).Concat([Tool(5, status: AlertSignalStatus.Unknown), Tool(6, status: AlertSignalStatus.Cancelled)]);

        var result = Evaluate(new HighToolFailureRatioAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
        Assert.Equal("minimum-sample-unmet", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void HighToolFailureRatio_IncompleteHistory_SuppressesAuthoritativeDenominator()
    {
        var snapshot = Snapshot(signals: StatusCalls(5, 0), reasons: ["historical_summary_only"]);

        var result = Evaluate(new HighToolFailureRatioAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void ExcessivePermissionWait_SourceNeutralFixtures_UseTraceTotalsAndBoundaries(string source)
    {
        var individual = Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(source, [Permission(1, 30)]));
        var total = Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(source, [Permission(1, 29), Permission(2, 31)]));
        var critical = Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(source, [Permission(1, 120)]));

        Assert.Equal(AlertSeverity.Warning, Assert.Single(individual.Receipts).Severity);
        Assert.Equal(60m, Observed(Assert.Single(total.Receipts), "total-wait"));
        Assert.Equal(AlertSeverity.Critical, Assert.Single(critical.Receipts).Severity);
    }

    [Fact]
    public void ExcessivePermissionWait_TotalCriticalBoundary_IsInclusive()
    {
        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(signals: [Permission(1, 100), Permission(2, 100), Permission(3, 100)])).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(300m, Observed(receipt, "total-wait"));
        Assert.Equal(3, receipt.Evidence.Count);
    }

    [Fact]
    public void ExcessivePermissionWait_NoTraceOrMissingDuration_DoesNotInferWait()
    {
        var noTrace = Snapshot(signals: [Permission(1, 30)]) with { TraceId = null, Signals = [Permission(1, 30, traceId: null)] };
        var missingDuration = Permission(1, 0) with { Metrics = [] };

        var noTraceResult = Evaluate(new ExcessivePermissionWaitAlertRule(), noTrace);
        var missingResult = Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(signals: [missingDuration]));

        Assert.Equal("trace-scope-unavailable", Assert.Single(noTraceResult.Suppressions).Code);
        Assert.Equal("incomplete-signal-facts", Assert.Single(missingResult.Suppressions).Code);
        Assert.Empty(noTraceResult.Receipts);
        Assert.Empty(missingResult.Receipts);
    }

    [Fact]
    public void ExcessivePermissionWait_PartialIndividualIsConclusiveButPartialTotalOnlyIsSuppressed()
    {
        var individual = Snapshot(signals: [Permission(1, 30)], reasons: ["ingest_gap"]);
        var totalOnly = Snapshot(signals: [Permission(1, 20), Permission(2, 20), Permission(3, 20)], reasons: ["ingest_gap"]);

        var individualResult = Evaluate(new ExcessivePermissionWaitAlertRule(), individual);
        var individualReceipt = Assert.Single(individualResult.Receipts);
        Assert.Equal(AlertSeverity.Warning, individualReceipt.Severity);
        Assert.Equal(30m, Observed(individualReceipt, "total-wait-lower-bound"));
        Assert.DoesNotContain(individualReceipt.ObservedValues, value => value.Name == "total-wait");
        Assert.Equal("incomplete-signal-facts", Assert.Single(individualResult.Suppressions).Code);

        var totalResult = Evaluate(new ExcessivePermissionWaitAlertRule(), totalOnly);
        Assert.Empty(totalResult.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(totalResult.Suppressions).Code);
    }

    [Fact]
    public void ExcessivePermissionWait_PartialTotalNeverEscalatesExactMaximumSeverity()
    {
        var snapshot = Snapshot(
            signals: [Permission(1, 100), Permission(2, 100), Permission(3, 100)],
            reasons: ["historical_summary_only"]);

        var result = Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot);
        var receipt = Assert.Single(result.Receipts);

        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(300m, Observed(receipt, "total-wait-lower-bound"));
        Assert.DoesNotContain(receipt.ObservedValues, value => value.Name == "total-wait");
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void ExcessivePermissionWait_MissingDurationNeverLabelsKnownSubtotalExact()
    {
        var missingDuration = Permission(2, 0) with { Metrics = [] };
        var snapshot = Snapshot(signals: [Permission(1, 120), missingDuration]);

        var result = Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot);
        var receipt = Assert.Single(result.Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(120m, Observed(receipt, "total-wait-lower-bound"));
        Assert.DoesNotContain(receipt.ObservedValues, value => value.Name == "total-wait");
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void ExcessivePermissionWait_PartialHistoryWithoutObservedWait_ReportsIncompleteFacts()
    {
        var snapshot = Snapshot(signals: [Tool(1)], reasons: ["historical_summary_only"]);

        var result = Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void ExcessivePermissionWait_IndividualAboveThresholdSchemaMaximum_RemainsExactCritical()
    {
        var snapshot = Snapshot(signals: [Permission(1, 86_401)]);

        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(86_401m, Observed(receipt, "maximum-wait"));
        Assert.Equal(86_401m, Observed(receipt, "total-wait"));
    }

    [Fact]
    public void ExcessivePermissionWait_ExactTotalAboveThresholdSchemaMaximum_RemainsCritical()
    {
        var snapshot = Snapshot(signals: [Permission(1, 250_000), Permission(2, 250_000), Permission(3, 250_000)]);

        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(750_000m, Observed(receipt, "total-wait"));
    }

    [Fact]
    public void ExcessivePermissionWait_DecimalMaximumSingleWait_RemainsExactCritical()
    {
        var snapshot = Snapshot(signals: [Permission(1, decimal.MaxValue)]);

        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(decimal.MaxValue, Observed(receipt, "maximum-wait"));
        Assert.Equal(decimal.MaxValue, Observed(receipt, "total-wait"));
    }

    [Fact]
    public void ExcessivePermissionWait_OverflowingPositiveTotal_EmitsCriticalWithHonestLowerBound()
    {
        var snapshot = Snapshot(signals: [Permission(1, 1), Permission(2, decimal.MaxValue)]);

        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), snapshot).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(decimal.MaxValue, Observed(receipt, "total-wait-lower-bound"));
        Assert.DoesNotContain(receipt.ObservedValues, value => value.Name == "total-wait");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcessivePermissionWait_FractionAndDecimalMaximum_NeverSilentlyRoundToExactTotal(bool maximumFirst)
    {
        var waits = maximumFirst
            ? new[] { Permission(1, decimal.MaxValue), Permission(2, 0.1m) }
            : new[] { Permission(1, 0.1m), Permission(2, decimal.MaxValue) };

        var receipt = Assert.Single(Evaluate(new ExcessivePermissionWaitAlertRule(), Snapshot(signals: waits)).Receipts);

        Assert.Equal(AlertSeverity.Critical, receipt.Severity);
        Assert.Equal(decimal.MaxValue, Observed(receipt, "total-wait-lower-bound"));
        Assert.DoesNotContain(receipt.ObservedValues, value => value.Name == "total-wait");
        Assert.Equal(2, receipt.Evidence.Count);
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void RepeatedFileReadOrSearch_SourceNeutralBoundaryFixtures_EmitExpectedSeverity(string source)
    {
        var warning = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(source, FileAccesses(3)));
        var critical = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(source, FileAccesses(5)));

        Assert.Equal(AlertSeverity.Warning, Assert.Single(warning.Receipts).Severity);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(critical.Receipts).Severity);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_DistinctRangesWatchPollAndExactEditPreventFalsePositive()
    {
        var file = Hmac('f');
        var signals = new[]
        {
            File(1, file, "read", Hmac('1')),
            File(2, file, "read", Hmac('2')),
            File(3, file, "watch"),
            File(4, file, "poll"),
            File(5, file, "read", Hmac('1')),
            File(6, file, "edit"),
            File(7, file, "read", Hmac('1')),
            File(8, file, "read", Hmac('1')),
        };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_EditForDifferentFile_DoesNotResetSegment()
    {
        var target = Hmac('f');
        var signals = new[]
        {
            File(1, target, range: Hmac('1')),
            File(2, Hmac('x'), "edit"),
            File(3, target, range: Hmac('1')),
            File(4, target, range: Hmac('1')),
        };

        var receipt = Assert.Single(Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals)).Receipts);

        Assert.Equal(3m, Observed(receipt, "access-count"));
    }

    [Fact]
    public void RepeatedFileReadOrSearch_SameOwnershipBranch_RepeatsWithinOneGroup()
    {
        var signals = Enumerable.Range(1, 3).Select(index => File(index, Hmac('f'), owner: "branch-a"));

        var receipt = Assert.Single(Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals)).Receipts);

        Assert.Equal(3m, Observed(receipt, "access-count"));
    }

    [Fact]
    public void RepeatedFileReadOrSearch_ParallelOwnershipBranches_DoNotCombine()
    {
        var signals = new[]
        {
            File(1, Hmac('f'), owner: "branch-a"),
            File(2, Hmac('f'), owner: "branch-b"),
            File(3, Hmac('f'), owner: "branch-a"),
            File(4, Hmac('f'), owner: "branch-b"),
        };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_EditInSameOwnershipBranch_ResetsOwnerSegment()
    {
        var signals = new[]
        {
            File(1, Hmac('f'), owner: "branch-a"),
            File(2, Hmac('f'), owner: "branch-a"),
            File(3, Hmac('f'), "edit", owner: "branch-a"),
            File(4, Hmac('f'), owner: "branch-a"),
            File(5, Hmac('f'), owner: "branch-a"),
        };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_EditInParallelBranch_DoesNotResetOwnerSegment()
    {
        var signals = new[]
        {
            File(1, Hmac('f'), owner: "branch-a"),
            File(2, Hmac('f'), owner: "branch-a"),
            File(3, Hmac('f'), "edit", owner: "branch-b"),
            File(4, Hmac('f'), owner: "branch-a"),
        };

        var receipt = Assert.Single(Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals)).Receipts);

        Assert.Equal(3m, Observed(receipt, "access-count"));
    }

    [Fact]
    public void RepeatedFileReadOrSearch_MissingOwnershipFact_Suppresses()
    {
        var signals = FileAccesses(3).ToArray();
        signals[1] = signals[1] with
        {
            ComparableKeys = signals[1].ComparableKeys.Where(key => key.Name != "ownership-key").ToArray(),
        };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_PartialHistoryCouldHideEdit_SuppressesPositiveCount()
    {
        var snapshot = Snapshot(signals: FileAccesses(3), reasons: ["historical_summary_only"]);

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_PartialHistoryBelowThreshold_ReportsIncompleteFacts()
    {
        var snapshot = Snapshot(signals: FileAccesses(2), reasons: ["ingest_gap"]);

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData("file-key")]
    [InlineData("operation-type")]
    public void RepeatedFileReadOrSearch_MissingSameFileFact_CouldHideEditAndSuppresses(string missingKey)
    {
        var signals = FileAccesses(4).ToArray();
        signals[2] = signals[2] with { ComparableKeys = signals[2].ComparableKeys.Where(key => key.Name != missingKey).ToArray() };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Fact]
    public void RepeatedFileReadOrSearch_AbsentRanges_GroupAsWholeFileAccess()
    {
        var signals = Enumerable.Range(1, 3).Select(index => File(index, Hmac('f')));

        var receipt = Assert.Single(Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals)).Receipts);

        Assert.Equal(AlertSeverity.Warning, receipt.Severity);
        Assert.Equal(3m, Observed(receipt, "access-count"));
    }

    [Fact]
    public void RepeatedFileReadOrSearch_PresentRangeWithWrongKind_Suppresses()
    {
        var signals = Enumerable.Range(1, 3).Select(index => File(index, Hmac('f'))).ToArray();
        signals[1] = signals[1] with
        {
            ComparableKeys = signals[1].ComparableKeys.Append(new("range-key", AlertComparableKeyKind.MetadataToken, "whole-file")).ToArray(),
        };

        var result = Evaluate(new RepeatedFileReadOrSearchAlertRule(), Snapshot(signals: signals));

        Assert.Empty(result.Receipts);
        Assert.Equal("incomplete-signal-facts", Assert.Single(result.Suppressions).Code);
    }

    [Theory]
    [InlineData(AlertCapabilityAvailability.Unknown)]
    [InlineData(AlertCapabilityAvailability.Unavailable)]
    public void RulePack_NonAvailableCapability_IsSuppressedByFrozenEngineContract(AlertCapabilityAvailability availability)
    {
        var snapshot = Snapshot(capabilities: [new("tool-call-status", availability)]);

        var result = Evaluate(new HighToolFailureRatioAlertRule(), snapshot);

        Assert.Empty(result.Receipts);
        var suppression = Assert.Single(result.Suppressions);
        Assert.Equal("missing_required_capability", suppression.Code);
        Assert.Equal(["tool-call-status"], suppression.MissingCapabilities);
    }

    [Fact]
    public void RulePack_AbsentCapability_IsSuppressedByFrozenEngineContract()
    {
        var result = Evaluate(new HighToolFailureRatioAlertRule(), Snapshot(capabilities: []));

        Assert.Empty(result.Receipts);
        var suppression = Assert.Single(result.Suppressions);
        Assert.Equal("missing_required_capability", suppression.Code);
        Assert.Equal(["tool-call-status"], suppression.MissingCapabilities);
    }

    [Fact]
    public void RulePack_UnresolvedExactEvidence_IsRejected()
    {
        var result = Evaluate(new RepeatedIdenticalToolCallAlertRule(), Snapshot(signals: RepeatedToolCalls(3)), resolves: false);

        Assert.Empty(result.Receipts);
        Assert.Equal("unresolved_evidence", Assert.Single(result.RejectedMatches).Code);
    }

    [Fact]
    public void RulePack_MaliciousArgumentsAndPaths_NeverReachReceiptOrEvaluationBytes()
    {
        const string maliciousArgument = "../../secret?token=<script>alert(1)</script>";
        const string maliciousPath = "C:\\Users\\person\\.ssh\\id_rsa#fragment";
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var toolHash = AlertSensitiveValueHasher.Hash(key, "session-1", "argument-hash", maliciousArgument);
        var fileHash = AlertSensitiveValueHasher.Hash(key, "session-1", "file-key", maliciousPath);
        var repeatedTool = Enumerable.Range(1, 3).Select(index => Tool(index, tool: toolHash));
        var repeatedFile = Enumerable.Range(4, 3).Select(index => File(index, fileHash, range: Hmac('1')));
        var snapshot = Snapshot(signals: repeatedTool.Concat(repeatedFile));
        var result = Evaluate(ToolAlertRulePack.CreateRules(), snapshot);
        var json = Encoding.UTF8.GetString(AlertCanonicalJson.SerializeEvaluation(result));

        Assert.Equal(2, result.Receipts.Count);
        Assert.DoesNotContain(maliciousArgument, json, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(toolHash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fileHash, json, StringComparison.Ordinal);
    }

    [Fact]
    public void RulePack_EquivalentSignalOrdering_ProducesByteEquivalentEvaluation()
    {
        var signals = RepeatedToolCalls(3).Concat(FileAccesses(3, sequenceOffset: 3)).ToArray();
        var forward = Snapshot(signals: signals);
        var reverse = forward with { Signals = signals.Reverse().ToArray() };

        var first = Evaluate(ToolAlertRulePack.CreateRules(), forward);
        var second = Evaluate(ToolAlertRulePack.CreateRules(), reverse);

        Assert.Equal(AlertCanonicalJson.SerializeEvaluation(first), AlertCanonicalJson.SerializeEvaluation(second));
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void FalsePositiveFixtureReview_SourceNeutralNegativeSet_ProducesNoReceipt(string source)
    {
        var signals = new[]
        {
            Tool(1, tool: Hmac('a'), owner: "parallel-a"),
            Tool(2, tool: Hmac('a'), owner: "parallel-b"),
            Tool(3, tool: Hmac('b'), owner: "parallel-a"),
            Tool(4, status: AlertSignalStatus.Error, retryChain: Hmac('r'), retryAttempt: 1),
            Tool(5, status: AlertSignalStatus.Success, retryChain: Hmac('r'), retryAttempt: 2),
            Permission(6, 29),
            File(7, Hmac('f')),
            File(8, Hmac('f'), "edit"),
            File(9, Hmac('f')),
            File(10, Hmac('f')),
        };

        var result = Evaluate(ToolAlertRulePack.CreateRules(), Snapshot(source, signals));

        Assert.Empty(result.Receipts);
    }

    private static AlertRuleDescriptor Descriptor(AlertRuleRegistry registry, string id) =>
        Assert.Single(registry.Rules, rule => rule.Descriptor.RuleId == id).Descriptor;

    private static IAlertRule CreateRule(string id) => id switch
    {
        "repeated-identical-tool-call" => new RepeatedIdenticalToolCallAlertRule(),
        "unrecovered-retry-chain" => new UnrecoveredRetryChainAlertRule(),
        "high-tool-failure-ratio" => new HighToolFailureRatioAlertRule(),
        "excessive-permission-wait" => new ExcessivePermissionWaitAlertRule(),
        "repeated-file-read-or-search" => new RepeatedFileReadOrSearchAlertRule(),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static void AssertThreshold(AlertRuleDescriptor descriptor, string name, string unit, decimal minimum, decimal maximum, decimal warning, decimal critical)
    {
        var threshold = Assert.Single(descriptor.Thresholds, item => item.Name == name);
        Assert.Equal(unit, threshold.Unit);
        Assert.Equal(minimum, threshold.Minimum);
        Assert.Equal(maximum, threshold.Maximum);
        Assert.Equal(warning, threshold.WarningDefault);
        Assert.Equal(critical, threshold.CriticalDefault);
    }

    private static decimal Observed(AlertReceipt receipt, string name) => Assert.Single(receipt.ObservedValues, item => item.Name == name).Value;

    private static AlertEvaluationResult Evaluate(IAlertRule rule, AlertNormalizedSnapshot snapshot, bool resolves = true) =>
        Evaluate([rule], snapshot, resolves);

    private static AlertEvaluationResult Evaluate(IEnumerable<IAlertRule> rules, AlertNormalizedSnapshot snapshot, bool resolves = true) =>
        new AlertEvaluationEngine(new AlertRuleRegistry(rules), new Resolver(resolves)).Evaluate(
            snapshot,
            new AlertEngineConfiguration(AlertContractVersions.Configuration, "tool-rules-test-v1", []));

    private static AlertNormalizedSnapshot Snapshot(
        string source = "github-copilot-vscode",
        IEnumerable<AlertSignal>? signals = null,
        IReadOnlyList<string>? reasons = null,
        IReadOnlyList<AlertCapabilityFact>? capabilities = null)
    {
        var items = (signals ?? [Tool(1)]).ToArray();
        var first = items.Min(item => item.ObservedAt);
        var last = items.Max(item => item.ObservedAt);
        return new(
            AlertContractVersions.Snapshot,
            source,
            "1.0.0",
            "session-1",
            "trace-1",
            reasons is { Count: > 0 } ? AlertCompleteness.Partial : AlertCompleteness.Full,
            reasons ?? [],
            first,
            last,
            capabilities ?? AllCapabilities(),
            items);
    }

    private static IReadOnlyList<AlertCapabilityFact> AllCapabilities() =>
    [
        new("tool-call-key", AlertCapabilityAvailability.Available),
        new("tool-call-ordering", AlertCapabilityAvailability.Available),
        new("tool-call-ownership", AlertCapabilityAvailability.Available),
        new("tool-retry-classification", AlertCapabilityAvailability.Available),
        new("canonical-tool-arguments", AlertCapabilityAvailability.Available),
        new("explicit-retry-classification", AlertCapabilityAvailability.Available),
        new("stable-tool-ordering", AlertCapabilityAvailability.Available),
        new("tool-name", AlertCapabilityAvailability.Available),
        new("tool-ownership", AlertCapabilityAvailability.Available),
        new("tool-call-status", AlertCapabilityAvailability.Available),
        new("tool-retry-attempt", AlertCapabilityAvailability.Available),
        new("terminal-run-status", AlertCapabilityAvailability.Available),
        new("explicit-permission-duration", AlertCapabilityAvailability.Available),
        new("file-access-key", AlertCapabilityAvailability.Available),
        new("file-access-ownership", AlertCapabilityAvailability.Available),
        new("file-operation-type", AlertCapabilityAvailability.Available),
        new("file-access-ordering", AlertCapabilityAvailability.Available),
    ];

    private static IEnumerable<AlertSignal> RepeatedToolCalls(int count) =>
        Enumerable.Range(1, count).Select(index => Tool(index));

    private static IEnumerable<AlertSignal> RetryChain(params AlertSignalStatus[] statuses) =>
        statuses.Select((status, index) => Tool(index + 1, retryKind: "explicit", status: status, retryChain: Hmac('r'), retryAttempt: index + 1));

    private static IEnumerable<AlertSignal> RetryChainAttempts(params (AlertSignalStatus Status, int Attempt)[] attempts) =>
        attempts.Select((item, index) => Tool(index + 1, retryKind: "explicit", status: item.Status, retryChain: Hmac('r'), retryAttempt: item.Attempt));

    private static IEnumerable<AlertSignal> StatusCalls(int errors, int successes) =>
        Enumerable.Range(1, errors).Select(index => Tool(index, status: AlertSignalStatus.Error))
            .Concat(Enumerable.Range(errors + 1, successes).Select(index => Tool(index, status: AlertSignalStatus.Success)));

    private static IEnumerable<AlertSignal> FileAccesses(int count, int sequenceOffset = 0) =>
        Enumerable.Range(1, count).Select(index => File(index + sequenceOffset, Hmac('f'), range: Hmac('1')));

    private static AlertNormalizedSnapshot WithoutTrace(AlertNormalizedSnapshot snapshot) => snapshot with
    {
        TraceId = null,
        Signals = snapshot.Signals.Select(signal => signal with { Evidence = signal.Evidence with { TraceId = null } }).ToArray(),
    };

    private static AlertSignal Tool(
        int sequence,
        string? tool = null,
        string owner = "branch-main",
        string retryKind = "none",
        AlertSignalStatus status = AlertSignalStatus.Success,
        string? retryChain = null,
        int? retryAttempt = null,
        string? traceId = "trace-1")
    {
        var keys = new List<AlertComparableKey>
        {
            new("argument-hash", AlertComparableKeyKind.SensitiveHmac, tool ?? Hmac('a')),
            new("tool-key", AlertComparableKeyKind.SensitiveHmac, tool ?? Hmac('a')),
            new("tool-name", AlertComparableKeyKind.MetadataToken, "fixture-tool"),
            new("ownership-key", AlertComparableKeyKind.MetadataToken, owner),
            new("retry-kind", AlertComparableKeyKind.MetadataToken, retryKind),
        };
        if (retryChain is not null)
        {
            keys.Add(new("retry-chain-key", AlertComparableKeyKind.SensitiveHmac, retryChain));
        }

        var metrics = retryAttempt is null ? [] : new AlertMetric[] { new("retry-attempt", "attempts", retryAttempt.Value) };
        return Signal(sequence, AlertSignalKind.ToolCall, status, metrics, keys, traceId: traceId);
    }

    private static AlertSignal Permission(int sequence, decimal seconds, string? traceId = "trace-1") =>
        Signal(sequence, AlertSignalKind.Permission, AlertSignalStatus.Success, [new("wait-duration", "seconds", seconds)], [], traceId: traceId);

    private static AlertSignal File(int sequence, string file, string operation = "read", string? range = null, string owner = "branch-main")
    {
        var keys = new List<AlertComparableKey>
        {
            new("file-key", AlertComparableKeyKind.SensitiveHmac, file),
            new("operation-type", AlertComparableKeyKind.MetadataToken, operation),
            new("ownership-key", AlertComparableKeyKind.MetadataToken, owner),
        };
        if (range is not null)
        {
            keys.Add(new("range-key", AlertComparableKeyKind.SensitiveHmac, range));
        }

        return Signal(sequence, AlertSignalKind.FileAccess, AlertSignalStatus.Success, [], keys);
    }

    private static AlertSignal SessionEvent(int sequence, AlertSignalStatus status, string parentSignalId, bool terminalRunStatus = false) =>
        Signal(
            sequence,
            AlertSignalKind.SessionEvent,
            status,
            [],
            terminalRunStatus ? [new("event-kind", AlertComparableKeyKind.MetadataToken, "terminal-run-status")] : [],
            parentSignalId);

    private static AlertSignal Signal(
        int sequence,
        AlertSignalKind kind,
        AlertSignalStatus status,
        IReadOnlyList<AlertMetric> metrics,
        IReadOnlyList<AlertComparableKey> keys,
        string? parentSignalId = null,
        string? traceId = "trace-1")
    {
        var observed = At.AddSeconds(sequence);
        var id = $"signal-{sequence}";
        var evidence = new AlertEvidenceReference(
            kind == AlertSignalKind.ToolCall ? AlertEvidenceKind.ToolCall : AlertEvidenceKind.Event,
            $"evidence-{sequence}",
            "session-1",
            traceId,
            $"span-{sequence}",
            null,
            $"event-{sequence}",
            kind == AlertSignalKind.ToolCall ? $"tool-{sequence}" : null,
            observed);
        return new(id, kind, sequence, observed, parentSignalId, status, metrics, keys, evidence);
    }

    private static string Hmac(char value)
    {
        var hexadecimal = value switch
        {
            'r' => 'c',
            'x' => 'd',
            _ => value,
        };
        return $"hmac-sha256-v1:{new string(hexadecimal, 64)}";
    }

    private static readonly DateTimeOffset At = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    private sealed class Resolver : IAlertEvidenceResolver
    {
        private readonly Func<AlertEvidenceReference, bool> _exists;

        public Resolver(bool exists)
            : this(_ => exists)
        {
        }

        public Resolver(Func<AlertEvidenceReference, bool> exists)
        {
            _exists = exists;
        }

        public bool Exists(AlertEvidenceReference reference) => _exists(reference);
    }
}

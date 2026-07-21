namespace CopilotAgentObservability.Alerts;

public static class ToolAlertRulePack
{
    public static IReadOnlyList<IAlertRule> CreateRules() =>
    [
        new RepeatedIdenticalToolCallAlertRule(),
        new UnrecoveredRetryChainAlertRule(),
        new HighToolFailureRatioAlertRule(),
        new ExcessivePermissionWaitAlertRule(),
        new RepeatedFileReadOrSearchAlertRule(),
    ];
}

public sealed class RepeatedIdenticalToolCallAlertRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = ToolAlertRuleSupport.Descriptor(
        "repeated-identical-tool-call",
        "Repeated identical tool call",
        "Reports repeated non-retry tool calls with one exact tool and ownership key.",
        ["canonical-tool-arguments", "explicit-retry-classification", "stable-tool-ordering", "tool-name", "tool-ownership"],
        ["argument-hash", "ownership-key", "tool-name"],
        [ToolAlertRuleSupport.Threshold("identical-call-count", "calls", 1, 100_000, 3, 5)],
        ["incomplete-signal-facts"]);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var incomplete = false;
        var candidates = new List<(AlertSignal Signal, string Argument, string Owner, string Tool)>();
        foreach (var signal in ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.ToolCall))
        {
            var argument = ToolAlertRuleSupport.Key(signal, "argument-hash", AlertComparableKeyKind.SensitiveHmac);
            var tool = ToolAlertRuleSupport.Key(signal, "tool-name", AlertComparableKeyKind.MetadataToken);
            var owner = ToolAlertRuleSupport.Key(signal, "ownership-key", AlertComparableKeyKind.MetadataToken);
            var retry = ToolAlertRuleSupport.Key(signal, "retry-kind", AlertComparableKeyKind.MetadataToken);
            if (argument is null || tool is null || owner is null || retry is not ("none" or "explicit"))
            {
                incomplete = true;
                continue;
            }

            if (retry == "none")
            {
                candidates.Add((signal, argument, owner, tool));
            }
        }

        var warning = context.EffectiveThresholds["identical-call-count.warning"];
        var critical = context.EffectiveThresholds["identical-call-count.critical"];
        var matches = candidates
            .GroupBy(item => (item.Argument, item.Owner, item.Tool))
            .OrderBy(group => group.Key.Argument, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Owner, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Tool, StringComparer.Ordinal)
            .Where(group => group.Count() >= warning)
            .Select(group => ToolAlertRuleSupport.Match(
                group.Count() >= critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                [new("identical-call-count", "calls", group.Count())],
                group.Select(item => item.Signal)))
            .ToArray();

        var suppression = incomplete || matches.Length == 0 && ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot)
            ? "incomplete-signal-facts"
            : null;
        return ToolAlertRuleSupport.Outcome(matches, suppression);
    }
}

public sealed class UnrecoveredRetryChainAlertRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = ToolAlertRuleSupport.Descriptor(
        "unrecovered-retry-chain",
        "Unrecovered retry chain",
        "Reports an exact retry chain whose authoritative terminal attempt failed.",
        ["tool-call-key", "tool-call-ordering", "tool-call-status", "tool-retry-attempt"],
        ["tool-key", "retry-chain-key"],
        [ToolAlertRuleSupport.Threshold("retry-chain-length", "attempts", 2, 100_000, 2, 3)],
        ["incomplete-signal-facts", "unknown-terminal-status"]);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var incomplete = false;
        var attempts = new List<(AlertSignal Signal, string Tool, string Chain, decimal Attempt)>();
        foreach (var signal in ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.ToolCall))
        {
            var attempt = ToolAlertRuleSupport.Metric(signal, "retry-attempt", "attempts");
            if (attempt is null)
            {
                continue;
            }

            var tool = ToolAlertRuleSupport.Key(signal, "tool-key", AlertComparableKeyKind.SensitiveHmac);
            var chain = ToolAlertRuleSupport.Key(signal, "retry-chain-key", AlertComparableKeyKind.SensitiveHmac);
            if (tool is null || chain is null || attempt <= 0 || attempt != decimal.Truncate(attempt.Value))
            {
                incomplete = true;
                continue;
            }

            attempts.Add((signal, tool, chain, attempt.Value));
        }

        var warning = context.EffectiveThresholds["retry-chain-length.warning"];
        var critical = context.EffectiveThresholds["retry-chain-length.critical"];
        var matches = new List<AlertRuleMatch>();
        var unknownTerminal = false;
        var incompleteInterval = ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot);

        foreach (var group in attempts.GroupBy(item => (item.Tool, item.Chain))
                     .OrderBy(group => group.Key.Tool, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.Chain, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(item => item.Signal.Sequence)
                .ThenBy(item => item.Signal.ObservedAt)
                .ThenBy(item => item.Signal.SignalId, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length < 2 || ordered.Zip(ordered.Skip(1), (left, right) => left.Attempt < right.Attempt).Any(value => !value))
            {
                incomplete |= ordered.Length >= 2;
                continue;
            }

            var hasFailedAttemptBeforeTerminal = ordered[..^1].Any(item => item.Signal.Status == AlertSignalStatus.Error);
            if (!hasFailedAttemptBeforeTerminal)
            {
                continue;
            }

            var terminal = ordered[^1].Signal;
            if (terminal.Status is AlertSignalStatus.Unknown or AlertSignalStatus.Cancelled)
            {
                unknownTerminal = true;
                continue;
            }

            if (terminal.Status != AlertSignalStatus.Error)
            {
                continue;
            }

            if (incompleteInterval)
            {
                incomplete = true;
                continue;
            }

            var linkedRunFailures = ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.SessionEvent)
                .Where(signal => signal.Status == AlertSignalStatus.Error && signal.ParentSignalId == terminal.SignalId)
                .ToArray();
            var severity = ordered.Length >= critical || linkedRunFailures.Length > 0
                ? AlertSeverity.Critical
                : AlertSeverity.Warning;
            if (ordered.Length >= warning || linkedRunFailures.Length > 0)
            {
                matches.Add(ToolAlertRuleSupport.Match(
                    severity,
                    [new("retry-chain-length", "attempts", ordered.Length)],
                    ordered.Select(item => item.Signal).Concat(linkedRunFailures)));
            }
        }

        var suppressions = new List<AlertRuleSuppression>();
        if (incomplete)
        {
            suppressions.Add(new("incomplete-signal-facts"));
        }
        if (unknownTerminal)
        {
            suppressions.Add(new("unknown-terminal-status"));
        }
        return new(matches, suppressions);
    }
}

public sealed class HighToolFailureRatioAlertRule : IAlertRule
{
    private const int MinimumKnownCalls = 5;

    public AlertRuleDescriptor Descriptor { get; } = ToolAlertRuleSupport.Descriptor(
        "high-tool-failure-ratio",
        "High tool failure ratio",
        "Reports a high error ratio over authoritative success and error tool statuses.",
        ["tool-call-status"],
        [],
        [ToolAlertRuleSupport.Threshold("failure-ratio", "ratio", 0, 1, 0.40m, 0.70m)],
        ["incomplete-signal-facts", "minimum-sample-unmet"]);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        if (ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot))
        {
            return ToolAlertRuleSupport.Outcome([], "incomplete-signal-facts");
        }

        var all = ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.ToolCall).ToArray();
        var known = all.Where(signal => signal.Status is AlertSignalStatus.Success or AlertSignalStatus.Error).ToArray();
        if (known.Length < MinimumKnownCalls)
        {
            return ToolAlertRuleSupport.Outcome([], "minimum-sample-unmet");
        }

        var errors = known.Count(signal => signal.Status == AlertSignalStatus.Error);
        var ratio = (decimal)errors / known.Length;
        var warning = context.EffectiveThresholds["failure-ratio.warning"];
        if (ratio < warning)
        {
            return ToolAlertRuleSupport.Outcome([]);
        }

        var severity = ratio >= context.EffectiveThresholds["failure-ratio.critical"]
            ? AlertSeverity.Critical
            : AlertSeverity.Warning;
        var match = ToolAlertRuleSupport.Match(
            severity,
            [
                new("failure-count", "calls", errors),
                new("failure-ratio", "ratio", ratio),
                new("known-call-count", "calls", known.Length),
                new("status-coverage", "ratio", all.Length == 0 ? 0 : (decimal)known.Length / all.Length),
                new("total-tool-call-count", "calls", all.Length),
            ],
            known);
        return ToolAlertRuleSupport.Outcome([match]);
    }
}

public sealed class ExcessivePermissionWaitAlertRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = ToolAlertRuleSupport.Descriptor(
        "excessive-permission-wait",
        "Excessive permission wait",
        "Reports excessive explicit permission wait duration within one exact trace.",
        ["explicit-permission-duration"],
        [],
        [
            ToolAlertRuleSupport.Threshold("individual-wait", "seconds", 0, 86_400, 30, 120),
            ToolAlertRuleSupport.Threshold("total-wait", "seconds", 0, 604_800, 60, 300),
        ],
        ["incomplete-signal-facts", "trace-scope-unavailable"]);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        if (context.Snapshot.TraceId is null)
        {
            return ToolAlertRuleSupport.Outcome([], "trace-scope-unavailable");
        }

        var incomplete = false;
        var waits = new List<(AlertSignal Signal, decimal Seconds)>();
        foreach (var signal in ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.Permission))
        {
            var duration = ToolAlertRuleSupport.Metric(signal, "wait-duration", "seconds");
            if (duration is null || duration < 0)
            {
                incomplete = true;
                continue;
            }
            waits.Add((signal, duration.Value));
        }

        if (waits.Count == 0)
        {
            var suppression = incomplete || ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot)
                ? "incomplete-signal-facts"
                : null;
            return ToolAlertRuleSupport.Outcome([], suppression);
        }

        var maximum = waits.Max(item => item.Seconds);
        var total = waits.Sum(item => item.Seconds);
        var individualWarning = maximum >= context.EffectiveThresholds["individual-wait.warning"];
        var individualCritical = maximum >= context.EffectiveThresholds["individual-wait.critical"];
        var totalWarning = total >= context.EffectiveThresholds["total-wait.warning"];
        var totalCritical = total >= context.EffectiveThresholds["total-wait.critical"];
        if (!individualWarning && !totalWarning)
        {
            return ToolAlertRuleSupport.Outcome([], incomplete ? "incomplete-signal-facts" : null);
        }

        if (!individualWarning && totalWarning && (incomplete || ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot)))
        {
            return ToolAlertRuleSupport.Outcome([], "incomplete-signal-facts");
        }

        var evidence = totalWarning
            ? waits.Select(item => item.Signal)
            : waits.Where(item => item.Seconds == maximum).Select(item => item.Signal);
        var match = ToolAlertRuleSupport.Match(
            individualCritical || totalCritical ? AlertSeverity.Critical : AlertSeverity.Warning,
            [new("maximum-wait", "seconds", maximum), new("total-wait", "seconds", total)],
            evidence);
        return ToolAlertRuleSupport.Outcome([match], incomplete ? "incomplete-signal-facts" : null);
    }
}

public sealed class RepeatedFileReadOrSearchAlertRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = ToolAlertRuleSupport.Descriptor(
        "repeated-file-read-or-search",
        "Repeated file read or search",
        "Reports repeated exact file reads or searches within an edit-bounded segment.",
        ["file-access-key", "file-access-ordering", "file-operation-type"],
        ["file-key", "operation-type", "range-key"],
        [ToolAlertRuleSupport.Threshold("access-count", "accesses", 1, 100_000, 3, 5)],
        ["incomplete-signal-facts"]);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var incomplete = false;
        var segmentByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<(AlertSignal Signal, string File, string Operation, string Range, int Segment)>();
        foreach (var signal in ToolAlertRuleSupport.Signals(context.Snapshot, AlertSignalKind.FileAccess))
        {
            var file = ToolAlertRuleSupport.Key(signal, "file-key", AlertComparableKeyKind.SensitiveHmac);
            var operation = ToolAlertRuleSupport.Key(signal, "operation-type", AlertComparableKeyKind.MetadataToken);
            if (file is null || operation is not ("read" or "search" or "edit" or "watch" or "poll"))
            {
                incomplete = true;
                continue;
            }

            segmentByFile.TryGetValue(file, out var segment);
            if (operation == "edit")
            {
                segmentByFile[file] = segment + 1;
                continue;
            }
            if (operation is "watch" or "poll")
            {
                continue;
            }

            var range = ToolAlertRuleSupport.Key(signal, "range-key", AlertComparableKeyKind.SensitiveHmac) ?? string.Empty;
            candidates.Add((signal, file, operation, range, segment));
        }

        var warning = context.EffectiveThresholds["access-count.warning"];
        var critical = context.EffectiveThresholds["access-count.critical"];
        var matches = candidates
            .GroupBy(item => (item.File, item.Operation, item.Range, item.Segment))
            .OrderBy(group => group.Key.File, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Operation, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Range, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Segment)
            .Where(group => group.Count() >= warning)
            .Select(group => ToolAlertRuleSupport.Match(
                group.Count() >= critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                [new("access-count", "accesses", group.Count())],
                group.Select(item => item.Signal)))
            .ToArray();
        var suppression = incomplete || matches.Length == 0 && ToolAlertRuleSupport.HasIncompleteInterval(context.Snapshot)
            ? "incomplete-signal-facts"
            : null;
        return ToolAlertRuleSupport.Outcome(matches, suppression);
    }
}

internal static class ToolAlertRuleSupport
{
    private static readonly string[] SourceSurfaces = ["claude-code", "codex-app", "codex-cli", "github-copilot-cli", "github-copilot-vscode"];
    private static readonly string[] EngineSuppressions = ["missing_required_capability", "rule_disabled", "source_not_applicable"];

    public static AlertRuleDescriptor Descriptor(
        string id,
        string title,
        string description,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string> groupingKeys,
        IReadOnlyList<AlertThresholdDefinition> thresholds,
        IReadOnlyList<string> suppressions) =>
        new(
            id,
            "1",
            title,
            description,
            requiredCapabilities,
            AlertRuleScope.Trace,
            groupingKeys,
            "trace",
            thresholds,
            EngineSuppressions.Concat(suppressions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            SourceSurfaces);

    public static AlertThresholdDefinition Threshold(
        string name,
        string unit,
        decimal minimum,
        decimal maximum,
        decimal warning,
        decimal critical) =>
        new(name, unit, AlertThresholdDirection.HigherIsWorse, minimum, maximum, warning, critical);

    public static IEnumerable<AlertSignal> Signals(AlertNormalizedSnapshot snapshot, AlertSignalKind kind) =>
        snapshot.Signals.Where(signal => signal.Kind == kind)
            .OrderBy(signal => signal.Sequence)
            .ThenBy(signal => signal.ObservedAt)
            .ThenBy(signal => signal.SignalId, StringComparer.Ordinal);

    public static string? Key(AlertSignal signal, string name, AlertComparableKeyKind kind) =>
        signal.ComparableKeys.SingleOrDefault(key => key.Name == name && key.Kind == kind)?.Value;

    public static decimal? Metric(AlertSignal signal, string name, string unit) =>
        signal.Metrics.SingleOrDefault(metric => metric.Name == name && metric.Unit == unit)?.Value;

    public static bool HasIncompleteInterval(AlertNormalizedSnapshot snapshot) =>
        snapshot.CompletenessReasons.Contains("historical_summary_only", StringComparer.Ordinal)
        || snapshot.CompletenessReasons.Contains("ingest_gap", StringComparer.Ordinal);

    public static AlertRuleMatch Match(
        AlertSeverity severity,
        IReadOnlyList<AlertObservedValue> observed,
        IEnumerable<AlertSignal> evidenceSignals)
    {
        var signals = evidenceSignals
            .OrderBy(signal => signal.Sequence)
            .ThenBy(signal => signal.ObservedAt)
            .ThenBy(signal => signal.SignalId, StringComparer.Ordinal)
            .ToArray();
        return new(
            severity,
            observed,
            signals.Select(signal => signal.Evidence).ToArray(),
            signals.Min(signal => signal.ObservedAt),
            signals.Max(signal => signal.ObservedAt));
    }

    public static AlertRuleOutcome Outcome(IReadOnlyList<AlertRuleMatch> matches, string? suppression = null) =>
        new(matches, suppression is null ? [] : [new(suppression)]);
}

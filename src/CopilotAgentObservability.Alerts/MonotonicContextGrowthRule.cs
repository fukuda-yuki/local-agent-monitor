namespace CopilotAgentObservability.Alerts;

internal sealed class MonotonicContextGrowthRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = new(
        "monotonic-context-growth",
        TokenAlertContract.Version,
        "Monotonic context growth",
        "At least three contiguous LLM calls have nondecreasing input tokens and a high last-to-first ratio.",
        ["llm-call-classification", "input-token-count", "model-identity", "token-semantics-version", "tool-schema-generation"],
        AlertRuleScope.Trace,
        ["model-id", "input-token-semantics-version", "tool-schema-generation"],
        "maximal-contiguous-nondecreasing-run",
        [TokenAlertContract.HigherThreshold("context-growth-ratio", TokenAlertContract.Ratio, 1.75m, 2.50m)],
        TokenAlertContract.Suppressions,
        TokenAlertContract.ApplicableSources);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var suppression = TokenAlertContract.SuppressIncomplete(context.Snapshot);
        if (suppression is not null) return suppression;

        var llm = TokenAlertContract.OrderedLlmSignals(context.Snapshot);
        if (TokenAlertContract.HasMixedCommonDimension(llm, true, false, false)) return TokenAlertContract.Suppressed("mixed-evaluation-dimension");
        if (TokenAlertContract.HasOutOfDomainTokenMetric(llm, TokenAlertContract.InputTokens))
            return TokenAlertContract.Suppressed("token-metric-out-of-domain");
        var eligibleCount = llm.Count(IsEligible);
        var runs = Runs(llm).Where(run => run.Count >= 3 && TokenAlertContract.Metric(run[0], TokenAlertContract.InputTokens) > 0).ToArray();
        if (runs.Length == 0) return TokenAlertContract.Suppressed("minimum-sample-unmet");

        var warning = context.EffectiveThresholds["context-growth-ratio.warning"];
        var critical = context.EffectiveThresholds["context-growth-ratio.critical"];
        var matches = new List<AlertRuleMatch>();
        foreach (var run in runs)
        {
            var first = TokenAlertContract.Metric(run[0], TokenAlertContract.InputTokens)!.Value;
            var last = TokenAlertContract.Metric(run[^1], TokenAlertContract.InputTokens)!.Value;
            var ratio = last / first;
            var severity = TokenAlertContract.HigherSeverity(ratio, warning, critical);
            if (severity is not null)
            {
                matches.Add(TokenAlertContract.Match(severity.Value, run, llm.Count, eligibleCount,
                    new("context-growth-ratio", TokenAlertContract.Ratio, ratio),
                    new("first-input-tokens", TokenAlertContract.Tokens, first),
                    new("last-input-tokens", TokenAlertContract.Tokens, last)));
            }
        }

        return TokenAlertContract.Matches(matches);
    }

    private static IReadOnlyList<IReadOnlyList<AlertSignal>> Runs(IReadOnlyList<AlertSignal> signals)
    {
        var runs = new List<IReadOnlyList<AlertSignal>>();
        var current = new List<AlertSignal>();
        string? currentGroup = null;
        foreach (var signal in signals)
        {
            if (!IsEligible(signal) || !TokenAlertContract.TryCommonGroup(signal, true, false, false, out var group))
            {
                AddRun(runs, current);
                currentGroup = null;
                continue;
            }

            var input = TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens)!.Value;
            var previous = current.Count == 0 ? null : TokenAlertContract.Metric(current[^1], TokenAlertContract.InputTokens);
            if (currentGroup != group || previous is not null && input < previous)
            {
                AddRun(runs, current);
            }

            currentGroup = group;
            current.Add(signal);
        }

        AddRun(runs, current);
        return runs;
    }

    private static bool IsEligible(AlertSignal signal) => signal.Status == AlertSignalStatus.Success
        && TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens) is >= 0
        && TokenAlertContract.TryCommonGroup(signal, true, false, false, out _);

    private static void AddRun(ICollection<IReadOnlyList<AlertSignal>> runs, List<AlertSignal> current)
    {
        if (current.Count > 0) runs.Add(current.ToArray());
        current.Clear();
    }
}

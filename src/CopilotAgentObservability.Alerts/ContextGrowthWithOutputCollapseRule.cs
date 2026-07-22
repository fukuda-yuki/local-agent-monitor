namespace CopilotAgentObservability.Alerts;

internal sealed class ContextGrowthWithOutputCollapseRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = new(
        "context-growth-with-output-collapse",
        TokenAlertContract.Version,
        "Context growth with output collapse",
        "Later LLM calls have higher median input and a lower median per-turn output-to-input ratio.",
        ["llm-call-classification", "input-token-count", "output-token-count", "model-identity", "token-semantics-version", "tool-schema-generation"],
        AlertRuleScope.Trace,
        ["model-id", "input-token-semantics-version", "output-token-semantics-version", "tool-schema-generation"],
        "eligible-evaluation-halves",
        [
            TokenAlertContract.HigherThreshold("context-half-growth-ratio", TokenAlertContract.Ratio, 1.50m, 2.00m),
            TokenAlertContract.LowerThreshold("output-input-collapse-ratio", TokenAlertContract.Ratio, 0.50m, 0.30m),
        ],
        TokenAlertContract.Suppressions,
        TokenAlertContract.ApplicableSources);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var suppression = TokenAlertContract.SuppressIncomplete(context.Snapshot);
        if (suppression is not null) return suppression;

        var llm = TokenAlertContract.OrderedLlmSignals(context.Snapshot);
        if (TokenAlertContract.HasMixedCommonDimension(llm, true, true, false)) return TokenAlertContract.Suppressed("mixed-evaluation-dimension");
        if (TokenAlertContract.HasOutOfDomainTokenMetric(llm, TokenAlertContract.InputTokens, TokenAlertContract.OutputTokens))
            return TokenAlertContract.Suppressed("token-metric-out-of-domain");
        var eligible = llm.Where(IsEligible).ToArray();
        var groups = eligible.GroupBy(signal =>
        {
            TokenAlertContract.TryCommonGroup(signal, true, true, false, out var group);
            return group;
        }, StringComparer.Ordinal).Where(group => group.Count() >= 4).ToArray();
        if (groups.Length == 0) return TokenAlertContract.Suppressed("minimum-sample-unmet");

        var growthWarning = context.EffectiveThresholds["context-half-growth-ratio.warning"];
        var growthCritical = context.EffectiveThresholds["context-half-growth-ratio.critical"];
        var collapseWarning = context.EffectiveThresholds["output-input-collapse-ratio.warning"];
        var collapseCritical = context.EffectiveThresholds["output-input-collapse-ratio.critical"];
        var matches = new List<AlertRuleMatch>();
        foreach (var group in groups)
        {
            var ordered = group.ToArray();
            var half = ordered.Length / 2;
            var first = ordered.Take(half).ToArray();
            var last = ordered.TakeLast(half).ToArray();
            var firstMedianInput = TokenAlertContract.Median(first.Select(Input));
            var lastMedianInput = TokenAlertContract.Median(last.Select(Input));
            var firstMedianOutputRatio = TokenAlertContract.Median(first.Select(OutputRatio));
            if (firstMedianInput <= 0 || firstMedianOutputRatio <= 0) continue;

            var lastMedianOutputRatio = TokenAlertContract.Median(last.Select(OutputRatio));
            var growth = lastMedianInput / firstMedianInput;
            var collapse = lastMedianOutputRatio / firstMedianOutputRatio;
            var severity = growth >= growthCritical && collapse <= collapseCritical
                ? AlertSeverity.Critical
                : growth >= growthWarning && collapse <= collapseWarning
                    ? AlertSeverity.Warning
                    : (AlertSeverity?)null;
            if (severity is null) continue;

            var included = first.Concat(last).ToArray();
            matches.Add(TokenAlertContract.Match(severity.Value, included, llm.Count, eligible.Length,
                new("context-half-growth-ratio", TokenAlertContract.Ratio, growth),
                new("output-input-collapse-ratio", TokenAlertContract.Ratio, collapse),
                new("first-half-median-input", TokenAlertContract.Tokens, firstMedianInput),
                new("last-half-median-input", TokenAlertContract.Tokens, lastMedianInput),
                new("first-half-median-output-input-ratio", TokenAlertContract.Ratio, firstMedianOutputRatio),
                new("last-half-median-output-input-ratio", TokenAlertContract.Ratio, lastMedianOutputRatio)));
        }

        return TokenAlertContract.Matches(matches);
    }

    private static bool IsEligible(AlertSignal signal) => signal.Status == AlertSignalStatus.Success
        && TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens) is > 0
        && TokenAlertContract.Metric(signal, TokenAlertContract.OutputTokens) is >= 0
        && TokenAlertContract.TryCommonGroup(signal, true, true, false, out _);

    private static decimal Input(AlertSignal signal) => TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens)!.Value;
    private static decimal OutputRatio(AlertSignal signal) => TokenAlertContract.Metric(signal, TokenAlertContract.OutputTokens)!.Value / Input(signal);
}

namespace CopilotAgentObservability.Alerts;

internal sealed class LowCacheReadRatioRule : IAlertRule
{
    private const decimal MinimumPostFirstInput = 10_000m;

    public AlertRuleDescriptor Descriptor { get; } = new(
        "low-cache-read-ratio",
        TokenAlertContract.Version,
        "Low cache read ratio",
        "Post-first eligible LLM calls read a low fraction of their input tokens from cache.",
        ["llm-call-classification", "input-token-count", "cache-read-token-count", "model-identity", "token-semantics-version"],
        AlertRuleScope.Trace,
        ["model-id", "input-token-semantics-version", "cache-read-token-semantics-version"],
        "post-first-eligible-turn-per-evaluation-dimension",
        [TokenAlertContract.LowerThreshold("cache-read-ratio", TokenAlertContract.Ratio, 0.20m, 0.05m)],
        TokenAlertContract.Suppressions,
        TokenAlertContract.ApplicableSources);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var suppression = TokenAlertContract.SuppressIncomplete(context.Snapshot);
        if (suppression is not null) return suppression;

        var llm = TokenAlertContract.OrderedLlmSignals(context.Snapshot);
        if (TokenAlertContract.HasMixedCommonDimension(llm, false, false, true)) return TokenAlertContract.Suppressed("mixed-evaluation-dimension");
        var eligible = llm.Where(IsEligible).ToArray();
        var groups = eligible.GroupBy(signal =>
        {
            TokenAlertContract.TryCommonGroup(signal, false, false, true, out var group);
            return group;
        }, StringComparer.Ordinal).Where(group => group.Count() >= 3).ToArray();
        if (groups.Length == 0) return TokenAlertContract.Suppressed("minimum-sample-unmet");

        var warning = context.EffectiveThresholds["cache-read-ratio.warning"];
        var critical = context.EffectiveThresholds["cache-read-ratio.critical"];
        var hadMinimumInput = false;
        var matches = new List<AlertRuleMatch>();
        foreach (var group in groups)
        {
            var included = group.Skip(1).ToArray();
            var totalInput = included.Sum(signal => TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens)!.Value);
            if (totalInput < MinimumPostFirstInput) continue;
            hadMinimumInput = true;
            var totalCache = included.Sum(signal => TokenAlertContract.Metric(signal, TokenAlertContract.CacheReadTokens)!.Value);
            var ratio = totalCache / totalInput;
            var severity = TokenAlertContract.LowerSeverity(ratio, warning, critical);
            if (severity is not null)
            {
                matches.Add(TokenAlertContract.Match(severity.Value, included, llm.Count, eligible.Length,
                    new("cache-read-ratio", TokenAlertContract.Ratio, ratio),
                    new("included-input-tokens", TokenAlertContract.Tokens, totalInput),
                    new("included-cache-read-tokens", TokenAlertContract.Tokens, totalCache)));
            }
        }

        return hadMinimumInput ? TokenAlertContract.Matches(matches) : TokenAlertContract.Suppressed("minimum-sample-unmet");
    }

    private static bool IsEligible(AlertSignal signal) => signal.Status == AlertSignalStatus.Success
        && TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens) is >= 0
        && TokenAlertContract.Metric(signal, TokenAlertContract.CacheReadTokens) is >= 0
        && TokenAlertContract.TryCommonGroup(signal, false, false, true, out _);
}

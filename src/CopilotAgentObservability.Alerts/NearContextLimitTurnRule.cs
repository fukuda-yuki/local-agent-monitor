namespace CopilotAgentObservability.Alerts;

internal sealed class NearContextLimitTurnRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = new(
        "near-context-limit-turn",
        TokenAlertContract.Version,
        "Near context limit turn",
        "An exact LLM call uses a high fraction of an explicit effective context limit.",
        ["llm-call-classification", "input-token-count", "model-identity", "token-semantics-version", "effective-context-limit", "effective-context-limit-authority", "effective-context-limit-version"],
        AlertRuleScope.Trace,
        ["model-id", "input-token-semantics-version", "effective-context-limit-authority", "effective-context-limit-version", "effective-context-limit"],
        "each-eligible-turn-per-evaluation-dimension",
        [TokenAlertContract.HigherThreshold("context-limit-utilization", TokenAlertContract.Fraction, 0.75m, 0.90m)],
        TokenAlertContract.Suppressions,
        TokenAlertContract.ApplicableSources);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var suppression = TokenAlertContract.SuppressIncomplete(context.Snapshot);
        if (suppression is not null) return suppression;

        var llm = TokenAlertContract.OrderedLlmSignals(context.Snapshot);
        if (TokenAlertContract.HasMixedLimitDimension(llm)) return TokenAlertContract.Suppressed("mixed-evaluation-dimension");
        var eligible = llm.Where(signal => signal.Status == AlertSignalStatus.Success
                && TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens) is >= 0
                && TokenAlertContract.TryLimitGroup(signal, out _, out _))
            .ToArray();
        if (eligible.Length == 0) return TokenAlertContract.Suppressed("minimum-sample-unmet");

        var warning = context.EffectiveThresholds["context-limit-utilization.warning"];
        var critical = context.EffectiveThresholds["context-limit-utilization.critical"];
        var matches = new List<AlertRuleMatch>();
        foreach (var signal in eligible)
        {
            TokenAlertContract.TryLimitGroup(signal, out _, out var limit);
            var input = TokenAlertContract.Metric(signal, TokenAlertContract.InputTokens)!.Value;
            var utilization = input / limit;
            var severity = TokenAlertContract.HigherSeverity(utilization, warning, critical);
            if (severity is not null)
            {
                matches.Add(TokenAlertContract.Match(severity.Value, [signal], llm.Count, eligible.Length,
                    new("context-limit-utilization", TokenAlertContract.Fraction, utilization),
                    new("input-tokens", TokenAlertContract.Tokens, input),
                    new("effective-context-limit", TokenAlertContract.Tokens, limit)));
            }
        }

        return TokenAlertContract.Matches(matches);
    }
}

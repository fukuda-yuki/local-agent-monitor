namespace CopilotAgentObservability.Alerts;

internal sealed class HighInitialContextUtilizationRule : IAlertRule
{
    public AlertRuleDescriptor Descriptor { get; } = new(
        "high-initial-context-utilization",
        TokenAlertContract.Version,
        "High initial context utilization",
        "The first successful LLM call uses a high fraction of an explicit effective context limit.",
        ["llm-call-classification", "input-token-count", "model-identity", "token-semantics-version", "effective-context-limit", "effective-context-limit-authority", "effective-context-limit-version"],
        AlertRuleScope.Trace,
        ["model-id", "input-token-semantics-version", "effective-context-limit-authority", "effective-context-limit-version", "effective-context-limit"],
        "first-successful-llm-call",
        [TokenAlertContract.HigherThreshold("initial-context-utilization", TokenAlertContract.Fraction, 0.50m, 0.80m)],
        TokenAlertContract.Suppressions,
        TokenAlertContract.ApplicableSources);

    public AlertRuleOutcome Evaluate(AlertRuleContext context)
    {
        var suppression = TokenAlertContract.SuppressIncomplete(context.Snapshot);
        if (suppression is not null) return suppression;

        var llm = TokenAlertContract.OrderedLlmSignals(context.Snapshot);
        if (TokenAlertContract.HasMixedLimitDimension(llm)) return TokenAlertContract.Suppressed("mixed-evaluation-dimension");
        if (TokenAlertContract.HasOutOfDomainTokenMetric(llm, TokenAlertContract.InputTokens, TokenAlertContract.EffectiveContextLimit))
            return TokenAlertContract.Suppressed("token-metric-out-of-domain");
        var initial = llm.FirstOrDefault(signal => signal.Status == AlertSignalStatus.Success);
        if (initial is null
            || TokenAlertContract.Metric(initial, TokenAlertContract.InputTokens) is not >= 0
            || !TokenAlertContract.TryLimitGroup(initial, out _, out var limit))
        {
            return TokenAlertContract.Suppressed("minimum-sample-unmet");
        }

        var warning = context.EffectiveThresholds["initial-context-utilization.warning"];
        var critical = context.EffectiveThresholds["initial-context-utilization.critical"];
        var input = TokenAlertContract.Metric(initial, TokenAlertContract.InputTokens)!.Value;
        var utilization = input / limit;
        var severity = TokenAlertContract.HigherSeverity(utilization, warning, critical);
        if (severity is null)
        {
            return TokenAlertContract.Matches([]);
        }

        return TokenAlertContract.Matches(
        [
            TokenAlertContract.Match(severity.Value, [initial], llm.Count, 1,
                new("initial-context-utilization", TokenAlertContract.Fraction, utilization),
                new("input-tokens", TokenAlertContract.Tokens, input),
                new("effective-context-limit", TokenAlertContract.Tokens, limit)),
        ]);
    }
}

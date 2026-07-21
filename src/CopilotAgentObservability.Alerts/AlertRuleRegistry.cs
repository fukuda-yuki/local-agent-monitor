namespace CopilotAgentObservability.Alerts;

public sealed class AlertRuleRegistry
{
    public AlertRuleRegistry(IEnumerable<IAlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = Array.AsReadOnly(rules.Select(rule => new FrozenRule(rule, AlertFreezer.Descriptor(rule.Descriptor)))
            .OrderBy(rule => rule.Descriptor.RuleId, StringComparer.Ordinal)
            .ThenBy(rule => rule.Descriptor.RuleVersion, StringComparer.Ordinal)
            .Cast<IAlertRule>()
            .ToArray());

        foreach (var rule in Rules)
        {
            AlertValidation.ValidateDescriptor(rule.Descriptor);
        }

        if (Rules.GroupBy(rule => (rule.Descriptor.RuleId, rule.Descriptor.RuleVersion))
            .Any(group => group.Count() != 1))
        {
            throw AlertValidation.InvalidRegistry("Duplicate rule ID/version.");
        }
    }

    public IReadOnlyList<IAlertRule> Rules { get; }

    private sealed class FrozenRule(IAlertRule implementation, AlertRuleDescriptor descriptor) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => implementation.Evaluate(context);
    }
}

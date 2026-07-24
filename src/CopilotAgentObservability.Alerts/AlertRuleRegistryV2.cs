namespace CopilotAgentObservability.Alerts;

public sealed class AlertRuleRegistryV2
{
    public AlertRuleRegistryV2()
    {
        Rules = Array.AsReadOnly<IAlertRuleV2>(
        [
            new BudgetThresholdRuleV2(new(
                "session-estimated-cost-threshold",
                "1",
                "Estimated Session cost threshold",
                "Compares one Session's estimated USD amount with configured warning and critical thresholds.",
                "estimated USD amount >= configured threshold",
                AlertCostScopeKindV2.Session,
                "session")),
            new BudgetThresholdRuleV2(new(
                "daily-estimated-cost-threshold",
                "1",
                "Estimated daily cost threshold",
                "Compares estimated USD amount across one UTC calendar day with configured warning and critical thresholds.",
                "estimated USD amount >= configured threshold",
                AlertCostScopeKindV2.UtcDay,
                "utc_day")),
            new BudgetThresholdRuleV2(new(
                "period-estimated-cost-threshold",
                "1",
                "Estimated rolling-period cost threshold",
                "Compares estimated USD amount across one configured rolling period with warning and critical thresholds.",
                "estimated USD amount >= configured threshold",
                AlertCostScopeKindV2.RollingPeriod,
                "rolling_period")),
        ]);
    }

    public IReadOnlyList<IAlertRuleV2> Rules { get; }

    public IAlertRuleV2 Resolve(AlertRuleIdentityV2 identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!AlertValidationV2.Token(identity.RuleId) || identity.RuleVersion != "1")
        {
            throw new AlertContractException("invalid_rule_identity", "Alert rule identity is invalid.");
        }

        return Rules.SingleOrDefault(rule =>
                rule.Descriptor.RuleId == identity.RuleId
                && rule.Descriptor.RuleVersion == identity.RuleVersion)
            ?? throw new AlertContractException("invalid_rule_identity", "Alert rule identity is invalid.");
    }

    private sealed class BudgetThresholdRuleV2(AlertRuleDescriptorV2 descriptor) : IAlertRuleV2
    {
        public AlertRuleDescriptorV2 Descriptor { get; } = descriptor;

        public AlertRuleOutcomeV2 Evaluate(AlertRuleContextV2 context)
        {
            var snapshot = context.Snapshot;
            var configuration = context.Configuration;
            if (snapshot.Scope.Kind != Descriptor.ScopeKind
                || configuration is not null && !WindowMatches(snapshot.Scope, configuration))
            {
                return new(null, "scope_not_applicable");
            }
            if (configuration is null || !configuration.Enabled)
            {
                return new(null, "rule_disabled");
            }
            if (snapshot.AcquisitionState == AlertCostAcquisitionStateV2.Incomplete)
            {
                return new(null, "eligible_set_incomplete");
            }
            if (snapshot.EligibleCount == 0)
            {
                return new(null, "no_eligible_sessions");
            }
            if (snapshot.EstimatedCount == 0)
            {
                return new(null, "no_covered_estimate");
            }
            if (snapshot.AggregateState == AlertCostAggregateStateV2.Unrepresentable)
            {
                return new(null, "aggregate_amount_not_representable");
            }
            if (snapshot.CoverageBasisPoints < configuration.MinimumCoverageBasisPoints)
            {
                return new(null, "insufficient_estimate_coverage");
            }
            if (snapshot.Amount >= configuration.CriticalThreshold)
            {
                return new(AlertSeverity.Critical, null);
            }
            if (snapshot.Amount >= configuration.WarningThreshold)
            {
                return new(AlertSeverity.Warning, null);
            }
            return new(null, null);
        }

        private static bool WindowMatches(
            AlertCostScopeV2 scope,
            AlertBudgetRuleConfigurationV2 configuration)
        {
            if (scope.Kind != configuration.ScopeKind) return false;
            if (scope.Kind != AlertCostScopeKindV2.RollingPeriod) return true;
            return scope.WindowStartUtc is not null
                && scope.WindowEndUtc is not null
                && (scope.WindowEndUtc.Value - scope.WindowStartUtc.Value).TotalDays
                    == configuration.WindowDays;
        }
    }
}

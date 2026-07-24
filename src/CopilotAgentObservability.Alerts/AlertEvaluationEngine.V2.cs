using System.Text;

namespace CopilotAgentObservability.Alerts;

public sealed partial class AlertEvaluationEngine
{
    private readonly AlertRuleRegistryV2? _registryV2;
    private readonly IAlertEvidenceResolverV2? _evidenceResolverV2;

    public AlertEvaluationEngine(
        AlertRuleRegistryV2 registry,
        IAlertEvidenceResolverV2 evidenceResolver)
    {
        _registryV2 = registry ?? throw new ArgumentNullException(nameof(registry));
        _evidenceResolverV2 = evidenceResolver ?? throw new ArgumentNullException(nameof(evidenceResolver));
    }

    public AlertEvaluationEngineResultV2 Evaluate(
        AlertRuleIdentityV2 selectedRule,
        AlertNormalizedSnapshotV2 snapshot,
        AlertEngineConfigurationV2 configuration,
        AlertEvidenceResolutionScopeV2 evidenceScope)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(evidenceScope);
            AlertValidationV2.ValidateSnapshot(snapshot);
            var registry = _registryV2
                ?? throw new AlertContractException("invalid_rule_registry", "Alert rule registry is invalid.");
            var rule = registry.Resolve(selectedRule);
            var configured = AlertValidationV2.ValidateConfiguration(registry, configuration);
            configured.TryGetValue((selectedRule.RuleId, selectedRule.RuleVersion), out var ruleConfiguration);

            foreach (var evidence in snapshot.Evidence)
            {
                AlertEvidenceResolutionStatusV2 resolution;
                try
                {
                    resolution = _evidenceResolverV2!.Resolve(evidence, evidenceScope);
                }
                catch (Exception exception) when (IsNonFatal(exception))
                {
                    return new(AlertEvaluationEngineStatusV2.ContractRejected, Code: "alert_contract_rejected");
                }

                if (resolution != AlertEvidenceResolutionStatusV2.Resolved)
                {
                    return resolution switch
                    {
                        AlertEvidenceResolutionStatusV2.Unresolved =>
                            new(AlertEvaluationEngineStatusV2.UnresolvedEvidence, Code: "unresolved_evidence"),
                        AlertEvidenceResolutionStatusV2.StoreFailure =>
                            new(AlertEvaluationEngineStatusV2.StoreFailure, Code: "alert_store_unavailable"),
                        _ => new(AlertEvaluationEngineStatusV2.ContractRejected, Code: "alert_contract_rejected"),
                    };
                }
            }

            var snapshotBytes = AlertCanonicalJsonV2.SerializeSnapshot(snapshot);
            var configurationBytes = AlertCanonicalJsonV2.SerializeConfiguration(configuration);
            EnsureWithinLimit(snapshotBytes);
            EnsureWithinLimit(configurationBytes);
            var configurationHash = AlertHashing.Sha256(AlertHashing.Frame(
                Encoding.UTF8.GetBytes("alert-configuration/v2"),
                configurationBytes));
            var inputHash = AlertHashing.Sha256(AlertHashing.Frame(
                Encoding.UTF8.GetBytes("alert-input/v2"),
                Encoding.UTF8.GetBytes(selectedRule.RuleId),
                Encoding.UTF8.GetBytes(selectedRule.RuleVersion),
                snapshotBytes,
                configurationBytes));
            var evaluationId = AlertHashing.Identifier(
                "alert-evaluation/v2",
                inputHash,
                configurationHash,
                selectedRule.RuleId,
                selectedRule.RuleVersion);

            var outcome = rule.Evaluate(new(snapshot, ruleConfiguration, rule.Descriptor));
            if (outcome.Severity is not null && outcome.SuppressionCode is not null
                || outcome.Severity is not null && !Enum.IsDefined(outcome.Severity.Value)
                || outcome.SuppressionCode is not null && !AlertValidationV2.IsSuppressionCode(outcome.SuppressionCode))
            {
                return new(AlertEvaluationEngineStatusV2.ContractRejected, Code: "invalid_rule_output");
            }

            IReadOnlyList<AlertReceiptV2> receipts = [];
            IReadOnlyList<AlertSuppressionV2> suppressions = [];
            if (outcome.Severity is not null)
            {
                var receipt = CreateReceipt(
                    evaluationId,
                    inputHash,
                    configurationHash,
                    rule.Descriptor,
                    outcome.Severity.Value,
                    snapshot,
                    configuration,
                    ruleConfiguration!);
                receipts = Array.AsReadOnly([receipt]);
            }
            else if (outcome.SuppressionCode is not null)
            {
                suppressions = Array.AsReadOnly(
                [
                    CreateSuppression(
                        evaluationId,
                        configurationHash,
                        rule.Descriptor,
                        outcome.SuppressionCode,
                        snapshot,
                        configuration),
                ]);
            }

            var evaluation = new AlertEvaluationResultV2(
                AlertContractVersionsV2.Evaluation,
                evaluationId,
                inputHash,
                configuration.ConfigurationVersion,
                configurationHash,
                selectedRule.RuleId,
                selectedRule.RuleVersion,
                configuration.SourceCostConfigurationId,
                configuration.SourceConfigurationHeadRevision,
                configuration.SourceConfigurationCatalogSha256,
                snapshot.Scope.Kind,
                snapshot.Scope.ScopeId,
                snapshot.Scope.WindowStartUtc,
                snapshot.Scope.WindowEndUtc,
                snapshot.EligibilityDigest,
                snapshot.Currency,
                snapshot.AggregateState,
                snapshot.EligibleCount,
                snapshot.EstimatedCount,
                snapshot.PartialCount,
                snapshot.NotEstimableCount,
                snapshot.MissingCount,
                snapshot.FailedCount,
                snapshot.UnavailableCount,
                snapshot.StaleCount,
                snapshot.CoverageBasisPoints,
                snapshot.FirstObservedAt,
                snapshot.LastObservedAt,
                receipts,
                suppressions,
                []);
            AlertValidationV2.ValidateEvaluation(evaluation);
            EnsureWithinLimit(AlertCanonicalJsonV2.SerializeEvaluation(evaluation));
            return new(AlertEvaluationEngineStatusV2.Success, evaluation);
        }
        catch (AlertContractException exception)
        {
            return new(
                AlertEvaluationEngineStatusV2.ContractRejected,
                Code: AlertValidationV2.Token(exception.Code) ? exception.Code : "alert_contract_rejected");
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return new(AlertEvaluationEngineStatusV2.ContractRejected, Code: "alert_contract_rejected");
        }
    }

    private static AlertReceiptV2 CreateReceipt(
        string evaluationId,
        string inputHash,
        string configurationHash,
        AlertRuleDescriptorV2 descriptor,
        AlertSeverity severity,
        AlertNormalizedSnapshotV2 snapshot,
        AlertEngineConfigurationV2 configuration,
        AlertBudgetRuleConfigurationV2 ruleConfiguration)
    {
        var withoutIdentity = new AlertReceiptV2(
            AlertContractVersionsV2.Receipt,
            AlertContractVersionsV2.SanitizedReceiptProfile,
            string.Empty,
            evaluationId,
            descriptor.RuleId,
            descriptor.RuleVersion,
            severity,
            AlertInitialState.Open,
            snapshot.SourceSurface,
            snapshot.SourceVersion,
            Freeze(snapshot.Scope),
            Freeze(snapshot.Evidence),
            snapshot.Currency!,
            snapshot.AggregateState,
            snapshot.Amount!.Value,
            ruleConfiguration.WarningThreshold,
            ruleConfiguration.CriticalThreshold,
            snapshot.EligibleCount!.Value,
            snapshot.EstimatedCount!.Value,
            snapshot.PartialCount!.Value,
            snapshot.NotEstimableCount!.Value,
            snapshot.MissingCount!.Value,
            snapshot.FailedCount!.Value,
            snapshot.UnavailableCount!.Value,
            snapshot.StaleCount!.Value,
            snapshot.CoverageNumerator!.Value,
            snapshot.CoverageDenominator!.Value,
            snapshot.CoverageBasisPoints!.Value,
            Freeze(snapshot.Members),
            configuration.SourceCostConfigurationId,
            configuration.SourceConfigurationHeadRevision,
            configuration.SourceConfigurationCatalogSha256,
            configuration.ConfigurationVersion,
            configurationHash,
            snapshot.Completeness,
            Array.AsReadOnly(snapshot.CompletenessReasons.ToArray()),
            snapshot.FirstObservedAt!.Value,
            snapshot.LastObservedAt!.Value,
            inputHash,
            descriptor.Title);
        return withoutIdentity with { AlertId = AlertReceiptIdentityV2.Create(withoutIdentity) };
    }

    private static AlertSuppressionV2 CreateSuppression(
        string evaluationId,
        string configurationHash,
        AlertRuleDescriptorV2 descriptor,
        string code,
        AlertNormalizedSnapshotV2 snapshot,
        AlertEngineConfigurationV2 configuration) =>
        new(
            AlertContractVersionsV2.Suppression,
            evaluationId,
            descriptor.RuleId,
            descriptor.RuleVersion,
            code,
            configuration.SourceCostConfigurationId,
            configuration.SourceConfigurationHeadRevision,
            configuration.SourceConfigurationCatalogSha256,
            configuration.ConfigurationVersion,
            configurationHash,
            snapshot.Scope.Kind,
            snapshot.Scope.ScopeId,
            snapshot.Scope.WindowStartUtc,
            snapshot.Scope.WindowEndUtc,
            snapshot.EligibilityDigest,
            snapshot.Currency,
            snapshot.AggregateState,
            snapshot.EligibleCount,
            snapshot.EstimatedCount,
            snapshot.PartialCount,
            snapshot.NotEstimableCount,
            snapshot.MissingCount,
            snapshot.FailedCount,
            snapshot.UnavailableCount,
            snapshot.StaleCount,
            snapshot.CoverageBasisPoints,
            snapshot.FirstObservedAt,
            snapshot.LastObservedAt);

    private static AlertCostScopeV2 Freeze(AlertCostScopeV2 scope) =>
        scope with { SessionIds = Array.AsReadOnly(scope.SessionIds.ToArray()) };

    private static IReadOnlyList<AlertEvidenceReferenceV2> Freeze(
        IReadOnlyList<AlertEvidenceReferenceV2> evidence) =>
        Array.AsReadOnly(evidence.Select(item => item with { }).ToArray());

    private static IReadOnlyList<AlertCostMemberV2> Freeze(
        IReadOnlyList<AlertCostMemberV2> members) =>
        Array.AsReadOnly(members.Select(item => item with { }).ToArray());

    private static void EnsureWithinLimit(byte[] bytes)
    {
        if (bytes.Length > 8_388_608)
        {
            throw new AlertContractException("contract_too_large", "Alert contract is too large.");
        }
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

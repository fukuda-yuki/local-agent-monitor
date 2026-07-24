namespace CopilotAgentObservability.Alerts;

public enum AlertEvaluationApplicationStatusV2
{
    Success,
    InitializationBusy,
    InitializationUnavailable,
    UnresolvedEvidence,
    EvidenceStoreFailure,
    AppendBusy,
    AppendUnavailable,
    AppendConflict,
    ContractRejected,
}

public sealed class AlertEvaluationOutcomeV2
{
    internal AlertEvaluationOutcomeV2(AlertEvaluationResultV2 evaluation)
    {
        EvaluationId = evaluation.EvaluationId;
        InputHash = evaluation.InputHash;
        ConfigurationVersion = evaluation.ConfigurationVersion;
        ConfigurationHash = evaluation.ConfigurationHash;
        ReceiptIds = Array.AsReadOnly(
            evaluation.Receipts.Select(item => item.AlertId).ToArray());
        Suppressions = Array.AsReadOnly(
            evaluation.Suppressions
                .Select(item => new AlertSuppressionProjectionV2(item))
                .ToArray());
    }

    public string EvaluationId { get; }
    public string InputHash { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public IReadOnlyList<string> ReceiptIds { get; }
    public IReadOnlyList<AlertSuppressionProjectionV2> Suppressions { get; }
}

public sealed record AlertEvaluationApplicationResultV2(
    AlertEvaluationApplicationStatusV2 Status,
    string? Code = null,
    AlertEvaluationOutcomeV2? Outcome = null);

public sealed partial class AlertEvaluationApplication
{
    private readonly AlertEvaluationEngine? _engineV2;
    private readonly AlertEngineConfigurationV2? _configurationV2;
    private readonly IAlertEngineStoreV2? _storeV2;

    public AlertEvaluationApplication(
        AlertRuleRegistryV2 registry,
        AlertEngineConfigurationV2 configuration,
        IAlertEvidenceResolverV2 evidenceResolver,
        IAlertEngineStoreV2 store)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(evidenceResolver);
        ArgumentNullException.ThrowIfNull(store);

        AlertValidationV2.ValidateConfiguration(registry, configuration);
        _engineV2 = new(registry, evidenceResolver);
        _configurationV2 = configuration with
        {
            Rules = Array.AsReadOnly(
                configuration.Rules.Select(item => item with { }).ToArray()),
        };
        _storeV2 = store;
    }

    public AlertEvaluationApplicationResultV2 EvaluateAndAppend(
        AlertRuleIdentityV2 selectedRule,
        AlertNormalizedSnapshotV2 snapshot)
    {
        if (selectedRule is null || snapshot is null)
        {
            return new(
                AlertEvaluationApplicationStatusV2.ContractRejected,
                "alert_contract_rejected");
        }

        AlertEngineStoreResultV2 initialization;
        try
        {
            initialization = _storeV2!.InitializeV2();
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return InitializationUnavailableV2();
        }

        if (initialization.Status != AlertEngineStoreStatusV2.Success)
        {
            return initialization.Status == AlertEngineStoreStatusV2.Busy
                && initialization.Code == "alert_store_busy"
                ? new(
                    AlertEvaluationApplicationStatusV2.InitializationBusy,
                    "alert_store_busy")
                : InitializationUnavailableV2();
        }

        AlertEvaluationEngineResultV2 evaluated;
        try
        {
            evaluated = _engineV2!.Evaluate(
                selectedRule,
                snapshot,
                _configurationV2!,
                new(_storeV2!, []));
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return new(
                AlertEvaluationApplicationStatusV2.ContractRejected,
                "alert_contract_rejected");
        }

        if (evaluated.Status != AlertEvaluationEngineStatusV2.Success)
        {
            return evaluated.Status switch
            {
                AlertEvaluationEngineStatusV2.UnresolvedEvidence => new(
                    AlertEvaluationApplicationStatusV2.UnresolvedEvidence,
                    "unresolved_evidence"),
                AlertEvaluationEngineStatusV2.StoreFailure => new(
                    AlertEvaluationApplicationStatusV2.EvidenceStoreFailure,
                    "alert_store_unavailable"),
                _ => new(
                    AlertEvaluationApplicationStatusV2.ContractRejected,
                    evaluated.Code is not null && AlertValidationV2.Token(evaluated.Code)
                        ? evaluated.Code
                        : "alert_contract_rejected"),
            };
        }

        AlertEngineStoreResultV2 appended;
        try
        {
            appended = _storeV2!.Append(evaluated.Evaluation!);
        }
        catch (Exception exception) when (IsNonFatalV2(exception))
        {
            return AppendUnavailableV2();
        }

        return appended.Status switch
        {
            AlertEngineStoreStatusV2.Success when appended.Code is null => new(
                AlertEvaluationApplicationStatusV2.Success,
                Outcome: new(evaluated.Evaluation!)),
            AlertEngineStoreStatusV2.Busy when appended.Code == "alert_store_busy" =>
                new(
                    AlertEvaluationApplicationStatusV2.AppendBusy,
                    "alert_store_busy"),
            AlertEngineStoreStatusV2.Conflict when appended.Code == "alert_store_conflict" =>
                new(
                    AlertEvaluationApplicationStatusV2.AppendConflict,
                    "alert_store_conflict"),
            AlertEngineStoreStatusV2.ContractRejected => new(
                AlertEvaluationApplicationStatusV2.ContractRejected,
                "alert_contract_rejected"),
            _ => AppendUnavailableV2(),
        };
    }

    private static AlertEvaluationApplicationResultV2 InitializationUnavailableV2() =>
        new(
            AlertEvaluationApplicationStatusV2.InitializationUnavailable,
            "alert_store_unavailable");

    private static AlertEvaluationApplicationResultV2 AppendUnavailableV2() =>
        new(
            AlertEvaluationApplicationStatusV2.AppendUnavailable,
            "alert_store_unavailable");

    private static bool IsNonFatalV2(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}

using System.Collections.ObjectModel;

namespace CopilotAgentObservability.Alerts;

public enum AlertEvaluationApplicationStatus
{
    Success,
    InitializationBusy,
    InitializationUnavailable,
    AppendBusy,
    AppendUnavailable,
    AppendConflict,
    ContractRejected,
}

public sealed record AlertEvaluationApplicationIdentity(
    string EvaluationId,
    string InputHash,
    string ConfigurationVersion,
    string ConfigurationHash,
    int ReceiptCount,
    int SuppressionCount,
    int RejectedMatchCount);

public sealed class AlertRejectedMatchProjectionV1
{
    internal AlertRejectedMatchProjectionV1(AlertRejectedMatch rejectedMatch)
    {
        RuleId = rejectedMatch.RuleId;
        RuleVersion = rejectedMatch.RuleVersion;
        Code = rejectedMatch.Code;
    }

    public string RuleId { get; }
    public string RuleVersion { get; }
    public string Code { get; }
}

public sealed class AlertEvaluationOutcomeV1
{
    internal AlertEvaluationOutcomeV1(AlertEvaluationResult evaluation)
    {
        EvaluationId = evaluation.EvaluationId;
        InputHash = evaluation.InputHash;
        ConfigurationVersion = evaluation.ConfigurationVersion;
        ConfigurationHash = evaluation.ConfigurationHash;
        ReceiptIds = Array.AsReadOnly(evaluation.Receipts.Select(item => item.AlertId).ToArray());
        Suppressions = Array.AsReadOnly(evaluation.Suppressions.Select(item => new AlertSuppressionProjectionV1(item)).ToArray());
        RejectedMatches = Array.AsReadOnly(evaluation.RejectedMatches.Select(item => new AlertRejectedMatchProjectionV1(item)).ToArray());
        Identity = new(
            EvaluationId,
            InputHash,
            ConfigurationVersion,
            ConfigurationHash,
            ReceiptIds.Count,
            Suppressions.Count,
            RejectedMatches.Count);
    }

    public string EvaluationId { get; }
    public string InputHash { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public IReadOnlyList<string> ReceiptIds { get; }
    public IReadOnlyList<AlertSuppressionProjectionV1> Suppressions { get; }
    public IReadOnlyList<AlertRejectedMatchProjectionV1> RejectedMatches { get; }
    public AlertEvaluationApplicationIdentity Identity { get; }
}

public sealed class AlertEvaluationApplicationResult
{
    internal AlertEvaluationApplicationResult(
        AlertEvaluationApplicationStatus status,
        string? code = null,
        AlertEvaluationOutcomeV1? outcome = null)
    {
        Status = status;
        Code = code;
        Outcome = outcome;
    }

    public AlertEvaluationApplicationStatus Status { get; }
    public string? Code { get; }
    public AlertEvaluationApplicationIdentity? Identity => Outcome?.Identity;
    public AlertEvaluationOutcomeV1? Outcome { get; }
}

public sealed class AlertEvaluationApplication
{
    private readonly AlertEvaluationEngine _engine;
    private readonly AlertEngineConfiguration _configuration;
    private readonly IAlertEngineStore _store;

    public AlertEvaluationApplication(
        AlertRuleRegistry registry,
        AlertEngineConfiguration configuration,
        IAlertEvidenceResolver evidenceResolver,
        IAlertEngineStore store)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(evidenceResolver);
        ArgumentNullException.ThrowIfNull(store);

        _engine = new AlertEvaluationEngine(registry, evidenceResolver);
        _configuration = Freeze(configuration);
        _store = store;
    }

    public AlertEvaluationApplicationResult EvaluateAndAppend(AlertNormalizedSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return Rejected("invalid_snapshot");
        }

        AlertStoreResult initialization;
        try
        {
            initialization = _store.Initialize();
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return InitializationUnavailable();
        }

        var initializationFailure = MapInitialization(initialization);
        if (initializationFailure is not null)
        {
            return initializationFailure;
        }

        AlertEvaluationResult evaluation;
        try
        {
            evaluation = _engine.Evaluate(snapshot, _configuration);
        }
        catch (AlertContractException exception)
        {
            return Rejected(AlertValidation.IsToken(exception.Code) ? exception.Code : "alert_contract_rejected");
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Rejected("alert_contract_rejected");
        }

        AlertStoreResult append;
        try
        {
            append = _store.Append(evaluation);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return AppendUnavailable();
        }

        return append switch
        {
            { Status: AlertStoreStatus.Success, Code: null } => new(
                AlertEvaluationApplicationStatus.Success,
                outcome: new AlertEvaluationOutcomeV1(evaluation)),
            { Status: AlertStoreStatus.Busy, Code: "alert_store_busy" } =>
                new(AlertEvaluationApplicationStatus.AppendBusy, "alert_store_busy"),
            { Status: AlertStoreStatus.Unavailable, Code: "alert_store_unavailable" } => AppendUnavailable(),
            { Status: AlertStoreStatus.Conflict, Code: "alert_store_conflict" } =>
                new(AlertEvaluationApplicationStatus.AppendConflict, "alert_store_conflict"),
            _ => AppendUnavailable(),
        };
    }

    private static AlertEvaluationApplicationResult? MapInitialization(AlertStoreResult? result) => result switch
    {
        { Status: AlertStoreStatus.Success, Code: null } => null,
        { Status: AlertStoreStatus.Busy, Code: "alert_store_busy" } =>
            new(AlertEvaluationApplicationStatus.InitializationBusy, "alert_store_busy"),
        { Status: AlertStoreStatus.Unavailable, Code: "alert_store_unavailable" } => InitializationUnavailable(),
        _ => InitializationUnavailable(),
    };

    private static AlertEngineConfiguration Freeze(AlertEngineConfiguration configuration)
    {
        if (configuration.Rules is null)
        {
            throw new AlertContractException("invalid_configuration", "Alert engine configuration is invalid.");
        }

        var rules = configuration.Rules.Select(rule =>
        {
            if (rule is null || rule.ThresholdOverrides is null)
            {
                throw new AlertContractException("invalid_configuration", "Alert engine configuration is invalid.");
            }

            var thresholds = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var threshold in rule.ThresholdOverrides)
            {
                thresholds.Add(threshold.Key, threshold.Value);
            }

            return rule with
            {
                ThresholdOverrides = new ReadOnlyDictionary<string, decimal>(thresholds),
                SourceSurfaceAllowlist = rule.SourceSurfaceAllowlist is null
                    ? null
                    : Array.AsReadOnly(rule.SourceSurfaceAllowlist.ToArray()),
            };
        }).ToArray();

        return configuration with { Rules = Array.AsReadOnly(rules) };
    }

    private static AlertEvaluationApplicationResult Rejected(string code) =>
        new(AlertEvaluationApplicationStatus.ContractRejected, code);

    private static AlertEvaluationApplicationResult InitializationUnavailable() =>
        new(AlertEvaluationApplicationStatus.InitializationUnavailable, "alert_store_unavailable");

    private static AlertEvaluationApplicationResult AppendUnavailable() =>
        new(AlertEvaluationApplicationStatus.AppendUnavailable, "alert_store_unavailable");

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

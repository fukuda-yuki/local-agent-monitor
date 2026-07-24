namespace CopilotAgentObservability.Alerts;

public static class AlertContractVersionsV2
{
    public const string Snapshot = "alert.snapshot.v2";
    public const string Configuration = "alert.config.v2";
    public const string Receipt = "alert.receipt.v2";
    public const string Evaluation = "alert.evaluation.v2";
    public const string Suppression = "alert.suppression.v2";
    public const string SanitizedReceiptProfile = "sanitized-alert-receipt.v2";
    public const string CanonicalJson = "alert.canonical-json.v2";
}

public enum AlertCostAcquisitionStateV2 { Complete, Incomplete }
public enum AlertCostAggregateStateV2 { Available, Unrepresentable, NotApplicable }
public enum AlertCostScopeKindV2 { Session, UtcDay, RollingPeriod }
public enum AlertCostMemberStateV2 { Estimated, Partial, NotEstimable, Missing, Failed, Unavailable, Stale }
public enum AlertCostAttemptResultKindV2 { Estimate, Unavailable, Failed }
public enum AlertEvidenceKindV2 { Session, PricingEstimate }
public enum AlertCostCompletenessV2 { Full, Partial }
public enum AlertEvidenceResolutionStatusV2 { Resolved, Unresolved, StoreFailure, ContractRejected }
public enum AlertEvaluationEngineStatusV2 { Success, UnresolvedEvidence, StoreFailure, ContractRejected }

public sealed record AlertCostScopeV2(
    string ScopeId,
    AlertCostScopeKindV2 Kind,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    IReadOnlyList<string> SessionIds);

public sealed record AlertCostMemberV2(
    string SessionId,
    DateTimeOffset SessionEffectiveAtUtc,
    DateTimeOffset SessionUpdatedAtUtc,
    string SourceSurface,
    string SourceApplicationVersion,
    AlertCostMemberStateV2 State,
    long AttemptRevision,
    AlertCostAttemptResultKindV2? AttemptResultKind,
    string? AttemptResultCode,
    long? HeadRevision,
    string? EstimateId,
    DateTimeOffset? EstimateCalculationTimeUtc,
    string? CatalogSha256,
    string? RegistryVersion,
    string? Provider,
    string? Model,
    string? BillingMode,
    decimal? Amount,
    string? Currency);

public sealed record AlertEvidenceReferenceV2(
    AlertEvidenceKindV2 Kind,
    string EvidenceId,
    string SessionId,
    DateTimeOffset ObservedAtUtc);

public sealed record AlertNormalizedSnapshotV2(
    string SchemaVersion,
    string ContextKind,
    string SourceSurface,
    string SourceVersion,
    AlertCostAcquisitionStateV2 AcquisitionState,
    IReadOnlyList<string> AcquisitionReasons,
    AlertCostAggregateStateV2 AggregateState,
    string EligibilityDigest,
    long? EligibleCount,
    long? EligibleLowerBound,
    AlertCostScopeV2 Scope,
    string? Currency,
    decimal? Amount,
    long? EstimatedCount,
    long? PartialCount,
    long? NotEstimableCount,
    long? MissingCount,
    long? FailedCount,
    long? UnavailableCount,
    long? StaleCount,
    long? CoverageNumerator,
    long? CoverageDenominator,
    int? CoverageBasisPoints,
    IReadOnlyList<AlertCostMemberV2> Members,
    IReadOnlyList<AlertEvidenceReferenceV2> Evidence,
    AlertCostCompletenessV2 Completeness,
    IReadOnlyList<string> CompletenessReasons,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt);

public sealed record AlertRuleIdentityV2(string RuleId, string RuleVersion);

public sealed record AlertBudgetRuleConfigurationV2(
    string RuleId,
    string RuleVersion,
    bool Enabled,
    string Currency,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    int MinimumCoverageBasisPoints,
    AlertCostScopeKindV2 ScopeKind,
    int? WindowDays);

public sealed record AlertEngineConfigurationV2(
    string SchemaVersion,
    string ConfigurationVersion,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    IReadOnlyList<AlertBudgetRuleConfigurationV2> Rules);

public sealed record AlertRuleDescriptorV2(
    string RuleId,
    string RuleVersion,
    string Title,
    string Description,
    string Formula,
    AlertCostScopeKindV2 ScopeKind,
    string EvaluationWindow);

public sealed record AlertRuleContextV2(
    AlertNormalizedSnapshotV2 Snapshot,
    AlertBudgetRuleConfigurationV2? Configuration,
    AlertRuleDescriptorV2 Descriptor);

public sealed record AlertRuleOutcomeV2(AlertSeverity? Severity, string? SuppressionCode);

public interface IAlertRuleV2
{
    AlertRuleDescriptorV2 Descriptor { get; }

    AlertRuleOutcomeV2 Evaluate(AlertRuleContextV2 context);
}

public interface IAlertEvidenceReadViewV2;

public sealed class AlertEvidenceReadViewV2 : IAlertEvidenceReadViewV2
{
    private AlertEvidenceReadViewV2() { }

    public static AlertEvidenceReadViewV2 Instance { get; } = new();
}

public sealed record StrictPendingPricingEvidenceV2(
    string EstimateId,
    string SessionId,
    DateTimeOffset CalculationTimeUtc,
    string CatalogSha256,
    string CanonicalEstimateSha256,
    string RunId,
    int TargetOrdinal);

public sealed class AlertEvidenceResolutionScopeV2
{
    public AlertEvidenceResolutionScopeV2(
        IAlertEvidenceReadViewV2 existingEvidenceReadView,
        IEnumerable<StrictPendingPricingEvidenceV2> pendingPricingEvidence)
    {
        ExistingEvidenceReadView = existingEvidenceReadView
            ?? throw new ArgumentNullException(nameof(existingEvidenceReadView));
        ArgumentNullException.ThrowIfNull(pendingPricingEvidence);
        PendingPricingEvidence = Array.AsReadOnly(
            pendingPricingEvidence.Select(item => item with { }).ToArray());
    }

    public IAlertEvidenceReadViewV2 ExistingEvidenceReadView { get; }
    public IReadOnlyList<StrictPendingPricingEvidenceV2> PendingPricingEvidence { get; }
}

public interface IAlertEvidenceResolverV2
{
    AlertEvidenceResolutionStatusV2 Resolve(
        AlertEvidenceReferenceV2 reference,
        AlertEvidenceResolutionScopeV2 scope);
}

public sealed record AlertSuppressionV2(
    string SchemaVersion,
    string EvaluationId,
    string RuleId,
    string RuleVersion,
    string Code,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    string ConfigurationVersion,
    string ConfigurationHash,
    AlertCostScopeKindV2 ScopeKind,
    string ScopeId,
    DateTimeOffset? ScopeStartUtc,
    DateTimeOffset? ScopeEndUtc,
    string EligibilityDigest,
    string? Currency,
    AlertCostAggregateStateV2 AggregateState,
    long? EligibleCount,
    long? EstimatedCount,
    long? PartialCount,
    long? NotEstimableCount,
    long? MissingCount,
    long? FailedCount,
    long? UnavailableCount,
    long? StaleCount,
    int? CoverageBasisPoints,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt);

public sealed record AlertReceiptV2(
    string SchemaVersion,
    string SanitizedExportProfile,
    string AlertId,
    string EvaluationId,
    string RuleId,
    string RuleVersion,
    AlertSeverity Severity,
    AlertInitialState InitialState,
    string SourceSurface,
    string SourceVersion,
    AlertCostScopeV2 Scope,
    IReadOnlyList<AlertEvidenceReferenceV2> Evidence,
    string Currency,
    AlertCostAggregateStateV2 AggregateState,
    decimal ObservedAmount,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    long EligibleCount,
    long EstimatedCount,
    long PartialCount,
    long NotEstimableCount,
    long MissingCount,
    long FailedCount,
    long UnavailableCount,
    long StaleCount,
    long CoverageNumerator,
    long CoverageDenominator,
    int CoverageBasisPoints,
    IReadOnlyList<AlertCostMemberV2> Members,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    string ConfigurationVersion,
    string ConfigurationHash,
    AlertCostCompletenessV2 Completeness,
    IReadOnlyList<string> CompletenessReasons,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string InputHash,
    string Summary);

public sealed record AlertEvaluationResultV2(
    string SchemaVersion,
    string EvaluationId,
    string InputHash,
    string ConfigurationVersion,
    string ConfigurationHash,
    string SelectedRuleId,
    string SelectedRuleVersion,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    AlertCostScopeKindV2 ScopeKind,
    string ScopeId,
    DateTimeOffset? ScopeStartUtc,
    DateTimeOffset? ScopeEndUtc,
    string EligibilityDigest,
    string? Currency,
    AlertCostAggregateStateV2 AggregateState,
    long? EligibleCount,
    long? EstimatedCount,
    long? PartialCount,
    long? NotEstimableCount,
    long? MissingCount,
    long? FailedCount,
    long? UnavailableCount,
    long? StaleCount,
    int? CoverageBasisPoints,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,
    IReadOnlyList<AlertReceiptV2> Receipts,
    IReadOnlyList<AlertSuppressionV2> Suppressions,
    IReadOnlyList<AlertRejectedMatch> RejectedMatches);

public sealed record AlertEvaluationEngineResultV2(
    AlertEvaluationEngineStatusV2 Status,
    AlertEvaluationResultV2? Evaluation = null,
    string? Code = null);

public static class AlertCostScopeIdentityV2
{
    public static string Create(
        AlertCostScopeKindV2 kind,
        DateTimeOffset? windowStartUtc,
        DateTimeOffset? windowEndUtc,
        string eligibilityDigest,
        IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var values = new List<byte[]>
        {
            System.Text.Encoding.UTF8.GetBytes("alert-cost-scope/v2"),
            System.Text.Encoding.UTF8.GetBytes(AlertWireV2.ScopeKind(kind)),
            System.Text.Encoding.UTF8.GetBytes(AlertWireV2.NullableTimestamp(windowStartUtc)),
            System.Text.Encoding.UTF8.GetBytes(AlertWireV2.NullableTimestamp(windowEndUtc)),
            System.Text.Encoding.UTF8.GetBytes(eligibilityDigest),
        };
        values.AddRange(sessionIds.Select(System.Text.Encoding.UTF8.GetBytes));
        return "cost-scope-" + AlertHashing.Sha256(AlertHashing.Frame([.. values]));
    }
}

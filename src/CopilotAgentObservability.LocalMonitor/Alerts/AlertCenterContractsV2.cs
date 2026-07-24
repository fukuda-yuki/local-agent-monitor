using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal sealed record AlertCenterQueryDtoV2(
    string? AlertId,
    string? SessionId,
    string? TraceId,
    string? Severity,
    string? State,
    string? RuleId,
    string? SourceSurface,
    string? Repository,
    string? Workspace,
    string? Completeness,
    string From,
    string To,
    string ReceiptKind,
    string ScopeKind,
    string Currency,
    string CoverageState,
    int Limit);

internal sealed record AlertCenterSnapshotV2(
    string SchemaVersion,
    string SnapshotId,
    string AcquisitionState,
    string? AcquisitionCapReason,
    int AcquiredReceiptCount,
    string MatchCountState,
    int MatchedItemCount,
    AlertCenterQueryDtoV2 Query,
    IReadOnlyList<AlertCenterItemV2> Items,
    int VisibleStartOrdinal,
    int VisibleEndOrdinal,
    bool HasPrevious,
    string? PreviousCursor,
    string? NextCursor,
    string RecurringState,
    IReadOnlyList<AlertCenterRecurringGroup> RecurringGroups,
    string CoverageState,
    IReadOnlyList<AlertCenterCoverageItemV2> Coverage,
    long? OmittedCoverageFactCount);

internal sealed record AlertCenterItemV2(
    string ReceiptKind,
    AlertCenterAlert? ReceiptV1,
    AlertCenterCostReceiptV2? CostReceiptV2);

internal sealed record AlertCenterCostReceiptV2(
    string AlertId,
    string EvaluationId,
    string RuleId,
    string RuleVersion,
    string Severity,
    string InitialState,
    AlertCenterLifecycle Lifecycle,
    string FirstObservedAt,
    string LastObservedAt,
    string Summary,
    AlertCenterCostRuleV2 Rule,
    string? Formula,
    string SourceSurface,
    string SourceVersion,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    string ConfigurationVersion,
    string ConfigurationHash,
    string InputHash,
    AlertCenterCostScopeV2 Scope,
    string EligibilityDigest,
    IReadOnlyList<AlertCenterCostEvidenceV2> Evidence,
    string Currency,
    string AggregateState,
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
    IReadOnlyList<AlertCenterCostMemberV2> Members);

internal sealed record AlertCenterCostScopeV2(
    string ScopeId,
    string Kind,
    string? WindowStartUtc,
    string? WindowEndUtc,
    IReadOnlyList<string> SessionIds);

internal sealed record AlertCenterCostRuleV2(
    string RuleId,
    string RuleVersion,
    string ContractState,
    string? Title,
    string? Description,
    string? EvaluationWindow,
    string? ScopeKind);

internal sealed record AlertCenterCostEvidenceV2(
    string Kind,
    string EvidenceId,
    string SessionId,
    string ObservedAtUtc,
    string State,
    string? Href);

internal sealed record AlertCenterCostMemberV2(
    string SessionId,
    string SessionEffectiveAtUtc,
    string State,
    long AttemptRevision,
    string? AttemptResultKind,
    string? AttemptResultCode,
    long? HeadRevision,
    string? EstimateId,
    string? CatalogSha256,
    string? RegistryVersion,
    string? BillingMode,
    string SessionEvidenceState,
    string? Repository,
    string? Workspace,
    string ScopeState,
    string SessionHref,
    string? EstimateEvidenceState,
    string? EstimateHref);

internal sealed record AlertCenterCoverageItemV2(
    string CoverageKind,
    AlertCenterCoverageFactV2? SuppressionV1,
    AlertCenterCostSuppressionV2? CostSuppressionV2);

internal sealed record AlertCenterCoverageFactV2(
    string EvaluationId,
    long SuppressionOrdinal,
    string RuleId,
    string RuleVersion,
    string Code,
    IReadOnlyList<string> MissingCapabilities,
    string ContextState,
    string? SourceSurface,
    string? SourceVersion,
    string? SessionId,
    string? TraceId,
    string? ObservationDate);

internal sealed record AlertCenterCostSuppressionV2(
    string EvaluationId,
    long SuppressionOrdinal,
    string RuleId,
    string RuleVersion,
    string Code,
    string SourceCostConfigurationId,
    long SourceConfigurationHeadRevision,
    string SourceConfigurationCatalogSha256,
    string ConfigurationVersion,
    string ConfigurationHash,
    string ScopeKind,
    string ScopeId,
    string? ScopeStartUtc,
    string? ScopeEndUtc,
    string EligibilityDigest,
    string? Currency,
    string AggregateState,
    long? EligibleCount,
    long? EstimatedCount,
    long? PartialCount,
    long? NotEstimableCount,
    long? MissingCount,
    long? FailedCount,
    long? UnavailableCount,
    long? StaleCount,
    int? CoverageBasisPoints,
    string? FirstObservedAt,
    string? LastObservedAt);

internal sealed record CostAlertPresentationMemberV1(
    string SessionId,
    DateTimeOffset SessionEffectiveAtUtc,
    string SessionEvidenceState,
    string? Repository,
    string? Workspace,
    string ScopeState,
    string SessionHref,
    string? EstimateId,
    string? EstimateEvidenceState,
    string? EstimateHref);

internal sealed record CostAlertPresentationResolutionV1(
    string State,
    IReadOnlyList<CostAlertPresentationMemberV1> Members);

internal interface ICostAlertPresentationResolverV1
{
    CostAlertPresentationResolutionV1 Resolve(
        IReadOnlyList<AlertCostMemberV2> members,
        IReadOnlyList<AlertEvidenceReferenceV2> evidence);
}

internal sealed class UnavailableCostAlertPresentationResolverV1
    : ICostAlertPresentationResolverV1
{
    public CostAlertPresentationResolutionV1 Resolve(
        IReadOnlyList<AlertCostMemberV2> members,
        IReadOnlyList<AlertEvidenceReferenceV2> evidence) =>
        new("unavailable", []);
}

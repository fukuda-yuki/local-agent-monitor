namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal static class AlertCenterContractVersions
{
    public const string Center = "alert.center.v1";
    public const string EvaluationRequest = "alert.center.evaluation-request.v1";
    public const string EvaluationResult = "alert.center.evaluation-result.v1";
}

internal enum AlertCenterReadStatus
{
    Success,
    Busy,
    Unavailable,
}

internal sealed record AlertCenterReadResult(
    AlertCenterReadStatus Status,
    AlertCenterSnapshot? Snapshot = null);

internal sealed record AlertCenterQuery(
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
    DateOnly From,
    DateOnly To,
    int Offset,
    int Limit);

internal sealed record AlertCenterQueryDto(
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
    int Offset,
    int Limit);

internal sealed record AlertCenterSnapshot(
    string SchemaVersion,
    string GeneratedAt,
    AlertCenterQueryDto Query,
    string SnapshotState,
    long? OmittedReceiptCount,
    long TotalCount,
    IReadOnlyList<AlertCenterAlert> Alerts,
    IReadOnlyList<AlertCenterRecurringGroup> RecurringGroups,
    IReadOnlyList<AlertCenterCoverageFact> Coverage);

internal sealed record AlertCenterAlert(
    string AlertId,
    string Severity,
    string InitialState,
    AlertCenterLifecycle Lifecycle,
    AlertCenterRule Rule,
    IReadOnlyList<AlertCenterValue> ObservedValues,
    IReadOnlyList<AlertCenterValue> EffectiveThresholds,
    AlertCenterSource Source,
    string SessionId,
    string? TraceId,
    AlertCenterScope Scope,
    AlertCenterCompleteness Completeness,
    string FirstObservedAt,
    string LastObservedAt,
    string Summary,
    IReadOnlyList<AlertCenterEvidence> Evidence,
    int EvidenceCount,
    AlertCenterRelationships Relationships,
    string CoverageNote,
    string EvaluationId);

internal sealed record AlertCenterLifecycle(
    string State,
    long Revision,
    string? LastOccurredAt,
    IReadOnlyList<string> AllowedActions);

internal sealed record AlertCenterRule(
    string RuleId,
    string RuleVersion,
    string ContractState,
    string? Title,
    string? Description,
    string? Formula,
    string? EvaluationWindow,
    string? Scope,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<AlertCenterThresholdDefinition> Thresholds);

internal sealed record AlertCenterThresholdDefinition(
    string Name,
    string Unit,
    string Direction,
    decimal Minimum,
    decimal Maximum,
    decimal WarningDefault,
    decimal CriticalDefault);

internal sealed record AlertCenterValue(string Name, string Unit, decimal Value);

internal sealed record AlertCenterSource(
    string Surface,
    string Version,
    string CapabilityState);

internal sealed record AlertCenterScope(
    string State,
    string? Repository,
    string? Workspace,
    string? TraceRepository,
    string? TraceWorkspace,
    string? SessionRepository,
    string? SessionWorkspace);

internal sealed record AlertCenterCompleteness(
    string State,
    IReadOnlyList<string> ReasonCodes);

internal sealed record AlertCenterEvidence(
    string Kind,
    string EvidenceId,
    string SessionId,
    string? TraceId,
    string? SpanId,
    string? TurnId,
    string? EventId,
    string? ToolCallId,
    string ObservedAt,
    string AvailabilityState,
    string? ContentState,
    string? Href);

internal sealed record AlertCenterRelationships(
    IReadOnlyList<string> PredecessorAlertIds,
    IReadOnlyList<string> SuccessorAlertIds);

internal sealed record AlertCenterRecurringGroup(
    string AggregationState,
    string RuleId,
    string RuleVersion,
    string? Repository,
    string? Workspace,
    string SourceSurface,
    string SourceVersion,
    string ObservationDate,
    string From,
    string To,
    int OccurrenceCount,
    int DistinctSessionCount,
    string FirstObservedAt,
    string LastObservedAt,
    IReadOnlyDictionary<string, int> CompletenessDistribution,
    IReadOnlyList<string> AlertIds,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<AlertCenterEvidenceReference> EvidenceReferences);

internal sealed record AlertCenterEvidenceReference(
    string Kind,
    string EvidenceId,
    string SessionId,
    string? TraceId,
    string? SpanId,
    string? TurnId,
    string? EventId,
    string? ToolCallId,
    string ObservedAt);

internal sealed record AlertCenterCoverageFact(
    string EvaluationId,
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

internal interface IAlertCenterReadModel
{
    AlertCenterReadResult Read(AlertCenterQuery query);
}

internal sealed class UnavailableAlertCenterReadModel : IAlertCenterReadModel
{
    public AlertCenterReadResult Read(AlertCenterQuery query) =>
        new(AlertCenterReadStatus.Unavailable);
}

internal enum AlertCenterEvaluationStatus
{
    Success,
    SessionNotFound,
    TraceNotFound,
    TraceNotOwned,
    SourcePartitionMissing,
    SourcePartitionAmbiguous,
    TraceIncomplete,
    StoreBusy,
    StoreUnavailable,
    StoreConflict,
    ContractRejected,
}

internal sealed record AlertCenterEvaluationResult(
    AlertCenterEvaluationStatus Status,
    AlertCenterEvaluationResponse? Response = null);

internal sealed record AlertCenterEvaluationResponse(
    string SchemaVersion,
    string EvaluationId,
    IReadOnlyList<string> ReceiptIds,
    IReadOnlyList<AlertCenterEvaluationSuppression> Suppressions,
    IReadOnlyList<AlertCenterEvaluationRejectedMatch> RejectedMatches);

internal sealed record AlertCenterEvaluationSuppression(
    string RuleId,
    string RuleVersion,
    string Code,
    IReadOnlyList<string> MissingCapabilities);

internal sealed record AlertCenterEvaluationRejectedMatch(
    string RuleId,
    string RuleVersion,
    string Code);

internal interface IAlertCenterEvaluationCoordinator
{
    AlertCenterEvaluationResult Evaluate(Guid sessionId, string traceId);
}

internal sealed class UnavailableAlertCenterEvaluationCoordinator : IAlertCenterEvaluationCoordinator
{
    public AlertCenterEvaluationResult Evaluate(Guid sessionId, string traceId) =>
        new(AlertCenterEvaluationStatus.StoreUnavailable);
}

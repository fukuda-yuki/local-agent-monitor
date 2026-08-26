namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalWorkspaceSessionDetailContribution(
    IReadOnlyList<LocalWorkspaceExecutionDetail> Executions,
    IReadOnlyList<LocalWorkspaceNodeDetail> Nodes,
    IReadOnlyList<LocalWorkspaceNodeEdgeDetail> Edges,
    IReadOnlyList<LocalWorkspaceContentAvailability> Content);

internal sealed record LocalWorkspaceExecutionDetail(
    string ExecutionId, string SessionId, string SourceKind, string SourceIdentity, long SourceOrdinal,
    string Lifecycle, string Status, string? Model, string? TraceId,
    string TimeAuthority, long? StartUtcTicks, long? EndUtcTicks, long? DurationMilliseconds,
    LocalWorkspaceActivityFacts Activity, LocalWorkspaceTokenFacts Tokens);

internal sealed record LocalWorkspaceNodeDetail(
    string NodeId, string SessionId, string ExecutionId, string SourceKind, string SourceIdentity, long SourceOrdinal,
    string? ParentNodeId, string RelationshipAuthority, string Kind, string NameState, string? NameText,
    string Lifecycle, string Status, string TimeAuthority, long? StartUtcTicks, long? EndUtcTicks, long? DurationMilliseconds,
    LocalWorkspaceActivityFacts Activity, LocalWorkspaceTokenFacts Tokens,
    string? TraceId, string? SpanId, string? EventId);

internal sealed record LocalWorkspaceNodeEdgeDetail(
    string NodeId, string RelatedNodeId, string RelationKind, string RelationshipAuthority, long SourceOrdinal);

internal sealed record LocalWorkspaceContentAvailability(string NodeId, string Part, string State);

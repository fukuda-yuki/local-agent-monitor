namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalWorkspaceSessionDetailContribution(
    IReadOnlyList<LocalWorkspaceExecutionDetail> Executions,
    IReadOnlyList<LocalWorkspaceNodeDetail> Nodes,
    IReadOnlyList<LocalWorkspaceNodeEdgeDetail> Edges,
    IReadOnlyList<LocalWorkspaceContentAvailability> Content,
    IReadOnlyList<string>? NativeSessionIds = null,
    IReadOnlyList<string>? Versions = null,
    string? InstructionSourceIdentity = null,
    long? InstructionAdditionalCount = null,
    string? CanonicalRevisionInput = null,
    string? SkillRegistryGenerationIdentity = null);

internal sealed record LocalWorkspaceExecutionDetail(
    string ExecutionId, string SessionId, string SourceKind, string SourceIdentity, long SourceOrdinal,
    string Lifecycle, string Status, string? Model, string? TraceId,
    string TimeAuthority, long? StartUtcTicks, long? EndUtcTicks, long? DurationMilliseconds,
    LocalWorkspaceActivityFacts Activity, LocalWorkspaceTokenFacts Tokens,
    string? SourceSurface = null, string? Version = null, long ChildCount = 0);

internal sealed record LocalWorkspaceNodeDetail(
    string NodeId, string SessionId, string ExecutionId, string SourceKind, string SourceIdentity, long SourceOrdinal,
    string? ParentNodeId, string RelationshipAuthority, string Kind, string NameState, string? NameText,
    string Lifecycle, string Status, string TimeAuthority, long? StartUtcTicks, long? EndUtcTicks, long? DurationMilliseconds,
    LocalWorkspaceActivityFacts Activity, LocalWorkspaceTokenFacts Tokens,
    string? TraceId, string? SpanId, string? EventId, long ChildCount = 0);

internal sealed record LocalWorkspaceNodeEdgeDetail(
    string NodeId, string RelatedNodeId, string RelationKind, string RelationshipAuthority, long SourceOrdinal);

internal sealed record LocalWorkspaceContentAvailability(
    string NodeId, string Part, string State, string? SourceItemId = null, string? RevisionInput = null,
    string? StoreKind = null, string? LocatorKind = null, string? JsonPointer = null, long? SelectedUtf8Bytes = null,
    string? RetentionItemId = null, string? RetentionStoreInstanceId = null, string? SourceCapturedAt = null,
    string? SourceExpiresAt = null, long? RetentionRevision = null, byte[]? RetentionOwnershipReceipt = null,
    byte[]? RetentionOwnerToken = null);

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
    string? SourceSurface = null, string? Version = null, long ChildCount = 0, bool Latest = false);

internal sealed record LocalWorkspaceNodeDetail(
    string NodeId, string SessionId, string ExecutionId, string SourceKind, string SourceIdentity, long SourceOrdinal,
    string? ParentNodeId, string RelationshipAuthority, string Kind, string NameState, string? NameText,
    string Lifecycle, string Status, string TimeAuthority, long? StartUtcTicks, long? EndUtcTicks, long? DurationMilliseconds,
    LocalWorkspaceActivityFacts Activity, LocalWorkspaceTokenFacts Tokens,
    string? TraceId, string? SpanId, string? EventId, long ChildCount = 0,
    bool HasMoreChildren = false, LocalWorkspaceFact<long>? CollapsedChildren = null,
    IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? SourceReferences = null,
    LocalWorkspaceToolMetadataDetail? ToolMetadata = null,
    LocalWorkspaceSkillMetadataDetail? SkillMetadata = null,
    LocalWorkspaceSubagentLifecycleDetail? SubagentLifecycle = null,
    LocalWorkspacePermissionMetadataDetail? PermissionMetadata = null);

internal sealed record LocalWorkspaceNodeSourceReferenceDetail(
    string SourceKind, string? SourceIdentity, string? TraceId, string? SpanId, string? EventId,
    string? RevisionInput = null, bool AuthorityValidated = false);

internal sealed record LocalWorkspaceToolMetadataDetail(
    string CallerState, string? CallerNodeId, string StartedState, string CompletedState, string FailedState,
    string ExitState, long? ExitCode, string McpServerIdentityState, string? McpServerIdentity,
    string McpServerNameState, string? McpServerName, string McpToolNameState, string? McpToolName,
    string RetryState, string RecoveryState, string ChildActivityState, long? ChildActivityCount,
    IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? SourceReferences = null);

internal sealed record LocalWorkspaceSkillMetadataDetail(
    string CurrentValidState, string SourceState, string? Source, string TriggerState, string? Trigger,
    string InventoryReferenceState, string? InventoryReference,
    string HistoricalSnapshotReferenceState, string? HistoricalSnapshotReference);

internal sealed record LocalWorkspaceSubagentLifecycleDetail(
    string SelectedState, string StartedState, string CompletedState, string FailedState,
    string DeselectedState, string InputState,
    IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? SourceReferences = null);

internal sealed record LocalWorkspacePermissionMetadataDetail(
    string DecisionState, string? Decision, string WaitState, long? WaitMilliseconds);

internal sealed record LocalWorkspaceNodeEdgeDetail(
    string NodeId, string RelatedNodeId, string RelationKind, string RelationshipAuthority, long SourceOrdinal);

internal sealed record LocalWorkspaceContentAvailability(
    string NodeId, string Part, string State, string? SourceItemId = null, string? RevisionInput = null,
    string? StoreKind = null, string? LocatorKind = null, string? JsonPointer = null, long? SelectedUtf8Bytes = null,
    string? RetentionItemId = null, string? RetentionStoreInstanceId = null, string? SourceCapturedAt = null,
    string? SourceExpiresAt = null, long? RetentionRevision = null, byte[]? RetentionOwnershipReceipt = null,
    byte[]? RetentionOwnerToken = null);

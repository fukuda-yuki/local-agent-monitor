namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public enum RetentionMutationPreviewResult { Actionable, EmptyNotApplicable }
public enum RetentionMutationEmptyReason { NoExactOwnedItems, AllCandidatesExcluded }
public enum RetentionMutationSourceState { Available, NotCaptured, Redacted, Unsupported, Unknown }
public enum RetentionMutationSessionCompleteness { Unbound, Partial, Rich, Full }
public enum RetentionMutationResultStatus { Committed, Replayed }

public sealed record RetentionMutationLifecycleCounts(
    int Expiring,
    int RetainedByPolicy,
    int ExpiredPendingDeletion,
    int DeletionQueued,
    int Deleting,
    int Deleted,
    int DeletionFailed)
{
    public static RetentionMutationLifecycleCounts From(IEnumerable<RetentionItemLifecycle> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var counts = states.GroupBy(static state => state).ToDictionary(static group => group.Key, static group => group.Count());
        return new(
            Count(RetentionItemLifecycle.Expiring),
            Count(RetentionItemLifecycle.RetainedByPolicy),
            Count(RetentionItemLifecycle.ExpiredPendingDeletion),
            Count(RetentionItemLifecycle.DeletionQueued),
            Count(RetentionItemLifecycle.Deleting),
            Count(RetentionItemLifecycle.Deleted),
            Count(RetentionItemLifecycle.DeletionFailed));

        int Count(RetentionItemLifecycle state) => counts.GetValueOrDefault(state);
    }
}

public sealed record RetentionPreviewItem(
    string ItemId,
    RetentionStoreKind StoreKind,
    RetentionItemLifecycle State,
    RetentionPinState PinState,
    RetentionDeleteState DeleteState,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? ExpiresAt,
    string PolicyId,
    int PolicyVersion,
    DateTimeOffset? ReadDeniedAt,
    DateTimeOffset? QueuedAt,
    long Revision,
    bool RetryExhausted,
    RetentionErrorCode? ErrorCode);

public sealed record RetentionCurrentStateSummary(
    int ReadableItemCount,
    int ReadDeniedItemCount,
    int PinnedItemCount,
    int UnpinnedItemCount,
    RetentionMutationLifecycleCounts LifecycleCounts);

public sealed record RetentionStoreKindSummary(
    RetentionStoreKind StoreKind,
    int ItemCount,
    int ReadableCount,
    int ReadDeniedCount);

public sealed record RetentionExclusionSummary(string ReasonCode, int ItemCount);

public sealed record RetentionCaptureExpiryPolicySummary(
    string PolicyId,
    int PolicyVersion,
    int ItemCount,
    DateTimeOffset? CapturedAtMin,
    DateTimeOffset? CapturedAtMax,
    DateTimeOffset? OriginalExpiresAtMin,
    DateTimeOffset? OriginalExpiresAtMax);

public sealed record RetentionRetainedImpact(
    bool RawContentWillBeDeleted,
    bool SessionMetadataRetained,
    int EventMetadataRetainedCount,
    int SafeSummaryRetainedCount,
    int EvidenceReferenceRetainedCount);

public sealed record RetentionActiveConflictSummary(string ConflictCode, int ItemCount, string ConflictVersion);

public sealed record RetentionMutationPreviewResponse(
    int SchemaVersion,
    RetentionMutationPreviewResult Result,
    RetentionMutationEmptyReason? EmptyReason,
    bool MutationAllowed,
    string PreviewId,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    RetentionMutationOperation Operation,
    RetentionMutationScope Scope,
    RetentionMutationSourceState? SourceState,
    RetentionMutationSessionCompleteness? SessionCompleteness,
    string? ContentState,
    RetentionCurrentStateSummary CurrentState,
    IReadOnlyList<RetentionPreviewItem> TargetItems,
    int TargetItemCount,
    IReadOnlyList<RetentionStoreKindSummary> StoreKindSummary,
    int ExcludedItemCount,
    IReadOnlyList<RetentionExclusionSummary> ExcludedItemsByReason,
    IReadOnlyList<RetentionCaptureExpiryPolicySummary> CaptureExpiryPolicySummary,
    RetentionRetainedImpact RetainedMetadataImpact,
    IReadOnlyList<RetentionActiveConflictSummary> ActiveCleanupExclusionConflicts,
    string? BackupNonPurgeWarningCode,
    string ExpectedStateVersion,
    string TargetItemSetDigest,
    string PreviewDigest,
    DateTimeOffset? ConfirmationExpiresAt,
    string? RejectionCode);

public sealed record RetentionConfirmationIssueRequest(string PreviewId, string PreviewDigest);
public sealed record RetentionConfirmationIssueResponse(int SchemaVersion, string ConfirmationId, string ConfirmationToken, DateTimeOffset ConfirmationExpiresAt);

public sealed record RetentionMutationResult(
    int SchemaVersion,
    string OperationId,
    string ResultCode,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    RetentionMutationOperation Operation,
    RetentionMutationScope Scope,
    int TargetItemCount,
    RetentionPinState PinState,
    RetentionMutationLifecycleCounts LifecycleCounts,
    bool ReadDenied,
    string? AuditEventId,
    string ExpectedVersion,
    string ResultVersion,
    string BackupNonPurgeWarningCode,
    bool IdempotentReplay,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt);

public sealed record RetentionMutationStatusResponse(
    int SchemaVersion,
    string OperationId,
    RetentionMutationOperation Operation,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    RetentionMutationResultStatus Status,
    string ResultCode,
    RetentionMutationLifecycleCounts LifecycleCounts,
    bool ReadDenied,
    string? AuditEventId,
    bool IdempotentReplay,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    string BackupNonPurgeWarningCode);

public sealed record RetentionItemStateResponse(
    int SchemaVersion,
    string ItemId,
    RetentionStoreKind StoreKind,
    RetentionItemLifecycle State,
    RetentionPinState PinState,
    RetentionDeleteState DeleteState,
    string PolicyId,
    int PolicyVersion,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReadDeniedAt,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? DeletionStartedAt,
    DateTimeOffset? DeletedAt,
    int AttemptCount,
    bool RetryExhausted,
    RetentionErrorCode? ErrorCode,
    DateTimeOffset? RetryAt,
    long Revision,
    string? SessionId);

public sealed record RetentionAuditEvent(
    string EventId,
    string OperationId,
    string EventType,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    string? SessionId,
    DateTimeOffset OccurredAt,
    string ActorLabel,
    RetentionMutationOperation Operation,
    string ReasonCode,
    string? Comment,
    RetentionPinState PreviousPinState,
    RetentionPinState NewPinState,
    RetentionMutationLifecycleCounts PreviousOperationState,
    RetentionMutationLifecycleCounts NewOperationState,
    string RequestIdempotencyKey,
    string ExpectedVersion,
    string ResultVersion,
    string TargetItemSetDigest,
    string CompletionCode,
    string? ErrorCode);

public sealed record RetentionHistoryResponse(
    int SchemaVersion,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    IReadOnlyList<RetentionAuditEvent> Events,
    string? NextCursor);

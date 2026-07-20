namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionStoredMutationPreview(
    RetentionMutationPreviewResponse Response,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    byte[]? WorkflowKeyDigest,
    string? ActiveConflictSnapshot,
    string? ConflictVersion,
    string? ReasonCode,
    byte[]? CommentSha256);

internal sealed record RetentionMutationPreviewMaterializationResult(
    RetentionMutationPreviewProjectionOutcome Outcome,
    string? ErrorCode,
    RetentionMutationPreviewProjection? Projection,
    IReadOnlyList<RetentionMutationActiveConflictSnapshot> ConflictSnapshot);

internal sealed record RetentionMutationPreviewPersistenceResult(
    RetentionIdempotencyDisposition Disposition,
    string? ResultJson,
    RetentionStoredMutationPreview? Preview,
    string? ErrorCode);

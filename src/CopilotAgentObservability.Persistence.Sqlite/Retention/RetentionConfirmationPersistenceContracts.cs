namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionConfirmationBindingPersistenceDisposition
{
    Stored,
    GenerationFailed,
    Consumed
}

internal enum RetentionConfirmationIssuePersistenceDisposition
{
    IssuedFresh,
    ReissuedAfterInvalidation,
    ConsumedLinkage,
    Conflict,
    Expired,
    GenerationFailed
}

internal enum RetentionConfirmationBindingState
{
    Active,
    Consumed,
    Invalidated
}

internal enum RetentionConfirmationValidationDisposition
{
    Invalid,
    Consumed,
    Expired,
    Active
}

internal enum RetentionConfirmationConsumptionDisposition
{
    Invalid,
    AlreadyConsumed,
    Expired,
    Consumed
}

internal sealed record RetentionConfirmationBindingRequest(
    string ConfirmationId,
    string PreviewId,
    string ConfirmationToken,
    RetentionMutationTarget Target,
    RetentionMutationOperation Operation,
    RetentionMutationScope Scope,
    string PreviewDigest,
    string ExpectedStateVersion,
    string TargetItemSetDigest,
    string ActiveConflictSnapshot,
    string ConflictVersion,
    string WorkflowIdempotencyKey,
    string ReasonCode,
    string? Comment,
    string? OperationId);

internal sealed record RetentionConfirmationBinding(
    string ConfirmationId,
    string PreviewId,
    int SchemaVersion,
    byte[] TokenSha256,
    byte[] Nonce,
    RetentionMutationTargetKind TargetKind,
    string TargetId,
    RetentionMutationOperation Operation,
    RetentionMutationScope Scope,
    string PreviewDigest,
    string ExpectedStateVersion,
    string TargetItemSetDigest,
    string ActiveConflictSnapshot,
    string ConflictVersion,
    DateTimeOffset ConfirmationExpiresAt,
    string WorkflowIdempotencyKey,
    string ReasonCode,
    byte[]? CommentSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset? InvalidatedAt,
    string? OperationId,
    RetentionConfirmationBindingState State);

internal sealed record RetentionConfirmationValidationResult(
    RetentionConfirmationValidationDisposition Disposition,
    string? Code,
    RetentionConfirmationBinding? Binding);

internal sealed record RetentionConfirmationConsumptionResult(
    RetentionConfirmationConsumptionDisposition Disposition,
    string? Code,
    RetentionConfirmationBinding? Binding);

internal sealed record RetentionConfirmationPersistenceResult(
    RetentionConfirmationBindingPersistenceDisposition Disposition,
    RetentionConfirmationBinding? Binding);

internal sealed record RetentionConfirmationIssuePersistenceResult(
    RetentionConfirmationIssuePersistenceDisposition Disposition,
    RetentionConfirmationBinding? Binding,
    string? OperationId,
    string? ResultJson,
    string? CompletionCode,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt);

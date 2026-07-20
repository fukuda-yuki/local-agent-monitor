namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionIdempotencyDisposition
{
    Created,
    Replayed,
    Conflict,
    Expired
}

internal sealed record RetentionIdempotencyRequest(
    string WorkflowKey,
    RetentionMutationOperationStep Step,
    string CanonicalRequest,
    string ResultJson,
    string? CompletionCode);

internal sealed record RetentionIdempotencyOutcome(
    RetentionIdempotencyDisposition Disposition,
    string? ResultJson,
    string? CompletionCode,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt);

internal sealed record RetentionAuditReadTarget(
    RetentionMutationTargetKind TargetKind,
    string TargetId);

internal enum RetentionAuditHistoryReadDisposition
{
    Found,
    TargetNotFound,
    CursorInvalid
}

internal sealed record RetentionAuditHistoryReadResult(
    RetentionAuditHistoryReadDisposition Disposition,
    IReadOnlyList<RetentionAuditEvent> Events,
    string? NextCursor);

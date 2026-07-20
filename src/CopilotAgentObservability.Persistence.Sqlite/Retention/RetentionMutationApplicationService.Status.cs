namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionMutationStatusApplicationResult(
    RetentionMutationStatusResponse? Status,
    string? ErrorCode);

internal sealed record RetentionMutationItemStateApplicationResult(
    RetentionItemStateResponse? Item,
    string? ErrorCode);

internal sealed partial class RetentionMutationApplicationService
{
    internal RetentionMutationStatusApplicationResult ReadOperationStatus(string? operationId)
    {
        var result = catalog.ReadOperationReceipt(operationId);
        if (result is null)
            return new(null, RetentionMutationErrorCodes.OperationNotFound);

        return new(new RetentionMutationStatusResponse(
            result.SchemaVersion,
            result.OperationId,
            result.Operation,
            result.TargetKind,
            result.TargetId,
            result.IdempotentReplay ? RetentionMutationResultStatus.Replayed : RetentionMutationResultStatus.Committed,
            result.ResultCode,
            result.LifecycleCounts,
            result.ReadDenied,
            result.AuditEventId,
            result.IdempotentReplay,
            result.CreatedAt,
            result.CompletedAt,
            result.BackupNonPurgeWarningCode), null);
    }

    internal RetentionMutationItemStateApplicationResult ReadItemState(string? itemId)
    {
        var item = catalog.ReadMutationItemState(itemId);
        return item is null
            ? new(null, RetentionMutationErrorCodes.TargetNotFound)
            : new(item, null);
    }
}

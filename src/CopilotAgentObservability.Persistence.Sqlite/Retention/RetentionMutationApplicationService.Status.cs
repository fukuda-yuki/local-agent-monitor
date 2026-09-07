namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionMutationStatusApplicationResult(
    RetentionMutationStatusResponse? Status,
    string? ErrorCode);

internal sealed record RetentionMutationItemStateApplicationResult(
    RetentionItemStateResponse? Item,
    string? ErrorCode);

internal sealed record RetentionSessionManagementResponse(
    string SchemaVersion, string SessionId, string TargetScope, int TargetItemCount, int ExcludedItemCount,
    RetentionCurrentStateSummary CurrentState, int ExpiringItemCount,
    DateTimeOffset? EarliestExpiresAt, DateTimeOffset? LatestExpiresAt);

internal sealed record RetentionSessionManagementApplicationResult(RetentionSessionManagementResponse? Status, string? ErrorCode);

internal sealed partial class RetentionMutationApplicationService
{
    internal RetentionSessionManagementApplicationResult ReadSessionManagement(string sessionId)
    {
        var target = new RetentionMutationTarget(RetentionMutationTargetKind.Session, sessionId);
        if (!RetentionMutationTargetValidator.Validate(target).IsValid)
            return new(null, RetentionMutationErrorCodes.RequestInvalid);
        var resolution = catalog.ResolveMutationTarget(target);
        if (resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotFound)
            return new(null, RetentionMutationErrorCodes.TargetNotFound);
        var items = resolution.Items;
        var now = timeProvider.GetUtcNow();
        var readable = items.Count(item => RetentionMutationStateProjection.IsReadable(item.State, item.ExpiresAt, item.ReadDeniedAt, now));
        var pinned = items.Count(item => RetentionMutationStateProjection.PinState(item.State) == RetentionPinState.Pinned);
        var expiring = items.Where(item => item.State == RetentionItemLifecycle.Expiring).ToArray();
        return new(new("retention-session-management.v1", sessionId, "session_event_content", items.Count, resolution.ExcludedItemCount,
            new(readable, items.Count - readable, pinned, items.Count - pinned,
                RetentionMutationLifecycleCounts.From(items.Select(item => item.State))),
            expiring.Length, expiring.Select(item => item.ExpiresAt).Min(), expiring.Select(item => item.ExpiresAt).Max()), null);
    }

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

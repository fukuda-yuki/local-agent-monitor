namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionMutationHistoryApplicationResult(
    RetentionHistoryResponse? History,
    string? ErrorCode);

internal sealed partial class RetentionMutationApplicationService
{
    internal RetentionMutationHistoryApplicationResult ReadHistory(
        RetentionMutationTargetKind targetKind,
        string? targetId,
        int limit,
        string? cursor)
    {
        var target = new RetentionAuditReadTarget(targetKind, targetId ?? string.Empty);
        var result = catalog.ReadAuditHistoryPage(target, limit, cursor);
        return result.Disposition switch
        {
            RetentionAuditHistoryReadDisposition.Found => new(
                new RetentionHistoryResponse(
                    RetentionMutationConstants.SchemaVersion,
                    targetKind,
                    target.TargetId,
                    result.Events,
                    result.NextCursor),
                null),
            RetentionAuditHistoryReadDisposition.TargetNotFound => new(null, RetentionMutationErrorCodes.TargetNotFound),
            RetentionAuditHistoryReadDisposition.CursorInvalid => new(null, RetentionMutationErrorCodes.HistoryCursorInvalid),
            _ => throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable)
        };
    }
}

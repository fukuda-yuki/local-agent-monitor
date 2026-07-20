using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionMutationConfirmationApplicationResult(
    RetentionConfirmationIssueResponse? Confirmation,
    RetentionConfirmationIssuePersistenceDisposition? Disposition,
    string? ErrorCode,
    bool IsReplay = false);

internal sealed record RetentionConfirmationIssueStoredResult(
    string ConfirmationId,
    DateTimeOffset ConfirmationExpiresAt);

internal sealed partial class RetentionMutationApplicationService
{
    internal RetentionMutationConfirmationApplicationResult IssueConfirmation(
        RetentionConfirmationIssueRequest? request,
        string? workflowKey)
    {
        if (request is null
            || !RetentionMutationIdentifiers.TryParsePreviewId(request.PreviewId, out _)
            || !IsDigest(request.PreviewDigest, "sha256-"))
            return new(null, null, RetentionMutationErrorCodes.RequestInvalid);
        if (!RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey))
            return new(null, null, RetentionMutationErrorCodes.IdempotencyKeyInvalid);

        var stored = catalog.ReadMutationPreview(request.PreviewId);
        if (stored is null)
            return new(null, null, RetentionMutationErrorCodes.PreviewNotFound);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        if (!string.Equals(request.PreviewDigest, stored.Response.PreviewDigest, StringComparison.Ordinal))
            return new(null, null, RetentionMutationErrorCodes.PreviewDigestMismatch);
        if (stored.ExpiresAt is { } expiresAt && now >= expiresAt)
            return new(null, null, RetentionMutationErrorCodes.PreviewExpired);
        if (stored.Response.Result == RetentionMutationPreviewResult.EmptyNotApplicable || stored.Response.TargetItemCount == 0)
            return new(null, null, RetentionMutationErrorCodes.TargetEmpty);
        if (stored.Response.RejectionCode is not null)
            return new(null, null, stored.Response.RejectionCode);
        if (stored.ReasonCode is null || stored.ActiveConflictSnapshot is null || stored.ConflictVersion is null)
            return new(null, null, RetentionMutationErrorCodes.CatalogUnavailable);

        var current = catalog.CollectMutationPreviewMaterialization(
            new(stored.Response.TargetKind, stored.Response.TargetId),
            stored.Response.Operation,
            stored.Response.Scope,
            now);
        var drift = EvaluateIssuanceDrift(stored, current);
        if (drift is not null)
            return new(null, null, drift);

        var confirmationId = confirmationIdGenerator();
        var token = tokenGenerator();

        var issueRequest = new RetentionIdempotencyRequest(
            workflowKey!,
            RetentionMutationOperationStep.ConfirmationIssue,
            RetentionMutationApplicationCanonicalization.ConfirmationRequest(request),
            JsonSerializer.Serialize(new RetentionConfirmationIssueStoredResult(confirmationId, stored.Response.ConfirmationExpiresAt!.Value)),
            null);
        var bindingRequest = new RetentionConfirmationBindingRequest(
            confirmationId,
            stored.Response.PreviewId,
            token,
            new(stored.Response.TargetKind, stored.Response.TargetId),
            stored.Response.Operation,
            stored.Response.Scope,
            stored.Response.PreviewDigest,
            stored.Response.ExpectedStateVersion,
            stored.Response.TargetItemSetDigest,
            stored.ActiveConflictSnapshot,
            stored.ConflictVersion,
            workflowKey!,
            stored.ReasonCode,
            null,
            null,
            stored.CommentSha256);
        var persistence = catalog.IssueConfirmation(issueRequest, bindingRequest);
        return persistence.Disposition switch
        {
            RetentionConfirmationIssuePersistenceDisposition.IssuedFresh or
            RetentionConfirmationIssuePersistenceDisposition.ReissuedAfterInvalidation =>
                new(
                    persistence.Binding is null
                        ? null
                        : new(
                            persistence.Binding.SchemaVersion,
                            persistence.Binding.ConfirmationId,
                            token,
                            persistence.Binding.ConfirmationExpiresAt),
                    persistence.Disposition,
                    persistence.Binding is null ? RetentionMutationErrorCodes.ConfirmationGenerationFailed : null),
            RetentionConfirmationIssuePersistenceDisposition.ConsumedLinkage =>
                new(null, persistence.Disposition, RetentionMutationErrorCodes.ConfirmationConsumed),
            RetentionConfirmationIssuePersistenceDisposition.Conflict =>
                new(null, persistence.Disposition, RetentionMutationErrorCodes.IdempotencyConflict),
            RetentionConfirmationIssuePersistenceDisposition.Expired =>
                new(null, persistence.Disposition, RetentionMutationErrorCodes.IdempotencyExpired),
            RetentionConfirmationIssuePersistenceDisposition.GenerationFailed =>
                new(null, persistence.Disposition, RetentionMutationErrorCodes.ConfirmationGenerationFailed),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static string? EvaluateIssuanceDrift(
        RetentionStoredMutationPreview stored,
        RetentionMutationPreviewMaterializationResult current)
    {
        // Confirmation issue applies the pinned target/pin/retention/conflict/version
        // rechecks; the complete 1-9 sequence is reserved for 90-D mutation time.
        if (current.Outcome != RetentionMutationPreviewProjectionOutcome.Ready || current.Projection is null)
            return RetentionMutationErrorCodes.ConfirmationTargetChanged;

        var projection = current.Projection;
        if (!string.Equals(projection.TargetItemSetDigest, stored.Response.TargetItemSetDigest, StringComparison.Ordinal))
            return RetentionMutationErrorCodes.ConfirmationTargetChanged;

        if (!PinVectorMatches(stored.Response.TargetItems, projection.TargetItems))
            return RetentionMutationErrorCodes.ConfirmationPinChanged;

        if (!RetentionMatches(stored.Response.TargetItems, projection.TargetItems))
            return RetentionMutationErrorCodes.ConfirmationRetentionChanged;

        var conflicts = current.ConflictSnapshot
            .Select(static item => new RetentionMutationConflictItem(item.ItemId, item.ConflictCode, item.LeaseGeneration))
            .ToArray();
        var conflictSnapshot = RetentionMutationApplicationCanonicalization.ConflictSnapshot(conflicts);
        var conflictVersion = RetentionMutationDigests.ConflictVersion(conflicts);
        if (!string.Equals(conflictSnapshot, stored.ActiveConflictSnapshot, StringComparison.Ordinal)
            || !string.Equals(conflictVersion, stored.ConflictVersion, StringComparison.Ordinal))
            return RetentionMutationErrorCodes.ConfirmationConflictChanged;

        return string.Equals(projection.ExpectedStateVersion, stored.Response.ExpectedStateVersion, StringComparison.Ordinal)
            ? null
            : RetentionMutationErrorCodes.ConfirmationVersionChanged;
    }

    private static bool PinVectorMatches(
        IReadOnlyList<RetentionPreviewItem> expected,
        IReadOnlyList<RetentionPreviewItem> actual) =>
        expected.Count == actual.Count
        && expected.OrderBy(static item => item.ItemId, StringComparer.Ordinal)
            .Zip(actual.OrderBy(static item => item.ItemId, StringComparer.Ordinal))
            .All(static pair => string.Equals(pair.First.ItemId, pair.Second.ItemId, StringComparison.Ordinal)
                && pair.First.PinState == pair.Second.PinState);

    private static bool RetentionMatches(
        IReadOnlyList<RetentionPreviewItem> expected,
        IReadOnlyList<RetentionPreviewItem> actual) =>
        expected.Count == actual.Count
        && expected.OrderBy(static item => item.ItemId, StringComparer.Ordinal)
            .Zip(actual.OrderBy(static item => item.ItemId, StringComparer.Ordinal))
            .All(static pair => string.Equals(pair.First.ItemId, pair.Second.ItemId, StringComparison.Ordinal)
                && pair.First.CapturedAt == pair.Second.CapturedAt
                && pair.First.ExpiresAt == pair.Second.ExpiresAt
                && string.Equals(pair.First.PolicyId, pair.Second.PolicyId, StringComparison.Ordinal)
                && pair.First.PolicyVersion == pair.Second.PolicyVersion
                && pair.First.ReadDeniedAt == pair.Second.ReadDeniedAt
                && pair.First.QueuedAt == pair.Second.QueuedAt
                && pair.First.RetryExhausted == pair.Second.RetryExhausted
                && pair.First.ErrorCode == pair.Second.ErrorCode);

    private static bool IsDigest(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length == prefix.Length + 64
        && value[prefix.Length..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static partial class RetentionMutationApplicationCanonicalization
{
    internal static string ConfirmationRequest(RetentionConfirmationIssueRequest request) =>
        RetentionMutationJcs.Canonicalize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["preview_id"] = request.PreviewId,
            ["preview_digest"] = request.PreviewDigest
        });
}

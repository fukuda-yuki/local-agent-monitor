using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionMutationPreviewApplicationResult(
    RetentionMutationPreviewResponse? Preview,
    string? ErrorCode,
    bool IsReplay = false);

internal sealed partial class RetentionMutationApplicationService
{
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider timeProvider;
    private readonly Func<string> previewIdGenerator;
    private readonly Func<string> confirmationIdGenerator;
    private readonly Func<string> tokenGenerator;

    internal RetentionMutationApplicationService(
        RetentionCatalogStore catalog,
        TimeProvider timeProvider,
        Func<string>? previewIdGenerator = null,
        Func<string>? confirmationIdGenerator = null,
        Func<string>? tokenGenerator = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.previewIdGenerator = previewIdGenerator ?? RetentionMutationIdentifiers.GeneratePreviewId;
        this.confirmationIdGenerator = confirmationIdGenerator ?? RetentionMutationIdentifiers.GenerateConfirmationId;
        this.tokenGenerator = tokenGenerator ?? RetentionMutationToken.Generate;
    }

    internal RetentionMutationPreviewApplicationResult CreatePreview(
        RetentionMutationPreviewRequest? request,
        string? workflowKey)
    {
        var validation = RetentionMutationRequestValidator.Validate(request);
        if (!validation.IsValid || request is null)
            return new(null, RetentionMutationErrorCodes.RequestInvalid);
        if (!RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey))
            return new(null, RetentionMutationErrorCodes.IdempotencyKeyInvalid);

        var normalized = request with { Comment = validation.NormalizedComment };
        var canonicalRequest = RetentionMutationApplicationCanonicalization.PreviewRequest(normalized);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var persistence = catalog.CreateMutationPreview(
            normalized,
            workflowKey!,
            canonicalRequest,
            now,
            (projection, conflicts, createdAt) => CreateStoredPreview(normalized, projection, conflicts, createdAt));

        if (persistence.ErrorCode is not null)
            return new(null, persistence.ErrorCode);
        if (persistence.Disposition == RetentionIdempotencyDisposition.Replayed)
        {
            var replay = DeserializePreview(persistence.ResultJson);
            return new(replay, null, true);
        }

        return new(DeserializePreview(persistence.ResultJson), null);
    }

    internal RetentionMutationPreviewApplicationResult ReadPreview(string? previewId)
    {
        var stored = previewId is null ? null : catalog.ReadMutationPreview(previewId);
        if (stored is null)
            return new(null, RetentionMutationErrorCodes.PreviewNotFound);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        if (stored.ExpiresAt is { } expiresAt && now >= expiresAt)
            return new(null, RetentionMutationErrorCodes.PreviewExpired);

        return new(stored.Response, null);
    }

    private RetentionStoredMutationPreview CreateStoredPreview(
        RetentionMutationPreviewRequest request,
        RetentionMutationPreviewProjection projection,
        IReadOnlyList<RetentionMutationActiveConflictSnapshot> conflicts,
        DateTimeOffset createdAt)
    {
        var digestInput = new RetentionPreviewDigestInput(
            projection.SchemaVersion,
            projection.Result,
            projection.EmptyReason,
            projection.MutationAllowed,
            projection.TargetKind,
            projection.TargetId,
            projection.Operation,
            projection.Scope,
            projection.SourceState,
            projection.SessionCompleteness,
            projection.ContentState,
            projection.CurrentState,
            projection.TargetItems,
            projection.TargetItemCount,
            projection.StoreKindSummary,
            projection.ExcludedItemCount,
            projection.ExcludedItemsByReason,
            projection.CaptureExpiryPolicySummary,
            projection.RetainedMetadataImpact,
            projection.ActiveCleanupExclusionConflicts,
            projection.BackupNonPurgeWarningCode,
            projection.RejectionCode,
            projection.ExpectedStateVersion,
            projection.TargetItemSetDigest);
        var previewDigest = RetentionMutationDigests.PreviewDigest(digestInput);
        DateTimeOffset? expiresAt = projection.BackupNonPurgeWarningCode is not null
            ? createdAt.Add(RetentionMutationConstants.ConfirmationLifetime)
            : null;
        var response = projection.ToResponse(previewIdGenerator(), previewDigest, expiresAt);
        var comment = RetentionMutationCommentValidator.Validate(request.Comment);
        var commentSha256 = comment.NormalizedComment is null
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(comment.NormalizedComment));
        var conflictItems = conflicts
            .Select(static item => new RetentionMutationConflictItem(item.ItemId, item.ConflictCode, item.LeaseGeneration))
            .ToArray();

        return new(
            response,
            createdAt,
            expiresAt,
            RetentionMutationApplicationCanonicalization.ConflictSnapshot(conflictItems),
            RetentionMutationDigests.ConflictVersion(conflictItems),
            request.ReasonCode,
            commentSha256);
    }

    private static RetentionMutationPreviewResponse DeserializePreview(string? resultJson) =>
        resultJson is not null
            ? JsonSerializer.Deserialize<RetentionMutationPreviewResponse>(resultJson)
                ?? throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable)
            : throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable);
}

internal static partial class RetentionMutationApplicationCanonicalization
{
    internal static string PreviewRequest(RetentionMutationPreviewRequest request) =>
        RetentionMutationJcs.Canonicalize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = RetentionMutationWire.TargetKind(request.Target.Kind),
                ["id"] = request.Target.Id
            },
            ["operation"] = RetentionMutationWire.Operation(request.Operation),
            ["scope"] = RetentionMutationWire.Scope(request.Scope),
            ["reason_code"] = request.ReasonCode,
            ["comment"] = request.Comment
        });

    internal static string ConflictSnapshot(IEnumerable<RetentionMutationConflictItem> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var order = RetentionMutationConflictCodes.All
            .Select((code, index) => (code, index))
            .ToDictionary(static pair => pair.code, static pair => pair.index, StringComparer.Ordinal);
        var values = conflicts
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => order.TryGetValue(item.ConflictCode, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.ConflictCode, StringComparer.Ordinal)
            .Select(static item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["conflict_code"] = item.ConflictCode,
                ["lease_generation"] = item.LeaseGeneration
            })
            .ToArray();
        return RetentionMutationJcs.Canonicalize(values);
    }
}

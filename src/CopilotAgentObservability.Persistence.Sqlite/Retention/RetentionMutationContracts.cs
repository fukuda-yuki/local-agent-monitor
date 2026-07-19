using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public enum RetentionMutationTargetKind { Session, Item }
public enum RetentionMutationScope { SessionItems, SingleItem }
public enum RetentionMutationOperation { Pin, Unpin, DeleteNow }
public enum RetentionPinState { Pinned, Unpinned, NotApplicable, Mixed }
public enum RetentionDeleteState { NotRequested, Queued, InProgress, Deleted, Failed }
public enum RetentionMutationStageClassification { PreviewStageAllowed, PreviewStageRejection, CommitStageOutcome, CommitStageRejection }
public enum RetentionMutationReachabilityClass { RequestStage, PreviewStage, ConfirmationIssueStage, CommitStage, Warning }
public enum RetentionMutationEvaluationCheck { TokenValidity = 1, TokenConsumption = 2, Expiry = 3, Binding = 4, TargetSet = 5, PinVector = 6, Retention = 7, Conflict = 8, Version = 9 }
public enum RetentionMutationOperationStep { Preview, ConfirmationIssue, Mutation }

public static class RetentionMutationWire
{
    public static string TargetKind(RetentionMutationTargetKind value) => value switch
    {
        RetentionMutationTargetKind.Session => "session",
        RetentionMutationTargetKind.Item => "item",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string Scope(RetentionMutationScope value) => value switch
    {
        RetentionMutationScope.SessionItems => "session_items",
        RetentionMutationScope.SingleItem => "single_item",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string Operation(RetentionMutationOperation value) => value switch
    {
        RetentionMutationOperation.Pin => "pin",
        RetentionMutationOperation.Unpin => "unpin",
        RetentionMutationOperation.DeleteNow => "delete_now",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string PinState(RetentionPinState value) => value switch
    {
        RetentionPinState.Pinned => "pinned",
        RetentionPinState.Unpinned => "unpinned",
        RetentionPinState.NotApplicable => "not_applicable",
        RetentionPinState.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string DeleteState(RetentionDeleteState value) => value switch
    {
        RetentionDeleteState.NotRequested => "not_requested",
        RetentionDeleteState.Queued => "queued",
        RetentionDeleteState.InProgress => "in_progress",
        RetentionDeleteState.Deleted => "deleted",
        RetentionDeleteState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public sealed record RetentionMutationTarget(RetentionMutationTargetKind Kind, string Id);

public sealed record RetentionMutationTargetValidationResult(bool IsValid, string? ErrorCode)
{
    public static RetentionMutationTargetValidationResult Valid { get; } = new(true, null);
    public static RetentionMutationTargetValidationResult Invalid { get; } = new(false, RetentionMutationErrorCodes.RequestInvalid);
}

public static class RetentionMutationTargetValidator
{
    public static RetentionMutationTargetValidationResult Validate(RetentionMutationTarget? target)
    {
        if (target is null || !Enum.IsDefined(target.Kind) || string.IsNullOrWhiteSpace(target.Id)) return RetentionMutationTargetValidationResult.Invalid;
        if (target.Kind == RetentionMutationTargetKind.Session
            && (!Guid.TryParseExact(target.Id, "D", out var sessionId) || !string.Equals(target.Id, sessionId.ToString("D"), StringComparison.Ordinal)))
            return RetentionMutationTargetValidationResult.Invalid;
        return RetentionMutationTargetValidationResult.Valid;
    }

    public static string NormalizeOpaqueItemId(RetentionMutationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != RetentionMutationTargetKind.Item || Validate(target) is { IsValid: false }) throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(target));
        return target.Id;
    }
}

public static class RetentionMutationSessionLinkage
{
    public static bool Qualifies(RetentionStoreKind storeKind, string? sourceItemId, string? joinedSessionId, string requestedSessionId) =>
        storeKind == RetentionStoreKind.SessionEventContent
        && !string.IsNullOrEmpty(sourceItemId)
        && joinedSessionId is not null
        && string.Equals(joinedSessionId, requestedSessionId, StringComparison.Ordinal);
}

public sealed record RetentionMutationPreviewRequest(
    RetentionMutationTarget Target,
    RetentionMutationOperation Operation,
    RetentionMutationScope Scope,
    string ReasonCode,
    string? Comment);

public sealed record RetentionMutationRequestValidationResult(bool IsValid, string? ErrorCode, string? NormalizedComment);

public static class RetentionMutationRequestValidator
{
    public static RetentionMutationRequestValidationResult Validate(RetentionMutationPreviewRequest? request)
    {
        if (request is null || !RetentionMutationTargetValidator.Validate(request.Target).IsValid || !Enum.IsDefined(request.Operation) || !Enum.IsDefined(request.Scope))
            return new(false, RetentionMutationErrorCodes.RequestInvalid, null);
        var expectedScope = request.Target.Kind == RetentionMutationTargetKind.Session ? RetentionMutationScope.SessionItems : RetentionMutationScope.SingleItem;
        if (request.Scope != expectedScope || !RetentionMutationReasonCodes.All.Contains(request.ReasonCode, StringComparer.Ordinal))
            return new(false, RetentionMutationErrorCodes.RequestInvalid, null);
        var comment = RetentionMutationCommentValidator.Validate(request.Comment);
        return comment.IsValid
            ? new(true, null, comment.NormalizedComment)
            : new(false, RetentionMutationErrorCodes.RequestInvalid, null);
    }
}

public sealed record RetentionMutationEffects(bool TokenConsumed, bool AuditWritten, bool IdempotencyStored, bool StateChanged);

public sealed record RetentionMutationItemState(
    RetentionItemLifecycle State,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    string PolicyId,
    int PolicyVersion,
    long Revision);

public static class RetentionMutationLifecycleStates
{
    public static IReadOnlyList<RetentionItemLifecycle> All { get; } =
    [
        RetentionItemLifecycle.Expiring,
        RetentionItemLifecycle.RetainedByPolicy,
        RetentionItemLifecycle.ExpiredPendingDeletion,
        RetentionItemLifecycle.DeletionQueued,
        RetentionItemLifecycle.Deleting,
        RetentionItemLifecycle.Deleted,
        RetentionItemLifecycle.DeletionFailed
    ];
}

public static class RetentionMutationStateProjection
{
    public static RetentionPinState PinState(RetentionItemLifecycle state) =>
        state == RetentionItemLifecycle.RetainedByPolicy ? RetentionPinState.Pinned : RetentionPinState.Unpinned;

    public static RetentionDeleteState DeleteState(RetentionItemLifecycle state) => state switch
    {
        RetentionItemLifecycle.Expiring or RetentionItemLifecycle.RetainedByPolicy => RetentionDeleteState.NotRequested,
        RetentionItemLifecycle.ExpiredPendingDeletion or RetentionItemLifecycle.DeletionQueued => RetentionDeleteState.Queued,
        RetentionItemLifecycle.Deleting => RetentionDeleteState.InProgress,
        RetentionItemLifecycle.Deleted => RetentionDeleteState.Deleted,
        RetentionItemLifecycle.DeletionFailed => RetentionDeleteState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

public static class RetentionMutationConstants
{
    public const int SchemaVersion = 1;
    public const int TargetItemLimit = 100;
    public const int IdempotencyLifetimeDays = 365;
    public const string ActorLabel = "local-user";
    public const string EventType = "retention_mutation";
    public const string BackupWarningCode = RetentionMutationErrorCodes.BackupNotPurged;
    public static TimeSpan ConfirmationLifetime => TimeSpan.FromMinutes(5);
}

public static class RetentionMutationReasonCodes
{
    public const string ResearchNeeded = "research_needed";
    public const string ReviewComplete = "review_complete";
    public const string PrivacyRequest = "privacy_request";
    public const string DataMinimization = "data_minimization";
    public const string TestCleanup = "test_cleanup";
    public const string OperatorCorrection = "operator_correction";
    public const string OtherLocalReason = "other_local_reason";
    public static IReadOnlyList<string> All { get; } = [ResearchNeeded, ReviewComplete, PrivacyRequest, DataMinimization, TestCleanup, OperatorCorrection, OtherLocalReason];
}

public static class RetentionMutationCompletionCodes
{
    public const string PinApplied = "retention_pin_applied";
    public const string PinNoop = "retention_pin_noop";
    public const string UnpinApplied = "retention_unpin_applied";
    public const string UnpinNoop = "retention_unpin_noop";
    public const string UnpinExpiredQueued = "retention_unpin_expired_queued";
    public const string DeleteQueued = "retention_delete_queued";
    public const string DeleteAlreadyQueued = "retention_delete_already_queued";
    public const string DeleteNowSupersededPin = "retention_delete_now_superseded_pin";
    public static IReadOnlyList<string> All { get; } = [PinApplied, PinNoop, UnpinApplied, UnpinNoop, UnpinExpiredQueued, DeleteQueued, DeleteAlreadyQueued, DeleteNowSupersededPin];
}

public static class RetentionMutationResultCodes
{
    public const string Replayed = "retention_mutation_replayed";
}

public static class RetentionMutationExclusionCodes
{
    public const string MissingOwnershipProof = "missing_ownership_proof";
    public static IReadOnlyList<string> All { get; } = [MissingOwnershipProof];
}

public static class RetentionMutationConflictCodes
{
    public const string ActiveReadLease = "active_read_lease";
    public const string ActiveOperationLease = "active_operation_lease";
    public const string ActiveDeletionLease = "active_deletion_lease";
    public const string ActiveDeleteIntent = "active_delete_intent";
    public static IReadOnlyList<string> All { get; } = [ActiveReadLease, ActiveOperationLease, ActiveDeletionLease, ActiveDeleteIntent];
}

public static class RetentionMutationErrorCodes
{
    public const string RequestInvalid = "retention_mutation_request_invalid";
    public const string TargetNotFound = "retention_target_not_found";
    public const string TargetLimitExceeded = "retention_mutation_target_limit_exceeded";
    public const string PreviewNotFound = "retention_preview_not_found";
    public const string IdempotencyKeyInvalid = "retention_idempotency_key_invalid";
    public const string IdempotencyConflict = "retention_idempotency_conflict";
    public const string IdempotencyExpired = "retention_idempotency_expired";
    public const string OperationNotFound = "retention_operation_not_found";
    public const string HistoryCursorInvalid = "retention_history_cursor_invalid";
    public const string CatalogUnavailable = "retention_catalog_unavailable";
    public const string TargetNotApplicable = "retention_target_not_applicable";
    public const string PinReadDenied = "retention_pin_read_denied";
    public const string PinDeleting = "retention_pin_deleting";
    public const string PinDeleted = "retention_pin_deleted";
    public const string UnpinReadDenied = "retention_unpin_read_denied";
    public const string UnpinDeleting = "retention_unpin_deleting";
    public const string UnpinDeleted = "retention_unpin_deleted";
    public const string DeleteAlreadyDeleting = "retention_delete_already_deleting";
    public const string DeleteAlreadyDeleted = "retention_delete_already_deleted";
    public const string DeleteFailed = "retention_delete_failed";
    public const string TargetEmpty = "retention_target_empty";
    public const string PreviewExpired = "retention_preview_expired";
    public const string PreviewDigestMismatch = "retention_preview_digest_mismatch";
    public const string ConfirmationGenerationFailed = "retention_confirmation_generation_failed";
    public const string ConfirmationConsumed = "retention_confirmation_consumed";
    public const string ConfirmationInvalid = "retention_confirmation_invalid";
    public const string ConfirmationExpired = "retention_confirmation_expired";
    public const string ConfirmationBindingMismatch = "retention_confirmation_binding_mismatch";
    public const string ConfirmationTargetChanged = "retention_confirmation_target_changed";
    public const string ConfirmationPinChanged = "retention_confirmation_pin_changed";
    public const string ConfirmationRetentionChanged = "retention_confirmation_retention_changed";
    public const string ConfirmationConflictChanged = "retention_confirmation_conflict_changed";
    public const string ConfirmationVersionChanged = "retention_confirmation_version_changed";
    public const string PinExpired = "retention_pin_expired";
    public const string MutationTransactionFailed = "retention_mutation_transaction_failed";
    public const string AuditWriteFailed = "retention_audit_write_failed";
    public const string DeleteAlreadyQueued = "retention_delete_already_queued";
    public const string BackupNotPurged = "retention_backup_not_purged";
}

public sealed record RetentionMutationErrorCodeEntry(
    string Code,
    RetentionMutationReachabilityClass Reachability,
    int? MutationTimeCheck,
    int? HttpStatus,
    int? PreviewHttpStatus = null,
    int? ConfirmationIssueHttpStatus = null);

public static class RetentionMutationErrorCodeRegistry
{
    public static IReadOnlyList<RetentionMutationErrorCodeEntry> All { get; } =
    [
        new(RetentionMutationErrorCodes.RequestInvalid, RetentionMutationReachabilityClass.RequestStage, null, 400),
        new(RetentionMutationErrorCodes.TargetNotFound, RetentionMutationReachabilityClass.RequestStage, null, 404),
        new(RetentionMutationErrorCodes.TargetLimitExceeded, RetentionMutationReachabilityClass.RequestStage, null, 413),
        new(RetentionMutationErrorCodes.PreviewNotFound, RetentionMutationReachabilityClass.RequestStage, null, 404),
        new(RetentionMutationErrorCodes.IdempotencyKeyInvalid, RetentionMutationReachabilityClass.RequestStage, null, 400),
        new(RetentionMutationErrorCodes.IdempotencyConflict, RetentionMutationReachabilityClass.RequestStage, null, 409),
        new(RetentionMutationErrorCodes.IdempotencyExpired, RetentionMutationReachabilityClass.RequestStage, null, 409),
        new(RetentionMutationErrorCodes.OperationNotFound, RetentionMutationReachabilityClass.RequestStage, null, 404),
        new(RetentionMutationErrorCodes.HistoryCursorInvalid, RetentionMutationReachabilityClass.RequestStage, null, 400),
        new(RetentionMutationErrorCodes.CatalogUnavailable, RetentionMutationReachabilityClass.RequestStage, null, 503),
        new(RetentionMutationErrorCodes.TargetNotApplicable, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.PinReadDenied, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.PinDeleting, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.PinDeleted, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.UnpinReadDenied, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.UnpinDeleting, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.UnpinDeleted, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.DeleteAlreadyDeleting, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.DeleteAlreadyDeleted, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.DeleteFailed, RetentionMutationReachabilityClass.PreviewStage, null, 409, 200, 409),
        new(RetentionMutationErrorCodes.TargetEmpty, RetentionMutationReachabilityClass.ConfirmationIssueStage, null, 409),
        new(RetentionMutationErrorCodes.PreviewExpired, RetentionMutationReachabilityClass.ConfirmationIssueStage, null, 409),
        new(RetentionMutationErrorCodes.PreviewDigestMismatch, RetentionMutationReachabilityClass.ConfirmationIssueStage, null, 409),
        new(RetentionMutationErrorCodes.ConfirmationGenerationFailed, RetentionMutationReachabilityClass.ConfirmationIssueStage, null, 503),
        new(RetentionMutationErrorCodes.ConfirmationConsumed, RetentionMutationReachabilityClass.ConfirmationIssueStage, 2, 409),
        new(RetentionMutationErrorCodes.ConfirmationInvalid, RetentionMutationReachabilityClass.CommitStage, 1, 401),
        new(RetentionMutationErrorCodes.ConfirmationExpired, RetentionMutationReachabilityClass.CommitStage, 3, 409),
        new(RetentionMutationErrorCodes.ConfirmationBindingMismatch, RetentionMutationReachabilityClass.CommitStage, 4, 409),
        new(RetentionMutationErrorCodes.ConfirmationTargetChanged, RetentionMutationReachabilityClass.CommitStage, 5, 409),
        new(RetentionMutationErrorCodes.ConfirmationPinChanged, RetentionMutationReachabilityClass.CommitStage, 6, 409),
        new(RetentionMutationErrorCodes.ConfirmationRetentionChanged, RetentionMutationReachabilityClass.CommitStage, 7, 409),
        new(RetentionMutationErrorCodes.ConfirmationConflictChanged, RetentionMutationReachabilityClass.CommitStage, 8, 409),
        new(RetentionMutationErrorCodes.ConfirmationVersionChanged, RetentionMutationReachabilityClass.CommitStage, 9, 409),
        new(RetentionMutationErrorCodes.PinExpired, RetentionMutationReachabilityClass.CommitStage, null, 409),
        new(RetentionMutationErrorCodes.MutationTransactionFailed, RetentionMutationReachabilityClass.CommitStage, null, 503),
        new(RetentionMutationErrorCodes.AuditWriteFailed, RetentionMutationReachabilityClass.CommitStage, null, 503),
        new(RetentionMutationErrorCodes.DeleteAlreadyQueued, RetentionMutationReachabilityClass.CommitStage, null, 200),
        new(RetentionMutationErrorCodes.BackupNotPurged, RetentionMutationReachabilityClass.Warning, null, 200)
    ];

    public static RetentionMutationErrorCodeEntry Get(string code) => All.FirstOrDefault(entry => string.Equals(entry.Code, code, StringComparison.Ordinal)) ?? throw new ArgumentOutOfRangeException(nameof(code));
}

public sealed record RetentionCommentValidationResult(bool IsValid, string? NormalizedComment, string? ErrorCode);

public static class RetentionMutationCommentValidator
{
    private static readonly string[] CredentialMarkers = ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "authorization", "bearer", "credential"];
    private static readonly string[] DatabaseKeyMarkers = ["rowid", "primary key", "primary_key", "autoincrement", "rpv1_", "rcid1_", "rt90v1_", "rid1_", "rae1_", "rhc1_"];

    public static RetentionCommentValidationResult Validate(string? comment)
    {
        if (comment is null) return new(true, null, null);
        if (!IsWellFormedUtf16(comment)) return Invalid();
        var normalized = comment.Normalize(NormalizationForm.FormC);
        if (normalized.EnumerateRunes().Count() is < 1 or > 256) return Invalid();
        foreach (var rune in normalized.EnumerateRunes()) if (Rune.IsControl(rune)) return Invalid();
        if (normalized.Contains('\r') || normalized.Contains('\n') || normalized.Contains('/') || normalized.Contains('\\')) return Invalid();
        var lower = normalized.ToLowerInvariant();
        if (Regex.IsMatch(normalized, @"[A-Za-z][A-Za-z0-9+.-]*:\S", RegexOptions.CultureInvariant)
            || lower.Contains("://", StringComparison.Ordinal)
            || lower.Contains("www.", StringComparison.Ordinal)) return Invalid();
        if (CredentialMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal))) return Invalid();
        if (DatabaseKeyMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal))) return Invalid();
        return new(true, normalized, null);
    }

    private static RetentionCommentValidationResult Invalid() => new(false, null, RetentionMutationErrorCodes.RequestInvalid);

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
            index++;
        }
        return true;
    }
}

public sealed record RetentionMutationConfirmRequest(string ConfirmationToken, RetentionMutationOperation Operation, RetentionMutationScope Scope, RetentionMutationTargetKind TargetKind, string TargetId);

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public enum RetentionMutationPreviewProjectionOutcome
{
    Ready,
    TargetNotFound,
    TargetLimitExceeded
}

public sealed record RetentionMutationPreviewProjectionResult(
    RetentionMutationPreviewProjectionOutcome Outcome,
    string? ErrorCode,
    RetentionMutationPreviewProjection? Projection);

public sealed record RetentionMutationPreviewProjection(
    int SchemaVersion,
    RetentionMutationPreviewResult Result,
    RetentionMutationEmptyReason? EmptyReason,
    bool MutationAllowed,
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
    string? RejectionCode)
{
    public RetentionMutationPreviewResponse ToResponse(
        string previewId,
        string previewDigest,
        DateTimeOffset? confirmationExpiresAt) => new(
            SchemaVersion,
            Result,
            EmptyReason,
            MutationAllowed,
            previewId,
            TargetKind,
            TargetId,
            Operation,
            Scope,
            SourceState,
            SessionCompleteness,
            ContentState,
            CurrentState,
            TargetItems,
            TargetItemCount,
            StoreKindSummary,
            ExcludedItemCount,
            ExcludedItemsByReason,
            CaptureExpiryPolicySummary,
            RetainedMetadataImpact,
            ActiveCleanupExclusionConflicts,
            BackupNonPurgeWarningCode,
            ExpectedStateVersion,
            TargetItemSetDigest,
            previewDigest,
            confirmationExpiresAt,
            RejectionCode);
}

internal sealed record RetentionMutationSessionProjection(
    RetentionMutationSourceState SourceState,
    RetentionMutationSessionCompleteness Completeness,
    string? ContentState);

public sealed record RetentionMutationActiveConflictSnapshot(
    string ItemId,
    string ConflictCode,
    long LeaseGeneration);

public static class RetentionMutationPreviewProjector
{
    internal static RetentionMutationPreviewProjection Project(
        RetentionMutationTarget target,
        RetentionMutationOperation operation,
        RetentionMutationScope scope,
        RetentionMutationTargetResolution resolution,
        RetentionMutationVersionVector versionVector,
        RetentionMutationSessionProjection? sessionProjection,
        IReadOnlyList<RetentionMutationActiveConflictSnapshot> conflicts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(versionVector);
        ArgumentNullException.ThrowIfNull(conflicts);
        if (resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotFound)
            throw new InvalidOperationException(RetentionMutationErrorCodes.TargetNotFound);

        var items = resolution.Items
            .OrderBy(static item => item.ExpiresAt is null ? 1 : 0)
            .ThenBy(static item => item.ExpiresAt)
            .ThenBy(static item => item.ItemId, StringComparer.Ordinal)
            .Select(static item => new RetentionPreviewItem(
                item.ItemId,
                item.StoreKind,
                item.State,
                RetentionMutationStateProjection.PinState(item.State),
                RetentionMutationStateProjection.DeleteState(item.State),
                item.CapturedAt,
                item.ExpiresAt,
                item.PolicyId,
                item.PolicyVersion,
                item.ReadDeniedAt,
                item.QueuedAt,
                item.Revision,
                item.RetryExhausted,
                item.ErrorCode))
            .ToArray();

        var currentState = CurrentState(items, now);
        var storeSummary = Enum.GetValues<RetentionStoreKind>()
            .Where(kind => items.Any(item => item.StoreKind == kind))
            .Select(kind => new RetentionStoreKindSummary(
                kind,
                items.Count(item => item.StoreKind == kind),
                items.Count(item => item.StoreKind == kind && IsReadable(item, now)),
                items.Count(item => item.StoreKind == kind && !IsReadable(item, now))))
            .ToArray();
        var policySummary = items
            .GroupBy(static item => (item.PolicyId, item.PolicyVersion))
            .OrderBy(group => group.Key.PolicyId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PolicyVersion)
            .Take(5)
            .Select(group => new RetentionCaptureExpiryPolicySummary(
                group.Key.PolicyId,
                group.Key.PolicyVersion,
                group.Count(),
                Min(group.Select(static item => item.CapturedAt)),
                Max(group.Select(static item => item.CapturedAt)),
                Min(group.Select(OriginalExpiry)),
                Max(group.Select(OriginalExpiry))))
            .ToArray();
        var conflictSummary = ProjectConflicts(conflicts);
        var rejectionCode = resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotApplicable
            ? RetentionMutationErrorCodes.TargetNotApplicable
            : items.Select(item => RetentionMutationTransitions.EvaluatePreview(operation, item.State, now, item.ExpiresAt).Code).FirstOrDefault(static code => code is not null);
        var empty = resolution.Outcome == RetentionMutationTargetResolutionOutcome.EmptyNotApplicable;
        var notApplicable = resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotApplicable;

        return new(
            RetentionMutationConstants.SchemaVersion,
            empty ? RetentionMutationPreviewResult.EmptyNotApplicable : RetentionMutationPreviewResult.Actionable,
            empty ? resolution.EmptyReason : null,
            !empty && !notApplicable && items.Length > 0 && rejectionCode is null,
            target.Kind,
            target.Id,
            operation,
            scope,
            sessionProjection?.SourceState,
            sessionProjection?.Completeness,
            sessionProjection?.ContentState,
            currentState,
            items,
            items.Length,
            storeSummary,
            resolution.ExcludedItemCount,
            resolution.ExcludedItemsByReason,
            policySummary,
            RetainedImpact(target, operation, items, now),
            conflictSummary,
            empty || notApplicable ? null : RetentionMutationConstants.BackupWarningCode,
            versionVector.ExpectedStateVersion,
            versionVector.TargetItemSetDigest,
            rejectionCode);
    }

    private static RetentionCurrentStateSummary CurrentState(IReadOnlyList<RetentionPreviewItem> items, DateTimeOffset now)
    {
        var readable = items.Count(item => IsReadable(item, now));
        return new(
            readable,
            items.Count - readable,
            items.Count(item => item.State == RetentionItemLifecycle.RetainedByPolicy),
            items.Count(item => item.State != RetentionItemLifecycle.RetainedByPolicy),
            RetentionMutationLifecycleCounts.From(items.Select(static item => item.State)));
    }

    private static bool IsReadable(RetentionPreviewItem item, DateTimeOffset now) =>
        item.ReadDeniedAt is null
        && (item.State == RetentionItemLifecycle.RetainedByPolicy
            || item.State == RetentionItemLifecycle.Expiring && item.ExpiresAt is { } expiresAt && expiresAt > now);

    private static IReadOnlyList<RetentionActiveConflictSummary> ProjectConflicts(IReadOnlyList<RetentionMutationActiveConflictSnapshot> conflicts)
    {
        if (conflicts.Count == 0) return [];
        var version = RetentionMutationDigests.ConflictVersion(conflicts.Select(static item => new RetentionMutationConflictItem(item.ItemId, item.ConflictCode, item.LeaseGeneration)));
        var order = RetentionMutationConflictCodes.All.Select((code, index) => (code, index)).ToDictionary(static value => value.code, static value => value.index, StringComparer.Ordinal);
        return conflicts
            .GroupBy(static item => item.ConflictCode)
            .OrderBy(group => order.TryGetValue(group.Key, out var index) ? index : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(4)
            .Select(group => new RetentionActiveConflictSummary(group.Key, group.Select(static item => item.ItemId).Distinct(StringComparer.Ordinal).Count(), version))
            .ToArray();
    }

    private static RetentionRetainedImpact RetainedImpact(
        RetentionMutationTarget target,
        RetentionMutationOperation operation,
        IReadOnlyList<RetentionPreviewItem> items,
        DateTimeOffset now) => new(
        operation == RetentionMutationOperation.DeleteNow
            || operation == RetentionMutationOperation.Unpin && items.Any(item => OriginalExpiry(item) is { } expiry && expiry <= now),
        target.Kind == RetentionMutationTargetKind.Session && items.Count > 0,
        target.Kind == RetentionMutationTargetKind.Session ? items.Count : 0,
        0,
        0);

    private static DateTimeOffset? OriginalExpiry(RetentionPreviewItem item) =>
        item.CapturedAt is not { } capturedAt
            ? item.ExpiresAt
            : item.PolicyId switch
            {
                "raw-default-90d" when item.PolicyVersion == 1 => capturedAt + RetentionV1Constants.RawDefaultTtl,
                "sensitive-bundle-7d" when item.PolicyVersion == 1 => capturedAt + RetentionV1Constants.SensitiveBundleTtl,
                _ => item.ExpiresAt
            };

    private static DateTimeOffset? OriginalExpiry(RetentionMutationResolvedItem item) =>
        item.CapturedAt is not { } capturedAt
            ? item.ExpiresAt
            : item.PolicyId switch
            {
                "raw-default-90d" when item.PolicyVersion == 1 => capturedAt + RetentionV1Constants.RawDefaultTtl,
                "sensitive-bundle-7d" when item.PolicyVersion == 1 => capturedAt + RetentionV1Constants.SensitiveBundleTtl,
                _ => item.ExpiresAt
            };

    private static DateTimeOffset? Min(IEnumerable<DateTimeOffset?> values)
    {
        var timestamps = values.OfType<DateTimeOffset>().ToArray();
        return timestamps.Length == 0 ? null : timestamps.Min();
    }

    private static DateTimeOffset? Max(IEnumerable<DateTimeOffset?> values)
    {
        var timestamps = values.OfType<DateTimeOffset>().ToArray();
        return timestamps.Length == 0 ? null : timestamps.Max();
    }
}

public sealed partial class RetentionCatalogStore
{
    public RetentionMutationPreviewProjectionResult CollectMutationPreviewProjection(
        RetentionMutationTarget target,
        RetentionMutationOperation operation,
        RetentionMutationScope scope,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!RetentionMutationTargetValidator.Validate(target).IsValid
            || !Enum.IsDefined(operation)
            || !Enum.IsDefined(scope)
            || target.Kind == RetentionMutationTargetKind.Session && scope != RetentionMutationScope.SessionItems
            || target.Kind == RetentionMutationTargetKind.Item && scope != RetentionMutationScope.SingleItem)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid);

        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var result = MaterializeMutationPreviewWithinTransaction(connection, transaction, target, operation, scope, now);
        transaction.Commit();
        return new(result.Outcome, result.ErrorCode, result.Projection);
    }

    internal RetentionMutationPreviewMaterializationResult CollectMutationPreviewMaterialization(
        RetentionMutationTarget target,
        RetentionMutationOperation operation,
        RetentionMutationScope scope,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var result = MaterializeMutationPreviewWithinTransaction(connection, transaction, target, operation, scope, now);
        transaction.Commit();
        return result;
    }

    internal RetentionMutationPreviewMaterializationResult MaterializeMutationPreviewWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionMutationTarget target,
        RetentionMutationOperation operation,
        RetentionMutationScope scope,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(target);
        if (!RetentionMutationTargetValidator.Validate(target).IsValid
            || !Enum.IsDefined(operation)
            || !Enum.IsDefined(scope)
            || target.Kind == RetentionMutationTargetKind.Session && scope != RetentionMutationScope.SessionItems
            || target.Kind == RetentionMutationTargetKind.Item && scope != RetentionMutationScope.SingleItem)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid);

        var resolution = target.Kind switch
        {
            RetentionMutationTargetKind.Session => ResolveSessionTarget(connection, transaction, target.Id),
            RetentionMutationTargetKind.Item => ResolveItemTarget(connection, transaction, target.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        if (resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotFound)
            return new(RetentionMutationPreviewProjectionOutcome.TargetNotFound, resolution.ErrorCode, null, []);
        if (resolution.Items.Count > RetentionMutationConstants.TargetItemLimit)
            return new(RetentionMutationPreviewProjectionOutcome.TargetLimitExceeded, RetentionMutationErrorCodes.TargetLimitExceeded, null, []);

        var versionVector = resolution.Outcome == RetentionMutationTargetResolutionOutcome.NotApplicable
            ? EmptyVersionVector()
            : MaterializeMutationVersionVector(connection, transaction, resolution.Items.Select(static item => item.ItemId).ToArray());
        var sessionProjection = target.Kind == RetentionMutationTargetKind.Session
            ? ReadSessionProjection(connection, transaction, target.Id)
            : null;
        var conflicts = ReadActiveConflicts(connection, transaction, resolution.Items.Select(static item => item.ItemId).ToArray(), now);
        var projection = RetentionMutationPreviewProjector.Project(target, operation, scope, resolution, versionVector, sessionProjection, conflicts, now);
        return new(RetentionMutationPreviewProjectionOutcome.Ready, null, projection, conflicts);
    }

    private static RetentionMutationVersionVector EmptyVersionVector()
    {
        var expected = Array.Empty<RetentionMutationExpectedStateItem>();
        var targets = Array.Empty<RetentionMutationDigestItem>();
        return new(expected, targets, RetentionMutationDigests.ExpectedStateVersion(expected), RetentionMutationDigests.TargetItemSetDigest(targets));
    }

    private static RetentionMutationSessionProjection ReadSessionProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var completeness = connection.CreateCommand();
        completeness.Transaction = transaction;
        completeness.CommandText = "SELECT completeness FROM sessions WHERE session_id=$session COLLATE BINARY;";
        completeness.Parameters.AddWithValue("$session", sessionId);
        var completenessValue = (string)completeness.ExecuteScalar()!;
        using var content = connection.CreateCommand();
        content.Transaction = transaction;
        content.CommandText = """
            SELECT CASE
                WHEN EXISTS(SELECT 1 FROM session_events WHERE session_id=$session COLLATE BINARY AND content_state='available') THEN 'available'
                WHEN EXISTS(SELECT 1 FROM session_events WHERE session_id=$session COLLATE BINARY AND content_state='expired_pending_deletion') THEN 'expired_pending_deletion'
                WHEN EXISTS(SELECT 1 FROM session_events WHERE session_id=$session COLLATE BINARY AND content_state='redacted') THEN 'redacted'
                WHEN EXISTS(SELECT 1 FROM session_events WHERE session_id=$session COLLATE BINARY AND content_state='unsupported') THEN 'unsupported'
                ELSE 'not_captured'
            END;
            """;
        content.Parameters.AddWithValue("$session", sessionId);
        var contentState = (string)content.ExecuteScalar()!;
        var sourceState = contentState switch
        {
            "available" => RetentionMutationSourceState.Available,
            "redacted" => RetentionMutationSourceState.Redacted,
            "unsupported" => RetentionMutationSourceState.Unsupported,
            "not_captured" => RetentionMutationSourceState.NotCaptured,
            _ => RetentionMutationSourceState.Unknown
        };
        var sessionCompleteness = completenessValue switch
        {
            "unbound" => RetentionMutationSessionCompleteness.Unbound,
            "partial" => RetentionMutationSessionCompleteness.Partial,
            "rich" => RetentionMutationSessionCompleteness.Rich,
            "full" => RetentionMutationSessionCompleteness.Full,
            _ => RetentionMutationSessionCompleteness.Unbound
        };
        return new(sourceState, sessionCompleteness, contentState);
    }

    private static IReadOnlyList<RetentionMutationActiveConflictSnapshot> ReadActiveConflicts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> itemIds,
        DateTimeOffset now)
    {
        if (itemIds.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[itemIds.Count];
        for (var index = 0; index < itemIds.Count; index++)
        {
            parameters[index] = "$item" + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(parameters[index], itemIds[index]);
        }
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.CommandText = $"""
            SELECT item_id,lease_kind,generation
            FROM retention_leases
            WHERE item_id IN ({string.Join(',', parameters)}) AND expires_at>$now
            UNION ALL
            SELECT item_id,'delete_intent',expected_revision
            FROM retention_delete_journal
            WHERE item_id IN ({string.Join(',', parameters)});
            """;
        using var reader = command.ExecuteReader();
        var conflicts = new List<RetentionMutationActiveConflictSnapshot>();
        while (reader.Read())
        {
            var code = reader.GetString(1) switch
            {
                "access" => RetentionMutationConflictCodes.ActiveReadLease,
                "operation" => RetentionMutationConflictCodes.ActiveOperationLease,
                "deletion" => RetentionMutationConflictCodes.ActiveDeletionLease,
                "delete_intent" => RetentionMutationConflictCodes.ActiveDeleteIntent,
                _ => null
            };
            if (code is not null) conflicts.Add(new(reader.GetString(0), code, reader.GetInt64(2)));
        }
        return conflicts;
    }
}

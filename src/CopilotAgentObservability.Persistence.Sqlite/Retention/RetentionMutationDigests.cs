using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionMutationDigestItem(string ItemId, RetentionStoreKind StoreKind);
public sealed record RetentionMutationExpectedStateItem(string ItemId, long Revision, RetentionPinState PinState, RetentionItemLifecycle State);
public sealed record RetentionMutationConflictItem(string ItemId, string ConflictCode, long LeaseGeneration);
public sealed record RetentionPreviewDigestInput(
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
    string? RejectionCode,
    string ExpectedStateVersion,
    string TargetItemSetDigest);

public static class RetentionMutationDigests
{
    public static string TargetItemSetDigest(IEnumerable<RetentionMutationDigestItem> items) =>
        HashPrefixed(TargetItemSetCanonicalJson(items), "sha256-");

    public static string TargetItemSetCanonicalJson(IEnumerable<RetentionMutationDigestItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => item.StoreKind)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["store_kind"] = StoreKind(item.StoreKind)
            })
            .ToArray();
        return RetentionMutationJcs.Canonicalize(values);
    }

    public static string ExpectedStateVersion(IEnumerable<RetentionMutationExpectedStateItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["revision"] = item.Revision,
                ["pin_state"] = PinState(item.PinState),
                ["state"] = Lifecycle(item.State)
            })
            .ToArray();
        return HashPrefixed(RetentionMutationJcs.Canonicalize(values), "v1-");
    }

    public static string ConflictVersion(IEnumerable<RetentionMutationConflictItem> conflicts)
    {
        return HashPrefixed(ConflictCanonicalJson(conflicts), "v1-");
    }

    internal static string ConflictCanonicalJson(IEnumerable<RetentionMutationConflictItem> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var registryOrder = RetentionMutationConflictCodes.All.Select((code, index) => (code, index)).ToDictionary(pair => pair.code, pair => pair.index, StringComparer.Ordinal);
        var values = conflicts
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => registryOrder.TryGetValue(item.ConflictCode, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.ConflictCode, StringComparer.Ordinal)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["conflict_code"] = item.ConflictCode,
                ["lease_generation"] = item.LeaseGeneration
            })
            .ToArray();
        return RetentionMutationJcs.Canonicalize(values);
    }

    public static string PreviewDigest(RetentionPreviewDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return HashPrefixed(RetentionMutationJcs.Canonicalize(PreviewDigestObject(input)), "sha256-");
    }

    private static object PreviewDigestObject(RetentionPreviewDigestInput input) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["schema_version"] = input.SchemaVersion,
        ["result"] = PreviewResult(input.Result),
        ["empty_reason"] = EmptyReason(input.EmptyReason),
        ["mutation_allowed"] = input.MutationAllowed,
        ["target_kind"] = RetentionMutationWire.TargetKind(input.TargetKind),
        ["target_id"] = input.TargetId,
        ["operation"] = RetentionMutationWire.Operation(input.Operation),
        ["scope"] = RetentionMutationWire.Scope(input.Scope),
        ["source_state"] = SourceState(input.SourceState),
        ["session_completeness"] = SessionCompleteness(input.SessionCompleteness),
        ["content_state"] = input.ContentState,
        ["current_state"] = CurrentState(input.CurrentState),
        ["target_items"] = input.TargetItems.Select(static item => PreviewItem(item)).Cast<object?>().ToArray(),
        ["target_item_count"] = input.TargetItemCount,
        ["store_kind_summary"] = input.StoreKindSummary.Select(static item => StoreKindSummary(item)).Cast<object?>().ToArray(),
        ["excluded_item_count"] = input.ExcludedItemCount,
        ["excluded_items_by_reason"] = input.ExcludedItemsByReason.Select(static item => ExclusionSummary(item)).Cast<object?>().ToArray(),
        ["capture_expiry_policy_summary"] = input.CaptureExpiryPolicySummary.Select(static item => CaptureExpiryPolicySummary(item)).Cast<object?>().ToArray(),
        ["retained_metadata_impact"] = RetainedImpact(input.RetainedMetadataImpact),
        ["active_cleanup_exclusion_conflicts"] = input.ActiveCleanupExclusionConflicts.Select(static item => ActiveConflictSummary(item)).Cast<object?>().ToArray(),
        ["backup_non_purge_warning_code"] = input.BackupNonPurgeWarningCode,
        ["rejection_code"] = input.RejectionCode,
        ["expected_state_version"] = input.ExpectedStateVersion,
        ["target_item_set_digest"] = input.TargetItemSetDigest
    };

    private static object CurrentState(RetentionCurrentStateSummary value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["readable_item_count"] = value.ReadableItemCount,
        ["read_denied_item_count"] = value.ReadDeniedItemCount,
        ["pinned_item_count"] = value.PinnedItemCount,
        ["unpinned_item_count"] = value.UnpinnedItemCount,
        ["lifecycle_counts"] = LifecycleCounts(value.LifecycleCounts)
    };

    private static object LifecycleCounts(RetentionMutationLifecycleCounts value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["expiring"] = value.Expiring,
        ["retained_by_policy"] = value.RetainedByPolicy,
        ["expired_pending_deletion"] = value.ExpiredPendingDeletion,
        ["deletion_queued"] = value.DeletionQueued,
        ["deleting"] = value.Deleting,
        ["deleted"] = value.Deleted,
        ["deletion_failed"] = value.DeletionFailed
    };

    private static object PreviewItem(RetentionPreviewItem value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["item_id"] = value.ItemId,
        ["store_kind"] = StoreKind(value.StoreKind),
        ["state"] = Lifecycle(value.State),
        ["pin_state"] = PinState(value.PinState),
        ["delete_state"] = RetentionMutationWire.DeleteState(value.DeleteState),
        ["captured_at"] = value.CapturedAt,
        ["expires_at"] = value.ExpiresAt,
        ["policy_id"] = value.PolicyId,
        ["policy_version"] = value.PolicyVersion,
        ["read_denied_at"] = value.ReadDeniedAt,
        ["queued_at"] = value.QueuedAt,
        ["revision"] = value.Revision,
        ["retry_exhausted"] = value.RetryExhausted,
        ["error_code"] = ErrorCode(value.ErrorCode)
    };

    private static object StoreKindSummary(RetentionStoreKindSummary value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["store_kind"] = StoreKind(value.StoreKind),
        ["item_count"] = value.ItemCount,
        ["readable_count"] = value.ReadableCount,
        ["read_denied_count"] = value.ReadDeniedCount
    };

    private static object ExclusionSummary(RetentionExclusionSummary value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["reason_code"] = value.ReasonCode,
        ["item_count"] = value.ItemCount
    };

    private static object CaptureExpiryPolicySummary(RetentionCaptureExpiryPolicySummary value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["policy_id"] = value.PolicyId,
        ["policy_version"] = value.PolicyVersion,
        ["item_count"] = value.ItemCount,
        ["captured_at_min"] = value.CapturedAtMin,
        ["captured_at_max"] = value.CapturedAtMax,
        ["original_expires_at_min"] = value.OriginalExpiresAtMin,
        ["original_expires_at_max"] = value.OriginalExpiresAtMax
    };

    private static object RetainedImpact(RetentionRetainedImpact value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["raw_content_will_be_deleted"] = value.RawContentWillBeDeleted,
        ["session_metadata_retained"] = value.SessionMetadataRetained,
        ["event_metadata_retained_count"] = value.EventMetadataRetainedCount,
        ["safe_summary_retained_count"] = value.SafeSummaryRetainedCount,
        ["evidence_reference_retained_count"] = value.EvidenceReferenceRetainedCount
    };

    private static object ActiveConflictSummary(RetentionActiveConflictSummary value) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["conflict_code"] = value.ConflictCode,
        ["item_count"] = value.ItemCount,
        ["conflict_version"] = value.ConflictVersion
    };

    private static string PreviewResult(RetentionMutationPreviewResult value) => value switch
    {
        RetentionMutationPreviewResult.Actionable => "actionable",
        RetentionMutationPreviewResult.EmptyNotApplicable => "empty_not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string? EmptyReason(RetentionMutationEmptyReason? value) => value switch
    {
        null => null,
        RetentionMutationEmptyReason.NoExactOwnedItems => "no_exact_owned_items",
        RetentionMutationEmptyReason.AllCandidatesExcluded => "all_candidates_excluded",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string? SourceState(RetentionMutationSourceState? value) => value switch
    {
        null => null,
        RetentionMutationSourceState.Available => "available",
        RetentionMutationSourceState.NotCaptured => "not_captured",
        RetentionMutationSourceState.Redacted => "redacted",
        RetentionMutationSourceState.Unsupported => "unsupported",
        RetentionMutationSourceState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string? SessionCompleteness(RetentionMutationSessionCompleteness? value) => value switch
    {
        null => null,
        RetentionMutationSessionCompleteness.Unbound => "unbound",
        RetentionMutationSessionCompleteness.Partial => "partial",
        RetentionMutationSessionCompleteness.Rich => "rich",
        RetentionMutationSessionCompleteness.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string? ErrorCode(RetentionErrorCode? value) => value switch
    {
        null => null,
        RetentionErrorCode.MigrationBlocked => "retention_migration_blocked",
        RetentionErrorCode.MissingTimestamp => "retention_missing_timestamp",
        RetentionErrorCode.InvalidIdentity => "retention_invalid_identity",
        RetentionErrorCode.OwnershipMismatch => "retention_ownership_mismatch",
        RetentionErrorCode.CaptureIncomplete => "retention_capture_incomplete",
        RetentionErrorCode.LeaseConflict => "retention_lease_conflict",
        RetentionErrorCode.LeaseLost => "retention_lease_lost",
        RetentionErrorCode.DeleteBusy => "retention_delete_busy",
        RetentionErrorCode.DeletePermissionDenied => "retention_delete_permission_denied",
        RetentionErrorCode.DeleteIoFailed => "retention_delete_io_failed",
        RetentionErrorCode.UnexpectedSourceMissing => "retention_unexpected_source_missing",
        RetentionErrorCode.MaintenanceBusy => "retention_maintenance_busy",
        RetentionErrorCode.ItemLimitExceeded => "retention_item_limit_exceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static string StoreKind(RetentionStoreKind kind) => kind switch
    {
        RetentionStoreKind.SessionEventContent => "session_event_content",
        RetentionStoreKind.RawRecord => "raw_record",
        RetentionStoreKind.AnalysisRunRaw => "analysis_run_raw",
        RetentionStoreKind.SensitiveBundle => "sensitive_bundle",
        RetentionStoreKind.AnalysisSdkDirectory => "analysis_sdk_directory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string Lifecycle(RetentionItemLifecycle state) => state switch
    {
        RetentionItemLifecycle.Expiring => "expiring",
        RetentionItemLifecycle.RetainedByPolicy => "retained_by_policy",
        RetentionItemLifecycle.ExpiredPendingDeletion => "expired_pending_deletion",
        RetentionItemLifecycle.DeletionQueued => "deletion_queued",
        RetentionItemLifecycle.Deleting => "deleting",
        RetentionItemLifecycle.Deleted => "deleted",
        RetentionItemLifecycle.DeletionFailed => "deletion_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static string PinState(RetentionPinState state) => state switch
    {
        RetentionPinState.Pinned => "pinned",
        RetentionPinState.Unpinned => "unpinned",
        RetentionPinState.NotApplicable => "not_applicable",
        RetentionPinState.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string HashPrefixed(string canonical, string prefix) => prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

internal static class RetentionMutationJcs
{
    internal static string Canonicalize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                WriteString(builder, text);
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case byte number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case sbyte number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case short number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case ushort number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case uint number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case ulong number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                WriteObject(builder, dictionary);
                return;
            case IEnumerable<object?> sequence:
                WriteArray(builder, sequence);
                return;
            case DateTimeOffset timestamp:
                WriteString(builder, timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;
            default:
                throw new ArgumentException("JCS input contains an unsupported value.", nameof(value));
        }
    }

    private static void WriteObject(StringBuilder builder, IReadOnlyDictionary<string, object?> dictionary)
    {
        builder.Append('{');
        var first = true;
        foreach (var pair in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first) builder.Append(',');
            first = false;
            WriteString(builder, pair.Key);
            builder.Append(':');
            Write(builder, pair.Value);
        }
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, IEnumerable<object?> sequence)
    {
        builder.Append('[');
        var first = true;
        foreach (var item in sequence)
        {
            if (!first) builder.Append(',');
            first = false;
            Write(builder, item);
        }
        builder.Append(']');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        if (value.Any(char.IsSurrogate))
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) { index++; continue; }
                if (char.IsSurrogate(value[index])) throw new ArgumentException("JCS input contains malformed UTF-16.", nameof(value));
            }
        }
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20) builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(rune.ToString());
                    break;
            }
        }
        builder.Append('"');
    }
}

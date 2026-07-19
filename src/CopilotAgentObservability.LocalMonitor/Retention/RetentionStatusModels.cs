using System.Text.Json.Serialization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed record RetentionStatusResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("pending_count")] long? PendingCount,
    [property: JsonPropertyName("queued_count")] long? QueuedCount,
    [property: JsonPropertyName("deleting_count")] long? DeletingCount,
    [property: JsonPropertyName("failed_count")] long? FailedCount,
    [property: JsonPropertyName("retry_exhausted_count")] long? RetryExhaustedCount,
    [property: JsonPropertyName("orphan_or_unexpected_missing_count")] long? OrphanOrUnexpectedMissingCount,
    [property: JsonPropertyName("expired_but_readable_violation_count")] long? ExpiredButReadableViolationCount,
    [property: JsonPropertyName("oldest_pending_age_seconds")] long? OldestPendingAgeSeconds,
    [property: JsonPropertyName("worker_state")] string WorkerState,
    [property: JsonPropertyName("last_successful_run_at")] string? LastSuccessfulRunAt,
    [property: JsonPropertyName("inventory_version")] int InventoryVersion,
    [property: JsonPropertyName("adapter_coverage_version")] int AdapterCoverageVersion,
    [property: JsonPropertyName("items")] IReadOnlyList<RetentionStatusItemResponse> Items);

internal sealed record RetentionStatusItemResponse(
    [property: JsonPropertyName("item_id")] string ItemId,
    [property: JsonPropertyName("store_kind")] string StoreKind,
    [property: JsonPropertyName("inventory_category")] string InventoryCategory,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("policy_version")] int PolicyVersion,
    [property: JsonPropertyName("captured_at")] string? CapturedAt,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("read_denied_at")] string? ReadDeniedAt,
    [property: JsonPropertyName("queued_at")] string? QueuedAt,
    [property: JsonPropertyName("deletion_started_at")] string? DeletionStartedAt,
    [property: JsonPropertyName("deleted_at")] string? DeletedAt,
    [property: JsonPropertyName("attempt_count")] int AttemptCount,
    [property: JsonPropertyName("retry_exhausted")] bool RetryExhausted,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("retry_at")] string? RetryAt);

internal sealed record RetentionSessionStatusResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("raw_retention_state")] string RawRetentionState,
    [property: JsonPropertyName("readable_count")] long ReadableCount,
    [property: JsonPropertyName("read_denied_count")] long ReadDeniedCount,
    [property: JsonPropertyName("lifecycle_counts")] RetentionLifecycleCounts LifecycleCounts);

internal sealed record RetentionLifecycleCounts(
    [property: JsonPropertyName("expiring")] long Expiring,
    [property: JsonPropertyName("retained_by_policy")] long RetainedByPolicy,
    [property: JsonPropertyName("expired_pending_deletion")] long ExpiredPendingDeletion,
    [property: JsonPropertyName("deletion_queued")] long DeletionQueued,
    [property: JsonPropertyName("deleting")] long Deleting,
    [property: JsonPropertyName("deleted")] long Deleted,
    [property: JsonPropertyName("deletion_failed")] long DeletionFailed);

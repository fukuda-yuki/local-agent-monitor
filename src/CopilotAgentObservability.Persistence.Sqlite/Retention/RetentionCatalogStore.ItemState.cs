using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionItemStateResponse? ReadMutationItemState(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;

        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.item_id,i.store_kind,i.state,i.policy_id,i.policy_version,i.captured_at,i.expires_at,
                   i.read_denied_at,i.queued_at,i.deletion_started_at,i.deleted_at,i.attempt_count,
                   i.retry_exhausted,i.error_code,i.next_retry_at,i.revision,
                   (SELECT e.session_id FROM session_events e WHERE i.store_kind='session_event_content' AND e.event_id=i.source_item_id)
            FROM retention_items i
            WHERE i.item_id=$item_id;
            """;
        command.Parameters.AddWithValue("$item_id", itemId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return null;
        }

        var response = new RetentionItemStateResponse(
            RetentionMutationConstants.SchemaVersion,
            reader.GetString(0),
            ParseItemStateStoreKind(reader.GetString(1)),
            ParseItemStateLifecycle(reader.GetString(2)),
            RetentionMutationStateProjection.PinState(ParseItemStateLifecycle(reader.GetString(2))),
            RetentionMutationStateProjection.DeleteState(ParseItemStateLifecycle(reader.GetString(2))),
            reader.GetString(3),
            reader.GetInt32(4),
            ParseItemStateTimestamp(reader.GetString(5)),
            ParseItemStateTimestamp(reader.GetString(6)),
            NullableItemStateTimestamp(reader, 7),
            NullableItemStateTimestamp(reader, 8),
            NullableItemStateTimestamp(reader, 9),
            NullableItemStateTimestamp(reader, 10),
            reader.GetInt32(11),
            reader.GetInt64(12) != 0,
            reader.IsDBNull(13) ? null : ParseItemStateErrorCode(reader.GetString(13)),
            NullableItemStateTimestamp(reader, 14),
            reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
        transaction.Commit();
        return response;
    }

    private static RetentionStoreKind ParseItemStateStoreKind(string value) => value switch
    {
        "session_event_content" => RetentionStoreKind.SessionEventContent,
        "raw_record" => RetentionStoreKind.RawRecord,
        "analysis_run_raw" => RetentionStoreKind.AnalysisRunRaw,
        "sensitive_bundle" => RetentionStoreKind.SensitiveBundle,
        "analysis_sdk_directory" => RetentionStoreKind.AnalysisSdkDirectory,
        _ => throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable)
    };

    private static RetentionItemLifecycle ParseItemStateLifecycle(string value) => value switch
    {
        "expiring" => RetentionItemLifecycle.Expiring,
        "retained_by_policy" => RetentionItemLifecycle.RetainedByPolicy,
        "expired_pending_deletion" => RetentionItemLifecycle.ExpiredPendingDeletion,
        "deletion_queued" => RetentionItemLifecycle.DeletionQueued,
        "deleting" => RetentionItemLifecycle.Deleting,
        "deleted" => RetentionItemLifecycle.Deleted,
        "deletion_failed" => RetentionItemLifecycle.DeletionFailed,
        _ => throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable)
    };

    private static RetentionErrorCode ParseItemStateErrorCode(string value) => value switch
    {
        "retention_migration_blocked" => RetentionErrorCode.MigrationBlocked,
        "retention_missing_timestamp" => RetentionErrorCode.MissingTimestamp,
        "retention_invalid_identity" => RetentionErrorCode.InvalidIdentity,
        "retention_ownership_mismatch" => RetentionErrorCode.OwnershipMismatch,
        "retention_capture_incomplete" => RetentionErrorCode.CaptureIncomplete,
        "retention_lease_conflict" => RetentionErrorCode.LeaseConflict,
        "retention_lease_lost" => RetentionErrorCode.LeaseLost,
        "retention_delete_busy" => RetentionErrorCode.DeleteBusy,
        "retention_delete_permission_denied" => RetentionErrorCode.DeletePermissionDenied,
        "retention_delete_io_failed" => RetentionErrorCode.DeleteIoFailed,
        "retention_unexpected_source_missing" => RetentionErrorCode.UnexpectedSourceMissing,
        "retention_maintenance_busy" => RetentionErrorCode.MaintenanceBusy,
        "retention_item_limit_exceeded" => RetentionErrorCode.ItemLimitExceeded,
        _ => throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable)
    };

    private static DateTimeOffset ParseItemStateTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? NullableItemStateTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseItemStateTimestamp(reader.GetString(ordinal));
}

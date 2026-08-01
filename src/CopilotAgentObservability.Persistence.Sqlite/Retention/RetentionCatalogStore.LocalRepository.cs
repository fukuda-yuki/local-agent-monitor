using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum LocalRepositoryRetentionFact { Expired, Unknown, Busy, Corrupt }

public sealed partial class RetentionCatalogStore
{
    internal static bool ValidateLocalRepositoryOperationLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at) =>
        ValidateSourceCompatibilityOperationLease(connection, transaction, grant, rawRecordId, at);

    internal static LocalRepositoryRetentionFact LocalRepositoryAvailabilityFact(RetentionCatalogContext context, long rawRecordId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (rawRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        try
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(context.DatabasePath, SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT item.state,item.error_code,item.read_denied_at,item.expires_at,item.deleted_at,
                       tombstone.receipt_at,tombstone.deleted_at
                FROM retention_items AS item
                LEFT JOIN retention_tombstones AS tombstone ON tombstone.item_id=item.item_id
                WHERE item.store_instance_id=$store_instance_id
                  AND item.store_kind='raw_record'
                  AND item.source_item_id=$source_item_id;
                """;
            command.Parameters.AddWithValue("$store_instance_id", context.StoreInstanceId);
            command.Parameters.AddWithValue("$source_item_id", rawRecordId.ToString(CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return LocalRepositoryRetentionFact.Unknown;
            if (!TryRequiredText(reader, 0, out var state)
                || !TryNullableText(reader, 1, out var error)
                || !TryNullableText(reader, 2, out var readDeniedText)
                || !TryRequiredText(reader, 3, out var expiresText)
                || !TryNullableText(reader, 4, out var deletedText)
                || !TryNullableText(reader, 5, out var receiptText)
                || !TryNullableText(reader, 6, out var tombstoneDeletedText)
                || !TryTimestamp(expiresText, out _)
                || !TryNullableTimestamp(readDeniedText, out var readDeniedAt)
                || !TryNullableTimestamp(deletedText, out var deletedAt)
                || !TryNullableTimestamp(receiptText, out var receiptAt)
                || !TryNullableTimestamp(tombstoneDeletedText, out var tombstoneDeletedAt))
                return LocalRepositoryRetentionFact.Corrupt;

            var hasTombstone = receiptAt is not null || tombstoneDeletedAt is not null;
            if (hasTombstone && (receiptAt is null || tombstoneDeletedAt is null || receiptAt != tombstoneDeletedAt))
                return LocalRepositoryRetentionFact.Corrupt;
            var hasReadDenial = readDeniedAt is not null;
            return state switch
            {
                "expiring" or "retained_by_policy" when !hasTombstone && readDeniedAt is null && deletedAt is null && error is null => LocalRepositoryRetentionFact.Unknown,
                "expired_pending_deletion" or "deletion_queued" or "deleting" when !hasTombstone && hasReadDenial && deletedAt is null && error is null => LocalRepositoryRetentionFact.Expired,
                "deleted" when hasTombstone && hasReadDenial && deletedAt is not null && deletedAt >= readDeniedAt && deletedAt == tombstoneDeletedAt && error is null => LocalRepositoryRetentionFact.Expired,
                "deletion_failed" when !hasTombstone && hasReadDenial && deletedAt is null && error is "retention_delete_busy" or "retention_delete_permission_denied" or "retention_delete_io_failed" => LocalRepositoryRetentionFact.Expired,
                "deletion_failed" when !hasTombstone && readDeniedAt is not null && deletedAt is null && error is "retention_unexpected_source_missing" or "retention_invalid_identity" or "retention_ownership_mismatch" => LocalRepositoryRetentionFact.Unknown,
                _ => LocalRepositoryRetentionFact.Corrupt,
            };
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return LocalRepositoryRetentionFact.Busy;
        }
        catch (SqliteException)
        {
            return LocalRepositoryRetentionFact.Corrupt;
        }
    }

    private static bool TryRequiredText(SqliteDataReader reader, int ordinal, out string value)
    {
        value = string.Empty;
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not string text) return false;
        value = text;
        return true;
    }

    private static bool TryNullableText(SqliteDataReader reader, int ordinal, out string? value)
    {
        value = null;
        if (reader.IsDBNull(ordinal)) return true;
        if (reader.GetValue(ordinal) is not string text) return false;
        value = text;
        return true;
    }

    private static bool TryNullableTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (value is null) return true;
        if (!TryTimestamp(value, out var parsed)) return false;
        timestamp = parsed;
        return true;
    }

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Offset == TimeSpan.Zero
            && string.Equals(value, parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && (timestamp = parsed) == parsed;
    }
}

using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal static bool ValidateSourceCompatibilityOperationLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grant);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM retention_items AS item
            JOIN retention_leases AS lease
              ON lease.item_id=item.item_id
             AND lease.lease_kind='operation'
            JOIN raw_records AS raw
              ON raw.id=$raw_record_id
             AND raw.retention_owner_token=$retention_read_source_token
            WHERE item.item_id=$retention_read_item_id
              AND item.store_kind='raw_record'
              AND item.source_item_id=CAST($raw_record_id AS TEXT)
              AND item.revision=$retention_read_revision
              AND item.read_denied_at IS NULL
              AND item.state IN ('expiring','retained_by_policy')
              AND item.expires_at>$at
              AND lease.owner=$retention_read_lease_owner
              AND lease.generation=$retention_read_lease_generation
              AND lease.expires_at=$retention_read_lease_expires_at
              AND lease.expires_at>$at;
            """;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue(
            "$at",
            at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        grant.BindSelectorCapability(command);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    internal static bool ValidateSkillProjectionOperationLeases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> rawRecordIds,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(rawRecordIds);
        if (grants.Count != rawRecordIds.Count)
            return false;
        for (var index = 0; index < grants.Count; index++)
        {
            if (!ValidateSourceCompatibilityOperationLease(
                    connection,
                    transaction,
                    grants[index],
                    rawRecordIds[index],
                    at))
                return false;
        }
        return true;
    }
}

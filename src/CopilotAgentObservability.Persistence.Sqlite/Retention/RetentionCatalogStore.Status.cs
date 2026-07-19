using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionStatusItemSnapshot(
    string ItemId, string StoreKind, string InventoryCategory, string State, string PolicyId, int PolicyVersion,
    DateTimeOffset? CapturedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? ReadDeniedAt, DateTimeOffset? QueuedAt,
    DateTimeOffset? DeletionStartedAt, DateTimeOffset? DeletedAt, int AttemptCount, bool RetryExhausted,
    string? ErrorCode, DateTimeOffset? RetryAt);

public sealed record RetentionStatusSnapshot(
    long? PendingCount, long? QueuedCount, long? DeletingCount, long? FailedCount, long? RetryExhaustedCount,
    long? OrphanOrUnexpectedMissingCount, long? ExpiredButReadableViolationCount, long? OldestPendingAgeSeconds,
    string WorkerState, DateTimeOffset? LastSuccessfulRunAt, IReadOnlyList<RetentionStatusItemSnapshot> Items);

public sealed record RetentionSessionStatusSnapshot(
    bool SessionExists, string RawRetentionState, long ReadableCount, long ReadDeniedCount,
    long ExpiringCount, long RetainedByPolicyCount, long ExpiredPendingDeletionCount, long DeletionQueuedCount,
    long DeletingCount, long DeletedCount, long DeletionFailedCount);

public sealed partial class RetentionCatalogStore
{
    public bool TryReadStatusSnapshot(bool workerEnabled, out RetentionStatusSnapshot? snapshot)
    {
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction();
            var now = timeProvider.GetUtcNow();
            var nowText = Timestamp(now);
            if (!DiagnosticRowsAreSafe(connection, transaction) || !DiagnosticTimestampsAreCanonical(connection, transaction)) throw new InvalidOperationException();
            var aggregate = ReadAggregate(connection, transaction, nowText);
            var items = ReadItems(connection, transaction, null);
            var state = ReadWorkerState(connection, transaction, workerEnabled, nowText, aggregate.RetryExhaustedCount != 0);
            transaction.Commit();
            snapshot = new(
                aggregate.PendingCount, aggregate.QueuedCount, aggregate.DeletingCount, aggregate.FailedCount,
                aggregate.RetryExhaustedCount, aggregate.OrphanOrUnexpectedMissingCount, aggregate.ExpiredButReadableViolationCount,
                OldestPendingAge(aggregate.OldestPendingExpiry, now), state.WorkerState, state.LastSuccessfulRunAt, items);
            return true;
        }
        catch (SqliteException) { snapshot = null; return false; }
        catch (InvalidOperationException) { snapshot = null; return false; }
        catch (FormatException) { snapshot = null; return false; }
        catch (OverflowException) { snapshot = null; return false; }
    }

    public bool TryReadSessionStatusSnapshot(string sessionId, out RetentionSessionStatusSnapshot? snapshot)
    {
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction();
            if (!Exists(connection, transaction, "SELECT 1 FROM sessions WHERE session_id=$session", ("$session", (object?)sessionId)))
            {
                transaction.Commit(); snapshot = new(false, "not_captured", 0, 0, 0, 0, 0, 0, 0, 0, 0); return true;
            }

            var aggregate = ReadSessionAggregate(connection, transaction, sessionId, Timestamp(timeProvider.GetUtcNow()));
            var rawState = aggregate.DistinctLifecycleCount == 0 ? "not_captured" : aggregate.DistinctLifecycleCount == 1 ? aggregate.SingleLifecycle! : "mixed";
            transaction.Commit();
            snapshot = new(true, rawState, aggregate.ReadableCount, aggregate.TotalCount - aggregate.ReadableCount,
                aggregate.ExpiringCount, aggregate.RetainedByPolicyCount, aggregate.ExpiredPendingDeletionCount,
                aggregate.DeletionQueuedCount, aggregate.DeletingCount, aggregate.DeletedCount, aggregate.DeletionFailedCount);
            return true;
        }
        catch (SqliteException) { snapshot = null; return false; }
        catch (InvalidOperationException) { snapshot = null; return false; }
        catch (FormatException) { snapshot = null; return false; }
        catch (OverflowException) { snapshot = null; return false; }
    }

    private static List<RetentionStatusItemSnapshot> ReadItems(SqliteConnection connection, SqliteTransaction transaction, string? sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sessionId is null
            ? "SELECT item_id,store_kind,state,policy_id,policy_version,captured_at,expires_at,read_denied_at,queued_at,deletion_started_at,deleted_at,attempt_count,retry_exhausted,error_code,next_retry_at FROM retention_items ORDER BY expires_at,item_id LIMIT 100;"
            : "SELECT i.item_id,i.store_kind,i.state,i.policy_id,i.policy_version,i.captured_at,i.expires_at,i.read_denied_at,i.queued_at,i.deletion_started_at,i.deleted_at,i.attempt_count,i.retry_exhausted,i.error_code,i.next_retry_at FROM retention_items i JOIN session_events e ON e.event_id=i.source_item_id WHERE i.store_kind='session_event_content' AND e.session_id=$session ORDER BY i.expires_at,i.item_id LIMIT 100;";
        if (sessionId is not null) command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader(); var items = new List<RetentionStatusItemSnapshot>();
        while (reader.Read())
        {
            var kind = reader.GetString(1);
            var state = reader.GetString(2);
            items.Add(new(reader.GetString(0), kind, "required_cleanup", state, reader.GetString(3), reader.GetInt32(4),
                At(reader, 5), At(reader, 6), At(reader, 7), At(reader, 8), At(reader, 9), At(reader, 10), reader.GetInt32(11), reader.GetInt64(12) != 0,
                reader.IsDBNull(13) ? null : reader.GetString(13), At(reader, 14)));
        }
        return items;
    }

    private static (string WorkerState, DateTimeOffset? LastSuccessfulRunAt) ReadWorkerState(SqliteConnection connection, SqliteTransaction transaction, bool workerEnabled, string now, bool exhausted)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT last_successful_run_at,worker_error_code,maintenance_error_code FROM retention_worker_state WHERE id=1;";
        using var reader = command.ExecuteReader(); if (!reader.Read()) return ("unknown", null);
        var last = At(reader, 0); var blocked = !reader.IsDBNull(1) || !reader.IsDBNull(2);
        if (!workerEnabled) return ("disabled", last);
        if (blocked || exhausted) return ("degraded", last);
        using var lease = connection.CreateCommand(); lease.Transaction = transaction;
        lease.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id WHERE l.lease_kind='deletion' AND l.expires_at>$now AND i.state='deleting' AND (NOT EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=i.item_id) OR EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=i.item_id AND j.expected_revision=i.revision)));"; lease.Parameters.AddWithValue("$now", now);
        return (Convert.ToInt64(lease.ExecuteScalar(), CultureInfo.InvariantCulture) != 0 ? "running" : "idle", last);
    }

    private static RetentionStatusAggregate ReadAggregate(SqliteConnection connection, SqliteTransaction transaction, string now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(state='expired_pending_deletion'),0),COALESCE(SUM(state='deletion_queued'),0),COALESCE(SUM(state='deleting'),0),COALESCE(SUM(state='deletion_failed'),0),COALESCE(SUM(state='deletion_failed' AND retry_exhausted=1),0),COALESCE(SUM(error_code='retention_unexpected_source_missing'),0),COALESCE(SUM(state<>'retained_by_policy' AND expires_at<=$now AND (read_denied_at IS NULL OR state='expiring')),0),MIN(CASE WHEN state IN ('expired_pending_deletion','deletion_queued','deleting','deletion_failed') THEN expires_at END) FROM retention_items;";
        command.Parameters.AddWithValue("$now", now); using var reader = command.ExecuteReader(); reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : At(reader, 7));
    }
    private static bool DiagnosticRowsAreSafe(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM retention_items WHERE store_kind NOT IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory') OR state NOT IN ('expiring','retained_by_policy','expired_pending_deletion','deletion_queued','deleting','deleted','deletion_failed') OR (store_kind='sensitive_bundle' AND policy_id<>'sensitive-bundle-7d') OR (store_kind<>'sensitive_bundle' AND policy_id<>'raw-default-90d') OR policy_version<>1 OR attempt_count NOT BETWEEN 0 AND 5 OR retry_exhausted NOT IN (0,1) OR (error_code IS NOT NULL AND error_code NOT IN ('retention_delete_busy','retention_delete_permission_denied','retention_delete_io_failed','retention_invalid_identity','retention_ownership_mismatch','retention_unexpected_source_missing','retention_item_limit_exceeded','retention_lease_lost')) OR NOT ((length(item_id)=32 AND item_id NOT GLOB '*[^0-9a-f]*') OR (length(item_id)=13 AND item_id GLOB 'ret-item-[0-9][0-9][0-9][0-9]')));";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }
    private static bool DiagnosticTimestampsAreCanonical(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT captured_at,expires_at,read_denied_at,queued_at,deletion_started_at,deleted_at,next_retry_at FROM retention_items;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) for (var ordinal = 0; ordinal < 7; ordinal++) if (!reader.IsDBNull(ordinal) && !IsCanonicalTimestamp(reader.GetString(ordinal))) return false;
        }
        using var lease = connection.CreateCommand(); lease.Transaction = transaction;
        lease.CommandText = "SELECT expires_at FROM retention_leases WHERE lease_kind='deletion';";
        using var leases = lease.ExecuteReader(); while (leases.Read()) if (!IsCanonicalTimestamp(leases.GetString(0))) return false;
        return true;
    }
    private static bool IsCanonicalTimestamp(string value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        && string.Equals(value, Timestamp(parsed), StringComparison.Ordinal);
    private static RetentionSessionAggregate ReadSessionAggregate(SqliteConnection connection, SqliteTransaction transaction, string sessionId, string now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(i.read_denied_at IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now))),0),COALESCE(SUM(i.state='expiring'),0),COALESCE(SUM(i.state='retained_by_policy'),0),COALESCE(SUM(i.state='expired_pending_deletion'),0),COALESCE(SUM(i.state='deletion_queued'),0),COALESCE(SUM(i.state='deleting'),0),COALESCE(SUM(i.state='deleted'),0),COALESCE(SUM(i.state='deletion_failed'),0),COUNT(DISTINCT i.state),MIN(i.state) FROM retention_items i JOIN session_events e ON e.event_id=i.source_item_id WHERE i.store_kind='session_event_content' AND e.session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$now", now); using var reader = command.ExecuteReader(); reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetString(10));
    }
    private static long? OldestPendingAge(DateTimeOffset? expiry, DateTimeOffset now)
    {
        if (expiry is null) return null;
        var seconds = Math.Floor((now - expiry.Value).TotalSeconds); return seconds <= 0 ? 0 : seconds >= long.MaxValue ? long.MaxValue : (long)seconds;
    }
    private static DateTimeOffset? At(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar() is not null;
    }
    private sealed record RetentionStatusAggregate(long PendingCount, long QueuedCount, long DeletingCount, long FailedCount, long RetryExhaustedCount, long OrphanOrUnexpectedMissingCount, long ExpiredButReadableViolationCount, DateTimeOffset? OldestPendingExpiry);
    private sealed record RetentionSessionAggregate(long TotalCount, long ReadableCount, long ExpiringCount, long RetainedByPolicyCount, long ExpiredPendingDeletionCount, long DeletionQueuedCount, long DeletingCount, long DeletedCount, long DeletionFailedCount, long DistinctLifecycleCount, string? SingleLifecycle);
}

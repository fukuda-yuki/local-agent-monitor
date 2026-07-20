using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionAuditHistoryReadResult ReadAuditHistoryPage(
        RetentionAuditReadTarget target,
        int limit,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        if (!HistoryTargetExists(connection, transaction, target))
        {
            transaction.Commit();
            return new(RetentionAuditHistoryReadDisposition.TargetNotFound, [], null);
        }

        string? cursorOccurredAt = null;
        string? cursorEventId = null;
        if (cursor is not null)
        {
            if (!RetentionMutationIdentifiers.TryParseHistoryCursor(cursor, out var cursorNonce))
            {
                transaction.Commit();
                return new(RetentionAuditHistoryReadDisposition.CursorInvalid, [], null);
            }

            cursorEventId = RetentionMutationIdentifiers.CreateAuditEventId(cursorNonce);
            cursorOccurredAt = ReadHistoryCursorTimestamp(connection, transaction, target, cursorEventId);
            if (cursorOccurredAt is null)
            {
                transaction.Commit();
                return new(RetentionAuditHistoryReadDisposition.CursorInvalid, [], null);
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = cursorEventId is null
            ? """
                SELECT event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,
                       operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,
                       new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,
                       completion_code,error_code
                FROM retention_audit_events
                WHERE target_kind=$target_kind AND target_id=$target_id
                ORDER BY occurred_at DESC, event_id DESC
                LIMIT $limit;
                """
            : """
                SELECT event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,
                       operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,
                       new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,
                       completion_code,error_code
                FROM retention_audit_events
                WHERE target_kind=$target_kind AND target_id=$target_id
                  AND (occurred_at<$cursor_occurred_at OR (occurred_at=$cursor_occurred_at AND event_id<$cursor_event_id))
                ORDER BY occurred_at DESC, event_id DESC
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(target.TargetKind));
        command.Parameters.AddWithValue("$target_id", target.TargetId);
        command.Parameters.AddWithValue("$limit", limit + 1);
        if (cursorEventId is not null)
        {
            command.Parameters.AddWithValue("$cursor_occurred_at", cursorOccurredAt!);
            command.Parameters.AddWithValue("$cursor_event_id", cursorEventId);
        }

        using var reader = command.ExecuteReader();
        var events = new List<RetentionAuditEvent>(limit + 1);
        while (reader.Read())
        {
            try
            {
                events.Add(ValidateAuditEvent(ReadAuditEvent(reader)));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable, exception);
            }
        }

        var hasNextPage = events.Count > limit;
        if (hasNextPage) events.RemoveAt(events.Count - 1);
        var nextCursor = hasNextPage ? HistoryCursor(events[^1].EventId) : null;
        transaction.Commit();
        return new(RetentionAuditHistoryReadDisposition.Found, events, nextCursor);
    }

    private static bool HistoryTargetExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionAuditReadTarget target)
    {
        if (!Enum.IsDefined(target.TargetKind) || string.IsNullOrWhiteSpace(target.TargetId)) return false;
        if (target.TargetKind == RetentionMutationTargetKind.Session
            && !RetentionMutationTargetValidator.Validate(new(target.TargetKind, target.TargetId)).IsValid)
            return false;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = target.TargetKind == RetentionMutationTargetKind.Session
            ? "SELECT 1 FROM sessions WHERE session_id=$target_id;"
            : "SELECT 1 FROM retention_items WHERE item_id=$target_id;";
        command.Parameters.AddWithValue("$target_id", target.TargetId);
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadHistoryCursorTimestamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionAuditReadTarget target,
        string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT occurred_at FROM retention_audit_events WHERE target_kind=$target_kind AND target_id=$target_id AND event_id=$event_id;";
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(target.TargetKind));
        command.Parameters.AddWithValue("$target_id", target.TargetId);
        command.Parameters.AddWithValue("$event_id", eventId);
        return command.ExecuteScalar() as string;
    }

    private static string HistoryCursor(string eventId)
    {
        if (!RetentionMutationIdentifiers.TryParseAuditEventId(eventId, out var nonce))
            throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable);
        return RetentionMutationIdentifiers.CreateHistoryCursor(nonce);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionIdempotencyOutcome GetOrCreateIdempotency(RetentionIdempotencyRequest request)
    {
        ValidateIdempotencyRequest(request);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        var outcome = GetOrCreateIdempotencyWithinTransaction(connection, transaction, request);
        transaction.Commit();
        return outcome;
    }

    internal RetentionIdempotencyOutcome GetOrCreateIdempotencyWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionIdempotencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateIdempotencyRequest(request);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var keyDigest = SHA256.HashData(Encoding.ASCII.GetBytes(request.WorkflowKey));
        var step = IdempotencyStep(request.Step);
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(request.CanonicalRequest));
        var existing = ReadIdempotency(connection, transaction, keyDigest, step);
        var lifetime = ReadIdempotencyLifetime(connection, transaction, keyDigest);

        if (existing is not null)
        {
            if (now >= existing.ExpiresAt)
                return new(RetentionIdempotencyDisposition.Expired, null, null, existing.CreatedAt, existing.ExpiresAt);

            return CryptographicOperations.FixedTimeEquals(fingerprint, existing.RequestFingerprint)
                ? new(RetentionIdempotencyDisposition.Replayed, existing.ResultJson, existing.CompletionCode, existing.CreatedAt, existing.ExpiresAt)
                : new(RetentionIdempotencyDisposition.Conflict, null, null, existing.CreatedAt, existing.ExpiresAt);
        }

        if (lifetime is not null)
        {
            if (now >= lifetime.Value.ExpiresAt)
                return new(RetentionIdempotencyDisposition.Expired, null, null, lifetime.Value.CreatedAt, lifetime.Value.ExpiresAt);

            InsertIdempotency(connection, transaction, keyDigest, step, fingerprint, request, lifetime.Value.CreatedAt, lifetime.Value.ExpiresAt);
            return new(RetentionIdempotencyDisposition.Created, request.ResultJson, request.CompletionCode, lifetime.Value.CreatedAt, lifetime.Value.ExpiresAt);
        }

        var createdAt = now;
        var expiresAt = createdAt.AddDays(RetentionMutationConstants.IdempotencyLifetimeDays);
        InsertIdempotency(connection, transaction, keyDigest, step, fingerprint, request, createdAt, expiresAt);
        return new(RetentionIdempotencyDisposition.Created, request.ResultJson, request.CompletionCode, createdAt, expiresAt);
    }

    internal void AppendAuditEvent(RetentionAuditEvent auditEvent)
    {
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        AppendAuditEventWithinTransaction(connection, transaction, auditEvent);
        transaction.Commit();
    }

    internal RetentionAuditEvent AppendAuditEventWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var canonical = ValidateAuditEvent(auditEvent);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retention_audit_events(
                event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,
                operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,
                new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,
                completion_code,error_code)
            VALUES($event_id,$operation_id,$event_type,$target_kind,$target_id,$session_id,$occurred_at,$actor_label,
                $operation,$reason_code,$comment,$previous_pin_state,$new_pin_state,$previous_operation_state,
                $new_operation_state,$request_idempotency_key,$expected_version,$result_version,$target_item_set_digest,
                $completion_code,$error_code);
            """;
        command.Parameters.AddWithValue("$event_id", canonical.EventId);
        command.Parameters.AddWithValue("$operation_id", canonical.OperationId);
        command.Parameters.AddWithValue("$event_type", canonical.EventType);
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(canonical.TargetKind));
        command.Parameters.AddWithValue("$target_id", canonical.TargetId);
        command.Parameters.AddWithValue("$session_id", (object?)canonical.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$occurred_at", Timestamp(canonical.OccurredAt));
        command.Parameters.AddWithValue("$actor_label", canonical.ActorLabel);
        command.Parameters.AddWithValue("$operation", RetentionMutationWire.Operation(canonical.Operation));
        command.Parameters.AddWithValue("$reason_code", canonical.ReasonCode);
        command.Parameters.AddWithValue("$comment", (object?)canonical.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous_pin_state", RetentionMutationWire.PinState(canonical.PreviousPinState));
        command.Parameters.AddWithValue("$new_pin_state", RetentionMutationWire.PinState(canonical.NewPinState));
        command.Parameters.AddWithValue("$previous_operation_state", LifecycleCountsJson(canonical.PreviousOperationState));
        command.Parameters.AddWithValue("$new_operation_state", LifecycleCountsJson(canonical.NewOperationState));
        command.Parameters.AddWithValue("$request_idempotency_key", canonical.RequestIdempotencyKey);
        command.Parameters.AddWithValue("$expected_version", canonical.ExpectedVersion);
        command.Parameters.AddWithValue("$result_version", canonical.ResultVersion);
        command.Parameters.AddWithValue("$target_item_set_digest", canonical.TargetItemSetDigest);
        command.Parameters.AddWithValue("$completion_code", canonical.CompletionCode);
        command.Parameters.AddWithValue("$error_code", (object?)canonical.ErrorCode ?? DBNull.Value);
        command.ExecuteNonQuery();
        return canonical;
    }

    internal IReadOnlyList<RetentionAuditEvent> ReadAuditEvents(RetentionMutationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!RetentionMutationTargetValidator.Validate(target).IsValid)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(target));

        using var connection = OpenExisting();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,
                   operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,
                   new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,
                   completion_code,error_code
            FROM retention_audit_events
            WHERE target_kind=$target_kind AND target_id=$target_id
            ORDER BY occurred_at DESC, event_id DESC;
            """;
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(target.Kind));
        command.Parameters.AddWithValue("$target_id", target.Id);
        using var reader = command.ExecuteReader();
        var events = new List<RetentionAuditEvent>();
        while (reader.Read()) events.Add(ReadAuditEvent(reader));
        return events;
    }

    private static void ValidateIdempotencyRequest(RetentionIdempotencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RetentionMutationIdentifiers.IsValidWorkflowKey(request.WorkflowKey))
            throw new ArgumentException(RetentionMutationErrorCodes.IdempotencyKeyInvalid, nameof(request));
        if (!Enum.IsDefined(request.Step))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(request));
        if (string.IsNullOrEmpty(request.CanonicalRequest) || !IsJson(request.CanonicalRequest))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(request));
        ValidateStoredPayload(request.ResultJson);
        if (request.CompletionCode is not null && !RetentionMutationCompletionCodes.All.Contains(request.CompletionCode, StringComparer.Ordinal))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(request));
    }

    private static void ValidateStoredPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || !IsJson(payload))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(payload));
        if (payload.Contains(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal)
            || payload.Contains('/')
            || payload.Contains('\\')
            || payload.Contains("password", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("bearer", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("api_key", StringComparison.Ordinal)
            || payload.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("stacktrace", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(payload));
    }

    private static bool IsJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is not JsonValueKind.Undefined;
        }
        catch (JsonException) { return false; }
    }

    private static string IdempotencyStep(RetentionMutationOperationStep step) => step switch
    {
        RetentionMutationOperationStep.Preview => "preview",
        RetentionMutationOperationStep.ConfirmationIssue => "confirmation_issue",
        RetentionMutationOperationStep.Mutation => "mutation",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    private static void InsertIdempotency(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] keyDigest,
        string step,
        byte[] fingerprint,
        RetentionIdempotencyRequest request,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO retention_mutation_idempotency(key_digest,step,request_fingerprint,result_json,completion_code,created_at,expires_at) VALUES($key_digest,$step,$fingerprint,$result_json,$completion_code,$created_at,$expires_at);";
        command.Parameters.AddWithValue("$key_digest", keyDigest);
        command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$result_json", request.ResultJson);
        command.Parameters.AddWithValue("$completion_code", (object?)request.CompletionCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", Timestamp(createdAt));
        command.Parameters.AddWithValue("$expires_at", Timestamp(expiresAt));
        command.ExecuteNonQuery();
    }

    private static IdempotencyRow? ReadIdempotency(SqliteConnection connection, SqliteTransaction transaction, byte[] keyDigest, string step)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_fingerprint,result_json,completion_code,created_at,expires_at FROM retention_mutation_idempotency WHERE key_digest=$key_digest AND step=$step;";
        command.Parameters.AddWithValue("$key_digest", keyDigest);
        command.Parameters.AddWithValue("$step", step);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetFieldValue<byte[]>(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)));
    }

    private static (DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)? ReadIdempotencyLifetime(SqliteConnection connection, SqliteTransaction transaction, byte[] keyDigest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT created_at,expires_at FROM retention_mutation_idempotency WHERE key_digest=$key_digest ORDER BY created_at ASC,step ASC LIMIT 1;";
        command.Parameters.AddWithValue("$key_digest", keyDigest);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (ParseTimestamp(reader.GetString(0)), ParseTimestamp(reader.GetString(1)))
            : null;
    }

    private static RetentionAuditEvent ValidateAuditEvent(RetentionAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!RetentionMutationIdentifiers.TryParseAuditEventId(auditEvent.EventId, out _)
            || string.IsNullOrWhiteSpace(auditEvent.OperationId)
            || auditEvent.OperationId.Any(char.IsControl)
            || auditEvent.OperationId.Contains('/')
            || auditEvent.OperationId.Contains('\\')
            || auditEvent.EventType != RetentionMutationConstants.EventType
            || !Enum.IsDefined(auditEvent.TargetKind)
            || !Enum.IsDefined(auditEvent.Operation)
            || auditEvent.OccurredAt.Offset != TimeSpan.Zero
            || auditEvent.ActorLabel != RetentionMutationConstants.ActorLabel
            || !RetentionMutationReasonCodes.All.Contains(auditEvent.ReasonCode, StringComparer.Ordinal)
            || !RetentionMutationCompletionCodes.All.Contains(auditEvent.CompletionCode, StringComparer.Ordinal)
            || !RetentionMutationIdentifiers.IsValidWorkflowKey(auditEvent.RequestIdempotencyKey)
            || (auditEvent.ErrorCode is not null && !IsKnownError(auditEvent.ErrorCode)))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        if (!RetentionMutationTargetValidator.Validate(new(auditEvent.TargetKind, auditEvent.TargetId)).IsValid)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        if (auditEvent.TargetKind == RetentionMutationTargetKind.Session
            && !string.Equals(auditEvent.SessionId, auditEvent.TargetId, StringComparison.Ordinal))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        if (auditEvent.SessionId is not null && !RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Session, auditEvent.SessionId)).IsValid)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        if (!IsDigest(auditEvent.ExpectedVersion, "v1-")
            || !IsDigest(auditEvent.ResultVersion, "v1-")
            || !IsDigest(auditEvent.TargetItemSetDigest, "sha256-"))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        ValidateCounts(auditEvent.PreviousOperationState);
        ValidateCounts(auditEvent.NewOperationState);
        var comment = RetentionMutationCommentValidator.Validate(auditEvent.Comment);
        if (!comment.IsValid)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(auditEvent));
        return comment.NormalizedComment == auditEvent.Comment ? auditEvent : auditEvent with { Comment = comment.NormalizedComment };
    }

    private static bool IsKnownError(string code) => RetentionMutationErrorCodeRegistry.All.Any(entry => entry.Code == code && entry.Reachability != RetentionMutationReachabilityClass.Warning);

    private static bool IsDigest(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length == prefix.Length + 64
        && value[prefix.Length..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateCounts(RetentionMutationLifecycleCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (RetentionMutationLifecycleStates.All.Select(state => state switch
            {
                RetentionItemLifecycle.Expiring => counts.Expiring,
                RetentionItemLifecycle.RetainedByPolicy => counts.RetainedByPolicy,
                RetentionItemLifecycle.ExpiredPendingDeletion => counts.ExpiredPendingDeletion,
                RetentionItemLifecycle.DeletionQueued => counts.DeletionQueued,
                RetentionItemLifecycle.Deleting => counts.Deleting,
                RetentionItemLifecycle.Deleted => counts.Deleted,
                RetentionItemLifecycle.DeletionFailed => counts.DeletionFailed,
                _ => throw new ArgumentOutOfRangeException()
            }).Any(static value => value < 0))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(counts));
    }

    private static string LifecycleCountsJson(RetentionMutationLifecycleCounts counts) => RetentionMutationJcs.Canonicalize(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["expiring"] = counts.Expiring,
        ["retained_by_policy"] = counts.RetainedByPolicy,
        ["expired_pending_deletion"] = counts.ExpiredPendingDeletion,
        ["deletion_queued"] = counts.DeletionQueued,
        ["deleting"] = counts.Deleting,
        ["deleted"] = counts.Deleted,
        ["deletion_failed"] = counts.DeletionFailed
    });

    private static RetentionAuditEvent ReadAuditEvent(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ParseTargetKind(reader.GetString(3)),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        ParseTimestamp(reader.GetString(6)),
        reader.GetString(7),
        ParseOperation(reader.GetString(8)),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParsePinState(reader.GetString(11)),
        ParsePinState(reader.GetString(12)),
        ParseCounts(reader.GetString(13)),
        ParseCounts(reader.GetString(14)),
        reader.GetString(15),
        reader.GetString(16),
        reader.GetString(17),
        reader.GetString(18),
        reader.GetString(19),
        reader.IsDBNull(20) ? null : reader.GetString(20));

    private static RetentionMutationLifecycleCounts ParseCounts(string value)
    {
        using var document = JsonDocument.Parse(value);
        var properties = document.RootElement.EnumerateObject().ToArray();
        var expected = new[] { "expiring", "retained_by_policy", "expired_pending_deletion", "deletion_queued", "deleting", "deleted", "deletion_failed" };
        if (document.RootElement.ValueKind != JsonValueKind.Object || properties.Length != expected.Length || properties.Select(static property => property.Name).Except(expected, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("retention_audit_state_invalid");
        return new(
            document.RootElement.GetProperty("expiring").GetInt32(),
            document.RootElement.GetProperty("retained_by_policy").GetInt32(),
            document.RootElement.GetProperty("expired_pending_deletion").GetInt32(),
            document.RootElement.GetProperty("deletion_queued").GetInt32(),
            document.RootElement.GetProperty("deleting").GetInt32(),
            document.RootElement.GetProperty("deleted").GetInt32(),
            document.RootElement.GetProperty("deletion_failed").GetInt32());
    }

    private static RetentionMutationTargetKind ParseTargetKind(string value) => value switch
    {
        "session" => RetentionMutationTargetKind.Session,
        "item" => RetentionMutationTargetKind.Item,
        _ => throw new InvalidOperationException("retention_audit_target_invalid")
    };

    private static RetentionMutationOperation ParseOperation(string value) => value switch
    {
        "pin" => RetentionMutationOperation.Pin,
        "unpin" => RetentionMutationOperation.Unpin,
        "delete_now" => RetentionMutationOperation.DeleteNow,
        _ => throw new InvalidOperationException("retention_audit_operation_invalid")
    };

    private static RetentionPinState ParsePinState(string value) => value switch
    {
        "pinned" => RetentionPinState.Pinned,
        "unpinned" => RetentionPinState.Unpinned,
        "not_applicable" => RetentionPinState.NotApplicable,
        "mixed" => RetentionPinState.Mixed,
        _ => throw new InvalidOperationException("retention_audit_pin_state_invalid")
    };

    private sealed record IdempotencyRow(byte[] RequestFingerprint, string ResultJson, string? CompletionCode, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}

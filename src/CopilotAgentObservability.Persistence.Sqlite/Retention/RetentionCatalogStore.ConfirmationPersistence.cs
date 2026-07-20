using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionConfirmationPersistenceResult StoreConfirmationBinding(RetentionConfirmationBindingRequest request)
    {
        ValidateConfirmationBindingRequest(request);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = StoreConfirmationBindingWithinTransaction(connection, transaction, request);
        if (result.Disposition == RetentionConfirmationBindingPersistenceDisposition.GenerationFailed)
        {
            transaction.Rollback();
            return result;
        }

        transaction.Commit();
        return result;
    }

    internal RetentionConfirmationPersistenceResult StoreConfirmationBindingWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionConfirmationBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateConfirmationBindingRequest(request);

        var consumed = ReadConfirmationBindingForPreview(connection, transaction, request.PreviewId, consumed: true);
        return consumed is not null
            ? new(RetentionConfirmationBindingPersistenceDisposition.Consumed, consumed)
            : InsertConfirmationBindingWithinTransaction(connection, transaction, request);
    }

    internal RetentionConfirmationIssuePersistenceResult IssueConfirmation(
        RetentionIdempotencyRequest idempotencyRequest,
        RetentionConfirmationBindingRequest bindingRequest)
    {
        ValidateIdempotencyRequest(idempotencyRequest);
        ValidateConfirmationBindingRequest(bindingRequest);
        if (idempotencyRequest.Step != RetentionMutationOperationStep.ConfirmationIssue
            || !string.Equals(idempotencyRequest.WorkflowKey, bindingRequest.WorkflowIdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(bindingRequest));

        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = IssueConfirmationWithinTransaction(connection, transaction, idempotencyRequest, bindingRequest);
        transaction.Commit();
        return result;
    }

    internal RetentionConfirmationIssuePersistenceResult IssueConfirmationWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionIdempotencyRequest idempotencyRequest,
        RetentionConfirmationBindingRequest bindingRequest)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateIdempotencyRequest(idempotencyRequest);
        ValidateConfirmationBindingRequest(bindingRequest);
        if (idempotencyRequest.Step != RetentionMutationOperationStep.ConfirmationIssue
            || !string.Equals(idempotencyRequest.WorkflowKey, bindingRequest.WorkflowIdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(bindingRequest));

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var keyDigest = SHA256.HashData(Encoding.ASCII.GetBytes(idempotencyRequest.WorkflowKey));
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyRequest.CanonicalRequest));
        var existing = ReadIdempotency(connection, transaction, keyDigest, "confirmation_issue");
        if (existing is not null)
        {
            if (now >= existing.ExpiresAt)
                return new(RetentionConfirmationIssuePersistenceDisposition.Expired, null, null, null, null, existing.CreatedAt, existing.ExpiresAt);
            if (!CryptographicOperations.FixedTimeEquals(fingerprint, existing.RequestFingerprint))
                return new(RetentionConfirmationIssuePersistenceDisposition.Conflict, null, null, null, null, existing.CreatedAt, existing.ExpiresAt);
        }

        var lifetime = ReadIdempotencyLifetime(connection, transaction, keyDigest);
        if (existing is null && lifetime is not null && now >= lifetime.Value.ExpiresAt)
            return new(RetentionConfirmationIssuePersistenceDisposition.Expired, null, null, null, null, lifetime.Value.CreatedAt, lifetime.Value.ExpiresAt);

        var consumed = ReadConfirmationBindingForPreview(connection, transaction, bindingRequest.PreviewId, consumed: true);
        if (consumed is not null)
        {
            var linkage = ReadOperationLinkage(connection, transaction, consumed.OperationId);
            return new(
                RetentionConfirmationIssuePersistenceDisposition.ConsumedLinkage,
                consumed,
                consumed.OperationId,
                linkage.ResultJson ?? existing?.ResultJson,
                linkage.CompletionCode ?? existing?.CompletionCode,
                existing?.CreatedAt ?? lifetime?.CreatedAt,
                existing?.ExpiresAt ?? lifetime?.ExpiresAt);
        }

        var tokenParts = RetentionMutationToken.TryParse(bindingRequest.ConfirmationToken, out var parts) ? parts : null;
        if (tokenParts is null)
            throw new ArgumentException(RetentionMutationErrorCodes.ConfirmationInvalid, nameof(bindingRequest));
        if (NonceExists(connection, transaction, tokenParts.Nonce))
            return new(RetentionConfirmationIssuePersistenceDisposition.GenerationFailed, null, null, null, null, null, null);

        var hadUnconsumed = HasUnconsumedConfirmationBinding(connection, transaction, bindingRequest.PreviewId);
        var stored = InsertConfirmationBindingWithinTransaction(connection, transaction, bindingRequest);
        if (stored.Disposition != RetentionConfirmationBindingPersistenceDisposition.Stored)
            return new(RetentionConfirmationIssuePersistenceDisposition.GenerationFailed, null, null, null, null, null, null);

        var createdAt = existing?.CreatedAt ?? lifetime?.CreatedAt ?? now;
        var expiresAt = existing?.ExpiresAt ?? lifetime?.ExpiresAt ?? createdAt.AddDays(RetentionMutationConstants.IdempotencyLifetimeDays);
        UpsertIdempotency(connection, transaction, keyDigest, "confirmation_issue", fingerprint, idempotencyRequest, createdAt, expiresAt);
        return new(
            hadUnconsumed
                ? RetentionConfirmationIssuePersistenceDisposition.ReissuedAfterInvalidation
                : RetentionConfirmationIssuePersistenceDisposition.IssuedFresh,
            stored.Binding,
            null,
            idempotencyRequest.ResultJson,
            idempotencyRequest.CompletionCode,
            createdAt,
            expiresAt);
    }

    internal RetentionConfirmationValidationResult ValidateConfirmationToken(string presentedToken)
    {
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var result = ValidateConfirmationTokenWithinTransaction(connection, transaction, presentedToken);
        transaction.Commit();
        return result;
    }

    internal RetentionConfirmationValidationResult ValidateConfirmationTokenWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string presentedToken)
    {
        if (!RetentionMutationToken.TryParse(presentedToken, out _))
            return new(RetentionConfirmationValidationDisposition.Invalid, RetentionMutationErrorCodes.ConfirmationInvalid, null);
        var binding = ReadConfirmationBindingByHash(connection, transaction, RetentionMutationToken.HashFullToken(presentedToken));
        if (binding is null || binding.State == RetentionConfirmationBindingState.Invalidated)
            return new(RetentionConfirmationValidationDisposition.Invalid, RetentionMutationErrorCodes.ConfirmationInvalid, binding);
        if (binding.State == RetentionConfirmationBindingState.Consumed)
            return new(RetentionConfirmationValidationDisposition.Consumed, RetentionMutationErrorCodes.ConfirmationConsumed, binding);
        if (timeProvider.GetUtcNow() >= binding.ConfirmationExpiresAt)
            return new(RetentionConfirmationValidationDisposition.Expired, RetentionMutationErrorCodes.ConfirmationExpired, binding);
        return new(RetentionConfirmationValidationDisposition.Active, null, binding);
    }

    internal RetentionConfirmationConsumptionResult ConsumeConfirmation(string presentedToken)
    {
        using var connection = OpenMutationConnection();
        using var transaction = BeginMutationTransaction(connection);
        var result = TryConsumeConfirmationWithinTransaction(connection, transaction, presentedToken);
        transaction.Commit();
        return result;
    }

    internal RetentionConfirmationConsumptionResult TryConsumeConfirmationWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string presentedToken)
    {
        var validation = ValidateConfirmationTokenWithinTransaction(connection, transaction, presentedToken);
        if (validation.Disposition == RetentionConfirmationValidationDisposition.Invalid)
            return new(RetentionConfirmationConsumptionDisposition.Invalid, validation.Code, validation.Binding);
        if (validation.Disposition == RetentionConfirmationValidationDisposition.Consumed)
            return new(RetentionConfirmationConsumptionDisposition.AlreadyConsumed, RetentionMutationErrorCodes.ConfirmationConsumed, validation.Binding);
        if (validation.Disposition == RetentionConfirmationValidationDisposition.Expired)
            return new(RetentionConfirmationConsumptionDisposition.Expired, RetentionMutationErrorCodes.ConfirmationExpired, validation.Binding);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var hash = RetentionMutationToken.HashFullToken(presentedToken);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE retention_confirmation_bindings SET consumed_at=$consumed_at WHERE token_sha256=$token_sha256 AND consumed_at IS NULL AND invalidated_at IS NULL AND confirmation_expires_at>$now;";
        update.Parameters.AddWithValue("$consumed_at", Timestamp(now));
        update.Parameters.AddWithValue("$token_sha256", hash);
        update.Parameters.AddWithValue("$now", Timestamp(now));
        if (update.ExecuteNonQuery() != 1)
        {
            var afterRace = ValidateConfirmationTokenWithinTransaction(connection, transaction, presentedToken);
            return afterRace.Disposition switch
            {
                RetentionConfirmationValidationDisposition.Consumed => new(RetentionConfirmationConsumptionDisposition.AlreadyConsumed, RetentionMutationErrorCodes.ConfirmationConsumed, afterRace.Binding),
                RetentionConfirmationValidationDisposition.Expired => new(RetentionConfirmationConsumptionDisposition.Expired, RetentionMutationErrorCodes.ConfirmationExpired, afterRace.Binding),
                _ => new(RetentionConfirmationConsumptionDisposition.Invalid, RetentionMutationErrorCodes.ConfirmationInvalid, afterRace.Binding)
            };
        }

        var consumed = ReadConfirmationBindingByHash(connection, transaction, hash);
        return new(RetentionConfirmationConsumptionDisposition.Consumed, null, consumed);
    }

    internal SqliteConnection OpenMutationConnection() => OpenExisting();

    internal SqliteTransaction BeginMutationTransaction(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction(deferred: false);
    }

    internal RetentionConfirmationBinding? ReadConfirmationBinding(string confirmationId)
    {
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var binding = ReadConfirmationBinding(connection, transaction, confirmationId);
        transaction.Commit();
        return binding;
    }

    private RetentionConfirmationPersistenceResult InsertConfirmationBindingWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionConfirmationBindingRequest request)
    {
        var previewCreatedAt = ReadPreviewCreatedAt(connection, transaction, request.PreviewId);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var tokenParts = RetentionMutationToken.TryParse(request.ConfirmationToken, out var parts) ? parts : null;
        if (tokenParts is null)
            throw new ArgumentException(RetentionMutationErrorCodes.ConfirmationInvalid, nameof(request));
        var tokenHash = RetentionMutationToken.HashFullToken(request.ConfirmationToken);

        if (NonceExists(connection, transaction, tokenParts.Nonce))
            return new(RetentionConfirmationBindingPersistenceDisposition.GenerationFailed, null);

        using var invalidate = connection.CreateCommand();
        invalidate.Transaction = transaction;
        invalidate.CommandText = "UPDATE retention_confirmation_bindings SET invalidated_at=$invalidated_at WHERE preview_id=$preview_id AND consumed_at IS NULL AND invalidated_at IS NULL;";
        invalidate.Parameters.AddWithValue("$invalidated_at", Timestamp(now));
        invalidate.Parameters.AddWithValue("$preview_id", request.PreviewId);
        invalidate.ExecuteNonQuery();

        var comment = RetentionMutationCommentValidator.Validate(request.Comment);
        var commentHash = request.CommentSha256 ?? (comment.NormalizedComment is null
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(comment.NormalizedComment)));
        var expiresAt = previewCreatedAt.Add(RetentionMutationConstants.ConfirmationLifetime);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO retention_confirmation_bindings(
                confirmation_id,preview_id,schema_version,token_sha256,nonce,target_kind,target_id,operation,scope,
                preview_digest,expected_state_version,target_item_set_digest,active_conflict_snapshot,conflict_version,
                confirmation_expires_at,workflow_idempotency_key,reason_code,comment_sha256,created_at,consumed_at,
                invalidated_at,operation_id)
            VALUES($confirmation_id,$preview_id,1,$token_sha256,$nonce,$target_kind,$target_id,$operation,$scope,
                $preview_digest,$expected_state_version,$target_item_set_digest,$active_conflict_snapshot,$conflict_version,
                $confirmation_expires_at,$workflow_idempotency_key,$reason_code,$comment_sha256,$created_at,NULL,NULL,$operation_id);
            """;
        insert.Parameters.AddWithValue("$confirmation_id", request.ConfirmationId);
        insert.Parameters.AddWithValue("$preview_id", request.PreviewId);
        insert.Parameters.AddWithValue("$token_sha256", tokenHash);
        insert.Parameters.AddWithValue("$nonce", tokenParts.Nonce);
        insert.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(request.Target.Kind));
        insert.Parameters.AddWithValue("$target_id", request.Target.Id);
        insert.Parameters.AddWithValue("$operation", RetentionMutationWire.Operation(request.Operation));
        insert.Parameters.AddWithValue("$scope", RetentionMutationWire.Scope(request.Scope));
        insert.Parameters.AddWithValue("$preview_digest", request.PreviewDigest);
        insert.Parameters.AddWithValue("$expected_state_version", request.ExpectedStateVersion);
        insert.Parameters.AddWithValue("$target_item_set_digest", request.TargetItemSetDigest);
        insert.Parameters.AddWithValue("$active_conflict_snapshot", request.ActiveConflictSnapshot);
        insert.Parameters.AddWithValue("$conflict_version", request.ConflictVersion);
        insert.Parameters.AddWithValue("$confirmation_expires_at", Timestamp(expiresAt));
        insert.Parameters.AddWithValue("$workflow_idempotency_key", request.WorkflowIdempotencyKey);
        insert.Parameters.AddWithValue("$reason_code", request.ReasonCode);
        insert.Parameters.AddWithValue("$comment_sha256", (object?)commentHash ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created_at", Timestamp(now));
        insert.Parameters.AddWithValue("$operation_id", (object?)request.OperationId ?? DBNull.Value);
        insert.ExecuteNonQuery();
        return new(RetentionConfirmationBindingPersistenceDisposition.Stored, ReadConfirmationBinding(connection, transaction, request.ConfirmationId));
    }

    private static RetentionConfirmationBinding? ReadConfirmationBindingForPreview(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string previewId,
        bool consumed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BindingSelect(consumed
            ? "preview_id=$preview_id AND consumed_at IS NOT NULL ORDER BY consumed_at DESC"
            : "preview_id=$preview_id AND consumed_at IS NULL");
        command.Parameters.AddWithValue("$preview_id", previewId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    private static bool HasUnconsumedConfirmationBinding(SqliteConnection connection, SqliteTransaction transaction, string previewId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_confirmation_bindings WHERE preview_id=$preview_id AND consumed_at IS NULL);";
        command.Parameters.AddWithValue("$preview_id", previewId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static (string? ResultJson, string? CompletionCode) ReadOperationLinkage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? operationId)
    {
        if (operationId is null) return (null, null);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT result_json,completion_code FROM retention_operation_receipts WHERE operation_id=$operation_id;";
        command.Parameters.AddWithValue("$operation_id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1))
            : (null, null);
    }

    private static void ValidateConfirmationBindingRequest(RetentionConfirmationBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RetentionMutationIdentifiers.TryParseConfirmationId(request.ConfirmationId, out _)
            || !RetentionMutationIdentifiers.TryParsePreviewId(request.PreviewId, out _)
            || !RetentionMutationToken.TryParse(request.ConfirmationToken, out _)
            || !RetentionMutationTargetValidator.Validate(request.Target).IsValid
            || !Enum.IsDefined(request.Operation)
            || !Enum.IsDefined(request.Scope)
            || (request.Target.Kind == RetentionMutationTargetKind.Item && request.Scope != RetentionMutationScope.SingleItem)
            || (request.Target.Kind == RetentionMutationTargetKind.Session && request.Scope != RetentionMutationScope.SessionItems)
            || !IsConfirmationDigest(request.PreviewDigest, "sha256-")
            || !IsConfirmationDigest(request.ExpectedStateVersion, "v1-")
            || !IsConfirmationDigest(request.TargetItemSetDigest, "sha256-")
            || !IsConfirmationDigest(request.ConflictVersion, "v1-")
            || string.IsNullOrEmpty(request.ActiveConflictSnapshot)
            || !IsConfirmationJson(request.ActiveConflictSnapshot)
            || !RetentionMutationIdentifiers.IsValidWorkflowKey(request.WorkflowIdempotencyKey)
            || !RetentionMutationReasonCodes.All.Contains(request.ReasonCode, StringComparer.Ordinal)
            || !ValidComment(request))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(request));
        ValidateStoredPayload(request.ActiveConflictSnapshot);
    }

    private static bool ValidComment(RetentionConfirmationBindingRequest request) =>
        request.CommentSha256 is { Length: 32 } && request.Comment is null
        || request.CommentSha256 is null && RetentionMutationCommentValidator.Validate(request.Comment).IsValid;

    private static DateTimeOffset ReadPreviewCreatedAt(SqliteConnection connection, SqliteTransaction transaction, string previewId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT created_at FROM retention_mutation_previews WHERE preview_id=$preview_id;";
        command.Parameters.AddWithValue("$preview_id", previewId);
        var value = command.ExecuteScalar();
        if (value is not string createdAt)
            throw new ArgumentException(RetentionMutationErrorCodes.PreviewNotFound, nameof(previewId));
        return ParseConfirmationTimestamp(createdAt);
    }

    private static bool NonceExists(SqliteConnection connection, SqliteTransaction transaction, byte[] nonce)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_confirmation_bindings WHERE nonce=$nonce);";
        command.Parameters.AddWithValue("$nonce", nonce);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static RetentionConfirmationBinding? ReadConfirmationBinding(SqliteConnection connection, SqliteTransaction transaction, string confirmationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BindingSelect("confirmation_id=$confirmation_id");
        command.Parameters.AddWithValue("$confirmation_id", confirmationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    private static RetentionConfirmationBinding? ReadConfirmationBindingByHash(SqliteConnection connection, SqliteTransaction transaction, byte[] tokenHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BindingSelect("token_sha256=$token_sha256");
        command.Parameters.AddWithValue("$token_sha256", tokenHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    private static string BindingSelect(string predicate) => $"""
        SELECT confirmation_id,preview_id,schema_version,token_sha256,nonce,target_kind,target_id,operation,scope,
               preview_digest,expected_state_version,target_item_set_digest,active_conflict_snapshot,conflict_version,
               confirmation_expires_at,workflow_idempotency_key,reason_code,comment_sha256,created_at,consumed_at,
               invalidated_at,operation_id
        FROM retention_confirmation_bindings WHERE {predicate} LIMIT 1;
        """;

    private static RetentionConfirmationBinding ReadBinding(SqliteDataReader reader)
    {
        DateTimeOffset? consumedAt = reader.IsDBNull(19) ? null : ParseConfirmationTimestamp(reader.GetString(19));
        DateTimeOffset? invalidatedAt = reader.IsDBNull(20) ? null : ParseConfirmationTimestamp(reader.GetString(20));
        var state = invalidatedAt is not null
            ? RetentionConfirmationBindingState.Invalidated
            : consumedAt is not null ? RetentionConfirmationBindingState.Consumed : RetentionConfirmationBindingState.Active;
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            ParseConfirmationTargetKind(reader.GetString(5)),
            reader.GetString(6),
            ParseConfirmationOperation(reader.GetString(7)),
            ParseConfirmationScope(reader.GetString(8)),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            ParseConfirmationTimestamp(reader.GetString(14)),
            reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetFieldValue<byte[]>(17),
            ParseConfirmationTimestamp(reader.GetString(18)),
            consumedAt,
            invalidatedAt,
            reader.IsDBNull(21) ? null : reader.GetString(21),
            state);
    }

    private static RetentionMutationTargetKind ParseConfirmationTargetKind(string value) => value switch
    {
        "session" => RetentionMutationTargetKind.Session,
        "item" => RetentionMutationTargetKind.Item,
        _ => throw new InvalidOperationException("retention_confirmation_binding_invalid")
    };

    private static RetentionMutationOperation ParseConfirmationOperation(string value) => value switch
    {
        "pin" => RetentionMutationOperation.Pin,
        "unpin" => RetentionMutationOperation.Unpin,
        "delete_now" => RetentionMutationOperation.DeleteNow,
        _ => throw new InvalidOperationException("retention_confirmation_binding_invalid")
    };

    private static RetentionMutationScope ParseConfirmationScope(string value) => value switch
    {
        "session_items" => RetentionMutationScope.SessionItems,
        "single_item" => RetentionMutationScope.SingleItem,
        _ => throw new InvalidOperationException("retention_confirmation_binding_invalid")
    };

    private static bool IsConfirmationDigest(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length == prefix.Length + 64
        && value[prefix.Length..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsConfirmationJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static DateTimeOffset ParseConfirmationTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}

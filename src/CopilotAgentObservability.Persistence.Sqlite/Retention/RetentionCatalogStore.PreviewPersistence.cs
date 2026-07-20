using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionMutationPreviewPersistenceResult CreateMutationPreview(
        RetentionMutationPreviewRequest request,
        string workflowKey,
        string canonicalRequest,
        DateTimeOffset now,
        Func<RetentionMutationPreviewProjection, IReadOnlyList<RetentionMutationActiveConflictSnapshot>, DateTimeOffset, RetentionStoredMutationPreview> createRecord)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createRecord);
        if (!RetentionMutationRequestValidator.Validate(request).IsValid
            || !RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey))
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid);

        var lookupRequest = new RetentionIdempotencyRequest(
            workflowKey,
            RetentionMutationOperationStep.Preview,
            canonicalRequest,
            "{}",
            null);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction(deferred: false);
        var lookup = LookupIdempotencyWithinTransaction(connection, transaction, lookupRequest);
        if (lookup is not null)
        {
            transaction.Commit();
            return new(lookup.Disposition, lookup.ResultJson, null, IdempotencyError(lookup.Disposition));
        }

        var materialization = MaterializeMutationPreviewWithinTransaction(
            connection,
            transaction,
            request.Target,
            request.Operation,
            request.Scope,
            now);
        if (materialization.Outcome != RetentionMutationPreviewProjectionOutcome.Ready)
        {
            transaction.Rollback();
            return new(
                RetentionIdempotencyDisposition.Conflict,
                null,
                null,
                materialization.ErrorCode);
        }

        var record = createRecord(materialization.Projection!, materialization.ConflictSnapshot, now);
        InsertMutationPreviewWithinTransaction(connection, transaction, record);
        var idempotencyRequest = lookupRequest with { ResultJson = JsonSerializer.Serialize(record.Response) };
        var created = GetOrCreateIdempotencyWithinTransaction(connection, transaction, idempotencyRequest);
        if (created.Disposition != RetentionIdempotencyDisposition.Created)
        {
            transaction.Rollback();
            return new(created.Disposition, created.ResultJson, null, IdempotencyError(created.Disposition));
        }

        transaction.Commit();
        return new(created.Disposition, created.ResultJson, record, null);
    }

    internal RetentionStoredMutationPreview? ReadMutationPreview(string previewId)
    {
        if (!RetentionMutationIdentifiers.TryParsePreviewId(previewId, out _)) return null;
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var result = ReadMutationPreviewWithinTransaction(connection, transaction, previewId);
        transaction.Commit();
        return result;
    }

    internal (bool Found, byte[]? WorkflowKeyDigest) ReadMutationPreviewWorkflowKeyDigest(string previewId)
    {
        if (!RetentionMutationIdentifiers.TryParsePreviewId(previewId, out _)) return (false, null);
        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT workflow_key_digest FROM retention_mutation_previews WHERE preview_id=$preview_id;";
        command.Parameters.AddWithValue("$preview_id", previewId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return (false, null);
        }

        var digest = reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0);
        transaction.Commit();
        return (true, digest);
    }

    internal RetentionStoredMutationPreview? ReadMutationPreviewWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string previewId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT preview_id,preview_json,expected_state_version,target_item_set_digest,preview_digest,
                   workflow_key_digest,created_at,expires_at,active_conflict_snapshot,conflict_version,reason_code,comment_sha256,comment
            FROM retention_mutation_previews
            WHERE preview_id=$preview_id;
            """;
        command.Parameters.AddWithValue("$preview_id", previewId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var response = JsonSerializer.Deserialize<RetentionMutationPreviewResponse>(reader.GetString(1))
            ?? throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable);
        var expectedStateVersion = reader.GetString(2);
        var targetItemSetDigest = reader.GetString(3);
        var previewDigest = reader.GetString(4);
        var workflowKeyDigest = reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5);
        var createdAt = ParseTimestamp(reader.GetString(6));
        DateTimeOffset? expiresAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7));
        if (!string.Equals(response.PreviewId, reader.GetString(0), StringComparison.Ordinal)
            || !string.Equals(response.ExpectedStateVersion, expectedStateVersion, StringComparison.Ordinal)
            || !string.Equals(response.TargetItemSetDigest, targetItemSetDigest, StringComparison.Ordinal)
            || !string.Equals(response.PreviewDigest, previewDigest, StringComparison.Ordinal)
            || response.ConfirmationExpiresAt != expiresAt)
            throw new InvalidOperationException(RetentionMutationErrorCodes.CatalogUnavailable);

        var commentSha256 = reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11);
        return new(
            response,
            createdAt,
            expiresAt,
            workflowKeyDigest,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            commentSha256,
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static void InsertMutationPreviewWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionStoredMutationPreview record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retention_mutation_previews(
                preview_id,schema_version,target_kind,target_id,operation,scope,preview_json,
                expected_state_version,target_item_set_digest,preview_digest,workflow_key_digest,created_at,expires_at,
                rejection_code,active_conflict_snapshot,conflict_version,reason_code,comment_sha256,comment)
            VALUES(
                $preview_id,$schema_version,$target_kind,$target_id,$operation,$scope,$preview_json,
                $expected_state_version,$target_item_set_digest,$preview_digest,$workflow_key_digest,$created_at,$expires_at,
                $rejection_code,$active_conflict_snapshot,$conflict_version,$reason_code,$comment_sha256,$comment);
            """;
        command.Parameters.AddWithValue("$preview_id", record.Response.PreviewId);
        command.Parameters.AddWithValue("$schema_version", record.Response.SchemaVersion);
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(record.Response.TargetKind));
        command.Parameters.AddWithValue("$target_id", record.Response.TargetId);
        command.Parameters.AddWithValue("$operation", RetentionMutationWire.Operation(record.Response.Operation));
        command.Parameters.AddWithValue("$scope", RetentionMutationWire.Scope(record.Response.Scope));
        command.Parameters.AddWithValue("$preview_json", JsonSerializer.Serialize(record.Response));
        command.Parameters.AddWithValue("$expected_state_version", record.Response.ExpectedStateVersion);
        command.Parameters.AddWithValue("$target_item_set_digest", record.Response.TargetItemSetDigest);
        command.Parameters.AddWithValue("$preview_digest", record.Response.PreviewDigest);
        command.Parameters.AddWithValue("$workflow_key_digest", (object?)record.WorkflowKeyDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", Timestamp(record.CreatedAt));
        command.Parameters.AddWithValue("$expires_at", (object?)record.ExpiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rejection_code", (object?)record.Response.RejectionCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$active_conflict_snapshot", (object?)record.ActiveConflictSnapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$conflict_version", (object?)record.ConflictVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason_code", (object?)record.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment_sha256", (object?)record.CommentSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)record.Comment ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? IdempotencyError(RetentionIdempotencyDisposition disposition) => disposition switch
    {
        RetentionIdempotencyDisposition.Conflict => RetentionMutationErrorCodes.IdempotencyConflict,
        RetentionIdempotencyDisposition.Expired => RetentionMutationErrorCodes.IdempotencyExpired,
        _ => null
    };

}

using System.Globalization;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal sealed record SkillInvocationSnapshotReplayRequest(
    string SourceAdapter,
    string SourceEventId,
    string RequestFingerprintSha256);

internal enum SkillInvocationSnapshotReplayOutcome
{
    EqualReplay,
    DifferentFingerprint,
    ReceiptMissing,
    Busy,
    Unavailable,
}

internal enum SkillInvocationSnapshotReceiptProbeOutcome
{
    Missing,
    DifferentFingerprint,
    EqualFingerprint,
    Busy,
    Unavailable,
}

// Both transaction-ownership arms below run the exact same Core: a divergence between an owned
// validation-only transaction and a mutation-owner's already-held one is the exact failure this
// component exists to prevent, so neither arm may carry its own copy of the graph logic.
internal static class SkillInvocationSnapshotReplayValidator
{
    private const string CopilotSdkSurface = "copilot-sdk";
    private static readonly string[] AcceptedBindingKinds = ["native", "explicit_resume", "explicit_handoff"];

    // Receipt lookup is its own stage ahead of the validation transaction so write contention
    // cannot mask a different-fingerprint conflict or a later registry-authority failure.
    internal static SkillInvocationSnapshotReceiptProbeOutcome ProbeReceipt(
        string databasePath,
        SkillInvocationSnapshotReplayRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var receipt = ReadReceipt(connection, transaction, request.SourceAdapter, request.SourceEventId);
                if (receipt is null)
                    return SkillInvocationSnapshotReceiptProbeOutcome.Missing;

                return string.Equals(
                    receipt.RequestFingerprintSha256,
                    request.RequestFingerprintSha256,
                    StringComparison.Ordinal)
                    ? SkillInvocationSnapshotReceiptProbeOutcome.EqualFingerprint
                    : SkillInvocationSnapshotReceiptProbeOutcome.DifferentFingerprint;
            }
            finally
            {
                transaction.Rollback();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillInvocationSnapshotReceiptProbeOutcome.Busy;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or FormatException
                or OverflowException or InvalidCastException)
        {
            return SkillInvocationSnapshotReceiptProbeOutcome.Unavailable;
        }
    }

    internal static SkillInvocationSnapshotReplayOutcome ValidateOwnedTransaction(
        string databasePath,
        SkillInvocationSnapshotReplayRequest request,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                return Core(connection, transaction, request, timeProvider);
            }
            finally
            {
                transaction.Rollback();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillInvocationSnapshotReplayOutcome.Busy;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or FormatException
                or OverflowException or InvalidCastException)
        {
            return SkillInvocationSnapshotReplayOutcome.Unavailable;
        }
    }

    internal static SkillInvocationSnapshotReplayOutcome ValidateInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillInvocationSnapshotReplayRequest request,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            return Core(connection, transaction, request, timeProvider);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillInvocationSnapshotReplayOutcome.Busy;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or FormatException
                or OverflowException or InvalidCastException)
        {
            return SkillInvocationSnapshotReplayOutcome.Unavailable;
        }
    }

    private static SkillInvocationSnapshotReplayOutcome Core(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillInvocationSnapshotReplayRequest request,
        TimeProvider timeProvider)
    {
        var receipt = ReadReceipt(connection, transaction, request.SourceAdapter, request.SourceEventId);
        if (receipt is null)
            return SkillInvocationSnapshotReplayOutcome.ReceiptMissing;

        // A fingerprint mismatch is conflict, not corruption: it must be decided from the receipt
        // row alone, before validation_at is sampled, so a losing replay never consumes the one
        // clock read the equal-fingerprint path is allowed.
        if (!string.Equals(receipt.RequestFingerprintSha256, request.RequestFingerprintSha256, StringComparison.Ordinal))
            return SkillInvocationSnapshotReplayOutcome.DifferentFingerprint;

        var validationAt = timeProvider.GetUtcNow();

        var snapshot = ReadSnapshot(connection, transaction, receipt.SnapshotId);
        if (snapshot is null)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        var item = ReadRetentionItem(connection, transaction, snapshot.ContentItemId);
        if (item is null)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        var content = ReadContentRow(connection, transaction, snapshot.EventId);
        var tombstoneExists = TombstoneExists(connection, transaction, snapshot.ContentItemId);

        if (content is not null)
        {
            var readability = RetentionCatalogStore.ClassifyRowReadability(item.CatalogItem, validationAt);
            if (readability == RetentionRowReadability.Readable && !tombstoneExists)
            {
                if (!VerifyLiveReadableContent(content, item, snapshot))
                    return SkillInvocationSnapshotReplayOutcome.Unavailable;
            }
            else
            {
                // Owner-valid but nonreadable (already denied, lifecycle-denied, or expired) still
                // has the raw row physically present until deletion completes. Equal-fingerprint
                // ingest replay is not authorized to read raw content in that window, so this arm
                // never opens session_event_content here -- it returns sanitized 503 on the
                // classification alone, the same as any other reader would see right now.
                return SkillInvocationSnapshotReplayOutcome.Unavailable;
            }
        }
        else if (item.CatalogItem.State != RetentionItemLifecycle.Deleted || !tombstoneExists)
        {
            return SkillInvocationSnapshotReplayOutcome.Unavailable;
        }

        return VerifyRemainingGraph(connection, transaction, snapshot, receipt)
            ? SkillInvocationSnapshotReplayOutcome.EqualReplay
            : SkillInvocationSnapshotReplayOutcome.Unavailable;
    }

    private static bool VerifyLiveReadableContent(
        ContentRow content, RetentionItemRow item, SnapshotRow snapshot)
    {
        if (!string.Equals(content.ContentKind, SkillInvocationSnapshotContentDocumentV1.ContentKind, StringComparison.Ordinal))
            return false;
        if (!string.Equals(content.CapturedAtText, snapshot.CapturedAtText, StringComparison.Ordinal))
            return false;
        if (!string.Equals(content.ExpiresAtText, item.ExpiresAtText, StringComparison.Ordinal))
            return false;

        var documentBytes = Encoding.UTF8.GetBytes(content.ContentJson);
        if (!SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(documentBytes, out var payloadToken, out _))
            return false;

        if (payloadToken.Length != snapshot.PayloadBytes)
            return false;
        if (!string.Equals(
                SkillInvocationSnapshotContentDocumentV1.PayloadSha256(payloadToken),
                snapshot.PayloadSha256,
                StringComparison.Ordinal))
            return false;
        if (!string.Equals(
                SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(documentBytes),
                snapshot.ContentDocumentSha256,
                StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool VerifyRemainingGraph(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, ReceiptRow receipt)
    {
        if (!EventMatches(connection, transaction, snapshot))
            return false;
        if (!NativeBindingMatches(connection, transaction, snapshot))
            return false;
        if (snapshot.RunId is not null && !RunMatches(connection, transaction, snapshot))
            return false;
        if (snapshot.ClaimId is not null && !ClaimMatches(connection, transaction, snapshot))
            return false;
        if (!IsLegalClassificationPair(snapshot.State, snapshot.Reason))
            return false;
        if (!string.Equals(snapshot.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal))
            return false;
        if (!string.Equals(receipt.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool EventMatches(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT type,source_adapter,source_surface,content_state,status,match_kind,parent_event_id,
                   terminal_outcome,terminal_policy_version
            FROM session_events
            WHERE session_id=$session_id AND event_id=$event_id;
            """;
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;

        return reader.GetString(0) == "skill.invoked"
            && reader.GetString(1) == "copilot-sdk-stream"
            && !reader.IsDBNull(2) && reader.GetString(2) == CopilotSdkSurface
            && reader.GetString(3) == "available"
            && reader.IsDBNull(4)
            && reader.IsDBNull(5)
            && reader.IsDBNull(6)
            && reader.IsDBNull(7)
            && reader.IsDBNull(8);
    }

    private static bool NativeBindingMatches(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT session_id,binding_kind FROM session_native_ids WHERE source_surface=$surface AND native_session_id=$native;";
        command.Parameters.AddWithValue("$surface", CopilotSdkSurface);
        command.Parameters.AddWithValue("$native", snapshot.NativeSessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var sessionId = reader.GetString(0);
        var bindingKind = reader.GetString(1);
        if (reader.Read())
            return false;

        return sessionId == snapshot.SessionId && Array.IndexOf(AcceptedBindingKinds, bindingKind) >= 0;
    }

    private static bool RunMatches(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM session_runs WHERE session_id=$session_id AND run_id=$run_id;";
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$run_id", snapshot.RunId!);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool ClaimMatches(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*) FROM skill_projection_sdk_claims
            WHERE claim_id=$claim_id AND session_id=$session_id AND event_id=$event_id AND created_at=$captured_at
              AND skill_name IS $name AND skill_source IS $source AND invocation_trigger IS $trigger
              AND payload_schema=$payload_schema AND schema_fingerprint=$schema_fingerprint AND payload_sha256=$payload_sha256;
            """;
        command.Parameters.AddWithValue("$claim_id", snapshot.ClaimId!);
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        command.Parameters.AddWithValue("$captured_at", snapshot.CapturedAtText);
        command.Parameters.AddWithValue("$name", snapshot.Name is null ? DBNull.Value : snapshot.Name);
        command.Parameters.AddWithValue("$source", snapshot.Source is null ? DBNull.Value : snapshot.Source);
        command.Parameters.AddWithValue("$trigger", snapshot.Trigger is null ? DBNull.Value : snapshot.Trigger);
        command.Parameters.AddWithValue("$payload_schema", snapshot.PayloadSchema);
        command.Parameters.AddWithValue("$schema_fingerprint", snapshot.SchemaFingerprint);
        command.Parameters.AddWithValue("$payload_sha256", snapshot.PayloadSha256);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool IsLegalClassificationPair(string state, string reason) => (state, reason) switch
    {
        ("malformed", "duplicate_property" or "unknown_property" or "invalid_field_type" or "name_invalid" or "path_invalid") => true,
        ("missing", "name_missing" or "body_missing" or "definition_path_missing") => true,
        ("binary", "body_unicode_invalid" or "path_unicode_invalid") => true,
        ("oversized", "body_oversized" or "path_oversized") => true,
        ("available", "none") => true,
        _ => false,
    };

    private static ReceiptRow? ReadReceipt(
        SqliteConnection connection, SqliteTransaction transaction, string sourceAdapter, string sourceEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT snapshot_id,request_fingerprint_sha256,created_at FROM skill_invocation_snapshot_receipts WHERE source_adapter=$adapter AND source_event_id=$event;";
        command.Parameters.AddWithValue("$adapter", sourceAdapter);
        command.Parameters.AddWithValue("$event", sourceEventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static SnapshotRow? ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT session_id,native_session_id,event_id,claim_id,run_id,name,source,trigger,state,reason,
                   content_item_id,payload_sha256,payload_bytes,content_document_sha256,
                   payload_schema,schema_fingerprint,captured_at,created_at
            FROM skill_invocation_snapshots
            WHERE snapshot_id=$snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SnapshotRow(
            SessionId: reader.GetString(0),
            NativeSessionId: reader.GetString(1),
            EventId: reader.GetString(2),
            ClaimId: reader.IsDBNull(3) ? null : reader.GetString(3),
            RunId: reader.IsDBNull(4) ? null : reader.GetString(4),
            Name: reader.IsDBNull(5) ? null : reader.GetString(5),
            Source: reader.IsDBNull(6) ? null : reader.GetString(6),
            Trigger: reader.IsDBNull(7) ? null : reader.GetString(7),
            State: reader.GetString(8),
            Reason: reader.GetString(9),
            ContentItemId: reader.GetString(10),
            PayloadSha256: reader.GetString(11),
            PayloadBytes: reader.GetInt64(12),
            ContentDocumentSha256: reader.GetString(13),
            PayloadSchema: reader.GetString(14),
            SchemaFingerprint: reader.GetString(15),
            CapturedAtText: reader.GetString(16),
            CreatedAtText: reader.GetString(17));
    }

    private static RetentionItemRow? ReadRetentionItem(SqliteConnection connection, SqliteTransaction transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT store_instance_id,store_kind,source_item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items WHERE item_id=$item_id;";
        command.Parameters.AddWithValue("$item_id", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetString(1) != "session_event_content")
            return null;

        var expiresAtText = reader.GetString(4);
        var catalogItem = new RetentionCatalogItem(
            itemId,
            new RetentionOwnershipKey(reader.GetString(0), RetentionStoreKind.SessionEventContent, reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(expiresAtText),
            ParseState(reader.GetString(5)),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));
        return new RetentionItemRow(catalogItem, expiresAtText);
    }

    private static ContentRow? ReadContentRow(SqliteConnection connection, SqliteTransaction transaction, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT content_kind,content_json,captured_at,expires_at FROM session_event_content WHERE event_id=$event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ContentRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static bool TombstoneExists(SqliteConnection connection, SqliteTransaction transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_tombstones WHERE item_id=$item_id);";
        command.Parameters.AddWithValue("$item_id", itemId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static RetentionItemLifecycle ParseState(string wire) =>
        Enum.Parse<RetentionItemLifecycle>(wire.Replace("_", string.Empty), ignoreCase: true);

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ReceiptRow(string SnapshotId, string RequestFingerprintSha256, string CreatedAtText);

    private sealed record RetentionItemRow(RetentionCatalogItem CatalogItem, string ExpiresAtText);

    private sealed record SnapshotRow(
        string SessionId,
        string NativeSessionId,
        string EventId,
        string? ClaimId,
        string? RunId,
        string? Name,
        string? Source,
        string? Trigger,
        string State,
        string Reason,
        string ContentItemId,
        string PayloadSha256,
        long PayloadBytes,
        string ContentDocumentSha256,
        string PayloadSchema,
        string SchemaFingerprint,
        string CapturedAtText,
        string CreatedAtText);

    private sealed record ContentRow(string ContentKind, string ContentJson, string CapturedAtText, string ExpiresAtText);
}

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

    internal static bool PersistedReceiptsMatchGraph(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT snapshot_id,request_fingerprint_sha256,created_at,source_adapter,source_event_id FROM skill_invocation_snapshot_receipts;";
        using var reader = command.ExecuteReader();
        var receipts = new List<ReceiptRow>();
        while (reader.Read())
            receipts.Add(new ReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));

        foreach (var receipt in receipts)
        {
            var snapshot = ReadSnapshot(connection, transaction, receipt.SnapshotId);
            if (snapshot is null
                || !TryComputePersistedFingerprint(connection, transaction, snapshot, receipt, out var fingerprint)
                || !string.Equals(receipt.RequestFingerprintSha256, fingerprint, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

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
                if (!TryReadLinkedReceipt(connection, transaction, request.SourceAdapter, request.SourceEventId, out var receipt))
                    return SkillInvocationSnapshotReceiptProbeOutcome.Missing;
                if (receipt is null)
                    return SkillInvocationSnapshotReceiptProbeOutcome.Unavailable;

                var snapshot = ReadSnapshot(connection, transaction, receipt.SnapshotId);
                if (snapshot is null
                    || !NonContentCanonicalGraphMatches(connection, transaction, snapshot, receipt)
                    || !TryComputePersistedFingerprint(connection, transaction, snapshot, receipt, out var persistedFingerprint)
                    || !string.Equals(receipt.RequestFingerprintSha256, persistedFingerprint, StringComparison.Ordinal))
                    return SkillInvocationSnapshotReceiptProbeOutcome.Unavailable;

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
        if (!TryReadLinkedReceipt(connection, transaction, request.SourceAdapter, request.SourceEventId, out var receipt))
            return SkillInvocationSnapshotReplayOutcome.ReceiptMissing;
        if (receipt is null)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        var snapshot = ReadSnapshot(connection, transaction, receipt.SnapshotId);
        if (snapshot is null)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        if (!NonContentCanonicalGraphMatches(connection, transaction, snapshot, receipt)
            || !TryComputePersistedFingerprint(connection, transaction, snapshot, receipt, out var persistedFingerprint)
            || !string.Equals(receipt.RequestFingerprintSha256, persistedFingerprint, StringComparison.Ordinal))
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        // Only a receipt first proved canonical may classify a caller's different fingerprint as
        // an idempotency conflict. Corrupt persisted state always fails closed instead.
        if (!string.Equals(receipt.RequestFingerprintSha256, request.RequestFingerprintSha256, StringComparison.Ordinal))
            return SkillInvocationSnapshotReplayOutcome.DifferentFingerprint;

        var validationAt = timeProvider.GetUtcNow();

        var item = ReadRetentionItem(connection, transaction, snapshot.ContentItemId);
        if (item is null)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        var readability = RetentionCatalogStore.ClassifyRowReadability(item.CatalogItem, validationAt);
        if (item.CatalogItem.State == RetentionItemLifecycle.Deleted)
            return VerifyRemainingGraph(connection, transaction, snapshot, receipt)
                ? SkillInvocationSnapshotReplayOutcome.EqualReplay
                : SkillInvocationSnapshotReplayOutcome.Unavailable;
        if (readability != RetentionRowReadability.Readable)
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

        var content = ReadContentRow(connection, transaction, snapshot.EventId);
        if (content is null || !VerifyLiveReadableContent(content, item, snapshot))
            return SkillInvocationSnapshotReplayOutcome.Unavailable;

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
                   terminal_outcome,terminal_policy_version,source_application_version,adapter_version,
                   normalization_version,schema_fingerprint,run_id,trace_id
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
            && reader.IsDBNull(8)
            && string.Equals(reader.GetString(9), snapshot.SourceApplicationVersion, StringComparison.Ordinal)
            && string.Equals(reader.GetString(10), snapshot.AdapterVersion, StringComparison.Ordinal)
            && string.Equals(reader.GetString(11), snapshot.NormalizationVersion, StringComparison.Ordinal)
            && string.Equals(reader.GetString(12), snapshot.SchemaFingerprint, StringComparison.Ordinal)
            && reader.IsDBNull(14)
            && snapshot.TraceId is null && snapshot.SpanId is null
            && (snapshot.State == "available"
                ? NullableTextEquals(reader.IsDBNull(13) ? null : reader.GetString(13), snapshot.RunId)
                : snapshot.RunId is null && snapshot.TraceId is null && snapshot.SpanId is null);
    }

    private static bool NonContentCanonicalGraphMatches(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, ReceiptRow receipt) =>
        EventMatches(connection, transaction, snapshot)
        && (snapshot.ClaimId is null
            ? FaultClaimCollisionCount(connection, transaction, snapshot) == 0
            : ClaimMatches(connection, transaction, snapshot))
        && NativeBindingMatches(connection, transaction, snapshot)
        && SessionEnvelopeMatches(connection, transaction, snapshot)
        && EventRunMatches(connection, transaction, snapshot)
        && NonContentStorageGraphMatches(connection, transaction, snapshot)
        && IsLegalClassificationPair(snapshot.State, snapshot.Reason)
        && ClassificationFieldsMatch(snapshot)
        && string.Equals(snapshot.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal)
        && string.Equals(receipt.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal);

    private static bool NonContentStorageGraphMatches(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        var item = ReadRetentionItem(connection, transaction, snapshot.ContentItemId);
        if (item is null
            || !string.Equals(item.CatalogItem.OwnershipKey.SourceItemId, snapshot.EventId, StringComparison.Ordinal)
            || item.CatalogItem.CapturedAt != ParseTimestamp(snapshot.CapturedAtText))
            return false;

        bool contentExists;
        bool contentMetadataValid = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT content_kind,captured_at,expires_at FROM session_event_content WHERE event_id=$event_id;";
            command.Parameters.AddWithValue("$event_id", snapshot.EventId);
            using var reader = command.ExecuteReader();
            contentExists = reader.Read();
            if (contentExists)
            {
                contentMetadataValid = string.Equals(reader.GetString(0), SkillInvocationSnapshotContentDocumentV1.ContentKind, StringComparison.Ordinal)
                    && string.Equals(reader.GetString(1), snapshot.CapturedAtText, StringComparison.Ordinal)
                    && string.Equals(reader.GetString(2), item.ExpiresAtText, StringComparison.Ordinal)
                    && !reader.Read();
            }
        }

        if (contentExists)
            return contentMetadataValid && item.CatalogItem.State != RetentionItemLifecycle.Deleted
                && !TombstoneExists(connection, transaction, snapshot.ContentItemId);
        return item.CatalogItem.State == RetentionItemLifecycle.Deleted
            && TombstoneExists(connection, transaction, snapshot.ContentItemId);
    }

    private static bool NullableTextEquals(string? left, string? right) =>
        left is null ? right is null : right is not null && string.Equals(left, right, StringComparison.Ordinal);

    private static bool ClassificationFieldsMatch(SnapshotRow snapshot) =>
        snapshot.State == "available"
            ? snapshot.ClaimId is not null && snapshot.Name is not null
              && snapshot.BodySha256 is not null && snapshot.BodyUtf8Bytes is not null
              && snapshot.DefinitionPathSha256 is not null && snapshot.DefinitionPathUtf8Bytes is not null
            : snapshot.ClaimId is null && snapshot.Name is null && snapshot.Source is null && snapshot.Trigger is null
              && snapshot.RunId is null && snapshot.TraceId is null && snapshot.SpanId is null
              && snapshot.BodySha256 is null && snapshot.BodyUtf8Bytes is null
              && snapshot.DefinitionPathSha256 is null && snapshot.DefinitionPathUtf8Bytes is null;

    private static bool SessionEnvelopeMatches(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT created_at,updated_at FROM sessions WHERE session_id=$session_id;";
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var capturedAt = ParseTimestamp(snapshot.CapturedAtText);
        var valid = ParseTimestamp(reader.GetString(0)) <= capturedAt && capturedAt <= ParseTimestamp(reader.GetString(1));
        return valid && !reader.Read();
    }

    private static bool EventRunMatches(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT e.run_id,r.source_surface,r.native_run_id,
                   (SELECT COUNT(*) FROM session_runs n
                    WHERE n.session_id=e.session_id AND n.source_surface='copilot-sdk' AND n.native_run_id=r.native_run_id)
            FROM session_events e
            LEFT JOIN session_runs r ON r.session_id=e.session_id AND r.run_id=e.run_id
            WHERE e.session_id=$session_id AND e.event_id=$event_id;
            """;
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var eventRunId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var validRun = eventRunId is null
            ? reader.IsDBNull(1) && reader.IsDBNull(2) && reader.GetInt64(3) == 0
            : !reader.IsDBNull(1) && string.Equals(reader.GetString(1), CopilotSdkSurface, StringComparison.Ordinal)
              && !reader.IsDBNull(2) && reader.GetString(2).Length > 0 && reader.GetInt64(3) == 1;
        return !reader.Read()
            && validRun
            && (snapshot.State == "available" ? NullableTextEquals(eventRunId, snapshot.RunId) : snapshot.RunId is null);
    }

    private static long FaultClaimCollisionCount(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        var sourceEventId = ReadEventSourceEventId(connection, transaction, snapshot);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM skill_projection_sdk_claims WHERE (session_id=$session_id AND event_id=$event_id) OR (source_adapter='copilot-sdk-stream' AND source_event_id=$source_event_id);";
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TryComputePersistedFingerprint(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, ReceiptRow receipt,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText =
            """
            SELECT source_adapter,source_event_id,source_surface,occurred_at,run_id
            FROM session_events WHERE session_id=$session_id AND event_id=$event_id;
            """;
        eventCommand.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        eventCommand.Parameters.AddWithValue("$event_id", snapshot.EventId);
        using var eventReader = eventCommand.ExecuteReader();
        if (!eventReader.Read())
            return false;
        var sourceAdapter = eventReader.GetString(0);
        var sourceEventId = eventReader.GetString(1);
        var sourceSurface = eventReader.IsDBNull(2) ? null : eventReader.GetString(2);
        var occurredAt = eventReader.GetString(3);
        var eventRunId = eventReader.IsDBNull(4) ? null : eventReader.GetString(4);
        if (eventReader.Read()
            || sourceSurface is null
            || !string.Equals(receipt.SourceAdapter, sourceAdapter, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceEventId, sourceEventId, StringComparison.Ordinal))
            return false;

        string? runNativeId = null;
        if (eventRunId is not null)
        {
            using var runCommand = connection.CreateCommand();
            runCommand.Transaction = transaction;
            runCommand.CommandText = "SELECT native_run_id FROM session_runs WHERE session_id=$session_id AND run_id=$run_id;";
            runCommand.Parameters.AddWithValue("$session_id", snapshot.SessionId);
            runCommand.Parameters.AddWithValue("$run_id", eventRunId);
            runNativeId = runCommand.ExecuteScalar() as string;
            if (runNativeId is null)
                return false;
        }

        var input = new SkillInvocationSnapshotReceiptFingerprintInput(
            sourceAdapter, sourceEventId, sourceSurface, snapshot.NativeSessionId, runNativeId,
            snapshot.SourceParentEventId, snapshot.SourceEphemeral, snapshot.TraceId, snapshot.SpanId,
            ParseTimestamp(occurredAt), snapshot.SourceApplicationVersion, snapshot.AdapterVersion,
            snapshot.NormalizationVersion, snapshot.PayloadSchema, snapshot.SchemaFingerprint, snapshot.PayloadSha256,
            checked((ulong)snapshot.PayloadBytes), snapshot.State, snapshot.Reason, snapshot.Name, snapshot.Source,
            snapshot.Trigger, snapshot.BodySha256, (ulong?)snapshot.BodyUtf8Bytes, snapshot.DefinitionPathSha256,
            (ulong?)snapshot.DefinitionPathUtf8Bytes, snapshot.ContentDocumentSha256);
        fingerprint = SkillInvocationSnapshotReceiptFingerprint.Compute(input);
        return true;
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
              AND source_application_version=$source_application_version AND adapter_version=$adapter_version
              AND normalization_version=$normalization_version
              AND payload_schema=$payload_schema AND schema_fingerprint=$schema_fingerprint AND payload_sha256=$payload_sha256
              AND source_event_id=$source_event_id AND source_adapter='copilot-sdk-stream' AND source_surface=$source_surface
              AND producer_trace_id IS $trace_id AND producer_span_id IS $span_id;
            """;
        command.Parameters.AddWithValue("$claim_id", snapshot.ClaimId!);
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        command.Parameters.AddWithValue("$captured_at", snapshot.CapturedAtText);
        command.Parameters.AddWithValue("$name", snapshot.Name is null ? DBNull.Value : snapshot.Name);
        command.Parameters.AddWithValue("$source", snapshot.Source is null ? DBNull.Value : snapshot.Source);
        command.Parameters.AddWithValue("$trigger", snapshot.Trigger is null ? DBNull.Value : snapshot.Trigger);
        command.Parameters.AddWithValue("$source_application_version", snapshot.SourceApplicationVersion);
        command.Parameters.AddWithValue("$adapter_version", snapshot.AdapterVersion);
        command.Parameters.AddWithValue("$normalization_version", snapshot.NormalizationVersion);
        command.Parameters.AddWithValue("$payload_schema", snapshot.PayloadSchema);
        command.Parameters.AddWithValue("$schema_fingerprint", snapshot.SchemaFingerprint);
        command.Parameters.AddWithValue("$payload_sha256", snapshot.PayloadSha256);
        command.Parameters.AddWithValue("$source_event_id", ReadEventSourceEventId(connection, transaction, snapshot));
        command.Parameters.AddWithValue("$source_surface", CopilotSdkSurface);
        command.Parameters.AddWithValue("$trace_id", snapshot.TraceId is null ? DBNull.Value : snapshot.TraceId);
        command.Parameters.AddWithValue("$span_id", snapshot.SpanId is null ? DBNull.Value : snapshot.SpanId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string ReadEventSourceEventId(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source_event_id FROM session_events WHERE session_id=$session_id AND event_id=$event_id;";
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("skill_invocation_snapshot_event_missing");
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

    private static bool TryReadLinkedReceipt(
        SqliteConnection connection, SqliteTransaction transaction, string sourceAdapter, string sourceEventId,
        out ReceiptRow? receipt)
    {
        receipt = null;
        if (TryReadReceiptByNaturalKey(connection, transaction, sourceAdapter, sourceEventId, out receipt))
            return true;

        string? snapshotId;
        using (var graphCommand = connection.CreateCommand())
        {
            graphCommand.Transaction = transaction;
            graphCommand.CommandText =
                """
                SELECT s.snapshot_id
                FROM session_events e
                LEFT JOIN skill_invocation_snapshots s
                  ON s.session_id=e.session_id AND s.event_id=e.event_id
                WHERE e.source_adapter=$adapter AND e.source_event_id=$event;
                """;
            graphCommand.Parameters.AddWithValue("$adapter", sourceAdapter);
            graphCommand.Parameters.AddWithValue("$event", sourceEventId);
            using var graphReader = graphCommand.ExecuteReader();
            if (!graphReader.Read())
                return false;
            snapshotId = graphReader.IsDBNull(0) ? null : graphReader.GetString(0);
            if (graphReader.Read())
                return true;
            if (snapshotId is null)
                return false;
        }

        using var receiptCommand = connection.CreateCommand();
        receiptCommand.Transaction = transaction;
        receiptCommand.CommandText =
            "SELECT snapshot_id,request_fingerprint_sha256,created_at,source_adapter,source_event_id FROM skill_invocation_snapshot_receipts WHERE snapshot_id=$snapshot_id;";
        receiptCommand.Parameters.AddWithValue("$snapshot_id", snapshotId);
        using var receiptReader = receiptCommand.ExecuteReader();
        if (!receiptReader.Read())
            return true;
        var row = new ReceiptRow(receiptReader.GetString(0), receiptReader.GetString(1), receiptReader.GetString(2), receiptReader.GetString(3), receiptReader.GetString(4));
        if (!receiptReader.Read())
            receipt = row;
        return true;
    }

    private static bool TryReadReceiptByNaturalKey(
        SqliteConnection connection, SqliteTransaction transaction, string sourceAdapter, string sourceEventId,
        out ReceiptRow? receipt)
    {
        receipt = null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT snapshot_id,request_fingerprint_sha256,created_at,source_adapter,source_event_id FROM skill_invocation_snapshot_receipts WHERE source_adapter=$adapter AND source_event_id=$event;";
        command.Parameters.AddWithValue("$adapter", sourceAdapter);
        command.Parameters.AddWithValue("$event", sourceEventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        var row = new ReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        if (!reader.Read())
            receipt = row;
        return true;
    }

    private static SnapshotRow? ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT session_id,native_session_id,event_id,claim_id,run_id,name,source,trigger,state,reason,
                   content_item_id,payload_sha256,payload_bytes,content_document_sha256,
                   trace_id,span_id,source_parent_event_id,source_ephemeral,body_sha256,body_utf8_bytes,
                   definition_path_sha256,definition_path_utf8_bytes,
                   source_application_version,adapter_version,normalization_version,
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
            TraceId: reader.IsDBNull(14) ? null : reader.GetString(14),
            SpanId: reader.IsDBNull(15) ? null : reader.GetString(15),
            SourceParentEventId: reader.IsDBNull(16) ? null : reader.GetString(16),
            SourceEphemeral: reader.GetInt64(17) == 1,
            BodySha256: reader.IsDBNull(18) ? null : reader.GetString(18),
            BodyUtf8Bytes: reader.IsDBNull(19) ? null : reader.GetInt64(19),
            DefinitionPathSha256: reader.IsDBNull(20) ? null : reader.GetString(20),
            DefinitionPathUtf8Bytes: reader.IsDBNull(21) ? null : reader.GetInt64(21),
            SourceApplicationVersion: reader.GetString(22),
            AdapterVersion: reader.GetString(23),
            NormalizationVersion: reader.GetString(24),
            PayloadSchema: reader.GetString(25),
            SchemaFingerprint: reader.GetString(26),
            CapturedAtText: reader.GetString(27),
            CreatedAtText: reader.GetString(28));
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

    private sealed record ReceiptRow(
        string SnapshotId, string RequestFingerprintSha256, string CreatedAtText, string SourceAdapter, string SourceEventId);

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
        string? TraceId,
        string? SpanId,
        string? SourceParentEventId,
        bool SourceEphemeral,
        string? BodySha256,
        long? BodyUtf8Bytes,
        string? DefinitionPathSha256,
        long? DefinitionPathUtf8Bytes,
        string SourceApplicationVersion,
        string AdapterVersion,
        string NormalizationVersion,
        string PayloadSchema,
        string SchemaFingerprint,
        string CapturedAtText,
        string CreatedAtText);

    private sealed record ContentRow(string ContentKind, string ContentJson, string CapturedAtText, string ExpiresAtText);
}

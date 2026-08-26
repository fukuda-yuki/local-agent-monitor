using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal enum SkillInvocationSnapshotMetadataOutcome
{
    Found,
    NotFound,
    Unavailable,
    Busy,
}

// A third member here would let a caller tell an inconsistent graph apart from a graph that never
// held a raw row at all; the contract folds both into the Unavailable outcome instead, so exactly
// two classifications ever reach a Found result -- this enum stays two members forever.
internal enum SkillInvocationSnapshotMetadataRetentionProjection
{
    Readable,
    UnreadableOrDeleted,
}

internal sealed record SkillInvocationSnapshotMetadataFacts(
    Guid SnapshotId,
    Guid SessionId,
    Guid EventId,
    DateTimeOffset InvokedAt,
    DateTimeOffset CapturedAt,
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    string PayloadSha256,
    string SourceEventId,
    string SourceAdapter,
    string SourceSurface,
    string? TraceId,
    string? SpanId,
    bool IsAvailable,
    string? FaultState,
    string? FaultReason,
    Guid? ClaimId,
    string? Name,
    string? Source,
    string? Trigger,
    Guid? RunId,
    string? BodySha256,
    ulong? BodyUtf8Bytes,
    string? DefinitionPathSha256,
    ulong? DefinitionPathUtf8Bytes,
    SkillInvocationSnapshotMetadataRetentionProjection RetentionProjection,
    DateTimeOffset? EffectiveExpiresAt);

internal sealed record SkillInvocationSnapshotMetadataReadResult(
    SkillInvocationSnapshotMetadataOutcome Outcome,
    SkillInvocationSnapshotMetadataFacts? Facts)
{
    internal static readonly SkillInvocationSnapshotMetadataReadResult NotFound =
        new(SkillInvocationSnapshotMetadataOutcome.NotFound, null);

    internal static readonly SkillInvocationSnapshotMetadataReadResult Unavailable =
        new(SkillInvocationSnapshotMetadataOutcome.Unavailable, null);

    internal static readonly SkillInvocationSnapshotMetadataReadResult Busy =
        new(SkillInvocationSnapshotMetadataOutcome.Busy, null);

    internal static SkillInvocationSnapshotMetadataReadResult ForFound(SkillInvocationSnapshotMetadataFacts facts) =>
        new(SkillInvocationSnapshotMetadataOutcome.Found, facts);
}

// Gate 7 and Gate 2 of docs/specifications/interfaces/skill-invocation-snapshot.md are the sole
// authority for the graph proofs and readability classification below. This reader returns
// semantic facts only: it never writes JSON, never references the document writer's types, never
// runs the #154 registry diagnostic, and never decides projection_validity/snapshot_state/
// snapshot_reason -- those stay with the route that composes this reader with the diagnostic and
// the writer.
internal static class SkillInvocationSnapshotMetadataReader
{
    private const string AvailableState = "available";
    private const string CopilotSdkSurface = "copilot-sdk";
    private const string CopilotSdkStreamAdapter = "copilot-sdk-stream";
    private const string SkillInvokedEventType = "skill.invoked";
    private const string AvailableContentState = "available";
    private const string SessionEventContentStoreKind = "session_event_content";

    internal static SkillInvocationSnapshotMetadataReadResult ReadOwnedTransaction(
        string databasePath, Guid sessionId, Guid snapshotId, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                return Core(connection, transaction, sessionId, snapshotId, timeProvider);
            }
            finally
            {
                transaction.Rollback();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillInvocationSnapshotMetadataReadResult.Busy;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or FormatException
                or OverflowException or InvalidCastException)
        {
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        }
    }

    internal static SkillInvocationSnapshotMetadataReadResult ReadInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid snapshotId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            return Core(connection, transaction, sessionId, snapshotId, timeProvider);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return SkillInvocationSnapshotMetadataReadResult.Busy;
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or FormatException
                or OverflowException or InvalidCastException)
        {
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        }
    }

    private static SkillInvocationSnapshotMetadataReadResult Core(
        SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, Guid snapshotId, TimeProvider timeProvider)
    {
        var snapshotIdText = snapshotId.ToString("D");
        var snapshot = ReadSnapshot(connection, transaction, snapshotIdText);
        if (snapshot is null)
            return SkillInvocationSnapshotMetadataReadResult.NotFound;

        // A caller cannot tell "no such snapshot" apart from "that snapshot belongs to a session I
        // did not name" without already knowing the correct session, so both collapse to the same
        // NotFound rather than confirming the ID exists under some other session.
        var sessionIdText = sessionId.ToString("D");
        if (!string.Equals(snapshot.SessionId, sessionIdText, StringComparison.Ordinal))
            return SkillInvocationSnapshotMetadataReadResult.NotFound;

        var eventRow = ReadEvent(connection, transaction, sessionIdText, snapshot.EventId);
        if (eventRow is null || !EventMatches(eventRow, snapshot)
            || !EventRunMatches(connection, transaction, snapshot.SessionId, eventRow.RunId))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        if (!NativeBindingMatches(connection, transaction, snapshot))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        if (!SessionEnvelopeMatches(connection, transaction, snapshot))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        var receipt = ReadReceipt(connection, transaction, snapshotIdText);
        if (receipt is null
            || !string.Equals(receipt.SourceAdapter, eventRow.SourceAdapter, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceEventId, eventRow.SourceEventId, StringComparison.Ordinal)
            || !string.Equals(receipt.RequestFingerprintSha256,
                ComputePersistedFingerprint(connection, transaction, snapshot, eventRow), StringComparison.Ordinal))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        if (!string.Equals(snapshot.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal)
            || !string.Equals(receipt.CreatedAtText, snapshot.CapturedAtText, StringComparison.Ordinal))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        var item = ReadRetentionItem(connection, transaction, snapshot.ContentItemId);
        if (item is null
            || !string.Equals(item.CatalogItem.OwnershipKey.SourceItemId, snapshot.EventId, StringComparison.Ordinal)
            || item.CatalogItem.CapturedAt != ParseTimestamp(snapshot.CapturedAtText))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        var content = ReadContentMetadata(connection, transaction, snapshot.EventId);
        var tombstoneExists = TombstoneExists(connection, transaction, snapshot.ContentItemId);
        if (content is not null
            ? !string.Equals(content.ContentKind, SkillInvocationSnapshotContentDocumentV1.ContentKind, StringComparison.Ordinal)
              || !string.Equals(content.CapturedAtText, snapshot.CapturedAtText, StringComparison.Ordinal)
              || !string.Equals(content.ExpiresAtText, item.ExpiresAtText, StringComparison.Ordinal)
              || item.CatalogItem.State == RetentionItemLifecycle.Deleted || tombstoneExists
            : item.CatalogItem.State != RetentionItemLifecycle.Deleted || !tombstoneExists)
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        if (snapshot.ClaimId is not null
            && ClaimCount(connection, transaction, snapshot, eventRow) != 1)
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;
        if (snapshot.ClaimId is null && FaultClaimCollisionCount(connection, transaction, snapshot, eventRow) != 0)
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        if (!IsLegalStateReasonPair(snapshot.State, snapshot.Reason))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        var isAvailable = snapshot.State == AvailableState;
        if (!AvailableFieldsAreConsistent(isAvailable, snapshot))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        // Sampled once, only to classify readability, and never folded into a returned fact: the
        // metadata contract forbids any clock value leaking into the document this reader feeds.
        var at = timeProvider.GetUtcNow();
        var readability = RetentionCatalogStore.ClassifyRowReadability(item.CatalogItem, at);
        // EXISTS proves the raw row's presence without selecting content_json: this layer proves
        // metadata facts only and must never open the document the writer alone is authorized to read.
        var contentExists = content is not null;

        SkillInvocationSnapshotMetadataRetentionProjection projection;
        if (readability == RetentionRowReadability.Readable)
        {
            if (!contentExists || tombstoneExists)
                return SkillInvocationSnapshotMetadataReadResult.Unavailable;
            projection = SkillInvocationSnapshotMetadataRetentionProjection.Readable;
        }
        else if (item.CatalogItem.State == RetentionItemLifecycle.Deleted)
        {
            if (contentExists || !tombstoneExists)
                return SkillInvocationSnapshotMetadataReadResult.Unavailable;
            projection = SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted;
        }
        else
        {
            if (!contentExists || tombstoneExists)
                return SkillInvocationSnapshotMetadataReadResult.Unavailable;
            projection = SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted;
        }

        var facts = new SkillInvocationSnapshotMetadataFacts(
            SnapshotId: snapshotId,
            SessionId: sessionId,
            EventId: Guid.Parse(snapshot.EventId),
            InvokedAt: ParseTimestamp(eventRow.OccurredAtText),
            CapturedAt: ParseTimestamp(snapshot.CapturedAtText),
            SourceApplicationVersion: snapshot.SourceApplicationVersion,
            AdapterVersion: snapshot.AdapterVersion,
            NormalizationVersion: snapshot.NormalizationVersion,
            PayloadSchema: snapshot.PayloadSchema,
            SchemaFingerprint: snapshot.SchemaFingerprint,
            PayloadSha256: snapshot.PayloadSha256,
            SourceEventId: eventRow.SourceEventId,
            SourceAdapter: eventRow.SourceAdapter,
            SourceSurface: eventRow.SourceSurface!,
            TraceId: snapshot.TraceId,
            SpanId: snapshot.SpanId,
            IsAvailable: isAvailable,
            FaultState: isAvailable ? null : snapshot.State,
            FaultReason: isAvailable ? null : snapshot.Reason,
            ClaimId: snapshot.ClaimId is null ? null : Guid.Parse(snapshot.ClaimId),
            Name: snapshot.Name,
            Source: snapshot.Source,
            Trigger: snapshot.Trigger,
            RunId: snapshot.RunId is null ? null : Guid.Parse(snapshot.RunId),
            BodySha256: snapshot.BodySha256,
            BodyUtf8Bytes: (ulong?)snapshot.BodyUtf8Bytes,
            DefinitionPathSha256: snapshot.DefinitionPathSha256,
            DefinitionPathUtf8Bytes: (ulong?)snapshot.DefinitionPathUtf8Bytes,
            RetentionProjection: projection,
            EffectiveExpiresAt: item.CatalogItem.State == RetentionItemLifecycle.Expiring
                ? item.CatalogItem.ExpiresAt
                : null);

        return SkillInvocationSnapshotMetadataReadResult.ForFound(facts);
    }

    private static bool EventMatches(EventRow row, SnapshotRow snapshot) =>
        row.Type == SkillInvokedEventType
        && row.SourceAdapter == CopilotSdkStreamAdapter
        && row.SourceSurface == CopilotSdkSurface
        && row.ContentState == AvailableContentState
        && row.Status is null
        && row.MatchKind is null
        && row.ParentEventId is null
        && row.TerminalOutcome is null
        && row.TerminalPolicyVersion is null
        && string.Equals(row.SourceApplicationVersion, snapshot.SourceApplicationVersion, StringComparison.Ordinal)
        && string.Equals(row.AdapterVersion, snapshot.AdapterVersion, StringComparison.Ordinal)
        && string.Equals(row.NormalizationVersion, snapshot.NormalizationVersion, StringComparison.Ordinal)
        && string.Equals(row.SchemaFingerprint, snapshot.SchemaFingerprint, StringComparison.Ordinal)
        && row.TraceId is null && snapshot.TraceId is null && snapshot.SpanId is null
        && (snapshot.State == AvailableState
            ? NullableTextEquals(row.RunId, snapshot.RunId)
            : snapshot.RunId is null && snapshot.TraceId is null && snapshot.SpanId is null);

    private static bool NullableTextEquals(string? left, string? right) =>
        left is null ? right is null : right is not null && string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsLegalStateReasonPair(string state, string reason) => (state, reason) switch
    {
        ("malformed", "duplicate_property" or "unknown_property" or "invalid_field_type" or "name_invalid" or "path_invalid") => true,
        ("missing", "name_missing" or "body_missing" or "definition_path_missing") => true,
        ("binary", "body_unicode_invalid" or "path_unicode_invalid") => true,
        ("oversized", "body_oversized" or "path_oversized") => true,
        ("available", "none") => true,
        _ => false,
    };

    private static bool AvailableFieldsAreConsistent(bool isAvailable, SnapshotRow snapshot) =>
        isAvailable
            ? snapshot.ClaimId is not null && snapshot.Name is not null
              && snapshot.BodySha256 is not null && snapshot.BodyUtf8Bytes is not null
              && snapshot.DefinitionPathSha256 is not null && snapshot.DefinitionPathUtf8Bytes is not null
            : snapshot.ClaimId is null && snapshot.Name is null
              && snapshot.Source is null && snapshot.Trigger is null && snapshot.RunId is null
              && snapshot.TraceId is null && snapshot.SpanId is null
              && snapshot.BodySha256 is null && snapshot.BodyUtf8Bytes is null
              && snapshot.DefinitionPathSha256 is null && snapshot.DefinitionPathUtf8Bytes is null;

    private static SnapshotRow? ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT session_id,event_id,claim_id,run_id,name,source,trigger,state,reason,content_item_id,
                   body_sha256,body_utf8_bytes,definition_path_sha256,definition_path_utf8_bytes,
                   native_session_id,trace_id,span_id,source_parent_event_id,source_ephemeral,
                   payload_sha256,payload_bytes,content_document_sha256,
                   captured_at,created_at,source_application_version,adapter_version,normalization_version,payload_schema,schema_fingerprint
            FROM skill_invocation_snapshots
            WHERE snapshot_id=$snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SnapshotRow(
            SessionId: reader.GetString(0),
            EventId: reader.GetString(1),
            ClaimId: reader.IsDBNull(2) ? null : reader.GetString(2),
            RunId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Name: reader.IsDBNull(4) ? null : reader.GetString(4),
            Source: reader.IsDBNull(5) ? null : reader.GetString(5),
            Trigger: reader.IsDBNull(6) ? null : reader.GetString(6),
            State: reader.GetString(7),
            Reason: reader.GetString(8),
            ContentItemId: reader.GetString(9),
            BodySha256: reader.IsDBNull(10) ? null : reader.GetString(10),
            BodyUtf8Bytes: reader.IsDBNull(11) ? null : reader.GetInt64(11),
            DefinitionPathSha256: reader.IsDBNull(12) ? null : reader.GetString(12),
            DefinitionPathUtf8Bytes: reader.IsDBNull(13) ? null : reader.GetInt64(13),
            NativeSessionId: reader.GetString(14),
            TraceId: reader.IsDBNull(15) ? null : reader.GetString(15),
            SpanId: reader.IsDBNull(16) ? null : reader.GetString(16),
            SourceParentEventId: reader.IsDBNull(17) ? null : reader.GetString(17),
            SourceEphemeral: reader.GetInt64(18) == 1,
            PayloadSha256: reader.GetString(19),
            PayloadBytes: reader.GetInt64(20),
            ContentDocumentSha256: reader.GetString(21),
            CapturedAtText: reader.GetString(22),
            CreatedAtText: reader.GetString(23),
            SourceApplicationVersion: reader.GetString(24),
            AdapterVersion: reader.GetString(25),
            NormalizationVersion: reader.GetString(26),
            PayloadSchema: reader.GetString(27),
            SchemaFingerprint: reader.GetString(28));
    }

    private static EventRow? ReadEvent(SqliteConnection connection, SqliteTransaction transaction, string sessionId, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT type,source_adapter,source_surface,content_state,status,match_kind,parent_event_id,
                   terminal_outcome,terminal_policy_version,occurred_at,source_application_version,
                   adapter_version,normalization_version,schema_fingerprint,source_event_id,run_id,trace_id
            FROM session_events
            WHERE session_id=$session_id AND event_id=$event_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$event_id", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new EventRow(
            Type: reader.GetString(0),
            SourceAdapter: reader.GetString(1),
            SourceSurface: reader.IsDBNull(2) ? null : reader.GetString(2),
            ContentState: reader.GetString(3),
            Status: reader.IsDBNull(4) ? null : reader.GetString(4),
            MatchKind: reader.IsDBNull(5) ? null : reader.GetString(5),
            ParentEventId: reader.IsDBNull(6) ? null : reader.GetString(6),
            TerminalOutcome: reader.IsDBNull(7) ? null : reader.GetString(7),
            TerminalPolicyVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            OccurredAtText: reader.GetString(9),
            SourceApplicationVersion: reader.GetString(10),
            AdapterVersion: reader.GetString(11),
            NormalizationVersion: reader.GetString(12),
            SchemaFingerprint: reader.GetString(13),
            SourceEventId: reader.GetString(14),
            RunId: reader.IsDBNull(15) ? null : reader.GetString(15),
            TraceId: reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private static ReceiptRow? ReadReceipt(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT source_adapter,source_event_id,request_fingerprint_sha256,created_at FROM skill_invocation_snapshot_receipts WHERE snapshot_id=$snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var row = new ReceiptRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        return reader.Read() ? null : row;
    }

    private static string ComputePersistedFingerprint(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, EventRow eventRow)
    {
        string? runNativeId = null;
        if (eventRow.RunId is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT native_run_id FROM session_runs WHERE session_id=$session_id AND run_id=$run_id;";
            command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
            command.Parameters.AddWithValue("$run_id", eventRow.RunId);
            runNativeId = command.ExecuteScalar() as string;
            if (runNativeId is null)
                throw new InvalidOperationException("skill_invocation_snapshot_run_missing");
        }

        var input = new SkillInvocationSnapshotReceiptFingerprintInput(
            eventRow.SourceAdapter, eventRow.SourceEventId, eventRow.SourceSurface!, snapshot.NativeSessionId,
            runNativeId, snapshot.SourceParentEventId, snapshot.SourceEphemeral, snapshot.TraceId, snapshot.SpanId,
            ParseTimestamp(eventRow.OccurredAtText), snapshot.SourceApplicationVersion, snapshot.AdapterVersion,
            snapshot.NormalizationVersion, snapshot.PayloadSchema, snapshot.SchemaFingerprint, snapshot.PayloadSha256,
            checked((ulong)snapshot.PayloadBytes), snapshot.State, snapshot.Reason, snapshot.Name, snapshot.Source,
            snapshot.Trigger, snapshot.BodySha256, (ulong?)snapshot.BodyUtf8Bytes, snapshot.DefinitionPathSha256,
            (ulong?)snapshot.DefinitionPathUtf8Bytes, snapshot.ContentDocumentSha256);
        return SkillInvocationSnapshotReceiptFingerprint.Compute(input);
    }

    private static long ClaimCount(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, EventRow eventRow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*) FROM skill_projection_sdk_claims
            WHERE claim_id=$claim_id AND session_id=$session_id AND event_id=$event_id
              AND source_application_version=$source_application_version AND adapter_version=$adapter_version
              AND normalization_version=$normalization_version AND payload_schema=$payload_schema
              AND schema_fingerprint=$schema_fingerprint AND source_event_id=$source_event_id
              AND source_adapter=$source_adapter AND source_surface=$source_surface
              AND payload_sha256=$payload_sha256 AND producer_trace_id IS $trace_id AND producer_span_id IS $span_id
              AND skill_name=$name AND skill_source IS $source AND invocation_trigger IS $trigger
              AND created_at=$captured_at;
            """;
        command.Parameters.AddWithValue("$claim_id", snapshot.ClaimId!);
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        command.Parameters.AddWithValue("$source_application_version", snapshot.SourceApplicationVersion);
        command.Parameters.AddWithValue("$adapter_version", snapshot.AdapterVersion);
        command.Parameters.AddWithValue("$normalization_version", snapshot.NormalizationVersion);
        command.Parameters.AddWithValue("$payload_schema", snapshot.PayloadSchema);
        command.Parameters.AddWithValue("$schema_fingerprint", snapshot.SchemaFingerprint);
        command.Parameters.AddWithValue("$source_event_id", eventRow.SourceEventId);
        command.Parameters.AddWithValue("$source_adapter", eventRow.SourceAdapter);
        command.Parameters.AddWithValue("$source_surface", eventRow.SourceSurface!);
        command.Parameters.AddWithValue("$payload_sha256", snapshot.PayloadSha256);
        command.Parameters.AddWithValue("$trace_id", snapshot.TraceId is null ? DBNull.Value : snapshot.TraceId);
        command.Parameters.AddWithValue("$span_id", snapshot.SpanId is null ? DBNull.Value : snapshot.SpanId);
        command.Parameters.AddWithValue("$name", snapshot.Name!);
        command.Parameters.AddWithValue("$source", snapshot.Source is null ? DBNull.Value : snapshot.Source);
        command.Parameters.AddWithValue("$trigger", snapshot.Trigger is null ? DBNull.Value : snapshot.Trigger);
        command.Parameters.AddWithValue("$captured_at", snapshot.CapturedAtText);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool EventRunMatches(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId, string? runId)
    {
        if (runId is null)
            return true;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT source_surface,native_run_id,
                   (SELECT COUNT(*) FROM session_runs n
                    WHERE n.session_id=r.session_id AND n.source_surface='copilot-sdk' AND n.native_run_id=r.native_run_id)
            FROM session_runs r WHERE session_id=$session_id AND run_id=$run_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() && string.Equals(reader.GetString(0), CopilotSdkSurface, StringComparison.Ordinal)
            && !reader.IsDBNull(1) && reader.GetString(1).Length > 0 && reader.GetInt64(2) == 1 && !reader.Read();
    }

    private static bool NativeBindingMatches(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot)
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
        var valid = string.Equals(reader.GetString(0), snapshot.SessionId, StringComparison.Ordinal)
            && reader.GetString(1) is "native" or "explicit_resume" or "explicit_handoff";
        return valid && !reader.Read();
    }

    private static long FaultClaimCollisionCount(
        SqliteConnection connection, SqliteTransaction transaction, SnapshotRow snapshot, EventRow eventRow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM skill_projection_sdk_claims WHERE (session_id=$session_id AND event_id=$event_id) OR (source_adapter=$adapter AND source_event_id=$source_event_id);";
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$event_id", snapshot.EventId);
        command.Parameters.AddWithValue("$adapter", eventRow.SourceAdapter);
        command.Parameters.AddWithValue("$source_event_id", eventRow.SourceEventId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
        if (reader.GetString(1) != SessionEventContentStoreKind)
            return null;

        var catalogItem = new RetentionCatalogItem(
            itemId,
            new RetentionOwnershipKey(reader.GetString(0), RetentionStoreKind.SessionEventContent, reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)),
            ParseState(reader.GetString(5)),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));
        return new RetentionItemRow(catalogItem, reader.GetString(4));
    }

    private static ContentMetadataRow? ReadContentMetadata(
        SqliteConnection connection, SqliteTransaction transaction, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_kind,captured_at,expires_at FROM session_event_content WHERE event_id=$event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var row = new ContentMetadataRow(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        return reader.Read() ? null : row;
    }

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

    private sealed record SnapshotRow(
        string SessionId,
        string EventId,
        string? ClaimId,
        string? RunId,
        string? Name,
        string? Source,
        string? Trigger,
        string State,
        string Reason,
        string ContentItemId,
        string? BodySha256,
        long? BodyUtf8Bytes,
        string? DefinitionPathSha256,
        long? DefinitionPathUtf8Bytes,
        string NativeSessionId,
        string? TraceId,
        string? SpanId,
        string? SourceParentEventId,
        bool SourceEphemeral,
        string PayloadSha256,
        long PayloadBytes,
        string ContentDocumentSha256,
        string CapturedAtText,
        string CreatedAtText,
        string SourceApplicationVersion,
        string AdapterVersion,
        string NormalizationVersion,
        string PayloadSchema,
        string SchemaFingerprint);

    private sealed record EventRow(
        string Type,
        string SourceAdapter,
        string? SourceSurface,
        string ContentState,
        string? Status,
        string? MatchKind,
        string? ParentEventId,
        string? TerminalOutcome,
        string? TerminalPolicyVersion,
        string OccurredAtText,
        string SourceApplicationVersion,
        string AdapterVersion,
        string NormalizationVersion,
        string SchemaFingerprint,
        string SourceEventId,
        string? RunId,
        string? TraceId);

    private sealed record ReceiptRow(
        string SourceAdapter, string SourceEventId, string RequestFingerprintSha256, string CreatedAtText);

    private sealed record RetentionItemRow(RetentionCatalogItem CatalogItem, string ExpiresAtText);
    private sealed record ContentMetadataRow(string ContentKind, string CapturedAtText, string ExpiresAtText);
}

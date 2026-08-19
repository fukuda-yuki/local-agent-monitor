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
    string PayloadSchema,
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
    SkillInvocationSnapshotMetadataRetentionProjection RetentionProjection);

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
        if (eventRow is null || !EventMatches(eventRow))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        if (ReceiptCount(connection, transaction, snapshotIdText) != 1)
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        var item = ReadRetentionItem(connection, transaction, snapshot.ContentItemId);
        if (item is null
            || !string.Equals(item.CatalogItem.OwnershipKey.SourceItemId, snapshot.EventId, StringComparison.Ordinal))
            return SkillInvocationSnapshotMetadataReadResult.Unavailable;

        if (snapshot.ClaimId is not null
            && ClaimCount(connection, transaction, snapshot.ClaimId, sessionIdText, snapshot.EventId) != 1)
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
        var contentExists = ContentExists(connection, transaction, snapshot.EventId);
        var tombstoneExists = TombstoneExists(connection, transaction, snapshot.ContentItemId);

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
            PayloadSchema: snapshot.PayloadSchema,
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
            RetentionProjection: projection);

        return SkillInvocationSnapshotMetadataReadResult.ForFound(facts);
    }

    private static bool EventMatches(EventRow row) =>
        row.Type == SkillInvokedEventType
        && row.SourceAdapter == CopilotSdkStreamAdapter
        && row.SourceSurface == CopilotSdkSurface
        && row.ContentState == AvailableContentState
        && row.Status is null
        && row.MatchKind is null
        && row.ParentEventId is null
        && row.TerminalOutcome is null
        && row.TerminalPolicyVersion is null;

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
                   captured_at,source_application_version,adapter_version,payload_schema
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
            CapturedAtText: reader.GetString(14),
            SourceApplicationVersion: reader.GetString(15),
            AdapterVersion: reader.GetString(16),
            PayloadSchema: reader.GetString(17));
    }

    private static EventRow? ReadEvent(SqliteConnection connection, SqliteTransaction transaction, string sessionId, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT type,source_adapter,source_surface,content_state,status,match_kind,parent_event_id,
                   terminal_outcome,terminal_policy_version,occurred_at
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
            OccurredAtText: reader.GetString(9));
    }

    private static long ReceiptCount(SqliteConnection connection, SqliteTransaction transaction, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM skill_invocation_snapshot_receipts WHERE snapshot_id=$snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ClaimCount(
        SqliteConnection connection, SqliteTransaction transaction, string claimId, string sessionId, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM skill_projection_sdk_claims WHERE claim_id=$claim_id AND session_id=$session_id AND event_id=$event_id;";
        command.Parameters.AddWithValue("$claim_id", claimId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$event_id", eventId);
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
        return new RetentionItemRow(catalogItem);
    }

    private static bool ContentExists(SqliteConnection connection, SqliteTransaction transaction, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM session_event_content WHERE event_id=$event_id);";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
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
        string CapturedAtText,
        string SourceApplicationVersion,
        string AdapterVersion,
        string PayloadSchema);

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
        string OccurredAtText);

    private sealed record RetentionItemRow(RetentionCatalogItem CatalogItem);
}

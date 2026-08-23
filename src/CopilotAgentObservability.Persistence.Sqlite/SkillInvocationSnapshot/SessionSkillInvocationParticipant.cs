using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal enum SessionSkillInvocationWriteOutcome
{
    Inserted,
    ReceiptRaced,
    SessionBindingInvalid,
    SessionAmbiguous,
    RunAmbiguous,
    EventConflict,
}

internal sealed record SessionSkillInvocationInsertedIdentity(Guid SessionId, Guid SnapshotId);

internal sealed record SessionSkillInvocationWrite(
    string SourceAdapter,
    string SourceSurface,
    string SourceEventId,
    string? SourceParentEventId,
    string NativeSessionId,
    string? RunNativeId,
    bool SourceEphemeral,
    DateTimeOffset OccurredAt,
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    ReadOnlyMemory<byte> PayloadTokenUtf8,
    string State,
    string Reason,
    string? Name,
    string? Source,
    string? Trigger,
    string? BodySha256,
    long? BodyUtf8Bytes,
    string? DefinitionPathSha256,
    long? DefinitionPathUtf8Bytes,
    Guid EventId,
    Guid SnapshotId,
    Guid? ClaimId,
    Guid NewSessionId,
    Guid NewRunId,
    DateTimeOffset WriteAt,
    DateTimeOffset ExpiresAt);

// Group 4 of docs/specifications/interfaces/skill-invocation-snapshot.md is the sole authority
// for this sequence. This participant never samples a clock or generates an identity: WriteAt is
// the caller's one already-sampled instant, and every ID on the model is caller-generated so that
// exactly one instant and one identity set land in every row this write touches.
internal static class SessionSkillInvocationParticipant
{
    private const string AvailableState = "available";
    private const string CopilotSdkSurface = "copilot-sdk";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private static readonly string[] AcceptedBindingKinds = ["native", "explicit_resume", "explicit_handoff"];

    internal static SessionSkillInvocationWriteOutcome InsertOrVerify(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionSkillInvocationWrite write) => InsertOrVerify(connection, transaction, write, out _);

    internal static SessionSkillInvocationWriteOutcome InsertOrVerify(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionSkillInvocationWrite write,
        out SessionSkillInvocationInsertedIdentity? insertedIdentity)
    {
        insertedIdentity = null;
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(write);
        Validate(write);

        if (ReceiptExists(connection, transaction, write.SourceAdapter, write.SourceEventId))
            return SessionSkillInvocationWriteOutcome.ReceiptRaced;

        var session = ResolveSession(connection, transaction, write);
        if (session.Outcome is { } sessionOutcome)
            return sessionOutcome;
        var sessionId = session.SessionId!;

        var run = ResolveRun(connection, transaction, write, sessionId);
        if (run.Outcome is { } runOutcome)
            return runOutcome;
        var runId = run.RunId;

        if (EventConflictExists(connection, transaction, write.SourceAdapter, write.SourceEventId))
            return SessionSkillInvocationWriteOutcome.EventConflict;

        InsertEvent(connection, transaction, write, sessionId, runId);

        var document = SkillInvocationSnapshotContentDocumentV1.Build(write.PayloadTokenUtf8.Span);
        var payloadSha256 = SkillInvocationSnapshotContentDocumentV1.PayloadSha256(write.PayloadTokenUtf8.Span);
        var documentSha256 = SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(document);
        var contentItemId = InsertContentAndRegisterRetention(connection, transaction, write, sessionId, runId, document);

        var isAvailable = write.State == AvailableState;
        string? claimId = null;
        if (isAvailable)
        {
            claimId = write.ClaimId!.Value.ToString("D");
            InsertClaim(connection, transaction, write, sessionId, claimId, payloadSha256);
        }

        InsertSnapshot(connection, transaction, write, sessionId, isAvailable ? runId : null, claimId, contentItemId, payloadSha256, documentSha256);
        InsertReceipt(connection, transaction, write, payloadSha256, documentSha256);

        insertedIdentity = new(Guid.Parse(sessionId), write.SnapshotId);

        return SessionSkillInvocationWriteOutcome.Inserted;
    }

    private static void Validate(SessionSkillInvocationWrite write)
    {
        var isAvailable = write.State == AvailableState;
        if ((write.ClaimId is not null) != isAvailable)
            throw new ArgumentException("ClaimId must be present exactly when State is 'available'.", nameof(write));

        if (isAvailable)
        {
            if (write.Name is null || write.BodySha256 is null || write.BodyUtf8Bytes is null
                || write.DefinitionPathSha256 is null || write.DefinitionPathUtf8Bytes is null)
                throw new ArgumentException(
                    "An 'available' classification requires Name, body, and definition-path facts.", nameof(write));
        }
        else if (write.Name is not null || write.Source is not null || write.Trigger is not null
            || write.BodySha256 is not null || write.BodyUtf8Bytes is not null
            || write.DefinitionPathSha256 is not null || write.DefinitionPathUtf8Bytes is not null)
        {
            throw new ArgumentException("A nonavailable classification must carry no Skill facts.", nameof(write));
        }

        if (FormatTimestamp(write.WriteAt).Length != 33)
            throw new ArgumentException("WriteAt must render to the canonical 33-character UTC timestamp.", nameof(write));
        if (FormatTimestamp(write.ExpiresAt).Length != 33)
            throw new ArgumentException("ExpiresAt must render to the canonical 33-character UTC timestamp.", nameof(write));
    }

    private static bool ReceiptExists(SqliteConnection connection, SqliteTransaction transaction, string sourceAdapter, string sourceEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM skill_invocation_snapshot_receipts WHERE source_adapter=$adapter AND source_event_id=$event LIMIT 1;";
        command.Parameters.AddWithValue("$adapter", sourceAdapter);
        command.Parameters.AddWithValue("$event", sourceEventId);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static bool EventConflictExists(SqliteConnection connection, SqliteTransaction transaction, string sourceAdapter, string sourceEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM session_events WHERE source_adapter=$adapter AND source_event_id=$event LIMIT 1;";
        command.Parameters.AddWithValue("$adapter", sourceAdapter);
        command.Parameters.AddWithValue("$event", sourceEventId);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static (string? SessionId, SessionSkillInvocationWriteOutcome? Outcome) ResolveSession(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write)
    {
        var rows = new List<(string SessionId, string BindingKind)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT session_id,binding_kind FROM session_native_ids WHERE source_surface='copilot-sdk' AND native_session_id=$native LIMIT 2;";
            command.Parameters.Add("$native", SqliteType.Text).Value = write.NativeSessionId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        // session_native_ids' primary key is (source_surface,native_session_id), so this exact
        // query can never observe two rows; the check is kept as the fail-closed backstop the
        // specification requires rather than proof the branch is reachable today.
        if (rows.Count > 1)
            return (null, SessionSkillInvocationWriteOutcome.SessionAmbiguous);

        if (rows.Count == 1)
        {
            var (sessionId, bindingKind) = rows[0];
            if (Array.IndexOf(AcceptedBindingKinds, bindingKind) < 0)
                return (null, SessionSkillInvocationWriteOutcome.SessionBindingInvalid);

            // Session status/completeness reduction belongs to the Session 14 owner, not this
            // participant: only last_seen_at/updated_at are bumped here (by MAX, comparing the
            // stored canonical text), and the binding's kind/identity/observed_at are untouched.
            var changed = Execute(connection, transaction,
                """
                UPDATE sessions SET
                    last_seen_at=CASE WHEN $occurred_at>last_seen_at THEN $occurred_at ELSE last_seen_at END,
                    updated_at=CASE WHEN $write_at>updated_at THEN $write_at ELSE updated_at END
                WHERE session_id=$session_id;
                """,
                ("$occurred_at", FormatTimestamp(write.OccurredAt)),
                ("$write_at", FormatTimestamp(write.WriteAt)),
                ("$session_id", sessionId));
            if (changed == 0)
                return (null, SessionSkillInvocationWriteOutcome.SessionBindingInvalid);

            return (sessionId, null);
        }

        var newSessionId = write.NewSessionId.ToString("D");
        var occurredAtText = FormatTimestamp(write.OccurredAt);
        var writeAtText = FormatTimestamp(write.WriteAt);
        Execute(connection, transaction,
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,started_at,ended_at,
                last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,'active','partial',NULL,NULL,NULL,NULL,$last_seen_at,'expiring',$created_at,$updated_at);
            """,
            ("$session_id", newSessionId), ("$last_seen_at", occurredAtText),
            ("$created_at", writeAtText), ("$updated_at", writeAtText));
        Execute(connection, transaction,
            """
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES($session_id,'copilot-sdk',$native,'native',$observed_at);
            """,
            ("$session_id", newSessionId), ("$native", write.NativeSessionId), ("$observed_at", occurredAtText));

        return (newSessionId, null);
    }

    private static (string? RunId, SessionSkillInvocationWriteOutcome? Outcome) ResolveRun(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write, string sessionId)
    {
        if (write.RunNativeId is null)
            return (null, null);

        var rows = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT run_id FROM session_runs WHERE session_id=$session AND source_surface='copilot-sdk' AND native_run_id=$native LIMIT 2;";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.Add("$native", SqliteType.Text).Value = write.RunNativeId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(reader.GetString(0));
        }

        if (rows.Count > 1)
            return (null, SessionSkillInvocationWriteOutcome.RunAmbiguous);
        if (rows.Count == 1)
            return (rows[0], null);

        var newRunId = write.NewRunId.ToString("D");
        Execute(connection, transaction,
            """
            INSERT INTO session_runs(
                run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,
                started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
            VALUES($run_id,$session_id,'copilot-sdk',$native_run_id,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');
            """,
            ("$run_id", newRunId), ("$session_id", sessionId), ("$native_run_id", write.RunNativeId));

        return (newRunId, null);
    }

    private static void InsertEvent(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write,
        string sessionId, string? runId)
    {
        // content_state='available' asserts only that the canonical raw Event content document
        // (inserted next) exists -- it says nothing about whether a current-valid Skill claim
        // exists, which is exactly why every classification, including the four fault states,
        // uses this same value here.
        Execute(connection, transaction,
            """
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                source_adapter,source_event_id,type,occurred_at,content_state,
                source_application_version,adapter_version,schema_fingerprint,normalization_version,
                match_kind,terminal_outcome,terminal_policy_version)
            VALUES(
                $event_id,$session_id,$run_id,'copilot-sdk',NULL,NULL,NULL,
                $source_adapter,$source_event_id,'skill.invoked',$occurred_at,'available',
                $source_application_version,$adapter_version,$schema_fingerprint,$normalization_version,
                NULL,NULL,NULL);
            """,
            ("$event_id", write.EventId.ToString("D")),
            ("$session_id", sessionId),
            ("$run_id", runId),
            ("$source_adapter", write.SourceAdapter),
            ("$source_event_id", write.SourceEventId),
            ("$occurred_at", FormatTimestamp(write.OccurredAt)),
            ("$source_application_version", write.SourceApplicationVersion),
            ("$adapter_version", write.AdapterVersion),
            ("$schema_fingerprint", write.SchemaFingerprint),
            ("$normalization_version", write.NormalizationVersion));
    }

    private static string InsertContentAndRegisterRetention(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write,
        string sessionId, string? runId, byte[] document)
    {
        var eventId = write.EventId.ToString("D");
        var capturedAtText = FormatTimestamp(write.WriteAt);
        var expiresAtText = FormatTimestamp(write.ExpiresAt);
        var ownerToken = RandomNumberGenerator.GetBytes(32);

        Execute(connection, transaction,
            """
            INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES($event_id,$content_kind,$content_json,$captured_at,$expires_at,$retention_owner_token);
            """,
            ("$event_id", eventId),
            ("$content_kind", SkillInvocationSnapshotContentDocumentV1.ContentKind),
            ("$content_json", Encoding.UTF8.GetString(document)),
            ("$captured_at", capturedAtText),
            ("$expires_at", expiresAtText),
            ("$retention_owner_token", ownerToken));

        // RegisterSessionEventContent samples its own internal admission clock through the
        // TimeProvider it is built with; fixing that provider at write.WriteAt -- the same
        // already-sampled instant used everywhere else -- keeps that internal check, too, off
        // wall-clock time, so it can never see the freshly captured item as already expired.
        var retentionCatalog = new RetentionCatalogStore(connection.DataSource, new FixedInstantTimeProvider(write.WriteAt));
        retentionCatalog.RegisterSessionEventContent(
            connection, transaction, eventId, SkillInvocationSnapshotContentDocumentV1.ContentKind,
            write.WriteAt, write.ExpiresAt, sessionId, runId, write.SourceAdapter, write.SourceEventId, ownerToken);

        return ReadRetentionItemId(connection, transaction, eventId);
    }

    private static string ReadRetentionItemId(SqliteConnection connection, SqliteTransaction transaction, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT item_id FROM retention_items
            WHERE store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
              AND store_kind='session_event_content'
              AND source_item_id=$event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        return (string)command.ExecuteScalar()!;
    }

    private static void InsertClaim(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write,
        string sessionId, string claimId, string payloadSha256)
    {
        var claim = new SkillProjectionSdkClaimWrite(
            claimId,
            sessionId,
            write.EventId.ToString("D"),
            write.SourceEventId,
            write.SourceAdapter,
            write.SourceSurface,
            write.SourceApplicationVersion,
            write.AdapterVersion,
            write.NormalizationVersion,
            write.PayloadSchema,
            write.SchemaFingerprint,
            payloadSha256,
            null,
            null,
            write.Name!,
            write.Source,
            write.Trigger,
            write.WriteAt);
        SkillProjectionSdkClaimParticipant.InsertOrVerify(connection, transaction, claim);
    }

    private static void InsertSnapshot(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write,
        string sessionId, string? runId, string? claimId, string contentItemId, string payloadSha256, string documentSha256)
    {
        var writeAtText = FormatTimestamp(write.WriteAt);
        Execute(connection, transaction,
            """
            INSERT INTO skill_invocation_snapshots(
                snapshot_id,session_id,native_session_id,event_id,claim_id,run_id,trace_id,span_id,
                name,source,trigger,state,reason,content_item_id,payload_sha256,payload_bytes,
                content_document_sha256,body_sha256,body_utf8_bytes,definition_path_sha256,definition_path_utf8_bytes,
                source_parent_event_id,source_ephemeral,source_application_version,adapter_version,
                normalization_version,payload_schema,schema_fingerprint,captured_at,created_at)
            VALUES(
                $snapshot_id,$session_id,$native_session_id,$event_id,$claim_id,$run_id,NULL,NULL,
                $name,$source,$trigger,$state,$reason,$content_item_id,$payload_sha256,$payload_bytes,
                $content_document_sha256,$body_sha256,$body_utf8_bytes,$definition_path_sha256,$definition_path_utf8_bytes,
                $source_parent_event_id,$source_ephemeral,$source_application_version,$adapter_version,
                $normalization_version,$payload_schema,$schema_fingerprint,$captured_at,$created_at);
            """,
            ("$snapshot_id", write.SnapshotId.ToString("D")),
            ("$session_id", sessionId),
            ("$native_session_id", write.NativeSessionId),
            ("$event_id", write.EventId.ToString("D")),
            ("$claim_id", claimId),
            ("$run_id", runId),
            ("$name", write.Name),
            ("$source", write.Source),
            ("$trigger", write.Trigger),
            ("$state", write.State),
            ("$reason", write.Reason),
            ("$content_item_id", contentItemId),
            ("$payload_sha256", payloadSha256),
            ("$payload_bytes", (long)write.PayloadTokenUtf8.Length),
            ("$content_document_sha256", documentSha256),
            ("$body_sha256", write.BodySha256),
            ("$body_utf8_bytes", write.BodyUtf8Bytes),
            ("$definition_path_sha256", write.DefinitionPathSha256),
            ("$definition_path_utf8_bytes", write.DefinitionPathUtf8Bytes),
            ("$source_parent_event_id", write.SourceParentEventId),
            ("$source_ephemeral", write.SourceEphemeral ? 1L : 0L),
            ("$source_application_version", write.SourceApplicationVersion),
            ("$adapter_version", write.AdapterVersion),
            ("$normalization_version", write.NormalizationVersion),
            ("$payload_schema", write.PayloadSchema),
            ("$schema_fingerprint", write.SchemaFingerprint),
            ("$captured_at", writeAtText),
            ("$created_at", writeAtText));
    }

    private static void InsertReceipt(
        SqliteConnection connection, SqliteTransaction transaction, SessionSkillInvocationWrite write,
        string payloadSha256, string documentSha256)
    {
        var fingerprintInput = new SkillInvocationSnapshotReceiptFingerprintInput(
            SourceAdapter: write.SourceAdapter,
            SourceEventId: write.SourceEventId,
            SourceSurface: write.SourceSurface,
            NativeSessionId: write.NativeSessionId,
            RunNativeId: write.RunNativeId,
            SourceParentEventId: write.SourceParentEventId,
            SourceEphemeral: write.SourceEphemeral,
            TraceId: null,
            SpanId: null,
            OccurredAt: write.OccurredAt,
            SourceApplicationVersion: write.SourceApplicationVersion,
            AdapterVersion: write.AdapterVersion,
            NormalizationVersion: write.NormalizationVersion,
            PayloadSchema: write.PayloadSchema,
            SchemaFingerprint: write.SchemaFingerprint,
            PayloadSha256: payloadSha256,
            PayloadBytes: (ulong)write.PayloadTokenUtf8.Length,
            State: write.State,
            Reason: write.Reason,
            Name: write.Name,
            Source: write.Source,
            Trigger: write.Trigger,
            BodySha256: write.BodySha256,
            BodyUtf8Bytes: (ulong?)write.BodyUtf8Bytes,
            DefinitionPathSha256: write.DefinitionPathSha256,
            DefinitionPathUtf8Bytes: (ulong?)write.DefinitionPathUtf8Bytes,
            ContentDocumentSha256: documentSha256);
        var fingerprint = SkillInvocationSnapshotReceiptFingerprint.Compute(fingerprintInput);

        Execute(connection, transaction,
            """
            INSERT INTO skill_invocation_snapshot_receipts(source_adapter,source_event_id,snapshot_id,request_fingerprint_sha256,created_at)
            VALUES($source_adapter,$source_event_id,$snapshot_id,$fingerprint,$created_at);
            """,
            ("$source_adapter", write.SourceAdapter),
            ("$source_event_id", write.SourceEventId),
            ("$snapshot_id", write.SnapshotId.ToString("D")),
            ("$fingerprint", fingerprint),
            ("$created_at", FormatTimestamp(write.WriteAt)));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static int Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private sealed class FixedInstantTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}

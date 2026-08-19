using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal enum SkillInvocationRetentionProjectionV1
{
    None,
    Readable,
    RetainedDeletedOrTombstoned,
    Inconsistent
}

internal enum SkillInvocationMetadataDocumentOutcomeV1
{
    Document,
    NotFound,
    Unavailable
}

internal abstract record SkillInvocationMetadataPersistedSnapshotV1
{
    private SkillInvocationMetadataPersistedSnapshotV1()
    {
    }

    internal sealed record Available(
        Guid ClaimId,
        string Name,
        string? Source,
        string? Trigger,
        Guid? RunId,
        string BodySha256,
        ulong BodyUtf8Bytes,
        string DefinitionPathSha256,
        ulong DefinitionPathUtf8Bytes) : SkillInvocationMetadataPersistedSnapshotV1;

    internal sealed record Fault : SkillInvocationMetadataPersistedSnapshotV1
    {
        internal Fault(SkillInvocationPayloadState state, SkillInvocationPayloadReason reason)
        {
            if (state == SkillInvocationPayloadState.Available)
            {
                throw new ArgumentException("A fault snapshot cannot carry the available payload state.", nameof(state));
            }

            State = state;
            Reason = reason;
        }

        internal SkillInvocationPayloadState State { get; }

        internal SkillInvocationPayloadReason Reason { get; }
    }
}

internal sealed record SkillInvocationMetadataDerivedStateV1
{
    private SkillInvocationMetadataDerivedStateV1(
        SkillInvocationMetadataDocumentOutcomeV1 outcome,
        string? projectionValidity,
        string? snapshotState,
        string? snapshotReason)
    {
        Outcome = outcome;
        ProjectionValidity = projectionValidity;
        SnapshotState = snapshotState;
        SnapshotReason = snapshotReason;
    }

    internal SkillInvocationMetadataDocumentOutcomeV1 Outcome { get; }

    internal string? ProjectionValidity { get; }

    internal string? SnapshotState { get; }

    internal string? SnapshotReason { get; }

    internal static SkillInvocationMetadataDerivedStateV1 ForDocument(
        string projectionValidity, string snapshotState, string snapshotReason) =>
        new(SkillInvocationMetadataDocumentOutcomeV1.Document, projectionValidity, snapshotState, snapshotReason);

    internal static readonly SkillInvocationMetadataDerivedStateV1 NotFound =
        new(SkillInvocationMetadataDocumentOutcomeV1.NotFound, null, null, null);

    internal static readonly SkillInvocationMetadataDerivedStateV1 Unavailable =
        new(SkillInvocationMetadataDocumentOutcomeV1.Unavailable, null, null, null);
}

internal sealed record SkillInvocationMetadataDocumentV1Input(
    Guid SnapshotId,
    Guid SessionId,
    Guid EventId,
    DateTimeOffset InvokedAt,
    SkillInvocationMetadataPersistedSnapshotV1? PersistedSnapshot,
    SkillInvocationRetentionProjectionV1 Retention,
    string? DiagnosticToken,
    DateTimeOffset CapturedAt,
    string SourceApplicationVersion,
    string AdapterVersion,
    string PayloadSchema);

internal sealed record SkillInvocationMetadataDocumentV1Response(int StatusCode, byte[] BodyUtf8);

internal static class SkillInvocationMetadataDocumentV1
{
    internal const string SchemaVersion = "local-skill-invocation-snapshot.metadata.v1";
    internal const string ContentType = "application/json; charset=utf-8";
    internal const string CacheControl = "no-store";

    internal const string NotFoundErrorToken = "skill_snapshot_not_found";
    internal const string UnavailableErrorToken = "local_monitor_ui_unavailable";

    internal const string DiagnosticCurrent = "current";
    internal const string DiagnosticStale = "stale";
    internal const string DiagnosticInvalid = "invalid";

    private const string SnapshotStateAvailable = "available";
    private const string SnapshotStateExpired = "expired";
    private const string SnapshotReasonNone = "none";

    private const int StatusOk = 200;
    private const int StatusNotFound = 404;
    private const int StatusUnavailable = 503;

    // The literal +00:00 suffix (not the zzz specifier) enforces the closed r0001 contract:
    // any offset other than zero must fail loudly in FormatTimestamp rather than silently
    // serialize a different, non-conformant suffix.
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";

    internal static SkillInvocationMetadataDocumentV1Response Write(SkillInvocationMetadataDocumentV1Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var derived = DeriveState(input.PersistedSnapshot, input.Retention, input.DiagnosticToken);
        return derived.Outcome switch
        {
            SkillInvocationMetadataDocumentOutcomeV1.NotFound =>
                new SkillInvocationMetadataDocumentV1Response(StatusNotFound, SkillInvocationJsonWriterV1.WriteErrorEntity(NotFoundErrorToken)),
            SkillInvocationMetadataDocumentOutcomeV1.Unavailable =>
                new SkillInvocationMetadataDocumentV1Response(StatusUnavailable, SkillInvocationJsonWriterV1.WriteErrorEntity(UnavailableErrorToken)),
            _ => new SkillInvocationMetadataDocumentV1Response(StatusOk, WriteDocument(input, derived))
        };
    }

    internal static SkillInvocationMetadataDerivedStateV1 DeriveState(
        SkillInvocationMetadataPersistedSnapshotV1? persistedSnapshot,
        SkillInvocationRetentionProjectionV1 retention,
        string? diagnosticToken)
    {
        // Spec row 6 (any inconsistent component/parent/Retention/tombstone/#154 graph) outranks
        // every other row: a broken graph forces 503 even when a persisted snapshot happens to be
        // present, because the spec forbids leaking any partial metadata in that case.
        if (retention == SkillInvocationRetentionProjectionV1.Inconsistent)
        {
            return SkillInvocationMetadataDerivedStateV1.Unavailable;
        }

        if (persistedSnapshot is null)
        {
            return retention == SkillInvocationRetentionProjectionV1.None
                ? SkillInvocationMetadataDerivedStateV1.NotFound
                : SkillInvocationMetadataDerivedStateV1.Unavailable;
        }

        if (retention == SkillInvocationRetentionProjectionV1.None)
        {
            return SkillInvocationMetadataDerivedStateV1.Unavailable;
        }

        var isExpired = retention == SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned;

        if (persistedSnapshot is SkillInvocationMetadataPersistedSnapshotV1.Available)
        {
            return SkillInvocationMetadataDerivedStateV1.ForDocument(
                RequireDiagnosticToken(diagnosticToken),
                isExpired ? SnapshotStateExpired : SnapshotStateAvailable,
                SnapshotReasonNone);
        }

        // A fault row never survives to have a claim, so there is nothing for the #154 diagnostic
        // to run against; projection_validity is always the closed "invalid" token here regardless
        // of whatever diagnosticToken a caller supplies (Gate 7's "not applicable: no claim" cells).
        var fault = (SkillInvocationMetadataPersistedSnapshotV1.Fault)persistedSnapshot;
        return SkillInvocationMetadataDerivedStateV1.ForDocument(
            DiagnosticInvalid,
            isExpired ? SnapshotStateExpired : PersistedStateToken(fault.State),
            PersistedReasonToken(fault.Reason));
    }

    private static string RequireDiagnosticToken(string? diagnosticToken) => diagnosticToken switch
    {
        DiagnosticCurrent or DiagnosticStale or DiagnosticInvalid => diagnosticToken,
        _ => throw new ArgumentException(
            "An available snapshot requires the same current/stale/invalid #154 diagnostic token as projection_validity.",
            nameof(diagnosticToken))
    };

    private static string PersistedStateToken(SkillInvocationPayloadState state) => state switch
    {
        SkillInvocationPayloadState.Malformed => "malformed",
        SkillInvocationPayloadState.Missing => "missing",
        SkillInvocationPayloadState.Binary => "binary",
        SkillInvocationPayloadState.Oversized => "oversized",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unrecognized fault snapshot state.")
    };

    private static string PersistedReasonToken(SkillInvocationPayloadReason reason) => reason switch
    {
        SkillInvocationPayloadReason.DuplicateProperty => "duplicate_property",
        SkillInvocationPayloadReason.UnknownProperty => "unknown_property",
        SkillInvocationPayloadReason.InvalidFieldType => "invalid_field_type",
        SkillInvocationPayloadReason.NameInvalid => "name_invalid",
        SkillInvocationPayloadReason.PathInvalid => "path_invalid",
        SkillInvocationPayloadReason.NameMissing => "name_missing",
        SkillInvocationPayloadReason.BodyMissing => "body_missing",
        SkillInvocationPayloadReason.DefinitionPathMissing => "definition_path_missing",
        SkillInvocationPayloadReason.BodyUnicodeInvalid => "body_unicode_invalid",
        SkillInvocationPayloadReason.PathUnicodeInvalid => "path_unicode_invalid",
        SkillInvocationPayloadReason.BodyOversized => "body_oversized",
        SkillInvocationPayloadReason.PathOversized => "path_oversized",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unrecognized fault snapshot reason.")
    };

    private static byte[] WriteDocument(SkillInvocationMetadataDocumentV1Input input, SkillInvocationMetadataDerivedStateV1 derived)
    {
        var available = input.PersistedSnapshot as SkillInvocationMetadataPersistedSnapshotV1.Available;
        var projectionValidity = derived.ProjectionValidity ?? throw new InvalidOperationException("A document outcome always carries projection_validity.");
        var snapshotState = derived.SnapshotState ?? throw new InvalidOperationException("A document outcome always carries snapshot_state.");
        var snapshotReason = derived.SnapshotReason ?? throw new InvalidOperationException("A document outcome always carries snapshot_reason.");

        return SkillInvocationJsonWriterV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("snapshot_id", input.SnapshotId.ToString("D"));
            writer.WriteString("session_id", input.SessionId.ToString("D"));
            WriteNullableGuid(writer, "claim_id", available?.ClaimId);
            writer.WriteString("event_id", input.EventId.ToString("D"));
            WriteNullableString(writer, "name", available?.Name);
            WriteNullableString(writer, "source", available?.Source);
            WriteNullableString(writer, "trigger", available?.Trigger);
            writer.WriteString("invoked_at", FormatTimestamp(input.InvokedAt));
            WriteNullableGuid(writer, "run_id", available?.RunId);
            writer.WriteNull("trace_id");
            writer.WriteNull("span_id");
            writer.WriteString("projection_validity", projectionValidity);
            writer.WriteString("snapshot_state", snapshotState);
            writer.WriteString("snapshot_reason", snapshotReason);
            WriteNullableString(writer, "body_sha256", available?.BodySha256);
            WriteNullableUnsignedNumber(writer, "body_utf8_bytes", available?.BodyUtf8Bytes);
            WriteNullableString(writer, "definition_path_sha256", available?.DefinitionPathSha256);
            WriteNullableUnsignedNumber(writer, "definition_path_utf8_bytes", available?.DefinitionPathUtf8Bytes);
            writer.WriteString("captured_at", FormatTimestamp(input.CapturedAt));
            writer.WriteString("source_application_version", input.SourceApplicationVersion);
            writer.WriteString("adapter_version", input.AdapterVersion);
            writer.WriteString("payload_schema", input.PayloadSchema);
            writer.WriteEndObject();
        });
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableGuid(Utf8JsonWriter writer, string propertyName, Guid? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.Value.ToString("D"));
        }
    }

    private static void WriteNullableUnsignedNumber(Utf8JsonWriter writer, string propertyName, ulong? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            SkillInvocationJsonWriterV1.WriteUnsignedNumber(writer, propertyName, value.Value);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "r0001 timestamps are UTC-only; a nonzero offset cannot serialize as the fixed +00:00 suffix.",
                nameof(value));
        }

        return value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }
}

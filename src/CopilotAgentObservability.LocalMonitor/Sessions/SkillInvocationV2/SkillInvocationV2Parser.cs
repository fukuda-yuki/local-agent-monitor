using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public static class SkillInvocationV2Parser
{
    private const string SourceAdapter = "copilot-sdk-stream";
    private const string SourceSurface = "copilot-sdk";
    private const string SourceApplicationVersion = "1.0.65";
    private const string AdapterVersion = "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1";
    private const string NormalizationVersion = "github-copilot-sdk.skill-invoked.normalize.v1";
    private const string PayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private const string SchemaFingerprint = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
    private const string EventType = "skill.invoked";

    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "source_adapter",
        "source_surface",
        "native_session_id",
        "source_application_version",
        "adapter_version",
        "normalization_version",
        "payload_schema",
        "schema_fingerprint",
        "events"
    };

    private static readonly HashSet<string> EventProperties = new(StringComparer.Ordinal)
    {
        "source_event_id",
        "source_parent_event_id",
        "type",
        "occurred_at",
        "run_native_id",
        "source_ephemeral",
        "trace_id",
        "span_id",
        "payload"
    };

    private static readonly HashSet<string> PayloadProperties = new(StringComparer.Ordinal)
    {
        "name",
        "path",
        "content",
        "allowedTools",
        "description",
        "pluginName",
        "pluginVersion",
        "source",
        "trigger"
    };

    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "project",
        "inherited",
        "personal-copilot",
        "personal-agents",
        "custom",
        "plugin",
        "builtin",
        "remote"
    };

    private static readonly HashSet<string> Triggers = new(StringComparer.Ordinal)
    {
        "user-invoked",
        "agent-invoked",
        "context-load"
    };

    public static ParsedSkillInvocationV2Batch Parse(
        ReadOnlySpan<byte> requestUtf8,
        ISkillInvocationV2RuntimeCapability runtimeCapability)
    {
        ArgumentNullException.ThrowIfNull(runtimeCapability);

        var state = new ParseState();
        try
        {
            var reader = new Utf8JsonReader(requestUtf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });

            if (!reader.Read())
            {
                state.OuterInvalid = true;
            }
            else if (reader.TokenType != JsonTokenType.StartObject)
            {
                state.OuterInvalid = true;
                ConsumeValue(ref reader, state);
            }
            else
            {
                ReadEnvelope(ref reader, state);
            }

            while (reader.Read())
            {
                state.OuterInvalid = true;
                ConsumeValue(ref reader, state);
            }
        }
        catch (JsonException)
        {
            throw InvalidRequest();
        }
        catch (InvalidOperationException)
        {
            throw InvalidRequest();
        }

        if (state.OuterInvalid || state.Payloads.Count != 1)
        {
            throw InvalidRequest();
        }

        var envelope = state.Payloads[0].Build(requestUtf8);
        return new ParsedSkillInvocationV2Batch([envelope], runtimeCapability, state.NativeSessionId!);
    }

    private static void ReadEnvelope(ref Utf8JsonReader reader, ParseState state)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                state.OuterInvalid = true;
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!TryDecodeString(ref reader, state, out var propertyName, out _))
            {
                state.OuterInvalid = true;
                if (!reader.Read()) throw InvalidRequest();
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!seen.Add(propertyName) || !EnvelopeProperties.Contains(propertyName))
            {
                state.OuterInvalid = true;
            }

            if (!reader.Read()) throw InvalidRequest();
            switch (propertyName)
            {
                case "schema_version":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var schemaVersion) || schemaVersion != 2)
                    {
                        state.OuterInvalid = true;
                        ConsumeValue(ref reader, state);
                    }
                    break;
                case "source_adapter":
                    RequireExactString(ref reader, state, SourceAdapter);
                    break;
                case "source_surface":
                    RequireExactString(ref reader, state, SourceSurface);
                    break;
                case "native_session_id":
                    RequireBoundedIdentity(ref reader, state, nullable: false, out var nativeSessionId);
                    state.NativeSessionId = nativeSessionId;
                    break;
                case "source_application_version":
                    RequireExactString(ref reader, state, SourceApplicationVersion);
                    break;
                case "adapter_version":
                    RequireExactString(ref reader, state, AdapterVersion);
                    break;
                case "normalization_version":
                    RequireExactString(ref reader, state, NormalizationVersion);
                    break;
                case "payload_schema":
                    RequireExactString(ref reader, state, PayloadSchema);
                    break;
                case "schema_fingerprint":
                    RequireExactString(ref reader, state, SchemaFingerprint);
                    break;
                case "events":
                    ReadEvents(ref reader, state);
                    break;
                default:
                    ConsumeValue(ref reader, state);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || seen.Count != EnvelopeProperties.Count || !seen.SetEquals(EnvelopeProperties))
        {
            state.OuterInvalid = true;
        }
    }

    private static void ReadEvents(ref Utf8JsonReader reader, ParseState state)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            state.OuterInvalid = true;
            ConsumeValue(ref reader, state);
            return;
        }

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            count++;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                state.OuterInvalid = true;
                ConsumeValue(ref reader, state);
                continue;
            }

            ReadEvent(ref reader, state);
        }

        if (reader.TokenType != JsonTokenType.EndArray || count != 1)
        {
            state.OuterInvalid = true;
        }
    }

    private static void ReadEvent(ref Utf8JsonReader reader, ParseState state)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? sourceEventId = null;
        string? sourceParentEventId = null;
        DateTimeOffset occurredAt = default;
        string? runNativeId = null;
        var sourceEphemeral = false;
        PayloadCandidate? payloadCandidate = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                state.OuterInvalid = true;
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!TryDecodeString(ref reader, state, out var propertyName, out _))
            {
                state.OuterInvalid = true;
                if (!reader.Read()) throw InvalidRequest();
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!seen.Add(propertyName) || !EventProperties.Contains(propertyName))
            {
                state.OuterInvalid = true;
            }

            if (!reader.Read()) throw InvalidRequest();
            switch (propertyName)
            {
                case "source_event_id":
                    RequireUuidV4(ref reader, state, nullable: false, out sourceEventId);
                    break;
                case "source_parent_event_id":
                    RequireUuidV4(ref reader, state, nullable: true, out sourceParentEventId);
                    break;
                case "type":
                    RequireExactString(ref reader, state, EventType);
                    break;
                case "occurred_at":
                    RequireTimestamp(ref reader, state, out occurredAt);
                    break;
                case "run_native_id":
                    RequireBoundedIdentity(ref reader, state, nullable: true, out runNativeId);
                    break;
                case "source_ephemeral":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                    {
                        sourceEphemeral = reader.TokenType == JsonTokenType.True;
                    }
                    else
                    {
                        state.OuterInvalid = true;
                        ConsumeValue(ref reader, state);
                    }
                    break;
                case "trace_id":
                case "span_id":
                    if (reader.TokenType != JsonTokenType.Null)
                    {
                        state.OuterInvalid = true;
                        ConsumeValue(ref reader, state);
                    }
                    break;
                case "payload":
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        state.OuterInvalid = true;
                        ConsumeValue(ref reader, state);
                    }
                    else
                    {
                        payloadCandidate = ReadPayload(ref reader, state);
                        state.Payloads.Add(payloadCandidate);
                    }
                    break;
                default:
                    ConsumeValue(ref reader, state);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || seen.Count != EventProperties.Count || !seen.SetEquals(EventProperties))
        {
            state.OuterInvalid = true;
        }

        if (payloadCandidate is not null)
        {
            payloadCandidate.SourceEventId = sourceEventId;
            payloadCandidate.SourceParentEventId = sourceParentEventId;
            payloadCandidate.OccurredAt = occurredAt;
            payloadCandidate.RunNativeId = runNativeId;
            payloadCandidate.SourceEphemeral = sourceEphemeral;
        }
    }

    private static PayloadCandidate ReadPayload(ref Utf8JsonReader reader, ParseState state)
    {
        var candidate = new PayloadCandidate(checked((int)reader.TokenStartIndex));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                candidate.InvalidFieldType = true;
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!TryDecodeString(ref reader, state, out var propertyName, out _))
            {
                candidate.UnknownProperty = true;
                if (!reader.Read()) throw InvalidRequest();
                ConsumeValue(ref reader, state);
                continue;
            }

            if (!seen.Add(propertyName))
            {
                candidate.DuplicateProperty = true;
            }

            if (!PayloadProperties.Contains(propertyName))
            {
                candidate.UnknownProperty = true;
            }

            if (!reader.Read()) throw InvalidRequest();
            switch (propertyName)
            {
                case "name":
                    candidate.NamePresent = true;
                    ReadName(ref reader, state, candidate);
                    break;
                case "path":
                    candidate.PathPresent = true;
                    ReadPath(ref reader, state, candidate);
                    break;
                case "content":
                    candidate.BodyPresent = true;
                    ReadBody(ref reader, state, candidate);
                    break;
                case "allowedTools":
                    ReadAllowedTools(ref reader, state, candidate);
                    break;
                case "description":
                    ReadBoundedOptionalString(ref reader, state, candidate, 4_096, 16_384);
                    break;
                case "pluginName":
                case "pluginVersion":
                    ReadBoundedOptionalString(ref reader, state, candidate, int.MaxValue, 256);
                    break;
                case "source":
                    ReadClosedOptionalString(ref reader, state, candidate, Sources, isSource: true);
                    break;
                case "trigger":
                    ReadClosedOptionalString(ref reader, state, candidate, Triggers, isSource: false);
                    break;
                default:
                    ConsumeValue(ref reader, state);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw InvalidRequest();
        }

        candidate.End = checked((int)reader.BytesConsumed);
        return candidate;
    }

    private static void ReadName(ref Utf8JsonReader reader, ParseState state, PayloadCandidate candidate)
    {
        if (!TryDecodeString(ref reader, state, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                candidate.NameInvalid = true;
            }
            else
            {
                candidate.InvalidFieldType = true;
                ConsumeValue(ref reader, state);
            }
            return;
        }

        candidate.Name = value;
        var scalarCount = 0;
        var invalidScalar = false;
        foreach (var rune in value.EnumerateRunes())
        {
            scalarCount++;
            invalidScalar |= Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control || IsNoncharacter(rune.Value);
        }

        if (scalarCount is < 1 or > 200 || Encoding.UTF8.GetByteCount(value) > 800 || invalidScalar)
        {
            candidate.NameInvalid = true;
        }
    }

    private static void ReadPath(ref Utf8JsonReader reader, ParseState state, PayloadCandidate candidate)
    {
        if (!TryDecodeString(ref reader, state, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                candidate.PathUnicodeInvalid = true;
            }
            else
            {
                candidate.InvalidFieldType = true;
                ConsumeValue(ref reader, state);
            }
            return;
        }

        candidate.Path = value;
        if (value.Length == 0 || value.EnumerateRunes().Any(rune => rune.Value <= 0x1f || rune.Value == 0x7f))
        {
            candidate.PathInvalid = true;
        }

        if (Encoding.UTF8.GetByteCount(value) > 4_096)
        {
            candidate.PathOversized = true;
        }
    }

    private static void ReadBody(ref Utf8JsonReader reader, ParseState state, PayloadCandidate candidate)
    {
        if (!TryDecodeString(ref reader, state, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                candidate.BodyUnicodeInvalid = true;
            }
            else
            {
                candidate.InvalidFieldType = true;
                ConsumeValue(ref reader, state);
            }
            return;
        }

        candidate.Body = value;
        if (Encoding.UTF8.GetByteCount(value) > 1_048_576)
        {
            candidate.BodyOversized = true;
        }
    }

    private static void ReadAllowedTools(ref Utf8JsonReader reader, ParseState state, PayloadCandidate candidate)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            candidate.InvalidFieldType = true;
            ConsumeValue(ref reader, state);
            return;
        }

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            count++;
            if (!TryDecodeString(ref reader, state, out var value, out _))
            {
                candidate.InvalidFieldType = true;
                ConsumeValue(ref reader, state);
                continue;
            }

            var scalarCount = value.EnumerateRunes().Count();
            if (scalarCount is < 1 or > 128 || Encoding.UTF8.GetByteCount(value) > 512)
            {
                candidate.InvalidFieldType = true;
            }
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw InvalidRequest();
        }

        if (count > 64)
        {
            candidate.InvalidFieldType = true;
        }
    }

    private static void ReadBoundedOptionalString(
        ref Utf8JsonReader reader,
        ParseState state,
        PayloadCandidate candidate,
        int maximumScalars,
        int maximumBytes)
    {
        if (!TryDecodeString(ref reader, state, out var value, out _))
        {
            candidate.InvalidFieldType = true;
            ConsumeValue(ref reader, state);
            return;
        }

        if (value.EnumerateRunes().Count() > maximumScalars || Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            candidate.InvalidFieldType = true;
        }
    }

    private static void ReadClosedOptionalString(
        ref Utf8JsonReader reader,
        ParseState state,
        PayloadCandidate candidate,
        HashSet<string> accepted,
        bool isSource)
    {
        if (!TryDecodeString(ref reader, state, out var value, out _) || !accepted.Contains(value))
        {
            candidate.InvalidFieldType = true;
            ConsumeValue(ref reader, state);
            return;
        }

        if (isSource)
        {
            candidate.Source = value;
        }
        else
        {
            candidate.Trigger = value;
        }
    }

    private static void RequireExactString(ref Utf8JsonReader reader, ParseState state, string expected)
    {
        if (!TryDecodeString(ref reader, state, out var value, out _) || !string.Equals(value, expected, StringComparison.Ordinal))
        {
            state.OuterInvalid = true;
            ConsumeValue(ref reader, state);
        }
    }

    private static void RequireBoundedIdentity(ref Utf8JsonReader reader, ParseState state, bool nullable, out string? value)
    {
        value = null;
        if (nullable && reader.TokenType == JsonTokenType.Null)
        {
            return;
        }

        if (!TryDecodeString(ref reader, state, out var decoded, out _)
            || decoded.EnumerateRunes().Count() is < 1 or > 256
            || decoded.Contains('\0', StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(decoded) > 1_024)
        {
            state.OuterInvalid = true;
            ConsumeValue(ref reader, state);
            return;
        }

        value = decoded;
    }

    private static void RequireUuidV4(ref Utf8JsonReader reader, ParseState state, bool nullable, out string? value)
    {
        value = null;
        if (nullable && reader.TokenType == JsonTokenType.Null)
        {
            return;
        }

        if (!TryDecodeString(ref reader, state, out var decoded, out _)
            || decoded.Length != 36
            || !Guid.TryParseExact(decoded, "D", out var uuid)
            || !string.Equals(uuid.ToString("D", CultureInfo.InvariantCulture), decoded, StringComparison.Ordinal)
            || decoded[14] != '4'
            || decoded[19] is not ('8' or '9' or 'a' or 'b'))
        {
            state.OuterInvalid = true;
            ConsumeValue(ref reader, state);
            return;
        }

        value = decoded;
    }

    private static void RequireTimestamp(ref Utf8JsonReader reader, ParseState state, out DateTimeOffset value)
    {
        value = default;
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";
        if (!TryDecodeString(ref reader, state, out var decoded, out _)
            || decoded.Length != 33
            || !DateTimeOffset.TryParseExact(decoded, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            || timestamp.Offset != TimeSpan.Zero
            || !string.Equals(timestamp.ToString(format, CultureInfo.InvariantCulture), decoded, StringComparison.Ordinal))
        {
            state.OuterInvalid = true;
            ConsumeValue(ref reader, state);
            return;
        }

        value = timestamp;
    }

    private static bool TryDecodeString(
        ref Utf8JsonReader reader,
        ParseState state,
        out string value,
        out bool unicodeInvalid)
    {
        value = string.Empty;
        unicodeInvalid = false;
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName))
        {
            return false;
        }

        if (!IsValidUtf8(reader.ValueSpan))
        {
            state.OuterInvalid = true;
            return false;
        }

        try
        {
            value = reader.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            unicodeInvalid = true;
            return false;
        }
    }

    private static void ConsumeValue(ref Utf8JsonReader reader, ParseState state)
    {
        ValidateRawStringToken(ref reader, state);
        if (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
        {
            return;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            ValidateRawStringToken(ref reader, state);
            if (reader.CurrentDepth == depth && reader.TokenType is (JsonTokenType.EndArray or JsonTokenType.EndObject))
            {
                return;
            }
        }

        throw InvalidRequest();
    }

    private static void ValidateRawStringToken(ref Utf8JsonReader reader, ParseState state)
    {
        if (reader.TokenType is (JsonTokenType.String or JsonTokenType.PropertyName) && !IsValidUtf8(reader.ValueSpan))
        {
            state.OuterInvalid = true;
        }
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            bytes = bytes[consumed..];
        }

        return true;
    }

    private static bool IsNoncharacter(int scalar) =>
        scalar is >= 0xfdd0 and <= 0xfdef || (scalar & 0xffff) is 0xfffe or 0xffff;

    private static JsonException InvalidRequest() => new("Invalid skill invocation v2 request.");

    private sealed class ParseState
    {
        public bool OuterInvalid { get; set; }

        public List<PayloadCandidate> Payloads { get; } = [];

        public string? NativeSessionId { get; set; }
    }

    private sealed class PayloadCandidate(int start)
    {
        public int Start { get; } = start;

        public int End { get; set; }

        public bool DuplicateProperty { get; set; }

        public bool UnknownProperty { get; set; }

        public bool InvalidFieldType { get; set; }

        public bool NameInvalid { get; set; }

        public bool PathInvalid { get; set; }

        public bool NamePresent { get; set; }

        public bool BodyPresent { get; set; }

        public bool PathPresent { get; set; }

        public bool BodyUnicodeInvalid { get; set; }

        public bool PathUnicodeInvalid { get; set; }

        public bool BodyOversized { get; set; }

        public bool PathOversized { get; set; }

        public string? Name { get; set; }

        public string? Source { get; set; }

        public string? Trigger { get; set; }

        public string? Body { get; set; }

        public string? Path { get; set; }

        public string? SourceEventId { get; set; }

        public string? SourceParentEventId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string? RunNativeId { get; set; }

        public bool SourceEphemeral { get; set; }

        public SkillInvocationV2AcceptedEnvelope Build(ReadOnlySpan<byte> requestUtf8)
        {
            var (state, reason) = Classify();
            SkillInvocationV2ParsedClaimFacts? facts = null;
            if (state == SkillInvocationV2PayloadState.Available)
            {
                facts = new SkillInvocationV2ParsedClaimFacts(
                    Name!,
                    Source,
                    Trigger,
                    new SkillInvocationV2TextEvidence(Body!),
                    new SkillInvocationV2TextEvidence(Path!));
            }

            var identity = new SkillInvocationV2EventIdentity(
                SourceEventId!,
                SourceParentEventId,
                OccurredAt,
                RunNativeId,
                SourceEphemeral,
                traceId: null,
                spanId: null);

            return new SkillInvocationV2AcceptedEnvelope(
                new SkillInvocationV2RawPayloadEvidence(requestUtf8[Start..End]),
                state,
                reason,
                facts,
                identity);
        }

        private (SkillInvocationV2PayloadState State, SkillInvocationV2PayloadReason Reason) Classify()
        {
            if (DuplicateProperty) return (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.DuplicateProperty);
            if (UnknownProperty) return (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.UnknownProperty);
            if (InvalidFieldType) return (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.InvalidFieldType);
            if (NameInvalid) return (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.NameInvalid);
            if (PathInvalid) return (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.PathInvalid);
            if (!NamePresent) return (SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.NameMissing);
            if (!BodyPresent) return (SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.BodyMissing);
            if (!PathPresent) return (SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.DefinitionPathMissing);
            if (BodyUnicodeInvalid) return (SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.BodyUnicodeInvalid);
            if (PathUnicodeInvalid) return (SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.PathUnicodeInvalid);
            if (BodyOversized) return (SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.BodyOversized);
            if (PathOversized) return (SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.PathOversized);
            return (SkillInvocationV2PayloadState.Available, SkillInvocationV2PayloadReason.None);
        }
    }
}

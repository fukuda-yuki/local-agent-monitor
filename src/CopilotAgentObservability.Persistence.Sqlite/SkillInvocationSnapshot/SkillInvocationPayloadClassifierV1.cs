using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

public enum SkillInvocationPayloadState
{
    Available,
    Malformed,
    Missing,
    Binary,
    Oversized
}

public enum SkillInvocationPayloadReason
{
    None,
    DuplicateProperty,
    UnknownProperty,
    InvalidFieldType,
    NameInvalid,
    PathInvalid,
    NameMissing,
    BodyMissing,
    DefinitionPathMissing,
    BodyUnicodeInvalid,
    PathUnicodeInvalid,
    BodyOversized,
    PathOversized
}

internal sealed record SkillInvocationPayloadAvailableFacts(
    string Name,
    string? Source,
    string? Trigger,
    string Body,
    string DefinitionPath);

internal sealed record SkillInvocationPayloadClassification(
    bool WellFormedToken,
    bool ObservedInvalidUtf8,
    SkillInvocationPayloadState State,
    SkillInvocationPayloadReason Reason,
    SkillInvocationPayloadAvailableFacts? AvailableFacts)
{
    // The receiver rejects a non-object payload token before Gate 6 ever runs, so no receiver-
    // defined state/reason exists for a malformed token; this sentinel pair only signals that
    // consumers must check WellFormedToken before reading State or Reason.
    internal static SkillInvocationPayloadClassification NotWellFormed(bool observedInvalidUtf8) =>
        new(false, observedInvalidUtf8, SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType, null);
}

internal sealed class SkillInvocationPayloadScan
{
    internal bool ObservedInvalidUtf8 { get; set; }

    internal int TokenEndIndex { get; set; }

    internal bool DuplicateProperty { get; set; }

    internal bool UnknownProperty { get; set; }

    internal bool InvalidFieldType { get; set; }

    internal bool NameInvalid { get; set; }

    internal bool PathInvalid { get; set; }

    internal bool NamePresent { get; set; }

    internal bool BodyPresent { get; set; }

    internal bool PathPresent { get; set; }

    internal bool BodyUnicodeInvalid { get; set; }

    internal bool PathUnicodeInvalid { get; set; }

    internal bool BodyOversized { get; set; }

    internal bool PathOversized { get; set; }

    internal string? Name { get; set; }

    internal string? Source { get; set; }

    internal string? Trigger { get; set; }

    internal string? Body { get; set; }

    internal string? Path { get; set; }
}

// Gate 6 of docs/specifications/interfaces/skill-invocation-snapshot.md is the sole authority for
// this classification. It lives here -- outside the v2 receiver -- because the historical content
// reader must re-prove the same classification under a Retention access grant; both consumers must
// share exactly one total order, so the receiver delegates here instead of keeping its own copy.
internal static class SkillInvocationPayloadClassifierV1
{
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

    internal static SkillInvocationPayloadScan ScanPayloadObject(ref Utf8JsonReader reader, ref bool observedInvalidUtf8)
    {
        var scan = new SkillInvocationPayloadScan();
        var observed = observedInvalidUtf8;
        try
        {
            ScanPayloadObjectCore(ref reader, scan, ref observed);
        }
        finally
        {
            observedInvalidUtf8 = observed;
        }

        return scan;
    }

    internal static SkillInvocationPayloadClassification Classify(ReadOnlySpan<byte> payloadTokenUtf8)
    {
        var reader = new Utf8JsonReader(payloadTokenUtf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });

        var observedInvalidUtf8 = false;
        SkillInvocationPayloadScan scan;
        try
        {
            if (!reader.Read())
            {
                return SkillInvocationPayloadClassification.NotWellFormed(observedInvalidUtf8);
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                ConsumeValueToken(ref reader, ref observedInvalidUtf8);
                return SkillInvocationPayloadClassification.NotWellFormed(observedInvalidUtf8);
            }

            scan = ScanPayloadObject(ref reader, ref observedInvalidUtf8);

            if (reader.Read())
            {
                ConsumeValueToken(ref reader, ref observedInvalidUtf8);
                return SkillInvocationPayloadClassification.NotWellFormed(observedInvalidUtf8);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return SkillInvocationPayloadClassification.NotWellFormed(observedInvalidUtf8);
        }

        var (state, reason) = ClassifyScan(scan);
        var availableFacts = state == SkillInvocationPayloadState.Available
            ? new SkillInvocationPayloadAvailableFacts(scan.Name!, scan.Source, scan.Trigger, scan.Body!, scan.Path!)
            : null;
        return new SkillInvocationPayloadClassification(true, observedInvalidUtf8, state, reason, availableFacts);
    }

    internal static (SkillInvocationPayloadState State, SkillInvocationPayloadReason Reason) ClassifyScan(SkillInvocationPayloadScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        if (scan.DuplicateProperty) return (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.DuplicateProperty);
        if (scan.UnknownProperty) return (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.UnknownProperty);
        if (scan.InvalidFieldType) return (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.InvalidFieldType);
        if (scan.NameInvalid) return (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.NameInvalid);
        if (scan.PathInvalid) return (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.PathInvalid);
        if (!scan.NamePresent) return (SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.NameMissing);
        if (!scan.BodyPresent) return (SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.BodyMissing);
        if (!scan.PathPresent) return (SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.DefinitionPathMissing);
        if (scan.BodyUnicodeInvalid) return (SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.BodyUnicodeInvalid);
        if (scan.PathUnicodeInvalid) return (SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.PathUnicodeInvalid);
        if (scan.BodyOversized) return (SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.BodyOversized);
        if (scan.PathOversized) return (SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.PathOversized);
        return (SkillInvocationPayloadState.Available, SkillInvocationPayloadReason.None);
    }

    internal static bool TryDecodeStringToken(
        ref Utf8JsonReader reader,
        out string value,
        out bool unicodeInvalid,
        out bool invalidUtf8)
    {
        value = string.Empty;
        unicodeInvalid = false;
        invalidUtf8 = false;
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName))
        {
            return false;
        }

        if (!IsValidUtf8(reader.ValueSpan))
        {
            invalidUtf8 = true;
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

    internal static void ConsumeValueToken(ref Utf8JsonReader reader, ref bool observedInvalidUtf8)
    {
        ValidateRawStringToken(ref reader, ref observedInvalidUtf8);
        if (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
        {
            return;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            ValidateRawStringToken(ref reader, ref observedInvalidUtf8);
            if (reader.CurrentDepth == depth && reader.TokenType is (JsonTokenType.EndArray or JsonTokenType.EndObject))
            {
                return;
            }
        }

        throw new JsonException("Invalid skill invocation payload token.");
    }

    private static void ScanPayloadObjectCore(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                scan.InvalidFieldType = true;
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
                continue;
            }

            if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var propertyName, out _))
            {
                scan.UnknownProperty = true;
                if (!reader.Read()) throw InvalidToken();
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
                continue;
            }

            if (!seen.Add(propertyName))
            {
                scan.DuplicateProperty = true;
            }

            if (!PayloadProperties.Contains(propertyName))
            {
                scan.UnknownProperty = true;
            }

            if (!reader.Read()) throw InvalidToken();
            switch (propertyName)
            {
                case "name":
                    scan.NamePresent = true;
                    ReadName(ref reader, scan, ref observedInvalidUtf8);
                    break;
                case "path":
                    scan.PathPresent = true;
                    ReadPath(ref reader, scan, ref observedInvalidUtf8);
                    break;
                case "content":
                    scan.BodyPresent = true;
                    ReadBody(ref reader, scan, ref observedInvalidUtf8);
                    break;
                case "allowedTools":
                    ReadAllowedTools(ref reader, scan, ref observedInvalidUtf8);
                    break;
                case "description":
                    ReadBoundedOptionalString(ref reader, scan, ref observedInvalidUtf8, 4_096, 16_384);
                    break;
                case "pluginName":
                case "pluginVersion":
                    ReadBoundedOptionalString(ref reader, scan, ref observedInvalidUtf8, int.MaxValue, 256);
                    break;
                case "source":
                    ReadClosedOptionalString(ref reader, scan, ref observedInvalidUtf8, Sources, isSource: true);
                    break;
                case "trigger":
                    ReadClosedOptionalString(ref reader, scan, ref observedInvalidUtf8, Triggers, isSource: false);
                    break;
                default:
                    ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw InvalidToken();
        }

        scan.TokenEndIndex = checked((int)reader.BytesConsumed);
    }

    private static void ReadName(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                scan.NameInvalid = true;
            }
            else
            {
                scan.InvalidFieldType = true;
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            }
            return;
        }

        scan.Name = value;
        var scalarCount = 0;
        var invalidScalar = false;
        foreach (var rune in value.EnumerateRunes())
        {
            scalarCount++;
            invalidScalar |= Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control || IsNoncharacter(rune.Value);
        }

        if (scalarCount is < 1 or > 200 || Encoding.UTF8.GetByteCount(value) > 800 || invalidScalar)
        {
            scan.NameInvalid = true;
        }
    }

    private static void ReadPath(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                scan.PathUnicodeInvalid = true;
            }
            else
            {
                scan.InvalidFieldType = true;
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            }
            return;
        }

        scan.Path = value;
        if (value.Length == 0 || value.EnumerateRunes().Any(rune => rune.Value <= 0x1f || rune.Value == 0x7f))
        {
            scan.PathInvalid = true;
        }

        if (Encoding.UTF8.GetByteCount(value) > 4_096)
        {
            scan.PathOversized = true;
        }
    }

    private static void ReadBody(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out var unicodeInvalid))
        {
            if (unicodeInvalid)
            {
                scan.BodyUnicodeInvalid = true;
            }
            else
            {
                scan.InvalidFieldType = true;
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            }
            return;
        }

        scan.Body = value;
        if (Encoding.UTF8.GetByteCount(value) > 1_048_576)
        {
            scan.BodyOversized = true;
        }
    }

    private static void ReadAllowedTools(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            scan.InvalidFieldType = true;
            ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            return;
        }

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            count++;
            if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out _))
            {
                scan.InvalidFieldType = true;
                ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
                continue;
            }

            var scalarCount = value.EnumerateRunes().Count();
            if (scalarCount is < 1 or > 128 || Encoding.UTF8.GetByteCount(value) > 512)
            {
                scan.InvalidFieldType = true;
            }
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw InvalidToken();
        }

        if (count > 64)
        {
            scan.InvalidFieldType = true;
        }
    }

    private static void ReadBoundedOptionalString(
        ref Utf8JsonReader reader,
        SkillInvocationPayloadScan scan,
        ref bool observedInvalidUtf8,
        int maximumScalars,
        int maximumBytes)
    {
        if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out _))
        {
            scan.InvalidFieldType = true;
            ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            return;
        }

        if (value.EnumerateRunes().Count() > maximumScalars || Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            scan.InvalidFieldType = true;
        }
    }

    private static void ReadClosedOptionalString(
        ref Utf8JsonReader reader,
        SkillInvocationPayloadScan scan,
        ref bool observedInvalidUtf8,
        HashSet<string> accepted,
        bool isSource)
    {
        if (!TryDecode(ref reader, scan, ref observedInvalidUtf8, out var value, out _) || !accepted.Contains(value))
        {
            scan.InvalidFieldType = true;
            ConsumeValue(ref reader, scan, ref observedInvalidUtf8);
            return;
        }

        if (isSource)
        {
            scan.Source = value;
        }
        else
        {
            scan.Trigger = value;
        }
    }

    private static bool TryDecode(
        ref Utf8JsonReader reader,
        SkillInvocationPayloadScan scan,
        ref bool observedInvalidUtf8,
        out string value,
        out bool unicodeInvalid)
    {
        var decoded = TryDecodeStringToken(ref reader, out value, out unicodeInvalid, out var invalidUtf8);
        if (invalidUtf8)
        {
            observedInvalidUtf8 = true;
            scan.ObservedInvalidUtf8 = true;
        }

        return decoded;
    }

    private static void ConsumeValue(ref Utf8JsonReader reader, SkillInvocationPayloadScan scan, ref bool observedInvalidUtf8)
    {
        ConsumeValueToken(ref reader, ref observedInvalidUtf8);
        // ConsumeValueToken validates raw string tokens; mirror that observation onto the scan so
        // a scan result alone tells whether any raw invalid UTF-8 was seen inside the payload.
        scan.ObservedInvalidUtf8 |= observedInvalidUtf8;
    }

    private static void ValidateRawStringToken(ref Utf8JsonReader reader, ref bool observedInvalidUtf8)
    {
        if (reader.TokenType is (JsonTokenType.String or JsonTokenType.PropertyName) && !IsValidUtf8(reader.ValueSpan))
        {
            observedInvalidUtf8 = true;
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

    private static JsonException InvalidToken() => new("Invalid skill invocation payload token.");
}

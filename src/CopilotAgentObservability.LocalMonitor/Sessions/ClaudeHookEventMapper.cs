using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed record ClaudeHookSourceMetadata(
    string? AdapterVersion,
    string? NormalizationVersion,
    string? SourceApplicationVersion,
    string? SchemaFingerprint);

internal static class ClaudeHookEventMapper
{
    private const string SourceAdapter = "claude-code-hook";
    private const string SourceSurface = "claude-code";

    private static readonly HashSet<string> EventTypeAllowlist = new(StringComparer.Ordinal)
    {
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PermissionRequest",
        "PostToolUse",
        "PostToolUseFailure",
        "SubagentStart",
        "SubagentStop",
        "Stop",
        "StopFailure",
        "SessionEnd",
    };

    public static bool TryMap(
        JsonElement producerEvent,
        DateTimeOffset capturedAt,
        ClaudeHookSourceMetadata? sourceMetadata,
        bool contentCaptureEnabled,
        out SessionIngestEnvelope? envelope)
    {
        envelope = null;
        if (!contentCaptureEnabled
            || !IsValid(sourceMetadata)
            || producerEvent.ValueKind != JsonValueKind.Object
            || HasDuplicatePropertyNames(producerEvent)
            || !TryRequiredString(producerEvent, "session_id", out var nativeSessionId)
            || nativeSessionId.Length > 256
            || !TryRequiredString(producerEvent, "hook_event_name", out var eventType)
            || !EventTypeAllowlist.Contains(eventType)
            || !HasRequiredString(producerEvent, "transcript_path")
            || !HasRequiredString(producerEvent, "cwd")
            || !ValidateCommonOptionalFields(producerEvent)
            || !ValidateEventFields(producerEvent, eventType))
        {
            return false;
        }

        var metadata = sourceMetadata!;
        var runNativeId = producerEvent.TryGetProperty("agent_id", out var agentId)
            ? agentId.GetString()
            : null;
        var mappedEvent = new SessionIngestEvent(
            ComputeCanonicalHash(producerEvent),
            eventType,
            capturedAt.ToString("O", CultureInfo.InvariantCulture),
            producerEvent.Clone(),
            ParentEventId: null,
            RunNativeId: runNativeId,
            TraceId: null);
        envelope = new SessionIngestEnvelope(
            SchemaVersion: 1,
            SourceAdapter,
            SourceSurface,
            nativeSessionId,
            [mappedEvent],
            ExplicitLink: null,
            metadata.SourceApplicationVersion,
            metadata.AdapterVersion,
            metadata.SchemaFingerprint,
            metadata.NormalizationVersion);
        return true;
    }

    private static bool IsValid(ClaudeHookSourceMetadata? metadata) => metadata is not null
        && IsMetadataToken(metadata.AdapterVersion)
        && IsMetadataToken(metadata.NormalizationVersion)
        && (metadata.SourceApplicationVersion is null || IsMetadataToken(metadata.SourceApplicationVersion))
        && (metadata.SchemaFingerprint is null || IsLowerHexSha256(metadata.SchemaFingerprint))
        && (metadata.SourceApplicationVersion is not null || metadata.SchemaFingerprint is not null);

    private static bool IsMetadataToken(string? value)
    {
        if (value is null || value.Length is < 1 or > 256 || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');
    }

    private static bool IsAsciiLetterOrDigit(char value) => value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';

    private static bool IsLowerHexSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasDuplicatePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicatePropertyNames(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicatePropertyNames(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ValidateCommonOptionalFields(JsonElement producerEvent)
    {
        if (!OptionalType(producerEvent, "prompt_id", JsonValueKind.String)
            || !OptionalType(producerEvent, "permission_mode", JsonValueKind.String)
            || !OptionalType(producerEvent, "agent_type", JsonValueKind.String)
            || !OptionalBoundedIdentity(producerEvent, "agent_id", 256))
        {
            return false;
        }

        if (!producerEvent.TryGetProperty("effort", out var effort))
        {
            return true;
        }

        return effort.ValueKind == JsonValueKind.Object
            && effort.TryGetProperty("level", out var level)
            && level.ValueKind == JsonValueKind.String;
    }

    private static bool ValidateEventFields(JsonElement producerEvent, string eventType) => eventType switch
    {
        "SessionStart" => HasRequiredString(producerEvent, "source")
            && OptionalType(producerEvent, "model", JsonValueKind.String)
            && OptionalType(producerEvent, "session_title", JsonValueKind.String),
        "UserPromptSubmit" => HasRequiredString(producerEvent, "prompt"),
        "PreToolUse" => HasRequiredString(producerEvent, "tool_name")
            && HasRequiredType(producerEvent, "tool_input", JsonValueKind.Object)
            && HasRequiredString(producerEvent, "tool_use_id"),
        "PermissionRequest" => HasRequiredString(producerEvent, "tool_name")
            && HasRequiredType(producerEvent, "tool_input", JsonValueKind.Object)
            && OptionalType(producerEvent, "permission_suggestions", JsonValueKind.Array),
        "PostToolUse" => HasRequiredString(producerEvent, "tool_name")
            && HasRequiredType(producerEvent, "tool_input", JsonValueKind.Object)
            && HasRequiredString(producerEvent, "tool_use_id")
            && producerEvent.TryGetProperty("tool_response", out _)
            && OptionalNumber(producerEvent, "duration_ms"),
        "PostToolUseFailure" => HasRequiredString(producerEvent, "tool_name")
            && HasRequiredType(producerEvent, "tool_input", JsonValueKind.Object)
            && HasRequiredString(producerEvent, "tool_use_id")
            && HasRequiredString(producerEvent, "error")
            && OptionalBoolean(producerEvent, "is_interrupt")
            && OptionalNumber(producerEvent, "duration_ms"),
        "SubagentStart" => HasRequiredBoundedIdentity(producerEvent, "agent_id", 256)
            && HasRequiredString(producerEvent, "agent_type"),
        "SubagentStop" => HasRequiredBoundedIdentity(producerEvent, "agent_id", 256)
            && HasRequiredType(producerEvent, "stop_hook_active", JsonValueKind.True, JsonValueKind.False)
            && HasRequiredString(producerEvent, "agent_type")
            && HasRequiredString(producerEvent, "agent_transcript_path")
            && HasRequiredString(producerEvent, "last_assistant_message")
            && OptionalType(producerEvent, "background_tasks", JsonValueKind.Array)
            && OptionalType(producerEvent, "session_crons", JsonValueKind.Array),
        "Stop" => HasRequiredType(producerEvent, "stop_hook_active", JsonValueKind.True, JsonValueKind.False)
            && HasRequiredString(producerEvent, "last_assistant_message")
            && OptionalType(producerEvent, "background_tasks", JsonValueKind.Array)
            && OptionalType(producerEvent, "session_crons", JsonValueKind.Array),
        "StopFailure" => HasRequiredString(producerEvent, "error")
            && OptionalType(producerEvent, "error_details", JsonValueKind.String)
            && OptionalType(producerEvent, "last_assistant_message", JsonValueKind.String),
        "SessionEnd" => HasRequiredString(producerEvent, "reason"),
        _ => false,
    };

    private static bool TryRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String;

    private static bool HasRequiredBoundedIdentity(JsonElement element, string name, int maximumLength) =>
        TryRequiredString(element, name, out var value) && value.Length <= maximumLength;

    private static bool OptionalBoundedIdentity(JsonElement element, string name, int maximumLength) =>
        !element.TryGetProperty(name, out _) || HasRequiredBoundedIdentity(element, name, maximumLength);

    private static bool OptionalType(JsonElement element, string name, JsonValueKind kind) =>
        !element.TryGetProperty(name, out var property) || property.ValueKind == kind;

    private static bool OptionalBoolean(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool OptionalNumber(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Number;

    private static bool HasRequiredType(JsonElement element, string name, params JsonValueKind[] kinds) =>
        element.TryGetProperty(name, out var property) && kinds.Contains(property.ValueKind);

    private static string ComputeCanonicalHash(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

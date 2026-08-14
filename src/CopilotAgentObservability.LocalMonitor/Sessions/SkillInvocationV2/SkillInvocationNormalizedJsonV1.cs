using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal static class SkillInvocationNormalizedJsonV1
{
    private const string SourceAdapter = "copilot-sdk-stream";
    private const string SourceSurface = "copilot-sdk";
    private const string SourceApplicationVersion = "1.0.65";
    private const string AdapterVersion = "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1";
    private const string NormalizationVersion = "github-copilot-sdk.skill-invoked.normalize.v1";
    private const string PayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private const string SchemaFingerprint = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
    private const string EventType = "skill.invoked";

    public static bool TryWrite(
        string? nativeSessionId,
        SkillInvokedEvent? sourceEvent,
        [NotNullWhen(true)] out byte[]? bodyUtf8)
    {
        bodyUtf8 = null;
        if (!SkillInvocationSdkV1Mapper.TryMap(nativeSessionId, sourceEvent, out var envelope))
        {
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = JavaScriptEncoder.Default
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 2);
            writer.WriteString("source_adapter", SourceAdapter);
            writer.WriteString("source_surface", SourceSurface);
            writer.WriteString("native_session_id", envelope.NativeSessionId);
            writer.WriteString("source_application_version", SourceApplicationVersion);
            writer.WriteString("adapter_version", AdapterVersion);
            writer.WriteString("normalization_version", NormalizationVersion);
            writer.WriteString("payload_schema", PayloadSchema);
            writer.WriteString("schema_fingerprint", SchemaFingerprint);
            writer.WriteStartArray("events");
            writer.WriteStartObject();
            writer.WriteString("source_event_id", envelope.SourceEventId);
            WriteNullableString(writer, "source_parent_event_id", envelope.SourceParentEventId);
            writer.WriteString("type", EventType);
            writer.WriteString("occurred_at", envelope.OccurredAt);
            WriteNullableString(writer, "run_native_id", envelope.RunNativeId);
            writer.WriteBoolean("source_ephemeral", envelope.SourceEphemeral);
            writer.WriteNull("trace_id");
            writer.WriteNull("span_id");
            WritePayload(writer, envelope.Payload);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        bodyUtf8 = buffer.WrittenSpan.ToArray();
        return true;
    }

    private static void WritePayload(Utf8JsonWriter writer, SkillInvocationSdkV1NormalizedPayload payload)
    {
        writer.WriteStartObject("payload");
        writer.WriteString("name", payload.Name);
        writer.WriteString("path", payload.Path);
        writer.WriteString("content", payload.Content);
        if (payload.AllowedTools is not null)
        {
            writer.WriteStartArray("allowedTools");
            foreach (var tool in payload.AllowedTools)
            {
                writer.WriteStringValue(tool);
            }
            writer.WriteEndArray();
        }

        WriteOptionalString(writer, "description", payload.Description);
        WriteOptionalString(writer, "pluginName", payload.PluginName);
        WriteOptionalString(writer, "pluginVersion", payload.PluginVersion);
        WriteOptionalString(writer, "source", payload.Source);
        WriteOptionalString(writer, "trigger", payload.Trigger);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
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
}

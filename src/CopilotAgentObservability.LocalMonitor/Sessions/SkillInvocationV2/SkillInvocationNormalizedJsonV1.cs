using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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

    public const int MaxProducerBodyBytes = 8_388_608;

    private const int EscapeChunkChars = 65_536;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.Default
    };

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
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteEnvelope(writer, envelope);
        }

        bodyUtf8 = buffer.WrittenSpan.ToArray();
        return true;
    }

    public static bool TryWriteCancellable(
        string? nativeSessionId,
        SkillInvokedEvent? sourceEvent,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out byte[]? bodyUtf8)
    {
        bodyUtf8 = null;
        if (!SkillInvocationSdkV1Mapper.TryMap(nativeSessionId, sourceEvent, out var envelope))
        {
            return false;
        }

        // Utf8JsonWriter requests one contiguous span per string token sized for the whole
        // value's worst case, so a near-limit body can never pass through the stopping
        // buffer via WriteString. The bounded serializer emits the identical document by
        // hand, appending chunked JavaScriptEncoder.Default output (byte-identical escaping)
        // so every append fits the 8,388,609-byte stopping buffer for any field composition.
        var buffer = new BoundedUtf8JsonBuffer(MaxProducerBodyBytes, cancellationToken);
        try
        {
            WriteEnvelopeBounded(buffer, envelope, cancellationToken);
        }
        catch (BoundedBufferOverflowException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (buffer.TotalWritten > MaxProducerBodyBytes)
        {
            return false;
        }

        bodyUtf8 = buffer.ToArray();
        return true;
    }

    private static void WriteEnvelopeBounded(
        BoundedUtf8JsonBuffer buffer,
        SkillInvocationSdkV1NormalizedEnvelope envelope,
        CancellationToken cancellationToken)
    {
        buffer.Append("{\"schema_version\":2"u8);
        AppendStringProperty(buffer, "source_adapter", SourceAdapter, cancellationToken);
        AppendStringProperty(buffer, "source_surface", SourceSurface, cancellationToken);
        AppendStringProperty(buffer, "native_session_id", envelope.NativeSessionId, cancellationToken);
        AppendStringProperty(buffer, "source_application_version", SourceApplicationVersion, cancellationToken);
        AppendStringProperty(buffer, "adapter_version", AdapterVersion, cancellationToken);
        AppendStringProperty(buffer, "normalization_version", NormalizationVersion, cancellationToken);
        AppendStringProperty(buffer, "payload_schema", PayloadSchema, cancellationToken);
        AppendStringProperty(buffer, "schema_fingerprint", SchemaFingerprint, cancellationToken);
        buffer.Append(",\"events\":[{"u8);
        AppendStringProperty(buffer, "source_event_id", envelope.SourceEventId, cancellationToken, first: true);
        AppendNullableStringProperty(buffer, "source_parent_event_id", envelope.SourceParentEventId, cancellationToken);
        AppendStringProperty(buffer, "type", EventType, cancellationToken);
        AppendStringProperty(buffer, "occurred_at", envelope.OccurredAt, cancellationToken);
        AppendNullableStringProperty(buffer, "run_native_id", envelope.RunNativeId, cancellationToken);
        buffer.Append(envelope.SourceEphemeral ? ",\"source_ephemeral\":true"u8 : ",\"source_ephemeral\":false"u8);
        buffer.Append(",\"trace_id\":null,\"span_id\":null"u8);
        WritePayloadBounded(buffer, envelope.Payload, cancellationToken);
        buffer.Append("}]}"u8);
    }

    private static void WritePayloadBounded(
        BoundedUtf8JsonBuffer buffer,
        SkillInvocationSdkV1NormalizedPayload payload,
        CancellationToken cancellationToken)
    {
        buffer.Append(",\"payload\":{"u8);
        AppendStringProperty(buffer, "name", payload.Name, cancellationToken, first: true);
        AppendStringProperty(buffer, "path", payload.Path, cancellationToken);
        AppendStringProperty(buffer, "content", payload.Content, cancellationToken);
        if (payload.AllowedTools is not null)
        {
            buffer.Append(",\"allowedTools\":["u8);
            var separator = "\""u8;
            foreach (var tool in payload.AllowedTools)
            {
                buffer.Append(separator);
                AppendEscaped(buffer, tool, cancellationToken);
                buffer.Append("\""u8);
                separator = ",\""u8;
            }

            buffer.Append("]"u8);
        }

        AppendOptionalStringProperty(buffer, "description", payload.Description, cancellationToken);
        AppendOptionalStringProperty(buffer, "pluginName", payload.PluginName, cancellationToken);
        AppendOptionalStringProperty(buffer, "pluginVersion", payload.PluginVersion, cancellationToken);
        AppendOptionalStringProperty(buffer, "source", payload.Source, cancellationToken);
        AppendOptionalStringProperty(buffer, "trigger", payload.Trigger, cancellationToken);
        buffer.Append("}"u8);
    }

    private static void AppendStringProperty(
        BoundedUtf8JsonBuffer buffer,
        string propertyName,
        string value,
        CancellationToken cancellationToken,
        bool first = false)
    {
        buffer.Append(first
            ? Encoding.UTF8.GetBytes($"\"{propertyName}\":\"")
            : Encoding.UTF8.GetBytes($",\"{propertyName}\":\""));
        AppendEscaped(buffer, value, cancellationToken);
        buffer.Append("\""u8);
    }

    private static void AppendNullableStringProperty(
        BoundedUtf8JsonBuffer buffer,
        string propertyName,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            buffer.Append(Encoding.UTF8.GetBytes($",\"{propertyName}\":null"));
        }
        else
        {
            AppendStringProperty(buffer, propertyName, value, cancellationToken);
        }
    }

    private static void AppendOptionalStringProperty(
        BoundedUtf8JsonBuffer buffer,
        string propertyName,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is not null)
        {
            AppendStringProperty(buffer, propertyName, value, cancellationToken);
        }
    }

    private static void AppendEscaped(BoundedUtf8JsonBuffer buffer, string value, CancellationToken cancellationToken)
    {
        for (var start = 0; start < value.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = NextEscapeChunkEnd(value, start);
            buffer.Append(Encoding.UTF8.GetBytes(JavaScriptEncoder.Default.Encode(value[start..end])));
            start = end;
        }
    }

    // Never ends a chunk on a high surrogate, so surrogate pairs are always encoded together.
    private static int NextEscapeChunkEnd(string value, int start)
    {
        var end = Math.Min(start + EscapeChunkChars, value.Length);
        if (end < value.Length && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return end;
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, SkillInvocationSdkV1NormalizedEnvelope envelope)
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

    internal sealed class BoundedBufferOverflowException : InvalidOperationException
    {
        internal BoundedBufferOverflowException()
            : base("The normalized skill invocation body exceeded the producer stopping buffer.")
        {
        }
    }

    // Stopping buffer: storage is exactly MaxProducerBodyBytes + 1 so a body whose final
    // length is MaxProducerBodyBytes + 1 is still fully appended and caught by the final
    // length check, while any append beyond that is refused.
    private sealed class BoundedUtf8JsonBuffer(int maxBytes, CancellationToken cancellationToken)
    {
        private readonly byte[] storage = new byte[maxBytes + 1];
        private int written;

        internal int TotalWritten => written;

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length > storage.Length - written)
            {
                throw new BoundedBufferOverflowException();
            }

            bytes.CopyTo(storage.AsSpan(written));
            written += bytes.Length;
        }

        internal byte[] ToArray() => storage.AsSpan(0, written).ToArray();
    }
}

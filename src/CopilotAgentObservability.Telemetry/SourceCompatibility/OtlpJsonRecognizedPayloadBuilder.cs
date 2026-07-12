using System.Buffers;

namespace CopilotAgentObservability.Telemetry;

internal static class OtlpJsonRecognizedPayloadBuilder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Build(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An OTLP trace request must be a JSON object.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteEnvelope(writer, document.RootElement, SourceStructuralEnvelope.Request);
        }

        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        JsonElement element,
        SourceStructuralEnvelope envelope)
    {
        writer.WriteStartObject();
        var emittedFallbacks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!OtlpTraceSchema.TryGetField(envelope, property.Name, out var field) ||
                field.Disposition == OtlpTraceFieldDisposition.TraceIgnored ||
                !OtlpTraceSchema.HasAcceptedJsonRepresentation(field, property.Value))
            {
                continue;
            }

            WriteAcceptedProperty(writer, property, field);
            if (field.EmitEmptyArrayWhenAbsent)
            {
                emittedFallbacks.Add(field.JsonName);
            }
        }

        foreach (var field in OtlpTraceSchema.Fields)
        {
            if (field.Envelope == envelope &&
                field.EmitEmptyArrayWhenAbsent &&
                !emittedFallbacks.Contains(field.JsonName))
            {
                writer.WriteStartArray(field.JsonName);
                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteAcceptedProperty(
        Utf8JsonWriter writer,
        JsonProperty property,
        OtlpTraceField field)
    {
        writer.WritePropertyName(property.Name);
        if (field.JsonRepresentation == OtlpJsonRepresentation.Array)
        {
            WriteRepeated(writer, property.Value, field);
            return;
        }

        if (field.Disposition == OtlpTraceFieldDisposition.ChildEnvelope)
        {
            WriteEnvelope(
                writer,
                property.Value,
                field.ChildEnvelope ?? throw new InvalidOperationException("Child fields require a child envelope."));
            return;
        }

        property.Value.WriteTo(writer);
    }

    private static void WriteRepeated(Utf8JsonWriter writer, JsonElement value, OtlpTraceField field)
    {
        writer.WriteStartArray();
        foreach (var item in value.EnumerateArray())
        {
            if (!OtlpTraceSchema.HasAcceptedJsonRepeatedElement(field, item))
            {
                continue;
            }

            if (field.Disposition == OtlpTraceFieldDisposition.ChildEnvelope)
            {
                WriteEnvelope(
                    writer,
                    item,
                    field.ChildEnvelope ?? throw new InvalidOperationException("Child fields require a child envelope."));
            }
            else
            {
                item.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
    }
}

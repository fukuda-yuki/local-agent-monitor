namespace CopilotAgentObservability.Telemetry;

internal static class RawOtlpIngestor
{
    public static RawTelemetryRecord CreateRecord(string inputPath, DateTimeOffset receivedAt)
    {
        var payloadJson = File.ReadAllText(inputPath, Encoding.UTF8);
        return CreateRecordFromPayloadJson(payloadJson, receivedAt);
    }

    public static RawTelemetryRecord CreateRecordFromPayloadJson(string payloadJson, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(payloadJson);
        ValidateRawOtlpEnvelope(document.RootElement);

        return new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: FindTraceId(document.RootElement),
            ReceivedAt: receivedAt,
            ResourceAttributesJson: ExtractResourceAttributesJson(document.RootElement),
            PayloadJson: payloadJson);
    }

    private static void ValidateRawOtlpEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("raw OTLP JSON must contain a top-level resourceSpans array.");
        }
    }

    private static string? FindTraceId(JsonElement root)
    {
        foreach (var resourceSpan in EnumerateArrayProperty(root, "resourceSpans"))
        {
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    if (span.TryGetProperty("traceId", out var traceIdElement)
                        && traceIdElement.ValueKind == JsonValueKind.String)
                    {
                        var traceId = traceIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(traceId))
                        {
                            return traceId;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractResourceAttributesJson(JsonElement root)
    {
        foreach (var resourceSpan in EnumerateArrayProperty(root, "resourceSpans"))
        {
            if (!resourceSpan.TryGetProperty("resource", out var resource)
                || !resource.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var attributesObject = OtlpAttributeConverter.ConvertAttributesArray(attributes);
            if (attributesObject.Count > 0)
            {
                return attributesObject.ToJsonString();
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            yield return item;
        }
    }
}

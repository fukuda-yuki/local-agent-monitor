namespace CopilotAgentObservability.Telemetry;

internal static class OtlpTracePayloadDecoder
{
    private const string JsonContentType = "application/json";
    private const string ProtobufContentType = "application/x-protobuf";

    public static DecodedOtlpTracePayload DecodeTracePayload(string? contentType, byte[] body)
    {
        return NormalizeContentType(contentType) switch
        {
            JsonContentType => DecodeJson(body),
            ProtobufContentType => OtlpProtobufTraceConverter.ConvertTraceRequest(body),
            _ => throw new UnsupportedOtlpContentTypeException(),
        };
    }

    private static DecodedOtlpTracePayload DecodeJson(byte[] body)
    {
        var payloadJson = Encoding.UTF8.GetString(body);
        return new DecodedOtlpTracePayload(payloadJson, OtlpJsonStructuralWalker.Build(payloadJson));
    }

    public static void EnsurePayloadContainsSpan(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        foreach (var resourceSpan in EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    if (span.ValueKind == JsonValueKind.Object)
                    {
                        return;
                    }
                }
            }
        }

        throw new InvalidDataException("trace payload must contain at least one span.");
    }

    private static string NormalizeContentType(string? contentType)
    {
        if (contentType is null)
        {
            return string.Empty;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? contentType : contentType[..separator]).Trim().ToLowerInvariant();
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

internal sealed class UnsupportedOtlpContentTypeException : Exception;

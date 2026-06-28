namespace CopilotAgentObservability.Telemetry;

internal static class OtlpSpanReader
{
    internal static readonly string[] InputTokenKeys =
    [
        "gen_ai.usage.input_tokens",
        "gen_ai.usage.prompt_tokens",
        "gen_ai.usage.input",
        "llm.usage.prompt_tokens",
        "usage.input",
        "input_tokens",
        "inputTokens",
        "promptTokens",
    ];

    internal static readonly string[] OutputTokenKeys =
    [
        "gen_ai.usage.output_tokens",
        "gen_ai.usage.completion_tokens",
        "gen_ai.usage.output",
        "llm.usage.completion_tokens",
        "usage.output",
        "output_tokens",
        "outputTokens",
        "completionTokens",
    ];

    internal static readonly string[] TotalTokenKeys =
    [
        "gen_ai.usage.total_tokens",
        "gen_ai.usage.total",
        "llm.usage.total_tokens",
        "usage.total",
        "total_tokens",
        "totalTokens",
    ];

    internal static readonly string[] GenAiOperationKeys =
    [
        "gen_ai.operation.name",
        "genAi.operation.name",
    ];

    internal static readonly string[] GenAiToolNameKeys =
    [
        "gen_ai.tool.name",
        "genAi.tool.name",
        "tool.name",
    ];

    internal static RawSpanInfo CreateSpan(JsonElement span)
    {
        var attributes = span.TryGetProperty("attributes", out var attributesElement)
            && attributesElement.ValueKind == JsonValueKind.Array
                ? OtlpAttributeConverter.ConvertAttributesArray(attributesElement)
                : new JsonObject();

        var events = new List<RawSpanEvent>();
        foreach (var eventElement in EnumerateArrayProperty(span, "events"))
        {
            var eventAttributes = eventElement.TryGetProperty("attributes", out var eventAttributesElement)
                && eventAttributesElement.ValueKind == JsonValueKind.Array
                    ? OtlpAttributeConverter.ConvertAttributesArray(eventAttributesElement)
                    : new JsonObject();
            events.Add(new RawSpanEvent(
                Name: ReadString(eventElement, "name"),
                Attributes: eventAttributes));
        }

        return new RawSpanInfo(
            TraceId: ReadString(span, "traceId"),
            SpanId: ReadString(span, "spanId"),
            ParentSpanId: ReadString(span, "parentSpanId"),
            Name: ReadString(span, "name"),
            Kind: ReadString(span, "kind"),
            StartTimeUnixNano: ReadUnsignedLong(span, "startTimeUnixNano"),
            EndTimeUnixNano: ReadUnsignedLong(span, "endTimeUnixNano"),
            StatusCode: TryGetObject(span, "status", out var status) ? ReadString(status, "code") : null,
            Attributes: attributes,
            Events: events);
    }

    internal static bool IsLlmSpan(RawSpanInfo span)
    {
        return HasAnyStringValue(span.Attributes, GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasStringValue(span.Attributes, "type", "generation")
            || HasStringValue(span.Attributes, "type", "chat")
            || HasSpanName(span, "chat");
    }

    internal static bool IsToolSpan(RawSpanInfo span)
    {
        return HasStringValue(span.Attributes, "type", "tool")
            || HasStringValue(span.Attributes, "kind", "tool")
            || HasStringValue(span.Attributes, "category", "tool")
            || HasAnyStringValue(span.Attributes, GenAiToolNameKeys)
            || HasSpanName(span, "execute_tool");
    }

    internal static bool IsKnownNonCountedSpan(RawSpanInfo span)
    {
        return HasSpanName(span, "invoke_agent")
            || HasSpanName(span, "execute_hook")
            || HasSpanName(span, "lifecycle_event")
            || HasAnyStringValue(span.Attributes, ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle");
    }

    internal static bool IsErrorSpan(RawSpanInfo span)
    {
        if (span.StatusCode is not null
            && (string.Equals(span.StatusCode, "2", StringComparison.Ordinal)
                || string.Equals(span.StatusCode, "ERROR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(span.StatusCode, "STATUS_CODE_ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return HasStringValue(span.Attributes, "level", "error")
            || HasStringValue(span.Attributes, "status", "error")
            || ReadString(span.Attributes, "error") is not null;
    }

    internal static int CountErrorEvents(RawSpanInfo span)
    {
        var count = 0;
        foreach (var spanEvent in span.Events)
        {
            if (spanEvent.Name is not null
                && (spanEvent.Name.Contains("exception", StringComparison.OrdinalIgnoreCase)
                    || spanEvent.Name.Contains("error", StringComparison.OrdinalIgnoreCase)))
            {
                count++;
                continue;
            }

            if (ReadString(spanEvent.Attributes, "exception.type") is not null
                || ReadString(spanEvent.Attributes, "error") is not null
                || HasStringValue(spanEvent.Attributes, "level", "error"))
            {
                count++;
            }
        }

        return count;
    }

    internal static bool HasSpanName(RawSpanInfo span, string expectedName)
    {
        if (span.Name is null)
        {
            return false;
        }

        return string.Equals(span.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            || span.Name.StartsWith($"{expectedName} ", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadString(JsonObject attributes, string propertyName)
    {
        if (!attributes.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
    }

    internal static int? ReadInt(JsonObject attributes, string propertyName)
    {
        if (!attributes.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return ReadInt(value);
    }

    internal static int? ReadInt(JsonNode value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (jsonValue.TryGetValue<long>(out var longValue)
                && longValue >= int.MinValue
                && longValue <= int.MaxValue)
            {
                return (int)longValue;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue)
                && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    internal static string? ReadFirstString(JsonObject attributes, IReadOnlyList<string> candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            var value = ReadString(attributes, candidateKey);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    internal static int? ReadFirstInt(JsonObject attributes, IReadOnlyList<string> candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            var value = ReadInt(attributes, candidateKey);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    internal static bool HasStringValue(JsonObject attributes, string propertyName, string expected)
    {
        return ReadString(attributes, propertyName) is { } actual
            && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasAnyStringValue(JsonObject attributes, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        foreach (var propertyName in propertyNames)
        {
            if (ReadString(attributes, propertyName) is not { } actual)
            {
                continue;
            }

            if (expectedValues.Length == 0
                || expectedValues.Any(expected => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    internal static ulong? ReadUnsignedLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetUInt64(out var value) => value,
            JsonValueKind.String when ulong.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    internal static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    internal static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            yield return item;
        }
    }

    internal static JsonObject ReadResourceAttributes(JsonElement resourceSpan)
    {
        if (!resourceSpan.TryGetProperty("resource", out var resource)
            || !resource.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return new JsonObject();
        }

        return OtlpAttributeConverter.ConvertAttributesArray(attributes);
    }

    internal static void MergeResourceAttributes(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (!target.ContainsKey(property.Key))
            {
                target[property.Key] = property.Value is null
                    ? null
                    : JsonNode.Parse(property.Value.ToJsonString());
            }
        }
    }

    internal sealed record RawSpanInfo(
        string? TraceId,
        string? SpanId,
        string? ParentSpanId,
        string? Name,
        string? Kind,
        ulong? StartTimeUnixNano,
        ulong? EndTimeUnixNano,
        string? StatusCode,
        JsonObject Attributes,
        IReadOnlyList<RawSpanEvent> Events);

    internal sealed record RawSpanEvent(
        string? Name,
        JsonObject Attributes);
}

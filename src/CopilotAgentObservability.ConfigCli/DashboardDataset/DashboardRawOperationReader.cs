namespace CopilotAgentObservability.ConfigCli;

internal sealed record DashboardRawOperation(
    string? TraceId,
    string? UserId,
    string? UserEmail,
    string OperationKind,
    string? ToolName,
    string? Model,
    string Status,
    int? DurationMs,
    int ErrorCount,
    int TimeoutCount,
    int RetryCount,
    int? ApprovalWaitMs,
    string? PermissionResult,
    int SubagentCallCount,
    int NestedAgentCallCount,
    int? TtftMs,
    string? TtftSource,
    DateTimeOffset? StartedAtUtc);

internal static class DashboardRawOperationReader
{
    public static IReadOnlyList<DashboardRawOperation> Read(string inputPath)
    {
        if (IsRawStorePath(inputPath))
        {
            var store = new RawTelemetryStore(inputPath);
            return store.ListRecords().SelectMany(record => ReadRawJson(record.PayloadJson)).ToArray();
        }

        return ReadRawJson(File.ReadAllText(inputPath));
    }

    private static bool IsRawStorePath(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        if (extension is ".db" or ".sqlite" or ".sqlite3")
        {
            return true;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(inputPath);
        var bytesRead = stream.Read(header);
        return bytesRead >= 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private static IReadOnlyList<DashboardRawOperation> ReadRawJson(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("raw OTLP JSON must contain a top-level resourceSpans array.");
        }

        var operations = new List<DashboardRawOperation>();
        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = ReadResourceAttributes(resourceSpan);
            var userId = ReadFirstString(resourceAttributes, ["user.id"]);
            var userEmail = ReadFirstString(resourceAttributes, ["user.email"]);
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    operations.Add(CreateOperation(span, userId, userEmail));
                }
            }
        }

        return operations;
    }

    private static JsonObject ReadResourceAttributes(JsonElement resourceSpan)
    {
        if (!resourceSpan.TryGetProperty("resource", out var resource)
            || !resource.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return new JsonObject();
        }

        return OtlpAttributeConverter.ConvertAttributesArray(attributes);
    }

    private static DashboardRawOperation CreateOperation(JsonElement span, string? userId, string? userEmail)
    {
        var attributes = span.TryGetProperty("attributes", out var attributesElement)
            && attributesElement.ValueKind == JsonValueKind.Array
                ? OtlpAttributeConverter.ConvertAttributesArray(attributesElement)
                : new JsonObject();

        var traceId = ReadString(span, "traceId");
        var spanName = ReadString(span, "name");
        var toolName = ReadFirstString(attributes, ["gen_ai.tool.name", "genAi.tool.name", "tool.name"]);
        var model = ReadFirstString(attributes, ["gen_ai.request.model", "gen_ai.response.model", "model", "llm.model"]);
        var operationKind = ClassifyOperation(spanName, attributes, toolName);
        var startNano = ReadUnsignedLong(span, "startTimeUnixNano");
        var endNano = ReadUnsignedLong(span, "endTimeUnixNano");
        var durationMs = ReadDurationMs(startNano, endNano);
        var status = IsErrorSpan(span, attributes) ? "error" : "success";
        var errorCount = status == "error" ? 1 : 0;
        var timeoutCount = HasStringValue(attributes, "error.type", "timeout") || HasStringValue(attributes, "timeout", "true") ? 1 : 0;
        var directTtft = ReadFirstInt(attributes, ["ttft_ms", "time_to_first_token_ms", "gen_ai.response.ttft_ms"]);
        var eventTtft = directTtft.HasValue ? null : ReadFirstGenerationEventTtft(span, startNano);

        return new DashboardRawOperation(
            TraceId: traceId,
            UserId: userId,
            UserEmail: userEmail,
            OperationKind: operationKind,
            ToolName: toolName,
            Model: model,
            Status: status,
            DurationMs: durationMs,
            ErrorCount: errorCount,
            TimeoutCount: timeoutCount,
            RetryCount: ReadFirstInt(attributes, ["retry_count", "retry.count", "gen_ai.operation.retry_count"]) ?? 0,
            ApprovalWaitMs: ReadFirstInt(attributes, ["approval_wait_ms", "approval.wait_ms", "permission.wait_ms"]),
            PermissionResult: ReadFirstString(attributes, ["permission.result", "approval.result"]),
            SubagentCallCount: operationKind == "subagent" ? 1 : ReadFirstInt(attributes, ["subagent_call_count", "subagent.call_count"]) ?? 0,
            NestedAgentCallCount: ReadFirstInt(attributes, ["nested_agent_call_count", "nested_agent.call_count"]) ?? 0,
            TtftMs: directTtft ?? eventTtft,
            TtftSource: directTtft.HasValue
                ? "direct-attribute"
                : eventTtft.HasValue
                    ? "derived-first-generation-event"
                    : null,
            StartedAtUtc: ToDateTimeOffset(startNano));
    }

    private static string ClassifyOperation(string? spanName, JsonObject attributes, string? toolName)
    {
        if (HasAnyStringValue(attributes, ["type", "kind", "category"], "permission", "approval")
            || Contains(spanName, "approval")
            || Contains(spanName, "permission"))
        {
            return "approval";
        }

        if (HasAnyStringValue(attributes, ["type", "kind", "category"], "subagent", "nested_agent")
            || Contains(spanName, "subagent")
            || Contains(spanName, "nested_agent"))
        {
            return "subagent";
        }

        if (!string.IsNullOrWhiteSpace(toolName)
            || HasAnyStringValue(attributes, ["type", "kind", "category"], "tool")
            || Contains(spanName, "execute_tool"))
        {
            return "tool";
        }

        if (HasAnyStringValue(attributes, ["gen_ai.operation.name", "genAi.operation.name"], "chat", "generate_content", "text_completion")
            || HasAnyStringValue(attributes, ["type", "kind", "category"], "generation", "chat", "llm")
            || Contains(spanName, "chat"))
        {
            return "llm";
        }

        return "unknown";
    }

    private static int? ReadFirstGenerationEventTtft(JsonElement span, ulong? startNano)
    {
        if (!startNano.HasValue)
        {
            return null;
        }

        foreach (var spanEvent in EnumerateArrayProperty(span, "events"))
        {
            var name = ReadString(spanEvent, "name");
            if (!IsFirstGenerationEvent(name))
            {
                continue;
            }

            var eventTime = ReadUnsignedLong(spanEvent, "timeUnixNano");
            if (eventTime.HasValue && eventTime.Value >= startNano.Value)
            {
                return (int)Math.Round((eventTime.Value - startNano.Value) / 1_000_000d, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    private static bool IsFirstGenerationEvent(string? name)
    {
        if (name is null)
        {
            return false;
        }

        return name.Contains("first_token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("first.chunk", StringComparison.OrdinalIgnoreCase)
            || name.Contains("first_chunk", StringComparison.OrdinalIgnoreCase)
            || name.Contains("message_start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsErrorSpan(JsonElement span, JsonObject attributes)
    {
        if (span.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && ReadString(status, "code") is { } code
            && (string.Equals(code, "2", StringComparison.Ordinal)
                || string.Equals(code, "ERROR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "STATUS_CODE_ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return HasStringValue(attributes, "level", "error")
            || HasStringValue(attributes, "status", "error")
            || ReadFirstString(attributes, ["error", "exception.type"]) is not null;
    }

    private static int? ReadDurationMs(ulong? startNano, ulong? endNano)
    {
        if (!startNano.HasValue || !endNano.HasValue || endNano.Value < startNano.Value)
        {
            return null;
        }

        return (int)Math.Round((endNano.Value - startNano.Value) / 1_000_000d, MidpointRounding.AwayFromZero);
    }

    private static DateTimeOffset? ToDateTimeOffset(ulong? unixNano)
    {
        if (!unixNano.HasValue)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixNano.Value / 1_000_000UL));
    }

    private static string? ReadFirstString(JsonObject attributes, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (attributes.TryGetPropertyValue(key, out var value) && value is not null)
            {
                return value.GetValueKind() == JsonValueKind.String
                    ? value.GetValue<string>()
                    : value.ToJsonString();
            }
        }

        return null;
    }

    private static int? ReadFirstInt(JsonObject attributes, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!attributes.TryGetPropertyValue(key, out var value) || value is null)
            {
                continue;
            }

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
        }

        return null;
    }

    private static bool HasStringValue(JsonObject attributes, string propertyName, string expected)
    {
        return ReadFirstString(attributes, [propertyName]) is { } actual
            && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyStringValue(JsonObject attributes, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        foreach (var propertyName in propertyNames)
        {
            if (ReadFirstString(attributes, [propertyName]) is not { } actual)
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

    private static bool Contains(string? value, string expected)
    {
        return value is not null && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName)
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

    private static ulong? ReadUnsignedLong(JsonElement element, string propertyName)
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

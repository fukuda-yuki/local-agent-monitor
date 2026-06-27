namespace CopilotAgentObservability.Telemetry;

internal static class RawMeasurementNormalizer
{
    private static readonly string[] TurnCountKeys =
    [
        "turn_count",
        "turnCount",
        "github.copilot.agent.turn.count",
    ];

    private static readonly string[] ToolCallCountKeys =
    [
        "tool_call_count",
        "toolCallCount",
        "github.copilot.tool.call.count",
    ];

    private static readonly string[] GenAiOperationKeys =
    [
        "gen_ai.operation.name",
        "genAi.operation.name",
    ];

    private static readonly string[] GenAiToolNameKeys =
    [
        "gen_ai.tool.name",
        "genAi.tool.name",
        "tool.name",
    ];

    private static readonly string[] InputTokenKeys =
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

    private static readonly string[] OutputTokenKeys =
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

    private static readonly string[] TotalTokenKeys =
    [
        "gen_ai.usage.total_tokens",
        "gen_ai.usage.total",
        "llm.usage.total_tokens",
        "usage.total",
        "total_tokens",
        "totalTokens",
    ];

    public static IReadOnlyList<MeasurementRow> Normalize(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return Normalize(document.RootElement);
    }

    public static IReadOnlyList<MeasurementRow> Normalize(IReadOnlyList<RawTelemetryRecord> records)
    {
        var rows = new List<MeasurementRow>();
        foreach (var record in records)
        {
            rows.AddRange(Normalize(record.PayloadJson));
        }

        return rows;
    }

    private static IReadOnlyList<MeasurementRow> Normalize(JsonElement root)
    {
        if (!root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("raw OTLP JSON must contain a top-level resourceSpans array.");
        }

        var groups = new Dictionary<string, RawTraceGroup>(StringComparer.Ordinal);
        var missingTraceIdIndex = 0;

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = ReadResourceAttributes(resourceSpan);
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var traceId = ReadString(span, "traceId");
                    var key = string.IsNullOrWhiteSpace(traceId)
                        ? $"__missing_trace_id_{missingTraceIdIndex++}"
                        : traceId!;

                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new RawTraceGroup(traceId);
                        groups.Add(key, group);
                    }

                    MergeResourceAttributes(group.ResourceAttributes, resourceAttributes);
                    group.Spans.Add(CreateSpan(span));
                }
            }
        }

        return groups.Values.Select(CreateRow).ToArray();
    }

    private static MeasurementRow CreateRow(RawTraceGroup group)
    {
        var explicitTurnCount = ReadFirstInt(group.ResourceAttributes, TurnCountKeys);
        var explicitToolCallCount = ReadFirstInt(group.ResourceAttributes, ToolCallCountKeys);
        var observedTurnCount = 0;
        var observedToolCallCount = 0;
        var errorCount = 0;
        var unknownSpans = new JsonArray();
        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;
        ulong? minStart = null;
        ulong? maxEnd = null;

        foreach (var span in group.Spans)
        {
            inputTokens = AddNullable(inputTokens, ReadFirstInt(span.Attributes, InputTokenKeys));
            outputTokens = AddNullable(outputTokens, ReadFirstInt(span.Attributes, OutputTokenKeys));
            totalTokens = AddNullable(totalTokens, ReadFirstInt(span.Attributes, TotalTokenKeys));

            explicitTurnCount ??= ReadFirstInt(span.Attributes, TurnCountKeys);
            explicitToolCallCount ??= ReadFirstInt(span.Attributes, ToolCallCountKeys);

            if (IsKnownNonCountedSpan(span))
            {
                // Non-counted spans may still carry token or error metadata.
            }
            else if (IsToolSpan(span))
            {
                observedToolCallCount++;
            }
            else if (IsLlmSpan(span))
            {
                observedTurnCount++;
            }
            else
            {
                unknownSpans.Add(CreateUnknownSpanNode(span));
            }

            if (IsErrorSpan(span))
            {
                errorCount++;
            }

            errorCount += CountErrorEvents(span);

            if (span.StartTimeUnixNano.HasValue)
            {
                minStart = minStart.HasValue ? Math.Min(minStart.Value, span.StartTimeUnixNano.Value) : span.StartTimeUnixNano.Value;
            }

            if (span.EndTimeUnixNano.HasValue)
            {
                maxEnd = maxEnd.HasValue ? Math.Max(maxEnd.Value, span.EndTimeUnixNano.Value) : span.EndTimeUnixNano.Value;
            }
        }

        totalTokens ??= inputTokens.HasValue && outputTokens.HasValue
            ? inputTokens + outputTokens
            : null;

        var turnCount = explicitTurnCount ?? observedTurnCount;
        var toolCallCount = explicitToolCallCount ?? observedToolCallCount;

        return new MeasurementRow(
            TraceId: group.TraceId,
            ExperimentId: ReadString(group.ResourceAttributes, "experiment.id"),
            ClientKind: ReadString(group.ResourceAttributes, "client.kind"),
            TaskId: ReadString(group.ResourceAttributes, "task.id"),
            TaskCategory: ReadString(group.ResourceAttributes, "task.category"),
            TaskRunIndex: ReadInt(group.ResourceAttributes, "task.run_index"),
            ExperimentCondition: ReadString(group.ResourceAttributes, "experiment.condition"),
            PromptVersion: ReadString(group.ResourceAttributes, "prompt.version"),
            AgentVariant: ReadString(group.ResourceAttributes, "agent.variant"),
            RepoSnapshot: ReadString(group.ResourceAttributes, "repo.snapshot"),
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TurnCount: turnCount,
            ToolCallCount: toolCallCount,
            DurationMs: ReadDurationMs(minStart, maxEnd),
            ErrorCount: errorCount,
            SuccessStatus: "not-evaluated",
            EvaluatorId: null,
            EvaluationNotes: null,
            EvaluatedAt: null,
            UnknownSpansJson: unknownSpans.Count == 0 ? null : unknownSpans,
            UnknownAttributesJson: CreateUnknownAttributesNode(group.ResourceAttributes),
            AggregationNotes: CreateAggregationNotes(unknownSpans));
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

    private static RawSpanInfo CreateSpan(JsonElement span)
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
            SpanId: ReadString(span, "spanId"),
            Name: ReadString(span, "name"),
            Kind: ReadString(span, "kind"),
            StartTimeUnixNano: ReadUnsignedLong(span, "startTimeUnixNano"),
            EndTimeUnixNano: ReadUnsignedLong(span, "endTimeUnixNano"),
            StatusCode: TryGetObject(span, "status", out var status) ? ReadString(status, "code") : null,
            Attributes: attributes,
            Events: events);
    }

    private static bool IsLlmSpan(RawSpanInfo span)
    {
        return HasAnyStringValue(span.Attributes, GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasStringValue(span.Attributes, "type", "generation")
            || HasStringValue(span.Attributes, "type", "chat")
            || HasSpanName(span, "chat");
    }

    private static bool IsToolSpan(RawSpanInfo span)
    {
        return HasStringValue(span.Attributes, "type", "tool")
            || HasStringValue(span.Attributes, "kind", "tool")
            || HasStringValue(span.Attributes, "category", "tool")
            || HasAnyStringValue(span.Attributes, GenAiToolNameKeys)
            || HasSpanName(span, "execute_tool");
    }

    private static bool IsKnownNonCountedSpan(RawSpanInfo span)
    {
        return HasSpanName(span, "invoke_agent")
            || HasSpanName(span, "execute_hook")
            || HasSpanName(span, "lifecycle_event")
            || HasAnyStringValue(span.Attributes, ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle");
    }

    private static bool IsErrorSpan(RawSpanInfo span)
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

    private static int CountErrorEvents(RawSpanInfo span)
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

    private static JsonObject CreateUnknownSpanNode(RawSpanInfo span)
    {
        var node = new JsonObject();
        AddStringNode(node, "id", span.SpanId);
        AddSafeStringNode(node, "name", span.Name);
        AddStringNode(node, "kind", span.Kind);
        return node;
    }

    private static JsonObject? CreateUnknownAttributesNode(JsonObject resourceAttributes)
    {
        var unknown = new JsonObject();
        MeasurementSanitizer.AddUnknownResourceAttributes(unknown, resourceAttributes);
        return unknown.Count == 0 ? null : unknown;
    }

    private static string CreateAggregationNotes(JsonArray unknownSpans)
    {
        const string countSource = "turn_count and tool_call_count are calculated from explicit count attributes when present, otherwise from classified raw OTLP spans.";
        return unknownSpans.Count == 0
            ? countSource
            : $"{countSource} Unknown spans are listed in unknown_spans_json.";
    }

    private static int? ReadDurationMs(ulong? minStart, ulong? maxEnd)
    {
        if (!minStart.HasValue || !maxEnd.HasValue || maxEnd.Value < minStart.Value)
        {
            return null;
        }

        var durationMs = (maxEnd.Value - minStart.Value) / 1_000_000d;
        return (int)Math.Round(durationMs, MidpointRounding.AwayFromZero);
    }

    private static int? AddNullable(int? current, int? value)
    {
        return value.HasValue ? (current ?? 0) + value.Value : current;
    }

    private static int? ReadFirstInt(JsonObject attributes, IReadOnlyList<string> candidateKeys)
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

    private static bool HasStringValue(JsonObject attributes, string propertyName, string expected)
    {
        return ReadString(attributes, propertyName) is { } actual
            && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyStringValue(JsonObject attributes, IReadOnlyList<string> propertyNames, params string[] expectedValues)
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

    private static bool HasSpanName(RawSpanInfo span, string expectedName)
    {
        if (span.Name is null)
        {
            return false;
        }

        return string.Equals(span.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            || span.Name.StartsWith($"{expectedName} ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonObject attributes, string propertyName)
    {
        if (!attributes.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
    }

    private static int? ReadInt(JsonObject attributes, string propertyName)
    {
        if (!attributes.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return ReadInt(value);
    }

    private static int? ReadInt(JsonNode value)
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

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
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

    private static void MergeResourceAttributes(JsonObject target, JsonObject source)
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

    private static void AddStringNode(JsonObject node, string propertyName, string? value)
    {
        if (value is not null)
        {
            node[propertyName] = value;
        }
    }

    private static void AddSafeStringNode(JsonObject node, string propertyName, string? value)
    {
        if (value is not null && !MeasurementSanitizer.IsUnsafeStringValue(value))
        {
            node[propertyName] = value;
        }
    }

    private sealed class RawTraceGroup
    {
        public RawTraceGroup(string? traceId)
        {
            TraceId = traceId;
        }

        public string? TraceId { get; }

        public JsonObject ResourceAttributes { get; } = new();

        public List<RawSpanInfo> Spans { get; } = [];
    }

    private sealed record RawSpanInfo(
        string? SpanId,
        string? Name,
        string? Kind,
        ulong? StartTimeUnixNano,
        ulong? EndTimeUnixNano,
        string? StatusCode,
        JsonObject Attributes,
        IReadOnlyList<RawSpanEvent> Events);

    private sealed record RawSpanEvent(
        string? Name,
        JsonObject Attributes);
}

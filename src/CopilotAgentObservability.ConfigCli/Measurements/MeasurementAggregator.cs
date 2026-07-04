namespace CopilotAgentObservability.ConfigCli;

internal static class MeasurementAggregator
{
    public static IReadOnlyList<MeasurementRow> Aggregate(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("traces", out var tracesElement)
            || tracesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("input JSON must contain a top-level traces array.");
        }

        var rows = new List<MeasurementRow>();
        foreach (var traceElement in tracesElement.EnumerateArray())
        {
            rows.Add(CreateRow(traceElement));
        }

        return rows;
    }

    private static MeasurementRow CreateRow(JsonElement traceElement)
    {
        var metadata = traceElement.TryGetProperty("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement
                : default;

        var resourceAttributes = TryGetObject(metadata, "resourceAttributes", out var resourceAttributesElement)
            ? resourceAttributesElement
            : default;

        var inputTokens = ReadInt(traceElement, "usage", "input")
            ?? ReadInt(traceElement, "usage", "inputTokens")
            ?? ReadInt(traceElement, "usage", "promptTokens");
        var outputTokens = ReadInt(traceElement, "usage", "output")
            ?? ReadInt(traceElement, "usage", "outputTokens")
            ?? ReadInt(traceElement, "usage", "completionTokens");
        var totalTokens = ReadInt(traceElement, "usage", "total")
            ?? ReadInt(traceElement, "usage", "totalTokens")
            ?? (inputTokens.HasValue && outputTokens.HasValue ? inputTokens + outputTokens : null);

        var observations = TryGetArray(traceElement, "observations", out var observationsElement)
            ? observationsElement
            : default;

        var unknownSpans = new JsonArray();
        int? turnCount = ReadExplicitCount(traceElement, resourceAttributes, observations, TurnCountKeys);
        int? toolCallCount = ReadExplicitCount(traceElement, resourceAttributes, observations, ToolCallCountKeys);
        int? errorCount = null;

        if (observations.ValueKind == JsonValueKind.Array)
        {
            var observedTurnCount = 0;
            var observedToolCallCount = 0;
            errorCount = 0;

            foreach (var observation in observations.EnumerateArray())
            {
                if (IsKnownNonCountedObservation(observation))
                {
                    // Explicitly non-counted observations win over overlapping tool or GenAI attributes.
                }
                else if (IsToolObservation(observation))
                {
                    observedToolCallCount++;
                }
                else if (IsLlmObservation(observation))
                {
                    observedTurnCount++;
                }
                else
                {
                    unknownSpans.Add(CreateUnknownSpanNode(observation));
                }

                turnCount ??= ReadExplicitCount(observation, default, default, TurnCountKeys);

                if (IsErrorObservation(observation))
                {
                    errorCount++;
                }
            }

            turnCount ??= observedTurnCount;
            toolCallCount ??= observedToolCallCount;
        }
        else
        {
            toolCallCount = ReadExplicitCount(traceElement, resourceAttributes, observations, ToolCallCountKeys);
        }

        var aggregationNotes = CreateAggregationNotes(observations, unknownSpans);

        return new MeasurementRow(
            TraceId: ReadString(traceElement, "id") ?? ReadString(traceElement, "traceId"),
            ExperimentId: ReadString(resourceAttributes, "experiment.id"),
            ClientKind: ReadString(resourceAttributes, "client.kind"),
            TaskId: ReadString(resourceAttributes, "task.id"),
            TaskCategory: ReadString(resourceAttributes, "task.category"),
            TaskRunIndex: ReadInt(resourceAttributes, "task.run_index"),
            ExperimentCondition: ReadString(resourceAttributes, "experiment.condition"),
            PromptVersion: ReadString(resourceAttributes, "prompt.version"),
            AgentVariant: ReadString(resourceAttributes, "agent.variant"),
            RepoSnapshot: ReadString(resourceAttributes, "repo.snapshot"),
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TurnCount: turnCount,
            ToolCallCount: toolCallCount,
            DurationMs: ReadInt(traceElement, "durationMs") ?? ReadDurationMs(traceElement),
            ErrorCount: errorCount,
            SuccessStatus: "not-evaluated",
            EvaluatorId: null,
            EvaluationNotes: null,
            EvaluatedAt: null,
            UnknownSpansJson: unknownSpans.Count == 0 ? null : unknownSpans,
            UnknownAttributesJson: CreateUnknownAttributesNode(resourceAttributes),
            AggregationNotes: aggregationNotes);
    }

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
    ];

    private static int? ReadExplicitCount(JsonElement element, JsonElement resourceAttributes, JsonElement observations, IReadOnlyList<string> candidateKeys)
    {
        return ReadFirstInt(element, candidateKeys)
            ?? ReadFirstIntFromObject(element, "attributes", candidateKeys)
            ?? ReadFirstIntFromObject(element, "metadata", candidateKeys)
            ?? ReadFirstInt(resourceAttributes, candidateKeys)
            ?? ReadFirstIntFromObservations(observations, candidateKeys);
    }

    private static int? ReadFirstIntFromObject(JsonElement element, string objectPropertyName, IReadOnlyList<string> candidateKeys)
    {
        return TryGetObject(element, objectPropertyName, out var nested)
            ? ReadFirstInt(nested, candidateKeys)
            : null;
    }

    private static int? ReadFirstIntFromObservations(JsonElement observations, IReadOnlyList<string> candidateKeys)
    {
        if (observations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var observation in observations.EnumerateArray())
        {
            var count = ReadFirstInt(observation, candidateKeys)
                ?? ReadFirstIntFromObject(observation, "attributes", candidateKeys);
            if (count.HasValue)
            {
                return count;
            }
        }

        return null;
    }

    private static int? ReadFirstInt(JsonElement element, IReadOnlyList<string> candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            var value = ReadInt(element, candidateKey);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsLlmObservation(JsonElement observation)
    {
        return HasStringValue(observation, "type", "generation")
            || HasStringValue(observation, "type", "chat")
            || HasAnyStringValue(observation, GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasAnyStringValueInObject(observation, "attributes", GenAiOperationKeys, "chat", "generate_content", "text_completion")
            || HasSpanName(observation, "chat");
    }

    private static bool IsToolObservation(JsonElement observation)
    {
        return HasStringValue(observation, "type", "tool")
            || HasStringValue(observation, "kind", "tool")
            || HasStringValue(observation, "category", "tool")
            || HasAnyStringValue(observation, GenAiToolNameKeys)
            || HasAnyStringValueInObject(observation, "attributes", GenAiToolNameKeys)
            || HasSpanName(observation, "execute_tool");
    }

    private static bool IsKnownNonCountedObservation(JsonElement observation)
    {
        return HasSpanName(observation, "invoke_agent")
            || HasSpanName(observation, "execute_hook")
            || HasSpanName(observation, "lifecycle_event")
            || HasAnyStringValue(observation, ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle")
            || HasAnyStringValueInObject(observation, "attributes", ["type", "kind", "category"], "permission", "approval", "hook", "lifecycle");
    }

    private static string CreateAggregationNotes(JsonElement observations, JsonArray unknownSpans)
    {
        var countSource = observations.ValueKind == JsonValueKind.Array
            ? "turn_count and tool_call_count are calculated from explicit count attributes when present, otherwise from classified observations."
            : "turn_count and tool_call_count require explicit count attributes when observations are missing.";

        return unknownSpans.Count == 0
            ? countSource
            : $"{countSource} Unknown observations are listed in unknown_spans_json.";
    }

    private static bool IsErrorObservation(JsonElement observation)
    {
        if (HasStringValue(observation, "level", "error")
            || HasStringValue(observation, "status", "error"))
        {
            return true;
        }

        return TryGetObject(observation, "statusMessage", out _)
            || ReadString(observation, "error") is not null;
    }

    private static bool HasStringValue(JsonElement element, string propertyName, string expected)
    {
        return ReadString(element, propertyName) is { } actual
            && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyStringValue(JsonElement element, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        foreach (var propertyName in propertyNames)
        {
            if (ReadString(element, propertyName) is not { } actual)
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

    private static bool HasAnyStringValueInObject(JsonElement element, string objectPropertyName, IReadOnlyList<string> propertyNames, params string[] expectedValues)
    {
        return TryGetObject(element, objectPropertyName, out var nested)
            && HasAnyStringValue(nested, propertyNames, expectedValues);
    }

    private static bool HasSpanName(JsonElement observation, string expectedName)
    {
        if (ReadString(observation, "name") is not { } actualName)
        {
            return false;
        }

        return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
            || actualName.StartsWith($"{expectedName} ", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject CreateUnknownSpanNode(JsonElement observation)
    {
        var node = new JsonObject();
        AddStringNode(node, "id", ReadString(observation, "id"));
        AddStringNode(node, "name", ReadString(observation, "name"));
        AddStringNode(node, "type", ReadString(observation, "type"));
        AddStringNode(node, "kind", ReadString(observation, "kind"));
        return node;
    }

    private static JsonObject? CreateUnknownAttributesNode(JsonElement resourceAttributes)
    {
        var unknown = new JsonObject();

        AddUnknownResourceAttributes(unknown, resourceAttributes);

        return unknown.Count == 0 ? null : unknown;
    }

    private static void AddUnknownResourceAttributes(JsonObject unknown, JsonElement resourceAttributes)
    {
        MeasurementSanitizer.AddUnknownResourceAttributes(unknown, resourceAttributes);
    }

    private static void AddStringNode(JsonObject node, string propertyName, string? value)
    {
        if (value is not null)
        {
            node[propertyName] = value;
        }
    }

    private static int? ReadDurationMs(JsonElement traceElement)
    {
        var durationSeconds = ReadDouble(traceElement, "duration");
        if (!durationSeconds.HasValue)
        {
            return null;
        }

        return (int)Math.Round(durationSeconds.Value * 1000, MidpointRounding.AwayFromZero);
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

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return ReadInt(property);
    }

    private static int? ReadInt(JsonElement element, string containerPropertyName, string propertyName)
    {
        return TryGetObject(element, containerPropertyName, out var container)
            ? ReadInt(container, propertyName)
            : null;
    }

    private static int? ReadInt(JsonElement property)
    {
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
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

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Array;
    }
}

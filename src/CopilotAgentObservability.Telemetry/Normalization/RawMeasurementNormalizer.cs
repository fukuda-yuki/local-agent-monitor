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
            var resourceAttributes = OtlpSpanReader.ReadResourceAttributes(resourceSpan);
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var traceId = OtlpSpanReader.ReadString(span, "traceId");
                    var key = string.IsNullOrWhiteSpace(traceId)
                        ? $"__missing_trace_id_{missingTraceIdIndex++}"
                        : traceId!;

                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new RawTraceGroup(traceId);
                        groups.Add(key, group);
                    }

                    OtlpSpanReader.MergeResourceAttributes(group.ResourceAttributes, resourceAttributes);
                    group.Spans.Add(OtlpSpanReader.CreateSpan(span));
                }
            }
        }

        return groups.Values.Select(CreateRow).ToArray();
    }

    private static MeasurementRow CreateRow(RawTraceGroup group)
    {
        var explicitTurnCount = OtlpSpanReader.ReadFirstInt(group.ResourceAttributes, TurnCountKeys);
        var explicitToolCallCount = OtlpSpanReader.ReadFirstInt(group.ResourceAttributes, ToolCallCountKeys);
        var observedTurnCount = 0;
        var observedToolCallCount = 0;
        var errorCount = 0;
        var unknownSpans = new JsonArray();
        ulong? minStart = null;
        ulong? maxEnd = null;

        // No-double-count token rollup: prefer invoke_agent tokens, fall back
        // to sum of chat/LLM spans only. Never sum both.
        long? invokeAgentInputSum = null;
        long? invokeAgentOutputSum = null;
        long? invokeAgentTotalSum = null;
        bool hasInvokeAgentTokens = false;
        long? chatInputSum = null;
        long? chatOutputSum = null;
        long? chatTotalSum = null;
        var spanIds = group.Spans
            .Select(span => span.SpanId)
            .Where(spanId => !string.IsNullOrEmpty(spanId))
            .Select(spanId => spanId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var span in group.Spans)
        {
            explicitTurnCount ??= OtlpSpanReader.ReadFirstInt(span.Attributes, TurnCountKeys);
            explicitToolCallCount ??= OtlpSpanReader.ReadFirstInt(span.Attributes, ToolCallCountKeys);

            var isInvokeAgent = OtlpSpanReader.HasSpanName(span, "invoke_agent");
            var isLlm = OtlpSpanReader.IsLlmSpan(span);

            var spanInput = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.InputTokenKeys);
            var spanOutput = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.OutputTokenKeys);
            var spanTotal = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.TotalTokenKeys);
            spanTotal ??= AddTokenCounts(spanInput, spanOutput);

            if (isInvokeAgent
                && HasUsage(spanInput, spanOutput, spanTotal)
                && IsRootSpan(span, spanIds))
            {
                invokeAgentInputSum = AddNullable(invokeAgentInputSum, spanInput);
                invokeAgentOutputSum = AddNullable(invokeAgentOutputSum, spanOutput);
                invokeAgentTotalSum = AddNullable(invokeAgentTotalSum, spanTotal);
                hasInvokeAgentTokens = true;
            }

            if (isLlm)
            {
                chatInputSum = AddNullable(chatInputSum, spanInput);
                chatOutputSum = AddNullable(chatOutputSum, spanOutput);
                chatTotalSum = AddNullable(chatTotalSum, spanTotal);
            }

            if (OtlpSpanReader.IsKnownNonCountedSpan(span))
            {
                // Non-counted spans may still carry error metadata.
            }
            else if (OtlpSpanReader.IsToolSpan(span))
            {
                observedToolCallCount++;
            }
            else if (isLlm)
            {
                observedTurnCount++;
            }
            else
            {
                unknownSpans.Add(CreateUnknownSpanNode(span));
            }

            if (OtlpSpanReader.IsErrorSpan(span))
            {
                errorCount++;
            }

            errorCount += OtlpSpanReader.CountErrorEvents(span);

            if (span.StartTimeUnixNano.HasValue)
            {
                minStart = minStart.HasValue ? Math.Min(minStart.Value, span.StartTimeUnixNano.Value) : span.StartTimeUnixNano.Value;
            }

            if (span.EndTimeUnixNano.HasValue)
            {
                maxEnd = maxEnd.HasValue ? Math.Max(maxEnd.Value, span.EndTimeUnixNano.Value) : span.EndTimeUnixNano.Value;
            }
        }

        int? inputTokens;
        int? outputTokens;
        int? totalTokens;

        if (hasInvokeAgentTokens)
        {
            inputTokens = ToTokenCount(invokeAgentInputSum);
            outputTokens = ToTokenCount(invokeAgentOutputSum);
            totalTokens = ToTokenCount(invokeAgentTotalSum);
        }
        else
        {
            inputTokens = ToTokenCount(chatInputSum);
            outputTokens = ToTokenCount(chatOutputSum);
            totalTokens = ToTokenCount(chatTotalSum);
        }

        totalTokens ??= AddTokenCounts(inputTokens, outputTokens);

        var turnCount = explicitTurnCount ?? observedTurnCount;
        var toolCallCount = explicitToolCallCount ?? observedToolCallCount;

        return new MeasurementRow(
            TraceId: group.TraceId,
            ExperimentId: OtlpSpanReader.ReadString(group.ResourceAttributes, "experiment.id"),
            ClientKind: OtlpSpanReader.ReadString(group.ResourceAttributes, "client.kind"),
            TaskId: OtlpSpanReader.ReadString(group.ResourceAttributes, "task.id"),
            TaskCategory: OtlpSpanReader.ReadString(group.ResourceAttributes, "task.category"),
            TaskRunIndex: OtlpSpanReader.ReadInt(group.ResourceAttributes, "task.run_index"),
            ExperimentCondition: OtlpSpanReader.ReadString(group.ResourceAttributes, "experiment.condition"),
            PromptVersion: OtlpSpanReader.ReadString(group.ResourceAttributes, "prompt.version"),
            AgentVariant: OtlpSpanReader.ReadString(group.ResourceAttributes, "agent.variant"),
            RepoSnapshot: OtlpSpanReader.ReadString(group.ResourceAttributes, "repo.snapshot"),
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

    private static JsonObject CreateUnknownSpanNode(OtlpSpanReader.RawSpanInfo span)
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

    private static long? AddNullable(long? current, int? value)
    {
        return value.HasValue ? (current ?? 0L) + value.Value : current;
    }

    private static int? AddTokenCounts(int? left, int? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return null;
        }

        var sum = (long)left.Value + right.Value;
        return ToTokenCount(sum);
    }

    private static int? ToTokenCount(long? value)
    {
        return value.HasValue && value.Value >= int.MinValue && value.Value <= int.MaxValue
            ? (int)value.Value
            : null;
    }

    private static bool HasUsage(int? inputTokens, int? outputTokens, int? totalTokens) =>
        inputTokens.HasValue || outputTokens.HasValue || totalTokens.HasValue;

    private static bool IsRootSpan(OtlpSpanReader.RawSpanInfo span, HashSet<string> spanIds) =>
        string.IsNullOrEmpty(span.ParentSpanId) || !spanIds.Contains(span.ParentSpanId);

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

        public List<OtlpSpanReader.RawSpanInfo> Spans { get; } = [];
    }
}

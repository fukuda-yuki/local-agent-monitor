namespace CopilotAgentObservability.Telemetry;

internal static class MonitorSpanProjectionBuilder
{
    private static readonly string[] AgentNameKeys =
    [
        "gen_ai.agent.name",
        "github.copilot.agent.name",
    ];

    private static readonly string[] RequestModelKeys =
    [
        "gen_ai.request.model",
    ];

    private static readonly string[] ResponseModelKeys =
    [
        "gen_ai.response.model",
    ];

    private static readonly string[] ReasoningTokenKeys =
    [
        "gen_ai.usage.reasoning.output_tokens",
        "gen_ai.usage.reasoning_tokens",
    ];

    private static readonly string[] CacheReadTokenKeys =
    [
        "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.usage.cache.read_input_tokens",
    ];

    private static readonly string[] CacheCreationTokenKeys =
    [
        "gen_ai.usage.cache_creation.input_tokens",
        "gen_ai.usage.cache.creation_input_tokens",
    ];

    private static readonly string[] FinishReasonKeys =
    [
        "gen_ai.response.finish_reasons",
        "gen_ai.finish_reasons",
    ];

    private static readonly string[] ConversationIdKeys =
    [
        "gen_ai.conversation.id",
        "conversation_id",
    ];

    private static readonly string[] ToolTypeKeys =
    [
        "gen_ai.tool.type",
        "github.copilot.tool.parameters.tool_type",
    ];

    private static readonly string[] McpToolNameKeys =
    [
        "github.copilot.tool.parameters.mcp_tool_name",
    ];

    private static readonly string[] McpServerHashKeys =
    [
        "github.copilot.tool.parameters.mcp_server_name_hash",
    ];

    private static readonly string[] ErrorTypeKeys =
    [
        "error.type",
    ];

    private static readonly HashSet<string> KnownFinishReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "length", "content_filter", "tool_calls", "function_call",
        "end_turn", "max_tokens", "stop_sequence",
    };

    public static IReadOnlyList<MonitorSpanProjection> Build(RawTelemetryRecord record)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var projections = new List<MonitorSpanProjection>();
        var ordinal = 0;

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var spanElement in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var span = OtlpSpanReader.CreateSpan(spanElement);
                    projections.Add(ProjectSpan(span, ordinal));
                    ordinal++;
                }
            }
        }

        return projections;
    }

    private static MonitorSpanProjection ProjectSpan(OtlpSpanReader.RawSpanInfo span, int ordinal)
    {
        var operation = ClassifyOperation(span);
        var category = ClassifyCategory(span, operation);

        var toolName = MeasurementSanitizer.SanitizeFreeFormName(
            OtlpSpanReader.ReadFirstString(span.Attributes, OtlpSpanReader.GenAiToolNameKeys));
        var toolType = OtlpSpanReader.ReadFirstString(span.Attributes, ToolTypeKeys);
        var mcpToolName = MeasurementSanitizer.SanitizeFreeFormName(
            OtlpSpanReader.ReadFirstString(span.Attributes, McpToolNameKeys));
        var mcpServerHash = OtlpSpanReader.ReadFirstString(span.Attributes, McpServerHashKeys);
        var agentName = MeasurementSanitizer.SanitizeFreeFormName(
            OtlpSpanReader.ReadFirstString(span.Attributes, AgentNameKeys));

        var requestModel = OtlpSpanReader.ReadFirstString(span.Attributes, RequestModelKeys);
        var responseModel = OtlpSpanReader.ReadFirstString(span.Attributes, ResponseModelKeys);

        var inputTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.InputTokenKeys);
        var outputTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.OutputTokenKeys);
        var totalTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, OtlpSpanReader.TotalTokenKeys);
        var reasoningTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, ReasoningTokenKeys);
        var cacheReadTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, CacheReadTokenKeys);
        var cacheCreationTokens = OtlpSpanReader.ReadFirstInt(span.Attributes, CacheCreationTokenKeys);

        totalTokens ??= AddTokenCounts(inputTokens, outputTokens);

        var status = OtlpSpanReader.IsErrorSpan(span) ? "error" : "ok";
        var errorType = SanitizeErrorType(span.Attributes);
        var finishReasons = SanitizeFinishReasons(span.Attributes);

        var conversationId = OtlpSpanReader.ReadFirstString(span.Attributes, ConversationIdKeys);

        return new MonitorSpanProjection(
            TraceId: span.TraceId,
            SpanId: span.SpanId,
            ParentSpanId: span.ParentSpanId,
            SpanOrdinal: ordinal,
            Operation: operation,
            Category: category,
            ToolName: toolName,
            ToolType: toolType,
            McpToolName: mcpToolName,
            McpServerHash: mcpServerHash,
            AgentName: agentName,
            RequestModel: requestModel,
            ResponseModel: responseModel,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            ReasoningTokens: reasoningTokens,
            CacheReadTokens: cacheReadTokens,
            CacheCreationTokens: cacheCreationTokens,
            Status: status,
            ErrorType: errorType,
            FinishReasons: finishReasons,
            ConversationId: conversationId,
            DurationMs: ComputeDurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
            StartTime: FormatTimestamp(span.StartTimeUnixNano),
            EndTime: FormatTimestamp(span.EndTimeUnixNano));
    }

    private static string? ClassifyOperation(OtlpSpanReader.RawSpanInfo span)
    {
        var opName = OtlpSpanReader.ReadFirstString(span.Attributes, OtlpSpanReader.GenAiOperationKeys);
        if (opName is not null)
        {
            return opName.ToLowerInvariant() switch
            {
                "invoke_agent" => "invoke_agent",
                "chat" => "chat",
                "execute_tool" => "execute_tool",
                "execute_hook" => "execute_hook",
                "generate_content" or "text_completion" => "chat",
                _ => opName,
            };
        }

        if (OtlpSpanReader.HasSpanName(span, "invoke_agent")) return "invoke_agent";
        if (OtlpSpanReader.HasSpanName(span, "chat")) return "chat";
        if (OtlpSpanReader.HasSpanName(span, "execute_tool")) return "execute_tool";
        if (OtlpSpanReader.HasSpanName(span, "execute_hook")) return "execute_hook";

        return null;
    }

    private static string ClassifyCategory(OtlpSpanReader.RawSpanInfo span, string? operation)
    {
        if (OtlpSpanReader.IsErrorSpan(span))
        {
            return "error";
        }

        return operation switch
        {
            "invoke_agent" => "agent_invocation",
            "chat" => "llm_call",
            "execute_tool" => "tool_call",
            "execute_hook" => "hook",
            _ when OtlpSpanReader.IsLlmSpan(span) => "llm_call",
            _ when OtlpSpanReader.IsToolSpan(span) => "tool_call",
            _ => "unknown",
        };
    }

    private static string? SanitizeErrorType(JsonObject attributes)
    {
        var errorType = OtlpSpanReader.ReadFirstString(attributes, ErrorTypeKeys);
        if (string.IsNullOrWhiteSpace(errorType))
        {
            return null;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(errorType, @"^[A-Za-z0-9._]+$"))
        {
            return null;
        }

        return errorType.Length > MeasurementSanitizer.MaxSanitizedNameLength
            ? errorType[..MeasurementSanitizer.MaxSanitizedNameLength]
            : errorType;
    }

    private static string? SanitizeFinishReasons(JsonObject attributes)
    {
        var raw = OtlpSpanReader.ReadFirstString(attributes, FinishReasonKeys);
        if (raw is null)
        {
            return null;
        }

        // finish_reasons can be a JSON array (e.g. ["stop"]) or a plain string
        var tokens = new List<string>();
        if (raw.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var token = item.GetString();
                        if (token is not null)
                        {
                            tokens.Add(token);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else
        {
            tokens.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var sanitized = new List<string>();
        foreach (var token in tokens)
        {
            if (KnownFinishReasons.Contains(token))
            {
                sanitized.Add(token);
            }
            else
            {
                var guarded = MeasurementSanitizer.SanitizeFreeFormName(token);
                if (guarded is not null)
                {
                    sanitized.Add(guarded);
                }
            }
        }

        return sanitized.Count > 0 ? string.Join(",", sanitized) : null;
    }

    private static string? FormatTimestamp(ulong? unixNano)
    {
        if (!unixNano.HasValue)
        {
            return null;
        }

        var ms = (long)(unixNano.Value / 1_000_000);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o", CultureInfo.InvariantCulture);
    }

    private static double? ComputeDurationMs(ulong? start, ulong? end)
    {
        if (!start.HasValue || !end.HasValue || end.Value < start.Value)
        {
            return null;
        }

        return (end.Value - start.Value) / 1_000_000d;
    }

    private static int? AddTokenCounts(int? left, int? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return null;
        }

        var sum = (long)left.Value + right.Value;
        return sum >= int.MinValue && sum <= int.MaxValue
            ? (int)sum
            : null;
    }
}

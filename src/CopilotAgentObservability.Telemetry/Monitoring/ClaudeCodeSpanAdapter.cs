namespace CopilotAgentObservability.Telemetry;

internal static class ClaudeCodeSpanAdapter
{
    private static readonly HashSet<string> RecognizedSpanNames = new(StringComparer.Ordinal)
    {
        "claude_code.interaction",
        "claude_code.llm_request",
        "claude_code.tool",
        "claude_code.tool.blocked_on_user",
        "claude_code.tool.execution",
        "claude_code.hook",
    };

    public static bool TryProject(
        OtlpSpanReader.RawSpanInfo span,
        int ordinal,
        out MonitorSpanProjection projection)
    {
        if (span.Name is null || !RecognizedSpanNames.Contains(span.Name))
        {
            projection = null!;
            return false;
        }

        var isLlmRequest = string.Equals(span.Name, "claude_code.llm_request", StringComparison.Ordinal);
        var isTool = string.Equals(span.Name, "claude_code.tool", StringComparison.Ordinal);
        var inputTokens = isLlmRequest ? OtlpSpanReader.ReadInt(span.Attributes, "input_tokens") : null;
        var outputTokens = isLlmRequest ? OtlpSpanReader.ReadInt(span.Attributes, "output_tokens") : null;
        var cacheReadTokens = isLlmRequest ? OtlpSpanReader.ReadInt(span.Attributes, "cache_read_tokens") : null;
        var cacheCreationTokens = isLlmRequest ? OtlpSpanReader.ReadInt(span.Attributes, "cache_creation_tokens") : null;
        var status = ResolveStatus(span.StatusCode);

        projection = new MonitorSpanProjection(
            TraceId: span.TraceId,
            SpanId: span.SpanId,
            ParentSpanId: span.ParentSpanId,
            SpanOrdinal: ordinal,
            Operation: ClassifyOperation(span.Name),
            Category: ClassifyCategory(span.Name, status),
            ToolName: isTool
                ? MeasurementSanitizer.SanitizeFreeFormName(OtlpSpanReader.ReadString(span.Attributes, "tool_name"))
                : null,
            ToolType: null,
            McpToolName: null,
            McpServerHash: null,
            AgentName: null,
            RequestModel: isLlmRequest
                ? OtlpSpanReader.ReadString(span.Attributes, "gen_ai.request.model")
                : null,
            ResponseModel: null,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: null,
            ReasoningTokens: null,
            CacheReadTokens: cacheReadTokens,
            CacheCreationTokens: cacheCreationTokens,
            Status: status,
            ErrorType: null,
            FinishReasons: null,
            ConversationId: null,
            DurationMs: ComputeDurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
            StartTime: FormatTimestamp(span.StartTimeUnixNano),
            EndTime: FormatTimestamp(span.EndTimeUnixNano));
        return true;
    }

    private static string? ClassifyOperation(string spanName) => spanName switch
    {
        "claude_code.llm_request" => "chat",
        "claude_code.tool" => "execute_tool",
        _ => null,
    };

    private static string ClassifyCategory(string spanName, string? status)
    {
        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            return "error";
        }

        return spanName switch
        {
            "claude_code.llm_request" => "llm_call",
            "claude_code.tool" => "tool_call",
            "claude_code.hook" => "hook",
            _ => "unknown",
        };
    }

    private static string? ResolveStatus(string? statusCode) => statusCode switch
    {
        "0" or "1" => "ok",
        "2" => "error",
        _ => null,
    };

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
}

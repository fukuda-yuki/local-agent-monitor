namespace CopilotAgentObservability.Telemetry;

internal sealed record MonitorTraceRollup(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int TurnCount,
    int AgentInvocationCount,
    double? DurationMs,
    string? PrimaryModel);

internal static class MonitorTraceRollupBuilder
{
    public static MonitorTraceRollup ComputeRollup(IReadOnlyList<MonitorSpanProjection> spans)
    {
        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;
        var turnCount = 0;
        var agentInvocationCount = 0;
        double? minStartMs = null;
        double? maxEndMs = null;
        var modelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Separate invoke_agent and chat spans for no-double-count rule.
        long? rootInputSum = null;
        long? rootOutputSum = null;
        long? rootTotalSum = null;
        bool hasRootInvokeAgentUsage = false;
        long? chatInputSum = null;
        long? chatOutputSum = null;
        long? chatTotalSum = null;

        var spanIds = spans
            .Select(span => span.SpanId)
            .Where(spanId => !string.IsNullOrEmpty(spanId))
            .Select(spanId => spanId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var span in spans)
        {
            var spanTotal = span.TotalTokens ?? AddTokenCounts(span.InputTokens, span.OutputTokens);

            if (string.Equals(span.Operation, "invoke_agent", StringComparison.Ordinal))
            {
                agentInvocationCount++;
                if (HasUsage(span) && IsRootSpan(span, spanIds))
                {
                    rootInputSum = AddNullable(rootInputSum, span.InputTokens);
                    rootOutputSum = AddNullable(rootOutputSum, span.OutputTokens);
                    rootTotalSum = AddNullable(rootTotalSum, spanTotal);
                    hasRootInvokeAgentUsage = true;
                }
            }

            if (string.Equals(span.Operation, "chat", StringComparison.Ordinal)
                || string.Equals(span.Category, "llm_call", StringComparison.Ordinal))
            {
                turnCount++;
                chatInputSum = AddNullable(chatInputSum, span.InputTokens);
                chatOutputSum = AddNullable(chatOutputSum, span.OutputTokens);
                chatTotalSum = AddNullable(chatTotalSum, spanTotal);

                var model = span.ResponseModel ?? span.RequestModel;
                if (model is not null)
                {
                    modelCounts[model] = modelCounts.TryGetValue(model, out var count) ? count + 1 : 1;
                }
            }

            if (span.StartTime is not null)
            {
                if (DateTimeOffset.TryParse(span.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start))
                {
                    var ms = start.ToUnixTimeMilliseconds();
                    minStartMs = minStartMs.HasValue ? Math.Min(minStartMs.Value, ms) : ms;
                }
            }

            if (span.EndTime is not null)
            {
                if (DateTimeOffset.TryParse(span.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end))
                {
                    var ms = end.ToUnixTimeMilliseconds();
                    maxEndMs = maxEndMs.HasValue ? Math.Max(maxEndMs.Value, ms) : ms;
                }
            }
        }

        // No-double-count: prefer root invoke_agent token sums; otherwise use
        // chat/LLM sums. Child invoke_agent usage is never promoted to the trace
        // headline unless the root/parent agent emitted that aggregate.
        if (hasRootInvokeAgentUsage)
        {
            inputTokens = ToTokenCount(rootInputSum);
            outputTokens = ToTokenCount(rootOutputSum);
            totalTokens = ToTokenCount(rootTotalSum);
        }
        else
        {
            inputTokens = ToTokenCount(chatInputSum);
            outputTokens = ToTokenCount(chatOutputSum);
            totalTokens = ToTokenCount(chatTotalSum);
        }

        totalTokens ??= AddTokenCounts(inputTokens, outputTokens);

        double? durationMs = minStartMs.HasValue && maxEndMs.HasValue && maxEndMs.Value >= minStartMs.Value
            ? maxEndMs.Value - minStartMs.Value
            : null;

        string? primaryModel = null;
        if (modelCounts.Count > 0)
        {
            primaryModel = modelCounts.MaxBy(kv => kv.Value).Key;
        }

        return new MonitorTraceRollup(
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TurnCount: turnCount,
            AgentInvocationCount: agentInvocationCount,
            DurationMs: durationMs,
            PrimaryModel: primaryModel);
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

    private static bool HasUsage(MonitorSpanProjection span) =>
        span.InputTokens.HasValue || span.OutputTokens.HasValue || span.TotalTokens.HasValue;

    private static bool IsRootSpan(MonitorSpanProjection span, HashSet<string> spanIds) =>
        string.IsNullOrEmpty(span.ParentSpanId) || !spanIds.Contains(span.ParentSpanId);
}

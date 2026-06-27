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
        MonitorSpanProjection? rootInvokeAgent = null;
        int? chatInputSum = null;
        int? chatOutputSum = null;
        int? chatTotalSum = null;

        foreach (var span in spans)
        {
            if (string.Equals(span.Operation, "invoke_agent", StringComparison.Ordinal))
            {
                agentInvocationCount++;
                // Use the first (root) invoke_agent with tokens as the trace total.
                if (rootInvokeAgent is null && span.InputTokens.HasValue)
                {
                    rootInvokeAgent = span;
                }
            }

            if (string.Equals(span.Operation, "chat", StringComparison.Ordinal)
                || string.Equals(span.Category, "llm_call", StringComparison.Ordinal))
            {
                turnCount++;
                chatInputSum = AddNullable(chatInputSum, span.InputTokens);
                chatOutputSum = AddNullable(chatOutputSum, span.OutputTokens);
                chatTotalSum = AddNullable(chatTotalSum, span.TotalTokens);

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

        // No-double-count: prefer invoke_agent tokens, fall back to chat sum.
        if (rootInvokeAgent is not null)
        {
            inputTokens = rootInvokeAgent.InputTokens;
            outputTokens = rootInvokeAgent.OutputTokens;
            totalTokens = rootInvokeAgent.TotalTokens;
        }
        else
        {
            inputTokens = chatInputSum;
            outputTokens = chatOutputSum;
            totalTokens = chatTotalSum;
        }

        totalTokens ??= inputTokens.HasValue && outputTokens.HasValue
            ? inputTokens + outputTokens
            : null;

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

    private static int? AddNullable(int? current, int? value)
    {
        return value.HasValue ? (current ?? 0) + value.Value : current;
    }
}

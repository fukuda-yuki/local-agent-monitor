namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 (D044) rollup additions: cache token sums follow the same
/// root-invoke-agent-else-chat no-double-count branch as the token headline, and
/// trace_status is ok / recovered / unrecovered (NULL for pre-v4 rows).
/// </summary>
public class MonitorTraceRollupCacheStatusTests
{
    [Fact]
    public void ComputeRollup_ChatSpans_SumsCacheTokens()
    {
        var spans = new[]
        {
            ChatSpan("s1", ordinal: 0, cacheRead: 800, cacheCreation: 100),
            ChatSpan("s2", ordinal: 1, cacheRead: 200, cacheCreation: null),
        };

        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(1000, rollup.CacheReadTokens);
        Assert.Equal(100, rollup.CacheCreationTokens);
    }

    [Fact]
    public void ComputeRollup_RootInvokeAgentUsage_UsesRootCacheSumsNotChatSums()
    {
        var spans = new[]
        {
            AgentSpan("root", parent: null, ordinal: 0, inputTokens: 2000, cacheRead: 1500, cacheCreation: 50),
            ChatSpan("child", ordinal: 1, cacheRead: 999, cacheCreation: 999) with { ParentSpanId = "root" },
        };

        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(1500, rollup.CacheReadTokens);
        Assert.Equal(50, rollup.CacheCreationTokens);
    }

    [Fact]
    public void ComputeRollup_NoCacheAttributes_KeepsCacheSumsNull()
    {
        var spans = new[] { ChatSpan("s1", ordinal: 0, cacheRead: null, cacheCreation: null) };

        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Null(rollup.CacheReadTokens);
        Assert.Null(rollup.CacheCreationTokens);
    }

    [Fact]
    public void ComputeRollup_NoErrorSpans_TraceStatusOk()
    {
        var spans = new[]
        {
            ChatSpan("s1", ordinal: 0, cacheRead: null, cacheCreation: null),
            ToolSpan("s2", ordinal: 1, status: "ok", startTime: "2026-07-01T00:00:10Z"),
        };

        Assert.Equal("ok", MonitorTraceRollupBuilder.ComputeRollup(spans).TraceStatus);
    }

    [Fact]
    public void ComputeRollup_ErrorFollowedByLaterSuccess_TraceStatusRecovered()
    {
        var spans = new[]
        {
            ToolSpan("fail", ordinal: 0, status: "error", startTime: "2026-07-01T00:00:10Z"),
            ToolSpan("retry", ordinal: 1, status: "ok", startTime: "2026-07-01T00:00:20Z"),
        };

        Assert.Equal("recovered", MonitorTraceRollupBuilder.ComputeRollup(spans).TraceStatus);
    }

    [Fact]
    public void ComputeRollup_LastSpanByStartTimeIsError_TraceStatusUnrecovered()
    {
        // Ordinal order says the ok span is last, but StartTime says the error span
        // is last — StartTime wins.
        var spans = new[]
        {
            ToolSpan("boom", ordinal: 0, status: "error", startTime: "2026-07-01T00:00:30Z"),
            ToolSpan("earlier-ok", ordinal: 1, status: "ok", startTime: "2026-07-01T00:00:10Z"),
        };

        Assert.Equal("unrecovered", MonitorTraceRollupBuilder.ComputeRollup(spans).TraceStatus);
    }

    [Fact]
    public void ComputeRollup_NoStartTimes_FallsBackToSpanOrdinal()
    {
        var spans = new[]
        {
            ToolSpan("fail", ordinal: 0, status: "error", startTime: null),
            ToolSpan("ok", ordinal: 1, status: "ok", startTime: null),
        };

        Assert.Equal("recovered", MonitorTraceRollupBuilder.ComputeRollup(spans).TraceStatus);

        var reversed = new[]
        {
            ToolSpan("ok", ordinal: 0, status: "ok", startTime: null),
            ToolSpan("fail", ordinal: 1, status: "error", startTime: null),
        };

        Assert.Equal("unrecovered", MonitorTraceRollupBuilder.ComputeRollup(reversed).TraceStatus);
    }

    [Fact]
    public void ApplySpanProjection_WritesCacheAndStatusColumns_ReadableFromTraceRows()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();

        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-cache",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: CacheErrorPayload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(3));

        var row = store.GetMonitorTrace("trace-cache");

        Assert.NotNull(row);
        Assert.Equal(700, row!.CacheReadTokens);
        Assert.Equal(90, row.CacheCreationTokens);
        Assert.Equal("recovered", row.TraceStatus);

        var listed = Assert.Single(store.ListMonitorTraces(0, 10).Items);
        Assert.Equal(700, listed.CacheReadTokens);
        Assert.Equal(90, listed.CacheCreationTokens);
        Assert.Equal("recovered", listed.TraceStatus);
    }

    [Fact]
    public void TraceRowsWithoutSpanProjection_KeepNullCacheAndStatus()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();

        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-old",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: """{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"trace-old","spanId":"01","name":"chat"}]}]}]}""");
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));

        var row = store.GetMonitorTrace("trace-old");

        Assert.NotNull(row);
        Assert.Null(row!.CacheReadTokens);
        Assert.Null(row.CacheCreationTokens);
        Assert.Null(row.TraceStatus);
    }

    /// <summary>
    /// One chat turn with cache usage, a failed tool call, then a successful retry
    /// later — cache sums 700/90 and trace_status recovered.
    /// </summary>
    private const string CacheErrorPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-cache","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1000000000",
           "endTimeUnixNano":"2000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"700"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"90"}}
           ]},
          {"traceId":"trace-cache","spanId":"2222","parentSpanId":"1111","name":"execute_tool str_replace",
           "startTimeUnixNano":"3000000000",
           "endTimeUnixNano":"4000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"str_replace"}}
           ]},
          {"traceId":"trace-cache","spanId":"3333","parentSpanId":"1111","name":"execute_tool str_replace",
           "startTimeUnixNano":"5000000000",
           "endTimeUnixNano":"6000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"str_replace"}}
           ]}
        ]}]}]}
        """;

    private static MonitorSpanProjection ChatSpan(string spanId, int ordinal, int? cacheRead, int? cacheCreation) =>
        BaseSpan(spanId, ordinal) with
        {
            Operation = "chat",
            Category = "llm_call",
            InputTokens = 1000,
            OutputTokens = 100,
            TotalTokens = 1100,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
        };

    private static MonitorSpanProjection AgentSpan(string spanId, string? parent, int ordinal, int inputTokens, int? cacheRead, int? cacheCreation) =>
        BaseSpan(spanId, ordinal) with
        {
            ParentSpanId = parent,
            Operation = "invoke_agent",
            Category = "agent_invocation",
            InputTokens = inputTokens,
            OutputTokens = 100,
            TotalTokens = inputTokens + 100,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
        };

    private static MonitorSpanProjection ToolSpan(string spanId, int ordinal, string status, string? startTime) =>
        BaseSpan(spanId, ordinal) with
        {
            Operation = "execute_tool",
            Category = "tool_call",
            Status = status,
            StartTime = startTime,
        };

    private static MonitorSpanProjection BaseSpan(string spanId, int ordinal) => new(
        TraceId: "trace-rollup",
        SpanId: spanId,
        ParentSpanId: null,
        SpanOrdinal: ordinal,
        Operation: null,
        Category: null,
        ToolName: null,
        ToolType: null,
        McpToolName: null,
        McpServerHash: null,
        AgentName: null,
        RequestModel: null,
        ResponseModel: null,
        InputTokens: null,
        OutputTokens: null,
        TotalTokens: null,
        ReasoningTokens: null,
        CacheReadTokens: null,
        CacheCreationTokens: null,
        Status: "ok",
        ErrorType: null,
        FinishReasons: null,
        ConversationId: null,
        DurationMs: null,
        StartTime: null,
        EndTime: null);
}

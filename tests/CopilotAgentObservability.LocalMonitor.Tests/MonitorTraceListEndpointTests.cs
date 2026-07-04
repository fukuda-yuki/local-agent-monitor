using System.Net;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 sanitized trace-list endpoint (`GET /api/monitor/trace-list`,
/// D042/D044): filters, tokens-desc default sort, offset paging, cache /
/// trace_status fields, and the no-prompt sanitized invariant.
/// </summary>
public class MonitorTraceListEndpointTests
{
    [Fact]
    public async Task TraceList_DefaultSort_TokensDescendingWithNullTokensLast()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var page = await GetJsonAsync(host, "/api/monitor/trace-list");
        var root = page.RootElement;

        Assert.Equal(4, root.GetProperty("total_matched").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.Equal(50, root.GetProperty("limit").GetInt32());
        var ids = root.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString())
            .ToList();
        Assert.Equal(["trace-big", "trace-unrecovered", "trace-recovered", "trace-unknown"], ids);
    }

    [Fact]
    public async Task TraceList_ItemsCarryCacheAndTraceStatusFields()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var page = await GetJsonAsync(host, "/api/monitor/trace-list?q=trace-big");
        var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal("trace-big", item.GetProperty("trace_id").GetString());
        Assert.Equal(400, item.GetProperty("cache_read_tokens").GetInt32());
        Assert.Equal(20, item.GetProperty("cache_creation_tokens").GetInt32());
        Assert.Equal("ok", item.GetProperty("trace_status").GetString());
    }

    [Theory]
    [InlineData("status=ok", "trace-big")]
    [InlineData("status=recovered", "trace-recovered")]
    [InlineData("status=unrecovered", "trace-unrecovered")]
    [InlineData("status=unknown", "trace-unknown")]
    public async Task TraceList_StatusFilter_MatchesRollupStatus(string statusQuery, string expectedTraceId)
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var page = await GetJsonAsync(host, $"/api/monitor/trace-list?{statusQuery}");
        var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(expectedTraceId, item.GetProperty("trace_id").GetString());
    }

    [Fact]
    public async Task TraceList_ModelFilterAndTraceIdSearch_Filter()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var modelPage = await GetJsonAsync(host, "/api/monitor/trace-list?model=gpt-4o");
        var modelIds = modelPage.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString())
            .ToList();
        Assert.Equal(["trace-big", "trace-unrecovered"], modelIds);

        var searchPage = await GetJsonAsync(host, "/api/monitor/trace-list?q=recover");
        var searchIds = searchPage.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString())
            .ToList();
        Assert.Equal(2, searchIds.Count);
        Assert.Contains("trace-recovered", searchIds);
        Assert.Contains("trace-unrecovered", searchIds);
    }

    [Fact]
    public async Task TraceList_OffsetPaging_ReturnsStablePagesAndTotal()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var first = await GetJsonAsync(host, "/api/monitor/trace-list?limit=2");
        var second = await GetJsonAsync(host, "/api/monitor/trace-list?limit=2&offset=2");

        Assert.Equal(4, first.RootElement.GetProperty("total_matched").GetInt32());
        Assert.Equal(4, second.RootElement.GetProperty("total_matched").GetInt32());
        var firstIds = first.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString()).ToList();
        var secondIds = second.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString()).ToList();
        Assert.Equal(["trace-big", "trace-unrecovered"], firstIds);
        Assert.Equal(["trace-recovered", "trace-unknown"], secondIds);
    }

    [Fact]
    public async Task TraceList_TimeSort_OrdersByLastSeenDescending()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        var page = await GetJsonAsync(host, "/api/monitor/trace-list?sort=time");
        var ids = page.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString())
            .ToList();

        // Seed minutes: unknown=4, unrecovered=3, recovered=2, big=1.
        Assert.Equal(["trace-unknown", "trace-unrecovered", "trace-recovered", "trace-big"], ids);
    }

    [Fact]
    public async Task TraceList_PeriodFilter_ExcludesOldRows()
    {
        using var temp = new MonitorTempDirectory();
        var store = SeedDefaultTraces(temp);
        await using var host = await StartHostAsync(temp);

        // Seeded rows sit at the 1970 epoch — far outside any 30d window.
        var page = await GetJsonAsync(host, "/api/monitor/trace-list?period=30d");

        Assert.Equal(0, page.RootElement.GetProperty("total_matched").GetInt32());
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/monitor/trace-list?status=broken")]
    [InlineData("/api/monitor/trace-list?sort=prompt")]
    [InlineData("/api/monitor/trace-list?offset=-1")]
    [InlineData("/api/monitor/trace-list?limit=0")]
    [InlineData("/api/monitor/trace-list?limit=201")]
    [InlineData("/api/monitor/trace-list?period=90d")]
    public async Task TraceList_RejectsInvalidQueryWith400(string path)
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_query", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TraceList_NeverReturnsRawContentOrPii()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedTrace(store, "trace-pii", SensitivePayload, minute: 1);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/api/monitor/trace-list");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("trace-pii", json);
        foreach (var marker in new[] { "SECRET_PROMPT_TEXT_MARKER", "SECRET_TOOL_ARGS_MARKER", "USER-ID-SECRET-MARKER", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, json);
        }
    }

    /// <summary>
    /// trace-big: 5000 tokens / gpt-4o / ok / cache 400+20 (minute 1).
    /// trace-recovered: 1000 tokens / gpt-4.1 / recovered (minute 2).
    /// trace-unrecovered: 3000 tokens / gpt-4o / unrecovered (minute 3).
    /// trace-unknown: trace-projected only (no span projection) → NULL rollup (minute 4).
    /// </summary>
    private static RawTelemetryStore SeedDefaultTraces(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedTrace(store, "trace-big", ChatPayload("trace-big", "gpt-4o", input: 4000, output: 1000, cacheRead: 400, cacheCreation: 20), minute: 1);
        SeedTrace(store, "trace-recovered", RecoveredPayload("trace-recovered", "gpt-4.1", input: 800, output: 200), minute: 2);
        SeedTrace(store, "trace-unrecovered", UnrecoveredPayload("trace-unrecovered", "gpt-4o", input: 2400, output: 600), minute: 3);
        SeedTrace(store, "trace-unknown", ChatPayload("trace-unknown", "gpt-4o", input: 10, output: 5, cacheRead: null, cacheCreation: null), minute: 4, spanProjection: false);
        return store;
    }

    private static void SeedTrace(RawTelemetryStore store, string traceId, string payloadJson, int minute, bool spanProjection = true)
    {
        var receivedAt = DateTimeOffset.UnixEpoch.AddMinutes(minute);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), receivedAt);
        if (spanProjection)
        {
            store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), receivedAt);
        }
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });

    private static string ChatPayload(string traceId, string model, int input, int output, int? cacheRead, int? cacheCreation)
    {
        var cacheAttributes = string.Empty;
        if (cacheRead is not null)
        {
            cacheAttributes += $$$""",{"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"{{{cacheRead}}}"}}""";
        }

        if (cacheCreation is not null)
        {
            cacheAttributes += $$$""",{"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"{{{cacheCreation}}}"}}""";
        }

        return $$$"""
            {"resourceSpans":[{"resource":{"attributes":[
              {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
            ]},"scopeSpans":[{"spans":[
              {"traceId":"{{{traceId}}}","spanId":"1111","name":"chat {{{model}}}","attributes":[
                {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                {"key":"gen_ai.response.model","value":{"stringValue":"{{{model}}}"}},
                {"key":"gen_ai.usage.input_tokens","value":{"intValue":"{{{input}}}"}},
                {"key":"gen_ai.usage.output_tokens","value":{"intValue":"{{{output}}}"}}{{{cacheAttributes}}}
              ]}
            ]}]}]}
            """;
    }

    /// <summary>A chat turn, a failed tool call, then a later successful retry → recovered.</summary>
    private static string RecoveredPayload(string traceId, string model, int input, int output) => $$$"""
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"{{{traceId}}}","spanId":"1111","name":"chat {{{model}}}",
           "startTimeUnixNano":"1000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"{{{model}}}"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"{{{input}}}"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"{{{output}}}"}}
           ]},
          {"traceId":"{{{traceId}}}","spanId":"2222","parentSpanId":"1111","name":"execute_tool str_replace",
           "startTimeUnixNano":"2000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]},
          {"traceId":"{{{traceId}}}","spanId":"3333","parentSpanId":"1111","name":"execute_tool str_replace",
           "startTimeUnixNano":"3000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]}
        ]}]}]}
        """;

    /// <summary>A chat turn then a terminal failed tool call → unrecovered.</summary>
    private static string UnrecoveredPayload(string traceId, string model, int input, int output) => $$$"""
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"{{{traceId}}}","spanId":"1111","name":"chat {{{model}}}",
           "startTimeUnixNano":"1000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"{{{model}}}"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"{{{input}}}"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"{{{output}}}"}}
           ]},
          {"traceId":"{{{traceId}}}","spanId":"2222","parentSpanId":"1111","name":"execute_tool run_tests",
           "startTimeUnixNano":"2000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]}
        ]}]}]}
        """;

    private const string SensitivePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.id","value":{"stringValue":"USER-ID-SECRET-MARKER"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private static async Task<JsonDocument> GetJsonAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

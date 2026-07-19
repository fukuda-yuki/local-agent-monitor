using System.Net;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 raw span-detail route (`GET /traces/{traceId}/spans/{spanId}/detail`,
/// D043): same-origin only, no-store, absent under --sanitized-only, 404 for
/// unknown ids, tool / llm formatted shapes, and the raw span JSON that always
/// works even when formatted extraction finds nothing.
/// </summary>
public class MonitorSpanDetailRouteTests
{
    private const string TraceId = "trace-span-detail";

    [Fact]
    public async Task SpanDetail_ToolSpan_ReturnsArgumentsResultTailAndEstimate()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{TraceId}/spans/tool1/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(TraceId, body.GetProperty("trace_id").GetString());
        Assert.Equal("tool1", body.GetProperty("span_id").GetString());
        Assert.Equal("str_replace", body.GetProperty("span").GetProperty("tool_name").GetString());

        var tool = body.GetProperty("tool");
        Assert.Contains("TOOL_ARGS_MARKER", tool.GetProperty("arguments").GetString());
        Assert.Contains("last line of output", tool.GetProperty("result_tail").GetString());
        Assert.True(tool.GetProperty("result_token_estimate").GetInt32() >= 1);
        Assert.Equal("1", tool.GetProperty("exit_code").GetString());

        Assert.Contains("TOOL_ARGS_MARKER", body.GetProperty("raw_span_json").GetString());
    }

    [Fact]
    public async Task SpanDetail_LlmSpan_ReturnsMessagePreviewsAndResponse()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        var body = JsonDocument.Parse(await host.Client.GetStringAsync($"/traces/{TraceId}/spans/llm1/detail")).RootElement;

        var llm = body.GetProperty("llm");
        var messages = llm.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("preview").GetString()!.Contains("PROMPT_MARKER"));
        Assert.Contains("COMPLETION_MARKER", llm.GetProperty("response_preview").GetString());
        Assert.True(llm.GetProperty("response_token_estimate").GetInt32() >= 1);
        Assert.Equal("chat", body.GetProperty("span").GetProperty("operation").GetString());
    }

    [Fact]
    public async Task SpanDetail_SpanWithoutRecognizedKeys_StillReturnsRawSpanJson()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        var body = JsonDocument.Parse(await host.Client.GetStringAsync($"/traces/{TraceId}/spans/bare1/detail")).RootElement;

        Assert.Equal(JsonValueKind.Null, body.GetProperty("tool").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("llm").ValueKind);
        Assert.Contains("\"bare1\"", body.GetProperty("raw_span_json").GetString());
    }

    [Theory]
    [InlineData("unknown-span")]
    [InlineData("0000000000000000")]
    public async Task SpanDetail_UnknownSpanReturns404(string spanId)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{TraceId}/spans/{Uri.EscapeDataString(spanId)}/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpanDetail_UnknownTraceReturns404()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/no-such-trace/spans/tool1/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpanDetail_CrossSiteAndForeignOriginAreForbidden()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}/spans/tool1/detail");
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        var crossSiteResponse = await host.Client.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);
        Assert.True(crossSiteResponse.Headers.CacheControl?.NoStore);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}/spans/tool1/detail");
        foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);
    }

    [Fact]
    public async Task SpanDetail_RouteIsAbsentUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{TraceId}/spans/tool1/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void Seed(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: TraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: Payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(3));
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false) =>
        MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });

    private const string Payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-span-detail","spanId":"llm1","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
            {"key":"gen_ai.prompt","value":{"stringValue":"PROMPT_MARKER fix the failing tests"}},
            {"key":"gen_ai.completion","value":{"stringValue":"COMPLETION_MARKER done"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
            {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"400"}}
          ]},
          {"traceId":"trace-span-detail","spanId":"tool1","parentSpanId":"llm1","name":"execute_tool str_replace",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
            {"key":"gen_ai.tool.name","value":{"stringValue":"str_replace"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"{\"path\":\"a.cs\",\"TOOL_ARGS_MARKER\":true}"}},
            {"key":"gen_ai.tool.result","value":{"stringValue":"first line\nsecond line\nlast line of output"}},
            {"key":"gen_ai.tool.exit_code","value":{"intValue":"1"}},
            {"key":"error.type","value":{"stringValue":"tool_failure"}}
          ]},
          {"traceId":"trace-span-detail","spanId":"bare1","parentSpanId":"llm1","name":"post_tool_hook","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"post_tool_hook"}}
          ]}
        ]}]}]}
        """;
}

using System.Net;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorProjectionApiTests
{
    [Fact]
    public async Task Ingestions_PaginateByCursorWithTerminalNull()
    {
        using var temp = new MonitorTempDirectory();
        var ids = SeedIngestions(temp, "trace-1", "trace-2", "trace-3");
        await using var host = await StartReadOnlyHostAsync(temp);

        var page1 = await GetJsonAsync(host, "/api/monitor/ingestions?limit=2");
        Assert.Equal(2, page1.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(ids[1], page1.RootElement.GetProperty("next_cursor").GetInt64());

        var page2 = await GetJsonAsync(host, $"/api/monitor/ingestions?after={ids[1]}&limit=2");
        Assert.Equal(1, page2.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page2.RootElement.GetProperty("next_cursor").ValueKind);

        // Exact-multiple terminal: exactly limit rows ⇒ next_cursor null.
        var exact = await GetJsonAsync(host, "/api/monitor/ingestions?limit=3");
        Assert.Equal(3, exact.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, exact.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task Traces_AggregateMultiTraceExportToOneRowPerTrace()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawAndProject(temp, "multi", MultiTraceJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var traces = await GetJsonAsync(host, "/api/monitor/traces");
        var traceIds = traces.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("trace_id").GetString())
            .ToHashSet();

        Assert.Equal(new HashSet<string?> { "trace-1", "trace-2" }, traceIds);
    }

    [Fact]
    public async Task Spans_PaginateByCursorWithTerminalNull()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawAndProjectSpans(temp, "trace-spans-page", ThreeSpanJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        // First page: 2 items, next_cursor non-null.
        var page1 = await GetJsonAsync(host, "/api/monitor/traces/trace-spans-page/spans?limit=2");
        var items1 = page1.RootElement.GetProperty("items");
        Assert.Equal(2, items1.GetArrayLength());
        var cursor1 = page1.RootElement.GetProperty("next_cursor");
        Assert.NotEqual(JsonValueKind.Null, cursor1.ValueKind);
        var afterCursor = cursor1.GetInt64();
        // next_cursor must match the id of the last item on page1.
        Assert.Equal(items1[1].GetProperty("id").GetInt64(), afterCursor);

        // Second page: remaining item(s), next_cursor null.
        var page2 = await GetJsonAsync(host, $"/api/monitor/traces/trace-spans-page/spans?after={afterCursor}&limit=2");
        var items2 = page2.RootElement.GetProperty("items");
        Assert.Equal(1, items2.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page2.RootElement.GetProperty("next_cursor").ValueKind);

        // Exact-multiple terminal: exactly limit rows => next_cursor null.
        var exact = await GetJsonAsync(host, "/api/monitor/traces/trace-spans-page/spans?limit=3");
        Assert.Equal(3, exact.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, exact.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task Traces_ExposeRollupColumns()
    {
        using var temp = new MonitorTempDirectory();
        // Two chat spans each with tokens and same model; no invoke_agent so rollup = chat sum.
        // Span 1: input=300, output=100, total=400, model=gpt-4o
        // Span 2: input=200, output=50,  total=250, model=gpt-4o
        // Expected rollup: input=500, output=150, total=650, turn_count=2, primary_model=gpt-4o
        SeedRawAndProjectSpans(temp, "trace-rollup", TwoTokenChatJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var traces = await GetJsonAsync(host, "/api/monitor/traces");
        var item = traces.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("trace_id").GetString() == "trace-rollup");

        Assert.Equal(650, item.GetProperty("total_tokens").GetInt32());
        Assert.Equal(2, item.GetProperty("turn_count").GetInt32());
        Assert.Equal("gpt-4o", item.GetProperty("primary_model").GetString());
    }

    [Fact]
    public async Task Traces_ExposeSanitizedRepositoryMetadata()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawAndProject(temp, "trace-repo", RepositoryMetadataJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var traces = await GetJsonAsync(host, "/api/monitor/traces");
        var item = Assert.Single(traces.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal("repo-api", item.GetProperty("repository_name").GetString());
        Assert.Equal("workspace-api", item.GetProperty("workspace_label").GetString());
        Assert.Equal("snapshot-api", item.GetProperty("repo_snapshot").GetString());
    }

    [Fact]
    public async Task Apis_NeverReturnRawContentOrPii()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawAndProjectSpans(temp, "trace-pii", RawAndPiiJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var ingestions = await GetStringAsync(host, "/api/monitor/ingestions");
        var traces = await GetStringAsync(host, "/api/monitor/traces");
        var spans = await GetStringAsync(host, "/api/monitor/traces/trace-pii/spans");

        foreach (var marker in new[] { "SECRET_PROMPT_TEXT_MARKER", "SECRET_TOOL_ARGS_MARKER", "USER-ID-SECRET-MARKER", "leak-marker@example.com", @"C:\Users\person\secret", "Bearer token-marker" })
        {
            Assert.DoesNotContain(marker, ingestions);
            Assert.DoesNotContain(marker, traces);
            Assert.DoesNotContain(marker, spans);
        }
    }

    [Fact]
    public async Task Spans_SanitizeFreeFormNameAttributes()
    {
        using var temp = new MonitorTempDirectory();
        // Inject email, Windows path, and secret-like marker into sanitized free-form fields:
        // tool_name (gen_ai.tool.name), mcp_tool_name (github.copilot.tool.parameters.mcp_tool_name),
        // agent_name (gen_ai.agent.name), error.type (error.type).
        SeedRawAndProjectSpans(temp, "trace-sanit", SanitizationJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var json = await GetStringAsync(host, "/api/monitor/traces/trace-sanit/spans");

        Assert.DoesNotContain("leak-tool@example.com", json);
        Assert.DoesNotContain(@"C:\secret\path", json);
        Assert.DoesNotContain("SECRET-AGENT-MARKER", json);
        // error.type containing "secret" is also dropped by SanitizeFreeFormName.
        Assert.DoesNotContain("secret-error-type", json);
    }

    [Theory]
    [InlineData("/api/monitor/ingestions?limit=0")]
    [InlineData("/api/monitor/ingestions?limit=999")]
    [InlineData("/api/monitor/ingestions?limit=abc")]
    [InlineData("/api/monitor/ingestions?after=-1")]
    [InlineData("/api/monitor/traces?limit=0")]
    [InlineData("/api/monitor/traces/trace-1/spans?limit=0")]
    [InlineData("/api/monitor/traces/trace-1/spans?limit=999")]
    [InlineData("/api/monitor/traces/trace-1/spans?after=-1")]
    public async Task CursorApis_RejectInvalidQueryWith400(string path)
    {
        using var temp = new MonitorTempDirectory();
        SeedIngestions(temp, "trace-1");
        await using var host = await StartReadOnlyHostAsync(temp);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_query", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProjectionWorker_PopulatesApisAndDrivesReadyState()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartLiveHostAsync(temp);

        for (var i = 0; i < 2; i++)
        {
            var post = await host.Client.PostAsync("/v1/traces", JsonContent(TraceJson($"live-trace-{i}")));
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }

        var ingestions = await WaitForIngestionCountAsync(host, expected: 2);
        Assert.Equal(2, ingestions);

        var ready = await host.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("\"status\":\"ready\"", await ready.Content.ReadAsStringAsync());
    }

    private static async Task<int> WaitForIngestionCountAsync(RunningMonitorHost host, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var document = await GetJsonAsync(host, "/api/monitor/ingestions?limit=200");
            var count = document.RootElement.GetProperty("items").GetArrayLength();
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(25);
        }

        return -1;
    }

    private static IReadOnlyList<long> SeedIngestions(MonitorTempDirectory temp, params string[] traceIds)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var ids = new List<long>();
        var minute = 1;
        foreach (var traceId in traceIds)
        {
            ids.Add(InsertAndProject(store, traceId, TraceJson(traceId), minute++));
        }

        return ids;
    }

    private static void SeedRawAndProject(MonitorTempDirectory temp, string traceId, string payloadJson)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProject(store, traceId, payloadJson, minute: 1);
    }

    private static void SeedRawAndProjectSpans(MonitorTempDirectory temp, string traceId, string payloadJson)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(100));
        var spans = MonitorSpanProjectionBuilder.Build(record);
        store.ApplySpanProjection(id, spans, DateTimeOffset.UnixEpoch.AddMinutes(101));
    }

    private static long InsertAndProject(RawTelemetryStore store, string traceId, string payloadJson, int minute)
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(minute),
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(100));
        return id;
    }

    private static Task<RunningMonitorHost> StartReadOnlyHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private static Task<RunningMonitorHost> StartLiveHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });

    private static async Task<JsonDocument> GetJsonAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetStringAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string TraceJson(string traceId) => TraceTemplate.Replace("__TRACE__", traceId);

    private const string TraceTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"__TRACE__","spanId":"2222222222222222","name":"chat gpt-4o"}]}]}]}
        """;

    private const string MultiTraceJson = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[
          {"traceId":"trace-1","spanId":"1111111111111111","name":"chat gpt-4o"},
          {"traceId":"trace-2","spanId":"2222222222222222","name":"chat gpt-4o"}
        ]}]}]}
        """;

    private const string RawAndPiiJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.id","value":{"stringValue":"USER-ID-SECRET-MARKER"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}},
          {"key":"repo.name","value":{"stringValue":"leak-marker@example.com"}},
          {"key":"workspace.name","value":{"stringValue":"C:\\Users\\person\\secret"}},
          {"key":"repo.snapshot","value":{"stringValue":"Bearer token-marker"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private const string RepositoryMetadataJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"repo.name","value":{"stringValue":"repo-api"}},
          {"key":"workspace.name","value":{"stringValue":"workspace-api"}},
          {"key":"repo.snapshot","value":{"stringValue":"snapshot-api"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-repo","spanId":"1111111111111111","name":"chat gpt-4o"}
        ]}]}]}
        """;

    // Three distinct spans for pagination test.
    private const string ThreeSpanJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-spans-page","spanId":"1001","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"trace-spans-page","spanId":"1002","parentSpanId":"1001","name":"execute_tool",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]},
          {"traceId":"trace-spans-page","spanId":"1003","parentSpanId":"1001","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]}
        ]}]}]}
        """;

    // Two chat spans with token usage for rollup test.
    // Expected rollup: input=500, output=150, total=650, turn_count=2, primary_model=gpt-4o
    private const string TwoTokenChatJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-rollup","spanId":"2001","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
           ]},
          {"traceId":"trace-rollup","spanId":"2002","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;

    // Spans with unsafe values in free-form name fields: tool_name (email), mcp_tool_name (Windows path),
    // agent_name (SECRET-AGENT-MARKER contains "secret"), error.type (secret-error-type contains "secret").
    private const string SanitizationJson = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"trace-sanit","spanId":"3001","name":"execute_tool",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"leak-tool@example.com"}},
             {"key":"github.copilot.tool.parameters.mcp_tool_name","value":{"stringValue":"C:\\secret\\path"}}
           ]},
          {"traceId":"trace-sanit","spanId":"3002","name":"invoke_agent",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"SECRET-AGENT-MARKER"}},
             {"key":"error.type","value":{"stringValue":"secret-error-type"}}
           ]}
        ]}]}]}
        """;

}

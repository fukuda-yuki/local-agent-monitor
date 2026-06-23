using System.Net;
using System.Net.Sockets;
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
    public async Task Apis_NeverReturnRawContentOrPii()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawAndProject(temp, "trace-pii", RawAndPiiJson);
        await using var host = await StartReadOnlyHostAsync(temp);

        var ingestions = await GetStringAsync(host, "/api/monitor/ingestions");
        var traces = await GetStringAsync(host, "/api/monitor/traces");

        foreach (var marker in new[] { "SECRET_PROMPT_TEXT_MARKER", "SECRET_TOOL_ARGS_MARKER", "USER-ID-SECRET-MARKER", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, ingestions);
            Assert.DoesNotContain(marker, traces);
        }
    }

    [Theory]
    [InlineData("/api/monitor/ingestions?limit=0")]
    [InlineData("/api/monitor/ingestions?limit=999")]
    [InlineData("/api/monitor/ingestions?limit=abc")]
    [InlineData("/api/monitor/ingestions?after=-1")]
    [InlineData("/api/monitor/traces?limit=0")]
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

    private static async Task<int> WaitForIngestionCountAsync(RunningHost host, int expected)
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

    private static async Task<RunningHost> StartReadOnlyHostAsync(MonitorTempDirectory temp)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, EnableRawView: false, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static async Task<RunningHost> StartLiveHostAsync(MonitorTempDirectory temp)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, EnableRawView: false, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static async Task<JsonDocument> GetJsonAsync(RunningHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetStringAsync(RunningHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string TraceJson(string traceId) => TraceTemplate.Replace("__TRACE__", traceId);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private sealed class RunningHost(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await app.StopAsync();
            }
            catch
            {
                // Ignore stop faults during teardown.
            }

            await app.DisposeAsync();
        }
    }
}

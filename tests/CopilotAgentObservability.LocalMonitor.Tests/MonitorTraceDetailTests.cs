using System.Net;
using System.Net.Sockets;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Trace-detail page (/traces/{traceId}) — the agent-execution view. It renders the
/// sanitized Summary + sub-agent tree + per-turn tokens and inlines the raw OTLP
/// payload. As a raw-bearing route it enforces same-origin + no-store, and under
/// --sanitized-only the whole page is absent (404). The full negative matrix is M6.
/// </summary>
public class MonitorTraceDetailTests
{
    private const string TraceId = "trace-detail";

    [Fact]
    public async Task TraceDetail_ByDefault_RendersSanitizedViewAndRawInlineWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{TraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        // Sanitized sections present.
        Assert.Contains("Summary", body);
        Assert.Contains("Sub-agent span tree", body);
        Assert.Contains("Per-turn token rollup", body);
        Assert.Contains("read_file", body);

        // Raw body shown inline by default.
        Assert.Contains("Raw OTLP payload", body);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", body);
    }

    [Fact]
    public async Task TraceDetail_UnderSanitizedOnly_Returns404AndNoRaw()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{TraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("leak-marker@example.com", body);
    }

    [Fact]
    public async Task TraceDetail_CrossSiteFetchIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cross_origin_forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TraceDetail_ForeignOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}");
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TraceDetail_UnknownTraceReturns404()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void SeedProjectedTrace(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: TraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: AgentTracePayload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
    }

    private static async Task<RunningHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, SanitizedOnly: sanitizedOnly, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private const string AgentTracePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-detail","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"trace-detail","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"trace-detail","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
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

using System.Net;
using System.Net.Sockets;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorUiTests
{
    [Fact]
    public async Task UiRoutes_ReturnSuccessfulHtmlPages()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        foreach (var path in new[] { "/", "/ingestions", "/traces", "/diagnostics" })
        {
            var response = await host.Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Local Ingestion Monitor", body);
        }
    }

    [Fact]
    public async Task OverviewAndDiagnostics_RenderReadinessWithoutRawContent()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var overview = await host.Client.GetStringAsync("/");
        var diagnostics = await host.Client.GetStringAsync("/diagnostics");

        Assert.Contains("status", overview);
        Assert.Contains("health/ready", diagnostics);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", overview);
        Assert.DoesNotContain("leak-marker@example.com", diagnostics);
    }

    [Fact]
    public async Task ListPages_LinkRawByDefaultAndHideUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        var rawRecordId = SeedRawWithSensitiveMarkers(temp);

        await using var defaultHost = await StartHostAsync(temp);
        var defaultIngestions = await defaultHost.Client.GetStringAsync("/ingestions");
        Assert.Contains($"/traces/{rawRecordId}/raw", defaultIngestions);
        // Raw is reachable by default, but the list itself stays sanitized; raw is a link only.
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", defaultIngestions);
        Assert.DoesNotContain("leak-marker@example.com", defaultIngestions);

        await using var sanitizedHost = await StartHostAsync(temp, sanitizedOnly: true);
        var sanitizedIngestions = await sanitizedHost.Client.GetStringAsync("/ingestions");
        Assert.DoesNotContain($"/traces/{rawRecordId}/raw", sanitizedIngestions);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", sanitizedIngestions);
    }

    [Fact]
    public async Task TracesPage_RendersSanitizedRowsAndNoRawContent()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetStringAsync("/traces");

        Assert.Contains("trace-ui", traces);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", traces);
        Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", traces);
        Assert.DoesNotContain("leak-marker@example.com", traces);
    }

    [Fact]
    public async Task ListPages_RejectNegativeAfterCursorWith400()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var ingestions = await host.Client.GetAsync("/ingestions?after=-1");
        var traces = await host.Client.GetAsync("/traces?after=-1");

        Assert.Equal(HttpStatusCode.BadRequest, ingestions.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, traces.StatusCode);
    }

    [Fact]
    public async Task Pages_ReferenceMonitorScriptAndScriptUsesCursorApis()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var index = await host.Client.GetStringAsync("/");
        var script = await host.Client.GetStringAsync("/monitor.js");

        Assert.Contains("/monitor.js", index);
        Assert.Contains("/api/monitor/ingestions", script);
        Assert.Contains("/api/monitor/traces", script);
        Assert.Contains("new EventSource('/events')", script);
    }

    [Fact]
    public async Task MonitorCss_IsServed()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/monitor.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void EnsureSchema(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
    }

    private static long SeedRawWithSensitiveMarkers(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-ui",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: SensitivePayload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        return id;
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

    private const string SensitivePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-ui","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
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

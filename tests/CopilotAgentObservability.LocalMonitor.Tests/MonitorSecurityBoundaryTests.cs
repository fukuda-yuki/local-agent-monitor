using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// DR6 negative security matrix for the Local Ingestion Monitor: default UI / API /
/// SSE never return raw or PII, the raw route is absent without the opt-in flag and
/// same-origin only with it, non-GET / cross-origin requests to the SSE stream are
/// refused, non-loopback Host headers are rejected, restart on the same DB recovers
/// without loss, and raw markers never leak into error responses.
/// </summary>
public class MonitorSecurityBoundaryTests
{
    private static readonly string[] Markers =
    {
        "SECRET_PROMPT_TEXT_MARKER",
        "SECRET_TOOL_ARGS_MARKER",
        "leak-marker@example.com",
    };

    [Fact]
    public async Task DefaultSurfaces_NeverReturnRawOrPii_EvenWithRawViewEnabled()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, enableRawView: true);

        foreach (var path in new[]
                 {
                     "/", "/ingestions", "/traces", "/diagnostics",
                     "/api/monitor/ingestions", "/api/monitor/traces",
                 })
        {
            var body = await host.Client.GetStringAsync(path);
            foreach (var marker in Markers)
            {
                Assert.DoesNotContain(marker, body);
            }
        }
    }

    [Fact]
    public async Task RawRoute_IsAbsentWithoutFlag()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, enableRawView: false);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RawRoute_WithFlag_CrossOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, enableRawView: true);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);
    }

    [Fact]
    public async Task Events_DoesNotAcceptCrossOriginPost()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadOnlyHostAsync(temp, enableRawView: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent("{}"),
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        var response = await host.Client.SendAsync(request);

        // SSE is a GET-only notification stream; there is no CSRF-exposed mutation.
        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 405 or 404 for cross-origin POST /events but got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task NonLoopbackHostHeader_IsRejectedOnPageRoutes()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, enableRawView: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/")
        {
            Headers = { Host = "example.com" },
        };
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_host", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RestartRecovery_ReusesDatabaseAndCatchesUpProjection()
    {
        using var temp = new MonitorTempDirectory();

        await using (var first = await StartLiveHostAsync(temp))
        {
            var response = await first.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await WaitForIngestionProjectionCountAsync(first, expected: 1));
        }

        await using var second = await StartLiveHostAsync(temp);
        Assert.Equal(1, await WaitForIngestionProjectionCountAsync(second, expected: 1));

        var ready = await second.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task RawPayloadMarkers_DoNotAppearInErrorResponses()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartLiveHostAsync(temp, enableRawView: true);

        var accepted = await host.Client.PostAsync("/v1/traces", JsonContent(SensitiveTraceJson));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var bad = await host.Client.PostAsync("/v1/traces", JsonContent("{"));
        var body = await bad.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, body);
        }

        Assert.DoesNotContain(temp.DatabasePath, body);
        Assert.DoesNotContain(Environment.UserName, body);
        Assert.DoesNotContain("Exception", body);
    }

    private static long SeedSensitiveProjectedRecord(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-sec",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: SensitiveTraceJson);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        return id;
    }

    private static async Task<int> WaitForIngestionProjectionCountAsync(RunningHost host, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var body = await host.Client.GetStringAsync("/api/monitor/ingestions?limit=200");
            using var document = JsonDocument.Parse(body);
            var count = document.RootElement.GetProperty("items").GetArrayLength();
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(25);
        }

        return -1;
    }

    private static async Task<RunningHost> StartReadOnlyHostAsync(MonitorTempDirectory temp, bool enableRawView)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, EnableRawView: enableRawView, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static async Task<RunningHost> StartLiveHostAsync(MonitorTempDirectory temp, bool enableRawView = false)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, EnableRawView: enableRawView, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string ValidTraceJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"55555555555555555555555555555555","spanId":"6666666666666666","name":"chat gpt-4o"}]}]}]}
        """;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private const string SensitiveTraceJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"55555555555555555555555555555555","spanId":"6666666666666666","name":"chat gpt-4o","attributes":[
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

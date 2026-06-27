using System.Net;
using System.Net.Sockets;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorRawViewTests
{
    [Fact]
    public async Task RawDetail_AbsentUnderSanitizedOnly_Returns404()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_ByDefault_RendersInertEscapedHtmlWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        // Stored markup is shown as escaped, inert text — never as live markup.
        Assert.Contains("&lt;script&gt;", body);
        Assert.DoesNotContain("<script", body);
    }

    [Fact]
    public async Task RawDetail_CrossSiteFetchIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cross_origin_forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDetail_ForeignOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_SameOriginTopLevelNavigationIsAllowed()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_UnknownIdReturns404()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/999999/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("raw_record_not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDetail_CursorApisStaySanitized()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var ingestions = await host.Client.GetStringAsync("/api/monitor/ingestions");
        var traces = await host.Client.GetStringAsync("/api/monitor/traces");

        foreach (var marker in new[] { "<script>", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, ingestions);
            Assert.DoesNotContain(marker, traces);
        }
    }

    private static long SeedRawRecord(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-raw",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: ScriptAndPiiPayload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
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

    private const string ScriptAndPiiPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-raw","spanId":"1111111111111111","name":"<script>alert(1)</script>"}
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

using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionProposalApplyRouteTests
{
    [Fact]
    public async Task Apply_rejects_a_nonempty_body_before_mutation()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{Guid.CreateVersion7()}/apply")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_apply_request", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Apply_rejects_chunked_body_over_one_mebibyte_before_mutation()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{Guid.CreateVersion7()}/apply")
        {
            Content = new StreamContent(new MemoryStream(new byte[1_048_577])),
        };
        request.Headers.TransferEncodingChunked = true;
        request.Content.Headers.ContentLength = null;
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request_too_large", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Roots_requires_same_origin_and_returns_only_opaque_root_metadata()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        await using var host = await StartAsync(temp, root);

        using var forbidden = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        forbidden.Headers.TryAddWithoutValidation("Origin", "https://example.test");
        var denied = await host.Client.SendAsync(forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"repository\"", body);
        Assert.DoesNotContain(rootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp, ConfiguredApplyRoot root)
    {
        var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes, ApplyRoots: [root]),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        await app.StartAsync();
        var address = app.Urls.Single();
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }
}

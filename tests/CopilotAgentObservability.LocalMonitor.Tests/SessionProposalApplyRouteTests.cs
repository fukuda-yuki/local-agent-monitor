using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionProposalApplyRouteTests
{
    [Theory]
    [InlineData("GET", "/api/session-workspace/proposal-applies/roots", false)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts", true)]
    [InlineData("GET", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001", false)]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection", true)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve", true)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/apply", false)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/0197d7c0-0000-7000-8000-000000000001/rollback", false)]
    public async Task Every_proposal_apply_route_applies_policy_before_configuration(string method, string path, bool body)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var crossSite = CreateRequest(method, path, body);
        crossSite.Headers.TryAddWithoutValidation("Origin", "https://example.test");

        var denied = await host.Client.SendAsync(crossSite);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        using var noRoots = CreateRequest(method, path, body);
        noRoots.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        var unavailable = await host.Client.SendAsync(noRoots);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("apply_not_configured", await unavailable.Content.ReadAsStringAsync());
        Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
    }

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

    [Theory]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts")]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection")]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve")]
    public async Task Body_routes_require_json_before_ids_or_mutation(string method, string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("unsupported_media_type", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts")]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection")]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve")]
    public async Task Body_routes_reject_declared_body_over_one_mebibyte(string method, string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request_too_large", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/session-workspace/proposal-applies/drafts/invalid/apply")]
    [InlineData("/api/session-workspace/proposal-applies/invalid/rollback")]
    public async Task Mutation_routes_reject_invalid_id_once_after_empty_body_validation(string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_apply_request", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Empty_mutation_body_accepts_arbitrary_content_type_then_validates_id()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts/invalid/apply") { Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_apply_request", await response.Content.ReadAsStringAsync());
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

    private static HttpRequestMessage CreateRequest(string method, string path, bool body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp, params ConfiguredApplyRoot[] roots)
    {
        var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes, ApplyRoots: roots),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        await app.StartAsync();
        var address = app.Urls.Single();
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }
}

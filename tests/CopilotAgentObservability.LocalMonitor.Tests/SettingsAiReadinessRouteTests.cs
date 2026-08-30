using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Settings;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsAiReadinessRouteTests
{
    private const string Path = "/api/local-monitor/v1/settings/ai-readiness";

    [Fact]
    public async Task GetAndPostExposeOnlyClosedNoStoreFacts()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp);
        using var get = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"provider\":\"github_copilot\",\"selected_model\":\"gpt-5\",\"selected_configuration\":\"standard\",\"readiness_state\":\"configured_not_checked\",\"last_check_result\":\"not_checked\",\"provider_egress_notice\":\"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action\"}", await get.Content.ReadAsStringAsync());
        using var post = Request(HttpMethod.Post, csrf: "local-monitor");
        using var checkedResponse = await host.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, checkedResponse.StatusCode);
        Assert.True(checkedResponse.Headers.CacheControl?.NoStore);
        Assert.Contains("\"readiness_state\":\"ready\"", await checkedResponse.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "cross-site", null, 403, "cross_origin_forbidden")]
    [InlineData("POST", "cross-site", "local-monitor", 403, "cross_origin_forbidden")]
    [InlineData("POST", null, null, 403, "csrf_required")]
    [InlineData("POST", null, "wrong", 403, "csrf_required")]
    public async Task OriginAndCsrfFailuresAreFixedNoStore(string method, string? fetchSite, string? csrf, int status, string error)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp);
        using var request = Request(new HttpMethod(method), csrf, fetchSite: fetchSite);
        await AssertError(await host.Client.SendAsync(request), status, error);
    }

    [Fact]
    public async Task MethodQueryBodyAndHostAreRejectedByExactOwner()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp);
        await AssertError(await host.Client.SendAsync(Request(HttpMethod.Put)), 405, "method_not_allowed");
        await AssertError(await host.Client.SendAsync(Request(HttpMethod.Get, uri: Path + "?extra=1")), 400, "invalid_request");
        await AssertError(await host.Client.SendAsync(Request(HttpMethod.Post, "local-monitor", new StringContent("{}", Encoding.UTF8, "application/json"))), 400, "invalid_request");
        using var invalidHost = Request(HttpMethod.Get);
        invalidHost.Headers.Host = "remote.example";
        await AssertError(await host.Client.SendAsync(invalidHost), 400, "invalid_host");
    }

    [Fact]
    public async Task SanitizedOnlyDoesNotRegisterReadinessResource()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: new() { StartWriter = false, StartProjectionWorker = false });
        using var response = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string error)
    {
        using (response)
        {
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal($"{{\"error\":\"{error}\"}}", await response.Content.ReadAsStringAsync());
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string? csrf = null, HttpContent? content = null, string? fetchSite = null, string? uri = null)
    {
        var request = new HttpRequestMessage(method, uri ?? Path) { Content = content };
        if (csrf is not null) request.Headers.Add("x-monitor-csrf", csrf);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return request;
    }

    private static Task<RunningMonitorHost> Host(MonitorTempDirectory temp)
    {
        var service = new SettingsAiReadinessService("github_copilot", "gpt-5", "standard", true,
            () => new Client(), TimeSpan.FromSeconds(1));
        return MonitorTestHost.StartAsync(temp, testOptions: new() { SettingsAiReadiness = service });
    }

    private sealed class Client : IOwnedCopilotClientV1
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<CopilotRuntimeStatusObservationV1?>(new("1.0.75", 3, null, true));
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

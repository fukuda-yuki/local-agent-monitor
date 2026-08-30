using System.Net;
using CopilotAgentObservability.LocalMonitor.Settings;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsAiReadinessRouteTests
{
    [Fact]
    public async Task GetAndPostShareOneClosedNoStoreResourceAndPostRequiresOriginAndCsrf()
    {
        using var temp = new MonitorTempDirectory();
        var service = new SettingsAiReadinessService("github_copilot", "gpt-5", "standard",
            () => new Client(), new(), TimeSpan.FromSeconds(1));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new() { SettingsAiReadiness = service });

        using var get = await host.Client.GetAsync("/api/local-monitor/v1/settings/ai-readiness");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"provider\":\"github_copilot\",\"selected_model\":\"gpt-5\",\"selected_configuration\":\"standard\",\"readiness_state\":\"configured_not_checked\",\"last_check_result\":\"not_checked\",\"provider_egress_notice\":\"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action\"}", await get.Content.ReadAsStringAsync());

        using var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/settings/ai-readiness");
        using var denied = await host.Client.SendAsync(missingCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("{\"error\":\"csrf_required\"}", await denied.Content.ReadAsStringAsync());

        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/settings/ai-readiness");
        post.Headers.Add("x-monitor-csrf", "local-monitor");
        using var checkedResponse = await host.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, checkedResponse.StatusCode);
        Assert.True(checkedResponse.Headers.CacheControl?.NoStore);
        Assert.Contains("\"readiness_state\":\"ready\"", await checkedResponse.Content.ReadAsStringAsync());
    }

    private sealed class Client : IOwnedCopilotClientV1
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(new("1.0.75", 3, null));
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

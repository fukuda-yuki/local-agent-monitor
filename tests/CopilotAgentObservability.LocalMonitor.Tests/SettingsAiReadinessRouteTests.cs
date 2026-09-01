using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.Settings;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using GitHub.Copilot;
using Microsoft.Extensions.DependencyInjection;

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
        await AssertError(await host.Client.SendAsync(Request(new HttpMethod("PROPFIND"))), 405, "method_not_allowed", "GET", "POST");
        await AssertError(await host.Client.SendAsync(Request(HttpMethod.Get, uri: Path + "?extra=1")), 400, "invalid_request");
        await AssertError(await host.Client.SendAsync(Request(HttpMethod.Post, "local-monitor", new StringContent("{}", Encoding.UTF8, "application/json"))), 400, "invalid_request");
        using var invalidHost = Request(HttpMethod.Get);
        invalidHost.Headers.Host = "remote.example";
        await AssertError(await host.Client.SendAsync(invalidHost), 400, "invalid_host");
    }

    [Theory]
    [InlineData("/API/local-monitor/v1/settings/ai-readiness")]
    [InlineData(Path + "/")]
    public async Task NearPathsAreEmptyNoStoreNotFound(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp);
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PartialInvalidAnalysisProviderDoesNotPreventHostStartup()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            UseUserSecrets = false,
            ConfigurationValues = new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Provider:Type"] = "openai",
            },
        });

        using var response = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"readiness_state\":\"configured_not_checked\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProductionCompositionUsesHostTimeProviderForReadinessExpiry()
    {
        using var temp = new MonitorTempDirectory();
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<CopilotRuntimeStatusObservationV1?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(_ =>
        {
            providerEntered.TrySetResult();
            return never.Task;
        });
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            UseUserSecrets = false,
            TimeProvider = timeProvider,
            SettingsAiReadinessClientFactory = () => client,
        });
        using var request = Request(HttpMethod.Post, csrf: "local-monitor");

        var responseTask = host.Client.SendAsync(request);
        await providerEntered.Task;
        Assert.False(responseTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"readiness_state\":\"check_failed\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task ProductionCompositionSharesInjectedClientFactoryWithReadinessAndLocalAi()
    {
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        const string runA = "018f0000-0000-7000-8000-000000000010";
        const string runB = "018f0000-0000-7000-8000-000000000020";
        using var temp = new MonitorTempDirectory();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var scope = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: temp.TimeProvider),
            skillRegistryAuthority: authority,
            timeProvider: temp.TimeProvider);
        var session = new LocalAiSession();
        var factoryCalls = 0;
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            UseUserSecrets = false,
            LocalRepositoryScopeSnapshotService = scope,
            SettingsAiReadinessClientFactory = () =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new Client(
                    _ => Task.FromResult<CopilotRuntimeStatusObservationV1?>(new("1.0.75", 3, null, true)),
                    session);
            },
        });
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(
            temp.DatabasePath, sessionId, runA, runB);
        using var readinessRequest = Request(HttpMethod.Post, csrf: "local-monitor");
        using var readinessResponse = await host.Client.SendAsync(readinessRequest);
        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);

        var application = host.Services.GetRequiredService<ILocalAiAnalysisApplicationV1>();
        var started = await application.StartSessionAsync(new(sessionId), CancellationToken.None);
        Assert.NotNull(started.RunId);
        LocalAiRunStatusV1? run = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            run = await application.ReadRunAsync(started.RunId!, CancellationToken.None);
            if (run?.State != "running") break;
            await Task.Delay(10);
        }

        Assert.Equal("zero_findings", run?.State);
        Assert.True(Volatile.Read(ref factoryCalls) >= 2);
        Assert.Equal(1, session.SendCalls);
    }

    [Fact]
    public async Task SanitizedOnlyDoesNotRegisterReadinessResource()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: new() { StartWriter = false, StartProjectionWorker = false });
        using var response = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string error, params string[] allow)
    {
        using (response)
        {
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (allow.Length > 0) Assert.Equal(allow, response.Content.Headers.Allow);
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
            () => new Client(), TimeSpan.FromSeconds(1), TimeProvider.System);
        return MonitorTestHost.StartAsync(temp, testOptions: new() { SettingsAiReadiness = service });
    }

    private sealed class Client : IOwnedCopilotClientV1
    {
        private readonly Func<CancellationToken, Task<CopilotRuntimeStatusObservationV1?>> status;

        internal Client() : this(_ => Task.FromResult<CopilotRuntimeStatusObservationV1?>(
            new("1.0.75", 3, null, true)))
        {
        }

        internal Client(
            Func<CancellationToken, Task<CopilotRuntimeStatusObservationV1?>> status,
            IOwnedCopilotSessionV1? session = null)
        {
            this.status = status;
            this.session = session;
        }

        private readonly IOwnedCopilotSessionV1? session;

        internal int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => status(cancellationToken);
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            Task.FromResult(session ?? throw new NotSupportedException());
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LocalAiSession : IOwnedCopilotSessionV1
    {
        internal int SendCalls { get; private set; }
        public string SessionId => "local-ai-session";
        public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.FromResult<string?>(
                "{\"summary\":\"shared-factory\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

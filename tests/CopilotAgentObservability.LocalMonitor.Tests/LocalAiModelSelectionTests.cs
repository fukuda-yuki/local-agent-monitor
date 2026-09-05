using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using GitHub.Copilot;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Affected")]
public sealed class LocalAiModelSelectionTests
{
    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string RunA = "018f0000-0000-7000-8000-000000000010";
    private const string RunB = "018f0000-0000-7000-8000-000000000020";

    [Fact]
    public async Task DiscoveryGetIsNotCheckedUntilExplicitRefresh()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp, new CatalogClient("model-a"));
        using var get = await host.Client.GetAsync("/api/local-monitor/v1/ai/models");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("not_checked", json.RootElement.GetProperty("discovery_state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("models").GetArrayLength());
        Assert.Equal(0, CatalogClient.ListCalls);
    }

    [Fact]
    public async Task DiscoveryRefreshDoesNotUseConfigurationSynthesizedModels()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp, new CatalogClient(), configuration: new Dictionary<string, string?>
        {
            ["CopilotAnalysis:Model"] = "glm-5.2",
            ["CopilotAnalysis:Models:glm-5.2:DisplayName"] = "GLM leftover",
        });
        using var response = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/models", "{}"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("empty", json.RootElement.GetProperty("discovery_state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("models").GetArrayLength());
        Assert.Equal("glm-5.2", json.RootElement.GetProperty("legacy_configured_model").GetString());
        Assert.False(json.RootElement.GetProperty("legacy_eligible").GetBoolean());
    }

    [Theory]
    [InlineData(CatalogBehavior.Unauthenticated, "unauthenticated")]
    [InlineData(CatalogBehavior.Unavailable, "unavailable")]
    [InlineData(CatalogBehavior.Failed, "failed")]
    [InlineData(CatalogBehavior.Empty, "empty")]
    public async Task DiscoveryRefreshReportsHonestStates(CatalogBehavior behavior, string state)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp, new CatalogClient(behavior: behavior));
        using var response = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/models", "{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(state, json.RootElement.GetProperty("discovery_state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task DiscoveryRefreshReturnsAccountIdsAndLegacyEligibility()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await Host(temp, new CatalogClient("model-a", "acme/byok-model"),
            configuration: new Dictionary<string, string?> { ["CopilotAnalysis:Model"] = "model-a" });
        using var response = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/models", "{}"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("ready", json.RootElement.GetProperty("discovery_state").GetString());
        Assert.Equal(new[] { "model-a", "acme/byok-model" }, json.RootElement.GetProperty("models").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToArray());
        Assert.True(json.RootElement.GetProperty("legacy_eligible").GetBoolean());
        Assert.Equal(1, CatalogClient.ListCalls);
    }

    [Fact]
    public async Task SessionAndNodeRunsCaptureSelectedModelWithoutCrossRunLeakage()
    {
        using var temp = new MonitorTempDirectory();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var scope = new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: temp.TimeProvider),
            skillRegistryAuthority: authority, timeProvider: temp.TimeProvider);
        var retention = new RetentionCatalogStore(RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, temp.TimeProvider), temp.TimeProvider);
        var repository = SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath, "legacy-model", "standard", temp.TimeProvider, retention);
        var provider = new RecordingProvider();
        var discovery = new FixedLocalAiModelDiscoveryV1("model-a", "model-b");
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), scope, repository, provider,
            timeProvider: temp.TimeProvider, repositoryAiEnabled: false, compareAiEnabled: false, models: discovery);
        await using var host = await MonitorTestHost.StartAsync(temp, repositoryAiEnabled: false, compareAiEnabled: false,
            testOptions: new()
            {
                LocalRepositoryScopeSnapshotService = scope,
                LocalAiAnalysisApplication = application,
                LocalAiModelDiscovery = discovery,
                TimeProvider = temp.TimeProvider,
            });
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(temp.DatabasePath, SessionId, RunA, RunB);
        using (var connection = Open(temp.DatabasePath)) LocalAiAnalysisSchemaV1.Ensure(connection);

        var first = await Start(host.Client, "/api/local-monitor/v1/ai/session-runs",
            $$"""{"session_id":"{{SessionId}}","model":"model-a"}""");
        Assert.Equal("zero_findings", (await Poll(host.Client, $"/api/local-monitor/v1/ai/session-runs/{first}")).GetProperty("state").GetString());
        string nodeId;
        using (var connection = Open(temp.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND kind='tool' ORDER BY node_id LIMIT 1;";
            command.Parameters.AddWithValue("$session", SessionId);
            nodeId = (string)command.ExecuteScalar()!;
        }
        var node = await Start(host.Client, "/api/local-monitor/v1/ai/node-runs",
            $$"""{"session_id":"{{SessionId}}","node_id":"{{nodeId}}","model":"model-b"}""");
        Assert.Equal("zero_findings", (await Poll(host.Client, $"/api/local-monitor/v1/ai/node-runs/{node}")).GetProperty("state").GetString());

        Assert.Equal(["model-a", "model-b"], provider.Models);
        using var firstRun = Open(temp.DatabasePath);
        using var query = firstRun.CreateCommand();
        query.CommandText = "SELECT model, configuration_sha256 FROM local_ai_runs WHERE run_id=$id;";
        query.Parameters.AddWithValue("$id", first);
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("model-a", reader.GetString(0));
        var firstHash = reader.GetString(1);
        reader.Close();
        query.Parameters["$id"].Value = node;
        using var nodeReader = query.ExecuteReader();
        Assert.True(nodeReader.Read());
        Assert.Equal("model-b", nodeReader.GetString(0));
        Assert.NotEqual(firstHash, nodeReader.GetString(1));
        Assert.Equal("model-a", JsonDocument.Parse((await Get(host.Client, $"/api/local-monitor/v1/ai/runs/{first}")).GetProperty("result").GetRawText())
            .RootElement.GetProperty("provenance").GetProperty("model").GetString());
    }

    [Fact]
    public async Task MissingAutoAndUndiscoveredModelsAreRejectedWithoutRuns()
    {
        using var temp = new MonitorTempDirectory();
        var discovery = new FixedLocalAiModelDiscoveryV1("model-a");
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), new NoSnapshots(), new CountingRuns(), new RecordingProvider(),
            models: discovery);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            LocalAiAnalysisApplication = application,
            LocalAiModelDiscovery = discovery,
        });
        using var missing = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/session-runs", $$"""{"session_id":"{{SessionId}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var auto = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/session-runs", $$"""{"session_id":"{{SessionId}}","model":"auto"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, auto.StatusCode);
        using var unknown = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/session-runs", $$"""{"session_id":"{{SessionId}}","model":"model-b"}"""));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal("{\"error\":\"model_unavailable\"}", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(0, CountingRuns.Creates);
    }

    [Fact]
    public async Task ControlledProviderMismatchPreservesFailedRunWithoutResult()
    {
        using var temp = new MonitorTempDirectory();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var scope = new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: temp.TimeProvider),
            skillRegistryAuthority: authority, timeProvider: temp.TimeProvider);
        var session = new LocalAiSession("other-model");
        var client = new CatalogClient(["model-a"], session: session);
        await using var host = await Host(temp, client, scope: scope);
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(temp.DatabasePath, SessionId, RunA, RunB);
        using var refresh = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/models", "{}"));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var runId = await Start(host.Client, "/api/local-monitor/v1/ai/session-runs",
            $$"""{"session_id":"{{SessionId}}","model":"model-a"}""");
        var status = await Poll(host.Client, $"/api/local-monitor/v1/ai/runs/{runId}");
        Assert.Equal("provider_failed", status.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("result").ValueKind);
        var reports = await Get(host.Client, $"/api/local-monitor/v1/ai/sessions/{SessionId}/reports");
        Assert.DoesNotContain(reports.GetProperty("reports").EnumerateArray(), item => item.GetProperty("run_id").GetString() == runId);
    }

    [Fact]
    public async Task ControlledProviderMatchingModelPersistsValidZeroFindingsAndReadback()
    {
        using var temp = new MonitorTempDirectory();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var scope = new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: temp.TimeProvider),
            skillRegistryAuthority: authority, timeProvider: temp.TimeProvider);
        var session = new LocalAiSession("model-a");
        await using var host = await Host(temp, new CatalogClient(["model-a"], session: session), scope: scope);
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(temp.DatabasePath, SessionId, RunA, RunB);
        using var refresh = await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/models", "{}"));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var runId = await Start(host.Client, "/api/local-monitor/v1/ai/session-runs",
            $$"""{"session_id":"{{SessionId}}","model":"model-a"}""");
        var status = await Poll(host.Client, $"/api/local-monitor/v1/ai/session-runs/{runId}");
        Assert.Equal("zero_findings", status.GetProperty("state").GetString());
        Assert.Equal("model-a", status.GetProperty("result").GetProperty("provenance").GetProperty("model").GetString());
        var reports = await Get(host.Client, $"/api/local-monitor/v1/ai/sessions/{SessionId}/reports");
        var item = Assert.Single(reports.GetProperty("reports").EnumerateArray());
        Assert.Equal(runId, item.GetProperty("run_id").GetString());
        Assert.Equal("zero_findings", item.GetProperty("state").GetString());
        Assert.Equal("retained", item.GetProperty("content_state").GetString());
        Assert.Equal("model-a", item.GetProperty("result").GetProperty("provenance").GetProperty("model").GetString());
    }

    public enum CatalogBehavior { Ready, Unauthenticated, Unavailable, Failed, Empty }

    private static Task<RunningMonitorHost> Host(MonitorTempDirectory temp, CatalogClient client,
        IReadOnlyDictionary<string, string?>? configuration = null,
        SqliteLocalRepositoryScopeSnapshotService? scope = null) =>
        MonitorTestHost.StartAsync(temp, repositoryAiEnabled: false, compareAiEnabled: false, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            LocalRepositoryScopeSnapshotService = scope,
            SettingsAiReadinessClientFactory = () => client,
            ConfigurationValues = configuration,
            TimeProvider = temp.TimeProvider,
        });

    private static async Task<string> Start(HttpClient client, string path, string body)
    {
        using var response = await client.SendAsync(Post(path, body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return json.RootElement.GetProperty("run_id").GetString()!;
    }

    private static async Task<JsonElement> Poll(HttpClient client, string path)
    {
        for (var i = 0; i < 50; i++)
        {
            var value = await Get(client, path);
            if (value.GetProperty("state").GetString() != "running") return value;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    private static async Task<JsonElement> Get(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return json.RootElement.Clone();
    }

    private static HttpRequestMessage Post(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private static Microsoft.Data.Sqlite.SqliteConnection Open(string path)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    internal sealed class FixedLocalAiModelDiscoveryV1 : ILocalAiModelDiscoveryV1
    {
        private readonly LocalAiModelDiscoverySnapshotV1 snapshot;
        public FixedLocalAiModelDiscoveryV1(params string[] ids)
        {
            var models = ids.Select(id => new LocalAiDiscoveredModelV1(id, id)).ToArray();
            snapshot = new("ready", models, null, false, 1);
        }
        public LocalAiModelDiscoverySnapshotV1 Current() => snapshot;
        public ValueTask<LocalAiModelDiscoverySnapshotV1> RefreshAsync(CancellationToken token) => ValueTask.FromResult(snapshot);
        public bool IsSelectable(string model) => snapshot.Models.Any(item => item.Id == model);
    }

    private sealed class CatalogClient : IOwnedCopilotClientV1
    {
        internal static int ListCalls;
        private readonly string[] ids;
        private readonly CatalogBehavior behavior;
        private readonly IOwnedCopilotSessionV1? session;
        public CatalogClient(params string[] ids) : this(ids, CatalogBehavior.Ready, null) { }
        public CatalogClient(string[] ids, IOwnedCopilotSessionV1 session) : this(ids, CatalogBehavior.Ready, session) { }
        public CatalogClient(CatalogBehavior behavior = CatalogBehavior.Ready, IOwnedCopilotSessionV1? session = null)
            : this([], behavior, session) { }
        private CatalogClient(string[] ids, CatalogBehavior behavior, IOwnedCopilotSessionV1? session)
        {
            this.ids = ids;
            this.behavior = behavior;
            this.session = session;
            ListCalls = 0;
        }
        public Task StartAsync(CancellationToken cancellationToken) =>
            behavior == CatalogBehavior.Failed ? Task.FromException(new InvalidOperationException("synthetic")) : Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(
            behavior == CatalogBehavior.Unavailable ? null
            : new CopilotRuntimeStatusObservationV1("1.0.75", 3, null, behavior != CatalogBehavior.Unauthenticated));
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            Task.FromResult(session ?? throw new NotSupportedException());
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CopilotModelCatalogEntryV1>?> ListModelsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ListCalls);
            if (behavior == CatalogBehavior.Failed) throw new InvalidOperationException("synthetic");
            if (behavior is CatalogBehavior.Empty or CatalogBehavior.Unauthenticated or CatalogBehavior.Unavailable)
                return Task.FromResult<IReadOnlyList<CopilotModelCatalogEntryV1>?>([]);
            return Task.FromResult<IReadOnlyList<CopilotModelCatalogEntryV1>?>(ids.Select(id => new CopilotModelCatalogEntryV1(id, id)).ToArray());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LocalAiSession(string effectiveModel) : IOwnedCopilotSessionV1
    {
        public string SessionId => "local-ai-session";
        public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnedCopilotFinalResponseV1?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<OwnedCopilotFinalResponseV1?>(new(
                "{\"summary\":\"ok\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}", effectiveModel));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingProvider : ILocalAiProviderAdapterV1
    {
        public List<string> Models { get; } = [];
        public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
        {
            Models.Add(request.Run.Model ?? "");
            return ValueTask.FromResult(LocalAiProviderOutcomeV1.Complete("""{"summary":"ok","findings":[],"improvement_suggestions":[],"limitations":[]}"""u8.ToArray()));
        }
    }

    private sealed class CountingRuns : ILocalAiRunRepositoryV1
    {
        internal static int Creates;
        public CountingRuns() => Creates = 0;
        public LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 snapshot, int timeout, string? model = null)
        {
            Creates++;
            throw new InvalidOperationException("create_not_expected");
        }
        public void Start(string runId) { }
        public LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome, DateTimeOffset completedAt) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Fail(string runId, string errorCode) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Read(string runId) => throw new NotSupportedException();
        public bool Cancel(string runId) => false;
        public LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentPayloadSha256) => throw new NotSupportedException();
    }

    private sealed class NoSnapshots : ILocalAiSnapshotProjectionServiceV1
    {
        public ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string sessionId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string sessionId, string nodeId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot, CancellationToken token) => ValueTask.FromResult(true);
    }
}

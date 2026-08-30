using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiSnapshotApplicationRouteTests
{
    [Theory]
    [InlineData(257, 1, 1)]
    [InlineData(1, 4097, 1)]
    [InlineData(1, 1, 4097)]
    public void SessionProjectionRejectsEveryNonTruncatingCardinalityLimit(int executions, int events, int spans)
    {
        var input = ProjectionInput(executions, events, spans);
        Assert.Throws<LocalAiScopeTooLargeException>(() => LocalAiSnapshotProjectionBuilderV1.BuildSession(input));
    }

    [Fact]
    public void NodeProjectionAdmitsOnlyAnchorAncestorsDescendantsAndSameExecutionReferences()
    {
        var input = ProjectionInput(1, 4, 0) with
        {
            AnchorNodeId = "node-anchor",
            Nodes =
            [
                new("node-root", "execution-1", null, []),
                new("node-anchor", "execution-1", "node-root", ["node-reference"]),
                new("node-child", "execution-1", "node-anchor", []),
                new("node-reference", "execution-1", null, []),
                new("node-other", "execution-2", null, []),
            ],
        };

        var snapshot = LocalAiSnapshotProjectionBuilderV1.BuildNode(input);
        Assert.Equal(["node-anchor", "node-child", "node-reference", "node-root"], snapshot.EvidenceIdentifiers.Order());
        Assert.DoesNotContain("node-other", snapshot.EvidenceIdentifiers);
    }

    [Fact]
    public async Task RawCapabilityRejectsOutsideEvidenceAndEnforcesEveryReadCeiling()
    {
        var capability = new LocalAiRawReadCapabilityV1(["allowed"], (_, _) => ValueTask.FromResult(new byte[1_048_576]));
        await Assert.ThrowsAsync<LocalAiRawReadException>(() => capability.ReadAsync("outside", CancellationToken.None).AsTask());
        for (var index = 0; index < 16; index++) _ = await capability.ReadAsync("allowed", CancellationToken.None);
        await Assert.ThrowsAsync<LocalAiRawReadException>(() => capability.ReadAsync("allowed", CancellationToken.None).AsTask());

        var oversized = new LocalAiRawReadCapabilityV1(["allowed"], (_, _) => ValueTask.FromResult(new byte[1_048_577]));
        await Assert.ThrowsAsync<LocalAiRawReadException>(() => oversized.ReadAsync("allowed", CancellationToken.None).AsTask());

        var reads = new LocalAiRawReadCapabilityV1(["allowed"], (_, _) => ValueTask.FromResult(Array.Empty<byte>()));
        for (var index = 0; index < 64; index++) _ = await reads.ReadAsync("allowed", CancellationToken.None);
        await Assert.ThrowsAsync<LocalAiRawReadException>(() => reads.ReadAsync("allowed", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ProviderUnavailableCreatesNeitherSnapshotNorRun()
    {
        var snapshots = new RecordingSnapshots();
        var runs = new RecordingRuns();
        var application = new LocalAiAnalysisApplicationV1(
            _ => ValueTask.FromResult(false), snapshots, runs, new Provider(LocalAiProviderOutcomeV1.Complete(ValidResult())));

        var result = await application.StartSessionAsync(new(SessionId, 60), CancellationToken.None);

        Assert.Equal("provider_unavailable", result.ErrorCode);
        Assert.Equal(0, snapshots.Reads);
        Assert.Equal(0, runs.Creates);
    }

    [Theory]
    [InlineData("partial", true, "provider_partial")]
    [InlineData("failed", true, "provider_failed")]
    [InlineData("complete", false, "stale_snapshot")]
    public async Task LifecyclePersistsPartialFailureAndStaleAsTerminalErrors(
        string kind, bool current, string expected)
    {
        var snapshot = new FixedSnapshots(current);
        var runs = new LifecycleRuns();
        var outcome = kind switch { "partial" => LocalAiProviderOutcomeV1.Partial(),
            "failed" => LocalAiProviderOutcomeV1.Failed(), _ => LocalAiProviderOutcomeV1.Complete(ValidResult()) };
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), snapshot, runs, new Provider(outcome));

        var response = await application.StartSessionAsync(new(SessionId, 60), CancellationToken.None);

        Assert.NotNull(response.RunId);
        Assert.Equal(expected, runs.State);
    }

    [Fact]
    public async Task NodeTranscriptIsInvocationOnlyAndNeverPassedToRunPersistence()
    {
        var runs = new LifecycleRuns(); var provider = new CapturingProvider();
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), new FixedSnapshots(true), runs, provider);
        var request = new LocalAiNodeStartRequestV1(SessionId, "node-anchor", 60, "question", [new("prior", "answer")]);

        _ = await application.StartNodeAsync(request, CancellationToken.None);

        Assert.Equal("question", provider.Request!.Question);
        Assert.Single(provider.Request.PriorTurns);
        Assert.DoesNotContain("question", runs.PersistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("answer", runs.PersistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutesAreClosedStrictNoStoreAndEnforceMethodCsrfAndCanonicalUuid()
    {
        var application = new StubApplication();
        await using var host = await Host(application);
        using var wrongMethod = await host.GetAsync("/api/local-monitor/v1/ai/session-runs");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("POST", wrongMethod.Content.Headers.Allow.Single());
        Assert.Equal("no-store", wrongMethod.Headers.CacheControl!.ToString());

        using var noCsrf = await host.PostAsync("/api/local-monitor/v1/ai/session-runs", Json($$"""{"session_id":"{{SessionId}}"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Equal("{\"error\":\"csrf_rejected\"}", await noCsrf.Content.ReadAsStringAsync());

        using var unknown = Request(HttpMethod.Post, "/api/local-monitor/v1/ai/session-runs", $$"""{"session_id":"{{SessionId}}","extra":true}""");
        using var unknownResponse = await host.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);

        using var invalidId = await host.GetAsync("/api/local-monitor/v1/ai/runs/018F0000-0000-7000-8000-000000000001");
        Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);
    }

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private static LocalAiProjectionInputV1 ProjectionInput(int executions, int events, int spans) => new(
        SessionId, "revision-a", Enumerable.Range(0, executions).Select(i => $"execution-{i}").ToArray(),
        Enumerable.Range(0, events).Select(i => new LocalAiProjectionNodeV1($"node-{i}", "execution-0", null, [])).ToArray(),
        Enumerable.Range(0, spans).Select(i => $"span-{i}").ToArray());
    private static byte[] ValidResult() => "{}"u8.ToArray();
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static HttpRequestMessage Request(HttpMethod method, string path, string body)
    {
        var request = new HttpRequestMessage(method, path) { Content = Json(body) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }
    private static async Task<RouteHost> Host(ILocalAiAnalysisApplicationV1 application)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); LocalAiRoutesV1.Map(app, application); await app.StartAsync();
        var address = app.Urls.Single(); return new RouteHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class RouteHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public Task<HttpResponseMessage> GetAsync(string path) => client.GetAsync(path);
        public Task<HttpResponseMessage> PostAsync(string path, HttpContent content) => client.PostAsync(path, content);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => client.SendAsync(request);
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class RecordingSnapshots : ILocalAiSnapshotProjectionServiceV1
    {
        public int Reads { get; private set; }
        public ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string sessionId, CancellationToken token) { Reads++; throw new Xunit.Sdk.XunitException("must not read"); }
        public ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string sessionId, string nodeId, CancellationToken token) { Reads++; throw new Xunit.Sdk.XunitException("must not read"); }
        public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot, CancellationToken token) => ValueTask.FromResult(true);
    }
    private sealed class RecordingRuns : ILocalAiRunRepositoryV1
    {
        public int Creates { get; private set; }
        public LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 snapshot, int timeout) { Creates++; throw new Xunit.Sdk.XunitException("must not create"); }
        public void Start(string runId) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Fail(string runId, string errorCode) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Read(string runId) => throw new NotSupportedException();
        public bool Cancel(string runId) => throw new NotSupportedException();
        public LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentRevision) => throw new NotSupportedException();
    }
    private sealed class Provider(LocalAiProviderOutcomeV1 outcome) : ILocalAiProviderAdapterV1
    { public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token) => ValueTask.FromResult(outcome); }
    private sealed class CapturingProvider : ILocalAiProviderAdapterV1
    {
        internal LocalAiProviderRequestV1? Request { get; private set; }
        public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
        { Request=request; return ValueTask.FromResult(LocalAiProviderOutcomeV1.Partial()); }
    }
    private sealed class FixedSnapshots(bool current) : ILocalAiSnapshotProjectionServiceV1
    {
        private readonly LocalAiSnapshotProjectionV1 snapshot = LocalAiSnapshotProjectionBuilderV1.BuildNode(
            ProjectionInput(1,1,0) with { AnchorNodeId="node-anchor", Nodes=[new("node-anchor","execution-0",null,[])] });
        public ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string sessionId,CancellationToken token) => ValueTask.FromResult(snapshot with { ScopeKind="session",NodeId=null,AnchorId=sessionId });
        public ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string sessionId,string nodeId,CancellationToken token) => ValueTask.FromResult(snapshot);
        public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token) => ValueTask.FromResult(current);
    }
    private sealed class LifecycleRuns : ILocalAiRunRepositoryV1
    {
        internal string State { get; private set; }="queued";
        internal string PersistedText { get; private set; }=string.Empty;
        public LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 snapshot,int timeout) { PersistedText=Encoding.UTF8.GetString(snapshot.PayloadCanonicalJson); return Status(); }
        public void Start(string runId)=>State="running";
        public LocalAiRunStatusV1 Complete(string runId,LocalAiProviderOutcomeV1 outcome) { State=outcome.Kind==LocalAiProviderOutcomeKindV1.Partial?"provider_partial":outcome.Kind==LocalAiProviderOutcomeKindV1.Failed?"provider_failed":"succeeded"; return Status(); }
        public LocalAiRunStatusV1 Fail(string runId,string errorCode){State=errorCode;return Status();}
        public LocalAiRunStatusV1 Read(string runId)=>Status();
        public bool Cancel(string runId){State="canceled";return true;}
        public LocalAiReportPageResponseV1 Reports(string sessionId,int? limit,string? cursor,string revision)=>new([],null);
        private LocalAiRunStatusV1 Status()=>new("018f0000-0000-7000-8000-000000000010",State,"session",SessionId,null,State=="running"?null:State);
    }
    private sealed class StubApplication : ILocalAiAnalysisApplicationV1
    {
        public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) => ValueTask.FromResult(new LocalAiStartResponseV1(null, "provider_unavailable"));
        public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token) => ValueTask.FromResult<LocalAiRunStatusV1?>(null);
        public ValueTask<bool> CancelAsync(string runId, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
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
    public void NodeProjectionExcludesSpanOwnedByUnadmittedNode()
    {
        var input = ProjectionInput(2, 2, 0) with
        {
            AnchorNodeId = "node-anchor",
            Nodes =
            [
                new("node-anchor", "execution-0", null, [], SanitizedSpanObservation: "trace-a:span-a"),
                new("node-other", "execution-1", null, [], SanitizedSpanObservation: "trace-b:span-b"),
            ],
        };
        var snapshot = LocalAiSnapshotProjectionBuilderV1.BuildNode(input);
        var payload = Encoding.UTF8.GetString(snapshot.PayloadCanonicalJson);
        Assert.Contains("trace-a:span-a", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-b:span-b", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultEnvelopeReplacesProviderOwnedProvenanceWithApplicationFacts()
    {
        var snapshot = LocalAiSnapshotProjectionBuilderV1.BuildSession(ProjectionInput(1, 1, 0));
        var run = new LocalAiRunStatusV1("018f0000-0000-7000-8000-000000000010", "running", "session", SessionId, null, null,
            RequestedAt: "2026-01-01T00:00:00.0000000+00:00", StartedAt: "2026-01-01T00:00:01.0000000+00:00",
            Model: "model-a", ConfigurationSha256: new string('a',64), PromptTemplateVersion: "template-a");
        var provider = Encoding.UTF8.GetBytes("""{"summary":"ok","findings":[],"improvement_suggestions":[],"limitations":[]}""");
        var bytes = LocalAiResultEnvelopeV1.Compose(provider, snapshot, run, new DateTimeOffset(2026,1,1,0,0,2,TimeSpan.Zero));
        using var document = JsonDocument.Parse(bytes);
        var provenance = document.RootElement.GetProperty("provenance");
        Assert.Equal("2026-01-01T00:00:02.0000000+00:00", provenance.GetProperty("completed_at").GetString());
        Assert.Equal("model-a", provenance.GetProperty("model").GetString());
        Assert.Equal(snapshot.SnapshotId, provenance.GetProperty("snapshot_id").GetString());
    }

    [Theory]
    [InlineData("{\"summary\":\"ok\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[],\"extra\":true}")]
    [InlineData("{\"summary\":\"ok\",\"summary\":\"again\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}")]
    [InlineData("{\"summary\":1,\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}")]
    public void ResultEnvelopeRejectsNonClosedProviderContent(string json)
    {
        var snapshot = LocalAiSnapshotProjectionBuilderV1.BuildSession(ProjectionInput(1, 1, 0));
        var run = new LocalAiRunStatusV1("018f0000-0000-7000-8000-000000000010", "running", "session", SessionId, null, null);
        Assert.Empty(LocalAiResultEnvelopeV1.Compose(Encoding.UTF8.GetBytes(json), snapshot, run, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ResultEnvelopeRejectsOversizedProviderContent()
    {
        var snapshot = LocalAiSnapshotProjectionBuilderV1.BuildSession(ProjectionInput(1, 1, 0));
        var run = new LocalAiRunStatusV1("018f0000-0000-7000-8000-000000000010", "running", "session", SessionId, null, null);
        Assert.Empty(LocalAiResultEnvelopeV1.Compose(new byte[1_048_577], snapshot, run, DateTimeOffset.UtcNow));
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
    public async Task HostStopClosesAdmissionCancelsAndDrainsActiveProvider()
    {
        var runs = new LifecycleRuns(); var provider = new BlockingProvider();
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), new FixedSnapshots(true), runs, provider);
        var started = await application.StartSessionAsync(new(SessionId, 60), CancellationToken.None);
        Assert.NotNull(started.RunId);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await application.StopAsync(CancellationToken.None);

        Assert.Equal("canceled", runs.State);
        var rejected = await application.StartSessionAsync(new(SessionId, 60), CancellationToken.None);
        Assert.Equal("provider_unavailable", rejected.ErrorCode);
    }

    [Fact]
    public async Task ExplicitCancelRacingProviderCompletionIsDisposalSafeAndTerminal()
    {
        var runs=new LifecycleRuns();var provider=new CompletionRaceProvider();
        var application=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),new FixedSnapshots(true),runs,provider);
        var started=await application.StartSessionAsync(new(SessionId,60),CancellationToken.None);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await application.CancelAsync(started.RunId!,CancellationToken.None));
        provider.Release.TrySetResult();
        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await application.StopAsync(CancellationToken.None);

        Assert.Equal("canceled",runs.State);
    }

    [Fact]
    public async Task HostStopDrainsReadinessReservationBeforeAnyPersistence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new RecordingSnapshots(); var runs = new RecordingRuns();
        var application = new LocalAiAnalysisApplicationV1(async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }, snapshots, runs,
            new Provider(LocalAiProviderOutcomeV1.Failed()));
        var start = application.StartSessionAsync(new(SessionId, 60), CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await application.StopAsync(CancellationToken.None);
        var response = await start;

        Assert.Equal("provider_unavailable", response.ErrorCode);
        Assert.Equal(0, snapshots.Reads);
        Assert.Equal(0, runs.Creates);
    }

    [Fact]
    public async Task HostStopDrainsSnapshotReservationBeforeRunPersistence()
    {
        var snapshots = new BlockingSnapshots(); var runs = new RecordingRuns();
        var application = new LocalAiAnalysisApplicationV1(_ => ValueTask.FromResult(true), snapshots, runs,
            new Provider(LocalAiProviderOutcomeV1.Failed()));
        var start = application.StartSessionAsync(new(SessionId, 60), CancellationToken.None).AsTask();
        await snapshots.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await application.StopAsync(CancellationToken.None);
        var response = await start;

        Assert.Equal("provider_unavailable", response.ErrorCode);
        Assert.Equal(0, runs.Creates);
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

        using var invalidId = await host.GetAsync("/api/local-monitor/v1/ai/runs/018f0000-0000-6000-8000-000000000001");
        Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);
    }

    [Fact]
    public async Task MutationValidationUsesSecurityThenMediaAndBodyPrecedence()
    {
        await using var host = await Host(new StubApplication());
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/ai/session-runs")
        { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteResponse = await host.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);

        using var unsupported = Request(HttpMethod.Post, "/api/local-monitor/v1/ai/session-runs", "{}");
        unsupported.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var unsupportedResponse = await host.SendAsync(unsupported);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupportedResponse.StatusCode);
        Assert.Equal("{\"error\":\"unsupported_media_type\"}", await unsupportedResponse.Content.ReadAsStringAsync());

        using var oversized = Request(HttpMethod.Post, "/api/local-monitor/v1/ai/session-runs", new string('x', 16_385));
        using var oversizedResponse = await host.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Equal("{\"error\":\"request_too_large\"}", await oversizedResponse.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"session_id\":\"018f0000-0000-7000-8000-000000000001\",\"session_id\":\"018f0000-0000-7000-8000-000000000001\"}")]
    [InlineData("{\"session_id\":\"018f0000-0000-7000-8000-000000000001\",\"timeout_seconds\":\"60\"}")]
    public async Task SessionMutationRejectsDuplicateAndWrongTypedJson(string body)
    {
        await using var host = await Host(new StubApplication());
        using var response = await host.SendAsync(Request(HttpMethod.Post, "/api/local-monitor/v1/ai/session-runs", body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/API/local-monitor/v1/ai/session-runs")]
    [InlineData("/api/local-monitor/v1/ai/session-runs/")]
    public async Task RouteAliasesReturnEmptyNoStoreNotFound(string path)
    {
        await using var host = await Host(new StubApplication());
        using var response = await host.SendAsync(Request(HttpMethod.Post, path, $$"""{"session_id":"{{SessionId}}"}"""));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task CanonicalUuidRequiresRfcVariant()
    {
        await using var host = await Host(new StubApplication());
        using var response = await host.GetAsync("/api/local-monitor/v1/ai/runs/018f0000-0000-7000-0000-000000000001");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UppercaseUuidVariableIsInvalidRequestNotAliasNotFound()
    {
        await using var host = await Host(new StubApplication());
        using var response = await host.GetAsync("/api/local-monitor/v1/ai/runs/018F0000-0000-7000-8000-000000000001");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
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
        public LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome, DateTimeOffset completedAt) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Fail(string runId, string errorCode) => throw new NotSupportedException();
        public LocalAiRunStatusV1 Read(string runId) => throw new NotSupportedException();
        public bool Cancel(string runId) => throw new NotSupportedException();
        public LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentRevision) => throw new NotSupportedException();
    }
    private sealed class BlockingSnapshots : ILocalAiSnapshotProjectionServiceV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string sessionId, CancellationToken token)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
        public ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string sessionId, string nodeId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot, CancellationToken token) => ValueTask.FromResult(true);
    }
    private sealed class Provider(LocalAiProviderOutcomeV1 outcome) : ILocalAiProviderAdapterV1
    { public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token) => ValueTask.FromResult(outcome); }
    private sealed class CapturingProvider : ILocalAiProviderAdapterV1
    {
        internal LocalAiProviderRequestV1? Request { get; private set; }
        public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
        { Request=request; return ValueTask.FromResult(LocalAiProviderOutcomeV1.Partial()); }
    }
    private sealed class BlockingProvider : ILocalAiProviderAdapterV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return LocalAiProviderOutcomeV1.Failed(); }
    }
    private sealed class CompletionRaceProvider : ILocalAiProviderAdapterV1
    {
        internal TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request,CancellationToken token)
        {Entered.TrySetResult();await Release.Task;Completed.TrySetResult();return LocalAiProviderOutcomeV1.Complete(ValidResult());}
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
        public LocalAiRunStatusV1 Complete(string runId,LocalAiProviderOutcomeV1 outcome,DateTimeOffset completedAt) { if(State=="canceled")return Status();State=outcome.Kind==LocalAiProviderOutcomeKindV1.Partial?"provider_partial":outcome.Kind==LocalAiProviderOutcomeKindV1.Failed?"provider_failed":"succeeded"; return Status(); }
        public LocalAiRunStatusV1 Fail(string runId,string errorCode){if(State=="canceled")return Status();State=errorCode;return Status();}
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

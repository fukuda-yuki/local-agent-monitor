using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionObjectiveEvaluationRouteTests
{
    [Fact]
    public async Task Post_RecordsExactReceiptAndGetReturnsItInRecordedOrder()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        await using var host = await MonitorTestHost.StartAsync(temp);
        var body = $$"""{"session_id":"{{batch.Detail.Session.SessionId:D}}","run_id":"{{batch.Detail.Runs[0].RunId:D}}","trace_id":"trace-exact","result":"pass","severity":"normal","evaluator_id":"eval","evaluator_version":"v1","criterion_id":"quality","case_key":"case-1","evidence_refs":[{"kind":"run","reference_id":"{{batch.Detail.Runs[0].RunId:D}}"},{"kind":"event","reference_id":"{{batch.Detail.Events[0].EventId:D}}"},{"kind":"trace","reference_id":"trace-exact"},{"kind":"gate","reference_id":"terminal"}]}""";

        using var response = await host.Client.SendAsync(Request(body));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var listed = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/objective-evaluations?session_id={batch.Detail.Session.SessionId:D}");
        var item = Assert.Single(listed!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("pass", item.GetProperty("result").GetString());
        Assert.Equal("trace-exact", item.GetProperty("trace_id").GetString());
        Assert.Equal(4, item.GetProperty("evidence_refs").GetArrayLength());
    }

    [Theory]
    [InlineData("pass", "severe")]
    [InlineData("pass", "normal")]
    public void Receipt_ValidatesResultSeverityContract(string result, string severity)
    {
        var receipt = new ObjectiveEvaluationReceipt(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "trace", result == "pass" ? ObjectiveResult.Pass : ObjectiveResult.Fail, severity == "normal" ? ObjectiveSeverity.Normal : ObjectiveSeverity.Severe, "eval", "v1", "quality", "case", [new("gate", "terminal")], DateTimeOffset.UtcNow);
        Assert.Equal(result == "pass" && severity == "normal", ObjectiveEvaluationValidation.IsValid(receipt));
    }

    [Fact]
    public void Receipt_RejectsUndefinedEnumValues()
    {
        var receipt = new ObjectiveEvaluationReceipt(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "trace", (ObjectiveResult)99, ObjectiveSeverity.Normal, "eval", "v1", "quality", "case", [new("gate", "terminal")], DateTimeOffset.UtcNow);

        Assert.False(ObjectiveEvaluationValidation.IsValid(receipt));
    }

    [Fact]
    public async Task Post_UsesFixedNoEchoErrorsForInvalidAndUnsafeRequests()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        const string sentinel = "C:\\\\secret\\token";
        using var response = await host.Client.SendAsync(Request($$"""{"session_id":"{{Guid.CreateVersion7():D}}","run_id":"{{Guid.CreateVersion7():D}}","trace_id":"{{sentinel}}","result":"pass","severity":"severe","evaluator_id":"{{sentinel}}","evaluator_version":"v1","criterion_id":"quality","case_key":"case","evidence_refs":[]}"""));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_objective_evaluation\"}", text);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a", 100)]
    [InlineData("a.b_c:d-9", 100)]
    [InlineData("a", 200)]
    public void Receipt_AcceptsIdentifierBoundaries(string value, int maximum)
    {
        var identifier = value.Length == 1 && maximum > 1 ? new string('a', maximum) : value;
        Assert.True(ObjectiveEvaluationValidation.IdentifierValue(identifier, maximum));
    }

    [Theory]
    [InlineData("_starts", 100)]
    [InlineData("has space", 100)]
    [InlineData("a/../path", 100)]
    [InlineData("a", 0)]
    public void Receipt_RejectsInvalidIdentifierBoundaries(string value, int maximum)
    {
        Assert.False(ObjectiveEvaluationValidation.IdentifierValue(value, maximum));
    }

    [Fact]
    public async Task Post_RejectsIdentityScopeAndEvidenceMismatchesWithoutEcho()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        await using var host = await MonitorTestHost.StartAsync(temp);
        var sentinel = "C:\\\\secret\\\\objective-token";
        var cases = new[]
        {
            Body(batch, session: Guid.CreateVersion7().ToString("D")), Body(batch, run: Guid.CreateVersion7().ToString("D")),
            Body(batch, trace: "other-trace"), Body(batch, evidence: "[{\"kind\":\"event\",\"reference_id\":\"" + Guid.CreateVersion7() + "\"}]"),
            Body(batch, evidence: "[{\"kind\":\"trace\",\"reference_id\":\"other-trace\"}]"),
            Body(batch, evidence: "[{\"kind\":\"gate\",\"reference_id\":\"missing\"}]"),
            Body(batch, evaluator: sentinel)
        };

        foreach (var (body, index) in cases.Select((body, index) => (body, index)))
        {
            using var response = await host.Client.SendAsync(Request(body));
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.True(response.StatusCode != HttpStatusCode.Created, $"case {index} unexpectedly created a receipt");
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }
        Assert.Empty(store.ListObjectiveEvaluations(batch.Detail.Session.SessionId));
    }

    [Theory]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"session_id\":\"x\",\"session_id\":\"y\"}")]
    [InlineData("not-json")]
    public async Task Post_RejectsMalformedUnknownAndDuplicateJson(string payload)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(Request(payload));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("{\"error\":\"invalid_objective_evaluation\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_RejectsNullTracePassSevereAndInvalidEvidenceCardinalityOrKind()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        await using var host = await MonitorTestHost.StartAsync(temp);
        var eleven = "[" + string.Join(',', Enumerable.Range(0, 11).Select(index => "{\"kind\":\"run\",\"reference_id\":\"" + batch.Detail.Runs[0].RunId + index + "\"}")) + "]";
        var cases = new[]
        {
            Body(batch).Replace("\"trace-exact\"", "null", StringComparison.Ordinal),
            Body(batch).Replace("\"severity\":\"normal\"", "\"severity\":\"severe\"", StringComparison.Ordinal).Replace("\"result\":\"fail\"", "\"result\":\"pass\"", StringComparison.Ordinal),
            Body(batch, evidence: "[]"),
            Body(batch, evidence: eleven),
            Body(batch, evidence: "[{\"kind\":\"unknown\",\"reference_id\":\"x\"}]"),
            Body(batch, evidence: "[{\"kind\":\"run\",\"reference_id\":\"" + batch.Detail.Runs[0].RunId + "\"},{\"kind\":\"run\",\"reference_id\":\"" + batch.Detail.Runs[0].RunId + "\"}]")
        };

        foreach (var body in cases)
        {
            using var response = await host.Client.SendAsync(Request(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("{\"error\":\"invalid_objective_evaluation\"}", await response.Content.ReadAsStringAsync());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
    }

    [Fact]
    public async Task Post_AcceptsBothGateKindsWhenTheyResolveInTheExactRun()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var errorEvent = batch.Detail.Events[0] with { Status = "error" };
        batch = batch with { Detail = batch.Detail with { Events = [errorEvent] } };
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(Request(Body(batch, evidence: "[{\"kind\":\"gate\",\"reference_id\":\"terminal\"},{\"kind\":\"gate\",\"reference_id\":\"error\"}]")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_RejectsRealCrossSessionAndCrossRunEvidenceForEveryKind()
    {
        using var temp = new MonitorTempDirectory();
        var first = CreateExactBatch();
        var secondRun = first.Detail.Runs[0] with { RunId = Guid.CreateVersion7(), TraceId = "trace-second-run" };
        var secondRunEvent = first.Detail.Events[0] with { EventId = Guid.CreateVersion7(), RunId = secondRun.RunId, TraceId = secondRun.TraceId, Type = "event", SourceEventId = "objective-event-second-run" };
        first = first with { Detail = first.Detail with { Runs = [first.Detail.Runs[0], secondRun], Events = [first.Detail.Events[0], secondRunEvent] } };
        var second = CreateExactBatch();
        var secondSession = second.Detail.Session with { SessionId = Guid.CreateVersion7() };
        var otherRun = second.Detail.Runs[0] with { RunId = Guid.CreateVersion7(), SessionId = secondSession.SessionId, TraceId = "trace-second-session" };
        var otherEvent = second.Detail.Events[0] with { EventId = Guid.CreateVersion7(), SessionId = secondSession.SessionId, RunId = otherRun.RunId, TraceId = otherRun.TraceId, Type = "event", SourceEventId = "objective-event-second-session" };
        var otherNative = second.Detail.NativeIds[0] with { SessionId = secondSession.SessionId, NativeSessionId = "objective-native-other" };
        second = second with { Detail = new(secondSession, [otherNative], [otherRun], [otherEvent]) };
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(first);
        store.Write(second);
        await using var host = await MonitorTestHost.StartAsync(temp);
        var wrongSession = new[]
        {
            Body(first, evidence: "[{\"kind\":\"run\",\"reference_id\":\"" + otherRun.RunId + "\"}]"),
            Body(first, evidence: "[{\"kind\":\"event\",\"reference_id\":\"" + otherEvent.EventId + "\"}]"),
            Body(first, evidence: "[{\"kind\":\"trace\",\"reference_id\":\"" + otherRun.TraceId + "\"}]"),
            Body(second, evidence: "[{\"kind\":\"gate\",\"reference_id\":\"terminal\"}]")
        };
        var wrongRun = new[]
        {
            Body(first, run: secondRun.RunId.ToString("D"), trace: secondRun.TraceId, evidence: "[{\"kind\":\"run\",\"reference_id\":\"" + first.Detail.Runs[0].RunId + "\"}]"),
            Body(first, run: secondRun.RunId.ToString("D"), trace: secondRun.TraceId, evidence: "[{\"kind\":\"event\",\"reference_id\":\"" + first.Detail.Events[0].EventId + "\"}]"),
            Body(first, run: secondRun.RunId.ToString("D"), trace: secondRun.TraceId, evidence: "[{\"kind\":\"trace\",\"reference_id\":\"trace-exact\"}]"),
            Body(first, run: secondRun.RunId.ToString("D"), trace: secondRun.TraceId, evidence: "[{\"kind\":\"gate\",\"reference_id\":\"terminal\"}]")
        };

        foreach (var body in wrongSession.Concat(wrongRun))
        {
            using var response = await host.Client.SendAsync(Request(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("{\"error\":\"objective_evidence_not_exact\"}", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Post_MapsControlledSqliteExclusiveLockToFixedStoreUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        await using var host = await MonitorTestHost.StartAsync(temp);
        await using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false, DefaultTimeout = 0 }.ToString());
        await lockConnection.OpenAsync();
        await using var lockCommand = lockConnection.CreateCommand();
        lockCommand.CommandText = "BEGIN EXCLUSIVE;";
        await lockCommand.ExecuteNonQueryAsync();

        using var response = await host.Client.SendAsync(Request(Body(batch)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"objective_store_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Post_EnforcesOriginCsrfMediaTypeAndBodyLimitWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/objective-evaluations") { Content = new StringContent("{}", Encoding.UTF8, "text/plain") },
            new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/objective-evaluations") { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            Request(new string('x', 1024 * 1024 + 1)),
        };
        requests[1].Headers.Add("Origin", "http://untrusted.example");
        requests[2].Content = new StreamedContent(1024 * 1024 + 1);

        foreach (var request in requests)
        {
            using (request)
            {
                using var response = await host.Client.SendAsync(request);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                Assert.True(new[] { HttpStatusCode.Forbidden, HttpStatusCode.UnsupportedMediaType, HttpStatusCode.RequestEntityTooLarge }.Contains(response.StatusCode));
            }
        }
    }

    [Fact]
    public async Task Get_IsNoStoreOrderedAndInvalidQueryDoesNotEcho()
    {
        using var temp = new MonitorTempDirectory();
        var batch = CreateExactBatch();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        store.Write(batch);
        var first = Receipt(batch, Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);
        var second = Receipt(batch, Guid.CreateVersion7(), DateTimeOffset.UnixEpoch.AddSeconds(1));
        store.CreateObjectiveEvaluation(second);
        store.CreateObjectiveEvaluation(first);
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync($"/api/session-workspace/objective-evaluations?session_id={batch.Detail.Session.SessionId:D}");
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var listed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(first.ObjectiveEvaluationId.ToString("D"), listed.RootElement.GetProperty("items")[0].GetProperty("objective_evaluation_id").GetString());
        using var invalid = await host.Client.GetAsync("/api/session-workspace/objective-evaluations?session_id=C%3A%5Csecret");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("secret", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-store", invalid.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Endpoint_HasNoUpdateOrDeleteOperation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/session-workspace/objective-evaluations"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage Request(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/objective-evaluations") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static string Body(SessionWriteBatch batch, string? session = null, string? run = null, string? trace = null, string? evaluator = null, string? evidence = null) =>
        $$"""{"session_id":"{{session ?? batch.Detail.Session.SessionId.ToString("D")}}","run_id":"{{run ?? batch.Detail.Runs[0].RunId.ToString("D")}}","trace_id":"{{trace ?? "trace-exact"}}","result":"fail","severity":"normal","evaluator_id":"{{evaluator ?? "eval"}}","evaluator_version":"v1","criterion_id":"quality","case_key":"case-1","evidence_refs":{{evidence ?? "[{\"kind\":\"run\",\"reference_id\":\"" + batch.Detail.Runs[0].RunId + "\"}]"}}}""";

    private static ObjectiveEvaluationReceipt Receipt(SessionWriteBatch batch, Guid id, DateTimeOffset recordedAt) =>
        new(id, batch.Detail.Session.SessionId, batch.Detail.Runs[0].RunId, "trace-exact", ObjectiveResult.Fail, ObjectiveSeverity.Normal, "eval", "v1", "quality", "case-1", [new("run", batch.Detail.Runs[0].RunId.ToString("D"))], recordedAt);

    private sealed class StreamedContent(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[length]).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static SessionWriteBatch CreateExactBatch()
    {
        var now = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
        var session = new ObservedSession(Guid.CreateVersion7(), ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null, now.AddMinutes(-1), now, now, SessionRawRetentionState.Expiring, now.AddMinutes(-1), now);
        var native = new SessionNativeId(session.SessionId, SessionSourceSurface.CopilotSdk, "objective-native", SessionBindingKind.Native, now.AddMinutes(-1));
        var run = new ObservedSessionRun(Guid.CreateVersion7(), session.SessionId, null, null, "trace-exact", null, null, ObservedSessionStatus.Completed, now.AddMinutes(-1), now, null, null, null);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), session.SessionId, run.RunId, null, null, "trace-exact", null, "test", "event", "Stop", now, SessionContentState.NotCaptured);
        return new(new(session, [native], [run], [@event]), []);
    }
}

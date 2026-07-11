using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

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

    private static HttpRequestMessage Request(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/objective-evaluations") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
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

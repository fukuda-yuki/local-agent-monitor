using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionHumanEvaluationRouteTests
{
    [Fact]
    public async Task Put_RequiresCsrfHeader()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var sessionId = await CreateSessionAsync(host.Client);

        using var response = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"csrf_required"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_RejectsCrossSiteRequestBeforeCsrfHeader()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var sessionId = await CreateSessionAsync(host.Client);
        using var request = EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}", csrf: true);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"cross_origin_forbidden"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_SetsEvaluationAndDetailReturnsVerdictWithRecordedAt()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-11T12:00:00Z"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { TimeProvider = time });
        var sessionId = await CreateSessionAsync(host.Client);

        using var response = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}", csrf: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var evaluation = detail!.RootElement.GetProperty("human_evaluation");
        Assert.Equal("expected", evaluation.GetProperty("verdict").GetString());
        Assert.Equal(time.GetUtcNow(), evaluation.GetProperty("recorded_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Put_OverwritesEvaluation()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-11T12:00:00Z"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { TimeProvider = time });
        var sessionId = await CreateSessionAsync(host.Client);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}", csrf: true))).StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));

        using var response = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"problem\"}", csrf: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var evaluation = detail!.RootElement.GetProperty("human_evaluation");
        Assert.Equal("problem", evaluation.GetProperty("verdict").GetString());
        Assert.Equal(time.GetUtcNow(), evaluation.GetProperty("recorded_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Put_NullVerdictClearsEvaluation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var sessionId = await CreateSessionAsync(host.Client);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}", csrf: true))).StatusCode);

        using var response = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":null}", csrf: true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        Assert.Equal(JsonValueKind.Null, detail!.RootElement.GetProperty("human_evaluation").ValueKind);
    }

    [Fact]
    public async Task Put_UsesFixedSessionAndRequestFailures()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var sessionId = await CreateSessionAsync(host.Client);

        using var invalidSession = await host.Client.SendAsync(EvaluationRequest("not-a-uuid", "{\"verdict\":\"expected\"}", csrf: true));
        using var unknownSession = await host.Client.SendAsync(EvaluationRequest(Guid.CreateVersion7().ToString("D"), "{\"verdict\":\"expected\"}", csrf: true));
        using var wrongContentType = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"expected\"}", csrf: true, contentType: "text/plain"));
        using var invalidVerdict = await host.Client.SendAsync(EvaluationRequest(sessionId, "{\"verdict\":\"other\"}", csrf: true));

        await AssertFailureAsync(invalidSession, HttpStatusCode.BadRequest, "invalid_session_id");
        await AssertFailureAsync(unknownSession, HttpStatusCode.NotFound, "session_not_found");
        await AssertFailureAsync(wrongContentType, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertFailureAsync(invalidVerdict, HttpStatusCode.BadRequest, "invalid_human_evaluation_request");
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var ingest = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent("""
                {"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-evaluation","events":[{"source_event_id":"evaluation-event","type":"Stop","occurred_at":"2026-07-11T00:00:00Z","payload":{}}]}
                """, Encoding.UTF8, "application/json"),
        };
        ingest.Headers.Add("X-CAO-Session-Event-Version", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ingest)).StatusCode);
        using var sessions = await client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        return sessions!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString()!;
    }

    private static HttpRequestMessage EvaluationRequest(string sessionId, string body, bool csrf = false, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/session-workspace/sessions/{sessionId}/human-evaluation")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (csrf)
        {
            request.Headers.Add("x-monitor-csrf", "local-monitor");
        }
        return request;
    }

    private static async Task AssertFailureAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal($$"""{"error":"{{error}}"}""", await response.Content.ReadAsStringAsync());
    }
}

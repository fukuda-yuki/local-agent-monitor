using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionImprovementProposalRouteTests
{
    [Fact]
    public async Task CreateProposal_ReturnsCreatedSanitizedObject_WhenRequestIsValid()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-1");

        using var response = await host.Client.SendAsync(ProposalRequest(ValidCandidate(session)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("candidate", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.TryGetProperty("content", out _));
        Assert.False(body.RootElement.TryGetProperty("source_fragment", out _));
    }

    [Theory]
    [InlineData("verified", "verification_owned_by_compare")]
    [InlineData("recommended", "insufficient_recommendation_evidence")]
    public async Task UpdateProposalStatus_RejectsInvalidTransition(string status, string error)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-2");
        var proposalId = await CreateCandidateAsync(host.Client, session);

        using var response = await host.Client.SendAsync(StatusRequest(proposalId, status));

        await AssertFixedError(response, HttpStatusCode.BadRequest, error);
    }

    [Fact]
    public async Task UpdateProposalStatus_does_not_claim_a_lifecycle_change_while_an_apply_authorization_is_active()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var first = await CreateTerminalSessionAsync(host.Client, "pending-authorization-1");
        var second = await CreateTerminalSessionAsync(host.Client, "pending-authorization-2");
        var proposalId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));
        using (var promoted = await host.Client.SendAsync(StatusRequest(proposalId, "recommended")))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        var store = new SqliteSessionStore(temp.DatabasePath);
        Assert.True(store.TryAuthorizeProposalApply(
            new ProposalApplyPendingOperation(Guid.CreateVersion7(), Guid.CreateVersion7(), proposalId, Guid.CreateVersion7(), 1, "apply", DateTimeOffset.UnixEpoch),
            proposalRevision: 2));

        using var response = await host.Client.SendAsync(StatusRequest(proposalId, "candidate"));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "invalid_proposal_status");
        var proposal = store.GetImprovementProposal(proposalId)!;
        Assert.Equal((ImprovementProposalStatus.Recommended, 2), (proposal.Status, proposal.Revision));
    }

    [Fact]
    public async Task Routes_UseFixedErrorsAndNeverEchoRejectedUnsafeText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-3");
        const string marker = "SECRET_PROPOSAL_MARKER password";

        using var crossSite = ProposalRequest(ValidCandidate(session));
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        crossSite.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/improvement-proposals") { Content = new StringContent(ValidCandidate(session), Encoding.UTF8, "application/json") };
        using var malformed = ProposalRequest("{\"unknown\":true}");
        using var unsafeContent = ProposalRequest(ValidCandidate(session).Replace("Improve test reliability", marker, StringComparison.Ordinal));

        await AssertFixedError(await host.Client.SendAsync(crossSite), HttpStatusCode.Forbidden, "cross_origin_forbidden");
        await AssertFixedError(await host.Client.SendAsync(missingCsrf), HttpStatusCode.Forbidden, "csrf_required");
        await AssertFixedError(await host.Client.SendAsync(malformed), HttpStatusCode.BadRequest, "invalid_proposal_request");
        var unsafeResponse = await host.Client.SendAsync(unsafeContent);
        await AssertFixedError(unsafeResponse, HttpStatusCode.BadRequest, "unsafe_proposal_content");
        Assert.DoesNotContain(marker, await unsafeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListProposal_UsesSessionValidationAndMetadataOnlyResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-4");
        _ = await CreateCandidateAsync(host.Client, session);

        using var response = await host.Client.GetAsync($"/api/session-workspace/improvement-proposals?session_id={session.SessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var proposal = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(proposal.TryGetProperty("content", out _));
        Assert.False(proposal.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task UpdateProposalStatus_PromotesOnlyWithTwoDistinctExactTerminalSessions()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var first = await CreateTerminalSessionAsync(host.Client, "proposal-5a");
        var second = await CreateTerminalSessionAsync(host.Client, "proposal-5b");
        var proposalId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));

        using var response = await host.Client.SendAsync(StatusRequest(proposalId, "recommended"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("recommended", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.TryGetProperty("content", out _));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("recommended")]
    public async Task UpdateProposalStatus_DoesNotMutateCurrentVerifiedProposal(string requestedStatus)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var first = await CreateTerminalSessionAsync(host.Client, "proposal-verified-a");
        var second = await CreateTerminalSessionAsync(host.Client, "proposal-verified-b");
        var proposalId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));
        MarkProposalVerified(temp.DatabasePath, proposalId);

        using var response = await host.Client.SendAsync(StatusRequest(proposalId, requestedStatus));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "verification_owned_by_compare");
        using var proposals = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/improvement-proposals?session_id={first.SessionId}");
        var proposal = Assert.Single(proposals!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("verified", proposal.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, proposal.GetProperty("verified_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("recommended_at").ValueKind);
    }

    [Fact]
    public async Task CreateProposal_RejectsMissingEvidenceWithoutEchoingReference()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-6");
        var missing = Guid.CreateVersion7().ToString("D");

        using var response = await host.Client.SendAsync(ProposalRequest(ValidCandidate(session).Replace(session.EventId, missing, StringComparison.Ordinal)));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "evidence_not_found");
        Assert.DoesNotContain(missing, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateProposalStatus_RejectsExistingRecommendationForSharedSourceSession()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var first = await CreateTerminalSessionAsync(host.Client, "proposal-7a");
        var second = await CreateTerminalSessionAsync(host.Client, "proposal-7b");
        var promotedId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(StatusRequest(promotedId, "recommended"))).StatusCode);
        var competingId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));

        using var response = await host.Client.SendAsync(StatusRequest(competingId, "recommended"));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "recommendation_already_exists");
    }

    [Theory]
    [InlineData("https://example.test/target")]
    [InlineData("src/Feature.cs")]
    [InlineData("..\\Feature.cs")]
    [InlineData("C:\\work\\Feature.cs")]
    [InlineData("C:Feature.cs")]
    [InlineData("public void X()")]
    [InlineData("```csharp")]
    public async Task CreateProposal_RejectsUrisPathsAndSourceFragmentsWithoutEcho(string unsafeValue)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-unsafe");
        using var response = await host.Client.SendAsync(ProposalRequest(ValidCandidate(session).Replace("\"test-helper\"", JsonSerializer.Serialize(unsafeValue), StringComparison.Ordinal)));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "unsafe_proposal_content");
        Assert.DoesNotContain(unsafeValue, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("const int MaxRetries = 3;")]
    [InlineData("using System;")]
    [InlineData("namespace Example;")]
    public async Task CreateProposal_RejectsOrdinarySourceFragmentsWithoutEcho(string unsafeValue)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-source");
        using var response = await host.Client.SendAsync(ProposalRequest(ValidCandidate(session).Replace("\"test-helper\"", JsonSerializer.Serialize(unsafeValue), StringComparison.Ordinal)));

        await AssertFixedError(response, HttpStatusCode.BadRequest, "unsafe_proposal_content");
        Assert.DoesNotContain(unsafeValue, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Improve retry reliability")]
    [InlineData("再試行の安定性を改善")]
    public async Task CreateProposal_AcceptsNaturalLanguageDisplayText(string value)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-natural-language");
        using var response = await host.Client.SendAsync(ProposalRequest(ValidCandidate(session).Replace("\"test-helper\"", JsonSerializer.Serialize(value), StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Writes_RejectUnsupportedMediaTypeAndOversizeBodies()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var session = await CreateTerminalSessionAsync(host.Client, "proposal-media");
        var proposalId = await CreateCandidateAsync(host.Client, session);
        using var postWrongType = Request(HttpMethod.Post, "/api/session-workspace/improvement-proposals", ValidCandidate(session), "text/plain");
        using var putWrongType = Request(HttpMethod.Put, $"/api/session-workspace/improvement-proposals/{proposalId:D}/status", "{\"status\":\"candidate\"}", "text/plain");
        using var postOversize = ProposalRequest(new string('x', 1_048_577));
        using var putOversize = StatusRequest(proposalId, new string('x', 1_048_577));

        await AssertFixedError(await host.Client.SendAsync(postWrongType), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertFixedError(await host.Client.SendAsync(putWrongType), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertFixedError(await host.Client.SendAsync(postOversize), HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        await AssertFixedError(await host.Client.SendAsync(putOversize), HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [Fact]
    public async Task UpdateProposalStatus_FindsProposalWhoseSourceSessionIsOlderThanTwoHundredLaterSessions()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var first = await CreateTerminalSessionAsync(host.Client, "proposal-old-1");
        var second = await CreateTerminalSessionAsync(host.Client, "proposal-old-2");
        var proposalId = await CreateCandidateAsync(host.Client, ValidCandidate(first, second));

        for (var index = 0; index < 201; index++)
        {
            await IngestTerminalSessionAsync(host.Client, $"proposal-later-{index}");
        }

        using var response = await host.Client.SendAsync(StatusRequest(proposalId, "recommended"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(proposalId, body.RootElement.GetProperty("proposal_id").GetGuid());
        Assert.Equal("recommended", body.RootElement.GetProperty("status").GetString());
    }

    private static Task<Guid> CreateCandidateAsync(HttpClient client, TerminalSession session) => CreateCandidateAsync(client, ValidCandidate(session));

    private static async Task<Guid> CreateCandidateAsync(HttpClient client, string payload)
    {
        using var response = await client.SendAsync(ProposalRequest(payload));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("proposal_id").GetGuid();
    }

    private static async Task<TerminalSession> CreateTerminalSessionAsync(HttpClient client, string suffix)
    {
        await IngestTerminalSessionAsync(client, suffix);
        using var sessions = await client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = sessions!.RootElement.GetProperty("items").EnumerateArray().First().GetProperty("session_id").GetString()!;
        using var detail = await client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        return new(sessionId, detail!.RootElement.GetProperty("events")[0].GetProperty("event_id").GetString()!);
    }

    private static async Task IngestTerminalSessionAsync(HttpClient client, string suffix)
    {
        using var ingest = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(
                "{\"schema_version\":1,\"source_adapter\":\"copilot-compatible-hook\",\"source_surface\":\"hook-unknown\",\"native_session_id\":\""
                + suffix
                + "\",\"events\":[{\"source_event_id\":\""
                + suffix
                + "-event\",\"type\":\"Stop\",\"occurred_at\":\"2026-07-11T00:00:00Z\",\"payload\":{}}]}",
                Encoding.UTF8,
                "application/json"),
        };
        ingest.Headers.Add("X-CAO-Session-Event-Version", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ingest)).StatusCode);
    }

    private static HttpRequestMessage ProposalRequest(string body) => Request(HttpMethod.Post, "/api/session-workspace/improvement-proposals", body);

    private static void MarkProposalVerified(string databasePath, Guid proposalId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE improvement_proposals SET status='verified', verified_at='2026-07-12T00:00:00.0000000+00:00' WHERE proposal_id=$proposal_id;";
        command.Parameters.AddWithValue("$proposal_id", proposalId.ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static HttpRequestMessage StatusRequest(Guid proposalId, string status) => Request(HttpMethod.Put, $"/api/session-workspace/improvement-proposals/{proposalId:D}/status", $$"""{"status":"{{status}}"}""");

    private static HttpRequestMessage Request(HttpMethod method, string path, string body, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static string ValidCandidate(TerminalSession session) => $$"""
        {"status":"candidate","target_kind":"skill","target_label":"test-helper","title":"Improve test reliability","summary":"Use focused route coverage.","expected_effect":"Faster feedback.","risk_note":"May require maintenance.","source_sessions":["{{session.SessionId}}"],"evidence_refs":[{"kind":"event","reference_id":"{{session.EventId}}"}]}
        """;

    private static string ValidCandidate(TerminalSession first, TerminalSession second) => $$"""
        {"status":"candidate","target_kind":"skill","target_label":"test-helper","title":"Improve test reliability","summary":"Use focused route coverage.","expected_effect":"Faster feedback.","risk_note":"May require maintenance.","source_sessions":["{{first.SessionId}}","{{second.SessionId}}"],"evidence_refs":[{"kind":"event","reference_id":"{{first.EventId}}"},{"kind":"event","reference_id":"{{second.EventId}}"}]}
        """;

    private sealed record TerminalSession(string SessionId, string EventId);

    private static async Task AssertFixedError(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal($$"""{"error":"{{error}}"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}

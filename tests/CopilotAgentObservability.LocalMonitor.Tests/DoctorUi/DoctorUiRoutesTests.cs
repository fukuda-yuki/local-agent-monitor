using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests.DoctorUi;

public sealed class DoctorUiRoutesTests
{
    private const string VerificationId = "0190c7a0-0000-7000-8000-000000000001";
    private const string Envelope = "{\"contract_version\":\"first-trace.v1\",\"command\":\"status\",\"success\":true,\"code\":\"verification_active\",\"adapter\":\"github-copilot-cli\",\"source_surface\":\"github-copilot-cli\",\"verification_id\":\"0190c7a0-0000-7000-8000-000000000001\",\"doctor\":{\"evaluation\":{\"states\":[{\"evidence_refs\":[\"receipt-1\",\"receipt-2\",\"receipt-3\"]}]},\"verification\":{\"accepted_evidence_refs\":[\"receipt-1\",\"receipt-2\",\"receipt-3\"]}},\"evaluation_preview\":null,\"guidance\":[],\"candidates\":[],\"truncated\":false}";

    [Fact]
    public async Task Sources_ReturnsFixedOrderedRegistryAndBoundedDetection()
    {
        var application = new StubApplication
        {
            Detection = new Dictionary<string, DoctorUiDetectionState>(StringComparer.Ordinal)
            {
                ["claude-code"] = DoctorUiDetectionState.Detected,
                ["github-copilot-cli"] = DoctorUiDetectionState.NotDetected,
            },
        };
        await using var host = await StartAsync(application);

        using var response = await host.Client.GetAsync("/api/doctor/ui/v1/sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("doctor.ui.v1", json.RootElement.GetProperty("schema_version").GetString());
        var sources = json.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(
            ["github-copilot-vscode", "github-copilot-cli", "github-copilot-app-sdk", "claude-code"],
            sources.Select(source => source.GetProperty("source_id").GetString()));
        Assert.Equal("GitHub Copilot CLI", sources[1].GetProperty("display_label").GetString());
        Assert.Equal("managed_windows", sources[1].GetProperty("setup_ownership").GetString());
        Assert.Equal("managed_cli_caller_managed_agent_sdk", sources[3].GetProperty("setup_ownership").GetString());
        Assert.Equal("not_detected", sources[1].GetProperty("detection_state").GetString());
        Assert.Equal("detected", sources[3].GetProperty("detection_state").GetString());
        Assert.Equal("unavailable", sources[0].GetProperty("detection_state").GetString());
    }

    [Fact]
    public async Task Status_PreservesCanonicalEnvelopeAndGeneratesExactOrderedNavigation()
    {
        var application = new StubApplication { Result = SuccessResult() };
        await using var host = await StartAsync(application);

        using var response = await host.Client.GetAsync($"/api/doctor/ui/v1/verifications/{VerificationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(Envelope, json.RootElement.GetProperty("envelope").GetRawText());
        var targets = json.RootElement.GetProperty("navigation_targets").EnumerateArray().ToArray();
        Assert.Equal("/traces/0123456789abcdef0123456789abcdef", targets[0].GetProperty("href").GetString());
        Assert.Equal("/diagnostics?session_id=0190c7a0-0000-7000-8000-000000000002#doctor-session", targets[1].GetProperty("href").GetString());
        Assert.Equal("/diagnostics?observation_id=obs.1#source-diagnostics", targets[2].GetProperty("href").GetString());
        Assert.Equal(VerificationId, application.StatusInput);
    }

    [Fact]
    public async Task Mutations_RequireSameOriginAndExactCsrfBeforeApplicationInvocation()
    {
        var application = new StubApplication { Result = SuccessResult() };
        await using var host = await StartAsync(application);

        using var crossSite = JsonRequest(HttpMethod.Post, "/api/doctor/ui/v1/verifications", "{\"source_id\":\"claude-code\"}");
        crossSite.Headers.Add("x-monitor-csrf", "local-monitor");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var deniedOrigin = await host.Client.SendAsync(crossSite);
        await AssertError(deniedOrigin, HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var missingCsrf = JsonRequest(HttpMethod.Post, "/api/doctor/ui/v1/verifications", "{\"source_id\":\"claude-code\"}");
        missingCsrf.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var deniedCsrf = await host.Client.SendAsync(missingCsrf);
        await AssertError(deniedCsrf, HttpStatusCode.Forbidden, "csrf_required");
        Assert.Null(application.BeginInput);
    }

    [Fact]
    public async Task BeginCompleteAndCancel_ValidateExactInputsAndDoNotRetryMutations()
    {
        var application = new StubApplication { Result = SuccessResult() };
        await using var host = await StartAsync(application);

        using var begin = await host.Client.SendAsync(Mutation(HttpMethod.Post, "/api/doctor/ui/v1/verifications", "{\"source_id\":\"github-copilot-cli\",\"interaction\":\"cli\",\"expires_at\":\"2026-07-21T01:10:00.0000000Z\"}"));
        Assert.Equal(HttpStatusCode.Created, begin.StatusCode);
        Assert.Equal(("github-copilot-cli", "cli", new DateTimeOffset(2026, 7, 21, 1, 10, 0, TimeSpan.Zero)), application.BeginInput);

        using var complete = await host.Client.SendAsync(Mutation(HttpMethod.Post, $"/api/doctor/ui/v1/verifications/{VerificationId}/complete", "{\"expected_revision\":7,\"accepted_evidence_refs\":[\"receipt-2\",\"receipt-1\"]}"));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal((VerificationId, 7), (application.CompleteInput!.Value.Id, application.CompleteInput.Value.Revision));
        Assert.Equal(["receipt-2", "receipt-1"], application.CompleteInput.Value.Refs);

        using var cancel = await host.Client.SendAsync(Mutation(HttpMethod.Post, $"/api/doctor/ui/v1/verifications/{VerificationId}/cancel", "{\"expected_revision\":8}"));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal((VerificationId, 8), application.CancelInput);
        Assert.Equal(3, application.MutationCalls);
    }

    [Fact]
    public async Task InvalidHostPayloadRevisionAndNavigationReturnFixedSanitizedErrors()
    {
        var application = new StubApplication { Result = SuccessResult() };
        await using var host = await StartAsync(application);

        using var invalidHost = new HttpRequestMessage(HttpMethod.Get, "/api/doctor/ui/v1/sources");
        invalidHost.Headers.Host = "example.invalid";
        using var hostResponse = await host.Client.SendAsync(invalidHost);
        await AssertError(hostResponse, HttpStatusCode.BadRequest, "invalid_host");

        using var invalidRevision = await host.Client.GetAsync($"/api/doctor/ui/v1/verifications/{VerificationId}?expected_revision=1");
        await AssertError(invalidRevision, HttpStatusCode.BadRequest, "invalid_payload");

        var largeBody = "{\"source_id\":\"" + new string('a', 65_537) + "\"}";
        using var oversized = await host.Client.SendAsync(Mutation(HttpMethod.Post, "/api/doctor/ui/v1/verifications", largeBody));
        await AssertError(oversized, HttpStatusCode.BadRequest, "invalid_payload");

        application.Result = SuccessResult() with
        {
            NavigationIdentities = [new("not-in-envelope", DoctorUiNavigationTargetKind.Trace, "0123456789abcdef0123456789abcdef")],
        };
        using var invalidNavigation = await host.Client.GetAsync($"/api/doctor/ui/v1/verifications/{VerificationId}");
        await AssertError(invalidNavigation, HttpStatusCode.InternalServerError, "invalid_application_result");
    }

    [Fact]
    public async Task CandidateOnlyEvidence_DoesNotAuthorizeExactNavigation()
    {
        const string candidateOnlyEnvelope = "{\"contract_version\":\"first-trace.v1\",\"command\":\"status\",\"success\":true,\"code\":\"verification_active\",\"adapter\":\"claude-code\",\"source_surface\":\"claude-code\",\"verification_id\":\"0190c7a0-0000-7000-8000-000000000001\",\"doctor\":{\"evaluation\":{\"states\":[]}},\"evaluation_preview\":{\"evaluation\":{\"states\":[{\"evidence_refs\":[\"candidate-only\"]}]}},\"guidance\":[],\"candidates\":[{\"evidence_ref\":\"candidate-only\"}],\"truncated\":false}";
        var application = new StubApplication
        {
            Result = new DoctorUiApplicationResult(
                StatusCodes.Status200OK,
                candidateOnlyEnvelope,
                [new("candidate-only", DoctorUiNavigationTargetKind.Trace, "0123456789abcdef0123456789abcdef")]),
        };
        await using var host = await StartAsync(application);

        using var response = await host.Client.GetAsync($"/api/doctor/ui/v1/verifications/{VerificationId}");

        await AssertError(response, HttpStatusCode.InternalServerError, "invalid_application_result");
    }

    private static DoctorUiApplicationResult SuccessResult() => new(
        StatusCodes.Status200OK,
        Envelope,
        [
            new("receipt-1", DoctorUiNavigationTargetKind.Trace, "0123456789abcdef0123456789abcdef"),
            new("receipt-2", DoctorUiNavigationTargetKind.Session, "0190c7a0-0000-7000-8000-000000000002"),
            new("receipt-3", DoctorUiNavigationTargetKind.SourceDiagnostic, "obs.1"),
        ]);

    private static HttpRequestMessage Mutation(HttpMethod method, string path, string body)
    {
        var request = JsonRequest(method, path, body);
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, string body) => new(method, path)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("doctor.ui.v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
    }

    private static async Task<RunningHost> StartAsync(IDoctorUiApplication application)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        DoctorUiRoutes.Map(app, application);
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class StubApplication : IDoctorUiApplication
    {
        public IReadOnlyDictionary<string, DoctorUiDetectionState> Detection { get; init; } = new Dictionary<string, DoctorUiDetectionState>();
        public DoctorUiApplicationResult Result { get; set; } = SuccessResult();
        public (string SourceId, string? Interaction, DateTimeOffset? ExpiresAt)? BeginInput { get; private set; }
        public string? StatusInput { get; private set; }
        public (string Id, int Revision, IReadOnlyList<string> Refs)? CompleteInput { get; private set; }
        public (string Id, int Revision)? CancelInput { get; private set; }
        public int MutationCalls { get; private set; }
        public IReadOnlyDictionary<string, DoctorUiDetectionState> DetectSources() => Detection;
        public DoctorUiApplicationResult Begin(string sourceId, string? interaction, DateTimeOffset? expiresAt) { BeginInput = (sourceId, interaction, expiresAt); MutationCalls++; return Result with { StatusCode = StatusCodes.Status201Created }; }
        public DoctorUiApplicationResult Status(string verificationId) { StatusInput = verificationId; return Result; }
        public DoctorUiApplicationResult Complete(string verificationId, int expectedRevision, IReadOnlyList<string> acceptedEvidenceRefs) { CompleteInput = (verificationId, expectedRevision, acceptedEvidenceRefs); MutationCalls++; return Result; }
        public DoctorUiApplicationResult Cancel(string verificationId, int expectedRevision) { CancelInput = (verificationId, expectedRevision); MutationCalls++; return Result; }
    }

    private sealed class RunningHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public async ValueTask DisposeAsync() { Client.Dispose(); await application.DisposeAsync(); }
    }
}

using System.Net;
using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests.DoctorUi;

public sealed class DoctorEvidenceRoutesTests
{
    private const string ObservationId = "obs.1";
    private static readonly Guid SessionId = Guid.Parse("0190c7a0-0000-7000-8000-000000000002");
    private const string SensitiveMarker = "SECRET_PROMPT_TEXT_MARKER leak-marker@example.com sk-live-SECRET C:\\Users\\victim\\secret.txt";

    [Fact]
    public async Task SourceDiagnostic_ReturnsOnlyAllowlistedProjectionAndNoStore()
    {
        var compatibilityStore = new CompatibilityStore(SourceRow());
        await using var host = await StartAsync(compatibilityStore, SessionStore(null));

        using var response = await host.Client.GetAsync($"/api/doctor/ui/v1/source-diagnostics/{ObservationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        AssertSensitiveDataAbsent(body);
        using var json = JsonDocument.Parse(body);
        AssertProperties(json.RootElement, "schema_version", "observation");
        Assert.Equal("doctor.ui.v1", json.RootElement.GetProperty("schema_version").GetString());
        var observation = json.RootElement.GetProperty("observation");
        AssertProperties(observation, "observation_id", "source_diagnostic", "observed_at");
        Assert.Equal(ObservationId, observation.GetProperty("observation_id").GetString());
        var diagnostic = observation.GetProperty("source_diagnostic");
        AssertProperties(
            diagnostic,
            "source_surface", "source_application_version", "source_adapter", "adapter_version",
            "schema_fingerprint", "compatibility_state", "reason_codes", "next_action");
        Assert.Equal("claude-code", diagnostic.GetProperty("source_surface").GetString());
        Assert.Equal("supported_with_unknown_fields", diagnostic.GetProperty("compatibility_state").GetString());
        Assert.Equal(["unknown_fields_observed"], diagnostic.GetProperty("reason_codes").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Session_ReturnsOnlyAllowlistedSummaryAndNoStore()
    {
        var detail = SessionDetailWithSensitiveChildren();
        await using var host = await StartAsync(new CompatibilityStore(null), SessionStore(detail));

        using var response = await host.Client.GetAsync($"/api/doctor/ui/v1/sessions/{SessionId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        AssertSensitiveDataAbsent(body);
        using var json = JsonDocument.Parse(body);
        AssertProperties(json.RootElement, "schema_version", "session");
        var session = json.RootElement.GetProperty("session");
        AssertProperties(session, "session_id", "status", "completeness", "started_at", "ended_at", "last_seen_at");
        Assert.Equal(SessionId, session.GetProperty("session_id").GetGuid());
        Assert.Equal("completed", session.GetProperty("status").GetString());
        Assert.Equal("full", session.GetProperty("completeness").GetString());
    }

    [Theory]
    [InlineData("/api/doctor/ui/v1/source-diagnostics/leak%40example.com")]
    [InlineData("/api/doctor/ui/v1/source-diagnostics/obs.1?extra=true")]
    [InlineData("/api/doctor/ui/v1/sessions/not-a-guid")]
    [InlineData("/api/doctor/ui/v1/sessions/0190C7A0-0000-7000-8000-000000000002")]
    [InlineData("/api/doctor/ui/v1/sessions/0190c7a0-0000-7000-8000-000000000002?extra=true")]
    public async Task MalformedIdentityOrQuery_ReturnsFixedBadRequest(string path)
    {
        await using var host = await StartAsync(new CompatibilityStore(SourceRow()), SessionStore(SessionDetailWithSensitiveChildren()));

        using var response = await host.Client.GetAsync(path);

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_evidence_identity");
    }

    [Theory]
    [InlineData("/api/doctor/ui/v1/source-diagnostics/missing.1")]
    [InlineData("/api/doctor/ui/v1/sessions/0190c7a0-0000-7000-8000-000000000003")]
    public async Task MissingExactEvidence_ReturnsFixedNotFound(string path)
    {
        await using var host = await StartAsync(new CompatibilityStore(null), SessionStore(null));

        using var response = await host.Client.GetAsync(path);

        await AssertError(response, HttpStatusCode.NotFound, "evidence_not_found");
    }

    [Fact]
    public async Task StoreExceptions_ReturnFixedUnavailableWithoutExceptionOrSensitiveEcho()
    {
        var exception = new InvalidOperationException($"database failed at {SensitiveMarker}");
        await using var host = await StartAsync(
            new CompatibilityStore(null, exception),
            SessionStore(null, exception));

        using var sourceResponse = await host.Client.GetAsync($"/api/doctor/ui/v1/source-diagnostics/{ObservationId}");
        await AssertError(sourceResponse, HttpStatusCode.ServiceUnavailable, "evidence_unavailable");
        using var sessionResponse = await host.Client.GetAsync($"/api/doctor/ui/v1/sessions/{SessionId:D}");
        await AssertError(sessionResponse, HttpStatusCode.ServiceUnavailable, "evidence_unavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHostOrNonLoopbackRemote_IsDeniedBeforeStoreRead(bool nonLoopbackRemote)
    {
        var compatibilityStore = new CompatibilityStore(SourceRow());
        var sessions = SessionStore(SessionDetailWithSensitiveChildren());
        var sessionProxy = (SessionStoreProxy)(object)sessions;
        await using var host = await StartAsync(compatibilityStore, sessions);
        using var request = new HttpRequestMessage(HttpMethod.Get, nonLoopbackRemote
            ? $"/api/doctor/ui/v1/sessions/{SessionId:D}"
            : $"/api/doctor/ui/v1/source-diagnostics/{ObservationId}");
        if (nonLoopbackRemote)
        {
            request.Headers.Add("X-Test-Remote", "non-loopback");
        }
        else
        {
            request.Headers.Host = "example.invalid";
        }

        using var response = await host.Client.SendAsync(request);

        await AssertError(response, HttpStatusCode.BadRequest, "invalid_evidence_identity");
        Assert.Equal(0, compatibilityStore.ListCalls);
        Assert.Equal(0, sessionProxy.GetDetailCalls);
    }

    private static SourceCompatibilityRow SourceRow() => new(
        Id: 1,
        ObservationId,
        RawRecordId: 9842,
        IngestBatchId: SensitiveMarker,
        SourceSurface: "claude-code",
        SourceApplicationVersion: "2.1.207",
        SourceAdapter: "claude-code-otel",
        AdapterVersion: "adapter-v1",
        SchemaFingerprint: new string('a', 64),
        InventoryHash: SensitiveMarker,
        CompatibilityState: SourceCompatibilityState.SupportedWithUnknownFields,
        ReasonCodes: [SourceCompatibilityReasonCodes.UnknownFieldsObserved],
        NextAction: SourceCompatibilityNextActions.ReviewUnknownFields,
        CaptureContentState: SourceCaptureContentState.Available,
        UnknownSpanCount: 1,
        UnknownEventCount: 2,
        UnknownAttributeCount: 3,
        OverflowDistinctCount: 4,
        OverflowOccurrenceCount: 5,
        ObservedAt: DateTimeOffset.Parse("2026-07-21T01:02:03Z"),
        UnknownObservations:
        [
            new SourceUnknownObservationRow(
                1, 1, SourceUnknownKind.Attribute, SensitiveMarker, 1, SensitiveMarker,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SensitiveMarker),
        ]);

    private static SessionDetail SessionDetailWithSensitiveChildren()
    {
        var at = DateTimeOffset.Parse("2026-07-21T01:02:03Z");
        var session = new ObservedSession(
            SessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            SensitiveMarker, SensitiveMarker, at, at.AddMinutes(1), at.AddMinutes(1),
            SessionRawRetentionState.Expiring, at, at);
        var run = new ObservedSessionRun(
            Guid.Parse("0190c7a0-0000-7000-8000-000000000004"), SessionId,
            SessionSourceSurface.ClaudeCode, SensitiveMarker, new string('b', 32), null,
            SensitiveMarker, ObservedSessionStatus.Completed, at, at, 1, 2, 3);
        var eventItem = new ObservedSessionEvent(
            Guid.Parse("0190c7a0-0000-7000-8000-000000000005"), SessionId, run.RunId,
            SessionSourceSurface.ClaudeCode, null, new string('b', 32), SensitiveMarker,
            "claude-code-otel", SensitiveMarker, SensitiveMarker, at, SessionContentState.Available);
        return new SessionDetail(
            session,
            [new SessionNativeId(SessionId, SessionSourceSurface.ClaudeCode, SensitiveMarker, SessionBindingKind.Native, at)],
            [run],
            [eventItem]);
    }

    private static ISessionStore SessionStore(SessionDetail? detail, Exception? exception = null)
    {
        var store = DispatchProxy.Create<ISessionStore, SessionStoreProxy>();
        var proxy = (SessionStoreProxy)(object)store;
        proxy.Detail = detail;
        proxy.Exception = exception;
        return store;
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        AssertSensitiveDataAbsent(body);
        using var json = JsonDocument.Parse(body);
        AssertProperties(json.RootElement, "schema_version", "error");
        Assert.Equal("doctor.ui.v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
    }

    private static void AssertSensitiveDataAbsent(string body)
    {
        Assert.DoesNotContain(SensitiveMarker, body, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "raw_record_id", "ingest_batch_id", "inventory_hash", "unknown_observations",
            "repository", "workspace", "native_ids", "runs", "events", "content_json",
        })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", body, StringComparison.Ordinal);
        }
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static async Task<RunningHost> StartAsync(
        ISourceCompatibilityStore compatibilityStore,
        ISessionStore sessionStore)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Remote"))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            }
            await next();
        });
        DoctorEvidenceRoutes.Map(app, compatibilityStore, sessionStore);
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class CompatibilityStore(SourceCompatibilityRow? row, Exception? exception = null) : ISourceCompatibilityStore
    {
        public int ListCalls { get; private set; }
        public void CreateSchema() => throw new NotSupportedException();
        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw new NotSupportedException();
        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => throw new NotSupportedException();
        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit)
        {
            ListCalls++;
            if (exception is not null) throw exception;
            return row is not null && after is null ? [row] : [];
        }
    }

    public class SessionStoreProxy : DispatchProxy
    {
        public SessionDetail? Detail { get; set; }
        public Exception? Exception { get; set; }
        public int GetDetailCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISessionStore.GetDetail))
            {
                GetDetailCalls++;
                if (Exception is not null) throw Exception;
                return args is [Guid id] && id == SessionId ? Detail : null;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class RunningHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.DisposeAsync();
        }
    }
}

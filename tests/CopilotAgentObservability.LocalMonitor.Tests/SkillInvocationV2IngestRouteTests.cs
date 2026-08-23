using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2IngestRouteTests : IAsyncLifetime
{
    private const string CapabilityHeaderName = "X-CAO-Skill-Runtime-Capability";
    private const string VersionHeaderName = "X-CAO-Session-Event-Version";
    private const string BodyProbeHeaderName = "X-Test-Throw-On-Body-Read";
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherToken = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ValidRequest =
        "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill-name\",\"path\":\"skills/SKILL.md\",\"content\":\"body\"}}]}";

    private readonly Dictionary<string, TransferRegistration> transfers = new(StringComparer.Ordinal);
    private WebApplication app = null!;
    private HttpClient client = null!;
    private int bodyReadAttempts;
    private int consumeCalls;

    internal Func<HttpContext, IHttpMaxRequestBodySizeFeature?, IHttpMaxRequestBodySizeFeature?> FeatureOverride { get; set; } =
        static (_, feature) => feature;

    internal Func<SkillInvocationV2IngestRequestFactsV1, SkillRuntimeBridgeTransfer, CancellationToken,
        Task<SkillInvocationV2IngestResultV1>> ExecuteIngestAsync { get; set; } =
        static (_, _, _) => Task.FromResult(new SkillInvocationV2IngestResultV1(
            SkillInvocationV2IngestOutcomeV1.Committed,
            TerminalSealAttempted: true));

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();

        app.Use(async (context, next) =>
        {
            var replacement = FeatureOverride(context, context.Features.Get<IHttpMaxRequestBodySizeFeature>());
            context.Features.Set(replacement);
            if (context.Request.Headers.ContainsKey(BodyProbeHeaderName))
            {
                Exception exception = context.Request.Headers[BodyProbeHeaderName].ToString() switch
                {
                    "bad-request" => new BadHttpRequestException("Malformed request body.", 400),
                    "io" => new IOException("Request body transport failed."),
                    _ => new InvalidOperationException("The route read a body before its owning stage."),
                };
                context.Request.Body = new ThrowingReadStream(
                    () => Interlocked.Increment(ref bodyReadAttempts),
                    exception);
            }

            await next();
        });

        SkillInvocationV2IngestRoute.Map(app, new SkillInvocationV2IngestRouteServicesV1(
            TryConsume,
            (facts, transfer, cancellationToken) => ExecuteIngestAsync(facts, transfer, cancellationToken)));

        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        foreach (var registration in transfers.Values)
        {
            registration.Transfer.ReleaseTransferredCapability();
        }

        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task WrongMethod_ReturnsExactOwned405WithoutConsumingOrReleasing(string method)
    {
        var registration = RegisterTransfer(ValidRequestBytes());

        using var request = BuildPost(ValidRequestBytes(), probeBody: true);
        request.Method = new HttpMethod(method);
        using var response = await client.SendAsync(request);

        await AssertMethodNotAllowedAsync(response);
        Assert.Equal(0, consumeCalls);
        Assert.Equal(0, bodyReadAttempts);
        Assert.Equal(1, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task Head_ReturnsOwned405RepresentationHeadersWithoutEntity()
    {
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, SkillInvocationV2IngestRoute.Pattern));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["POST"], response.Content.Headers.Allow);
        Assert.Equal(30, response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("consumed")]
    public async Task UnusableCapabilityForms_ReturnByteIdenticalStage1UnavailableWithoutReadingBody(string form)
    {
        if (form == "consumed")
        {
            RegisterTransfer(ValidRequestBytes());
            var consumed = TryConsume(Token);
            Assert.NotNull(consumed);
            consumed!.ReleaseTransferredCapability();
        }

        using var request = BuildPost(ValidRequestBytes(), capabilityToken: form switch
        {
            "missing" => null,
            "malformed" => "bad",
            "unknown" => OtherToken,
            _ => Token,
        }, probeBody: true);
        if (form == "duplicate")
        {
            request.Headers.Remove(CapabilityHeaderName);
            request.Headers.TryAddWithoutValidation(CapabilityHeaderName, [Token, OtherToken]);
        }

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"local_monitor_ui_unavailable\"}"u8);
        Assert.Equal(0, bodyReadAttempts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("readonly")]
    public async Task UnusableMaxBodyFeature_ReturnsStage1UnavailableAndLeavesCapabilityConsumable(string form)
    {
        FeatureOverride = form == "missing"
            ? static (_, _) => null
            : static (_, _) => new StubMaxBodyFeature { IsReadOnly = true };
        var registration = RegisterTransfer(ValidRequestBytes());

        using var request = BuildPost(ValidRequestBytes(), probeBody: true);
        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"local_monitor_ui_unavailable\"}"u8);
        Assert.Equal(0, consumeCalls);
        Assert.Equal(0, bodyReadAttempts);
        var transfer = TryConsume(Token);
        Assert.Same(registration.Transfer, transfer);
        transfer!.ReleaseTransferredCapability();
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json, text/plain")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=utf-8; charset=utf-8")]
    [InlineData("application/json; charset=utf-8; boundary=x")]
    [InlineData("application/json; charset=utf-16")]
    public async Task RejectedMedia_ReturnsExact415WithoutAllowAndReleasesCapability(string contentType)
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        using var request = BuildPost(ValidRequestBytes(), contentType: contentType);

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType,
            "{\"error\":\"unsupported_media_type\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task TwoPhysicalContentTypeFields_ReturnExact415()
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        using var request = BuildPost(ValidRequestBytes(), contentType: null);
        request.Content!.Headers.TryAddWithoutValidation("Content-Type", ["application/json", "application/json"]);

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType,
            "{\"error\":\"unsupported_media_type\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/JSON")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json;charset=UTF-8")]
    public async Task AcceptedMedia_ReachesClassificationAndReturnsExact204(string contentType)
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        using var request = BuildPost(ValidRequestBytes(), contentType: contentType);

        using var response = await client.SendAsync(request);

        await AssertNoContentAsync(response);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task DeclaredBodyAboveBoundary_ReturnsOwned413WithoutReadingBody()
    {
        var bytes = new byte[SkillInvocationV2IngestRoute.MaxRequestBodyBytes + 1];
        var registration = RegisterTransfer(bytes);
        using var request = BuildPost(bytes, probeBody: true);
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "{\"error\":\"request_too_large\"}"u8);
        Assert.Equal(0, bodyReadAttempts);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task StreamedBodyOneByteAboveBoundary_ReturnsOwned413()
    {
        var bytes = new byte[SkillInvocationV2IngestRoute.MaxRequestBodyBytes + 1];
        var registration = RegisterTransfer(bytes);
        using var request = BuildPost(bytes, contentType: null);
        request.Content = new StreamContent(new MemoryStream(bytes));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "{\"error\":\"request_too_large\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task BodyAtExactBoundary_ProceedsPastSizeGate()
    {
        var prefix = ValidRequestBytes();
        var bytes = new byte[SkillInvocationV2IngestRoute.MaxRequestBodyBytes];
        prefix.CopyTo(bytes, 0);
        bytes.AsSpan(prefix.Length).Fill((byte)' ');
        var registration = RegisterTransfer(bytes);

        using var response = await client.SendAsync(BuildPost(bytes));

        await AssertNoContentAsync(response);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("sha256")]
    public async Task ConsumedEntryLengthOrSha256Mismatch_ReturnsExact400(string mismatch)
    {
        var body = ValidRequestBytes();
        var expected = mismatch == "length" ? [.. body, (byte)' '] : body.ToArray();
        if (mismatch == "sha256")
        {
            expected[0] = (byte)'[';
        }
        var registration = RegisterTransfer(expected);
        var executeCalls = 0;
        ExecuteIngestAsync = (_, _, _) =>
        {
            Interlocked.Increment(ref executeCalls);
            return Task.FromResult(new SkillInvocationV2IngestResultV1(
                SkillInvocationV2IngestOutcomeV1.Committed,
                TerminalSealAttempted: true));
        };

        using var response = await client.SendAsync(BuildPost(body));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}"u8);
        Assert.Equal(0, executeCalls);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task NonPayloadTooLargeBadRequestDuringBodyRead_ReturnsExact400AndReleasesCapability()
    {
        var registration = RegisterTransfer(ValidRequestBytes());

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes(), bodyReadFailure: "bad-request"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}"u8);
        Assert.Equal(1, bodyReadAttempts);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task InvalidatedGeneration_SubstitutesUnavailableForBadRequestDuringBodyRead()
    {
        var registration = RegisterTransfer(ValidRequestBytes(), invalidateOnConsume: true);

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes(), bodyReadFailure: "bad-request"));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"local_monitor_ui_unavailable\"}"u8);
        Assert.Equal(1, bodyReadAttempts);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task IOExceptionDuringBodyRead_AbortsWithoutSubstituteResponseAndReleasesCapability()
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        using var request = BuildPost(ValidRequestBytes(), bodyReadFailure: "io");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, bodyReadAttempts);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong")]
    public async Task InvalidVersionHeader_ReturnsExact400(string form)
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        using var request = BuildPost(ValidRequestBytes(), version: form == "missing" ? null : form == "wrong" ? "1" : "2");
        if (form == "duplicate")
        {
            request.Headers.Remove(VersionHeaderName);
            request.Headers.TryAddWithoutValidation(VersionHeaderName, ["2", "2"]);
        }

        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ParserOuterFault_ReturnsExact400()
    {
        var body = "{}"u8.ToArray();
        var registration = RegisterTransfer(body);

        using var response = await client.SendAsync(BuildPost(body));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData("committed", true, 204, null)]
    [InlineData("replay", true, 204, null)]
    [InlineData("conflict", false, 409, "{\"error\":\"idempotency_conflict\"}")]
    [InlineData("conflict", true, 409, "{\"error\":\"idempotency_conflict\"}")]
    [InlineData("busy", false, 503, "{\"error\":\"persistence_busy\"}")]
    [InlineData("busy", true, 503, "{\"error\":\"persistence_busy\"}")]
    [InlineData("unavailable", false, 503, "{\"error\":\"local_monitor_ui_unavailable\"}")]
    [InlineData("unavailable", true, 503, "{\"error\":\"local_monitor_ui_unavailable\"}")]
    public async Task OrchestratorOutcome_MapsToExactOwnedResponse(
        string outcomeName,
        bool terminalSealAttempted,
        int status,
        string? entity)
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        var outcome = outcomeName switch
        {
            "committed" => SkillInvocationV2IngestOutcomeV1.Committed,
            "replay" => SkillInvocationV2IngestOutcomeV1.ReplaySucceeded,
            "conflict" => SkillInvocationV2IngestOutcomeV1.IdempotencyConflict,
            "busy" => SkillInvocationV2IngestOutcomeV1.PersistenceBusy,
            _ => SkillInvocationV2IngestOutcomeV1.Unavailable,
        };
        ExecuteIngestAsync = (_, _, _) => Task.FromResult(new SkillInvocationV2IngestResultV1(
            outcome,
            terminalSealAttempted));

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes()));

        if (entity is null)
        {
            await AssertNoContentAsync(response);
        }
        else
        {
            await AssertErrorAsync(response, (HttpStatusCode)status, Encoding.UTF8.GetBytes(entity));
        }
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task InvalidatedGeneration_SubstitutesUnavailableFor409Candidate()
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        ExecuteIngestAsync = (_, _, _) =>
        {
            registration.Generation.Invalidate();
            return Task.FromResult(new SkillInvocationV2IngestResultV1(
                SkillInvocationV2IngestOutcomeV1.IdempotencyConflict,
                TerminalSealAttempted: false));
        };

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes()));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"local_monitor_ui_unavailable\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task InvalidatedGeneration_SubstitutesUnavailableFor415Candidate()
    {
        var registration = RegisterTransfer(ValidRequestBytes(), invalidateOnConsume: true);

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes(), contentType: "text/plain"));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"local_monitor_ui_unavailable\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task AlreadyAttemptedTerminalSeal_IsPublishedWithoutASecondSeal()
    {
        var registration = RegisterTransfer(ValidRequestBytes());
        ExecuteIngestAsync = (_, _, _) =>
        {
            registration.Generation.Invalidate();
            return Task.FromResult(new SkillInvocationV2IngestResultV1(
                SkillInvocationV2IngestOutcomeV1.IdempotencyConflict,
                TerminalSealAttempted: true));
        };

        using var response = await client.SendAsync(BuildPost(ValidRequestBytes()));

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "{\"error\":\"idempotency_conflict\"}"u8);
        Assert.Equal(0, registration.Generation.OutstandingCapabilityCount);
    }

    private SkillRuntimeBridgeTransfer? TryConsume(string? token)
    {
        Interlocked.Increment(ref consumeCalls);
        if (token is null || !transfers.Remove(token, out var registration))
        {
            return null;
        }

        if (registration.InvalidateOnConsume)
        {
            registration.Generation.Invalidate();
        }

        return registration.Transfer;
    }

    private TransferRegistration RegisterTransfer(byte[] expectedBody, bool invalidateOnConsume = false)
    {
        var generation = new CopilotRuntimeGenerationV1(new StubRuntimeClient(), new SkillHostShutdownGateV1(), SkillInvocationV2TestIdentity.V1065);
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        var transfer = new SkillRuntimeBridgeTransfer(
            capability,
            expectedBody.Length,
            SHA256.HashData(expectedBody));
        var registration = new TransferRegistration(generation, transfer, invalidateOnConsume);
        transfers.Add(Token, registration);
        return registration;
    }

    private static byte[] ValidRequestBytes() => Encoding.UTF8.GetBytes(ValidRequest);

    private static HttpRequestMessage BuildPost(
        byte[] body,
        string? capabilityToken = Token,
        string? version = "2",
        string? contentType = "application/json",
        bool probeBody = false,
        string? bodyReadFailure = null)
    {
        var content = new ByteArrayContent(body);
        var request = new HttpRequestMessage(HttpMethod.Post, SkillInvocationV2IngestRoute.Pattern) { Content = content };
        if (capabilityToken is not null)
        {
            request.Headers.TryAddWithoutValidation(CapabilityHeaderName, capabilityToken);
        }
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation(VersionHeaderName, version);
        }
        if (probeBody || bodyReadFailure is not null)
        {
            request.Headers.TryAddWithoutValidation(BodyProbeHeaderName, bodyReadFailure ?? "probe");
        }
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return request;
    }

    private static Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        ReadOnlySpan<byte> expectedEntity) =>
        AssertErrorCoreAsync(response, status, expectedEntity.ToArray());

    private static async Task AssertErrorCoreAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        byte[] expectedEntity)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(expectedEntity, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        AssertNoAllow(response);
    }

    private static async Task AssertMethodNotAllowedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("{\"error\":\"method_not_allowed\"}"u8.ToArray(),
            await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal(["POST"], response.Content.Headers.Allow);
    }

    private static async Task AssertNoContentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        AssertNoAllow(response);
    }

    private static void AssertNoAllow(HttpResponseMessage response)
    {
        Assert.Empty(response.Content.Headers.Allow);
    }

    private sealed record TransferRegistration(
        CopilotRuntimeGenerationV1 Generation,
        SkillRuntimeBridgeTransfer Transfer,
        bool InvalidateOnConsume);

    private sealed class StubMaxBodyFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class ThrowingReadStream(Action onRead, Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead();
            throw exception;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            onRead();
            throw exception;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubRuntimeClient : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(new("1.0.65", 3, "1.0.65"));

        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> skillDirectories,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);

        public Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RecordSessionStartCopilotVersion(string? copilotVersion)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using GitHub.Copilot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotRoutesTests : IAsyncLifetime
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";
    private const string SnapshotId = "22222222-2222-4222-8222-222222222222";
    private const string RootPath = @"C:\skills";
    private const string ValidRequest = """{"schema_version":"local-skill-current-file-read.request.v1"}""";

    private static string MetadataPath => $"/api/local-monitor/v1/sessions/{SessionId}/skill-invocations/{SnapshotId}";

    private static string ContentPath => MetadataPath + "/content";

    private static string CurrentFilePath => MetadataPath + "/current-file-read";

    private readonly TempHandleSource handleSource = new();

    private WebApplication app = null!;
    private HttpClient client = null!;
    private CopilotRuntimeAdmissionV1 runtimeAdmission = null!;
    private CopilotRuntimeGenerationV1 generation = null!;
    private StubNativeReader nativeReader = null!;
    private StubGrant grant = null!;
    private StubLease authorizationLease = null!;
    private HttpContext? currentContext;

    internal SkillHistoricalContentRouteResultV1 HistoricalResult { get; set; } =
        new(SkillHistoricalContentRouteOutcomeV1.Document, "{\"historical\":true}"u8.ToArray());

    internal SkillInvocationMetadataDocumentV1Response MetadataResponse { get; set; } =
        new(200, "{\"metadata\":true}"u8.ToArray());

    internal Func<HttpContext, IHttpMaxRequestBodySizeFeature?, IHttpMaxRequestBodySizeFeature?> FeatureOverride { get; set; } =
        static (_, feature) => feature;

    internal Func<Stream, Stream> ResponseBodyOverride { get; set; } = static body => body;

    public async Task InitializeAsync()
    {
        var preflight = SkillDiscoveryRootPreflightV1.Run(
            [],
            [RootPath],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new StubOpener(handleSource)));
        var shutdownGate = new SkillHostShutdownGateV1();
        var rootGeneration = new SkillDiscoveryRootGenerationV1(preflight, shutdownGate);

        runtimeAdmission = new CopilotRuntimeAdmissionV1(shutdownGate);
        generation = runtimeAdmission.PublishReadyTestCandidate(new StubRuntimeClient(), out _);
        nativeReader = new StubNativeReader();
        grant = new StubGrant();
        authorizationLease = new StubLease();

        var orchestrator = new SkillCurrentFileOrchestratorV1(
            new StubHistoricalGate(grant),
            new StubAuthorizationGate(authorizationLease),
            runtimeAdmission,
            new StubDiscoveryGateway(),
            nativeReader);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();

        app.Use(async (context, next) =>
        {
            currentContext = context;
            var replacement = FeatureOverride(context, context.Features.Get<IHttpMaxRequestBodySizeFeature>());
            context.Features.Set(replacement);
            var responseBody = context.Response.Body;
            context.Response.Body = ResponseBodyOverride(responseBody);
            try
            {
                await next();
            }
            finally
            {
                context.Response.Body = responseBody;
                currentContext = null;
            }
        });

        SkillInvocationSnapshotRoutes.Map(app, new SkillInvocationSnapshotRouteServicesV1(
            (_, _, _) => Task.FromResult(MetadataResponse),
            (_, _, _) => Task.FromResult(HistoricalResult),
            rootGeneration,
            orchestrator));

        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        handleSource.Dispose();
    }

    [Theory]
    [InlineData("POST", "GET")]
    [InlineData("PUT", "GET")]
    [InlineData("DELETE", "GET")]
    [InlineData("OPTIONS", "GET")]
    public async Task AWrongMethodOnAMatchingGetPathIsTheOwned405(string method, string allow)
    {
        foreach (var path in new[] { MetadataPath, ContentPath })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal([allow], response.Content.Headers.Allow);
            Assert.Equal("{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task AWrongMethodOnTheCurrentFilePathAllowsOnlyPost()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CurrentFilePath);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["POST"], response.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HeadIsTheSameOwned405WithRepresentationLengthAndNoEntity()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, MetadataPath);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET"], response.Content.Headers.Allow);
        Assert.Equal(30, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task NoNon405ResponseCarriesAnAllowHeader()
    {
        using var metadata = await client.GetAsync(MetadataPath);
        using var content = await client.GetAsync(ContentPath);
        using var currentFile = await PostCurrentFileAsync(ValidRequest);

        Assert.Empty(metadata.Content.Headers.Allow);
        Assert.Empty(content.Content.Headers.Allow);
        Assert.Empty(currentFile.Content.Headers.Allow);
    }

    [Fact]
    public async Task ANonmatchingPathKeepsTheOuter404WithoutAnOwnedEntity()
    {
        using var response = await client.GetAsync(MetadataPath + "/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ACrossSiteGetIsTheExactCsrfRejection()
    {
        foreach (var path in new[] { MetadataPath, ContentPath })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("{\"error\":\"csrf_rejected\"}", await response.Content.ReadAsStringAsync());
            Assert.Empty(response.Content.Headers.Allow);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local-Monitor")]
    [InlineData("local-monitor-extra")]
    public async Task CurrentFileRejectsEveryNonExactMonitorCsrfHeader(string? headerValue)
    {
        using var response = await PostCurrentFileAsync(ValidRequest, csrfHeader: headerValue, addCsrfHeader: headerValue is not null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"csrf_rejected\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CurrentFileRejectsADuplicateMonitorCsrfHeader()
    {
        using var request = BuildCurrentFileRequest(ValidRequest, addCsrfHeader: false);
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"csrf_rejected\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMissingMaxRequestBodyFeatureIsTheStage1UnavailableEvenForACrossSiteRequest()
    {
        FeatureOverride = static (_, _) => null;

        using var request = BuildCurrentFileRequest(ValidRequest, addCsrfHeader: false);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AReadOnlyMaxRequestBodyFeatureIsTheStage1Unavailable()
    {
        FeatureOverride = static (_, _) => new StubMaxBodyFeature { IsReadOnly = true };

        using var response = await PostCurrentFileAsync(ValidRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMaxRequestBodyFeatureThatReadsBackAWrongValueIsTheStage1Unavailable()
    {
        FeatureOverride = static (_, _) => new StubMaxBodyFeature { IgnoreSetter = true, MaxRequestBodySize = 4096 };

        using var response = await PostCurrentFileAsync(ValidRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMaxRequestBodyFeatureWhoseSetterThrowsIsTheStage1Unavailable()
    {
        FeatureOverride = static (_, _) => new StubMaxBodyFeature { ThrowOnSet = true };

        using var response = await PostCurrentFileAsync(ValidRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; boundary=x")]
    [InlineData("application/json, text/plain")]
    public async Task ARejectedRequestMediaIsTheExact415(string contentType)
    {
        using var request = BuildCurrentFileRequest(null, addCsrfHeader: true);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidRequest));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("{\"error\":\"unsupported_media_type\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/JSON; charset=UTF-8")]
    [InlineData("application/json ; charset=utf-8")]
    public async Task AnAcceptedRequestMediaReachesTheRequestParse(string contentType)
    {
        using var request = BuildCurrentFileRequest(null, addCsrfHeader: true);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidRequest));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task ABodyAtExactlyTheBoundaryProceedsAndOneByteOverIsTheOwned413()
    {
        var padding = SkillInvocationSnapshotRoutes.CurrentFileRequestMaxBytes - ValidRequest.Length;
        Assert.True(padding >= 0);

        using var atBoundary = await PostCurrentFileAsync(ValidRequest + new string(' ', padding));
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, atBoundary.StatusCode);

        using var overBoundary = await PostCurrentFileAsync(ValidRequest + new string(' ', padding + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overBoundary.StatusCode);
        Assert.Equal("{\"error\":\"request_too_large\"}", await overBoundary.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AStreamedBodyOneByteOverTheBoundaryIsTheOwned413()
    {
        using var request = BuildCurrentFileRequest(null, addCsrfHeader: true);
        var oversized = Encoding.UTF8.GetBytes(
            ValidRequest.PadRight(SkillInvocationSnapshotRoutes.CurrentFileRequestMaxBytes + 1));
        request.Content = new StreamContent(new MemoryStream(oversized));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"request_too_large\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"schema_version":"wrong"}""")]
    [InlineData("""{"schema_version":"local-skill-current-file-read.request.v1","extra":1}""")]
    public async Task ARejectedRequestDocumentIsTheExact400(string body)
    {
        using var response = await PostCurrentFileAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not-a-uuid", SnapshotId)]
    [InlineData(SessionId, "not-a-uuid")]
    public async Task AnInvalidRouteIdentityIsTheExactSnapshotNotFound(string sessionId, string snapshotId)
    {
        var path = $"/api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}";

        using var metadata = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await metadata.Content.ReadAsStringAsync());

        using var content = await client.GetAsync(path + "/content");
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await content.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheMetadataRouteWritesItsOwnersStatusAndBytes()
    {
        MetadataResponse = new(503, "{\"error\":\"local_monitor_ui_unavailable\"}"u8.ToArray());

        using var response = await client.GetAsync(MetadataPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task HistoricalContentOutcomesMapToTheirExactStatusesAndBytes()
    {
        var expected = new (SkillHistoricalContentRouteOutcomeV1 Outcome, int Status, string Body)[]
        {
            (SkillHistoricalContentRouteOutcomeV1.NotFound, 404, "{\"error\":\"skill_snapshot_not_found\"}"),
            (SkillHistoricalContentRouteOutcomeV1.Expired, 410, "{\"error\":\"skill_snapshot_expired\"}"),
            (SkillHistoricalContentRouteOutcomeV1.ContentUnavailable, 422, "{\"error\":\"skill_snapshot_content_unavailable\"}"),
            (SkillHistoricalContentRouteOutcomeV1.Busy, 503, "{\"error\":\"persistence_busy\"}"),
            (SkillHistoricalContentRouteOutcomeV1.Unavailable, 503, "{\"error\":\"local_monitor_ui_unavailable\"}"),
        };

        foreach (var (outcome, status, body) in expected)
        {
            HistoricalResult = new(outcome, []);

            using var response = await client.GetAsync(ContentPath);

            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AnAcceptedCurrentFileRequestReachesTheOrchestratorAndReturnsItsCandidate()
    {
        using var response = await PostCurrentFileAsync(ValidRequest);

        // The stub orchestrator chain reports a missing current file, proving the request crossed
        // every route stage and the orchestrator produced the candidate.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{\"error\":\"skill_current_file_missing\"}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task CurrentFileRawResponse_HoldsCapabilityUntilTheResponseWriteCompletes()
    {
        var currentBody = "# current body\n"u8.ToArray();
        nativeReader.Result = CurrentSkillNativeReadResultV1.Success(
            currentBody,
            SHA256.HashData(currentBody),
            DateTimeOffset.UnixEpoch);
        var writeGate = new BlockingWriteStream();
        ResponseBodyOverride = body => writeGate.Attach(body);

        var responseTask = client.SendAsync(
            BuildCurrentFileRequest(ValidRequest, addCsrfHeader: true),
            HttpCompletionOption.ResponseHeadersRead);
        await writeGate.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, generation.OutstandingCapabilityCount);
        Assert.Equal(0, authorizationLease.DisposeCalls);

        writeGate.AllowWrite();
        using var response = await responseTask;
        await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(0, generation.OutstandingCapabilityCount);
        Assert.Equal(1, authorizationLease.DisposeCalls);
    }

    [Fact]
    public async Task CurrentFilePreRuntimeFailures_AcquireNoCapability()
    {
        runtimeAdmission.InvalidateCurrentTestGeneration();

        using var response = await PostCurrentFileAsync(ValidRequest);
        await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, generation.OutstandingCapabilityCount);
        Assert.Equal(1, authorizationLease.DisposeCalls);
    }

    [Fact]
    public async Task CurrentFileShutdownClosure_AcquiresNoCapabilityAndAborts()
    {
        runtimeAdmission.CloseForShutdown();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PostCurrentFileAsync(ValidRequest));

        Assert.Equal(0, generation.OutstandingCapabilityCount);
        Assert.Equal(1, authorizationLease.DisposeCalls);
    }

    [Fact]
    public async Task CurrentFileCallerAbort_ReleasesTheAcquiredCapability()
    {
        nativeReader.BeforeRead = () => currentContext!.Abort();

        await Assert.ThrowsAnyAsync<Exception>(() => PostCurrentFileAsync(ValidRequest));

        Assert.True(SpinWait.SpinUntil(
            () => generation.OutstandingCapabilityCount == 0,
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, authorizationLease.DisposeCalls);
    }

    [Fact]
    public async Task CurrentFileResponseWriteException_ReleasesTheAcquiredCapability()
    {
        ResponseBodyOverride = body => new ThrowingWriteStream(body);

        using var response = await PostCurrentFileAsync(ValidRequest);
        await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(SpinWait.SpinUntil(
            () => generation.OutstandingCapabilityCount == 0,
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, authorizationLease.DisposeCalls);
    }

    // The orchestrator's no-response abort has to survive the route: a Retention loss proved at the
    // terminal boundary must leave the transport with no status line, no header, and no entity byte
    // rather than being relabelled into any of the route's ordinary responses.
    [Theory]
    [InlineData("safe_error")]
    [InlineData("raw_success")]
    public async Task CurrentFileRetentionLoss_WritesNoStatusHeaderOrEntityByte(string arm)
    {
        if (arm == "raw_success")
        {
            var currentBody = "# current body\n"u8.ToArray();
            nativeReader.Result = CurrentSkillNativeReadResultV1.Success(
                currentBody, SHA256.HashData(currentBody), DateTimeOffset.UnixEpoch);
        }

        grant.CompleteWithoutRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
        grant.SealRawResult = SkillInvocationSnapshotContentTerminalResult.Lost;
        CountingWriteStream? entity = null;
        ResponseBodyOverride = body => entity = new CountingWriteStream(body);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PostCurrentFileAsync(ValidRequest));

        Assert.NotNull(entity);
        Assert.Equal(0, entity!.BytesWritten);
        Assert.True(SpinWait.SpinUntil(
            () => generation.OutstandingCapabilityCount == 0,
            TimeSpan.FromSeconds(10)));
    }

    private Task<HttpResponseMessage> PostCurrentFileAsync(
        string body,
        string? csrfHeader = "local-monitor",
        bool addCsrfHeader = true) =>
        client.SendAsync(BuildCurrentFileRequest(body, addCsrfHeader, csrfHeader));

    private HttpRequestMessage BuildCurrentFileRequest(
        string? body,
        bool addCsrfHeader,
        string? csrfHeader = "local-monitor")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CurrentFilePath);
        if (addCsrfHeader)
        {
            request.Headers.TryAddWithoutValidation("x-monitor-csrf", csrfHeader);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private sealed class StubMaxBodyFeature : IHttpMaxRequestBodySizeFeature
    {
        private long? maxRequestBodySize;

        internal bool ThrowOnSet { get; init; }

        internal bool IgnoreSetter { get; init; }

        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize
        {
            get => maxRequestBodySize;
            init => maxRequestBodySize = value;
        }

        long? IHttpMaxRequestBodySizeFeature.MaxRequestBodySize
        {
            get => maxRequestBodySize;
            set
            {
                if (ThrowOnSet)
                {
                    throw new InvalidOperationException("The stub feature refuses the assignment.");
                }

                if (!IgnoreSetter)
                {
                    maxRequestBodySize = value;
                }
            }
        }
    }

    private sealed class StubHistoricalGate(StubGrant grant) : ISkillCurrentFileHistoricalGateV1
    {
        public Task<SkillCurrentFileHistoricalAdmissionV1> AdmitAsync(
            Guid sessionId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SkillCurrentFileHistoricalAdmissionV1(
                SkillInvocationSnapshotContentOutcome.Granted,
                grant));
    }

    private sealed class StubGrant : ISkillCurrentFileRetentionGrantV1
    {
        public SkillInvocationSnapshotContentFacts Facts { get; } = new(
            Guid.Parse(SnapshotId),
            "# body\n",
            @"C:\skills\review\SKILL.md",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "1111111111111111111111111111111111111111111111111111111111111111",
            7,
            26,
            DateTimeOffset.UnixEpoch);

        internal SkillInvocationSnapshotContentTerminalResult SealRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.Sealed;

        internal SkillInvocationSnapshotContentTerminalResult CompleteWithoutRawResult { get; set; } =
            SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw;

        public SkillInvocationSnapshotContentTerminalResult TrySealRawResponse() => SealRawResult;

        public SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw() => CompleteWithoutRawResult;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAuthorizationGate(StubLease lease) : ISkillCurrentAuthorizationGateV1
    {
        public SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquire(Guid sessionId, Guid snapshotId) =>
            SkillProjectionCurrentSdkClaimAuthorizationResult.ForAcquired(
                new SkillProjectionCurrentSdkClaimAuthorization("review", "custom", lease));
    }

    private sealed class StubLease : ISkillRegistryGenerationLease
    {
        internal int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private sealed class StubDiscoveryGateway : ISkillDiscoveryGatewayV1
    {
        public Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
            CopilotRuntimeOperationCapabilityV1 capability,
            DiscoveryRootSetV1 roots,
            CancellationToken cancellationToken) =>
            Task.FromResult<CopilotSkillDiscoveryOutcome>(new CopilotSkillDiscoveryOutcome.Discovered(
            [
                new("review", "custom", @"C:\skills\review\SKILL.md", null, null, null, true, true)
            ]));
    }

    private sealed class StubNativeReader : ICurrentSkillNativeFileReaderV1
    {
        internal CurrentSkillNativeReadResultV1 Result { get; set; } =
            CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing);

        internal Action? BeforeRead { get; set; }

        public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            return Result;
        }
    }

    private class DelegatingWriteStream(Stream inner) : Stream
    {
        protected Stream Inner { get; } = inner;

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class BlockingWriteStream : DelegatingWriteStream
    {
        private readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private BlockingWriteStream(Stream inner)
            : base(inner)
        {
        }

        internal BlockingWriteStream()
            : this(Stream.Null)
        {
        }

        internal Task WriteStarted => writeStarted.Task;

        internal BlockingWriteStream Attach(Stream inner) => new(inner)
        {
            sharedWriteStarted = writeStarted,
            sharedAllowWrite = allowWrite
        };

        private TaskCompletionSource sharedWriteStarted = null!;
        private TaskCompletionSource sharedAllowWrite = null!;

        internal void AllowWrite() => allowWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            sharedWriteStarted.TrySetResult();
            await sharedAllowWrite.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class ThrowingWriteStream(Stream inner) : DelegatingWriteStream(inner)
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Response write failed."));
    }

    // Counts what actually reached the response body so a no-response abort can be proved by the
    // absence of entity bytes rather than only by the client-side transport failure.
    private sealed class CountingWriteStream(Stream inner) : DelegatingWriteStream(inner)
    {
        private long written;

        internal long BytesWritten => Interlocked.Read(ref written);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref written, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
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

    private sealed class StubOpener(TempHandleSource handleSource) : IDiscoveryRootOpenerV1
    {
        public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
        {
            Assert.True(SkillProducerPathKeyV1.TryParse(
                configuredRootPath, SkillProducerPathKeyPlatform.Windows, out var pathKey, out _));

            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateWindows(3, new byte[16]),
                handleSource.OpenHandle()));
        }

        public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root) => !root.IsDisposed;
    }

    private sealed class TempHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-skillroutes-{Guid.NewGuid():N}");

        private readonly string filePath;

        public TempHandleSource()
        {
            Directory.CreateDirectory(directoryPath);
            filePath = Path.Combine(directoryPath, "handle-source.bin");
            File.WriteAllBytes(filePath, [1, 2, 3]);
        }

        public SafeFileHandle OpenHandle() => File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

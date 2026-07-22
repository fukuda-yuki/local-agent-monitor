using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.AspNetCore.Http;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalImportRouteTests
{
    [Fact]
    public async Task HostDisposalDisposesTheHistoricalImportApplication()
    {
        using var temp = new MonitorTempDirectory();
        var application = new DisposableHistoricalImportApplication();

        await using (var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietHostOptions(application)))
        {
            Assert.False(application.IsDisposed);
        }

        Assert.True(application.IsDisposed);
    }

    [Theory]
    [InlineData("github-copilot-cli", "selected_root", "session-1")]
    [InlineData("claude-code", "explicit_user_selection", null)]
    public async Task CurrentProductionSource_ReturnsStrictUnsupportedPreviewWithoutConfirmationOrWrites(
        string sourceSurface,
        string referenceKind,
        string? sessionId)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: QuietHostOptions());
        var privateReference = NativeAbsoluteReference("SECRET_HISTORY_PATH.jsonl");

        using var previewRequest = Post(
            "/api/historical-import/v1/previews",
            Selection(privateReference, sourceSurface, referenceKind, sessionId));
        using var preview = await host.Client.SendAsync(previewRequest);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.True(preview.Headers.CacheControl?.NoStore);
        var body = await preview.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateReference, body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("historical-import-workflow/v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("historical-import-workflow-preview/v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(sourceSurface, root.GetProperty("source_surface").GetString());
        Assert.Equal("unsupported", root.GetProperty("source_badge").GetString());
        Assert.Equal("not_read", root.GetProperty("content_risk").GetString());
        Assert.False(root.GetProperty("commit_allowed").GetBoolean());
        Assert.Equal("historical_import_no_eligible_candidates", root.GetProperty("rejection_code").GetString());
        Assert.Equal(0, root.GetProperty("counts").GetProperty("eligible").GetProperty("value").GetInt32());

        using var confirmationRequest = Post("/api/historical-import/v1/confirmations", $$"""
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-confirmation-request/v1","preview_id":"{{root.GetProperty("preview_id").GetString()}}","preview_digest":"{{root.GetProperty("preview_digest").GetString()}}","snapshot_version":"{{root.GetProperty("snapshot_version").GetString()}}","decision":"confirm"}
            """);
        using var confirmation = await host.Client.SendAsync(confirmationRequest);
        await AssertErrorAsync(confirmation, HttpStatusCode.Conflict, "historical_import_no_eligible_candidates");

        using var history = await host.Client.GetAsync("/api/historical-import/v1/history?limit=50");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.True(history.Headers.CacheControl?.NoStore);
        using (var historyJson = JsonDocument.Parse(await history.Content.ReadAsStreamAsync()))
            Assert.Empty(historyJson.RootElement.GetProperty("items").EnumerateArray());
        using var observations = await host.Client.GetAsync("/api/historical-import/v1/observations?limit=50");
        Assert.Equal(HttpStatusCode.OK, observations.StatusCode);
        Assert.True(observations.Headers.CacheControl?.NoStore);
        using (var observationJson = JsonDocument.Parse(await observations.Content.ReadAsStreamAsync()))
            Assert.Empty(observationJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task WorkflowRoutes_EnforceLoopbackSameOriginCsrfStrictBoundedJsonAndNoStore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 512, testOptions: QuietHostOptions());
        var valid = Selection(null);

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/historical-import/v1/history?limit=50");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        await AssertErrorAsync(invalidHost, HttpStatusCode.BadRequest, "invalid_host");

        using var missingCsrf = Post("/api/historical-import/v1/previews", valid, csrf: false);
        await AssertErrorAsync(await host.Client.SendAsync(missingCsrf), HttpStatusCode.Forbidden, "csrf_required");

        using var crossSiteWrite = Post("/api/historical-import/v1/previews", valid);
        crossSiteWrite.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertErrorAsync(await host.Client.SendAsync(crossSiteWrite), HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var wrongContent = new HttpRequestMessage(HttpMethod.Post, "/api/historical-import/v1/previews")
        {
            Content = new StringContent(valid, Encoding.UTF8, "text/plain"),
        };
        wrongContent.Headers.Add("x-monitor-csrf", "local-monitor");
        await AssertErrorAsync(await host.Client.SendAsync(wrongContent), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var unknownMember = Post("/api/historical-import/v1/previews", valid[..^1] + ",\"candidate_batch\":{}}");
        await AssertErrorAsync(await host.Client.SendAsync(unknownMember), HttpStatusCode.BadRequest, "historical_import_request_invalid");

        using var duplicateMember = Post("/api/historical-import/v1/previews", valid[..^1] + ",\"source_surface\":\"claude-code\"}");
        await AssertErrorAsync(await host.Client.SendAsync(duplicateMember), HttpStatusCode.BadRequest, "historical_import_request_invalid");

        using var oversized = Post("/api/historical-import/v1/previews", new string('x', 2_048));
        await AssertErrorAsync(await host.Client.SendAsync(oversized), HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RouteBodyLimit_RejectsKnownAndChunkedBodiesOverOneMiB(bool declareLength)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            maxRequestBodyBytes: 2_097_152,
            testOptions: QuietHostOptions());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/historical-import/v1/previews")
        {
            Content = new StreamingJsonContent(1_048_577, declareLength),
        };
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [Theory]
    [InlineData("/api/historical-import/v1/previews/hip_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "historical_import_preview_not_found")]
    [InlineData("/api/historical-import/v1/imports/hop_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "historical_import_operation_not_found")]
    [InlineData("/api/historical-import/v1/imports/hop_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/result", "historical_import_operation_not_found")]
    [InlineData("/api/historical-import/v1/observations/hob_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "historical_import_observation_not_found")]
    public async Task UnknownReadTargets_ReturnFixedNoStoreNotFound(string path, string errorCode)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());

        using var response = await host.Client.GetAsync(path);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, errorCode);
    }

    [Theory]
    [InlineData("/api/historical-import/v1/previews/hip_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?unexpected=1")]
    [InlineData("/api/historical-import/v1/imports/hop_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?unexpected=1")]
    [InlineData("/api/historical-import/v1/imports/hop_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/result?unexpected=1")]
    [InlineData("/api/historical-import/v1/observations/hob_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?unexpected=1")]
    public async Task IdentifierReads_RejectUnknownQueryBeforeInvokingApplication(string path)
    {
        using var temp = new MonitorTempDirectory();
        var application = new ThrowingHistoricalImportApplication(
            new InvalidOperationException("application must not be invoked"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var response = await host.Client.GetAsync(path);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, HistoricalImportErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task CollectionReadsUseTheContractDefaultLimitOfOneHundred()
    {
        using var temp = new MonitorTempDirectory();
        var application = new CapturingHistoricalImportApplication();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var history = await host.Client.GetAsync("/api/historical-import/v1/history");
        using var observations = await host.Client.GetAsync("/api/historical-import/v1/observations");

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.OK, observations.StatusCode);
        Assert.Equal(100, application.HistoryLimit);
        Assert.Equal(100, application.ObservationLimit);
        Assert.Null(application.ObservationCursor);
    }

    [Theory]
    [InlineData("/api/historical-import/v1/history?limit=")]
    [InlineData("/api/historical-import/v1/history?limit=0")]
    [InlineData("/api/historical-import/v1/history?limit=101")]
    [InlineData("/api/historical-import/v1/history?limit=1.0")]
    [InlineData("/api/historical-import/v1/observations?cursor=")]
    public async Task CollectionReadsRejectPresentButInvalidQueryValues(string path)
    {
        using var temp = new MonitorTempDirectory();
        var application = new ThrowingHistoricalImportApplication(
            new InvalidOperationException("application must not be invoked"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var response = await host.Client.GetAsync(path);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, HistoricalImportErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task ConsumedConfirmation_ReturnsOnlyFixedConflictAndValidatedOperationLocation()
    {
        using var temp = new MonitorTempDirectory();
        var operationId = "hop_" + new string('b', 32);
        var application = new ThrowingHistoricalImportApplication(
            new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationConsumed, operationId));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));
        using var request = Post("/api/historical-import/v1/confirmations", """
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-confirmation-request/v1","preview_id":"hip_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","preview_digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","snapshot_version":"hsv_1","decision":"confirm"}
            """);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"/api/historical-import/v1/imports/{operationId}", response.Headers.Location?.OriginalString);
        Assert.Equal("{\"error\":\"historical_import_confirmation_consumed\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HistoricalImportErrorCodes.ResultNotAvailable, HttpStatusCode.Conflict)]
    [InlineData(HistoricalImportErrorCodes.SourceChanged, HttpStatusCode.Conflict)]
    [InlineData(HistoricalImportErrorCodes.TransactionFailed, HttpStatusCode.ServiceUnavailable)]
    public async Task CreatedOperationFailureReturnsItsValidatedStatusLocation(string errorCode, HttpStatusCode status)
    {
        using var temp = new MonitorTempDirectory();
        var operationId = "hop_" + new string('c', 32);
        var application = new ThrowingHistoricalImportApplication(
            new HistoricalImportException(errorCode, operationId));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));
        using var request = Post("/api/historical-import/v1/imports", """
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-request/v1","request_id":"hir_22222222222222222222222222222222","idempotency_key":"hik_22222222222222222222222222222222","confirmation_id":"hic_22222222222222222222222222222222","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1"}
            """);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal($"/api/historical-import/v1/imports/{operationId}", response.Headers.Location?.OriginalString);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(errorCode, json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SuccessfulCommit_ReturnsResultAndOperationStatusLocation()
    {
        using var temp = new MonitorTempDirectory();
        var application = new SuccessfulCommitHistoricalImportApplication();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));
        using var request = Post("/api/historical-import/v1/imports", """
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-request/v1","request_id":"hir_22222222222222222222222222222222","idempotency_key":"hik_22222222222222222222222222222222","confirmation_id":"hic_22222222222222222222222222222222","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1"}
            """);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "/api/historical-import/v1/imports/hop_22222222222222222222222222222222",
            response.Headers.Location?.OriginalString);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("hop_22222222222222222222222222222222", json.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("committed", json.RootElement.GetProperty("transaction_outcome").GetString());
    }

    [Fact]
    public async Task ResultBeforeCompletion_ReturnsFixedConflict()
    {
        using var temp = new MonitorTempDirectory();
        var application = new ThrowingHistoricalImportApplication(
            new HistoricalImportException(HistoricalImportErrorCodes.ResultNotAvailable));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var response = await host.Client.GetAsync(
            "/api/historical-import/v1/imports/hop_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/result");

        await AssertErrorAsync(response, HttpStatusCode.Conflict, HistoricalImportErrorCodes.ResultNotAvailable);
    }

    [Fact]
    public async Task UnexpectedApplicationFailure_MapsToFixedNoLeakUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        var application = new ThrowingHistoricalImportApplication(
            new InvalidOperationException("C:\\Users\\person\\SECRET_HISTORY_PATH.jsonl"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var response = await host.Client.GetAsync("/api/historical-import/v1/history?limit=50");

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, HistoricalImportErrorCodes.StoreUnavailable);
    }

    [Fact]
    public async Task BadHttpRequestFailure_MapsToFixedNoLeakInvalidRequest()
    {
        using var temp = new MonitorTempDirectory();
        var application = new ThrowingHistoricalImportApplication(
            new BadHttpRequestException("C:\\Users\\person\\SECRET_HISTORY_PATH.jsonl", StatusCodes.Status400BadRequest));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions(application));

        using var response = await host.Client.GetAsync("/api/historical-import/v1/history?limit=50");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, HistoricalImportErrorCodes.RequestInvalid);
    }

    private static string Selection(
        string? exactReference,
        string sourceSurface = "github-copilot-cli",
        string referenceKind = "selected_root",
        string? sessionId = "session-1") => JsonSerializer.Serialize(new
    {
        contract_version = "historical-import-workflow/v1",
        schema_version = "historical-import-workflow-source-selection/v1",
        source_surface = sourceSurface,
        reference_kind = referenceKind,
        exact_reference = exactReference ?? NativeAbsoluteReference("synthetic-history"),
        session_id = sessionId,
        source_application_version = "1.0.71",
        requested_capture = "metadata_only",
        consent_granted = true,
    });

    private static HttpRequestMessage Post(string path, string body, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static string NativeAbsoluteReference(string leaf) => OperatingSystem.IsWindows()
        ? $"C:\\historical-import-tests\\{leaf}"
        : $"/historical-import-tests/{leaf}";

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            Assert.Equal(["error"], json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
        }
    }

    private static MonitorHostTestOptions QuietHostOptions(IHistoricalImportApplication? application = null) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        HistoricalImportApplication = application,
    };

    private sealed class ThrowingHistoricalImportApplication(Exception exception) : IHistoricalImportApplication
    {
        public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request) => throw exception;
        public HistoricalImportPreview ReadPreview(string previewId) => throw exception;
        public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request) => throw exception;
        public HistoricalImportResult Commit(HistoricalImportCommitRequest request) => throw exception;
        public HistoricalImportStatus ReadStatus(string operationId) => throw exception;
        public HistoricalImportResult ReadResult(string operationId) => throw exception;
        public HistoricalImportHistory ListHistory(int limit = 100) => throw exception;
        public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null) => throw exception;
        public HistoricalObservationDetail GetObservation(string observationId) => throw exception;
    }

    private sealed class DisposableHistoricalImportApplication : IHistoricalImportApplication, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request) => throw new NotSupportedException();
        public HistoricalImportPreview ReadPreview(string previewId) => throw new NotSupportedException();
        public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request) => throw new NotSupportedException();
        public HistoricalImportResult Commit(HistoricalImportCommitRequest request) => throw new NotSupportedException();
        public HistoricalImportStatus ReadStatus(string operationId) => throw new NotSupportedException();
        public HistoricalImportResult ReadResult(string operationId) => throw new NotSupportedException();
        public HistoricalImportHistory ListHistory(int limit = 100) => throw new NotSupportedException();
        public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null) => throw new NotSupportedException();
        public HistoricalObservationDetail GetObservation(string observationId) => throw new NotSupportedException();
    }

    private sealed class SuccessfulCommitHistoricalImportApplication : IHistoricalImportApplication
    {
        public HistoricalImportResult Commit(HistoricalImportCommitRequest request) =>
            HistoricalImportJson.Deserialize<HistoricalImportResult>("""
                {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-result/v1","operation_id":"hop_22222222222222222222222222222222","request_id":"hir_22222222222222222222222222222222","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1","outcome":"succeeded","transaction_outcome":"committed","idempotency_outcome":"first_application","source_kind":"historical","source_surface":"github-copilot-cli","source_tier":"tier_b","profile_id":"synthetic-contract-profile","adapter_id":"synthetic-contract-adapter-v1","evidence_status":"production","counts":{"total":{"availability":"available","value":1},"new_observations":{"availability":"available","value":1},"new_sessions":{"availability":"unavailable","value":null},"new_events":{"availability":"unavailable","value":null},"duplicates":{"availability":"available","value":0},"conflicts":{"availability":"available","value":0},"record_rejections":{"availability":"available","value":0}},"observations":[],"duplicates":[],"conflicts":[],"retention":{"disposition":"not_applicable","created_item_count":0,"pin_state":"not_applicable","deletion_state":"not_applicable"}}
                """);

        public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request) => throw new NotSupportedException();
        public HistoricalImportPreview ReadPreview(string previewId) => throw new NotSupportedException();
        public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request) => throw new NotSupportedException();
        public HistoricalImportStatus ReadStatus(string operationId) => throw new NotSupportedException();
        public HistoricalImportResult ReadResult(string operationId) => throw new NotSupportedException();
        public HistoricalImportHistory ListHistory(int limit = 100) => throw new NotSupportedException();
        public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null) => throw new NotSupportedException();
        public HistoricalObservationDetail GetObservation(string observationId) => throw new NotSupportedException();
    }

    private sealed class StreamingJsonContent : HttpContent
    {
        private readonly int length;
        private readonly bool declareLength;

        public StreamingJsonContent(int length, bool declareLength)
        {
            this.length = length;
            this.declareLength = declareLength;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = declareLength ? length : 0;
            return declareLength;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            var remaining = length;
            while (remaining > 0)
            {
                var count = Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }

    }

    private sealed class CapturingHistoricalImportApplication : IHistoricalImportApplication
    {
        public int? HistoryLimit { get; private set; }
        public int? ObservationLimit { get; private set; }
        public string? ObservationCursor { get; private set; }

        public HistoricalImportHistory ListHistory(int limit = 100)
        {
            HistoryLimit = limit;
            return new(HistoricalImportContractVersions.Workflow, HistoricalImportContractVersions.ImportHistory, []);
        }

        public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null)
        {
            ObservationLimit = limit;
            ObservationCursor = cursor;
            return new(HistoricalImportContractVersions.Workflow, HistoricalImportContractVersions.ObservationList, "historical", [], null);
        }

        public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request) => throw new NotSupportedException();
        public HistoricalImportPreview ReadPreview(string previewId) => throw new NotSupportedException();
        public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request) => throw new NotSupportedException();
        public HistoricalImportResult Commit(HistoricalImportCommitRequest request) => throw new NotSupportedException();
        public HistoricalImportStatus ReadStatus(string operationId) => throw new NotSupportedException();
        public HistoricalImportResult ReadResult(string operationId) => throw new NotSupportedException();
        public HistoricalObservationDetail GetObservation(string observationId) => throw new NotSupportedException();
    }
}

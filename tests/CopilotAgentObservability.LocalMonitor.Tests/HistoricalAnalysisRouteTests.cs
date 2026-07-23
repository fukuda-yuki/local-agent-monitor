using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisRouteTests
{
    [Fact]
    public async Task Preview_ValidRequest_ReturnsRepositorySafeOwnerProjectionAndStoredBinding()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietOptions());

        using var response = await host.Client.SendAsync(PreviewRequest(ValidBody()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        Assert.Equal(
        [
            "schema_version",
            "extraction_id",
            "raw_local_sha256",
            "repository_safe_sha256",
            "selection",
            "included",
            "excluded",
            "truncated_before",
            "truncated_session_count",
        ], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("historical-analysis-preview.response.v1", root.GetProperty("schema_version").GetString());
        Assert.Matches("^historical-extraction-[a-z0-9]{32}$", root.GetProperty("extraction_id").GetString());
        Assert.Matches("^[a-f0-9]{64}$", root.GetProperty("raw_local_sha256").GetString());
        Assert.Matches("^[a-f0-9]{64}$", root.GetProperty("repository_safe_sha256").GetString());
        Assert.Empty(root.GetProperty("included").EnumerateArray());
        Assert.Empty(root.GetProperty("excluded").EnumerateArray());
        Assert.Equal(
        [
            "repository",
            "workspace",
            "from",
            "to",
            "explicit_session_ids",
            "source_surfaces",
            "task_label",
            "experiment_label",
            "maximum_session_count",
            "sanitized_only",
        ], root.GetProperty("selection").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Preview_UnknownField_ReturnsFixedInvalidRequest()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietOptions());
        var body = ValidBody()[..^1] + ",\"unexpected\":true}";

        using var response = await host.Client.SendAsync(PreviewRequest(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            """{"schema_version":"historical-analysis-error.v1","error":"invalid_historical_analysis_request"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(InvalidPreviewBodies))]
    public async Task Preview_OpenOrNonCanonicalShape_ReturnsFixedInvalidRequest(string body)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietOptions());

        using var response = await host.Client.SendAsync(PreviewRequest(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            """{"schema_version":"historical-analysis-error.v1","error":"invalid_historical_analysis_request"}""",
            await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<string> InvalidPreviewBodies()
    {
        var selection = """{"repository":"repo-a","workspace":null,"from":null,"to":null,"explicit_session_ids":[],"source_surfaces":[],"task_label":null,"experiment_label":null,"maximum_session_count":50,"sanitized_only":false}""";
        return new()
        {
            "{\"selection\":" + selection + ",\"schema_version\":\"historical-analysis-preview.request.v1\"}",
            "{\"schema_version\":\"historical-analysis-preview.request.v1\",\"schema_version\":\"historical-analysis-preview.request.v1\",\"selection\":" + selection + "}",
            """{"schema_version":"historical-analysis-preview.request.v1","selection":{"repository":"repo-a","workspace":null,"from":null,"to":null,"explicit_session_ids":null,"source_surfaces":[],"task_label":null,"experiment_label":null,"maximum_session_count":50,"sanitized_only":false}}""",
            """{"schema_version":"historical-analysis-preview.request.v1","selection":{"repository":null,"workspace":null,"from":null,"to":null,"explicit_session_ids":["018F0000-0000-7000-8000-000000000001"],"source_surfaces":[],"task_label":null,"experiment_label":null,"maximum_session_count":50,"sanitized_only":false}}""",
        };
    }

    [Fact]
    public async Task Preview_RejectionsFollowSecurityOrderAndNeverInvokeOwner()
    {
        using var temp = new MonitorTempDirectory();
        var source = new CountingSnapshotSource();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(source));

        using var compound = PreviewRequest(ValidBody(), "/api/historical-analysis/v1/preview?unexpected=1", csrf: false);
        compound.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(await host.Client.SendAsync(compound), HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var missingCsrf = PreviewRequest(ValidBody(), csrf: false);
        await AssertError(await host.Client.SendAsync(missingCsrf), HttpStatusCode.Forbidden, "csrf_required");

        using var invalidHost = PreviewRequest(ValidBody());
        invalidHost.Headers.Host = "example.invalid";
        await AssertError(await host.Client.SendAsync(invalidHost), HttpStatusCode.BadRequest, "invalid_host");

        using var nonJson = PreviewRequest(ValidBody());
        nonJson.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        await AssertError(await host.Client.SendAsync(nonJson), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var query = PreviewRequest(ValidBody(), "/api/historical-analysis/v1/preview?unexpected=1");
        await AssertError(await host.Client.SendAsync(query), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var declared = PreviewRequest(new string('x', HistoricalAnalysisContractsV1.MaximumRequestBytes + 1));
        await AssertError(await host.Client.SendAsync(declared), HttpStatusCode.RequestEntityTooLarge, "request_too_large");

        using var streamed = new HttpRequestMessage(HttpMethod.Post, "/api/historical-analysis/v1/preview")
        {
            Content = new StreamingContent(HistoricalAnalysisContractsV1.MaximumRequestBytes + 1),
        };
        streamed.Headers.Add("x-monitor-csrf", "local-monitor");
        await AssertError(await host.Client.SendAsync(streamed), HttpStatusCode.RequestEntityTooLarge, "request_too_large");

        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task Preview_SanitizedOnlyHostRejectsRawSelectionBeforeDescriptorCapableOwnerAccess()
    {
        using var temp = new MonitorTempDirectory();
        var source = new InstructionSnapshotSource();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietOptions(source));

        using var response = await host.Client.SendAsync(PreviewRequest(ValidBody()));

        await AssertError(
            response,
            HttpStatusCode.BadRequest,
            HistoricalAnalysisErrorCodesV1.InvalidRequest);
        Assert.Equal(0, source.OpenCount);
        Assert.Equal(0, source.DescriptorReadCount);
    }

    [Fact]
    public async Task InstructionStart_ProviderFreeHostResolvesBindingBeforeUnavailableWithoutCreatingRun()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new InstructionSnapshotSource()));
        var preview = await PreviewBinding(host);

        using var absent = await host.Client.SendAsync(InstructionStartRequest(
            preview,
            transform: body => body.Replace(
                preview.ExtractionId,
                "historical-extraction-11111111111111111111111111111111",
                StringComparison.Ordinal)));
        await AssertError(absent, HttpStatusCode.NotFound, "historical_extraction_not_found");
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));

        using var stale = await host.Client.SendAsync(InstructionStartRequest(
            preview,
            transform: body => body.Replace(
                preview.RawLocalSha256,
                new string('b', 64),
                StringComparison.Ordinal)));
        await AssertError(stale, HttpStatusCode.Conflict, "stale_extraction");
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));

        using var valid = await host.Client.SendAsync(InstructionStartRequest(preview));
        await AssertError(valid, HttpStatusCode.ServiceUnavailable, "provider_unavailable");
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));
    }

    [Fact]
    public async Task InstructionStart_SanitizedOnlyHostRejectsTestProviderWithoutCreatingRun()
    {
        using var temp = new MonitorTempDirectory();
        var provider = new FailIfCalledProvider();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietOptions(new InstructionSnapshotSource(), provider));
        var preview = await PreviewBinding(host, sanitizedOnly: true);

        using var response = await host.Client.SendAsync(InstructionStartRequest(preview));

        await AssertError(response, HttpStatusCode.ServiceUnavailable, "provider_unavailable");
        Assert.Equal(0, provider.CallCount);
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));
    }

    [Fact]
    public async Task InstructionStartAndStatus_ProviderRun_ReturnExactQueuedRunningAndZeroFindingReadDto()
    {
        using var temp = new MonitorTempDirectory();
        var provider = new BlockingZeroProvider();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new InstructionSnapshotSource(), provider));
        var preview = await PreviewBinding(host);

        using var start = await host.Client.SendAsync(InstructionStartRequest(preview));

        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Equal("no-store", start.Headers.CacheControl?.ToString());
        Assert.False(start.Headers.Contains("Access-Control-Allow-Origin"));
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["schema_version", "analysis_run_id", "state"],
            startJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("historical-analysis-instruction-start.response.v1", startJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("queued", startJson.RootElement.GetProperty("state").GetString());
        var runId = long.Parse(startJson.RootElement.GetProperty("analysis_run_id").GetString()!);

        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var running = await host.Client.GetAsync($"/api/historical-analysis/v1/instruction-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        Assert.Equal("no-store", running.Headers.CacheControl?.ToString());
        Assert.False(running.Headers.Contains("Access-Control-Allow-Origin"));
        using (var runningJson = JsonDocument.Parse(await running.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(
            [
                "schema_version",
                "run_id",
                "request",
                "dataset_projection",
                "state",
                "requested_at",
                "started_at",
                "completed_at",
                "receipt",
                "handoff_bytes",
            ], runningJson.RootElement.EnumerateObject().Select(property => property.Name));
            AssertExactInstructionReadProjection(runningJson.RootElement, "running");
            Assert.Equal("", runningJson.RootElement.GetProperty("handoff_bytes").GetString());
        }

        provider.Release.TrySetResult();
        var completed = await WaitForInstructionState(host, runId, "zero_findings");
        AssertExactInstructionReadProjection(completed.RootElement, "zero_findings");
        var handoffBytes = Convert.FromBase64String(completed.RootElement.GetProperty("handoff_bytes").GetString()!);
        Assert.Equal(runId, InstructionFindingHandoffConsumerV1.Validate(handoffBytes));
        var receipt = completed.RootElement.GetProperty("receipt");
        Assert.Equal(
        [
            "schema_version",
            "run_id",
            "extraction_id",
            "extraction_sha256",
            "state",
            "model",
            "provider",
            "configuration_sha256",
            "timeout_ms",
            "prompt_template_version",
            "truncated_before",
            "sanitized_only",
            "content_available",
            "dataset_distribution",
            "handoff_sha256",
            "findings",
        ], receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal(10000, receipt.GetProperty("timeout_ms").GetInt32());
        Assert.Equal("zero_findings", receipt.GetProperty("state").GetString());
        AssertExactDistribution(receipt.GetProperty("dataset_distribution"));
        completed.Dispose();
    }

    [Theory]
    [InlineData(0, "queued")]
    [InlineData(1, "running")]
    [InlineData(2, "succeeded")]
    [InlineData(3, "zero_findings")]
    [InlineData(4, "no_eligible_sessions")]
    [InlineData(5, "content_unavailable")]
    [InlineData(6, "stale_extraction")]
    [InlineData(7, "extraction_invalid")]
    [InlineData(8, "invalid_citation")]
    [InlineData(9, "provider_partial")]
    [InlineData(10, "provider_failed")]
    [InlineData(11, "timed_out")]
    [InlineData(12, "canceled")]
    public void InstructionReadProjection_PreservesEveryOwnerStateWire(
        int stateValue,
        string expected)
    {
        var state = (HistoricalInstructionAnalysisStateV1)stateValue;
        var read = new HistoricalInstructionAnalysisReadV1(
            HistoricalInstructionAnalysisContractsV1.ReadSchemaVersion,
            1,
            new(
                HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
                "historical-extraction-11111111111111111111111111111111",
                new string('a', 64),
                "model-v1",
                "provider-v1",
                new string('b', 64),
                10000,
                HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion),
            new(false, false, true, new([], [], [])),
            state,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            []);

        Assert.Equal(expected, HistoricalAnalysisInstructionReadResponseV1.From(read).State);
    }

    [Fact]
    public async Task InstructionStatus_InvalidNestedHandoff_FailsClosed()
    {
        using var temp = new MonitorTempDirectory();
        var provider = new BlockingZeroProvider();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new InstructionSnapshotSource(), provider));
        var preview = await PreviewBinding(host);
        using var start = await host.Client.SendAsync(InstructionStartRequest(preview));
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStreamAsync());
        var runId = long.Parse(startJson.RootElement.GetProperty("analysis_run_id").GetString()!);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Release.TrySetResult();
        (await WaitForInstructionState(host, runId, "zero_findings")).Dispose();
        CorruptHandoffWithMatchingChecksums(temp.DatabasePath, runId);

        using var response = await host.Client.GetAsync($"/api/historical-analysis/v1/instruction-runs/{runId}");

        await AssertError(response, HttpStatusCode.ServiceUnavailable, "historical_analysis_store_unavailable");
    }

    [Fact]
    public async Task InstructionRoutes_RejectInvalidSecurityBodyBindingAndStatusWithoutCreatingRun()
    {
        using var temp = new MonitorTempDirectory();
        var provider = new FailIfCalledProvider();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new InstructionSnapshotSource(), provider));
        var preview = await PreviewBinding(host);

        using var compound = InstructionStartRequest(
            preview,
            "/api/historical-analysis/v1/instruction-runs?unexpected=1",
            csrf: false);
        compound.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(await host.Client.SendAsync(compound), HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var missingCsrf = InstructionStartRequest(preview, csrf: false);
        await AssertError(await host.Client.SendAsync(missingCsrf), HttpStatusCode.Forbidden, "csrf_required");

        using var invalidHost = InstructionStartRequest(preview);
        invalidHost.Headers.Host = "example.invalid";
        await AssertError(await host.Client.SendAsync(invalidHost), HttpStatusCode.BadRequest, "invalid_host");

        using var nonJson = InstructionStartRequest(preview);
        nonJson.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        await AssertError(await host.Client.SendAsync(nonJson), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var query = InstructionStartRequest(
            preview,
            "/api/historical-analysis/v1/instruction-runs?unexpected=1");
        await AssertError(await host.Client.SendAsync(query), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var unknown = InstructionStartRequest(
            preview,
            transform: body => body[..^1] + ",\"unexpected\":true}");
        await AssertError(await host.Client.SendAsync(unknown), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var duplicate = InstructionStartRequest(
            preview,
            transform: body => body[..^1] + ",\"model\":\"model-v1\"}");
        await AssertError(await host.Client.SendAsync(duplicate), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var missing = InstructionStartRequest(
            preview,
            transform: body => body.Replace("\"model\":\"model-v1\",", string.Empty, StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(missing), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var reordered = InstructionStartRequest(
            preview,
            transform: body => body.Replace(
                "\"schema_version\":\"historical-analysis-instruction-start.request.v1\",\"extraction_id\":",
                "\"extraction_id\":",
                StringComparison.Ordinal)[..^1]
                + ",\"schema_version\":\"historical-analysis-instruction-start.request.v1\"}");
        await AssertError(await host.Client.SendAsync(reordered), HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var declared = InstructionStartRequest(preview);
        declared.Content = new StringContent(
            new string('x', HistoricalAnalysisContractsV1.MaximumRequestBytes + 1),
            Encoding.UTF8,
            "application/json");
        await AssertError(await host.Client.SendAsync(declared), HttpStatusCode.RequestEntityTooLarge, "request_too_large");

        using var streamed = InstructionStartRequest(preview);
        streamed.Content = new StreamingContent(HistoricalAnalysisContractsV1.MaximumRequestBytes + 1);
        await AssertError(await host.Client.SendAsync(streamed), HttpStatusCode.RequestEntityTooLarge, "request_too_large");

        using var stale = InstructionStartRequest(
            preview,
            transform: body => body.Replace(preview.RawLocalSha256, new string('b', 64), StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(stale), HttpStatusCode.Conflict, "stale_extraction");

        using var absent = InstructionStartRequest(
            preview,
            transform: body => body.Replace(preview.ExtractionId, "historical-extraction-11111111111111111111111111111111", StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(absent), HttpStatusCode.NotFound, "historical_extraction_not_found");

        using var invalidStatus = await host.Client.GetAsync("/api/historical-analysis/v1/instruction-runs/01");
        await AssertError(invalidStatus, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var missingStatus = await host.Client.GetAsync("/api/historical-analysis/v1/instruction-runs/1");
        await AssertError(missingStatus, HttpStatusCode.NotFound, "historical_analysis_run_not_found");

        using var crossSiteStatusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/historical-analysis/v1/instruction-runs/1");
        crossSiteStatusRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(
            await host.Client.SendAsync(crossSiteStatusRequest),
            HttpStatusCode.Forbidden,
            "cross_origin_forbidden");

        Assert.Equal(0, provider.CallCount);
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));
    }

    [Theory]
    [InlineData(false, "zero_drivers")]
    [InlineData(true, "succeeded")]
    public async Task EfficiencyStartAndStatus_ReturnExactQueuedRunningAndOwnerReceipt(
        bool includeRetryDriver,
        string expectedState)
    {
        using var temp = new MonitorTempDirectory();
        var executor = new BlockingEfficiencyExecutor();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(
                new InstructionSnapshotSource(includeRetryDriver),
                historicalEfficiencyExecutor: executor));
        var preview = await PreviewBinding(host);

        using var start = await host.Client.SendAsync(EfficiencyStartRequest(preview));

        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Equal("no-store", start.Headers.CacheControl?.ToString());
        Assert.False(start.Headers.Contains("Access-Control-Allow-Origin"));
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["schema_version", "analysis_run_id", "state"],
            startJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "historical-analysis-efficiency-start.response.v1",
            startJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("queued", startJson.RootElement.GetProperty("state").GetString());
        var runId = startJson.RootElement.GetProperty("analysis_run_id").GetString()!;
        Assert.Matches("^historical-efficiency-run-[a-z0-9]{32}$", runId);
        Assert.Null(new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath).Get(1));

        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var running = await host.Client.GetAsync($"/api/historical-analysis/v1/efficiency-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        Assert.Equal("no-store", running.Headers.CacheControl?.ToString());
        Assert.False(running.Headers.Contains("Access-Control-Allow-Origin"));
        using (var runningJson = JsonDocument.Parse(await running.Content.ReadAsStreamAsync()))
        {
            AssertExactEfficiencyStatus(runningJson.RootElement, "running", receiptExpected: false);
        }

        executor.Release.TrySetResult();
        using var completed = await WaitForEfficiencyState(host, runId, expectedState);
        AssertExactEfficiencyStatus(completed.RootElement, expectedState, receiptExpected: true);
        var exact = Assert.IsType<HistoricalEfficiencyAnalysisV1>(executor.Result);
        Assert.Equal(exact.PayloadSha256, completed.RootElement.GetProperty("receipt_payload_sha256").GetString());
        Assert.Equal(
            Encoding.UTF8.GetString(exact.CanonicalBytes),
            completed.RootElement.GetProperty("receipt").GetRawText());
    }

    [Fact]
    public async Task EfficiencyStart_AbsentStaleAndStrictInvalidRequestsDoNotInvokeExecutor()
    {
        using var temp = new MonitorTempDirectory();
        var executor = new FailIfCalledEfficiencyExecutor();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(
                new InstructionSnapshotSource(),
                historicalEfficiencyExecutor: executor));
        var preview = await PreviewBinding(host);

        using var compound = EfficiencyStartRequest(
            preview,
            "/api/historical-analysis/v1/efficiency-runs?unexpected=1",
            csrf: false);
        compound.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(
            await host.Client.SendAsync(compound),
            HttpStatusCode.Forbidden,
            "cross_origin_forbidden");

        using var missingCsrf = EfficiencyStartRequest(preview, csrf: false);
        await AssertError(
            await host.Client.SendAsync(missingCsrf),
            HttpStatusCode.Forbidden,
            "csrf_required");

        using var nonJson = EfficiencyStartRequest(preview);
        nonJson.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        await AssertError(
            await host.Client.SendAsync(nonJson),
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type");

        using var absent = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            transform: body => body.Replace(
                preview.ExtractionId,
                "historical-extraction-11111111111111111111111111111111",
                StringComparison.Ordinal)));
        await AssertError(absent, HttpStatusCode.NotFound, "historical_extraction_not_found");

        using var stale = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            transform: body => body.Replace(
                preview.RepositorySafeSha256,
                new string('b', 64),
                StringComparison.Ordinal)));
        await AssertError(stale, HttpStatusCode.Conflict, "stale_extraction");

        using var unknown = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            transform: body => body[..^1] + ",\"unexpected\":true}"));
        await AssertError(unknown, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var duplicate = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            transform: body => body[..^1]
                + $$""","repository_safe_sha256":"{{preview.RepositorySafeSha256}}"}"""));
        await AssertError(duplicate, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var reordered = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            transform: body => body.Replace(
                "\"schema_version\":\"historical-analysis-efficiency-start.request.v1\",",
                string.Empty,
                StringComparison.Ordinal)[..^1]
                + ",\"schema_version\":\"historical-analysis-efficiency-start.request.v1\"}"));
        await AssertError(reordered, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var query = await host.Client.SendAsync(EfficiencyStartRequest(
            preview,
            "/api/historical-analysis/v1/efficiency-runs?unexpected=1"));
        await AssertError(query, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var declared = EfficiencyStartRequest(preview);
        declared.Content = new StringContent(
            new string('x', HistoricalAnalysisContractsV1.MaximumRequestBytes + 1),
            Encoding.UTF8,
            "application/json");
        await AssertError(
            await host.Client.SendAsync(declared),
            HttpStatusCode.RequestEntityTooLarge,
            "request_too_large");

        using var streamed = EfficiencyStartRequest(preview);
        streamed.Content = new StreamingContent(HistoricalAnalysisContractsV1.MaximumRequestBytes + 1);
        await AssertError(
            await host.Client.SendAsync(streamed),
            HttpStatusCode.RequestEntityTooLarge,
            "request_too_large");

        using var invalidStatus = await host.Client.GetAsync(
            "/api/historical-analysis/v1/efficiency-runs/historical-efficiency-run-ABC");
        await AssertError(invalidStatus, HttpStatusCode.BadRequest, "invalid_historical_analysis_request");

        using var missingStatus = await host.Client.GetAsync(
            "/api/historical-analysis/v1/efficiency-runs/historical-efficiency-run-11111111111111111111111111111111");
        await AssertError(missingStatus, HttpStatusCode.NotFound, "historical_analysis_run_not_found");

        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData(EfficiencyExecutorOutcome.InvalidInput, "analysis_failed")]
    [InlineData(EfficiencyExecutorOutcome.ForgedPayloadSha256, "analysis_failed")]
    [InlineData(EfficiencyExecutorOutcome.WaitForCancellation, "timed_out")]
    [InlineData(EfficiencyExecutorOutcome.SynchronousIgnoreCancellation, "timed_out")]
    public async Task EfficiencyStatus_PreservesFailedTerminalStates(
        EfficiencyExecutorOutcome outcome,
        string expectedState)
    {
        using var temp = new MonitorTempDirectory();
        var executor = new ControllableEfficiencyExecutor(outcome);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(
                new InstructionSnapshotSource(),
                historicalEfficiencyExecutor: executor,
                historicalEfficiencyTimeout: TimeSpan.FromMilliseconds(50)));
        var preview = await PreviewBinding(host);
        using var start = await host.Client.SendAsync(EfficiencyStartRequest(preview));
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStreamAsync());
        var runId = startJson.RootElement.GetProperty("analysis_run_id").GetString()!;

        try
        {
            using var completed = await WaitForEfficiencyState(host, runId, expectedState);
            AssertExactEfficiencyStatus(completed.RootElement, expectedState, receiptExpected: false);
        }
        finally
        {
            executor.Release.Set();
        }
    }

    [Fact]
    public async Task EfficiencyStatus_ExtractionRemovedWhileRunningEndsStaleWithoutReceipt()
    {
        using var temp = new MonitorTempDirectory();
        var executor = new BlockingEfficiencyExecutor();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(
                new InstructionSnapshotSource(),
                historicalEfficiencyExecutor: executor));
        var preview = await PreviewBinding(host);
        using var start = await host.Client.SendAsync(EfficiencyStartRequest(preview));
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStreamAsync());
        var runId = startJson.RootElement.GetProperty("analysis_run_id").GetString()!;
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DeleteHistoricalExtraction(temp.DatabasePath, preview.ExtractionId);

        executor.Release.TrySetResult();
        using var completed = await WaitForEfficiencyState(host, runId, "stale_extraction");

        AssertExactEfficiencyStatus(completed.RootElement, "stale_extraction", receiptExpected: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceResolve_PairedIndexesPreserveOrderExactNavigationAndRepositorySafeBody(
        bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        var source = new ResolutionSnapshotSource();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: QuietOptions(source));
        var preview = await PreviewBinding(host, sanitizedOnly);
        var sourceReadsAfterPreview = source.ReadCount;
        var references = new[]
        {
            ResolutionSnapshotSource.AvailableTraceReference,
            ResolutionSnapshotSource.AvailableSpanReference,
            ResolutionSnapshotSource.AvailableSessionReference,
        };

        using var response = await host.Client.SendAsync(EvidenceResolveRequest(preview, references));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ResolutionSnapshotSource.SensitiveMarker, body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["schema_version", "resolutions"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "historical-analysis-evidence-resolve.response.v1",
            json.RootElement.GetProperty("schema_version").GetString());
        var resolutions = json.RootElement.GetProperty("resolutions").EnumerateArray().ToArray();
        Assert.Equal(references, resolutions.Select(item => item.GetProperty("reference").GetString()));
        AssertResolution(resolutions[0], "resolved", "not_applicable", "/traces/trace%2Bone");
        AssertResolution(resolutions[1], "resolved", "not_applicable", "/traces/trace%2Bone?span=span%2Bone");
        AssertResolution(
            resolutions[2],
            "resolved",
            "available",
            $"/diagnostics?session_id={ResolutionSnapshotSource.AvailableSessionId:D}");
        Assert.Equal(1, source.OpenCount);
        Assert.Equal(sourceReadsAfterPreview, source.ReadCount);
    }

    [Fact]
    public async Task EvidenceResolve_MissingAmbiguousAndExpiredReferencesRemainDistinctAndOrdered()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new ResolutionSnapshotSource()));
        var preview = await PreviewBinding(host);
        var references = new[]
        {
            ResolutionSnapshotSource.ExpiredSessionReference,
            ResolutionSnapshotSource.MissingTraceReference,
            ResolutionSnapshotSource.AmbiguousSpanReference,
        };

        using var response = await host.Client.SendAsync(EvidenceResolveRequest(preview, references));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var resolutions = json.RootElement.GetProperty("resolutions").EnumerateArray().ToArray();
        Assert.Equal(references, resolutions.Select(item => item.GetProperty("reference").GetString()));
        AssertResolution(
            resolutions[0],
            "expired",
            "expired_pending_deletion",
            $"/diagnostics?session_id={ResolutionSnapshotSource.ExpiredSessionId:D}");
        AssertResolution(resolutions[1], "missing", "not_applicable", null);
        AssertResolution(resolutions[2], "unresolved", "not_applicable", null);
    }

    [Fact]
    public async Task EvidenceResolve_RejectsEmptyDuplicateOversizedSingularAndOpenBodies()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new ResolutionSnapshotSource()));
        var preview = await PreviewBinding(host);
        var valid = ResolutionSnapshotSource.AvailableTraceReference;
        var seventeen = Enumerable.Range(0, 17)
            .Select(index => InstructionFindingReferenceTokenizationV1.TokenizeTrace($"missing-{index}"))
            .ToArray();
        var bodies = new[]
        {
            EvidenceResolveBody(preview, []),
            EvidenceResolveBody(preview, [valid, valid]),
            EvidenceResolveBody(preview, seventeen),
            $$"""{"schema_version":"historical-analysis-evidence-resolve.request.v1","extraction_id":"{{preview.ExtractionId}}","repository_safe_sha256":"{{preview.RepositorySafeSha256}}","reference":"{{valid}}"}""",
            $$"""{"extraction_id":"{{preview.ExtractionId}}","schema_version":"historical-analysis-evidence-resolve.request.v1","repository_safe_sha256":"{{preview.RepositorySafeSha256}}","references":["{{valid}}"]}""",
            EvidenceResolveBody(preview, [valid])[..^1] + ",\"unexpected\":true}",
            EvidenceResolveBody(preview, ["trace-ref-0000000000000000000000000000000g"]),
        };

        foreach (var body in bodies)
        {
            using var response = await host.Client.SendAsync(EvidenceResolveRequest(body));
            await AssertError(
                response,
                HttpStatusCode.BadRequest,
                HistoricalAnalysisErrorCodesV1.InvalidRequest);
        }
    }

    [Fact]
    public async Task EvidenceResolve_AbsentAndChecksumChangedExtractionRemainDistinct()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new ResolutionSnapshotSource()));
        var preview = await PreviewBinding(host);

        using var absent = await host.Client.SendAsync(EvidenceResolveRequest(
            preview with
            {
                ExtractionId = "historical-extraction-11111111111111111111111111111111",
            },
            [ResolutionSnapshotSource.AvailableTraceReference]));
        await AssertError(absent, HttpStatusCode.NotFound, HistoricalAnalysisErrorCodesV1.ExtractionNotFound);

        using var stale = await host.Client.SendAsync(EvidenceResolveRequest(
            preview with { RepositorySafeSha256 = new string('b', 64) },
            [ResolutionSnapshotSource.AvailableTraceReference]));
        await AssertError(stale, HttpStatusCode.Conflict, HistoricalAnalysisErrorCodesV1.StaleExtraction);
    }

    [Fact]
    public async Task EvidenceResolve_RejectionsFollowSecurityBodyAndQueryGuards()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: QuietOptions(new ResolutionSnapshotSource()));
        var preview = await PreviewBinding(host);
        var body = EvidenceResolveBody(preview, [ResolutionSnapshotSource.AvailableTraceReference]);

        using var compound = EvidenceResolveRequest(
            body,
            "/api/historical-analysis/v1/evidence/resolve?unexpected=1",
            csrf: false);
        compound.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(
            await host.Client.SendAsync(compound),
            HttpStatusCode.Forbidden,
            "cross_origin_forbidden");

        using var missingCsrf = EvidenceResolveRequest(body, csrf: false);
        await AssertError(
            await host.Client.SendAsync(missingCsrf),
            HttpStatusCode.Forbidden,
            "csrf_required");

        using var invalidHost = EvidenceResolveRequest(body);
        invalidHost.Headers.Host = "example.invalid";
        await AssertError(
            await host.Client.SendAsync(invalidHost),
            HttpStatusCode.BadRequest,
            "invalid_host");

        using var nonJson = EvidenceResolveRequest(body);
        nonJson.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        await AssertError(
            await host.Client.SendAsync(nonJson),
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type");

        using var query = EvidenceResolveRequest(
            body,
            "/api/historical-analysis/v1/evidence/resolve?unexpected=1");
        await AssertError(
            await host.Client.SendAsync(query),
            HttpStatusCode.BadRequest,
            HistoricalAnalysisErrorCodesV1.InvalidRequest);

        using var declared = EvidenceResolveRequest(
            new string('x', HistoricalAnalysisContractsV1.MaximumRequestBytes + 1));
        await AssertError(
            await host.Client.SendAsync(declared),
            HttpStatusCode.RequestEntityTooLarge,
            "request_too_large");

        using var streamed = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/historical-analysis/v1/evidence/resolve")
        {
            Content = new StreamingContent(HistoricalAnalysisContractsV1.MaximumRequestBytes + 1),
        };
        streamed.Headers.Add("x-monitor-csrf", "local-monitor");
        await AssertError(
            await host.Client.SendAsync(streamed),
            HttpStatusCode.RequestEntityTooLarge,
            "request_too_large");
    }

    private static HttpRequestMessage PreviewRequest(
        string body,
        string path = "/api/historical-analysis/v1/preview",
        bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static HttpRequestMessage EvidenceResolveRequest(
        (string ExtractionId, string RawLocalSha256, string RepositorySafeSha256) preview,
        IReadOnlyList<string> references) =>
        EvidenceResolveRequest(EvidenceResolveBody(preview, references));

    private static HttpRequestMessage EvidenceResolveRequest(
        string body,
        string path = "/api/historical-analysis/v1/evidence/resolve",
        bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static string EvidenceResolveBody(
        (string ExtractionId, string RawLocalSha256, string RepositorySafeSha256) preview,
        IReadOnlyList<string> references) =>
        $$"""{"schema_version":"historical-analysis-evidence-resolve.request.v1","extraction_id":"{{preview.ExtractionId}}","repository_safe_sha256":"{{preview.RepositorySafeSha256}}","references":{{JsonSerializer.Serialize(references)}}}""";

    private static void AssertResolution(
        JsonElement resolution,
        string expectedResolutionState,
        string expectedContentState,
        string? expectedTarget)
    {
        Assert.Equal(
            ["reference", "resolution_state", "content_state", "target"],
            resolution.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedResolutionState, resolution.GetProperty("resolution_state").GetString());
        Assert.Equal(expectedContentState, resolution.GetProperty("content_state").GetString());
        if (expectedTarget is null)
            Assert.Equal(JsonValueKind.Null, resolution.GetProperty("target").ValueKind);
        else
            Assert.Equal(expectedTarget, resolution.GetProperty("target").GetString());
    }

    private static string ValidBody(bool sanitizedOnly = false)
    {
        const string body =
            """
            {"schema_version":"historical-analysis-preview.request.v1","selection":{"repository":"repo-a","workspace":null,"from":null,"to":null,"explicit_session_ids":[],"source_surfaces":[],"task_label":null,"experiment_label":null,"maximum_session_count":50,"sanitized_only":false}}
            """;
        return sanitizedOnly
            ? body.Replace(
                "\"sanitized_only\":false",
                "\"sanitized_only\":true",
                StringComparison.Ordinal)
            : body;
    }

    private static MonitorHostTestOptions QuietOptions(
        IHistoricalEvidenceSnapshotSourceV1? historicalEvidenceSource = null,
        IHistoricalInstructionAnalysisProviderV1? historicalInstructionProvider = null,
        IHistoricalEfficiencyExecutorV1? historicalEfficiencyExecutor = null,
        TimeSpan? historicalEfficiencyTimeout = null) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        HistoricalEvidenceSource = historicalEvidenceSource,
        HistoricalInstructionProvider = historicalInstructionProvider,
        HistoricalEfficiencyExecutor = historicalEfficiencyExecutor,
        HistoricalEfficiencyTimeout = historicalEfficiencyTimeout,
    };

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(
            $"{{\"schema_version\":\"historical-analysis-error.v1\",\"error\":\"{code}\"}}",
            await response.Content.ReadAsStringAsync());
    }

    private sealed class CountingSnapshotSource : IHistoricalEvidenceSnapshotSourceV1
    {
        internal int OpenCount { get; private set; }

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            throw new InvalidOperationException("Rejected requests must not invoke the owner.");
        }
    }

    private static async Task<(string ExtractionId, string RawLocalSha256, string RepositorySafeSha256)> PreviewBinding(
        RunningMonitorHost host,
        bool sanitizedOnly = false)
    {
        using var response = await host.Client.SendAsync(PreviewRequest(ValidBody(sanitizedOnly)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return (
            json.RootElement.GetProperty("extraction_id").GetString()!,
            json.RootElement.GetProperty("raw_local_sha256").GetString()!,
            json.RootElement.GetProperty("repository_safe_sha256").GetString()!);
    }

    private static HttpRequestMessage InstructionStartRequest(
        (string ExtractionId, string RawLocalSha256, string RepositorySafeSha256) preview,
        string path = "/api/historical-analysis/v1/instruction-runs",
        bool csrf = true,
        Func<string, string>? transform = null)
    {
        var body =
            $$"""
            {"schema_version":"historical-analysis-instruction-start.request.v1","extraction_id":"{{preview.ExtractionId}}","raw_local_sha256":"{{preview.RawLocalSha256}}","model":"model-v1","provider":"provider-v1","configuration_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","timeout_ms":10000,"prompt_template_version":"historical-instruction-analysis.prompt.v1"}
            """;
        body = transform?.Invoke(body) ?? body;
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static HttpRequestMessage EfficiencyStartRequest(
        (string ExtractionId, string RawLocalSha256, string RepositorySafeSha256) preview,
        string path = "/api/historical-analysis/v1/efficiency-runs",
        bool csrf = true,
        Func<string, string>? transform = null)
    {
        var body =
            $$"""
            {"schema_version":"historical-analysis-efficiency-start.request.v1","extraction_id":"{{preview.ExtractionId}}","repository_safe_sha256":"{{preview.RepositorySafeSha256}}"}
            """;
        body = transform?.Invoke(body) ?? body;
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static async Task<JsonDocument> WaitForInstructionState(
        RunningMonitorHost host,
        long runId,
        string expectedState)
    {
        var deadline = Stopwatch.StartNew();
        string? lastState = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var response = await host.Client.GetAsync($"/api/historical-analysis/v1/instruction-runs/{runId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            lastState = json.RootElement.GetProperty("state").GetString();
            if (lastState == expectedState) return json;
            json.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        throw new Xunit.Sdk.XunitException(
            $"Instruction run {runId} did not reach {expectedState} within 5 seconds; last state was {lastState ?? "<none>"}.");
    }

    private static async Task<JsonDocument> WaitForEfficiencyState(
        RunningMonitorHost host,
        string runId,
        string expectedState)
    {
        var deadline = Stopwatch.StartNew();
        string? lastState = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var response = await host.Client.GetAsync($"/api/historical-analysis/v1/efficiency-runs/{runId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            lastState = json.RootElement.GetProperty("state").GetString();
            if (lastState == expectedState) return json;
            json.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        throw new Xunit.Sdk.XunitException(
            $"Efficiency run {runId} did not reach {expectedState} within 5 seconds; last state was {lastState ?? "<none>"}.");
    }

    private static void AssertExactEfficiencyStatus(
        JsonElement root,
        string expectedState,
        bool receiptExpected)
    {
        Assert.Equal(
        [
            "schema_version",
            "analysis_run_id",
            "extraction_id",
            "repository_safe_sha256",
            "state",
            "requested_at",
            "started_at",
            "completed_at",
            "receipt",
            "receipt_payload_sha256",
        ], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("historical-analysis-efficiency-status.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(expectedState, root.GetProperty("state").GetString());
        Assert.Equal(receiptExpected, root.GetProperty("receipt").ValueKind != JsonValueKind.Null);
        Assert.Equal(receiptExpected, root.GetProperty("receipt_payload_sha256").ValueKind != JsonValueKind.Null);
    }

    private static void AssertExactInstructionReadProjection(JsonElement root, string expectedState)
    {
        var request = root.GetProperty("request");
        Assert.Equal(
        [
            "schema_version",
            "extraction_id",
            "extraction_sha256",
            "model",
            "provider",
            "configuration_sha256",
            "timeout_ms",
            "prompt_template_version",
        ], request.EnumerateObject().Select(property => property.Name));
        Assert.Equal(10000, request.GetProperty("timeout_ms").GetInt32());
        Assert.False(request.TryGetProperty("timeout_milliseconds", out _));

        var dataset = root.GetProperty("dataset_projection");
        Assert.Equal(
        [
            "truncated_before",
            "sanitized_only",
            "content_available",
            "dataset_distribution",
        ], dataset.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["completeness", "source_kinds", "capabilities"],
            dataset.GetProperty("dataset_distribution").EnumerateObject().Select(property => property.Name));
        AssertExactDistribution(dataset.GetProperty("dataset_distribution"));
        Assert.Equal(expectedState, root.GetProperty("state").GetString());
    }

    private static void AssertExactDistribution(JsonElement distribution)
    {
        foreach (var property in distribution.EnumerateObject())
        {
            foreach (var count in property.Value.EnumerateArray())
            {
                Assert.Equal(
                    ["key", "count"],
                    count.EnumerateObject().Select(value => value.Name));
            }
        }
    }

    private static void CorruptHandoffWithMatchingChecksums(string databasePath, long runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT receipt_json FROM historical_instruction_analysis_runs WHERE run_id=$id;";
        read.Parameters.AddWithValue("$id", runId);
        var receipt = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        var invalidHandoff = Encoding.UTF8.GetBytes("{}");
        var handoffSha = Convert.ToHexString(SHA256.HashData(invalidHandoff)).ToLowerInvariant();
        receipt["handoff_sha256"] = handoffSha;
        var receiptBytes = Encoding.UTF8.GetBytes(receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var receiptSha = Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant();
        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE historical_instruction_analysis_runs
            SET receipt_json=$receipt,receipt_sha256=$receipt_sha,handoff_json='{}',handoff_sha256=$handoff_sha
            WHERE run_id=$id;
            """;
        update.Parameters.AddWithValue("$receipt", Encoding.UTF8.GetString(receiptBytes));
        update.Parameters.AddWithValue("$receipt_sha", receiptSha);
        update.Parameters.AddWithValue("$handoff_sha", handoffSha);
        update.Parameters.AddWithValue("$id", runId);
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    private static void DeleteHistoricalExtraction(string databasePath, string extractionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM historical_evidence_datasets WHERE extraction_id=$id;";
        command.Parameters.AddWithValue("$id", extractionId);
        Assert.Equal(2, command.ExecuteNonQuery());
    }

    private sealed class BlockingZeroProvider : IHistoricalInstructionAnalysisProviderV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HistoricalInstructionProviderCompletionV1.Complete, "trace-1", []);
        }
    }

    private sealed class FailIfCalledProvider : IHistoricalInstructionAnalysisProviderV1
    {
        internal int CallCount { get; private set; }

        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Rejected start must not invoke the provider.");
        }
    }

    private sealed class BlockingEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HistoricalEfficiencyAnalysisV1? Result { get; private set; }

        public async Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Result = HistoricalEfficiencyAnalyzerV1.Analyze(extraction);
            return Result;
        }
    }

    private sealed class FailIfCalledEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        internal int CallCount { get; private set; }

        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Rejected efficiency start must not invoke the executor.");
        }
    }

    public enum EfficiencyExecutorOutcome
    {
        InvalidInput,
        ForgedPayloadSha256,
        WaitForCancellation,
        SynchronousIgnoreCancellation,
    }

    private sealed class ControllableEfficiencyExecutor(EfficiencyExecutorOutcome outcome)
        : IHistoricalEfficiencyExecutorV1
    {
        internal ManualResetEventSlim Release { get; } = new(initialState: false);

        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            if (outcome == EfficiencyExecutorOutcome.InvalidInput)
            {
                return Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction with
                {
                    RepositorySafeBytes = Encoding.UTF8.GetBytes("{}"),
                }));
            }
            if (outcome == EfficiencyExecutorOutcome.ForgedPayloadSha256)
            {
                return Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction) with
                {
                    PayloadSha256 = new string('f', 64),
                });
            }
            if (outcome == EfficiencyExecutorOutcome.SynchronousIgnoreCancellation)
            {
                Release.Wait();
                return Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction));
            }
            return WaitForCancellation(cancellationToken);
        }

        private static async Task<HistoricalEfficiencyAnalysisV1> WaitForCancellation(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class InstructionSnapshotSource(bool includeRetryDriver = false)
        : IHistoricalEvidenceSnapshotSourceV1
    {
        private static readonly Guid SessionId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        internal int OpenCount { get; private set; }
        internal int DescriptorReadCount { get; private set; }

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(
                new Lease(this, includeRetryDriver));
        }

        private sealed class Lease(
            InstructionSnapshotSource owner,
            bool includeRetryDriver) : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-instruction-v1";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions =>
            [
                new(
                    SessionId,
                    SessionSourceSurface.CopilotSdk,
                    "1.0.0",
                    "adapter.v1",
                    SessionCompleteness.Full,
                    [],
                    HistoricalEvidenceSourceKindV1.LiveOtel,
                    SessionContentState.Available,
                    "repo-a",
                    "workspace-a",
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 1, 0, 1, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 1, 0, 1, 30, TimeSpan.Zero),
                    new HistoricalSessionCapabilitiesV1(
                        true, false, false, false, includeRetryDriver, false, false, false, false, false, false, false),
                    includeRetryDriver
                        ?
                        [
                            new(SessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor),
                            new(SessionId, "trace-1", "span-2", 2, HistoricalEvidenceRelativePositionV1.Following),
                        ]
                        : [new(SessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                    []),
            ];
            public long OmittedEarlierMatchingSessionCount => 0;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken)
            {
                if (includeDescriptors) owner.DescriptorReadCount++;
                List<HistoricalEvidenceGroupDraftV1> groups =
                [
                    new(
                        HistoricalEvidenceGroupKindV1.TurnRollup,
                        [new(SessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                        1,
                        "turn",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ];
                if (includeRetryDriver)
                {
                    groups.Add(new(
                        HistoricalEvidenceGroupKindV1.RetryChain,
                        [
                            new(SessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor),
                            new(SessionId, "trace-1", "span-2", 2, HistoricalEvidenceRelativePositionV1.Following),
                        ],
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null));
                }
                return ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(groups);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ResolutionSnapshotSource : IHistoricalEvidenceSnapshotSourceV1
    {
        internal const string SensitiveMarker = "private-workspace-path-marker";
        internal static readonly Guid AvailableSessionId =
            Guid.Parse("018f0000-0000-7000-8000-000000000011");
        internal static readonly Guid ExpiredSessionId =
            Guid.Parse("018f0000-0000-7000-8000-000000000012");
        private static readonly Guid AmbiguousSessionId =
            Guid.Parse("018f0000-0000-7000-8000-000000000013");
        internal static readonly string AvailableSessionReference = SafeReference(
            AvailableSessionId,
            "trace+one",
            "span+one").SessionId!;
        internal static readonly string AvailableTraceReference =
            InstructionFindingReferenceTokenizationV1.TokenizeTrace("trace+one");
        internal static readonly string AvailableSpanReference = SafeReference(
            AvailableSessionId,
            "trace+one",
            "span+one").SpanId!;
        internal static readonly string ExpiredSessionReference = SafeReference(
            ExpiredSessionId,
            "trace-two",
            "shared-span").SessionId!;
        internal static readonly string AmbiguousSpanReference = SafeReference(
            ExpiredSessionId,
            "trace-two",
            "shared-span").SpanId!;
        internal static readonly string MissingTraceReference =
            InstructionFindingReferenceTokenizationV1.TokenizeTrace("not-in-extraction");
        internal int OpenCount { get; private set; }
        internal int ReadCount { get; private set; }

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(this));
        }

        private static InstructionEvidenceReferenceV1 SafeReference(
            Guid sessionId,
            string traceId,
            string spanId) =>
            InstructionFindingReferenceTokenizationV1.Tokenize(new(
                sessionId.ToString("D"),
                traceId,
                spanId,
                1,
                InstructionEvidenceRelativePositionV1.Anchor));

        private sealed class Lease(ResolutionSnapshotSource owner) : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-resolution-v1";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions =>
            [
                Metadata(AvailableSessionId, "trace+one", "span+one", SessionContentState.Available),
                Metadata(ExpiredSessionId, "trace-two", "shared-span", SessionContentState.ExpiredPendingDeletion),
                Metadata(AmbiguousSessionId, "trace-three", "shared-span", SessionContentState.NotCaptured),
            ];
            public long OmittedEarlierMatchingSessionCount => 0;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken)
            {
                owner.ReadCount++;
                var (traceId, spanId) = sessionId == AvailableSessionId
                    ? ("trace+one", "span+one")
                    : sessionId == ExpiredSessionId
                        ? ("trace-two", "shared-span")
                        : ("trace-three", "shared-span");
                return ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(
                [
                    new(
                        HistoricalEvidenceGroupKindV1.TurnRollup,
                        [new(sessionId, traceId, spanId, 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                        1,
                        "turn",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private static HistoricalSessionMetadataV1 Metadata(
                Guid sessionId,
                string traceId,
                string spanId,
                SessionContentState contentState) =>
                new(
                    sessionId,
                    SessionSourceSurface.CopilotSdk,
                    SensitiveMarker,
                    SensitiveMarker,
                    SessionCompleteness.Full,
                    [],
                    HistoricalEvidenceSourceKindV1.LiveOtel,
                    contentState,
                    "repo-a",
                    SensitiveMarker,
                    SensitiveMarker,
                    SensitiveMarker,
                    new DateTimeOffset(2026, 7, 1, 0, 1, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 1, 0, 1, 30, TimeSpan.Zero),
                    new HistoricalSessionCapabilitiesV1(
                        true, false, false, false, false, false, false, false, false, false, false, false),
                    [new(sessionId, traceId, spanId, 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                    []);
        }
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly int length;

        internal StreamingContent(int length)
        {
            this.length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var remaining = length;
            var buffer = new byte[8192];
            while (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }
    }
}

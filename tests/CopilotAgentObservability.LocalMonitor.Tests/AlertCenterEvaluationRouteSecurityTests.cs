using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterEvaluationRouteSecurityTests
{
    [Fact]
    public async Task EvaluationRoute_ReturnsOnlyFrozenOutcomeAndInvokesExactCoordinatorOnce()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var coordinator = new FixtureCoordinator(new(
            AlertCenterEvaluationStatus.Success,
            new AlertCenterEvaluationResponse(
                AlertCenterContractVersions.EvaluationResult,
                new string('a', 64),
                [],
                [new("high-tool-failure-ratio", "1", "missing_required_capability", ["tool-call-status"])],
                [])));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(coordinator));

        using var response = await host.Client.SendAsync(Request(sessionId, "exact-trace"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(AlertCenterContractVersions.EvaluationResult, json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(new string('a', 64), json.RootElement.GetProperty("evaluation_id").GetString());
        Assert.Empty(json.RootElement.GetProperty("receipt_ids").EnumerateArray());
        var suppression = Assert.Single(json.RootElement.GetProperty("suppressions").EnumerateArray());
        Assert.Equal("missing_required_capability", suppression.GetProperty("code").GetString());
        Assert.Equal([(sessionId, "exact-trace")], coordinator.Calls);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("snapshot", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argument", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SessionNotFound", HttpStatusCode.NotFound, "alert_center_session_not_found")]
    [InlineData("TraceNotFound", HttpStatusCode.NotFound, "alert_center_trace_not_found")]
    [InlineData("TraceNotOwned", HttpStatusCode.NotFound, "alert_center_trace_not_owned")]
    [InlineData("SourcePartitionMissing", HttpStatusCode.Conflict, "alert_center_source_partition_missing")]
    [InlineData("SourcePartitionAmbiguous", HttpStatusCode.Conflict, "alert_center_source_partition_ambiguous")]
    [InlineData("TraceIncomplete", HttpStatusCode.Conflict, "alert_center_trace_incomplete")]
    [InlineData("StoreConflict", HttpStatusCode.Conflict, "alert_center_store_conflict")]
    [InlineData("ContractRejected", HttpStatusCode.Conflict, "alert_center_contract_rejected")]
    [InlineData("StoreBusy", HttpStatusCode.ServiceUnavailable, "alert_center_store_busy")]
    [InlineData("StoreUnavailable", HttpStatusCode.ServiceUnavailable, "alert_center_store_unavailable")]
    public async Task EvaluationRoute_MapsClosedCoordinatorStatuses(
        string statusName,
        HttpStatusCode expectedStatus,
        string code)
    {
        using var temp = NewTemp();
        var coordinator = new FixtureCoordinator(new(Enum.Parse<AlertCenterEvaluationStatus>(statusName)));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(coordinator));

        using var response = await host.Client.SendAsync(Request(Guid.CreateVersion7(), "exact-trace"));

        await AssertError(response, expectedStatus, code);
    }

    [Fact]
    public async Task EvaluationRoute_EnforcesOriginCsrfMediaTypeQueryBodyAndHostBeforeCoordinator()
    {
        using var temp = NewTemp();
        var coordinator = new FixtureCoordinator(new(AlertCenterEvaluationStatus.StoreUnavailable));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(coordinator));
        var sessionId = Guid.CreateVersion7();

        using var crossSite = Request(sessionId, "exact-trace", csrf: false);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteResponse = await host.Client.SendAsync(crossSite);
        await AssertError(crossSiteResponse, HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var noCsrfResponse = await host.Client.SendAsync(Request(sessionId, "exact-trace", csrf: false));
        await AssertError(noCsrfResponse, HttpStatusCode.Forbidden, "csrf_required");

        using var mediaType = Request(sessionId, "exact-trace");
        mediaType.Content!.Headers.ContentType = new("text/plain");
        using var mediaTypeResponse = await host.Client.SendAsync(mediaType);
        await AssertError(mediaTypeResponse, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var query = Request(sessionId, "exact-trace", path: "/api/alert-center/v1/evaluations?unexpected=1");
        using var queryResponse = await host.Client.SendAsync(query);
        await AssertError(queryResponse, HttpStatusCode.BadRequest, "alert_center_invalid_request");

        using var oversized = Request(sessionId, new string('t', 5_000));
        using var oversizedResponse = await host.Client.SendAsync(oversized);
        await AssertError(oversizedResponse, HttpStatusCode.RequestEntityTooLarge, "request_too_large");

        using var invalidHost = Request(sessionId, "exact-trace");
        invalidHost.Headers.Host = "example.invalid";
        using var invalidHostResponse = await host.Client.SendAsync(invalidHost);
        await AssertError(invalidHostResponse, HttpStatusCode.BadRequest, "invalid_host");

        Assert.Empty(coordinator.Calls);
    }

    [Fact]
    public async Task AlertCenterPage_IsNoStoreAndRejectsCrossSiteBeforeRenderingControls()
    {
        using var temp = NewTemp();
        var coordinator = new FixtureCoordinator(new(AlertCenterEvaluationStatus.StoreUnavailable));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(coordinator));

        using var sameOrigin = await host.Client.GetAsync("/alerts");
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
        Assert.Equal("no-store", sameOrigin.Headers.CacheControl?.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/alerts");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(request);

        await AssertError(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");
        Assert.DoesNotContain("alert-filters", await crossSite.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/alerts");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        await AssertError(invalidHost, HttpStatusCode.BadRequest, "invalid_host");
        Assert.Empty(coordinator.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task EvaluationRoute_RejectsMalformedNonCanonicalAndOpenSchemaBodies(string body)
    {
        using var temp = NewTemp();
        var coordinator = new FixtureCoordinator(new(AlertCenterEvaluationStatus.StoreUnavailable));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(coordinator));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/alert-center/v1/evaluations")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        await AssertError(response, HttpStatusCode.BadRequest, "alert_center_invalid_request");
        Assert.Empty(coordinator.Calls);
    }

    public static TheoryData<string> InvalidBodies()
    {
        var v4 = Guid.NewGuid().ToString("D");
        var v7 = Guid.CreateVersion7().ToString("D");
        var nonRfcVariant = v7[..19] + "0" + v7[20..];
        return new()
        {
            "",
            "null",
            "[]",
            "{",
            $$"""{"schema_version":"wrong","session_id":"{{v7}}","trace_id":"trace"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v4}}","trace_id":"trace"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v7.ToUpperInvariant()}}","trace_id":"trace"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{nonRfcVariant}}","trace_id":"trace"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v7}}","trace_id":"trace/path"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v7}}","trace_id":"trace","unexpected":true}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v7}}","session_id":"{{v7}}","trace_id":"trace"}""",
            $$"""{"schema_version":"alert.center.evaluation-request.v1","session_id":"{{v7}}","trace_id":1}""",
        };
    }

    private static MonitorHostTestOptions Options(IAlertCenterEvaluationCoordinator coordinator) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        AlertCenterEvaluationCoordinator = coordinator,
    };

    private static HttpRequestMessage Request(
        Guid sessionId,
        string traceId,
        bool csrf = true,
        string path = "/api/alert-center/v1/evaluations")
    {
        var body = JsonSerializer.Serialize(new
        {
            schema_version = AlertCenterContractVersions.EvaluationRequest,
            session_id = sessionId.ToString("D"),
            trace_id = traceId,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(
            $"{{\"schema_version\":\"{AlertCenterContractVersions.Center}\",\"error\":\"{code}\"}}",
            await response.Content.ReadAsStringAsync());
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private sealed class FixtureCoordinator(AlertCenterEvaluationResult result) : IAlertCenterEvaluationCoordinator
    {
        internal List<(Guid SessionId, string TraceId)> Calls { get; } = [];

        public AlertCenterEvaluationResult Evaluate(Guid sessionId, string traceId)
        {
            Calls.Add((sessionId, traceId));
            return result;
        }
    }
}

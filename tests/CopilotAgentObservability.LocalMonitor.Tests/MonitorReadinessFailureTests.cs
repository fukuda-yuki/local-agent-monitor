using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// HTTP-level proof that <c>/health/ready</c> maps the readiness contract to status
/// codes under saturation: momentary backpressure / sub-threshold lag stay 200
/// (degraded), while sustained stall, projection-lag-exceeded, and unknown
/// projection status are 503 (not_ready) with the pinned reason tokens.
/// </summary>
public class MonitorReadinessFailureTests
{
    [Fact]
    public async Task Ready_QueueFull_IsMomentary200_ThenSustained503()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = MonitorTestHealth.Ready(time);
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(SyntheticRecord(), out _));
        await using var host = await StartHostAsync(temp, health, queue: queue, ingestionStallThresholdSeconds: 2);

        var failedPost = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedPost.StatusCode);
        Assert.Contains("queue_full", await failedPost.Content.ReadAsStringAsync());

        var momentary = await host.Client.GetAsync("/health/ready");
        var momentaryBody = await momentary.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, momentary.StatusCode);
        Assert.Contains("\"status\":\"degraded\"", momentaryBody);
        Assert.Contains("ingestion_backpressure", momentaryBody);
        Assert.DoesNotContain("ingestion_stalled", momentaryBody);

        time.Advance(TimeSpan.FromSeconds(2));

        var sustained = await host.Client.GetAsync("/health/ready");
        var sustainedBody = await sustained.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sustained.StatusCode);
        Assert.Contains("\"status\":\"not_ready\"", sustainedBody);
        Assert.Contains("ingestion_stalled", sustainedBody);
    }

    [Fact]
    public async Task Ready_CommitTimeout_StartsStallWindowAndBecomes503()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = MonitorTestHealth.Ready(time);
        await using var host = await StartHostAsync(
            temp,
            health,
            commitTimeout: TimeSpan.FromMilliseconds(50),
            ingestionStallThresholdSeconds: 2);

        var timedOut = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Contains("commit_timeout", await timedOut.Content.ReadAsStringAsync());

        var momentary = await host.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, momentary.StatusCode);
        Assert.Contains("ingestion_backpressure", await momentary.Content.ReadAsStringAsync());

        time.Advance(TimeSpan.FromSeconds(2));

        var sustained = await host.Client.GetAsync("/health/ready");
        var sustainedBody = await sustained.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sustained.StatusCode);
        Assert.Contains("ingestion_stalled", sustainedBody);
    }

    [Fact]
    public async Task Ready_ProjectionLagBelowThreshold_IsDegraded200()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = MonitorTestHealth.Ready(time);
        health.SetProjectionStatus(backlog: 3, oldestUnprocessedReceivedAt: DateTimeOffset.UnixEpoch.AddSeconds(-30));
        await using var host = await StartHostAsync(temp, health, projectionLagThresholdSeconds: 60);

        var response = await host.Client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"degraded\"", body);
        Assert.Contains("\"projection_lag_seconds\":30", body);
        Assert.DoesNotContain("projection_lag_exceeded", body);
    }

    [Fact]
    public async Task Ready_ProjectionLagAtThreshold_IsNotReady503()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = MonitorTestHealth.Ready(time);
        health.SetProjectionStatus(backlog: 5, oldestUnprocessedReceivedAt: DateTimeOffset.UnixEpoch.AddSeconds(-60));
        await using var host = await StartHostAsync(temp, health, projectionLagThresholdSeconds: 60);

        var response = await host.Client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"not_ready\"", body);
        Assert.Contains("projection_lag_exceeded", body);
    }

    [Fact]
    public async Task Ready_ProjectionStatusUnknown_IsNotReady503()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = new MonitorHealthState(time);
        health.SetLoopbackBound(true);
        health.MarkMigrationComplete();
        health.SetWriterRunning(true);
        health.SetProjectionWorkerRunning(true);
        // Deliberately no SetProjectionStatus: lag has never been observed.
        await using var host = await StartHostAsync(temp, health);

        var response = await host.Client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"not_ready\"", body);
        Assert.Contains("projection_status_unknown", body);
        // The pinned readiness body schema is still present on the failure path.
        Assert.Contains("\"checks\":", body);
        Assert.Contains("\"degraded_reasons\":", body);
    }

    private static Task<RunningMonitorHost> StartHostAsync(
        MonitorTempDirectory temp,
        MonitorHealthState health,
        IngestionQueue? queue = null,
        TimeSpan? commitTimeout = null,
        int ingestionStallThresholdSeconds = MonitorOptions.DefaultIngestionStallThresholdSeconds,
        int projectionLagThresholdSeconds = MonitorOptions.DefaultProjectionLagThresholdSeconds)
    {
        var testOptions = new MonitorHostTestOptions
        {
            Health = health,
            Queue = queue,
            CommitTimeout = commitTimeout,
            StartWriter = false,
            StartProjectionWorker = false,
        };
        return MonitorTestHost.StartAsync(
            temp,
            testOptions: testOptions,
            maxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            ingestionStallThresholdSeconds: ingestionStallThresholdSeconds,
            projectionLagThresholdSeconds: projectionLagThresholdSeconds);
    }

    private static RawTelemetryRecord SyntheticRecord() =>
        new(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace",
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string ValidTraceJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222","name":"chat gpt-4o"}]}]}]}
        """;

}

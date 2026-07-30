using System.Net;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedOnlyHostCompositionTests
{
    private const string UnsupportedEndpoint =
        """{"accepted":false,"error":"unsupported_endpoint","message":"Only /v1/traces is supported."}""";
    private const string RuntimeBackupInvalidHost =
        """{"error":"invalid_host"}""";
    private const string MethodNotAllowed =
        """{"accepted":false,"error":"method_not_allowed","message":"Only POST is supported for /v1/traces."}""";

    public static TheoryData<HttpMethod, string> KnownHumanRequests =>
        new()
        {
            { HttpMethod.Get, "/" },
            { HttpMethod.Head, "/" },
            { HttpMethod.Get, "/traces?period=all" },
            { HttpMethod.Get, "/traces/" },
            { HttpMethod.Get, "/traces/trace-id" },
            { HttpMethod.Get, "/traces/trace-id/" },
            { HttpMethod.Get, "/diagnostics" },
            { HttpMethod.Get, "/diagnostics/" },
            { HttpMethod.Get, "/ingestions" },
            { HttpMethod.Get, "/ingestions/" },
            { HttpMethod.Get, "/historical-analysis" },
            { HttpMethod.Get, "/historical-analysis/" },
            { HttpMethod.Get, "/historical-import" },
            { HttpMethod.Get, "/historical-import/" },
            { HttpMethod.Get, "/sanitized-import" },
            { HttpMethod.Get, "/sanitized-import/" },
            { HttpMethod.Get, "/retention/trace/trace-id" },
            { HttpMethod.Get, "/retention/trace/trace-id/" },
            { HttpMethod.Get, "/alerts" },
            { HttpMethod.Get, "/alerts/" },
            { HttpMethod.Get, "/costs" },
            { HttpMethod.Get, "/costs/" },
            { HttpMethod.Get, "/backup-restore" },
            { HttpMethod.Get, "/backup-restore/" },
            { HttpMethod.Get, "/monitor.css" },
            { HttpMethod.Head, "/monitor.js" },
            { HttpMethod.Get, "/monitor-cache-panel.js" },
            { HttpMethod.Get, "/monitor-diagnostics.js" },
            { HttpMethod.Get, "/monitor-drawer.js" },
            { HttpMethod.Get, "/monitor-error-mode.js" },
            { HttpMethod.Get, "/monitor-flow.js" },
            { HttpMethod.Get, "/monitor-historical-analysis.js" },
            { HttpMethod.Get, "/monitor-historical-import.js" },
            { HttpMethod.Get, "/monitor-inspector.js" },
            { HttpMethod.Get, "/monitor-overview.js" },
            { HttpMethod.Get, "/monitor-retention.js" },
            { HttpMethod.Get, "/monitor-sanitized-import.js" },
            { HttpMethod.Get, "/monitor-shell.js" },
            { HttpMethod.Get, "/monitor-span-detail.js" },
            { HttpMethod.Get, "/monitor-tracelist.js" },
            { HttpMethod.Get, "/monitor-waterfall.js" },
            { HttpMethod.Get, "/alert-center.js" },
            { HttpMethod.Head, "/costs.js" },
            { HttpMethod.Get, "/vendor/fonts/fonts.css" },
            { HttpMethod.Get, "/api/doctor/ui/v1/sources" },
            { HttpMethod.Get, "/api/doctor/ui/v1/source-diagnostics/observation-id" },
            { HttpMethod.Get, "/api/runtime-backup/v1/backups/backup-id" },
            { HttpMethod.Get, "/api/local-monitor/v1/repositories" },
        };

    [Theory]
    [MemberData(nameof(KnownHumanRequests))]
    public async Task KnownHumanGetAndHead_AreClosedEmptyNotFound(HttpMethod method, string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());
        using var request = new HttpRequestMessage(method, path);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task EndpointDiscovery_ContainsOnlyMachineAndFrozenCompatibilityRoutes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());

        Assert.Contains("/health/live", host.RoutePatterns);
        Assert.Contains("/health/ready", host.RoutePatterns);
        Assert.Contains("/v1/traces", host.RoutePatterns);
        Assert.Contains("/api/monitor/summary", host.RoutePatterns);
        Assert.Contains("/api/session-workspace/status", host.RoutePatterns);
        Assert.Contains("/api/analysis/options", host.RoutePatterns);
        Assert.Contains("/api/raw-replay/v1/export-previews", host.RoutePatterns);

        Assert.DoesNotContain("/", host.RoutePatterns);
        Assert.DoesNotContain("/traces", host.RoutePatterns);
        Assert.DoesNotContain("/traces/{traceId}", host.RoutePatterns);
        Assert.DoesNotContain("/diagnostics", host.RoutePatterns);
        Assert.DoesNotContain("/historical-analysis", host.RoutePatterns);
        Assert.DoesNotContain("/historical-import", host.RoutePatterns);
        Assert.DoesNotContain("/sanitized-import", host.RoutePatterns);
        Assert.DoesNotContain("/alerts", host.RoutePatterns);
        Assert.DoesNotContain("/costs", host.RoutePatterns);
        Assert.DoesNotContain("/backup-restore", host.RoutePatterns);
        Assert.DoesNotContain(
            host.RoutePatterns,
            route => route.StartsWith("/api/doctor/ui/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.RoutePatterns,
            route => route.StartsWith("/api/runtime-backup/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "/sessions/{sessionId}/events/{eventId}/content",
            host.RoutePatterns);
        Assert.DoesNotContain(
            host.RoutePatterns,
            route => route.StartsWith("/traces/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.RoutePatterns,
            route => route.StartsWith("/api/local-monitor/v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrozenMachineFallbacks_KeepExactStatusBytesAndContentType()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());

        using var live = await host.Client.GetAsync("/health/live");
        await AssertJsonAsync(live, HttpStatusCode.OK, """{"status":"live"}""");

        using var unknown = await host.Client.GetAsync("/future-machine-api");
        await AssertJsonAsync(unknown, HttpStatusCode.NotFound, UnsupportedEndpoint);

        using var methodFallback = await host.Client.GetAsync("/v1/traces");
        await AssertJsonAsync(methodFallback, HttpStatusCode.MethodNotAllowed, MethodNotAllowed);

        using var sessionRawFallback = await host.Client.GetAsync(
            $"/sessions/{Guid.CreateVersion7()}/events/{Guid.CreateVersion7()}/content");
        await AssertJsonAsync(sessionRawFallback, HttpStatusCode.NotFound, UnsupportedEndpoint);

        foreach (var path in new[]
        {
            "/traces/1/raw",
            "/traces/trace-id/prompt-label",
            "/traces/trace-id/spans/span-id/detail",
            "/traces/trace-id/analysis/runs/1",
        })
        {
            using var technicalRawFallback = await host.Client.GetAsync(path);
            await AssertJsonAsync(
                technicalRawFallback,
                HttpStatusCode.NotFound,
                UnsupportedEndpoint);
        }

        using var analysisStart = await host.Client.PostAsync(
            "/traces/trace-id/analysis",
            new StringContent("{}"));
        await AssertJsonAsync(
            analysisStart,
            HttpStatusCode.NotFound,
            UnsupportedEndpoint);

        using var rawReplayRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/raw-replay/v1/export-previews")
        {
            Content = new StringContent("not-json"),
        };
        rawReplayRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var rawReplay = await host.Client.SendAsync(rawReplayRequest);
        await AssertJsonAsync(
            rawReplay,
            HttpStatusCode.Forbidden,
            """{"error":"sanitized_only_denied"}""");
        Assert.True(rawReplay.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/diagnostics//")]
    [InlineData("/monitor.js/")]
    [InlineData("/traces//")]
    [InlineData("/traces/trace-id//")]
    [InlineData("/traces/trace-id/extra")]
    [InlineData("/retention/trace/trace-id/extra")]
    public async Task NearMissHumanPaths_KeepGenericFallback(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());

        using var response = await host.Client.GetAsync(path);

        await AssertJsonAsync(response, HttpStatusCode.NotFound, UnsupportedEndpoint);
    }

    [Theory]
    [InlineData("/monitor-machine.js")]
    [InlineData("/monitor-future.css")]
    public async Task NearMissHumanAssets_KeepFrameworkNotFound(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());

        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains("Cache-Control"));
    }

    [Theory]
    [InlineData("/api/runtime-backup/v1/backups/missing")]
    [InlineData("/backup-restore")]
    [InlineData("/backup-restore/")]
    public async Task RuntimeBackupOwnerRejectsInvalidHostBeforeSanitizedClosure(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHost());
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "monitor.example.invalid";

        using var response = await host.Client.SendAsync(request);

        await AssertJsonAsync(response, HttpStatusCode.BadRequest, RuntimeBackupInvalidHost);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task MachineRuntime_OtlpIngestPersistsProjectsAndPublishesOrderedSseNotification()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = TimeProvider.System,
        };
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: new MonitorHostTestOptions
            {
                ProjectionPollInterval = TimeSpan.FromMilliseconds(25),
                UseUserSecrets = false,
            });
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/events");
        using var streamResponse = await host.Client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        await using var stream = await streamResponse.Content.ReadAsStreamAsync();
        Assert.Equal(
            ": connected\n\n",
            await ReadUntilAsync(stream, "\n\n", TimeSpan.FromSeconds(5)));

        using var ingest = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SyntheticTraceJson));

        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        Assert.Equal("application/json", ingest.Content.Headers.ContentType?.ToString());
        using (var receipt = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync()))
        {
            Assert.True(receipt.RootElement.GetProperty("accepted").GetBoolean());
            Assert.True(receipt.RootElement.GetProperty("rawRecordId").GetInt64() > 0);
            Assert.True(receipt.RootElement.GetProperty("observationId").GetInt64() > 0);
        }

        var raw = Assert.Single(temp.CreateRawStore().ListRecords());
        Assert.Equal(SyntheticTraceId, raw.TraceId);
        Assert.Equal(SyntheticTraceJson, raw.PayloadJson);

        var notification = await ReadUntilAsync(
            stream,
            "data: {}\n\n",
            TimeSpan.FromSeconds(10));
        Assert.StartsWith("id: ", notification, StringComparison.Ordinal);
        Assert.True(
            notification.IndexOf("\nevent: projection\n", StringComparison.Ordinal) >
            notification.IndexOf("id: ", StringComparison.Ordinal));
        Assert.True(
            notification.IndexOf("\ndata: {}\n\n", StringComparison.Ordinal) >
            notification.IndexOf("\nevent: projection\n", StringComparison.Ordinal));

        var projectedBody = await WaitForProjectedTraceAsync(host, SyntheticTraceId);
        using var projected = JsonDocument.Parse(projectedBody);
        var item = Assert.Single(
            projected.RootElement.GetProperty("items").EnumerateArray(),
            trace => trace.GetProperty("trace_id").GetString() == SyntheticTraceId);
        Assert.Equal(1, item.GetProperty("span_count").GetInt32());
        Assert.DoesNotContain("synthetic prompt body", projectedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachineRuntime_SessionIngestCommitsAndWorkspaceReadsStaySanitized()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = TimeProvider.System,
        };
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: new MonitorHostTestOptions
            {
                UseUserSecrets = false,
            });
        using var ingestRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/session-ingest/v1/events")
        {
            Content = JsonContent(SyntheticSessionEnvelope),
        };
        ingestRequest.Headers.Add("X-CAO-Session-Event-Version", "1");

        using var ingest = await host.Client.SendAsync(ingestRequest);

        Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);
        Assert.Equal(string.Empty, await ingest.Content.ReadAsStringAsync());

        using var listResponse = await host.Client.GetAsync("/api/session-workspace/sessions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal("application/json", listResponse.Content.Headers.ContentType?.ToString());
        var listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SyntheticSessionContent, listBody, StringComparison.Ordinal);
        using var list = JsonDocument.Parse(listBody);
        var session = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        var sessionId = session.GetProperty("session_id").GetString();
        Assert.NotNull(sessionId);
        Assert.False(session.TryGetProperty("payload", out _));

        using var detailResponse = await host.Client.GetAsync(
            $"/api/session-workspace/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal("application/json", detailResponse.Content.Headers.ContentType?.ToString());
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SyntheticSessionContent, detailBody, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(detailBody);
        var sessionEvent = Assert.Single(
            detail.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("UserPromptSubmit", sessionEvent.GetProperty("type").GetString());
        Assert.Equal("available", sessionEvent.GetProperty("content_state").GetString());
        Assert.False(sessionEvent.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task MachineRuntime_ReadinessKeepsExactRawDefaultStatusContentTypeAndBytes()
    {
        const string expected =
            """{"status":"ready","checks":{"loopback_bound":true,"db_open":true,"migration_complete":true,"writer_running":true,"projection_worker_running":true,"ingestion_accepting":true,"projection_lag_seconds":0,"projection_backlog":0,"span_projection_lag_seconds":0,"span_projection_backlog":0,"projection_failure_count":0},"degraded_reasons":[]}""";
        using var rawTemp = new MonitorTempDirectory();
        using var sanitizedTemp = new MonitorTempDirectory();
        var rawTime = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        var sanitizedTime = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        await using var raw = await MonitorTestHost.StartAsync(
            rawTemp,
            testOptions: ReadyQuietHost(rawTime));
        await using var sanitized = await MonitorTestHost.StartAsync(
            sanitizedTemp,
            sanitizedOnly: true,
            testOptions: ReadyQuietHost(sanitizedTime));

        using var rawResponse = await raw.Client.GetAsync("/health/ready");
        using var sanitizedResponse = await sanitized.Client.GetAsync("/health/ready");
        var rawBytes = await rawResponse.Content.ReadAsByteArrayAsync();
        var sanitizedBytes = await sanitizedResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sanitizedResponse.StatusCode);
        Assert.Equal("application/json", rawResponse.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            rawResponse.Content.Headers.ContentType?.ToString(),
            sanitizedResponse.Content.Headers.ContentType?.ToString());
        Assert.Equal(Encoding.UTF8.GetBytes(expected), rawBytes);
        Assert.Equal(rawBytes, sanitizedBytes);
    }

    private static async Task AssertJsonAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedBody)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
    }

    private static MonitorHostTestOptions QuietHost() =>
        new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartRetentionCleanupWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            UseUserSecrets = false,
        };

    private static MonitorHostTestOptions ReadyQuietHost(MutableTimeProvider time) =>
        new()
        {
            Health = MonitorTestHealth.Ready(time),
            StartWriter = false,
            StartProjectionWorker = false,
            StartRetentionCleanupWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            TimeProvider = time,
            UseUserSecrets = false,
        };

    private static async Task<string> ReadUntilAsync(
        Stream stream,
        string marker,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];
        var builder = new StringBuilder();
        try
        {
            while (!builder.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellation.Token);
                if (read == 0)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // Return the partial stream so the assertion reports the missing bytes.
        }

        return builder.ToString();
    }

    private static async Task<string> WaitForProjectedTraceAsync(
        RunningMonitorHost host,
        string traceId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string body = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await host.Client.GetAsync("/api/monitor/traces?limit=50");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("trace_id").GetString() == traceId))
            {
                return body;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Trace '{traceId}' was not visible through /api/monitor/traces. Last response: {body}");
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private const string SyntheticTraceId = "11111111111111111111111111111111";
    private const string SyntheticTraceJson =
        """{"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222","name":"chat gpt-4o","attributes":[{"key":"gen_ai.prompt","value":{"stringValue":"synthetic prompt body"}}]}]}]}]}""";
    private const string SyntheticSessionContent = "synthetic session machine content";
    private const string SyntheticSessionEnvelope =
        """
        {
          "schema_version": 1,
          "source_adapter": "copilot-compatible-hook",
          "source_surface": "hook-unknown",
          "native_session_id": "sanitized-machine-session",
          "events": [
            {
              "source_event_id": "sanitized-machine-event",
              "type": "UserPromptSubmit",
              "occurred_at": "2026-07-31T00:00:00Z",
              "payload": {
                "message": "synthetic session machine content"
              }
            }
          ]
        }
        """;
}

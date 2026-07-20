using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// DR6 negative security matrix for the Local Ingestion Monitor: default UI / API /
/// SSE never return raw or PII, the raw route is absent without the opt-in flag and
/// same-origin only with it, non-GET / cross-origin requests to the SSE stream are
/// refused, non-loopback Host headers are rejected, restart on the same DB recovers
/// without loss, and raw markers never leak into error responses.
/// </summary>
public class MonitorSecurityBoundaryTests
{
    private const string ClaudeSecretMarker = "sk-task18-claude-secret-marker";
    private static readonly string[] Markers =
    [
        "SECRET_PROMPT_TEXT_MARKER",
        "SECRET_TOOL_ARGS_MARKER",
        "leak-marker@example.com",
        .. Issue91SecretCorpus.Markers,
    ];

    // Distinctive fragments of the unsafe values injected into the guarded free-form
    // attributes by SanitizationProbePayload; none may surface in any sanitized read API.
    private static readonly string[] InjectedUnsafeFragments =
    {
        "leak-tool@evil.example.com", // email injected into gen_ai.tool.name
        "sk-live-DEADBEEF",           // secret-like injected into mcp_tool_name
        "victim",                     // windows path injected into gen_ai.agent.name
        "etc/shadow",                 // unix path injected into error.type
    };

    private const string SanitizationProbeTraceId = "trace-probe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeHookSecretMarkerStaysOutOfSanitizedSurfacesAndRawStorage(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartLiveHostAsync(temp, sanitizedOnly);
        using var ingest = ClaudeIngestRequest("UserPromptSubmit", $$"""{"api_key":"{{ClaudeSecretMarker}}","message":"synthetic"}""");

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(ingest)).StatusCode);
        using var sessions = JsonDocument.Parse(await host.Client.GetStringAsync("/api/session-workspace/sessions"));
        var sessionId = sessions.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        var detailBody = await host.Client.GetStringAsync($"/api/session-workspace/sessions/{sessionId}");
        Assert.DoesNotContain(ClaudeSecretMarker, detailBody, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(detailBody);
        var @event = detail.RootElement.GetProperty("events")[0];
        Assert.Equal("available", @event.GetProperty("content_state").GetString());
        var eventId = @event.GetProperty("event_id").GetString();
        var content = await host.Client.GetAsync($"/sessions/{sessionId}/events/{eventId}/content");
        Assert.Equal(sanitizedOnly ? HttpStatusCode.NotFound : HttpStatusCode.OK, content.StatusCode);
        Assert.DoesNotContain(ClaudeSecretMarker, await content.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await host.DisposeAsync();
        Assert.All(Directory.GetFiles(temp.Path), path =>
            Assert.DoesNotContain(ClaudeSecretMarker, Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClaudeContentDisabledAndErrorSurfacesDoNotExposeSecretMarker()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartLiveHostAsync(temp);
        using var usage = ClaudeIngestRequest("assistant.usage", $$"""{"api_key":"{{ClaudeSecretMarker}}"}""");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(usage)).StatusCode);
        using var sessions = JsonDocument.Parse(await host.Client.GetStringAsync("/api/session-workspace/sessions"));
        var sessionId = sessions.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        var detailBody = await host.Client.GetStringAsync($"/api/session-workspace/sessions/{sessionId}");
        Assert.DoesNotContain(ClaudeSecretMarker, detailBody, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(detailBody);
        Assert.Equal("not_captured", detail.RootElement.GetProperty("events")[0].GetProperty("content_state").GetString());

        using var invalid = ClaudeIngestRequest("UserPromptSubmit", $$"""{"api_key":"{{ClaudeSecretMarker}}"}""");
        invalid.Content = new StringContent(
            (await invalid.Content!.ReadAsStringAsync()).Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal),
            Encoding.UTF8,
            "application/json");
        var error = await host.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.DoesNotContain(ClaudeSecretMarker, await error.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await host.DisposeAsync();
        Assert.All(Directory.GetFiles(temp.Path), path =>
            Assert.DoesNotContain(ClaudeSecretMarker, Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task SanitizedSurfaces_NeverReturnRawOrPii_EvenWithRawShownByDefault()
    {
        // The JSON APIs, the SSE stream, and diagnostics carry sanitized metadata
        // only — never raw or PII — even with raw shown by default. (The dashboard
        // and trace list surface the prompt label by design; see the D032 tests.)
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        foreach (var path in new[]
                 {
                     "/diagnostics",
                     "/api/monitor/ingestions", "/api/monitor/source-diagnostics", "/api/monitor/traces",
                 })
        {
            var body = await host.Client.GetStringAsync(path);
            foreach (var marker in Markers)
            {
                Assert.DoesNotContain(marker, body);
            }
        }
    }

    [Fact]
    public async Task DashboardAndTraceList_ShowPromptByDefault_ButNeverToolArgsOrPii()
    {
        // D032: the dashboard and trace list label traces with the user prompt by
        // default (raw-bearing), surfacing ONLY the prompt — never tool arguments
        // or PII, and never via /api/monitor/* (server-rendered pages only).
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        // period=all keeps the epoch-seeded fixture rows visible on the Sprint18
        // trace list (its default period filter is "today").
        foreach (var path in new[] { "/", "/traces?period=all" })
        {
            var body = await host.Client.GetStringAsync(path);
            Assert.Contains("SECRET_PROMPT_TEXT_MARKER", body);
            Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", body);
            Assert.DoesNotContain("leak-marker@example.com", body);
        }
    }

    [Fact]
    public async Task DashboardAndTraceList_OmitPromptUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, sanitizedOnly: true);

        foreach (var path in new[] { "/", "/traces" })
        {
            var body = await host.Client.GetStringAsync(path);
            foreach (var marker in Markers)
            {
                Assert.DoesNotContain(marker, body);
            }
        }
    }

    [Fact]
    public async Task DashboardAndTraceList_CrossOriginIsForbidden_WhenRawShown()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        foreach (var path in new[] { "/", "/traces" })
        {
            using var crossSite = new HttpRequestMessage(HttpMethod.Get, path);
            crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);

            using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, path);
            foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);
        }
    }

    [Fact]
    public async Task DashboardAndTraceList_SetNoStore_WhenRawShown()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        foreach (var path in new[] { "/", "/traces" })
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore, $"{path} must send Cache-Control: no-store when raw is shown.");
        }
    }

    [Fact]
    public async Task RawRoute_IsAbsentUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RawRoute_CrossOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);
    }

    [Fact]
    public async Task SpanDetailRoute_IsAbsentUnderSanitizedOnly()
    {
        // Sprint18 D043: the span-detail route follows the same raw-route boundary.
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync("/traces/55555555555555555555555555555555/spans/6666666666666666/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpanDetailRoute_CrossOriginIsForbidden_AndSendsNoStore()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);
        const string path = "/traces/55555555555555555555555555555555/spans/6666666666666666/detail";

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, path);
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, path);
        foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);

        var sameOrigin = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
        Assert.True(sameOrigin.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Events_DoesNotAcceptCrossOriginPost()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadOnlyHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent("{}"),
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
        var response = await host.Client.SendAsync(request);

        // SSE is a GET-only notification stream; there is no CSRF-exposed mutation.
        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 405 or 404 for cross-origin POST /events but got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task NonLoopbackHostHeader_IsRejectedOnPageRoutes()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/")
        {
            Headers = { Host = "example.com" },
        };
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_host", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RestartRecovery_ReusesDatabaseAndCatchesUpProjection()
    {
        using var temp = new MonitorTempDirectory();

        await using (var first = await StartLiveHostAsync(temp, startProjectionWorker: false))
        {
            var response = await first.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var firstStore = temp.CreateRawStore();
        var rawRecord = Assert.Single(firstStore.ListRecords());
        Assert.Equal(ProjectionDispositionState.NotStarted, firstStore.GetProjectionDisposition(rawRecord.Id!.Value)!.State);

        var projectionStore = new ProjectionStatusSignalStore(
            new RawTelemetryStoreProjectionStore(temp.CreateRawStore()));
        await using var second = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                ProjectionStore = projectionStore,
                ProjectionPollInterval = TimeSpan.FromMilliseconds(50),
            });
        await projectionStore.InitialTraceProjectionStatusRead.Task;
        var ingestions = await second.Client.GetStringAsync("/api/monitor/ingestions?limit=200");
        using var document = JsonDocument.Parse(ingestions);
        Assert.Equal(1, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(ProjectionDispositionState.Completed, firstStore.GetProjectionDisposition(rawRecord.Id.Value)!.State);

        var ready = await second.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task RawPayloadMarkers_DoNotAppearInErrorResponses()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartLiveHostAsync(temp);

        var accepted = await host.Client.PostAsync("/v1/traces", JsonContent(SensitiveTraceJson));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var bad = await host.Client.PostAsync("/v1/traces", JsonContent("{"));
        var body = await bad.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, body);
        }

        Assert.DoesNotContain(temp.DatabasePath, body);
        Assert.DoesNotContain(Environment.UserName, body);
        Assert.DoesNotContain("Exception", body);
    }

    [Fact]
    public async Task SpanApi_NeverReturnsRawOrPii_UnderRawDefaultOn()
    {
        // The per-span read API (/api/monitor/traces/{traceId}/spans) is sanitized
        // metadata only — never raw or PII — even with raw shown by default.
        using var temp = new MonitorTempDirectory();
        SeedSanitizationProbeTrace(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        var body = await host.Client.GetStringAsync($"/api/monitor/traces/{SanitizationProbeTraceId}/spans?limit=200");

        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, body);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpanApi_GuardsUnsafeFreeFormValues_AndKeepsRows(bool sanitizedOnly)
    {
        // DR6 per-attribute sanitization: email / path / secret-like values injected
        // into each guarded free-form attribute are dropped from the projection
        // (column -> null) while the rest of the row survives, and a safe value is
        // kept. Holds under raw-default-on and under --sanitized-only alike.
        using var temp = new MonitorTempDirectory();
        SeedSanitizationProbeTrace(temp);
        await using var host = await StartReadOnlyHostAsync(temp, sanitizedOnly);

        var body = await host.Client.GetStringAsync($"/api/monitor/traces/{SanitizationProbeTraceId}/spans?limit=200");

        foreach (var fragment in InjectedUnsafeFragments)
        {
            Assert.DoesNotContain(fragment, body);
        }

        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, body);
        }

        using var document = JsonDocument.Parse(body);
        var spans = document.RootElement.GetProperty("items");

        var toolSpan = FindSpan(spans, "probe-tool");
        Assert.Equal(JsonValueKind.Null, toolSpan.GetProperty("tool_name").ValueKind);     // unsafe email dropped
        Assert.Equal(JsonValueKind.Null, toolSpan.GetProperty("mcp_tool_name").ValueKind); // unsafe secret dropped
        Assert.Equal("function", toolSpan.GetProperty("tool_type").GetString());           // row kept

        var agentSpan = FindSpan(spans, "probe-agent");
        Assert.Equal(JsonValueKind.Null, agentSpan.GetProperty("agent_name").ValueKind);   // unsafe windows path dropped
        Assert.Equal("invoke_agent", agentSpan.GetProperty("operation").GetString());      // row kept

        var errorSpan = FindSpan(spans, "probe-error");
        Assert.Equal(JsonValueKind.Null, errorSpan.GetProperty("error_type").ValueKind);   // unsafe unix path dropped

        var safeSpan = FindSpan(spans, "probe-safe");
        Assert.Equal("read_file", safeSpan.GetProperty("tool_name").GetString());          // positive control: safe value kept
    }

    [Fact]
    public async Task SanitizedOnly_ExcludesPiiFromAllReadApis()
    {
        // Under --sanitized-only, the list and per-span read APIs still carry no PII
        // or injected unsafe values (raw mode off does not loosen the projection guard).
        using var temp = new MonitorTempDirectory();
        SeedSanitizationProbeTrace(temp);
        await using var host = await StartReadOnlyHostAsync(temp, sanitizedOnly: true);

        foreach (var path in new[]
                 {
                     "/api/monitor/ingestions",
                     "/api/monitor/source-diagnostics",
                     "/api/monitor/traces",
                     $"/api/monitor/traces/{SanitizationProbeTraceId}/spans",
                 })
        {
            var body = await host.Client.GetStringAsync(path);
            foreach (var marker in Markers)
            {
                Assert.DoesNotContain(marker, body);
            }

            foreach (var fragment in InjectedUnsafeFragments)
            {
                Assert.DoesNotContain(fragment, body);
            }
        }
    }

    [Fact]
    public async Task AllRawBearingRoutes_SetNoStore()
    {
        // DR6 asserts Cache-Control: no-store on ALL raw-bearing routes, not only the
        // raw-detail route. Pin both the trace-detail page and the raw-detail route here.
        using var temp = new MonitorTempDirectory();
        var rawRecordId = SeedSanitizationProbeTrace(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        var traceDetail = await host.Client.GetAsync($"/traces/{SanitizationProbeTraceId}");
        Assert.Equal(HttpStatusCode.OK, traceDetail.StatusCode);
        Assert.True(traceDetail.Headers.CacheControl?.NoStore, "trace-detail must send Cache-Control: no-store.");

        var rawDetail = await host.Client.GetAsync($"/traces/{rawRecordId}/raw");
        Assert.Equal(HttpStatusCode.OK, rawDetail.StatusCode);
        Assert.True(rawDetail.Headers.CacheControl?.NoStore, "raw-detail must send Cache-Control: no-store.");
    }

    private static long SeedSensitiveProjectedRecord(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-sec",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: SensitiveTraceJson);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        // Span projection links the raw record to the trace so the dashboard /
        // trace-list prompt extraction (ListRawRecordsByTraceId) can read it.
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private static async Task<int> WaitForIngestionProjectionCountAsync(RunningMonitorHost host, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var body = await host.Client.GetStringAsync("/api/monitor/ingestions?limit=200");
            using var document = JsonDocument.Parse(body);
            var count = document.RootElement.GetProperty("items").GetArrayLength();
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(25);
        }

        return -1;
    }

    private static Task<RunningMonitorHost> StartReadOnlyHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false)
    {
        new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        return MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
    }

    private static Task<RunningMonitorHost> StartLiveHostAsync(
        MonitorTempDirectory temp,
        bool sanitizedOnly = false,
        bool startProjectionWorker = true) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions
            {
                StartProjectionWorker = startProjectionWorker,
                ProjectionPollInterval = TimeSpan.FromMilliseconds(50),
            });

    private sealed class ProjectionStatusSignalStore(IMonitorProjectionStore inner) : ProjectionStoreTestDouble
    {
        public TaskCompletionSource InitialTraceProjectionStatusRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken) =>
            inner.ListUnprocessedForProjectionAsync(limit, cancellationToken);

        public override bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt) =>
            inner.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt);

        public override ProjectionDisposition? GetProjectionDisposition(long rawRecordId) => inner.GetProjectionDisposition(rawRecordId);

        public override bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
            inner.TryBeginProjection(rawRecordId, expectedRevision, updatedAt);

        public override bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
            inner.RecordProjectionFailure(rawRecordId, expectedRevision, updatedAt);

        public override bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt, int expectedDispositionRevision) =>
            inner.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt, expectedDispositionRevision);

        public override MonitorProjectionStatus GetProjectionStatus()
        {
            var status = inner.GetProjectionStatus();
            InitialTraceProjectionStatusRead.TrySetResult();
            return status;
        }

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken) =>
            inner.ListUnprocessedForSpanProjectionAsync(limit, cancellationToken);

        public override bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) =>
            inner.ApplySpanProjection(rawRecordId, spans, projectedAt);

        public override MonitorProjectionStatus GetSpanProjectionStatus() => inner.GetSpanProjectionStatus();

        public override MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            inner.ListMonitorIngestions(afterRawRecordId, limit);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage ClaudeIngestRequest(string eventType, string payload)
    {
        var json = $$"""
            {"schema_version":1,"source_adapter":"claude-code-hook","source_surface":"claude-code",
             "native_session_id":"task18-security-native","source_application_version":"fixture-v1",
             "adapter_version":"claude-hook-v1","normalization_version":"session-normalization-v1",
             "events":[{"source_event_id":"task18-{{eventType}}","type":"{{eventType}}",
             "occurred_at":"2026-07-13T00:00:00Z","payload":{{payload}}}]}
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CAO-Session-Event-Version", "1");
        return request;
    }

    private static string ValidTraceJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"55555555555555555555555555555555","spanId":"6666666666666666","name":"chat gpt-4o"}]}]}]}
        """;

    private static string SensitiveTraceJson => SensitiveTraceJsonTemplate.Replace(
        "\"SECRET_PROMPT_TEXT_MARKER\"",
        JsonSerializer.Serialize(string.Join('|', new[] { "SECRET_PROMPT_TEXT_MARKER" }.Concat(Issue91SecretCorpus.Markers))),
        StringComparison.Ordinal);

    private const string SensitiveTraceJsonTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"55555555555555555555555555555555","spanId":"6666666666666666","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private static long SeedSanitizationProbeTrace(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: SanitizationProbeTraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: SanitizationProbePayload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private static JsonElement FindSpan(JsonElement items, string spanId)
    {
        foreach (var span in items.EnumerateArray())
        {
            if (string.Equals(span.GetProperty("span_id").GetString(), spanId, StringComparison.Ordinal))
            {
                return span;
            }
        }

        throw new Xunit.Sdk.XunitException($"span_id '{spanId}' not found in the span projection.");
    }

    // One trace whose spans inject email / path / secret-like values into each guarded
    // free-form attribute (gen_ai.tool.name, mcp_tool_name, gen_ai.agent.name, error.type),
    // plus a safe sibling tool span (read_file) as a positive control, and raw / PII
    // (gen_ai.prompt, gen_ai.tool.arguments, user.email) that must never reach the
    // sanitized projection.
    private const string SanitizationProbePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-probe","spanId":"probe-agent","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"C:\\Users\\victim\\secret.txt"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"trace-probe","spanId":"probe-tool","parentSpanId":"probe-agent","name":"execute_tool",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"leak-tool@evil.example.com"}},
             {"key":"github.copilot.tool.parameters.mcp_tool_name","value":{"stringValue":"api_key=sk-live-DEADBEEF"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
           ]},
          {"traceId":"trace-probe","spanId":"probe-error","parentSpanId":"probe-agent","name":"execute_tool",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"error.type","value":{"stringValue":"/etc/shadow"}}
           ]},
          {"traceId":"trace-probe","spanId":"probe-safe","parentSpanId":"probe-agent","name":"execute_tool",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
           ]}
        ]}]}]}
        """;

}

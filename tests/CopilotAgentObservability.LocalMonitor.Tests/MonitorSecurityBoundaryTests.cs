using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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
    private static readonly string[] Markers =
    {
        "SECRET_PROMPT_TEXT_MARKER",
        "SECRET_TOOL_ARGS_MARKER",
        "leak-marker@example.com",
    };

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

    [Fact]
    public async Task DefaultSurfaces_NeverReturnRawOrPii_EvenWithRawShownByDefault()
    {
        using var temp = new MonitorTempDirectory();
        SeedSensitiveProjectedRecord(temp);
        await using var host = await StartReadOnlyHostAsync(temp);

        foreach (var path in new[]
                 {
                     "/", "/ingestions", "/traces", "/diagnostics",
                     "/api/monitor/ingestions", "/api/monitor/traces",
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

        await using (var first = await StartLiveHostAsync(temp))
        {
            var response = await first.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await WaitForIngestionProjectionCountAsync(first, expected: 1));
        }

        await using var second = await StartLiveHostAsync(temp);
        Assert.Equal(1, await WaitForIngestionProjectionCountAsync(second, expected: 1));

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
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
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
        return id;
    }

    private static async Task<int> WaitForIngestionProjectionCountAsync(RunningHost host, int expected)
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

    private static async Task<RunningHost> StartReadOnlyHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, SanitizedOnly: sanitizedOnly, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static async Task<RunningHost> StartLiveHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, SanitizedOnly: sanitizedOnly, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { ProjectionPollInterval = TimeSpan.FromMilliseconds(50) });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string ValidTraceJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"55555555555555555555555555555555","spanId":"6666666666666666","name":"chat gpt-4o"}]}]}]}
        """;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private const string SensitiveTraceJson = """
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
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
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

    private sealed class RunningHost(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await app.StopAsync();
            }
            catch
            {
                // Ignore stop faults during teardown.
            }

            await app.DisposeAsync();
        }
    }
}

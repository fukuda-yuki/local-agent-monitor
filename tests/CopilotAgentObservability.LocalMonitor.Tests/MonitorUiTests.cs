using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorUiTests
{
    [Fact]
    public async Task UiRoutes_ReturnSuccessfulHtmlPages()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        foreach (var path in new[] { "/", "/traces", "/diagnostics" })
        {
            var response = await host.Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("ローカルモニター", body);
        }
    }

    [Fact]
    public async Task IngestionsRoute_IsRetired_Returns404()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/ingestions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ShowsPromptByDefault_ButNotToolArgsOrPii()
    {
        // D032: the dashboard labels traces with the user prompt by default
        // (raw-bearing), but surfaces ONLY the prompt — never tool arguments or PII.
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var overview = await host.Client.GetStringAsync("/");

        Assert.Contains("ダッシュボード", overview);
        Assert.Contains("現在の状態", overview);
        Assert.Contains("今すぐ開く", overview);
        Assert.Contains("取り込み履歴", overview);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", overview);
        Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", overview);
        Assert.DoesNotContain("leak-marker@example.com", overview);
    }

    [Fact]
    public async Task Diagnostics_RendersReadinessWithoutRawOrPii()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var diagnostics = await host.Client.GetStringAsync("/diagnostics");

        Assert.Contains("health/ready", diagnostics);
        Assert.Contains("Readiness", diagnostics);
        Assert.Contains("コンポーネント確認", diagnostics);
        Assert.Contains("Loopback bind", diagnostics);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", diagnostics);
        Assert.DoesNotContain("leak-marker@example.com", diagnostics);
    }

    [Fact]
    public async Task Dashboard_LinksRawAndShowsPromptByDefault_HidesBothUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        var rawRecordId = SeedRawWithSensitiveMarkers(temp);

        await using var defaultHost = await StartHostAsync(temp);
        var defaultDashboard = await defaultHost.Client.GetStringAsync("/");
        // Raw is shown by default: the ingestion list links the raw record and the
        // trace is labelled by its prompt (D032).
        Assert.Contains($"/traces/{rawRecordId}/raw", defaultDashboard);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", defaultDashboard);
        Assert.DoesNotContain("leak-marker@example.com", defaultDashboard);

        await using var sanitizedHost = await StartHostAsync(temp, sanitizedOnly: true);
        var sanitizedDashboard = await sanitizedHost.Client.GetStringAsync("/");
        Assert.DoesNotContain($"/traces/{rawRecordId}/raw", sanitizedDashboard);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", sanitizedDashboard);
    }

    [Fact]
    public async Task TracesPage_ShowsPromptByDefault_ButNotToolArgsOrPii()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetStringAsync("/traces");

        // The trace id is still shown (shortened) and the prompt labels the card (D032),
        // but only the prompt is surfaced — never tool arguments or PII.
        Assert.Contains("trace-ui", traces);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", traces);
        Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", traces);
        Assert.DoesNotContain("leak-marker@example.com", traces);
    }

    [Fact]
    public async Task TracesPage_OmitsPromptUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var traces = await host.Client.GetStringAsync("/traces");

        Assert.Contains("trace-ui", traces);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", traces);
        Assert.DoesNotContain("leak-marker@example.com", traces);
    }

    [Fact]
    public async Task TracesPage_RejectsNegativeAfterCursorWith400()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetAsync("/traces?after=-1");

        Assert.Equal(HttpStatusCode.BadRequest, traces.StatusCode);
    }

    [Fact]
    public async Task Pages_ReferenceMonitorScriptAndScriptUsesCursorApis()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var index = await host.Client.GetStringAsync("/");
        var script = await host.Client.GetStringAsync("/monitor.js");

        Assert.Contains("/monitor.js", index);
        Assert.Contains("/api/monitor/ingestions", script);
        Assert.Contains("/api/monitor/traces", script);
        Assert.Contains("new EventSource('/events')", script);
    }

    [Fact]
    public async Task MonitorCss_IsServed()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/monitor.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VendoredFont_IsServedAsWoff2()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/vendor/fonts/noto-sans-mono-latin-400-normal.woff2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("font/woff2", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/vendor/cytoscape.min.js")]
    [InlineData("/vendor/dagre.min.js")]
    [InlineData("/vendor/cytoscape-dagre.js")]
    public async Task GraphVendorScripts_AreRemoved_Return404(string path)
    {
        // D033: the Cytoscape / dagre vendored graph dependency is removed; the
        // Flow Chart / Span Tree are now plain DOM.
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TracesPage_RendersTraceCards()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetStringAsync("/traces");

        // The redesigned list renders one card per trace with metric chips.
        Assert.Contains("trace-card", traces);
        Assert.Contains("trace-status", traces);
        Assert.Contains("Trace Explorer", traces);
        Assert.Contains("trace-card-chips", traces);
    }

    [Fact]
    public async Task Theme_VendorsFontsLocallyWithNoExternalCdn()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var index = await host.Client.GetStringAsync("/");
        var css = await host.Client.GetStringAsync("/monitor.css");

        // Fonts are referenced from the local vendor path, never a CDN (D028).
        Assert.Contains("/vendor/fonts/", css);
        Assert.Contains("--monitor-bg: oklch(0.15 0.012 264)", css);
        Assert.Contains("--monitor-accent: oklch(0.65 0.16 255)", css);
        foreach (var cdn in new[] { "googleapis.com", "gstatic.com", "cdn.jsdelivr.net", "unpkg.com" })
        {
            Assert.DoesNotContain(cdn, css);
            Assert.DoesNotContain(cdn, index);
        }
    }

    [Fact]
    public async Task MonitorViewsScript_UsesOnlySanitizedSpanApiForFlowChart()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-views.js");

        Assert.Contains("/api/monitor/traces/", script);
        Assert.Contains("next_cursor", script);
        Assert.Contains("parent_span_id", script);
        Assert.Contains("if (parent)", script);
        Assert.DoesNotContain("/raw", script);
        Assert.DoesNotContain("Html.Raw", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public async Task MonitorViewsScript_UsesSanitizedSpanFieldsForTimelineFilterSort()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-views.js");

        Assert.Contains("timeline-errors-only", script);
        Assert.Contains("timeline-sort", script);
        Assert.Contains("status", script);
        Assert.Contains("error_type", script);
        Assert.Contains("total_tokens", script);
        Assert.Contains("start_time", script);
        Assert.Contains("textContent", script);
        Assert.DoesNotContain("/raw", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public async Task MonitorViewsScript_UsesSanitizedSpanFieldsForCacheExplorer()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-views.js");

        Assert.Contains("renderCacheExplorer", script);
        Assert.Contains("cache_read_tokens", script);
        Assert.Contains("cache_creation_tokens", script);
        Assert.Contains("input_tokens", script);
        Assert.Contains("parent_span_id", script);
        Assert.Contains("textContent", script);
        Assert.DoesNotContain("/raw", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    private static void EnsureSchema(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
    }

    private static long SeedRawWithSensitiveMarkers(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-ui",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: SensitivePayload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        // Span projection links the raw record to the trace, which is what the
        // dashboard / trace-list prompt extraction reads (ListRawRecordsByTraceId).
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string SensitivePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-ui","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

}

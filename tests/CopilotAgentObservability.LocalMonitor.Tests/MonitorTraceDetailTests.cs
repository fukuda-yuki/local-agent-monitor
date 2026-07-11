using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.LocalMonitor.Projection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Trace-detail page (/traces/{traceId}) — Sprint18 §6.3. Server renders the
/// breadcrumb / status pill / meta / token-total card and the flow | waterfall
/// + cache-column shells that monitor-flow.js / monitor-waterfall.js /
/// monitor-cache-panel.js fill from the sanitized spans API. By default the
/// page inlines the raw OTLP payload. It enforces same-origin + no-store.
/// Under --sanitized-only, the sanitized shell remains available and the raw
/// section is absent.
/// </summary>
public class MonitorTraceDetailTests
{
    private const string TraceId = "trace-detail";
    private const string SecondaryTraceId = "secondary-trace";

    [Fact]
    public async Task TraceDetail_ByDefault_RendersSanitizedViewAndRawInlineWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{TraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        // Sanitized sections present (Japanese UI).
        Assert.Contains("実行の流れ", body);
        Assert.Contains("エラーのみ", body);
        Assert.Contains("キャッシュの観点", body);
        Assert.Contains("ターン別キャッシュ読取率", body);

        // Raw body shown inline by default.
        Assert.Contains("Raw OTLP ペイロード", body);
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", body);
    }

    [Fact]
    public async Task TraceDetail_RendersFlowWaterfallAndCacheColumnShell()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var body = await host.Client.GetStringAsync($"/traces/{TraceId}");

        // Sprint18 §6.3: tabs are gone — one screen with the フロー | waterfall
        // segment toggle, errors-only filter, and the standing cache column.
        Assert.DoesNotContain("role=\"tablist\"", body);
        Assert.DoesNotContain("id=\"tab-summary\"", body);
        Assert.Contains("id=\"view-toggle\"", body);
        Assert.Contains("data-view=\"flow\"", body);
        Assert.Contains("data-view=\"waterfall\"", body);
        Assert.Contains("id=\"errors-only\"", body);
        Assert.Contains("id=\"flow-view\"", body);
        Assert.Contains("id=\"waterfall-view\"", body);
        Assert.Contains("id=\"cache-overview\"", body);
        Assert.Contains("id=\"cache-turns\"", body);
        Assert.Contains("id=\"agent-summary\"", body);
        Assert.Contains("id=\"agent-summary-state\"", body);
        Assert.Contains("Agent実行グラフを読み込み中", body);
        Assert.Contains("swatch-agent", body);
        Assert.Contains("data-trace-id=\"trace-detail\"", body);
        Assert.Contains("/monitor-flow.js", body);
        Assert.Contains("/monitor-waterfall.js", body);
        Assert.Contains("/monitor-cache-panel.js", body);
    }

    [Fact]
    public async Task TraceDetail_RendersHeaderWithTokenTotalCardAndAdjacentNav()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var body = await host.Client.GetStringAsync($"/traces/{TraceId}");

        Assert.Contains("token-total-card", body);
        Assert.Contains("トークン合計", body);
        Assert.Contains("status-pill", body);
        Assert.Contains("← 前", body);
        Assert.Contains("次 →", body);
        Assert.Contains("Copilot で解析", body);
    }

    [Fact]
    public async Task TraceDetail_RendersCopilotDrawerUnderRawDefault()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var body = await host.Client.GetStringAsync($"/traces/{TraceId}");

        // Sprint18 §6.6: the analysis surface is the Copilot drawer, with the
        // mandatory data-boundary copy.
        Assert.Contains("id=\"copilot-drawer\"", body);
        Assert.Contains("GitHub Copilot で解析", body);
        Assert.Contains("ローカル SDK 経由 · raw はローカルから出ません", body);
        Assert.Contains("id=\"drawer-focus\"", body);
        Assert.Contains("value=\"tool-usage\"", body);
        Assert.Contains("value=\"agent-flow\"", body);
        Assert.Contains("value=\"instruction-diagnosis\"", body);
        Assert.Contains("指示診断", body);
        Assert.Contains("さらに質問…", body);
        Assert.Contains("/monitor-drawer.js", body);
    }

    [Fact]
    public async Task TraceDetail_DoesNotLoadGraphVendorScripts_AndHasNoCdn()
    {
        // D033/D042: no vendored graph runtime and no CDN; the page is driven by
        // the plain-DOM Sprint18 modules.
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var body = await host.Client.GetStringAsync($"/traces/{TraceId}");

        Assert.Contains("/monitor-flow.js", body);
        Assert.DoesNotContain("/monitor-views.js", body);
        Assert.DoesNotContain("/vendor/cytoscape.min.js", body);
        Assert.DoesNotContain("/vendor/dagre.min.js", body);
        Assert.DoesNotContain("/vendor/cytoscape-dagre.js", body);
        foreach (var cdn in new[] { "googleapis.com", "gstatic.com", "cdn.jsdelivr.net", "unpkg.com" })
        {
            Assert.DoesNotContain(cdn, body);
        }
    }

    [Fact]
    public async Task TraceDetail_UnderSanitizedOnly_RendersSanitizedTabsWithoutRaw()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{TraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("実行の流れ", body);
        Assert.Contains("キャッシュの観点", body);
        Assert.Contains("data-trace-id=\"trace-detail\"", body);
        Assert.Contains("data-raw-available=\"false\"", body);
        Assert.DoesNotContain("Raw OTLP ペイロード", body);
        Assert.DoesNotContain("copilot-drawer", body);
        Assert.DoesNotContain("Copilot で解析", body);
        Assert.DoesNotContain("/raw", body);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("leak-marker@example.com", body);
    }

    [Fact]
    public async Task TraceDetail_CrossSiteFetchIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("cross_origin_forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TraceDetail_ForeignOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{TraceId}");
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task TraceDetail_UnknownTraceReturns404()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TraceDetail_SecondaryTraceInMultiTraceRawRecord_RendersRawPreview()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp, MultiTracePayload, rawRecordTraceId: "primary-trace");
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{SecondaryTraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SECONDARY_TRACE_RAW_MARKER", body);
        Assert.DoesNotContain("No raw records for this trace.", body);
    }

    [Fact]
    public async Task TraceDetail_RawInlineIsBoundedAndLinksToFullRawRecord()
    {
        using var temp = new MonitorTempDirectory();
        var rawRecordId = SeedProjectedTrace(temp, LargeRawPayload, rawRecordTraceId: TraceId);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{TraceId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"/traces/{rawRecordId}/raw", body);
        Assert.Contains("raw プレビューは省略されています", body);
        Assert.DoesNotContain("FULL_RAW_MARKER_AFTER_PREVIEW", body);
    }

    [Fact]
    public void TraceDetail_PersistenceBusy_Returns503PersistenceBusy()
    {
        var services = new ServiceCollection()
            .AddSingleton(new MonitorOptions(
                DatabasePath: "unused.db",
                Url: "http://127.0.0.1:4320",
                SanitizedOnly: false,
                MaxRequestBodyBytes: 31_457_280))
            .AddSingleton<IMonitorProjectionStore>(new BusyProjectionStore())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        var model = new TraceDetailModel
        {
            PageContext = new PageContext
            {
                HttpContext = httpContext,
            },
        };

        var result = Assert.IsType<ContentResult>(model.OnGet(TraceId));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        Assert.Contains("persistence_busy", result.Content);
        Assert.Equal("no-store", httpContext.Response.Headers.CacheControl.ToString());
    }

    private static long SeedProjectedTrace(MonitorTempDirectory temp) =>
        SeedProjectedTrace(temp, AgentTracePayload, rawRecordTraceId: TraceId);

    private static long SeedProjectedTrace(MonitorTempDirectory temp, string payload, string rawRecordTraceId)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: rawRecordTraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: payload);
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

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string AgentTracePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-detail","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"trace-detail","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"trace-detail","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
           ]}
        ]}]}]}
        """;

    private const string MultiTracePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"primary-trace","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"PRIMARY_TRACE_RAW_MARKER"}}
           ]},
          {"traceId":"secondary-trace","spanId":"2000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECONDARY_TRACE_RAW_MARKER"}}
           ]}
        ]}]}]}
        """;

    private static string LargeRawPayload =>
        """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"trace-detail","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"PADDING_PLACEHOLDER FULL_RAW_MARKER_AFTER_PREVIEW"}}
           ]}
        ]}]}]}
        """.Replace("PADDING_PLACEHOLDER", new string('x', 6000));

    private sealed class BusyProjectionStore : IMonitorProjectionStore
    {
        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) =>
            throw new NotSupportedException();

        public bool ApplyProjection(
            long rawRecordId,
            string source,
            DateTimeOffset receivedAt,
            MonitorRecordProjection projection,
            DateTimeOffset projectedAt) =>
            throw new NotSupportedException();

        public MonitorProjectionStatus GetProjectionStatus() =>
            throw new NotSupportedException();

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) =>
            throw new NotSupportedException();

        public bool ApplySpanProjection(
            long rawRecordId,
            IReadOnlyList<MonitorSpanProjection> spans,
            DateTimeOffset projectedAt) =>
            throw new NotSupportedException();

        public MonitorProjectionStatus GetSpanProjectionStatus() =>
            throw new NotSupportedException();

        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            throw new NotSupportedException();

        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
            throw new NotSupportedException();

        public MonitorTraceRow? GetMonitorTrace(string traceId) =>
            throw new PersistenceBusyException();

        public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) =>
            throw new NotSupportedException();

        public RawTelemetryRecord? GetRawRecordById(long id) =>
            throw new NotSupportedException();

        public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit) =>
            throw new NotSupportedException();

        public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) =>
            throw new NotSupportedException();

        public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) =>
            throw new NotSupportedException();

        public MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) =>
            throw new NotSupportedException();
    }
}

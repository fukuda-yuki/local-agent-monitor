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

        foreach (var path in new[] { "/", "/traces", "/diagnostics", "/retention/session/11111111-1111-7111-8111-111111111111", "/retention/item/item.synthetic" })
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
    public async Task Overview_ShowsPromptByDefault_ButNotToolArgsOrPii()
    {
        // D032: the overview labels traces with the user prompt by default
        // (raw-bearing), but surfaces ONLY the prompt — never tool arguments or PII.
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var overview = await host.Client.GetStringAsync("/");

        Assert.Contains("概要", overview);
        Assert.Contains("高コスト trace TOP5", overview);
        Assert.Contains("最近のトレース", overview);
        Assert.Contains("時間帯別トークン", overview);
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

        using var response = await host.Client.GetAsync("/diagnostics");
        var diagnostics = await response.Content.ReadAsStringAsync();

        // Sprint18 §6.7: readiness heading + probe link, 4-stage pipeline,
        // component table, thresholds, and the C5 ingestion-history section —
        // all sanitized (no prompt / PII).
        Assert.Contains("health/ready", diagnostics);
        Assert.Contains("① 受信", diagnostics);
        Assert.Contains("④ 表示", diagnostics);
        Assert.Contains("コンポーネント確認", diagnostics);
        Assert.Contains("問題があった時だけ読む場所", diagnostics);
        Assert.Contains("Loopback bind", diagnostics);
        Assert.Contains("しきい値", diagnostics);
        Assert.Contains("id=\"ingestion-history\"", diagnostics);
        Assert.Contains("id=\"source-diagnostics\"", diagnostics);
        Assert.Contains("ソース互換性", diagnostics);
        Assert.Contains("/monitor-diagnostics.js", diagnostics);
        Assert.Contains("id=\"retention-diagnostics\"", diagnostics);
        Assert.Contains("id=\"retention-diagnostics-items\"", diagnostics);
        Assert.Contains("/monitor-retention.js", diagnostics);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", diagnostics);
        Assert.DoesNotContain("leak-marker@example.com", diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_RepositoryMetadataInventoryIsKeyOnly(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        SeedRepositoryMetadataDiagnostics(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly);

        var diagnostics = await host.Client.GetStringAsync("/diagnostics");

        Assert.Contains("id=\"repository-metadata-diagnostics\"", diagnostics);
        Assert.Contains("metadata_present", diagnostics);
        Assert.Contains("vcs.repository.name", diagnostics);
        Assert.Contains("vcs.repository.url.full", diagnostics);
        Assert.Contains("vcs.ref.head.revision", diagnostics);
        Assert.Contains("workspace.name", diagnostics);
        Assert.Contains("resource", diagnostics);
        Assert.Contains("span", diagnostics);
        Assert.Contains("event", diagnostics);
        Assert.DoesNotContain("PRIVATE_REPOSITORY_VALUE_MARKER", diagnostics);
        Assert.DoesNotContain("PRIVATE_OWNER_VALUE_MARKER", diagnostics);
        Assert.DoesNotContain("PRIVATE_URL_REPOSITORY_MARKER", diagnostics);
        Assert.DoesNotContain("private-marker@example.com", diagnostics);
        Assert.DoesNotContain("C:\\private\\repository", diagnostics);
        Assert.DoesNotContain("unsafe repository key", diagnostics);
    }

    [Fact]
    public async Task RetentionPage_RendersExactTargetAccessibleDialogWithoutPublishingPreview()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        const string targetId = "11111111-1111-7111-8111-111111111111";
        var response = await host.Client.GetAsync($"/retention/session/{targetId}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("id=\"retention-root\"", page);
        Assert.Contains("data-target-kind=\"session\"", page);
        Assert.Contains($"data-target-id=\"{targetId}\"", page);
        Assert.Contains("<dialog", page);
        Assert.Contains("aria-labelledby=\"retention-dialog-title\"", page);
        Assert.Contains("aria-describedby=\"retention-dialog-description\"", page);
        Assert.Contains("id=\"retention-operation\"", page);
        Assert.Contains("id=\"retention-reason\"", page);
        Assert.Contains("id=\"retention-comment\"", page);
        Assert.Contains("id=\"retention-preview\"", page);
        Assert.Contains("id=\"retention-confirm\"", page);
        Assert.Contains("id=\"retention-live\"", page);
        Assert.Contains("/monitor-retention.js", page);
        Assert.DoesNotContain("confirmation_token", page);
        Assert.DoesNotContain("autofocus", page);
        Assert.True(page.IndexOf("value=\"delete_now\"", StringComparison.Ordinal) > page.IndexOf("value=\"unpin\"", StringComparison.Ordinal));
        foreach (var reasonCode in new[] { "research_needed", "review_complete", "privacy_request", "data_minimization", "test_cleanup", "operator_correction", "other_local_reason" })
        {
            Assert.Contains($"value=\"{reasonCode}\"", page);
        }
    }

    [Theory]
    [InlineData("/retention/trace/11111111-1111-7111-8111-111111111111")]
    [InlineData("/retention/session/not-a-session-id")]
    public async Task RetentionPage_RejectsUnsupportedOrInvalidTargets(string path)
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Origin", "https://foreign.example")]
    public async Task RetentionPage_RejectsCrossSiteRequestsBeforeRenderingActions(string header, string value)
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/retention/session/11111111-1111-7111-8111-111111111111");
        request.Headers.TryAddWithoutValidation(header, value);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"error\":\"cross_origin_forbidden\"}", body);
        Assert.DoesNotContain("retention-dialog", body);
        Assert.DoesNotContain("retention-confirm", body);
    }

    [Fact]
    public async Task Overview_ShowsPromptByDefault_HidesItUnderSanitizedOnly()
    {
        // D032/D042: the overview's prompt labels are server-rendered raw-bearing
        // content; --sanitized-only falls back to a shortened TraceId. (The raw
        // record link moved off this page in the Sprint18 redesign — the raw-detail
        // route is reached from the trace-detail page.)
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);

        await using var defaultHost = await StartHostAsync(temp);
        var defaultOverview = await defaultHost.Client.GetStringAsync("/");
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", defaultOverview);
        Assert.DoesNotContain("leak-marker@example.com", defaultOverview);

        await using var sanitizedHost = await StartHostAsync(temp, sanitizedOnly: true);
        var sanitizedOverview = await sanitizedHost.Client.GetStringAsync("/");
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", sanitizedOverview);
        Assert.Contains("trace-ui", sanitizedOverview);
    }

    [Fact]
    public async Task TracesPage_ShowsPromptByDefault_ButNotToolArgsOrPii()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetStringAsync("/traces?period=all");

        // The trace id is still shown (shortened) and the prompt labels the row (D032),
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

        var traces = await host.Client.GetStringAsync("/traces?period=all");

        Assert.Contains("trace-ui", traces);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", traces);
        Assert.DoesNotContain("leak-marker@example.com", traces);
    }

    [Theory]
    [InlineData("/traces?period=90d")]
    [InlineData("/traces?sort=prompt")]
    [InlineData("/traces?status=broken")]
    public async Task TracesPage_RejectsInvalidFilterValuesWith400(string path)
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetAsync(path);

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
    public async Task DiagnosticsScript_UsesSourceDiagnosticsDtoWithoutMarkupInjection()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-diagnostics.js");

        Assert.Contains("/api/monitor/source-diagnostics${query}", script);
        Assert.Contains("sourceDiagnosticsPageSize = 50", script);
        Assert.Contains("maximumSourceDiagnosticsPages = 200", script);
        Assert.Contains("seenCursors", script);
        Assert.Contains("item.compatibility_state", script);
        Assert.Contains("item.reason_codes", script);
        Assert.Contains("item.next_action", script);
        Assert.Contains("item.unknown_span_count", script);
        Assert.Contains("item.unknown_event_count", script);
        Assert.Contains("item.unknown_attribute_count", script);
        Assert.Contains("textContent", script);
        Assert.DoesNotContain("innerHTML", script);
        Assert.DoesNotContain("setTimeout", script);
    }

    [Fact]
    public async Task RetentionScript_UsesAuthoritativeOneShotWorkflowWithoutLeaksOrAutomaticRetry()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-retention.js");

        foreach (var endpoint in new[]
        {
            "/api/retention/v1/status",
            "/api/retention/v1/sessions/",
            "/api/retention/v1/items/",
            "/api/retention/v1/previews",
            "/api/retention/v1/confirmations",
            "/api/retention/v1/mutations",
        })
        {
            Assert.Contains(endpoint, script);
        }

        Assert.Contains("x-monitor-csrf", script);
        Assert.Contains("Idempotency-Key", script);
        Assert.Contains("crypto.getRandomValues", script);
        Assert.Contains("rid1_", script);
        Assert.Contains("confirmationToken = null", script);
        Assert.Contains("finally", script);
        Assert.Contains("confirmation_consumed", script);
        Assert.Contains("response.headers.get(\"Location\")", script);
        Assert.Contains("refreshStatusAndPreviewOnce", script);
        Assert.Contains("if (!operation || !reason.value || commentLength > 256)", script);
        Assert.Contains("if (recoveryUsed) return", script);
        Assert.Contains("!location.startsWith(\"/\") || location.startsWith(\"//\")", script);
        Assert.Contains("addEventListener(\"pagehide\"", script);
        Assert.Contains("addEventListener(\"cancel\"", script);
        Assert.Contains("showModal()", script);
        Assert.Contains("textContent", script);
        Assert.Contains("createElement", script);
        Assert.Contains("invalidatePreview", script);
        Assert.Contains("previewSurface.hidden = true", script);
        Assert.Contains("operationControl.addEventListener(\"change\", invalidatePreview)", script);
        Assert.Contains("reason.addEventListener(\"change\", invalidatePreview)", script);
        Assert.Contains("comment.addEventListener(\"input\", invalidatePreview)", script);
        Assert.Contains("selectionRevision", script);
        Assert.Contains("resolveConsumedLocation", script);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "await resolveConsumedLocation\\(failure\\)").Count);
        Assert.Contains("renderCommittedCore(committed)", script);
        Assert.Contains("await loadCommittedSupplements(committed)", script);
        Assert.True(script.IndexOf("renderCommittedCore(committed)", StringComparison.Ordinal) < script.IndexOf("await loadCommittedSupplements(committed)", StringComparison.Ordinal));
        var committedFunction = script[script.IndexOf("async function renderCommitted(committed)", StringComparison.Ordinal)..script.IndexOf("async function refreshStatusAndPreviewOnce", StringComparison.Ordinal)];
        Assert.DoesNotContain("refreshStatusAndPreviewOnce", committedFunction);
        Assert.Contains("Promise.allSettled", committedFunction);
        Assert.Contains("function committedStatus(committed)", script);
        Assert.Contains("return committed.status || (committed.idempotent_replay ? \"replayed\" : \"committed\")", script);
        Assert.Contains("operationStatus.textContent = committedStatus(committed)", script);
        Assert.Contains("`/api/retention/v1/mutations/${encodeURIComponent(committed.operation_id)}`", script);
        Assert.Contains("operationStatus.textContent = committedStatus(authoritativeStatus)", script);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "operationStatus\\.textContent =").Count);
        Assert.Contains("Promise.allSettled([operationSupplement, targetSupplement, workerSupplement])", committedFunction);
        var operationSupplement = committedFunction[committedFunction.IndexOf("const operationSupplement", StringComparison.Ordinal)..committedFunction.IndexOf("const targetSupplement", StringComparison.Ordinal)];
        Assert.Contains("catch (failure)", operationSupplement);
        Assert.DoesNotContain("refreshStatusAndPreviewOnce", operationSupplement);
        Assert.DoesNotContain("operationStatus.textContent =", operationSupplement[operationSupplement.IndexOf("catch (failure)", StringComparison.Ordinal)..]);
        foreach (var previewField in new[]
        {
            "target_item_count", "store_kind_summary", "current_state", "target_items", "capture_expiry_policy_summary",
            "retained_metadata_impact", "active_cleanup_exclusion_conflicts", "backup_non_purge_warning_code",
            "expected_state_version", "target_item_set_digest", "preview_digest", "confirmation_expires_at",
        })
        {
            Assert.Contains(previewField, script);
        }

        Assert.Matches("(?s)confirmationToken = \\(await requestJson\\(\\\"/api/retention/v1/confirmations\\\".*?\\)\\)\\.confirmation_token;.*?requestJson\\(\\\"/api/retention/v1/mutations\\\".*?finally \\{\\s*confirmationToken = null;", script);

        foreach (var forbidden in new[] { "innerHTML", "localStorage", "sessionStorage", "console.", "setInterval", "setTimeout", "/api/session-workspace", "window.location.search" })
        {
            Assert.DoesNotContain(forbidden, script);
        }
    }

    [Fact]
    public async Task MonitorCss_IsServed()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/monitor.css");
        var css = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(".retention-dialog", css);
        Assert.Contains(".retention-operation-destructive", css);
        Assert.Contains(".retention-warning", css);
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
    public async Task TracesPage_RendersMasterDetailTableWithToolbarAndPreview()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawWithSensitiveMarkers(temp);
        await using var host = await StartHostAsync(temp);

        var traces = await host.Client.GetStringAsync("/traces?period=all");

        // Sprint18 §6.2: toolbar filters, grid table with token column, and the
        // selection-driven preview panel.
        Assert.Contains("trace-list-table", traces);
        Assert.Contains("trace-search", traces);
        Assert.Contains("tracelist-toolbar", traces);
        Assert.Contains("trace-preview", traces);
        Assert.Contains("trace-row", traces);
        Assert.Contains("token-heat", traces);
        // Default sort is tokens-descending (the sortable token header is marked).
        Assert.Contains("data-sort-key=\"tokens\" aria-sort=\"descending\"", traces);
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
        // Tokens are the Sprint18 handoff §10 hex values (D042 C2).
        Assert.Contains("/vendor/fonts/", css);
        Assert.Contains("--monitor-bg: #14171e", css);
        Assert.Contains("--monitor-accent: #4da3e8", css);
        foreach (var cdn in new[] { "googleapis.com", "gstatic.com", "cdn.jsdelivr.net", "unpkg.com" })
        {
            Assert.DoesNotContain(cdn, css);
            Assert.DoesNotContain(cdn, index);
        }
    }

    [Fact]
    public async Task FlowScript_UsesOnlySanitizedSpanApi()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var script = await host.Client.GetStringAsync("/monitor-flow.js");

        Assert.Contains("/api/monitor/traces/", script);
        Assert.Contains("next_cursor", script);
        Assert.Contains("parent_span_id", script);
        Assert.Contains("textContent", script);
        Assert.DoesNotContain("/raw", script);
        Assert.DoesNotContain("Html.Raw", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public async Task WaterfallAndCachePanelScripts_AreSanitizedRenderers()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var waterfall = await host.Client.GetStringAsync("/monitor-waterfall.js");
        var cachePanel = await host.Client.GetStringAsync("/monitor-cache-panel.js");

        Assert.Contains("total_tokens", waterfall);
        Assert.Contains("textContent", waterfall);
        Assert.Contains("cache_read_tokens", cachePanel);
        Assert.Contains("cache_creation_tokens", cachePanel);
        Assert.Contains("input_tokens", cachePanel);
        Assert.Contains("textContent", cachePanel);
        foreach (var script in new[] { waterfall, cachePanel })
        {
            Assert.DoesNotContain("fetch(", script);
            Assert.DoesNotContain("/raw", script);
            Assert.DoesNotContain("innerHTML", script);
        }
    }

    [Fact]
    public async Task MonitorViewsScript_IsRetired_Returns404()
    {
        using var temp = new MonitorTempDirectory();
        EnsureSchema(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/monitor-views.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void EnsureSchema(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
    }

    private static long SeedRawWithSensitiveMarkers(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
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

    private static long SeedRepositoryMetadataDiagnostics(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        return store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-repository-metadata-diagnostics",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: RepositoryMetadataPayload));
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

    private const string RepositoryMetadataPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"vcs.repository.name","value":{"stringValue":"PRIVATE_REPOSITORY_VALUE_MARKER"}},
          {"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/PRIVATE_OWNER_VALUE_MARKER/PRIVATE_URL_REPOSITORY_MARKER"}},
          {"key":"vcs.owner.name","value":{"stringValue":"private-marker@example.com"}},
          {"key":"unsafe repository key","value":{"stringValue":"C:\\private\\repository"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-repository-metadata-diagnostics","spanId":"1111111111111111","name":"diagnostic","attributes":[
            {"key":"vcs.ref.head.revision","value":{"stringValue":"PRIVATE_REVISION_VALUE_MARKER"}}
          ],"events":[{"name":"event","attributes":[
            {"key":"workspace.name","value":{"stringValue":"PRIVATE_WORKSPACE_VALUE_MARKER"}}
          ]}]}
        ]}]}]}
        """;

}

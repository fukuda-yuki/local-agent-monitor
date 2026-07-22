using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class SanitizedImportUiTests
{
    [Fact]
    public async Task Page_IsJapaneseNoStoreAndRejectsCrossSiteRead()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);

        using var response = await host.Client.GetAsync("/sanitized-import");
        var body = await response.Content.ReadAsStringAsync();
        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, "/sanitized-import");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("サニタイズ済み証拠を取り込む", body, StringComparison.Ordinal);
        Assert.Contains("/monitor-sanitized-import.js", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal("{\"error\":\"cross_origin_forbidden\"}", await crossSite.Content.ReadAsStringAsync());
        Assert.Equal("no-store", crossSite.Headers.CacheControl?.ToString());
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_FileChangeInvalidatesPreviewThenExplicitCommitRefreshesHistory()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        var alternate = Path.Combine(temp.Path, "selected-again.zip");
        File.Copy(golden, alternate);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var posts = new List<IRequest>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/api/sanitized-import/v1/", StringComparison.Ordinal))
                posts.Add(request);
        };

        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#sanitized-import-preview-button")).ToBeDisabledAsync();
        Assert.Empty(posts);

        var input = page.Locator("#sanitized-import-file");
        await input.SetInputFilesAsync(golden);
        var firstPreviewResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/previews", StringComparison.Ordinal));
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        Assert.Equal(200, (await firstPreviewResponse).Status);
        await Expect(page.Locator("#sanitized-import-preview")).ToBeVisibleAsync();
        await Expect(page.Locator("#sanitized-import-commit-button")).ToBeEnabledAsync();
        await Expect(page.Locator("#sanitized-import-preview-content")).ToContainTextAsync("identity-projection");

        await input.SetInputFilesAsync(alternate);
        await Expect(page.Locator("#sanitized-import-preview")).ToBeHiddenAsync();
        await Expect(page.Locator("#sanitized-import-commit-button")).ToBeDisabledAsync();
        var secondPreviewResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/previews", StringComparison.Ordinal)
            && posts.Count(request => request.Url.EndsWith("/api/sanitized-import/v1/previews", StringComparison.Ordinal)) == 2);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        Assert.Equal(200, (await secondPreviewResponse).Status);

        var commitReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/sanitized-import/v1/imports", async route =>
        {
            if (route.Request.Method == "POST")
            {
                commitReached.TrySetResult(true);
                await releaseCommit.Task;
            }
            await route.ContinueAsync();
        });
        var commitResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/imports", StringComparison.Ordinal));
        await page.Locator("#sanitized-import-commit-button").ClickAsync();
        await commitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(input).ToBeDisabledAsync();
        releaseCommit.TrySetResult(true);
        Assert.Equal(201, (await commitResponse).Status);

        await Expect(page.Locator("#sanitized-import-result")).ToBeVisibleAsync();
        await Expect(input).ToBeEnabledAsync();
        await Expect(page.Locator("#sanitized-import-result-content")).ToContainTextAsync("committed");
        await Expect(page.Locator("#sanitized-import-result-content")).ToContainTextAsync("対象レコード");
        await Expect(page.Locator("#sanitized-import-result-content")).ToContainTextAsync("graph state 更新");
        await Expect(page.Locator("#sanitized-import-history tbody tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#sanitized-import-history")).ToContainTextAsync("スキップ");
        Assert.Equal(2, posts.Count(request => request.Url.EndsWith("/api/sanitized-import/v1/previews", StringComparison.Ordinal)));
        var importRequest = Assert.Single(posts, request => request.Url.EndsWith("/api/sanitized-import/v1/imports", StringComparison.Ordinal));
        Assert.Equal("application/zip", await importRequest.HeaderValueAsync("Content-Type"));
        Assert.Equal("local-monitor", await importRequest.HeaderValueAsync("x-monitor-csrf"));
        Assert.Equal(64, (await importRequest.HeaderValueAsync("X-Sanitized-Import-Preview-Digest"))?.Length);
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_AbortedPreviewAndOverlappingHistoryCannotOverwriteNewerState()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        var alternate = Path.Combine(temp.Path, "new-selection.zip");
        File.Copy(golden, alternate);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var firstPreviewReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPreview = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHistoryReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstHistory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHistoryFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewOrdinal = 0;
        var historyOrdinal = 0;
        await page.RouteAsync("**/api/sanitized-import/v1/previews", async route =>
        {
            if (Interlocked.Increment(ref previewOrdinal) == 1)
            {
                firstPreviewReached.TrySetResult(true);
                await releaseFirstPreview.Task;
            }
            try { await route.ContinueAsync(); }
            catch (PlaywrightException) { }
        });
        await page.RouteAsync("**/api/sanitized-import/v1/imports*", async route =>
        {
            if (route.Request.Method != "GET")
            {
                await route.ContinueAsync();
                return;
            }
            if (Interlocked.Increment(ref historyOrdinal) == 1)
            {
                firstHistoryReached.TrySetResult(true);
                await releaseFirstHistory.Task;
                try { await route.FulfillAsync(HistoryResponse("old-import")); }
                catch (PlaywrightException) { }
                finally { firstHistoryFinished.TrySetResult(true); }
                return;
            }
            await route.FulfillAsync(HistoryResponse("new-import"));
        });

        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await firstHistoryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var input = page.Locator("#sanitized-import-file");
        await input.SetInputFilesAsync(golden);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        await firstPreviewReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input.SetInputFilesAsync(alternate);
        releaseFirstPreview.TrySetResult(true);

        await Expect(page.Locator("#sanitized-import-preview")).ToBeHiddenAsync();
        await Expect(page.Locator("#sanitized-import-error")).ToBeHiddenAsync();
        await Expect(page.Locator("#sanitized-import-preview-button")).ToBeEnabledAsync();
        var currentPreviewResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/previews", StringComparison.Ordinal) && response.Status == 200);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        await currentPreviewResponse;
        await Expect(page.Locator("#sanitized-import-commit-button")).ToBeEnabledAsync();
        var commitResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/imports", StringComparison.Ordinal) && response.Request.Method == "POST");
        await page.Locator("#sanitized-import-commit-button").ClickAsync();
        Assert.Equal(201, (await commitResponse).Status);

        await Expect(page.Locator("#sanitized-import-history tbody")).ToContainTextAsync("new-import");
        releaseFirstHistory.TrySetResult(true);
        await firstHistoryFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(page.Locator("#sanitized-import-history tbody")).ToContainTextAsync("new-import");
        await Expect(page.Locator("#sanitized-import-history tbody")).Not.ToContainTextAsync("old-import");
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_ConfirmedCommitRemainsSuccessfulWhenHistoryRefreshFails()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var initialHistory = page.WaitForResponseAsync(response =>
            response.Url.Contains("/api/sanitized-import/v1/imports?limit=50", StringComparison.Ordinal));
        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await initialHistory;
        var historyOrdinal = 0;
        await page.RouteAsync("**/api/sanitized-import/v1/imports*", async route =>
        {
            if (route.Request.Method == "GET" && Interlocked.Increment(ref historyOrdinal) == 1)
                await route.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{\"error\":\"import_store_unavailable\"}" });
            else
                await route.ContinueAsync();
        });

        await page.Locator("#sanitized-import-file").SetInputFilesAsync(golden);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        await Expect(page.Locator("#sanitized-import-commit-button")).ToBeEnabledAsync();
        var commitResponse = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/sanitized-import/v1/imports", StringComparison.Ordinal) && response.Request.Method == "POST");
        await page.Locator("#sanitized-import-commit-button").ClickAsync();
        Assert.Equal(201, (await commitResponse).Status);

        await Expect(page.Locator("#sanitized-import-result")).ToBeVisibleAsync();
        await Expect(page.Locator("#sanitized-import-live")).ToHaveTextAsync("取り込み結果を確認しました。");
        await Expect(page.Locator("#sanitized-import-error")).ToContainTextAsync("取り込みは確定しましたが、履歴を更新できませんでした");
        await page.Locator("#sanitized-import-history-refresh").ClickAsync();
        await Expect(page.Locator("#sanitized-import-error")).ToBeHiddenAsync();
        await Expect(page.Locator("#sanitized-import-history tbody tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#sanitized-import-live")).ToHaveTextAsync("取り込み結果を確認しました。");
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_PreviewRendersBoundedSourceAndEvidenceDetailsAsInertText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        await page.RouteAsync("**/api/sanitized-import/v1/imports*", route => route.FulfillAsync(EmptyHistoryResponse()));
        await page.RouteAsync("**/api/sanitized-import/v1/previews", route => route.FulfillAsync(PreviewResponse(canCommit: false, includeConflict: true)));

        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#sanitized-import-file").SetInputFilesAsync(golden);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();

        var content = page.Locator("#sanitized-import-preview-content");
        await Expect(content).ToContainTextAsync("sanitized-evidence-manifest.v1");
        await Expect(content).ToContainTextAsync("2026-07-22T10:11:12.0000000Z");
        await Expect(content).ToContainTextAsync("github-copilot-chat / agent-1.2.3");
        await Expect(content).ToContainTextAsync("2026-07-20T00:00:00.0000000Z");
        await Expect(content).ToContainTextAsync("instruction findings");
        await Expect(content).ToContainTextAsync("complete: 2");
        await Expect(content).ToContainTextAsync("projection: projection-v7");
        await Expect(content).ToContainTextAsync("identity-projection");
        await Expect(content).ToContainTextAsync("署名・出所・認可・source-store provenance は未証明");
        await Expect(content).ToContainTextAsync("競合の詳細（表示 1 / 1）");
        await Expect(content).ToContainTextAsync("conflict-record-1");
        await Expect(content).ToContainTextAsync(new string('a', 64));
        await Expect(content).ToContainTextAsync(new string('b', 64));
        await Expect(content).ToContainTextAsync("manifest の missing / external 宣言（表示 256 / 300）");
        await Expect(content).ToContainTextAsync("取り込み先で現在未解決の参照（表示 256 / 300）");
        await Expect(content).ToContainTextAsync("<img src=x onerror=window.__importPreviewExecuted=true>");
        await Expect(content.Locator(".monitor-table-wrapper").Nth(1).Locator("tbody tr")).ToHaveCountAsync(256);
        Assert.Equal(0, await content.Locator("img").CountAsync());
        Assert.False(await page.EvaluateAsync<bool>("() => window.__importPreviewExecuted === true"));
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_DeterministicCommitRejectionExplicitlySaysNothingWasCommitted()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        await page.RouteAsync("**/api/sanitized-import/v1/imports*", route => route.Request.Method == "POST"
            ? route.FulfillAsync(new() { Status = 409, ContentType = "application/json", Body = "{\"error\":\"preview_changed\"}" })
            : route.FulfillAsync(EmptyHistoryResponse()));
        await page.RouteAsync("**/api/sanitized-import/v1/previews", route => route.FulfillAsync(PreviewResponse(canCommit: true, includeConflict: false)));

        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#sanitized-import-file").SetInputFilesAsync(golden);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        await page.Locator("#sanitized-import-commit-button").ClickAsync();

        var error = page.Locator("#sanitized-import-error");
        await Expect(error).ToContainTextAsync("取り込みは確定されませんでした（preview_changed）");
        await Expect(error).Not.ToContainTextAsync("確定結果を確認できませんでした");
        await Expect(page.Locator("#sanitized-import-live")).ToHaveTextAsync("取り込みは確定されませんでした。");
        await Expect(page.Locator("#sanitized-import-result")).ToBeHiddenAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Browser_UnparseableCommitResponseKeepsOutcomeAmbiguous()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var golden = Path.Combine(SanitizedImportServiceTests.FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        await page.RouteAsync("**/api/sanitized-import/v1/imports*", route => route.Request.Method == "POST"
            ? route.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{" })
            : route.FulfillAsync(EmptyHistoryResponse()));
        await page.RouteAsync("**/api/sanitized-import/v1/previews", route => route.FulfillAsync(PreviewResponse(canCommit: true, includeConflict: false)));

        await page.GotoAsync($"{host.Url}/sanitized-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#sanitized-import-file").SetInputFilesAsync(golden);
        await page.Locator("#sanitized-import-preview-button").ClickAsync();
        await page.Locator("#sanitized-import-commit-button").ClickAsync();

        var error = page.Locator("#sanitized-import-error");
        await Expect(error).ToContainTextAsync("確定結果を確認できませんでした（invalid_response）");
        await Expect(error).Not.ToContainTextAsync("取り込みは確定されませんでした");
    }

    private static RouteFulfillOptions HistoryResponse(string importId) => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = $$"""
            {"schema_version":"sanitized-import-history.v1","items":[{"import_id":"{{importId}}","archive_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","preview_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","status":"committed","eligible_records":1,"new_records":1,"updated_records":0,"skipped_records":0,"rejected_records":0,"duplicate_records":0,"conflict_records":0,"graph_nodes":3,"graph_declarations":0,"graph_state_updates":0,"graph_edges":2,"raw_retention_items":0,"migration":{"version":1,"chain":"sanitized-evidence-bundle.v1->sanitized-import-store.v1","step":"identity-projection","chain_sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","lossy":false},"source_snapshot_id":"snapshot-history","source_local_monitor_version":"monitor-history","imported_at":"2026-07-23T00:00:00.0000000Z"}]}
            """,
    };

    private static RouteFulfillOptions EmptyHistoryResponse() => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = "{\"schema_version\":\"sanitized-import-history.v1\",\"items\":[]}",
    };

    private static RouteFulfillOptions PreviewResponse(bool canCommit, bool includeConflict) => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = JsonSerializer.Serialize(new
        {
            success = true,
            error_code = (string?)null,
            schema_version = "sanitized-import-preview.v1",
            preview_id = new string('c', 64),
            preview_digest = new string('d', 64),
            archive_sha256 = new string('e', 64),
            manifest_schema_version = "sanitized-evidence-manifest.v1",
            bundle_schema_version = "sanitized-evidence-bundle.v1",
            bundle_profile = "sanitized-evidence",
            compatibility = "compatible",
            migration = new
            {
                version = 1,
                chain = "sanitized-evidence-bundle.v1->sanitized-import-store.v1",
                step = "identity-projection",
                chain_sha256 = new string('f', 64),
                lossy = false,
            },
            source_snapshot_id = "snapshot-ui-detail",
            source_local_monitor_version = "monitor-9.9.0",
            source_created_at = "2026-07-22T10:11:12.0000000Z",
            source_agent_versions = new[] { new { source_surface = "github-copilot-chat", version = "agent-1.2.3" } },
            selection = new
            {
                session_ids = new[] { "session-ui-1" },
                trace_ids = new[] { "trace-ui-1" },
                source_surfaces = new[] { "github-copilot-chat" },
                repository_names = new[] { "repository-ui" },
                workspace_labels = new[] { "workspace-ui" },
                receipt_types = new[] { "repository_metadata_projection" },
                start_inclusive = "2026-07-20T00:00:00.0000000Z",
                end_exclusive = "2026-07-23T00:00:00.0000000Z",
            },
            date_range = new { start = "2026-07-20T00:00:00.0000000Z", end = "2026-07-22T23:59:59.0000000Z" },
            source_labels = new[] { new { repository_name = "repository-ui", workspace_label = "workspace-ui", repo_snapshot = "snapshot-label-ui" } },
            record_counts = new Dictionary<string, int> { ["repository_metadata_projection"] = 2 },
            capabilities = new
            {
                instruction_findings = "available",
                alert_receipts = "missing",
                historical_instruction_analysis = "unavailable",
                historical_efficiency_analysis = "unavailable",
                alert_center = "unavailable",
            },
            completeness_distribution = new Dictionary<string, int> { ["complete"] = 2 },
            content_state_distribution = new Dictionary<string, int> { ["not_captured"] = 2 },
            retention_state_distribution = new Dictionary<string, int> { ["retained_by_policy"] = 2 },
            processing_versions = new Dictionary<string, string> { ["projection"] = "projection-v7", ["scanner"] = "scanner-v1" },
            total_records = 2,
            total_uncompressed_bytes = 4096,
            eligible_records = 2,
            new_records = includeConflict ? 0 : 2,
            updated_records = 0,
            skipped_records = 0,
            rejected_records = includeConflict ? 2 : 0,
            duplicate_records = 0,
            conflict_records = includeConflict ? 1 : 0,
            graph_state_updates = 0,
            conflicts = includeConflict
                ? new[] { new { record_type = "repository_metadata_projection", record_id = "conflict-record-1", incoming_sha256 = new string('a', 64), existing_sha256 = new string('b', 64) } }
                : Array.Empty<object>(),
            manifest_declaration_count = includeConflict ? 300 : 0,
            manifest_declarations = includeConflict
                ? Enumerable.Range(0, 300).Select(index => new
                {
                    node_kind = "record:alert_receipt",
                    source_id = index == 0
                        ? "<img src=x onerror=window.__importPreviewExecuted=true>"
                        : $"declared-record-{index:D3}",
                    state = "missing",
                }).ToArray()
                : Array.Empty<object>(),
            unresolved_reference_count = includeConflict ? 300 : 0,
            unresolved_references = includeConflict
                ? Enumerable.Range(0, 300).Select(index => new
                {
                    node_kind = "record:alert_receipt",
                    source_id = index == 0
                        ? "<img src=x onerror=window.__importPreviewExecuted=true>"
                        : $"missing-record-{index:D3}",
                    state = "external",
                }).ToArray()
                : Array.Empty<object>(),
            expected_changes = new
            {
                records = includeConflict ? 0 : 2,
                origins = includeConflict ? 0 : 2,
                graph_nodes = includeConflict ? 0 : 3,
                graph_declarations = 0,
                graph_state_updates = 0,
                graph_edges = includeConflict ? 0 : 2,
                history_rows = includeConflict ? 0 : 1,
                raw_retention_items = 0,
            },
            can_commit = canCommit,
        }),
    };

    private static Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp) => MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    });
}

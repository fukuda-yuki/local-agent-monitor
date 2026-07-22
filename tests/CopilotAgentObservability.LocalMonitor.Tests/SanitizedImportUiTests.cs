using System.Net;
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
        await Expect(page.Locator("#sanitized-import-history tbody tr")).ToHaveCountAsync(1);
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

    private static RouteFulfillOptions HistoryResponse(string importId) => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = $$"""
            {"schema_version":"sanitized-import-history.v1","items":[{"imported_at":"2026-07-23T00:00:00.0000000Z","import_id":"{{importId}}","new_records":1,"duplicate_records":0,"status":"committed"}]}
            """,
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

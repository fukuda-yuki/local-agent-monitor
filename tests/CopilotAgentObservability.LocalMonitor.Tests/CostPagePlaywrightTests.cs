using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class CostPagePlaywrightTests
{
    private const string SessionId = "0198f5b8-0c00-7000-8000-000000000001";
    private const string EstimateId =
        "pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact(Timeout = 60_000)]
    public async Task CostPage_RendersExactCompleteAndPartialFactsWithoutDefinitiveClaims()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallStorageTripwires(page);
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        await Expect(page.Locator("section[aria-labelledby='cost-overall-heading'] #cost-overall-heading")).ToHaveCountAsync(1);
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("50.00%");
        await Expect(page.Locator("#cost-range-totals")).ToContainTextAsync("12.50 USD");
        await Expect(page.Locator("#cost-range-totals")).ToContainTextAsync("1.25 USD");
        await Expect(page.Locator("#cost-range-totals")).ToContainTextAsync("provisional");
        await Expect(page.Locator("#cost-daily-trend")).ToContainTextAsync("2026-07-23 UTC");
        await Expect(page.Locator("#cost-groups")).ToContainTextAsync("input");
        await Expect(page.Locator("#cost-groups")).ToContainTextAsync("partial");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("partial");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("unknown_model");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("stale");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("failed");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("local_override");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("estimated_cost_not_invoice.v1");
        await Expect(page.Locator("#cost-session a[href^='/api/costs/']")).ToHaveCountAsync(1);
        await Expect(page.Locator("#cost-context-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#cost-live")).ToContainTextAsync("exact estimate");
        Assert.False(await page.EvaluateAsync<bool>("() => window.__costStorageTouched === true"));
        Assert.False(await page.EvaluateAsync<bool>("() => window.__costServiceWorkerTouched === true"));
        Assert.False(await page.EvaluateAsync<bool>("() => window.__costDatabaseTouched === true"));
        Assert.False(await page.EvaluateAsync<bool>("() => window.__costCacheTouched === true"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));
        Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => location.hash"));
        Assert.Equal(0, await page.Locator("#cost-catalog a, #cost-catalog img, #cost-catalog script").CountAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_IncompleteSnapshotWithholdsTotalsAndAnnouncesLowerBound()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(Configuration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        var analytics = CompleteAnalyticsWithCursor;
        string? analyticsUrl = null;
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
        {
            analyticsUrl = route.Request.Url;
            return route.FulfillAsync(Json(analytics));
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("label:has(#cost-filter-from)")).ToContainTextAsync("開始（UTC）");
        await Expect(page.Locator("label:has(#cost-filter-to)")).ToContainTextAsync("終了（UTC、含まない）");
        await Expect(page.Locator("label:has(#cost-filter-status)")).ToContainTextAsync("状態");
        await Expect(page.Locator("label:has(#cost-filter-repository)")).ToContainTextAsync("Repository");
        await Expect(page.Locator("label:has(#cost-filter-workspace)")).ToContainTextAsync("Workspace");
        await page.Locator("#cost-filter-status").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#cost-filter-registry")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#cost-filter-repository")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#cost-filter-workspace")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.Locator("#cost-filter-repository")).ToBeFocusedAsync();
        await Expect(page.Locator("#cost-range-total-list")).ToContainTextAsync("12.50 USD");
        await Expect(page.Locator("#cost-groups-next")).ToBeVisibleAsync();
        analytics = IncompleteAnalytics;
        await page.Locator("#cost-filter-mode").SelectOptionAsync("github_ai_credits");
        await page.Locator("#cost-filter-status").SelectOptionAsync("failed");
        await page.Locator("#cost-filters button[type='submit']").ClickAsync();

        await Expect(page.Locator("#cost-incomplete")).ToBeVisibleAsync();
        await Expect(page.Locator("#cost-incomplete")).ToContainTextAsync("2,001");
        await Expect(page.Locator("#cost-incomplete")).ToContainTextAsync("断定しません");
        await Expect(page.Locator("#cost-range-totals")).ToBeHiddenAsync();
        await Expect(page.Locator("#cost-daily-trend")).ToBeHiddenAsync();
        await Expect(page.Locator("#cost-overall")).Not.ToContainTextAsync("0 件");
        await Expect(page.Locator("section[aria-labelledby='cost-overall-heading'] #cost-overall-heading")).ToHaveCountAsync(1);
        await Expect(page.Locator("#cost-range-total-list")).ToBeEmptyAsync();
        await Expect(page.Locator("#cost-daily-list")).ToBeEmptyAsync();
        await Expect(page.Locator("#cost-groups-next")).ToBeHiddenAsync();
        Assert.Equal(string.Empty, await page.Locator("#cost-groups-next").GetAttributeAsync("data-cursor"));
        Assert.Contains("billing_mode=github_ai_credits", analyticsUrl, StringComparison.Ordinal);
        await Expect(page.Locator("#cost-live")).ToContainTextAsync("incomplete");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_PreviewUsesCanonicalCsrfAgainstRealHost()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(
            $"{host.Url}/costs",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-root")).ToHaveAttributeAsync("data-read-state", "fresh");
        await Expect(page.Locator("#cost-preview")).ToBeEnabledAsync();

        var request = await page.RunAndWaitForRequestAsync(
            () => page.Locator("#cost-preview").ClickAsync(),
            "**/api/costs/v1/configuration/preview");

        Assert.Equal("local-monitor", await request.HeaderValueAsync("x-monitor-csrf"));
        await Expect(page.Locator("#cost-preview-result")).ToContainTextAsync("0 Sessions");
        await Expect(page.Locator("#cost-commit")).ToBeEnabledAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_SerializesPreviewCommitAndRecalculationPolling()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page);
        var previewPosts = 0;
        var commitPosts = 0;
        var recalculationPosts = 0;
        var polls = 0;
        string? commitBody = null;
        string? recalculationBody = null;
        await page.RouteAsync("**/api/costs/v1/configuration/preview", async route =>
        {
            await AssertCanonicalMutation(route.Request);
            previewPosts++;
            Assert.Contains("\"schema_version\":\"cost.configuration-preview-request.v1\"", route.Request.PostData);
            Assert.Contains("\"rule_id\":\"session-estimated-cost-threshold\"", route.Request.PostData);
            Assert.Contains("\"rule_id\":\"daily-estimated-cost-threshold\"", route.Request.PostData);
            Assert.Contains("\"rule_id\":\"period-estimated-cost-threshold\"", route.Request.PostData);
            var consumed = CostConfigurationPreviewRequestConsumerV1.Consume(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            await route.FulfillAsync(Json(CreatePreview(consumed.Value!)));
        });
        await page.RouteAsync("**/api/costs/v1/configurations", async route =>
        {
            await AssertCanonicalMutation(route.Request);
            commitPosts++;
            commitBody ??= route.Request.PostData;
            Assert.Equal(commitBody, route.Request.PostData);
            var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            if (commitPosts == 1)
            {
                await route.FulfillAsync(Json("{", 201));
                return;
            }
            var result = CostConfigurationCommitConsumerV1.CreateResult(
                consumed.Value!.Configuration.ConfigurationId,
                2,
                consumed.Value.CatalogSha256);
            await route.FulfillAsync(Json(
                Encoding.UTF8.GetString(CostConfigurationCommitConsumerV1.SerializeResult(result)),
                201));
        });
        await page.RouteAsync("**/api/costs/v1/recalculations", async route =>
        {
            await AssertCanonicalMutation(route.Request);
            recalculationPosts++;
            recalculationBody ??= route.Request.PostData;
            Assert.Equal(recalculationBody, route.Request.PostData);
            Assert.Contains("\"schema_version\":\"cost.recalculation-request.v1\"", route.Request.PostData);
            Assert.Contains("\"scope_kind\":\"session\"", route.Request.PostData);
            Assert.Contains("\"scope_kind\":\"utc_day\"", route.Request.PostData);
            Assert.Contains("\"scope_kind\":\"rolling_period\"", route.Request.PostData);
            await route.FulfillAsync(Json(recalculationPosts == 1 ? "{" : RecalculationRunning, 202));
        });
        await page.RouteAsync("**/api/costs/v1/recalculations/0198f5b8-0c00-7000-8000-000000000099", async route =>
        {
            polls++;
            await route.FulfillAsync(Json(RecalculationSucceeded));
        });

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#cost-config-surface").FillAsync("github-copilot-vscode");
        await page.Locator("#cost-config-version").FillAsync("1.0.4");
        await page.Locator("#cost-config-adapter").FillAsync("synthetic-pricing-v1");
        await page.Locator("#cost-config-entry").SelectOptionAsync("github-copilot:gpt-4.1:credit");
        await Expect(page.Locator("#cost-config-provider")).ToHaveValueAsync("github_copilot");
        await Expect(page.Locator("#cost-config-mode")).ToHaveValueAsync("github_ai_credits");
        await Expect(page.Locator("#cost-config-route")).ToHaveValueAsync("credit_consuming_interaction");
        await page.Locator("#cost-budget-enabled").CheckAsync();
        await page.Locator("#cost-budget-daily-enabled").CheckAsync();
        await page.Locator("#cost-budget-period-enabled").CheckAsync();
        await page.Locator("#cost-budget-period-days").FillAsync("30");
        await page.Locator("#cost-scope-utc-day").CheckAsync();
        await page.Locator("#cost-scope-period").CheckAsync();
        await page.Locator("#cost-preview").ClickAsync();
        await Expect(page.Locator("#cost-preview-result")).ToContainTextAsync("1 Session");
        await Expect(page.Locator("#cost-commit")).ToBeEnabledAsync();
        await page.Locator("#cost-commit").ClickAsync();
        await Expect(page.Locator("#cost-live")).ToContainTextAsync("committed");

        await page.Locator("#cost-recalculate").ClickAsync();
        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync("succeeded");
        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync("estimated");
        Assert.Equal(1, previewPosts);
        Assert.Equal(2, commitPosts);
        Assert.Equal(2, recalculationPosts);
        Assert.True(polls >= 1);
        await Expect(page.Locator("#cost-preview")).ToBeEnabledAsync();
        await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_PreviewPreservesEveryHydratedSourceAndBudgetEntry()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(PreservedConfiguration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
            route.FulfillAsync(Json(CompleteAnalytics)));
        CostConfigurationPreviewRequestV1? posted = null;
        await page.RouteAsync("**/api/costs/v1/configuration/preview", async route =>
        {
            var consumed = CostConfigurationPreviewRequestConsumerV1.Consume(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            posted = consumed.Value;
            await route.FulfillAsync(Json(CreatePreview(consumed.Value!)));
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.GetByLabel("Source surface", new() { Exact = true })).ToHaveValueAsync("a-");
        await Expect(page.GetByLabel("configure session-estimated-cost-threshold", new() { Exact = true })).ToBeCheckedAsync();
        await Expect(page.GetByLabel("configure daily-estimated-cost-threshold", new() { Exact = true })).ToBeCheckedAsync();
        await Expect(page.GetByLabel("configure period-estimated-cost-threshold", new() { Exact = true })).ToBeCheckedAsync();
        await page.Locator("#cost-preview").ClickAsync();
        await Expect(page.Locator("#cost-preview-result")).ToContainTextAsync("1 Session");

        Assert.NotNull(posted);
        Assert.Equal(3, posted.SourceEntries.Count);
        Assert.Equal(3, posted.BudgetEntries.Count);
        Assert.Contains(posted.SourceEntries, item => item.SourceSurface == "a_");
        Assert.Equal(
            ["a-", "a_", "github-copilot-vscode"],
            posted.SourceEntries.Select(item => item.SourceSurface));
        Assert.Contains(posted.BudgetEntries, item => item.RuleId == "period-estimated-cost-threshold" && item.WindowDays == 30);
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_CanExplicitlyReplaceExistingMappingsWithZeroSourcesAndBudgets()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(PreservedConfiguration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
            route.FulfillAsync(Json(CompleteAnalytics)));
        CostConfigurationPreviewRequestV1? posted = null;
        await page.RouteAsync("**/api/costs/v1/configuration/preview", async route =>
        {
            var consumed = CostConfigurationPreviewRequestConsumerV1.Consume(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            posted = consumed.Value;
            await route.FulfillAsync(Json(CreatePreview(consumed.Value!)));
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-recalculate")).ToBeDisabledAsync();
        Assert.Equal("not-allowed", await page.Locator("#cost-recalculate").EvaluateAsync<string>(
            "element => getComputedStyle(element).cursor"));
        await page.Locator("#cost-config-clear-sources").CheckAsync();
        await Expect(page.Locator("#cost-config-clear-sources")).ToHaveAttributeAsync(
            "aria-describedby",
            "cost-clear-sources-note");
        await Expect(page.Locator("#cost-config-surface")).ToBeDisabledAsync();
        await Expect(page.Locator("#cost-config-entry")).ToBeDisabledAsync();
        await page.Locator("#cost-budget-enabled").UncheckAsync();
        await page.Locator("#cost-budget-daily-enabled").UncheckAsync();
        await page.Locator("#cost-budget-period-enabled").UncheckAsync();
        await page.Locator("#cost-preview").ClickAsync();
        await Expect(page.Locator("#cost-preview-result")).ToContainTextAsync("1 Session");

        Assert.NotNull(posted);
        Assert.Empty(posted.SourceEntries);
        Assert.Empty(posted.BudgetEntries);
        await page.Locator("#cost-config-clear-sources").UncheckAsync();
        await Expect(page.Locator("#cost-config-surface")).ToBeEnabledAsync();
        await Expect(page.Locator("#cost-config-entry")).ToBeEnabledAsync();
    }

    [Theory(Timeout = 60_000)]
    [InlineData("matching", true)]
    [InlineData("unconfigured", false)]
    [InlineData("changed", false)]
    public async Task CostPage_RecalculationRequiresCanonicalMatchingConfiguration(
        string catalogState,
        bool expectedEnabled)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(ConfigurationForState(catalogState))));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
            route.FulfillAsync(Json(CompleteAnalytics)));
        await RouteSessionReads(page);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        if (expectedEnabled)
            await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();
        else
            await Expect(page.Locator("#cost-recalculate")).ToBeDisabledAsync();
        await Expect(page.Locator("#cost-config-state")).ToContainTextAsync(catalogState);
    }

    [Fact(Timeout = 60_000)]
    public async Task ContextualCostLinks_DoNotChangeTheTwoItemPrimaryNavigation()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var tracePage = await browser.NewPageAsync();
        await tracePage.GotoAsync(
            $"{host.Url}/traces/{MonitorRichTrace.TraceId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(tracePage.Locator("#trace-cost-link")).ToHaveAttributeAsync("href", "/costs");
        await Expect(tracePage.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
        await tracePage.CloseAsync();

        var diagnosticsPage = await browser.NewPageAsync();
        await diagnosticsPage.GotoAsync(
            $"{host.Url}/diagnostics?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(diagnosticsPage.Locator("#diagnostics-cost-link")).ToHaveAttributeAsync("href", "/costs");
        await Expect(diagnosticsPage.Locator("#doctor-session-cost-link")).ToHaveAttributeAsync(
            "href",
            $"/costs?session_id={SessionId}");
        await Expect(diagnosticsPage.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
        await diagnosticsPage.CloseAsync();

        var overviewPage = await browser.NewPageAsync();
        await overviewPage.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(overviewPage.Locator("#overview-cost-link")).ToHaveAttributeAsync("href", "/costs");
        await Expect(overviewPage.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_DoesNotRenderPreviewAcrossANewerReadGeneration()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        var previewReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/costs/v1/configuration/preview", async route =>
        {
            previewReached.SetResult();
            await releasePreview.Task;
            var consumed = CostConfigurationPreviewRequestConsumerV1.Consume(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            await route.FulfillAsync(Json(CreatePreview(consumed.Value!)));
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        await page.Locator("#cost-config-surface").FillAsync("github-copilot-vscode");
        await page.Locator("#cost-config-version").FillAsync("1.0.4");
        await page.Locator("#cost-config-adapter").FillAsync("synthetic-pricing-v1");
        await page.Locator("#cost-config-entry").SelectOptionAsync("github-copilot:gpt-4.1:credit");
        await page.Locator("#cost-preview").ClickAsync();
        await previewReached.Task;

        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        releasePreview.SetResult();

        await Expect(page.Locator("#cost-commit")).ToBeDisabledAsync();
        await Expect(page.Locator("#cost-preview-result")).Not.ToContainTextAsync("1 Session");
        await Expect(page.Locator("#cost-live")).ToContainTextAsync("superseded");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CostPage_LateCommitSuccessRefreshesWhileLateFailureDoesNotOverwriteNewerRead(
        bool commitSucceeds)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var analyticsReads = 0;
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(Configuration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
        {
            analyticsReads++;
            return route.FulfillAsync(Json(CompleteAnalytics));
        });
        await page.RouteAsync("**/api/costs/v1/configuration/preview", async route =>
        {
            var consumed = CostConfigurationPreviewRequestConsumerV1.Consume(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            await route.FulfillAsync(Json(CreatePreview(consumed.Value!)));
        });
        var commitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/costs/v1/configurations", async route =>
        {
            var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(
                Encoding.UTF8.GetBytes(route.Request.PostData!));
            Assert.Equal(CostConsumerStatus.Success, consumed.Status);
            commitReached.SetResult();
            await releaseCommit.Task;
            if (!commitSucceeds)
            {
                await route.FulfillAsync(Json("""{"error":"configuration_conflict"}""", 409));
                return;
            }
            var result = CostConfigurationCommitConsumerV1.CreateResult(
                consumed.Value!.Configuration.ConfigurationId,
                2,
                consumed.Value.CatalogSha256);
            await route.FulfillAsync(Json(
                Encoding.UTF8.GetString(CostConfigurationCommitConsumerV1.SerializeResult(result)),
                201));
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#cost-preview").ClickAsync();
        await Expect(page.Locator("#cost-commit")).ToBeEnabledAsync();
        await page.Locator("#cost-commit").ClickAsync();
        await commitReached.Task;
        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        Assert.Equal(2, analyticsReads);
        releaseCommit.SetResult();
        await Expect(page.Locator("#cost-config-surface")).ToBeEnabledAsync();

        Assert.Equal(commitSucceeds ? 3 : 2, analyticsReads);
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("committed");
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("結果を確認できません");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_AcceptedRecalculationStillRefreshesAfterNewerReadSupersedesPoll()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var configurationReads = 0;
        var catalogReads = 0;
        var analyticsReads = 0;
        var estimateHistoryReads = 0;
        var attemptHistoryReads = 0;
        var exactEstimateReads = 0;
        var refreshedReads = Enumerable.Range(0, 6)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
        {
            configurationReads++;
            if (configurationReads == 3) refreshedReads[0].SetResult();
            return route.FulfillAsync(Json(Configuration));
        });
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
        {
            catalogReads++;
            if (catalogReads == 3) refreshedReads[1].SetResult();
            return route.FulfillAsync(Json(Catalog));
        });
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
        {
            analyticsReads++;
            if (analyticsReads == 3) refreshedReads[2].SetResult();
            return route.FulfillAsync(Json(CompleteAnalytics));
        });
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates?*", route =>
        {
            estimateHistoryReads++;
            if (estimateHistoryReads == 3) refreshedReads[3].SetResult();
            return route.FulfillAsync(Json(SessionHistory));
        });
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/recalculations?*", route =>
        {
            attemptHistoryReads++;
            if (attemptHistoryReads == 3) refreshedReads[4].SetResult();
            return route.FulfillAsync(Json(SessionRecalculations));
        });
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates/{EstimateId}", route =>
        {
            exactEstimateReads++;
            if (exactEstimateReads == 3) refreshedReads[5].SetResult();
            return route.FulfillAsync(Json(ExactEstimate));
        });
        await page.RouteAsync("**/api/costs/v1/recalculations", route =>
            route.FulfillAsync(Json(RecalculationRunning, 202)));
        var pollReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            "**/api/costs/v1/recalculations/0198f5b8-0c00-7000-8000-000000000099",
            async route =>
            {
                pollReached.TrySetResult();
                await releasePoll.Task;
                await route.FulfillAsync(Json(RecalculationSucceeded));
            });

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();
        await page.Locator("#cost-recalculate").ClickAsync();
        await pollReached.Task;
        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        Assert.Equal(
            (2, 2, 2, 2, 2, 2),
            (configurationReads, catalogReads, analyticsReads, estimateHistoryReads, attemptHistoryReads, exactEstimateReads));
        releasePoll.SetResult();
        await Task.WhenAll(refreshedReads.Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();

        Assert.Equal(
            (3, 3, 3, 3, 3, 3),
            (configurationReads, catalogReads, analyticsReads, estimateHistoryReads, attemptHistoryReads, exactEstimateReads));
        await Expect(page.Locator("#cost-recalculation")).Not.ToContainTextAsync("succeeded");
        await Expect(page.Locator("#cost-recalculation")).Not.ToContainTextAsync("estimated");
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("recalculation succeeded");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_NeverTerminalRecalculationStopsAfterFortyPollsAndAllowsReadback()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates?*", route =>
            route.FulfillAsync(Json(SessionHistory)));
        var historyReads = 0;
        var historyReadback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/recalculations?*", route =>
        {
            historyReads++;
            if (historyReads == 2) historyReadback.SetResult();
            return route.FulfillAsync(Json(SessionRecalculations));
        });
        var recalculationPosts = 0;
        await page.RouteAsync("**/api/costs/v1/recalculations", route =>
        {
            recalculationPosts++;
            return route.FulfillAsync(Json(RecalculationRunning, 202));
        });
        var polls = 0;
        await page.RouteAsync(
            "**/api/costs/v1/recalculations/0198f5b8-0c00-7000-8000-000000000099",
            route =>
            {
                polls++;
                Assert.InRange(polls, 1, 40);
                return route.FulfillAsync(Json(RecalculationRunning));
            });

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();
        await page.Locator("#cost-recalculate").ClickAsync();

        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync(
            "polling_stopped · retryable",
            new LocatorAssertionsToContainTextOptions { Timeout = 8_000 });
        Assert.Equal(1, recalculationPosts);
        Assert.Equal(40, polls);
        await Expect(page.Locator("#cost-recalculate")).ToBeEnabledAsync();
        await Expect(page.Locator("#cost-recalculation")).Not.ToContainTextAsync("succeeded");
        await Expect(page.Locator("#cost-recalculation")).Not.ToContainTextAsync("failed");
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("recalculation succeeded");
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("recalculation failed");

        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await historyReadback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, historyReads);
        Assert.Equal(40, polls);
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_ReplacesBoundedCatalogEstimateAndAttemptPagesUsingExactCursors()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(Configuration)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
            route.FulfillAsync(Json(CompleteAnalytics)));
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates/{EstimateId}", route =>
            route.FulfillAsync(Json(ExactEstimate)));
        var catalogAdvanced = false;
        var estimatesAdvanced = false;
        var attemptsAdvanced = false;
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
        {
            catalogAdvanced = route.Request.Url.Contains("after=catalog-page", StringComparison.Ordinal);
            return route.FulfillAsync(Json(catalogAdvanced ? Catalog : CatalogWithNext));
        });
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates?*", route =>
        {
            estimatesAdvanced = route.Request.Url.Contains("after=estimate-page", StringComparison.Ordinal);
            return route.FulfillAsync(Json(estimatesAdvanced ? SessionHistory : SessionHistoryWithNext));
        });
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/recalculations?*", route =>
        {
            attemptsAdvanced = route.Request.Url.Contains("after=attempt-page", StringComparison.Ordinal);
            return route.FulfillAsync(Json(attemptsAdvanced ? SessionRecalculations : SessionRecalculationsWithNext));
        });

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-catalog-next")).ToBeVisibleAsync();
        await Expect(page.Locator("#cost-estimates-next")).ToBeVisibleAsync();
        await Expect(page.Locator("#cost-attempts-next")).ToBeVisibleAsync();

        await page.Locator("#cost-catalog-next").ClickAsync();
        await Expect(page.Locator("#cost-catalog-next")).ToBeHiddenAsync();
        await Expect(page.Locator("#cost-config-heading")).ToBeFocusedAsync();
        await page.Locator("#cost-estimates-next").ClickAsync();
        await Expect(page.Locator("#cost-estimates-next")).ToBeHiddenAsync();
        await Expect(page.Locator("#cost-estimate-history-heading")).ToBeFocusedAsync();
        await page.Locator("#cost-attempts-next").ClickAsync();
        await Expect(page.Locator("#cost-attempts-next")).ToBeHiddenAsync();
        await Expect(page.Locator("#cost-attempt-history-heading")).ToBeFocusedAsync();

        Assert.True(catalogAdvanced);
        Assert.True(estimatesAdvanced);
        Assert.True(attemptsAdvanced);
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_DoesNotRenderAnAbortedPollAsARecalculationFailure()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page);
        await page.RouteAsync("**/api/costs/v1/recalculations", route =>
            route.FulfillAsync(Json(RecalculationRunning, 202)));
        var pollReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            "**/api/costs/v1/recalculations/0198f5b8-0c00-7000-8000-000000000099",
            async route =>
            {
                pollReached.SetResult();
                await releasePoll.Task;
                await route.FulfillAsync(Json(RecalculationSucceeded));
            });

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#cost-recalculate").ClickAsync();
        await pollReached.Task;
        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#cost-overall")).ToContainTextAsync("2 / 4");
        releasePoll.SetResult();

        await Expect(page.Locator("#cost-recalculation")).Not.ToContainTextAsync("recalculation failed");
        await Expect(page.Locator("#cost-live")).Not.ToContainTextAsync("結果を確認できません");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_FiltersExactRepositoryWorkspaceAndSeparatesMixedRegistryCurrencyStates()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(Configuration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(Catalog)));
        string? analyticsUrl = null;
        var analyticsReads = 0;
        var filteredAnalyticsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/costs/v1/analytics?*", async route =>
        {
            analyticsUrl = route.Request.Url;
            analyticsReads++;
            var isFiltered = route.Request.Url.Contains("repository=Repo.Label", StringComparison.Ordinal);
            await route.FulfillAsync(Json(isFiltered ? FilteredRepositoryAnalytics : MixedRepositoryAnalytics));
            if (isFiltered) filteredAnalyticsRead.SetResult();
        });

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-group-rows tr")).ToHaveCountAsync(3);
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("registry-a");
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("4.00 USD");
        await Expect(page.Locator("#cost-group-rows tr").Nth(1)).ToContainTextAsync("registry-b");
        await Expect(page.Locator("#cost-group-rows tr").Nth(1)).ToContainTextAsync("4.50 USD");
        await Expect(page.Locator("#cost-group-rows tr").Nth(1)).ToContainTextAsync(
            "repository unknown / workspace unknown");
        await Expect(page.Locator("#cost-group-rows tr").Nth(2)).ToContainTextAsync(
            "repository (missing) / workspace (missing)");
        await Expect(page.Locator("#cost-group-rows tr").Nth(2)).ToContainTextAsync("currency (missing)");
        await page.Locator("#cost-filter-repository").FillAsync("Repo.Label");
        await page.Locator("#cost-filter-workspace").FillAsync("Workspace Label");
        await page.Locator("#cost-filters button[type='submit']").ClickAsync();
        await filteredAnalyticsRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(page.Locator("#cost-group-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("filtered-model");

        Assert.Contains("repository=Repo.Label", analyticsUrl, StringComparison.Ordinal);
        Assert.Contains("workspace=Workspace+Label", analyticsUrl, StringComparison.Ordinal);
        Assert.Equal(2, analyticsReads);
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("Repo.Label");
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("Workspace Label");
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("registry-a");
        await Expect(page.Locator("#cost-group-rows tr").Nth(0)).ToContainTextAsync("USD");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_RendersFullSessionEstimateAsCompleteNonzeroTotal()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page, FullEstimate, FullEstimateHistory);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("estimated / fresh");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("complete_total · 4.25 USD");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("1/1 categories");
        await Expect(page.Locator("#cost-exact-estimate")).Not.ToContainTextAsync("provisional");
        await Expect(page.Locator("#cost-exact-estimate")).Not.ToContainTextAsync("not-estimable");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_ExplainsPlanIncludedZeroAsZeroAdditionalCostAndShowsProvenance()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics, catalog: CatalogWithIncludedZero);
        await RouteSessionReads(page, PlanIncludedZeroEstimate, PlanIncludedZeroHistory);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("estimated / fresh");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("0 USD");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync(
            "追加コスト 0（plan/seat 自体の価格が 0 という意味ではありません）");
        await Expect(page.Locator("#cost-catalog")).ToContainTextAsync(
            "追加コスト 0（plan/seat 自体の価格が 0 という意味ではありません）");
        await Expect(page.Locator("#cost-exact-estimate")).Not.ToContainTextAsync("free");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync(CatalogSha);
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("reviewed 2026-07-20");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("stale after 2026-08-20");
    }

    [Theory(Timeout = 60_000)]
    [InlineData("subscription")]
    [InlineData("custom_enterprise")]
    public async Task CostPage_RendersSubscriptionAndCustomModesAsNotEstimable(string billingMode)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        var reason = billingMode == "subscription"
            ? "subscription_or_contract_unknown"
            : "custom_contract";
        var estimate = NotEstimableEstimate
            .Replace("__BILLING_MODE__", billingMode, StringComparison.Ordinal)
            .Replace("__REASON__", reason, StringComparison.Ordinal);
        var history = NotEstimableHistory
            .Replace("__BILLING_MODE__", billingMode, StringComparison.Ordinal)
            .Replace("__REASON__", reason, StringComparison.Ordinal);
        await RouteSessionReads(page, estimate, history);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("not-estimable / fresh");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync(billingMode);
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("not_applicable");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync(reason);
        await Expect(page.Locator("#cost-exact-estimate")).Not.ToContainTextAsync("0 USD");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_WarnsWhenEstimateCalculationUtcDateIsAfterRegistryStaleAfterDate()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page, StaleRegistryEstimate, StaleRegistryHistory);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-exact-estimate [role='note']")).ToContainTextAsync(
            "registry metadata warning");
        await Expect(page.Locator("#cost-exact-estimate [role='note']")).ToContainTextAsync(
            "calculation UTC date 2026-08-21 > stale after 2026-08-20");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_DoesNotWarnWhenCalculationUtcDateEqualsRegistryStaleAfterDate()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await RouteSessionReads(page, StaleBoundaryEstimate, StaleBoundaryHistory);

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}&estimate_id={EstimateId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("stale after 2026-08-20");
        await Expect(page.Locator("#cost-exact-estimate [role='note']")).ToHaveCountAsync(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_RetainsAtMostSixtyFourSourcesAndOneHundredCatalogEntries()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics, catalog: OversizedCatalog);

        await page.GotoAsync($"{host.Url}/costs", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-catalog article")).ToHaveCountAsync(164);
        await Expect(page.Locator("#cost-config-entry option")).ToHaveCountAsync(101);
        await Expect(page.Locator("#cost-catalog")).Not.ToContainTextAsync("source-64");
        await Expect(page.Locator("#cost-catalog")).Not.ToContainTextAsync("model-100");

        await page.EvaluateAsync(
            """
            () => {
              const select = document.getElementById("cost-config-entry");
              const option = document.createElement("option");
              option.value = "entry-100";
              option.textContent = "synthetic unretained entry";
              select.append(option);
              select.value = option.value;
              select.dispatchEvent(new Event("change"));
            }
            """);
        await Expect(page.Locator("#cost-config-provider")).ToHaveValueAsync("");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_RendersBudgetWarningCriticalAndCoverageSuppressionFromCanonicalReadFields()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics, PreservedConfiguration);
        await RouteSessionReads(page);
        await page.RouteAsync("**/api/costs/v1/recalculations", route =>
            route.FulfillAsync(Json(RecalculationWithBudgetMatrix, 202)));

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#cost-budget-state")).ToContainTextAsync(
            "session-estimated-cost-threshold · enabled · USD · warning 10 / critical 20");
        await Expect(page.Locator("#cost-budget-state")).ToContainTextAsync(
            "daily-estimated-cost-threshold · disabled · USD · warning 50 / critical 100");
        await Expect(page.Locator("#cost-budget-state")).ToContainTextAsync(
            "period-estimated-cost-threshold · enabled · USD · warning 200 / critical 400");
        await page.Locator("#cost-scope-utc-day").CheckAsync();
        await page.Locator("#cost-scope-period").CheckAsync();
        await page.Locator("#cost-recalculate").ClickAsync();

        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync(
            $"session-estimated-cost-threshold · receipt · {new string('1', 64)}");
        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync(
            $"daily-estimated-cost-threshold · receipt · {new string('2', 64)}");
        await Expect(page.Locator("#cost-recalculation")).ToContainTextAsync(
            "period-estimated-cost-threshold · suppression · insufficient_estimate_coverage");
    }

    [Fact(Timeout = 60_000)]
    public async Task CostPage_RendersCodexDefaultAdapterUnavailableWithoutInventingZero()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RouteBaseReads(page, CompleteAnalytics);
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates?*", route =>
            route.FulfillAsync(Json(CodexUnavailableHistory)));
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/recalculations?*", route =>
            route.FulfillAsync(Json(SessionRecalculations)));

        await page.GotoAsync(
            $"{host.Url}/costs?session_id={SessionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#cost-session")).ToContainTextAsync("unavailable");
        await Expect(page.Locator("#cost-session")).ToContainTextAsync("codex_adapter_unavailable");
        await Expect(page.Locator("#cost-exact-estimate")).ToContainTextAsync("monetary zero ではありません");
        await Expect(page.Locator("#cost-exact-estimate")).Not.ToContainTextAsync("0 USD");
    }

    private static async Task RouteBaseReads(
        IPage page,
        string analytics,
        string? configuration = null,
        string? catalog = null)
    {
        await page.RouteAsync("**/api/costs/v1/configuration", route =>
            route.FulfillAsync(Json(configuration ?? Configuration)));
        await page.RouteAsync("**/api/costs/v1/catalog?*", route =>
            route.FulfillAsync(Json(catalog ?? Catalog)));
        await page.RouteAsync("**/api/costs/v1/analytics?*", route =>
            route.FulfillAsync(Json(analytics)));
    }

    private static async Task RouteSessionReads(
        IPage page,
        string exactEstimate = ExactEstimate,
        string sessionHistory = SessionHistory)
    {
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates?*", route =>
            route.FulfillAsync(Json(sessionHistory)));
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/estimates/{EstimateId}", route =>
            route.FulfillAsync(Json(exactEstimate)));
        await page.RouteAsync($"**/api/costs/v1/sessions/{SessionId}/recalculations?*", route =>
            route.FulfillAsync(Json(SessionRecalculations)));
    }

    private static async Task AssertCanonicalMutation(IRequest request) =>
        Assert.Equal("local-monitor", await request.HeaderValueAsync("x-monitor-csrf"));

    private static async Task InstallStorageTripwires(IPage page) =>
        await page.AddInitScriptAsync(
            """
            (() => {
              window.__costStorageTouched = false;
              for (const name of ["localStorage", "sessionStorage"]) {
                const storage = window[name];
                for (const method of ["setItem", "removeItem", "clear"]) {
                  const original = storage[method].bind(storage);
                  storage[method] = (...args) => { window.__costStorageTouched = true; return original(...args); };
                }
              }
              window.__costDatabaseTouched = false;
              for (const method of ["open", "deleteDatabase"]) {
                const original = indexedDB[method].bind(indexedDB);
                indexedDB[method] = (...args) => { window.__costDatabaseTouched = true; return original(...args); };
              }
              window.__costCacheTouched = false;
              if (window.caches) {
                for (const method of ["open", "delete"]) {
                  const original = caches[method].bind(caches);
                  caches[method] = (...args) => { window.__costCacheTouched = true; return original(...args); };
                }
              }
              window.__costServiceWorkerTouched = false;
              if (navigator.serviceWorker) {
                const original = navigator.serviceWorker.register.bind(navigator.serviceWorker);
                navigator.serviceWorker.register = (...args) => { window.__costServiceWorkerTouched = true; return original(...args); };
              }
            })();
            """);

    private static string CreatePreview(CostConfigurationPreviewRequestV1 request)
    {
        var predecessor = EmptyConfiguration.ConfigurationId;
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            predecessor,
            CatalogSha,
            request.SourceEntries,
            request.BudgetEntries,
            new DateTimeOffset(2026, 7, 23, 4, 0, 0, TimeSpan.Zero));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            1,
            predecessor,
            CatalogSha,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            1,
            1,
            "exact",
            1,
            "exact");
        return Encoding.UTF8.GetString(CostConfigurationPreviewCanonicalJsonV1.Serialize(preview));
    }

    private static string ConfigurationForState(string catalogState) =>
        catalogState switch
        {
            "matching" => Configuration,
            "changed" => SerializeConfiguration(EmptyConfiguration, "changed", 1, ChangedCatalogSha),
            "unconfigured" => SerializeApi(new CostConfigurationReadApplicationV1(
                "cost.configuration-read.v1",
                0,
                null,
                null,
                CatalogSha,
                "unconfigured",
                null,
                0,
                "exact")),
            _ => throw new ArgumentOutOfRangeException(nameof(catalogState)),
        };

    private static string SerializeConfiguration(
        CostConfigurationV1 configuration,
        string catalogState,
        int selectedSessionCount,
        string providerCatalogSha = CatalogSha) =>
        SerializeApi(new CostConfigurationReadApplicationV1(
            "cost.configuration-read.v1",
            1,
            configuration.ConfigurationId,
            configuration.CatalogSha256,
            providerCatalogSha,
            catalogState,
            configuration,
            selectedSessionCount,
            "exact"));

    private static string SerializeApi<T>(T value) =>
        JsonSerializer.Serialize(value, ApiJson);

    private static string CreateOversizedCatalog()
    {
        var sources = Enumerable.Range(0, 65)
            .Select(index => new CostCatalogSourceReadV1(
                "bundled",
                $"source-{index:D2}",
                $"Synthetic source {index:D2}",
                $"registry-{index:D2}",
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 8, 1)))
            .ToArray();
        var entries = Enumerable.Range(0, 101)
            .Select(index => new CostCatalogEntryReadV1(
                "bundled",
                "source-00",
                "Synthetic source 00",
                "registry-00",
                $"entry-{index:D3}",
                null,
                "active",
                null,
                "github_copilot",
                $"model-{index:D3}",
                "github_ai_credits",
                "credit_consuming_interaction",
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 8, 1),
                "USD",
                false,
                "https://example.com/pricing/synthetic"))
            .ToArray();
        return SerializeApi(new CostCatalogApplicationV1(
            "cost.catalog.v1",
            CatalogSha,
            sources,
            entries,
            null));
    }

    private static string CreateFilteredRepositoryAnalytics()
    {
        var root = JsonNode.Parse(MixedRepositoryAnalytics)!.AsObject();
        root["snapshot_id"] =
            "cost-analytics-snapshot-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        root["eligible_session_count"] = 1;
        var filters = root["filters"]!.AsObject();
        filters["repository"] = "Repo.Label";
        filters["workspace"] = "Workspace Label";
        var overall = root["overall"]!.AsObject();
        overall["eligible_session_count"] = 1;
        overall["estimated_session_count"] = 1;
        overall["not_estimable_session_count"] = 0;
        overall["coverage_numerator"] = 1;
        overall["coverage_denominator"] = 1;
        overall["coverage_basis_points"] = 10000;
        root["range_totals"] = new JsonArray(root["range_totals"]![0]!.DeepClone());
        root["daily_totals"] = new JsonArray(root["daily_totals"]![0]!.DeepClone());
        var group = root["groups"]![0]!.DeepClone().AsObject();
        group["model"] = "filtered-model";
        root["groups"] = new JsonArray(group);
        return root.ToJsonString();
    }

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new TestUtcDateTimeOffsetConverter());
        return options;
    }

    private static RouteFulfillOptions Json(string body, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json",
        Body = body,
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
    };

    private static MonitorHostTestOptions QuietHostOptions() => new()
    {
        StartProjectionWorker = false,
        StartWriter = false,
        StartRetentionCleanupWorker = false,
    };

    private sealed class TestUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture));
    }

    private const string CatalogSha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChangedCatalogSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions ApiJson = CreateApiJsonOptions();
    private static readonly CostConfigurationV1 EmptyConfiguration =
        CostConfigurationCanonicalJsonV1.Create(
            null,
            CatalogSha,
            [],
            [],
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
    private static readonly CostConfigurationV1 PreservedConfigurationModel =
        CostConfigurationCanonicalJsonV1.Create(
            null,
            CatalogSha,
            [
                new("github-copilot-vscode", "1.0.4", "synthetic-pricing-v1", "github_copilot", "github_ai_credits", "credit_consuming_interaction"),
                new("a_", "2.0.0", "synthetic-pricing-v1", "unknown", "unknown", "unknown"),
                new("a-", "2.0.0", "synthetic-pricing-v1", "unknown", "unknown", "unknown"),
            ],
            [
                new("session-estimated-cost-threshold", "1", true, "USD", "10", "20", 10000, "session", null),
                new("daily-estimated-cost-threshold", "1", false, "USD", "50", "100", 9000, "utc_day", null),
                new("period-estimated-cost-threshold", "1", true, "USD", "200", "400", 8000, "rolling_period", 30),
            ],
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
    private static readonly string Configuration =
        SerializeConfiguration(EmptyConfiguration, "matching", 1);
    private static readonly string PreservedConfiguration =
        SerializeConfiguration(PreservedConfigurationModel, "matching", 3);
    private static readonly string Catalog = SerializeApi(
        new CostCatalogApplicationV1(
            "cost.catalog.v1",
            CatalogSha,
            [new("bundled", "github-public", "GitHub public pricing", "2026-07-01", new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1))],
            [new(
                "bundled",
                "github-public",
                "GitHub public pricing",
                "2026-07-01",
                "github-copilot:gpt-4.1:credit",
                null,
                "active",
                null,
                "github_copilot",
                "gpt-4.1",
                "github_ai_credits",
                "credit_consuming_interaction",
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 8, 1),
                "USD",
                false,
                "https://example.com/pricing/%3Cscript%3E")],
            null));
    private static readonly string CatalogWithIncludedZero = Catalog.Replace(
        "\"included_zero_incremental_cost\":false",
        "\"included_zero_incremental_cost\":true",
        StringComparison.Ordinal);
    private static readonly string OversizedCatalog = CreateOversizedCatalog();
    private static readonly string FilteredRepositoryAnalytics = CreateFilteredRepositoryAnalytics();
    private const string CompleteAnalytics =
        """{"schema_version":"cost.analytics.v1","snapshot_id":"cost-analytics-snapshot-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","state":"complete","cap_reason":null,"eligible_session_count":4,"eligible_session_lower_bound":null,"group_lower_bound":null,"filters":{"from":"2026-07-22T00:00:00.0000000Z","to":"2026-07-24T00:00:00.0000000Z","source_surface":null,"provider":null,"model":null,"billing_mode":null,"status":null,"registry_version":null,"currency":null,"repository":null,"workspace":null,"limit":50},"overall":{"eligible_session_count":4,"estimated_session_count":2,"partial_session_count":1,"not_estimable_session_count":0,"missing_session_count":0,"failed_session_count":1,"unavailable_session_count":0,"stale_session_count":0,"coverage_numerator":2,"coverage_denominator":4,"coverage_basis_points":5000},"range_totals":[{"registry_version":"2026-07-01","currency":"USD","estimated_amount_state":"available","estimated_amount":"12.50","partial_known_component_amount_state":"available","partial_known_component_amount":"1.25","partial_reason_counts":[{"reason":"unknown_model","session_count":1}]}],"daily_totals":[{"utc_date":"2026-07-23","registry_version":"2026-07-01","currency":"USD","estimated_amount_state":"available","estimated_amount":"12.50","partial_known_component_amount_state":"available","partial_known_component_amount":"1.25","partial_reason_counts":[{"reason":"unknown_model","session_count":1}]}],"groups":[{"utc_date":"2026-07-23","source_surface":"github-copilot-vscode","provider":"github_copilot","model":"gpt-4.1","billing_mode":"github_ai_credits","repository":null,"workspace":null,"registry_version":"2026-07-01","currency":"USD","component_category":"input","group_id":"cost-analytics-group-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","unknown_dimensions":["repository","workspace"],"eligible_session_count":4,"estimated_session_count":2,"partial_session_count":1,"not_estimable_session_count":0,"missing_session_count":0,"failed_session_count":1,"unavailable_session_count":0,"stale_session_count":0,"coverage_basis_points":5000,"component_session_count":3,"estimated_component_session_count":2,"partial_component_session_count":1,"estimated_amount_state":"available","estimated_amount":"8.00","partial_known_component_amount_state":"available","partial_known_component_amount":"1.25","partial_reason_counts":[{"reason":"unknown_model","session_count":1}]}],"next_cursor":null}""";
    private const string IncompleteAnalytics =
        """{"schema_version":"cost.analytics.v1","snapshot_id":"cost-analytics-snapshot-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"incomplete","cap_reason":"eligible_session_limit","eligible_session_count":null,"eligible_session_lower_bound":2001,"group_lower_bound":null,"filters":{"from":"2026-07-22T00:00:00.0000000Z","to":"2026-07-24T00:00:00.0000000Z","source_surface":null,"provider":null,"model":null,"billing_mode":null,"status":null,"registry_version":null,"currency":null,"repository":null,"workspace":null,"limit":50},"overall":null,"range_totals":[],"daily_totals":[],"groups":[],"next_cursor":null}""";
    private const string MixedRepositoryAnalytics =
        """{"schema_version":"cost.analytics.v1","snapshot_id":"cost-analytics-snapshot-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","state":"complete","cap_reason":null,"eligible_session_count":3,"eligible_session_lower_bound":null,"group_lower_bound":null,"filters":{"from":"2026-07-22T00:00:00.0000000Z","to":"2026-07-24T00:00:00.0000000Z","source_surface":null,"provider":null,"model":null,"billing_mode":null,"status":null,"registry_version":null,"currency":null,"repository":null,"workspace":null,"limit":50},"overall":{"eligible_session_count":3,"estimated_session_count":2,"partial_session_count":0,"not_estimable_session_count":1,"missing_session_count":0,"failed_session_count":0,"unavailable_session_count":0,"stale_session_count":0,"coverage_numerator":2,"coverage_denominator":3,"coverage_basis_points":6666},"range_totals":[{"registry_version":"registry-a","currency":"USD","estimated_amount_state":"available","estimated_amount":"4.00","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]},{"registry_version":"registry-b","currency":"USD","estimated_amount_state":"available","estimated_amount":"4.50","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]}],"daily_totals":[{"utc_date":"2026-07-23","registry_version":"registry-a","currency":"USD","estimated_amount_state":"available","estimated_amount":"4.00","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]},{"utc_date":"2026-07-23","registry_version":"registry-b","currency":"USD","estimated_amount_state":"available","estimated_amount":"4.50","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]}],"groups":[{"utc_date":"2026-07-23","source_surface":"github-copilot-vscode","provider":"github_copilot","model":"gpt-4.1","billing_mode":"github_ai_credits","repository":"Repo.Label","workspace":"Workspace Label","registry_version":"registry-a","currency":"USD","component_category":"input","group_id":"cost-analytics-group-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","unknown_dimensions":[],"eligible_session_count":1,"estimated_session_count":1,"partial_session_count":0,"not_estimable_session_count":0,"missing_session_count":0,"failed_session_count":0,"unavailable_session_count":0,"stale_session_count":0,"coverage_basis_points":10000,"component_session_count":1,"estimated_component_session_count":1,"partial_component_session_count":0,"estimated_amount_state":"available","estimated_amount":"4.00","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]},{"utc_date":"2026-07-23","source_surface":"github-copilot-vscode","provider":"github_copilot","model":"gpt-4.2","billing_mode":"github_ai_credits","repository":"unknown","workspace":"unknown","registry_version":"registry-b","currency":"USD","component_category":"output","group_id":"cost-analytics-group-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","unknown_dimensions":[],"eligible_session_count":1,"estimated_session_count":1,"partial_session_count":0,"not_estimable_session_count":0,"missing_session_count":0,"failed_session_count":0,"unavailable_session_count":0,"stale_session_count":0,"coverage_basis_points":10000,"component_session_count":1,"estimated_component_session_count":1,"partial_component_session_count":0,"estimated_amount_state":"available","estimated_amount":"4.50","partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]},{"utc_date":"2026-07-23","source_surface":"github-copilot-vscode","provider":"github_copilot","model":"contract-model","billing_mode":"subscription","repository":null,"workspace":null,"registry_version":null,"currency":null,"component_category":null,"group_id":"cost-analytics-group-dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","unknown_dimensions":["repository","workspace","registry_version","currency","component_category"],"eligible_session_count":1,"estimated_session_count":0,"partial_session_count":0,"not_estimable_session_count":1,"missing_session_count":0,"failed_session_count":0,"unavailable_session_count":0,"stale_session_count":0,"coverage_basis_points":0,"component_session_count":0,"estimated_component_session_count":0,"partial_component_session_count":0,"estimated_amount_state":"not_applicable","estimated_amount":null,"partial_known_component_amount_state":"not_applicable","partial_known_component_amount":null,"partial_reason_counts":[]}],"next_cursor":null}""";
    private static readonly string CompleteAnalyticsWithCursor =
        CompleteAnalytics.Replace(
            "\"next_cursor\":null",
            "\"next_cursor\":\"cost-analytics-cursor-v1.fixture\"",
            StringComparison.Ordinal);
    private static readonly string CatalogWithNext =
        Catalog.Replace("\"next_after\":null", "\"next_after\":\"catalog-page\"", StringComparison.Ordinal);
    private static readonly string SessionHistoryWithNext =
        SessionHistory.Replace("\"next_after\":null", "\"next_after\":\"estimate-page\"", StringComparison.Ordinal);
    private static readonly string SessionRecalculationsWithNext =
        SessionRecalculations.Replace("\"next_after\":null", "\"next_after\":\"attempt-page\"", StringComparison.Ordinal);
    private const string SessionHistory =
        """{"schema_version":"cost.session-estimates.v1","session_id":"0198f5b8-0c00-7000-8000-000000000001","calculation_state":"partial","active_head_revision":2,"active_estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","latest_attempt_revision":3,"latest_attempt":{"attempt_revision":3,"run_id":"0198f5b8-0c00-7000-8000-000000000003","calculation_time_utc":"2026-07-23T03:00:00.0000000Z","freshness":"stale","kind":"failed","estimate_status":null,"estimate_id":null,"code":"source_mapping_unavailable"},"items":[{"head_revision":2,"estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","predecessor_estimate_id":"pricing-estimate-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","calculation_time_utc":"2026-07-23T02:00:00.0000000Z","session_effective_at_utc":"2026-07-23T01:00:00.0000000Z","estimate_status":"partial","freshness":"fresh","amount_kind":"provisional_known_component_subtotal","amount":"1.25","currency":"USD","provider":"github_copilot","model":"gpt-4.1","billing_mode":"github_ai_credits","pricing_route":"credit_consuming_interaction","catalog_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","configuration_id":"cost-configuration-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registry":{"registry_version":"local-1","source_kind":"local_override","source_id":"operator-reviewed","source_label":"Operator reviewed override","entry_key":"github:model:api","effective_from_utc":"2026-07-20T00:00:00.0000000Z","effective_to_utc":null,"last_reviewed_date":"2026-07-20","stale_after_date":"2026-08-20","currency":"USD","source_reference":null},"components":[{"category":"input","state":"available","amount":"1.25","missing_reason":null},{"category":"output","state":"missing","amount":null,"missing_reason":"unknown_model"}],"coverage":{"required_categories":["input","output"],"estimated_categories":["input"],"missing_categories":["output"]},"reasons":["unknown_model"],"delta":{"state":"not_applicable","amount":null,"currency":null,"basis_freshness":null,"changed_fields":["coverage"]},"disclaimer":"estimated_cost_not_invoice.v1"}],"next_after":null}""";
    private const string ExactEstimate =
        """{"schema_version":"cost.session-estimate.v1","session_id":"0198f5b8-0c00-7000-8000-000000000001","active_head_revision":2,"active_estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","item":{"head_revision":2,"estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","predecessor_estimate_id":null,"calculation_time_utc":"2026-07-23T02:00:00.0000000Z","session_effective_at_utc":"2026-07-23T01:00:00.0000000Z","estimate_status":"partial","freshness":"fresh","amount_kind":"provisional_known_component_subtotal","amount":"1.25","currency":"USD","provider":"github_copilot","model":"gpt-4.1","billing_mode":"github_ai_credits","pricing_route":"credit_consuming_interaction","catalog_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","configuration_id":"cost-configuration-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registry":{"registry_version":"local-1","source_kind":"local_override","source_id":"operator-reviewed","source_label":"Operator reviewed override","entry_key":"github:model:api","effective_from_utc":"2026-07-20T00:00:00.0000000Z","effective_to_utc":null,"last_reviewed_date":"2026-07-20","stale_after_date":"2026-08-20","currency":"USD","source_reference":null},"components":[{"category":"input","state":"available","amount":"1.25","missing_reason":null},{"category":"output","state":"missing","amount":null,"missing_reason":"unknown_model"}],"coverage":{"required_categories":["input","output"],"estimated_categories":["input"],"missing_categories":["output"]},"reasons":["unknown_model"],"delta":{"state":"not_applicable","amount":null,"currency":null,"basis_freshness":null,"changed_fields":[]},"disclaimer":"estimated_cost_not_invoice.v1"}}""";
    private static readonly string PlanIncludedZeroEstimate = ExactEstimate
        .Replace("\"estimate_status\":\"partial\"", "\"estimate_status\":\"estimated\"", StringComparison.Ordinal)
        .Replace("\"amount_kind\":\"provisional_known_component_subtotal\"", "\"amount_kind\":\"complete_total\"", StringComparison.Ordinal)
        .Replace("\"amount\":\"1.25\"", "\"amount\":\"0\"", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"github_ai_credits\"", "\"billing_mode\":\"plan_included\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"credit_consuming_interaction\"", "\"pricing_route\":\"plan_included_zero\"", StringComparison.Ordinal)
        .Replace(
            """{"category":"input","state":"available","amount":"0","missing_reason":null},{"category":"output","state":"missing","amount":null,"missing_reason":"unknown_model"}""",
            """{"category":"included_zero_incremental_cost","state":"available","amount":"0","missing_reason":null}""",
            StringComparison.Ordinal)
        .Replace(
            "\"coverage\":{\"required_categories\":[\"input\",\"output\"],\"estimated_categories\":[\"input\"],\"missing_categories\":[\"output\"]},\"reasons\":[\"unknown_model\"]",
            "\"coverage\":{\"required_categories\":[\"included_zero_incremental_cost\"],\"estimated_categories\":[\"included_zero_incremental_cost\"],\"missing_categories\":[]},\"reasons\":[]",
            StringComparison.Ordinal);
    private static readonly string PlanIncludedZeroHistory = SessionHistory
        .Replace("\"calculation_state\":\"partial\"", "\"calculation_state\":\"estimated\"", StringComparison.Ordinal)
        .Replace("\"estimate_status\":\"partial\"", "\"estimate_status\":\"estimated\"", StringComparison.Ordinal)
        .Replace("\"amount_kind\":\"provisional_known_component_subtotal\"", "\"amount_kind\":\"complete_total\"", StringComparison.Ordinal)
        .Replace("\"amount\":\"1.25\"", "\"amount\":\"0\"", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"github_ai_credits\"", "\"billing_mode\":\"plan_included\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"credit_consuming_interaction\"", "\"pricing_route\":\"plan_included_zero\"", StringComparison.Ordinal)
        .Replace(
            """{"category":"input","state":"available","amount":"0","missing_reason":null},{"category":"output","state":"missing","amount":null,"missing_reason":"unknown_model"}""",
            """{"category":"included_zero_incremental_cost","state":"available","amount":"0","missing_reason":null}""",
            StringComparison.Ordinal)
        .Replace(
            "\"coverage\":{\"required_categories\":[\"input\",\"output\"],\"estimated_categories\":[\"input\"],\"missing_categories\":[\"output\"]},\"reasons\":[\"unknown_model\"]",
            "\"coverage\":{\"required_categories\":[\"included_zero_incremental_cost\"],\"estimated_categories\":[\"included_zero_incremental_cost\"],\"missing_categories\":[]},\"reasons\":[]",
            StringComparison.Ordinal);
    private static readonly string FullEstimate = PlanIncludedZeroEstimate
        .Replace("\"amount\":\"0\"", "\"amount\":\"4.25\"", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"plan_included\"", "\"billing_mode\":\"github_ai_credits\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"plan_included_zero\"", "\"pricing_route\":\"credit_consuming_interaction\"", StringComparison.Ordinal)
        .Replace("included_zero_incremental_cost", "input", StringComparison.Ordinal);
    private static readonly string FullEstimateHistory = PlanIncludedZeroHistory
        .Replace("\"amount\":\"0\"", "\"amount\":\"4.25\"", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"plan_included\"", "\"billing_mode\":\"github_ai_credits\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"plan_included_zero\"", "\"pricing_route\":\"credit_consuming_interaction\"", StringComparison.Ordinal)
        .Replace("included_zero_incremental_cost", "input", StringComparison.Ordinal);
    private static readonly string NotEstimableEstimate = ExactEstimate
        .Replace("\"estimate_status\":\"partial\"", "\"estimate_status\":\"not-estimable\"", StringComparison.Ordinal)
        .Replace("\"amount_kind\":\"provisional_known_component_subtotal\"", "\"amount_kind\":\"not_applicable\"", StringComparison.Ordinal)
        .Replace("\"amount\":\"1.25\",\"currency\":\"USD\"", "\"amount\":null,\"currency\":null", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"github_ai_credits\"", "\"billing_mode\":\"__BILLING_MODE__\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"credit_consuming_interaction\"", "\"pricing_route\":\"not_estimable_contract\"", StringComparison.Ordinal)
        .Replace(
            "\"components\":[{\"category\":\"input\",\"state\":\"available\",\"amount\":\"1.25\",\"missing_reason\":null},{\"category\":\"output\",\"state\":\"missing\",\"amount\":null,\"missing_reason\":\"unknown_model\"}],\"coverage\":{\"required_categories\":[\"input\",\"output\"],\"estimated_categories\":[\"input\"],\"missing_categories\":[\"output\"]},\"reasons\":[\"unknown_model\"]",
            "\"components\":[],\"coverage\":{\"required_categories\":[],\"estimated_categories\":[],\"missing_categories\":[]},\"reasons\":[\"__REASON__\"]",
            StringComparison.Ordinal);
    private static readonly string NotEstimableHistory = SessionHistory
        .Replace("\"calculation_state\":\"partial\"", "\"calculation_state\":\"not_estimable\"", StringComparison.Ordinal)
        .Replace("\"estimate_status\":\"partial\"", "\"estimate_status\":\"not-estimable\"", StringComparison.Ordinal)
        .Replace("\"amount_kind\":\"provisional_known_component_subtotal\"", "\"amount_kind\":\"not_applicable\"", StringComparison.Ordinal)
        .Replace("\"amount\":\"1.25\",\"currency\":\"USD\"", "\"amount\":null,\"currency\":null", StringComparison.Ordinal)
        .Replace("\"billing_mode\":\"github_ai_credits\"", "\"billing_mode\":\"__BILLING_MODE__\"", StringComparison.Ordinal)
        .Replace("\"pricing_route\":\"credit_consuming_interaction\"", "\"pricing_route\":\"not_estimable_contract\"", StringComparison.Ordinal)
        .Replace(
            "\"components\":[{\"category\":\"input\",\"state\":\"available\",\"amount\":\"1.25\",\"missing_reason\":null},{\"category\":\"output\",\"state\":\"missing\",\"amount\":null,\"missing_reason\":\"unknown_model\"}],\"coverage\":{\"required_categories\":[\"input\",\"output\"],\"estimated_categories\":[\"input\"],\"missing_categories\":[\"output\"]},\"reasons\":[\"unknown_model\"]",
            "\"components\":[],\"coverage\":{\"required_categories\":[],\"estimated_categories\":[],\"missing_categories\":[]},\"reasons\":[\"__REASON__\"]",
            StringComparison.Ordinal);
    private static readonly string StaleRegistryEstimate = ExactEstimate.Replace(
        "\"calculation_time_utc\":\"2026-07-23T02:00:00.0000000Z\"",
        "\"calculation_time_utc\":\"2026-08-21T02:00:00.0000000Z\"",
        StringComparison.Ordinal);
    private static readonly string StaleRegistryHistory = SessionHistory.Replace(
        "\"calculation_time_utc\":\"2026-07-23T02:00:00.0000000Z\"",
        "\"calculation_time_utc\":\"2026-08-21T02:00:00.0000000Z\"",
        StringComparison.Ordinal);
    private static readonly string StaleBoundaryEstimate = ExactEstimate.Replace(
        "\"calculation_time_utc\":\"2026-07-23T02:00:00.0000000Z\"",
        "\"calculation_time_utc\":\"2026-08-20T02:00:00.0000000Z\"",
        StringComparison.Ordinal);
    private static readonly string StaleBoundaryHistory = SessionHistory.Replace(
        "\"calculation_time_utc\":\"2026-07-23T02:00:00.0000000Z\"",
        "\"calculation_time_utc\":\"2026-08-20T02:00:00.0000000Z\"",
        StringComparison.Ordinal);
    private const string CodexUnavailableHistory =
        """{"schema_version":"cost.session-estimates.v1","session_id":"0198f5b8-0c00-7000-8000-000000000001","calculation_state":"unavailable","active_head_revision":null,"active_estimate_id":null,"latest_attempt_revision":1,"latest_attempt":{"attempt_revision":1,"run_id":"0198f5b8-0c00-7000-8000-000000000003","calculation_time_utc":"2026-07-23T03:00:00.0000000Z","freshness":"fresh","kind":"unavailable","estimate_status":null,"estimate_id":null,"code":"codex_adapter_unavailable"},"items":[],"next_after":null}""";
    private const string SessionRecalculations =
        """{"schema_version":"cost.session-recalculations.v1","session_id":"0198f5b8-0c00-7000-8000-000000000001","active":null,"attempts":[{"attempt_revision":3,"run_id":"0198f5b8-0c00-7000-8000-000000000003","calculation_time_utc":"2026-07-23T03:00:00.0000000Z","freshness":"stale","kind":"failed","estimate_status":null,"estimate_id":null,"code":"source_mapping_unavailable","recalculation_href":"/api/costs/v1/recalculations/0198f5b8-0c00-7000-8000-000000000003"}],"next_after":null}""";
    private const string RecalculationRunning =
        """{"schema_version":"cost.recalculation.v1","run_id":"0198f5b8-0c00-7000-8000-000000000099","request_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","state":"running","target_count":1,"scope_count":1,"targets":[{"target_ordinal":0,"session_id":"0198f5b8-0c00-7000-8000-000000000001","base_head_revision":2,"base_estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","result":null}],"events":[],"budget_results":[],"failure_code":null}""";
    private const string RecalculationSucceeded =
        """{"schema_version":"cost.recalculation.v1","run_id":"0198f5b8-0c00-7000-8000-000000000099","request_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","state":"succeeded","target_count":1,"scope_count":1,"targets":[{"target_ordinal":0,"session_id":"0198f5b8-0c00-7000-8000-000000000001","base_head_revision":2,"base_estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","result":{"kind":"estimate","status":"estimated","estimate_id":"pricing-estimate-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}}],"events":[],"budget_results":[{"scope_ordinal":0,"scope":{"scope_kind":"session","session_id":"0198f5b8-0c00-7000-8000-000000000001"},"rule_id":"session-estimated-cost-threshold","rule_version":"1","outcome":{"kind":"no_match","evaluation_id":"evaluation-a"}}],"failure_code":null}""";
    private const string RecalculationWithBudgetMatrix =
        """{"schema_version":"cost.recalculation.v1","run_id":"0198f5b8-0c00-7000-8000-000000000099","request_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","state":"succeeded","target_count":1,"scope_count":3,"targets":[{"target_ordinal":0,"session_id":"0198f5b8-0c00-7000-8000-000000000001","base_head_revision":2,"base_estimate_id":"pricing-estimate-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","result":{"kind":"estimate","status":"estimated","estimate_id":"pricing-estimate-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}}],"events":[],"budget_results":[{"scope_ordinal":0,"scope":{"scope_kind":"session","session_id":"0198f5b8-0c00-7000-8000-000000000001"},"rule_id":"session-estimated-cost-threshold","rule_version":"1","outcome":{"kind":"receipt","evaluation_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","alert_id":"1111111111111111111111111111111111111111111111111111111111111111"}},{"scope_ordinal":1,"scope":{"scope_kind":"utc_day","utc_date":"2026-07-23"},"rule_id":"daily-estimated-cost-threshold","rule_version":"1","outcome":{"kind":"receipt","evaluation_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","alert_id":"2222222222222222222222222222222222222222222222222222222222222222"}},{"scope_ordinal":2,"scope":{"scope_kind":"rolling_period","cutoff_utc":"2026-07-24T00:00:00.0000000Z","window_days":30},"rule_id":"period-estimated-cost-threshold","rule_version":"1","outcome":{"kind":"suppression","evaluation_id":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","suppression_ordinal":0,"code":"insufficient_estimate_coverage"}}],"failure_code":null}""";
}

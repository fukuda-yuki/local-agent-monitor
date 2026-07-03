using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 trace detail (§6.3): flow | waterfall segment toggle over the
/// sanitized spans API, parallel-group rendering, span selection with URL
/// state, and the standing cache column. Uses the shared rich-trace fixture
/// (parallel trio, cache turns, recovered retry pair).
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorDesignViewPlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceDetail_FlowWaterfallCacheColumn_RenderFromSanitizedSpansOnly(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (sanitizedOnly)
        {
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        }

        // Hex token from the handoff §10 (D042 C2).
        Assert.Equal(
            "#14171e",
            await page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).getPropertyValue('--monitor-bg').trim()"));

        // The rich trace is a recovered-error trace, so エラーのみ defaults ON —
        // switch it off to see the full flow (M7 covers the error mode itself).
        await Expect(page.Locator("#errors-only")).ToBeCheckedAsync();
        await page.Locator("#errors-only").UncheckAsync();

        // Flow view: start/end markers, turn cards with intent labels, the
        // parallel trio under turn 1, and the recovered retry pair under turn 2.
        await Expect(page.Locator("#flow-view")).ToBeVisibleAsync();
        await Expect(page.Locator("#flow-view .turn-card")).ToHaveCountAsync(3);
        await Expect(page.Locator("#flow-view .turn-card").First).ToContainTextAsync("ターン1 · 調査");
        await Expect(page.Locator("#flow-view .parallel-badge")).ToContainTextAsync("⑂ 並行 3 件");
        await Expect(page.Locator("#flow-view .parallel-lane .tool-card")).ToHaveCountAsync(3);
        await Expect(page.Locator("#flow-view .tool-card.tool-error")).ToHaveCountAsync(1);
        await Expect(page.Locator("#flow-view .tool-card.tool-error")).ToContainTextAsync("✕ 失敗 · tool_failure");
        await Expect(page.Locator("#flow-view .flow-start")).ToContainTextAsync("copilot-agent 開始");
        await Expect(page.Locator("#flow-view .flow-end")).ToContainTextAsync("完了");

        // Cache column (§6.3 right): read rate + effective-input conversion.
        // Turn inputs 8000+9000+9500 = 26500; cache reads 5000+6500+7000 = 18500 → 70%.
        await Expect(page.Locator("#cache-overview .cache-rate-value")).ToHaveTextAsync("70%");
        await Expect(page.Locator("#cache-overview")).ToContainTextAsync("実効入力換算");
        await Expect(page.Locator("#cache-turns .cache-turn-bar")).ToHaveCountAsync(3);

        // Span selection: clicking a turn highlights it and lands in the URL.
        await page.Locator("#flow-view .turn-card").First.ClickAsync();
        await Expect(page.Locator("#flow-view .turn-card.selected")).ToHaveCountAsync(1);
        Assert.Contains("span=t100", page.Url);

        // Toggle to waterfall: selection survives, parallel rows share the vocabulary.
        await page.Locator("#view-toggle .view-btn[data-view='waterfall']").ClickAsync();
        await Expect(page.Locator("#waterfall-view")).ToBeVisibleAsync();
        await Expect(page.Locator("#flow-view")).ToBeHiddenAsync();
        Assert.Contains("view=waterfall", page.Url);
        Assert.Contains("span=t100", page.Url);
        await Expect(page.Locator("#waterfall-view .wf-group-head")).ToContainTextAsync("⑂ 並行 3 件");
        await Expect(page.Locator("#waterfall-view .wf-prefix")).ToHaveCountAsync(3);
        await Expect(page.Locator("#waterfall-view .wf-row.selected")).ToHaveCountAsync(1);
        // Tokens column carries values for llm rows only.
        await Expect(page.Locator("#waterfall-view .wf-llm .wf-tokens").First).Not.ToHaveTextAsync("—");
        await Expect(page.Locator("#waterfall-view .wf-tool .wf-tokens").First).ToHaveTextAsync("—");

        // Only the sanitized spans API was consulted; nothing raw-bearing.
        Assert.Contains(requestedUrls, url => url.Contains($"/api/monitor/traces/{MonitorRichTrace.TraceId}/spans", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
        await Expect(page.Locator("#flow-view canvas")).ToHaveCountAsync(0);
        await Expect(page.Locator("#waterfall-view canvas")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TraceDetail_UrlState_RestoresViewAndSelection()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        // f201 sits in the error turn, so it stays visible even while the
        // recovered trace's default errors-only filter is on.
        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}?view=waterfall&span=f201", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#waterfall-view")).ToBeVisibleAsync();
        await Expect(page.Locator("#flow-view")).ToBeHiddenAsync();
        await Expect(page.Locator("#waterfall-view .wf-row.selected")).ToHaveCountAsync(1);
        Assert.Equal("f201", await page.Locator("#waterfall-view .wf-row.selected").GetAttributeAsync("data-span-id"));
    }
}

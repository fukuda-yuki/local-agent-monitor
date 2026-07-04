using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 error analysis mode (§6.5): the summary strip, the error panel
/// replacing the cache column (list / detail with raw exception message /
/// input-token trend with the 128K line), errors-only defaulting ON, and the
/// recovered / unrecovered vocabulary. Resolution of the canvas 4a-2 question:
/// recovered-only traces also enter error mode (the design defines the variant
/// for any trace containing errors).
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorErrorModePlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorMode_UnrecoveredTrace_ShowsStripPanelAndTrend(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp, unrecovered: true);
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

        // Status pill and terminal marker reflect the unrecovered rollup.
        await Expect(page.Locator(".status-pill")).ToContainTextAsync("エラー · 異常終了");
        await Expect(page.Locator("#errors-only")).ToBeCheckedAsync();

        // Summary strip: 2 errors, 1 recovered, 1 terminal.
        await Expect(page.Locator("#error-strip")).ToBeVisibleAsync();
        await Expect(page.Locator("#error-strip-text")).ToContainTextAsync("エラー 2件");
        await Expect(page.Locator("#error-strip-text")).ToContainTextAsync("1件は回復済み");
        await Expect(page.Locator("#error-strip-text")).ToContainTextAsync("1件が原因でトレースが異常終了");

        // The error panel replaces the cache column.
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();
        await Expect(page.Locator("#cache-column")).ToBeHiddenAsync();
        await Expect(page.Locator("#error-panel .error-row")).ToHaveCountAsync(2);
        await Expect(page.Locator("#error-panel .error-row .recovery-pill.pill-recovered")).ToHaveCountAsync(1);
        await Expect(page.Locator("#error-panel .error-row .recovery-pill.pill-unrecovered")).ToHaveCountAsync(1);

        // The flow marks the terminal error and the abnormal end.
        await Expect(page.Locator("#flow-view .tool-card.tool-unrecovered")).ToHaveCountAsync(1);
        await Expect(page.Locator("#flow-view .flow-end")).ToContainTextAsync("異常終了");

        // Token trend card carries per-turn bars and the limit line.
        await Expect(page.Locator("#error-panel .token-trend-bar")).ToHaveCountAsync(3);
        await Expect(page.Locator("#error-panel .token-limit-line")).ToHaveCountAsync(1);

        // Error detail: raw exception message only in the raw-default posture.
        if (sanitizedOnly)
        {
            await Expect(page.Locator("#error-message-block")).ToContainTextAsync("--sanitized-only では表示できません");
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/detail", StringComparison.Ordinal));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Contains(requestedUrls, url => url.Contains("/spans/f201/detail", StringComparison.Ordinal));
        }

        // Selecting the unrecovered error highlights its span in the flow.
        await page.Locator("#error-panel .error-row", new PageLocatorOptions { HasTextString = "未回復" }).ClickAsync();
        await Expect(page.Locator("#flow-view .tool-card.tool-unrecovered")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("selected"));
    }

    [Fact]
    public async Task ErrorMode_RecoveredOnlyTrace_AlsoEntersErrorMode()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp); // recovered pair only, no terminal error
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator(".status-pill")).ToContainTextAsync("エラー · 回復済み");
        await Expect(page.Locator("#errors-only")).ToBeCheckedAsync();
        await Expect(page.Locator("#error-strip")).ToBeVisibleAsync();
        await Expect(page.Locator("#error-strip-text")).ToContainTextAsync("エラー 1件 — 1件は回復済み");
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();
        await Expect(page.Locator("#cache-column")).ToBeHiddenAsync();
        await Expect(page.Locator("#flow-view .flow-end")).ToContainTextAsync("完了");
    }
}

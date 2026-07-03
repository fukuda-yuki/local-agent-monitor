using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 shell (D042): 208px sidebar with a 2-item nav (概要 / トレース, no
/// 診断 nav item), receive-status badge, and the diagnostics popover entry.
/// The shell scripts read only /health/ready and sanitized /api/monitor/* —
/// never a raw-bearing route.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorShellPlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shell_SidebarNavAndStatusPopover_WorkWithoutRawFetches(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            Health = MonitorTestHealth.Ready(time),
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // 2-item nav: 概要 / トレース, and no 診断 nav item (popover is the entry).
        await Expect(page.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
        await Expect(page.Locator(".sidebar-nav")).ToContainTextAsync("概要");
        await Expect(page.Locator(".sidebar-nav")).ToContainTextAsync("トレース");
        Assert.DoesNotContain("診断", await page.Locator(".sidebar-nav").InnerTextAsync());

        // Badge reflects readiness and opens the popover.
        await Expect(page.Locator("#status-badge-text")).ToContainTextAsync("正常 · 受信中");
        await Expect(page.Locator("#status-popover")).ToBeHiddenAsync();
        await page.Locator("#status-badge").ClickAsync();
        await Expect(page.Locator("#status-popover")).ToBeVisibleAsync();
        await Expect(page.Locator("#popover-title")).ToContainTextAsync("受信できます — ready");
        await Expect(page.Locator("#popover-endpoint")).ToContainTextAsync("/health/ready → 200");
        await Expect(page.Locator("#popover-pipeline li")).ToHaveCountAsync(4);
        await Expect(page.Locator("#popover-pipeline")).ToContainTextAsync("① 受信 (OTLP)");
        await Expect(page.Locator("#popover-pipeline")).ToContainTextAsync("④ DB / migration");

        // Esc closes the popover and returns focus to the badge.
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#status-popover")).ToBeHiddenAsync();

        // 詳細診断を開く navigates to /diagnostics; the sidebar still has 2 items
        // there (C1) and the §6.7 pipeline summary renders.
        await page.Locator("#status-badge").ClickAsync();
        await page.Locator("#status-popover a.primary").ClickAsync();
        await page.WaitForURLAsync($"{host.Url}/diagnostics");
        await Expect(page.Locator(".sidebar-nav .sidebar-link")).ToHaveCountAsync(2);
        await Expect(page.Locator(".pipeline-card")).ToHaveCountAsync(4);
        await Expect(page.Locator("#ingestion-history")).ToHaveCountAsync(1);

        // The popover's 取り込み履歴 link opens the history section via the fragment.
        await page.Locator("#status-badge").ClickAsync();
        await page.Locator("#status-popover a", new PageLocatorOptions { HasTextString = "取り込み履歴" }).ClickAsync();
        await page.WaitForURLAsync($"{host.Url}/diagnostics#ingestion-history");
        await Expect(page.Locator("#ingestion-history")).ToHaveAttributeAsync("open", "");

        // The shell never fetches raw-bearing routes, in either posture.
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("prompt-label", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("/health/ready", StringComparison.Ordinal));
    }
}

using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 span inspector (§6.4): clicking a span swaps the inspector into the
/// cache-column slot, 整形 is the default tab, raw shows the OTLP span JSON,
/// Esc / ✕ / re-click closes back to the cache column, and sanitized contexts
/// never fetch the raw span-detail route.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorInspectorPlaywrightTests
{
    [Fact]
    public async Task Inspector_CoalescesSameSpanDetailRequestWithErrorPanelAndDoesNotCacheFailure()
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
        var firstDetailRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDetailResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailRequestCount = 0;
        await page.RouteAsync($"**/traces/{MonitorRichTrace.TraceId}/spans/f201/detail", async route =>
        {
            detailRequestCount++;
            if (detailRequestCount == 1)
            {
                firstDetailRequest.SetResult();
                await releaseFirstDetailResponse.Task;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json",
                    Body = "{\"accepted\":false,\"error\":\"persistence_busy\"}",
                });
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = "{\"raw_span_json\":\"{\\\"spanId\\\":\\\"f201\\\"}\"}",
            });
        });

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await firstDetailRequest.Task;
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeVisibleAsync();
        Assert.Equal(1, detailRequestCount);

        releaseFirstDetailResponse.SetResult();
        await page.Locator(".inspector-tab", new PageLocatorOptions { HasTextString = "raw" }).ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("raw スパン JSON を取得できませんでした");

        await page.Keyboard.PressAsync("Escape");
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await page.Locator(".inspector-tab", new PageLocatorOptions { HasTextString = "raw" }).ClickAsync();
        await Expect(page.Locator(".inspector-raw-json")).ToContainTextAsync("f201");
        Assert.Equal(2, detailRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspector_OpensClosesAndRespectsRawBoundary(bool sanitizedOnly)
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
        var pageErrors = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        // The rich trace contains a recovered error, so the error panel (M7)
        // occupies the side column; the inspector swaps with it.
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();
        await page.Locator("#errors-only").UncheckAsync();
        await Expect(page.Locator("#span-inspector")).ToBeHiddenAsync();

        // Click the recovered-error tool span: inspector replaces the side panel.
        var detailResponse = sanitizedOnly
            ? null
            : page.WaitForResponseAsync(response => response.Url.Contains($"/traces/{MonitorRichTrace.TraceId}/spans/f201/detail", StringComparison.Ordinal));
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeVisibleAsync();
        await Expect(page.Locator("#error-panel")).ToBeHiddenAsync();
        await Expect(page.Locator("#cache-column")).ToBeHiddenAsync();
        await Expect(page.Locator(".inspector-name")).ToContainTextAsync("str_replace");
        // 整形 is the default tab.
        await Expect(page.Locator(".inspector-tab.active")).ToHaveTextAsync("整形");

        if (sanitizedOnly)
        {
            await Expect(page.Locator(".inspector-note").First).ToContainTextAsync("--sanitized-only");
            // The raw tab is unavailable and the raw route is never fetched.
            await page.Locator(".inspector-tab", new PageLocatorOptions { HasTextString = "raw" }).ClickAsync();
            await Expect(page.Locator(".inspector-note").First).ToContainTextAsync("raw タブは利用できません");
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/detail", StringComparison.Ordinal));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // The formatted view loads from the raw span-detail route (D043).
            Assert.Contains(requestedUrls, url => url.Contains($"/traces/{MonitorRichTrace.TraceId}/spans/f201/detail", StringComparison.Ordinal));
            Assert.Equal(200, (await detailResponse!).Status);
            await Expect(page.Locator("#span-inspector")).ToContainTextAsync("メタ");
            // Raw tab shows the OTLP span JSON.
            await page.Locator(".inspector-tab", new PageLocatorOptions { HasTextString = "raw" }).ClickAsync();
            await Expect(page.Locator(".inspector-raw-json")).ToContainTextAsync("\"spanId\"");
            await Expect(page.Locator(".inspector-raw-json")).ToContainTextAsync("f201");
        }

        // Esc closes the inspector and restores the error panel.
        await page.Keyboard.PressAsync("Escape");
        Assert.True(pageErrors.Count == 0, string.Join(" | ", pageErrors));
        await Expect(page.Locator("#span-inspector")).ToBeHiddenAsync();
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();

        // Re-clicking the same span opens, clicking it again closes.
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeVisibleAsync();
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeHiddenAsync();
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();

        // ✕ also closes.
        await page.Locator("#flow-view .tool-card.tool-error").ClickAsync();
        await page.Locator(".inspector-close").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeHiddenAsync();
        await Expect(page.Locator("#error-panel")).ToBeVisibleAsync();
    }
}

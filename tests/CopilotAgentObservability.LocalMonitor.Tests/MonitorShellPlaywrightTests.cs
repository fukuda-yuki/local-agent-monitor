using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Local Monitor v1 shared shell: full-width breadcrumb header, receiver and
/// Settings actions, and the extension-only Unified Settings modal host.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorShellPlaywrightTests
{
    [Fact]
    public async Task Shell_AtReferenceViewport_UsesNoSidebarHeaderGeometry()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });

        const string untrustedBreadcrumb = "<img src=x onerror=window.__breadcrumbExecuted=true>";
        await page.GotoAsync(
            $"{host.Url}/?breadcrumb={Uri.EscapeDataString(untrustedBreadcrumb)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var header = page.Locator(".monitor-shell-header");
        await Expect(header).ToHaveCSSAsync("height", "48px");
        await Expect(page.Locator(".monitor-content")).ToHaveCSSAsync("padding-left", "24px");
        await Expect(page.Locator(".monitor-content")).ToHaveCSSAsync("padding-right", "24px");
        await Expect(page.Locator(".monitor-sidebar, .sidebar-nav")).ToHaveCountAsync(0);
        await Expect(header.Locator("a, input, [role='search'], [data-ai-status], .kpi-grid")).ToHaveCountAsync(0);
        await Expect(header.Locator("button")).ToHaveCountAsync(2);
        await Expect(page.Locator("#shell-breadcrumb[data-breadcrumb-host]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#shell-breadcrumb a")).ToHaveCountAsync(0);
        await Expect(page.Locator("#shell-breadcrumb")).ToHaveTextAsync("Local Monitor");
        await Expect(page.Locator("#shell-breadcrumb img, #shell-breadcrumb script")).ToHaveCountAsync(0);
        Assert.False(await page.EvaluateAsync<bool>("() => window.__breadcrumbExecuted === true"));
        await Expect(page.Locator("#receiver-status-action")).ToContainTextAsync("正常 · 受信中");
        await Expect(page.Locator("#settings-action")).ToHaveTextAsync("設定");

        var positions = await page.EvaluateAsync<float[]>(
            """
            () => {
              const breadcrumb = document.querySelector("#shell-breadcrumb").getBoundingClientRect();
              const actions = document.querySelector(".monitor-shell-actions").getBoundingClientRect();
              return [breadcrumb.left, breadcrumb.right, actions.left, actions.right];
            }
            """);
        Assert.Equal(24f, positions[0]);
        Assert.True(positions[1] <= positions[2]);
        Assert.Equal(1342f, positions[3]);
    }

    [Fact]
    public async Task Shell_SettingsActions_OpenRequestedExtensionHooks()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync(
            """
            () => {
              window.__settingsRequests = [];
              document.addEventListener("cao-settings-open", event => {
                window.__settingsRequests.push(event.detail.section ?? null);
              });
            }
            """);

        var modal = page.Locator("#settings-modal");
        await Expect(modal).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(modal).ToHaveAttributeAsync("aria-labelledby", "settings-modal-title");
        await Expect(modal.Locator("[data-settings-navigation-host]")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-settings-content-host]")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-settings-navigation-host] > *, [data-settings-content-host] > *")).ToHaveCountAsync(0);

        await page.Locator("#receiver-status-action").ClickAsync();

        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal).ToHaveAttributeAsync("data-requested-section", "receiver");
        await Expect(page.Locator(".monitor-content")).ToBeVisibleAsync();
        var modalSize = await modal.EvaluateAsync<float[]>(
            "element => { const rect = element.getBoundingClientRect(); return [rect.width, rect.height]; }");
        Assert.Equal(960f, modalSize[0]);
        Assert.Equal(640f, modalSize[1]);

        await page.Locator("#settings-modal-close").ClickAsync();
        await page.Locator("#settings-action").ClickAsync();

        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal).Not.ToHaveAttributeAsync("data-requested-section", "receiver");
        var requests = await page.EvaluateAsync<string?[]>("() => window.__settingsRequests");
        Assert.Equal(2, requests.Length);
        Assert.Equal("receiver", requests[0]);
        Assert.Null(requests[1]);
    }

    [Fact]
    public async Task Shell_SettingsModal_ContainsBidirectionalFocus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync(
            """
            () => {
              const first = document.createElement("button");
              first.type = "button";
              first.id = "settings-extension-first";
              first.textContent = "first";
              document.querySelector("[data-settings-navigation-host]").append(first);

              const last = document.createElement("button");
              last.type = "button";
              last.id = "settings-extension-last";
              last.textContent = "last";

              const selectedRadio = document.createElement("input");
              selectedRadio.type = "radio";
              selectedRadio.name = "settings-extension-mode";
              selectedRadio.id = "settings-extension-radio-selected";
              selectedRadio.checked = true;

              const unselectedRadio = document.createElement("input");
              unselectedRadio.type = "radio";
              unselectedRadio.name = "settings-extension-mode";
              unselectedRadio.id = "settings-extension-radio-unselected";

              const inertContainer = document.createElement("div");
              inertContainer.inert = true;
              const inertButton = document.createElement("button");
              inertButton.type = "button";
              inertButton.id = "settings-extension-inert";
              inertContainer.append(inertButton);

              const negativeTabIndex = document.createElement("button");
              negativeTabIndex.type = "button";
              negativeTabIndex.id = "settings-extension-negative";
              negativeTabIndex.tabIndex = -2;

              document.querySelector("[data-settings-navigation-host]")
                .append(selectedRadio, unselectedRadio);
              document.querySelector("[data-settings-content-host]")
                .append(last, inertContainer, negativeTabIndex);
            }
            """);

        await page.Locator("#settings-action").ClickAsync();

        await Expect(page.Locator("#settings-modal-close")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.Locator("#settings-extension-last")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-modal-close")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-first")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-radio-selected")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-last")).ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-radio-unselected")).Not.ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-inert")).Not.ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-negative")).Not.ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#settings-action")).ToBeFocusedAsync();

        await page.Locator("#receiver-status-action").ClickAsync();
        await page.Locator("#settings-modal-close").ClickAsync();
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#receiver-status-action")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Shell_NarrowHealthFailure_RemainsReachableWithoutSensitiveRequests()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 360, Height = 640 },
        });
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);
        var healthRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/health/ready", async route =>
        {
            healthRequested.TrySetResult(true);
            await releaseHealth.Task;
            await route.AbortAsync();
        });

        try
        {
            await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await healthRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var headerBefore = await page.Locator(".monitor-shell-header").EvaluateAsync<float[]>(
                "element => { const rect = element.getBoundingClientRect(); return [rect.top, rect.height]; }");

            releaseHealth.TrySetResult(true);
            await Expect(page.Locator("#receiver-status-action")).ToContainTextAsync("未接続");
            var headerAfter = await page.Locator(".monitor-shell-header").EvaluateAsync<float[]>(
                "element => { const rect = element.getBoundingClientRect(); return [rect.top, rect.height]; }");

            Assert.Equal(headerBefore, headerAfter);
            Assert.Equal(0, await page.EvaluateAsync<int>(
                "() => document.documentElement.scrollWidth - document.documentElement.clientWidth"));
            var actionRight = await page.Locator(".monitor-shell-actions").EvaluateAsync<float>(
                "element => element.getBoundingClientRect().right");
            Assert.True(actionRight <= 348f);

            await page.Locator("#settings-action").ClickAsync();
            var modalBounds = await page.Locator("#settings-modal").EvaluateAsync<float[]>(
                """
                element => {
                  const rect = element.getBoundingClientRect();
                  return [rect.left, rect.top, rect.right, rect.bottom];
                }
                """);
            Assert.True(modalBounds[0] >= 20f);
            Assert.True(modalBounds[1] >= 20f);
            Assert.True(modalBounds[2] <= 340f);
            Assert.True(modalBounds[3] <= 620f);
            Assert.Equal(0, await page.EvaluateAsync<int>(
                "() => document.documentElement.scrollWidth - document.documentElement.clientWidth"));

            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("prompt-label", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                requestedUrls,
                url => url.Contains("/api/monitor/trace-list?limit=1", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("user.email", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requestedUrls, url => url.Contains("/health/ready", StringComparison.Ordinal));
        }
        finally
        {
            releaseHealth.TrySetResult(true);
        }
    }

    private static async Task<RunningMonitorHost> StartReadyHostAsync(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        return await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            Health = MonitorTestHealth.Ready(time),
        });
    }
}

using CopilotAgentObservability.LocalMonitor.Analysis;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 Copilot drawer (§6.6, D045) with a fake completing runner: open via
/// the standing header button, run an analysis, follow-up chat resends the
/// client-held history, and Esc closes.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public class MonitorDrawerPlaywrightTests
{
    [Fact]
    public async Task Drawer_RunAndFollowUpChat_ResendHistory()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        var analysisStore = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        var runner = new RecordingRunner(analysisStore);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            AnalysisStore = analysisStore,
            AnalysisRunner = runner,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#copilot-drawer")).ToBeHiddenAsync();

        // Open via the standing header button; boundary copy is visible.
        await page.Locator("#copilot-open").ClickAsync();
        await Expect(page.Locator("#copilot-drawer")).ToBeVisibleAsync();
        await Expect(page.Locator(".drawer-boundary")).ToHaveTextAsync("ローカル SDK 経由 · raw はローカルから出ません");
        await Expect(page.Locator("#flow-card")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("dimmed-behind-drawer"));

        // First run: focus-based analysis, findings appear.
        await page.Locator("#drawer-focus").SelectOptionAsync("tokens");
        await page.Locator("#drawer-run").ClickAsync();
        await Expect(page.Locator(".drawer-run-chip").First).ToContainTextAsync("観点「トークン」で解析を実行");
        await Expect(page.Locator(".drawer-answer-text").First).ToContainTextAsync("FAKE_FINDINGS run 1");

        // Follow-up via the input: a new run is created with question + history.
        await page.Locator("#drawer-question").FillAsync("削減余地は?");
        await page.Locator("#drawer-send").ClickAsync();
        await Expect(page.Locator(".drawer-question-bubble")).ToHaveTextAsync("削減余地は?");
        await Expect(page.Locator(".drawer-answer-text")).ToHaveCountAsync(2);

        Assert.Equal(2, runner.Contexts.Count);
        Assert.Null(runner.Contexts[0].Question);
        Assert.Equal("削減余地は?", runner.Contexts[1].Question);
        var turn = Assert.Single(runner.Contexts[1].History!);
        Assert.Contains("FAKE_FINDINGS run 1", turn.Answer);

        // Suggestion chips submit as follow-ups too (history now has 2 turns).
        await page.Locator(".suggest-chip").First.ClickAsync();
        await Expect(page.Locator(".drawer-answer-text")).ToHaveCountAsync(3);
        Assert.Equal(3, runner.Contexts.Count);
        Assert.Equal(2, runner.Contexts[2].History!.Count);

        // Esc closes the drawer (and only the drawer).
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#copilot-drawer")).ToBeHiddenAsync();
        await Expect(page.Locator("#flow-card")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("dimmed-behind-drawer"));
    }

    [Fact]
    public async Task Drawer_NarrowViewportUsesAvailableWidthWithoutSidebarReservation()
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
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 360, Height = 640 },
        });

        await page.GotoAsync(
            $"{host.Url}/traces/{MonitorRichTrace.TraceId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#copilot-open").ClickAsync();

        var drawer = page.Locator("#copilot-drawer");
        await Expect(drawer).ToBeVisibleAsync();
        var bounds = await drawer.EvaluateAsync<float[]>(
            """
            element => {
              const rect = element.getBoundingClientRect();
              return [rect.left, rect.right, rect.width, element.clientWidth, element.scrollWidth];
            }
            """);
        Assert.Equal(24f, bounds[0]);
        Assert.Equal(336f, bounds[1]);
        Assert.Equal(312f, bounds[2]);
        Assert.True(bounds[4] <= bounds[3], $"Drawer content overflows horizontally: {bounds[4]} > {bounds[3]}.");
        await Expect(page.Locator("#drawer-focus")).ToBeVisibleAsync();
        await Expect(page.Locator("#drawer-question")).ToBeVisibleAsync();
        await page.Locator("#drawer-close").ClickAsync();
        await Expect(drawer).ToBeHiddenAsync();
    }

    [Fact]
    public async Task SettingsEscape_ClosesOnlyTopmostModalBeforeTraceInteractions()
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

        await page.GotoAsync(
            $"{host.Url}/traces/{MonitorRichTrace.TraceId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var selectedSpan = page.Locator("#flow-view [data-span-id='f201']").First;
        await Expect(selectedSpan).ToBeVisibleAsync();
        await selectedSpan.ClickAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"[?&]span=f201(?:&|$)"));

        await page.Locator("#copilot-open").ClickAsync();
        await Expect(page.Locator("#copilot-drawer")).ToBeVisibleAsync();
        await page.Locator("#settings-action").EvaluateAsync("button => button.click()");
        await page.Locator("[data-settings-content-host]").EvaluateAsync(
            """
            host => {
              const child = document.createElement("dialog");
              child.id = "settings-extension-child-dialog";
              child.setAttribute("aria-label", "Extension confirmation");

              const action = document.createElement("button");
              action.type = "button";
              action.textContent = "Confirm";
              child.append(action);
              host.append(child);

              window.__settingsChildCancelCount = 0;
              child.addEventListener("cancel", () => {
                window.__settingsChildCancelCount += 1;
              });
              child.showModal();
              action.focus();
            }
            """);

        var childDialog = page.Locator("#settings-extension-child-dialog");
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await Expect(childDialog).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Escape");

        await Expect(childDialog).ToBeHiddenAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__settingsChildCancelCount"));
        await Expect(page.Locator("#copilot-drawer")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"[?&]span=f201(?:&|$)"));

        await page.Keyboard.PressAsync("Escape");

        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#settings-action")).ToBeFocusedAsync();
        await Expect(page.Locator("#copilot-drawer")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"[?&]span=f201(?:&|$)"));

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#copilot-drawer")).ToBeHiddenAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"[?&]span=f201(?:&|$)"));

        await page.Keyboard.PressAsync("Escape");
        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"[?&]span=f201(?:&|$)"));
    }

    private sealed class RecordingRunner : IMonitorAnalysisRunner
    {
        private readonly IMonitorAnalysisStore analysisStore;

        public RecordingRunner(IMonitorAnalysisStore analysisStore)
        {
            this.analysisStore = analysisStore;
        }

        public List<MonitorAnalysisContext> Contexts { get; } = new();

        public Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            analysisStore.MarkRunning(context.RunId, DateTimeOffset.UnixEpoch.AddMinutes(4));
            analysisStore.CompleteRun(
                context.RunId,
                Assert.IsType<MonitorAnalysisOperationToken>(context.OperationToken),
                null,
                $"FAKE_FINDINGS run {context.RunId}",
                DateTimeOffset.UnixEpoch.AddMinutes(5));
            return Task.CompletedTask;
        }
    }
}

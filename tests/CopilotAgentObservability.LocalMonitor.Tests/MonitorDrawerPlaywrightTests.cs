using CopilotAgentObservability.LocalMonitor.Analysis;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 Copilot drawer (§6.6, D045) with a fake completing runner: open via
/// the standing header button, run an analysis, follow-up chat resends the
/// client-held history, Esc closes, and the drawer is absent under
/// --sanitized-only.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorDrawerPlaywrightTests
{
    [Fact]
    public async Task Drawer_RunAndFollowUpChat_ResendHistory()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        var analysisStore = new SqliteMonitorAnalysisStore(temp.DatabasePath);
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
    public async Task Drawer_IsAbsentUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#copilot-drawer")).ToHaveCountAsync(0);
        await Expect(page.Locator("#copilot-open")).ToHaveCountAsync(0);
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
                $"FAKE_FINDINGS run {context.RunId}",
                DateTimeOffset.UnixEpoch.AddMinutes(5));
            return Task.CompletedTask;
        }
    }
}

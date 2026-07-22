using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Alerts;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class AlertCenterPlaywrightTests
{
    private static readonly JsonSerializerOptions AlertCenterJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_CriticalEvidenceRecurringAndKeyboardFlowStaySanitizedAndAccessible()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot(
            [
                Alert(AlertA, "session-a", "trace-a", "span-a", "open"),
                Alert(AlertB, "session-b", "trace-b", "span-b", "acknowledged"),
                Alert(AlertC, "session-c", "trace-c", "span-c", "superseded"),
                Alert(AlertD, "session-d", "trace-d", "span-d", "resolved"),
            ],
            [Recurring("incomplete_snapshot")],
            [Coverage(), ExactCoverage()],
            snapshotState: "incomplete",
            omittedReceiptCount: null));
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#alert-sanitized-state")).ToContainTextAsync("sanitized-only");
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(4);
        await Expect(page.Locator("#alert-detail-heading")).ToContainTextAsync("High tool failure ratio");
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("incomplete");
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("omitted unknown");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("supported_at_evaluation");
        await Expect(page.Locator("#alert-detail .alert-evidence-link[href^='/traces']")).ToHaveAttributeAsync("href", "/traces/trace-a?span=span-a");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("expired");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("unknown");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("missing");
        await Expect(page.Locator("#alert-rows .alert-row").First).ToContainTextAsync("failure-ratio.critical 0.7 ratio");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("incomplete_snapshot");
        await Expect(page.Locator("#alert-recurring")).Not.ToContainTextAsync("2 Sessions");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("missing_required_capability");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("context unknown");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("2026-07-22");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("trace-repo");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("session-repo");
        await Expect(page.Locator("#alert-rows .alert-row").First).ToContainTextAsync("初回");
        await Expect(page.Locator("#alert-rows .alert-row").First).ToContainTextAsync("最終");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("mixed completeness");
        await Expect(page.Locator("#alert-action-comment")).ToHaveAttributeAsync("maxlength", "256");
        await Expect(page.Locator(".sidebar-nav a")).ToHaveCountAsync(2);
        await Expect(page.Locator(".sidebar-nav a[href='/alerts']")).ToHaveCountAsync(0);
        Assert.Equal(0, await page.Locator("#alert-detail script").CountAsync());

        var second = page.Locator("#alert-rows .alert-row").Nth(1);
        Assert.Null(await second.GetAttributeAsync("role"));
        var selectButton = second.Locator("[data-alert-select]");
        var reachedSecondRow = false;
        for (var index = 0; index < 40; index++)
        {
            await page.Keyboard.PressAsync("Tab");
            reachedSecondRow = await page.EvaluateAsync<bool>(
                "alertId => document.activeElement?.getAttribute('data-alert-select') === alertId",
                AlertB);
            if (reachedSecondRow) break;
        }
        Assert.True(reachedSecondRow, "Tab order did not reach the second Alert Center row selector.");
        await page.Keyboard.PressAsync("Enter");
        await Expect(selectButton).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(page.Locator("#alert-detail-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("選択");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("Lifecycle history");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("acknowledge");

        await page.Locator($"[data-alert-select='{AlertC}']").ClickAsync();
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("superseded");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("許可された操作はありません");
        await page.Locator($"[data-alert-select='{AlertD}']").ClickAsync();
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("resolved");
        await Expect(page.Locator("[data-alert-action='reopen']")).ToBeVisibleAsync();

        var filterResponseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("/api/alert-center/v1/alerts?", StringComparison.Ordinal)
            && response.Url.Contains("state=acknowledged", StringComparison.Ordinal));
        await page.Locator("#alert-filter-state").SelectOptionAsync("acknowledged");
        await filterResponseTask;
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("state=acknowledged"));
        Assert.Contains(readModel.Queries, query => query.State == "acknowledged");

        await page.GotoAsync($"{host.Url}/diagnostics?session_id=session-a", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#doctor-session-alert-link")).ToHaveAttributeAsync("href", "/alerts?session_id=session-a");
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_PaginatesEveryMatchingAlertAndReportsTheVisibleRange()
    {
        using var temp = NewTemp();
        var readModel = new PagingFixtureReadModel();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(100);
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("1–100 / 101");
        await Expect(page.Locator("#alert-page-next")).ToBeEnabledAsync();
        await page.Locator("#alert-page-next").ClickAsync();

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("offset=100"));
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(1);
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("101–101 / 101");
        await Expect(page.Locator("#alert-page-next")).ToBeDisabledAsync();
        await Expect(page.Locator("#alert-page-previous")).ToBeEnabledAsync();
        Assert.Contains(readModel.Queries, query => query.Offset == 100 && query.Limit == 100);

        await page.Locator("#alert-page-previous").ClickAsync();
        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("offset="));
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(100);

        await page.Locator("#alert-page-next").ClickAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("offset=100"));
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(1);
        await page.Locator("#alert-filter-severity").SelectOptionAsync("critical");
        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("offset="));
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(100);
        Assert.Contains(readModel.Queries, query => query.Offset == 0 && query.Severity == "critical");

        await Expect(page.Locator("#alert-filter-rule option[value='off-page-rule']")).ToHaveCountAsync(1);
        await Expect(page.Locator("#alert-filter-source option[value='claude-code']")).ToHaveCountAsync(1);
        await page.Locator("#alert-filter-rule").SelectOptionAsync("off-page-rule");
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-filter-rule")).ToHaveValueAsync("off-page-rule");
        await Expect(page.Locator("#alert-filter-rule option[value='off-page-rule']")).ToHaveCountAsync(1);
        await page.Locator("#alert-filter-source").SelectOptionAsync("claude-code");
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-filter-source")).ToHaveValueAsync("claude-code");
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_EmptyAndApiErrorAreDistinct()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot([], [], [Coverage()]));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-empty")).ToContainTextAsync("アラートはありません");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("アラートではありません");

        readModel.Snapshot = Snapshot(
            [],
            [],
            [],
            snapshotState: "incomplete",
            omittedReceiptCount: null,
            coverageState: "incomplete",
            omittedCoverageFactCount: null);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-empty")).ToContainTextAsync("0 件とは断定できません");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("0 件とは断定できません");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("省略件数は不明");
        await Expect(page.Locator("#alert-coverage")).ToContainTextAsync("0 件とは断定できません");

        readModel.Status = AlertCenterReadStatus.Unavailable;
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-error")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-error")).ToContainTextAsync("読み込めませんでした");
        await Expect(page.Locator("#alert-empty")).ToBeHiddenAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_CustomPeriodRejectsMissingReversedAndOverlongRangesBeforeReading()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot([], [], []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();

        await page.Locator("#alert-filter-period").SelectOptionAsync("custom");
        var readsBeforeInvalidSubmit = readModel.Queries.Count;
        await page.Locator("#alert-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("両方");
        Assert.Equal(readsBeforeInvalidSubmit, readModel.Queries.Count);

        await page.Locator("#alert-filter-from").FillAsync("2026-07-23");
        await page.Locator("#alert-filter-to").FillAsync("2026-07-22");
        await page.Locator("#alert-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("以前");
        Assert.Equal(readsBeforeInvalidSubmit, readModel.Queries.Count);

        await page.Locator("#alert-filter-from").FillAsync("2025-07-22");
        await page.Locator("#alert-filter-to").FillAsync("2026-07-23");
        await page.Locator("#alert-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("366 日以内");
        Assert.Equal(readsBeforeInvalidSubmit, readModel.Queries.Count);

        await page.Locator("#alert-filter-from").FillAsync("2025-07-23");
        var responseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("from=2025-07-23", StringComparison.Ordinal)
            && response.Url.Contains("to=2026-07-23", StringComparison.Ordinal));
        await page.Locator("#alert-filters button[type='submit']").ClickAsync();
        await responseTask;
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("from=2025-07-23.*to=2026-07-23"));
        Assert.Contains(readModel.Queries, query => query.From == new DateOnly(2025, 7, 23) && query.To == new DateOnly(2026, 7, 23));
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_SafeMarkupLikeFilterValueRemainsInertText()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot([], [], []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string label = "<img src=x onerror=window.__alertMarkupExecuted=true>";

        await page.GotoAsync(
            $"{host.Url}/alerts?repository={Uri.EscapeDataString(label)}&period=30d",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#alert-filter-repository")).ToHaveValueAsync(label);
        await Expect(page.Locator("#alert-filters img")).ToHaveCountAsync(0);
        Assert.False(await page.EvaluateAsync<bool>("() => window.__alertMarkupExecuted === true"));
        Assert.Contains(readModel.Queries, query => query.Repository == label);

        const string exactRepository = " exact repository label ";
        const string exactWorkspace = " exact workspace label ";
        await page.Locator("#alert-filter-repository").FillAsync(exactRepository);
        await page.Locator("#alert-filter-workspace").FillAsync(exactWorkspace);
        var responseTask = page.WaitForResponseAsync(response =>
            response.Url.Contains("/api/alert-center/v1/alerts?", StringComparison.Ordinal));
        await page.Locator("#alert-filters button[type='submit']").ClickAsync();
        await responseTask;
        await Expect(page.Locator("#alert-filter-repository")).ToHaveValueAsync(exactRepository);
        await Expect(page.Locator("#alert-filter-workspace")).ToHaveValueAsync(exactWorkspace);
        Assert.Contains(readModel.Queries, query =>
            query.Repository == exactRepository && query.Workspace == exactWorkspace);
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_ProductionApiAndBrowserNeverExposeStoredRawToolArgumentOrUnsafeScope()
    {
        const string rawMarker = "RAW_ALERT_CENTER_BROWSER_ARGUMENT_MARKER";
        const string unsafeRepository = @"\Device\HarddiskVolume1\browser-private-repository";
        const string unsafeWorkspace = "private/browser-workspace";
        using var temp = NewTemp();
        var sessionId = AlertCenterRouteTests.SeedPersistedTraceAndSession(
            temp,
            "raw-browser-boundary",
            authoritativeToolStatus: true,
            rawPayloadMarker: rawMarker,
            repository: unsafeRepository,
            workspace: unsafeWorkspace);
        _ = AlertCenterRouteTests.AppendPersistedAlert(temp, sessionId, "raw-browser-boundary");
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: AlertCenterRouteTests.OptionsForProductionStore());

        using (var apiResponse = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d"))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, apiResponse.StatusCode);
            var apiText = await apiResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(rawMarker, apiText, StringComparison.Ordinal);
            Assert.DoesNotContain(unsafeRepository, apiText, StringComparison.Ordinal);
            Assert.DoesNotContain(unsafeWorkspace, apiText, StringComparison.Ordinal);
        }

        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(1);
        var pageText = await page.ContentAsync();
        Assert.DoesNotContain(rawMarker, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeRepository, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeWorkspace, pageText, StringComparison.Ordinal);
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("Scope state");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("unknown");
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_SupersededFilterResponseCannotOverwriteNewerUrlOrRows()
    {
        using var temp = NewTemp();
        var stale = Alert(AlertA, "session-a", "trace-a", "span-a", "open");
        var fresh = Alert(AlertB, "session-b", "trace-b", "span-b", "open") with
        {
            Severity = "warning",
            Rule = stale.Rule with { RuleId = "fresh-warning-rule", Title = "Fresh warning result" },
        };
        var readModel = new FixtureReadModel(Snapshot([stale, fresh], [], []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/alert-center/v1/alerts?*", async route =>
        {
            var query = new Uri(route.Request.Url).Query;
            if (query.Contains("severity=critical", StringComparison.Ordinal))
            {
                staleStarted.TrySetResult();
                await releaseStale.Task;
                try
                {
                    await route.FulfillAsync(JsonResponse(Snapshot([stale], [], [])));
                }
                catch (PlaywrightException)
                {
                    // AbortController may already have cancelled this superseded request.
                }
                finally
                {
                    staleFinished.TrySetResult();
                }
                return;
            }
            if (query.Contains("severity=warning", StringComparison.Ordinal))
            {
                await route.FulfillAsync(JsonResponse(Snapshot([fresh], [], [])));
                return;
            }
            await route.ContinueAsync();
        });

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(2);
        await page.Locator("#alert-filter-severity").SelectOptionAsync("critical");
        await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await page.Locator("#alert-filter-severity").SelectOptionAsync("warning");
        await Expect(page.Locator("#alert-detail-heading")).ToHaveTextAsync("Fresh warning result");
        releaseStale.TrySetResult();
        await staleFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("severity=warning"));
        await Expect(page.Locator("#alert-detail-heading")).ToHaveTextAsync("Fresh warning result");
        await Expect(page.Locator("#alert-rows")).Not.ToContainTextAsync("High tool failure ratio");
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_RendersSupportedLowNAndSourceSeparatedRecurringGroups()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot(
            [Alert(AlertA, "session-a", "trace-a", "span-a", "open")],
            [
                Recurring("supported", "high-tool-failure-ratio", "github-copilot-vscode", 2),
                Recurring("low_n", "single-session-rule", "github-copilot-vscode", 1),
                Recurring("supported", "claude-recurring-rule", "claude-code", 2),
            ],
            []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#alert-recurring .alert-recurring-card")).ToHaveCountAsync(3);
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("supported · 2 Sessions");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("low_n · 1 Sessions");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("github-copilot-vscode@1.0.4");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("claude-code@1.0.4");
    }

    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_LifecycleSuccessAndStaleConflictUseFrozenApiWithoutSilentOverwrite()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot([Alert(AlertA, "session-a", "trace-a", "span-a", "open")], [], []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var calls = 0;
        var bodies = new List<string>();
        await page.RouteAsync("**/api/alerts/v1/*/lifecycle/actions", async route =>
        {
            calls++;
            bodies.Add(route.Request.PostData ?? string.Empty);
            Assert.Equal("local-monitor", await route.Request.HeaderValueAsync("x-monitor-csrf"));
            var key = await route.Request.HeaderValueAsync("Idempotency-Key");
            Assert.StartsWith("aid1_", key, StringComparison.Ordinal);
            Assert.Equal(48, key!.Length);
            if (calls is 1 or 2)
            {
                var state = calls == 1 ? "acknowledged" : "dismissed";
                var updated = Alert(AlertA, "session-a", "trace-a", "span-a", state);
                if (calls == 2)
                {
                    var acknowledge = updated.Lifecycle.History[0] with
                    {
                        Action = "acknowledge",
                        PreviousState = "open",
                        State = "acknowledged",
                    };
                    var dismiss = updated.Lifecycle.History[0] with
                    {
                        Revision = 2,
                        Action = "dismiss",
                        PreviousState = "acknowledged",
                        State = "dismissed",
                    };
                    updated = updated with
                    {
                        Lifecycle = new("dismissed", 2, "2026-07-23T12:00:00.0000000Z", ["reopen"], [dismiss, acknowledge]),
                    };
                }
                readModel.Snapshot = Snapshot([updated], [], []);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"schema_version\":\"alert.lifecycle.v1\",\"alert_id\":\"" + AlertA + "\",\"state\":\"" + state + "\",\"revision\":" + calls + ",\"last_occurred_at\":\"2026-07-23T12:00:00.0000000Z\",\"event\":{},\"idempotent_replay\":false}",
                });
            }
            else
            {
                if (calls == 4) readModel.Status = AlertCenterReadStatus.Unavailable;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/json",
                    Body = "{\"schema_version\":\"alert.lifecycle.v1\",\"error\":\"alert_revision_conflict\"}",
                });
            }
        });

        await page.GotoAsync($"{host.Url}/alerts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-alert-action='acknowledge']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("更新しました");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("acknowledged");
        using (var body = JsonDocument.Parse(bodies[0]))
        {
            Assert.Equal("alert.lifecycle.v1", body.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("acknowledge", body.RootElement.GetProperty("action").GetString());
            Assert.Equal(0, body.RootElement.GetProperty("expected_revision").GetInt64());
        }

        await page.Locator("[data-alert-action='dismiss']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("dismiss で更新しました");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("dismissed");
        using (var body = JsonDocument.Parse(bodies[1]))
        {
            Assert.Equal("dismiss", body.RootElement.GetProperty("action").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("expected_revision").GetInt64());
        }

        await page.Locator("[data-alert-action='reopen']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("更新が競合");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("dismissed");
        Assert.Equal(3, calls);

        await page.Locator("[data-alert-action='reopen']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("最新状態を再読み込みできませんでした");
        await Expect(page.Locator("#alert-error")).ToBeVisibleAsync();
        Assert.Equal(4, calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task Overview_UsesSameCriticalAlertDtoAndLinksExactSelection()
    {
        using var temp = NewTemp();
        var critical = Alert(AlertA, "session-a", "trace-a", "span-a", "open");
        var warning = Alert(AlertB, "session-b", "trace-b", "span-b", "open") with
        {
            Severity = "warning",
            Source = new("claude-code", "1.0.4", "supported_at_evaluation"),
        };
        var readModel = new FilteringFixtureReadModel([critical, warning], [Recurring()])
        {
            SnapshotState = "incomplete",
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("open 2");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("critical 1");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("warning 1");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("github-copilot-vscode@1.0.4 1");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("claude-code@1.0.4 1");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("top recurring rule は確定できません");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("High tool failure ratio");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("critical");
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("今日");
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("最新とは断定できません");
        await Expect(page.Locator("#overview-alert-body a")).ToHaveAttributeAsync("href", $"/alerts?alert={AlertA}&period=today");

        readModel.Alerts = [];
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("0 件とは断定できません");

        readModel.Alerts = [critical, warning];
        readModel.SnapshotState = "complete";
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("top recurring · high-tool-failure-ratio@1 · 2 Sessions");
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("latest critical");
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("最終観測");

        await page.RouteAsync("**/api/monitor/overview?period=7d", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 503,
            ContentType = "application/json",
            Body = "{}",
        }));
        readModel.Alerts = [critical with { AlertId = AlertC }];
        await page.Locator("#period-toggle .period-btn[data-period='7d']").ClickAsync();
        await Expect(page.Locator("#overview-alert-body a")).ToHaveAttributeAsync("href", $"/alerts?alert={AlertC}&period=7d");
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("7日");
        await Expect(page.Locator("#overview-alert-card .panel-link")).ToHaveAttributeAsync("href", "/alerts?state=open&period=7d");
    }

    [Fact(Timeout = 60_000)]
    public async Task Overview_OlderPeriodAlertResponsesCannotOverwriteTheCurrentPeriodCard()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot([], [], []));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var stale = Alert(AlertA, "session-stale", "trace-stale", "span-stale", "open");
        var fresh = Alert(AlertB, "session-fresh", "trace-fresh", "span-fresh", "open") with
        {
            Rule = stale.Rule with { RuleId = "fresh-seven-day-rule", Title = "Fresh seven-day alert" },
        };
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStartedCount = 0;
        var staleFinishedCount = 0;
        await page.RouteAsync("**/api/alert-center/v1/alerts?*", async route =>
        {
            var query = new Uri(route.Request.Url).Query;
            var alert = query.Contains("period=7d", StringComparison.Ordinal) ? fresh : stale;
            var items = query.Contains("severity=warning", StringComparison.Ordinal)
                ? Array.Empty<AlertCenterAlert>()
                : [alert];
            var response = Snapshot(items, [], [], totalCount: items.Length);
            if (query.Contains("period=today", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref staleStartedCount) == 3) staleStarted.TrySetResult();
                await releaseStale.Task;
                try
                {
                    await route.FulfillAsync(JsonResponse(response));
                }
                catch (PlaywrightException)
                {
                    // The newer period aborts every old-period request.
                }
                finally
                {
                    if (Interlocked.Increment(ref staleFinishedCount) == 3) staleFinished.TrySetResult();
                }
                return;
            }
            await route.FulfillAsync(JsonResponse(response));
        });

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await page.Locator("#period-toggle .period-btn[data-period='7d']").ClickAsync();
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("Fresh seven-day alert");
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("7日");
        await Expect(page.Locator("#overview-alert-body a")).ToHaveAttributeAsync("href", $"/alerts?alert={AlertB}&period=7d");
        releaseStale.TrySetResult();
        await staleFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("Fresh seven-day alert");
        await Expect(page.Locator("#overview-alert-body")).Not.ToContainTextAsync("High tool failure ratio");
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("7日");
    }

    private const string AlertA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AlertB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AlertC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string AlertD = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private static MonitorHostTestOptions Options(IAlertCenterReadModel readModel) => new()
    {
        AlertCenterReadModel = readModel,
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static RouteFulfillOptions JsonResponse(AlertCenterSnapshot value) => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = JsonSerializer.Serialize(value, AlertCenterJson),
    };

    private static AlertCenterSnapshot Snapshot(
        IReadOnlyList<AlertCenterAlert> alerts,
        IReadOnlyList<AlertCenterRecurringGroup> recurring,
        IReadOnlyList<AlertCenterCoverageFact> coverage,
        string snapshotState = "complete",
        long? omittedReceiptCount = 0,
        int offset = 0,
        int limit = 50,
        long? totalCount = null,
        string coverageState = "complete",
        long? omittedCoverageFactCount = 0) => new(
            AlertCenterContractVersions.Center,
            "2026-07-23T12:00:00.0000000Z",
            new(null, null, null, null, null, null, null, null, null, null, "2026-06-24", "2026-07-23", offset, limit),
            snapshotState,
            omittedReceiptCount,
            coverageState,
            omittedCoverageFactCount,
            totalCount ?? alerts.Count,
            alerts,
            recurring,
            coverage);

    private static AlertCenterAlert Alert(string alertId, string sessionId, string traceId, string spanId, string state) => new(
        alertId,
        "critical",
        "open",
        new(
            state,
            state == "open" ? 0 : 1,
            state == "open" ? null : "2026-07-23T11:00:00.0000000Z",
            state switch
            {
                "open" => ["acknowledge", "dismiss", "resolve"],
                "acknowledged" => ["dismiss", "resolve"],
                "dismissed" or "resolved" => ["reopen"],
                _ => [],
            },
            state == "open"
                ? []
                : [new(
                    1,
                    state switch
                    {
                        "acknowledged" => "acknowledge",
                        "dismissed" => "dismiss",
                        "resolved" => "resolve",
                        _ => "supersede",
                    },
                    "open",
                    state,
                    "2026-07-23T11:00:00.0000000Z",
                    state == "superseded" ? "local_system" : "local_user",
                    "user_reviewed",
                    state == "superseded" ? alertId : null,
                    state == "superseded" ? AlertA : null,
                    "alert_lifecycle_updated")]),
        new(
            "high-tool-failure-ratio",
            "1",
            "registered",
            "High tool failure ratio",
            "Reports a high error ratio over authoritative success and error tool statuses.",
            "Reports a high error ratio over authoritative success and error tool statuses.",
            "trace",
            "trace",
            ["tool-call-status"],
            [new("failure-ratio", "ratio", "higher_is_worse", 0, 1, 0.4m, 0.7m)]),
        [new("failure-ratio", "ratio", 0.8m)],
        [new("failure-ratio.critical", "ratio", 0.7m), new("failure-ratio.warning", "ratio", 0.4m)],
        new("github-copilot-vscode", "1.0.4", "supported_at_evaluation"),
        sessionId,
        traceId,
        new("conflict", null, null, "trace-repo", "trace-workspace", "session-repo", "session-workspace"),
        new("partial", ["schema_drift_detected"]),
        "2026-07-22T10:00:00.0000000Z",
        "2026-07-22T10:00:04.0000000Z",
        "<script>fixture-markup()</script>",
        [
            new("span", $"evidence-{spanId}", sessionId, traceId, spanId, null, null, null, "2026-07-22T10:00:00.0000000Z", "available", null, $"/traces/{traceId}?span={spanId}"),
            new("event", $"event-{spanId}", sessionId, traceId, null, null, $"event-{spanId}", null, "2026-07-22T10:00:01.0000000Z", "expired", "expired", $"/diagnostics?session_id={sessionId}"),
            new("tool_call", $"tool-{spanId}", sessionId, traceId, spanId, null, null, $"tool-{spanId}", "2026-07-22T10:00:02.0000000Z", "unknown", null, null),
            new("turn", $"missing-{spanId}", sessionId, traceId, null, $"turn-{spanId}", null, null, "2026-07-22T10:00:03.0000000Z", "missing", null, null),
        ],
        4,
        new([], []),
        "partial:schema_drift_detected",
        "evaluation-fixture");

    private static AlertCenterRecurringGroup Recurring(
        string state = "supported",
        string ruleId = "high-tool-failure-ratio",
        string source = "github-copilot-vscode",
        int distinctSessions = 2) => new(
        state,
        ruleId,
        "1",
        "repo-a",
        "workspace-a",
        source,
        "1.0.4",
        "2026-07-22",
        "2026-06-24",
        "2026-07-23",
        distinctSessions,
        distinctSessions,
        "2026-07-22T10:00:00.0000000Z",
        "2026-07-22T10:00:04.0000000Z",
        distinctSessions == 1
            ? new Dictionary<string, int> { ["partial"] = 1 }
            : new Dictionary<string, int> { ["partial"] = 1, ["rich"] = distinctSessions - 1 },
        new[] { AlertA, AlertB }.Take(distinctSessions).ToArray(),
        Enumerable.Range(1, distinctSessions).Select(index => $"session-{index}").ToArray(),
        []);

    private static AlertCenterCoverageFact Coverage() => new(
        new string('e', 64),
        "near-context-limit-turn",
        "1",
        "missing_required_capability",
        ["effective-context-limit"],
        "unknown",
        null,
        null,
        null,
        null,
        null);

    private static AlertCenterCoverageFact ExactCoverage() => new(
        new string('f', 64),
        "high-tool-failure-ratio",
        "1",
        "minimum_sample_not_met",
        [],
        "exact_evaluation",
        "github-copilot-vscode",
        "1.0.4",
        "session-a",
        "trace-a",
        "2026-07-22");

    private sealed class FixtureReadModel(AlertCenterSnapshot snapshot) : IAlertCenterReadModel
    {
        internal AlertCenterReadStatus Status { get; set; } = AlertCenterReadStatus.Success;
        internal AlertCenterSnapshot Snapshot { get; set; } = snapshot;
        internal List<AlertCenterQuery> Queries { get; } = [];

        public AlertCenterReadResult Read(AlertCenterQuery query)
        {
            Queries.Add(query);
            return Status == AlertCenterReadStatus.Success
                ? new(Status, Snapshot)
                : new(Status);
        }
    }

    private sealed class FilteringFixtureReadModel(
        IReadOnlyList<AlertCenterAlert> alerts,
        IReadOnlyList<AlertCenterRecurringGroup> recurring) : IAlertCenterReadModel
    {
        internal IReadOnlyList<AlertCenterAlert> Alerts { get; set; } = alerts;
        internal IReadOnlyList<AlertCenterRecurringGroup> Recurring { get; set; } = recurring;
        internal string SnapshotState { get; set; } = "complete";

        public AlertCenterReadResult Read(AlertCenterQuery query)
        {
            var filtered = Alerts
                .Where(item => query.State is null || item.Lifecycle.State == query.State)
                .Where(item => query.Severity is null || item.Severity == query.Severity)
                .Where(item => query.RuleId is null || item.Rule.RuleId == query.RuleId)
                .Where(item => query.SourceSurface is null || item.Source.Surface == query.SourceSurface)
                .ToArray();
            var snapshot = Snapshot(
                filtered.Skip(query.Offset).Take(query.Limit).ToArray(),
                query.Severity is null ? Recurring : [],
                [],
                SnapshotState,
                SnapshotState == "incomplete" ? null : 0,
                query.Offset,
                query.Limit,
                filtered.LongLength);
            return new(AlertCenterReadStatus.Success, snapshot with
            {
                Query = new(
                    query.AlertId,
                    query.SessionId,
                    query.TraceId,
                    query.Severity,
                    query.State,
                    query.RuleId,
                    query.SourceSurface,
                    query.Repository,
                    query.Workspace,
                    query.Completeness,
                    query.From.ToString("yyyy-MM-dd"),
                    query.To.ToString("yyyy-MM-dd"),
                    query.Offset,
                    query.Limit),
            });
        }
    }

    private sealed class PagingFixtureReadModel : IAlertCenterReadModel
    {
        internal List<AlertCenterQuery> Queries { get; } = [];

        public AlertCenterReadResult Read(AlertCenterQuery query)
        {
            Queries.Add(query);
            var filteredOut = query.RuleId is not null || query.SourceSurface is not null;
            var alerts = filteredOut
                ? Array.Empty<AlertCenterAlert>()
                : query.Offset == 0
                ? Enumerable.Range(1, 100)
                    .Select(index => Alert(index.ToString("x64"), $"session-{index}", $"trace-{index}", $"span-{index}", "open"))
                    .ToArray()
                : [Alert(AlertB, "session-101", "trace-101", "span-101", "open")];
            return new(
                AlertCenterReadStatus.Success,
                Snapshot(
                    alerts,
                    [],
                    [new(
                        new string('9', 64),
                        "off-page-rule",
                        "1",
                        "minimum_sample_not_met",
                        [],
                        "exact_evaluation",
                        "claude-code",
                        "1.2.3",
                        "session-101",
                        "trace-101",
                        "2026-07-22")],
                    offset: query.Offset,
                    limit: query.Limit,
                    totalCount: filteredOut ? 0 : 101));
        }
    }
}

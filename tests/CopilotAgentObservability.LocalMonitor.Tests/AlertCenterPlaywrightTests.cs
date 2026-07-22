using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Alerts;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class AlertCenterPlaywrightTests
{
    [Fact(Timeout = 60_000)]
    public async Task AlertCenter_CriticalEvidenceRecurringAndKeyboardFlowStaySanitizedAndAccessible()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot(
            [Alert(AlertA, "session-a", "trace-a", "span-a", "open"), Alert(AlertB, "session-b", "trace-b", "span-b", "acknowledged")],
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
        await Expect(page.Locator("#alert-rows .alert-row")).ToHaveCountAsync(2);
        await Expect(page.Locator("#alert-detail-heading")).ToContainTextAsync("High tool failure ratio");
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("incomplete");
        await Expect(page.Locator("#alert-count")).ToContainTextAsync("omitted unknown");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("supported_at_evaluation");
        await Expect(page.Locator("#alert-detail .alert-evidence-link[href^='/traces']")).ToHaveAttributeAsync("href", "/traces/trace-a?span=span-a");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("expired");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("unknown");
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
        Assert.DoesNotContain("RAW_SECRET_MARKER", await page.ContentAsync());
        Assert.Equal(0, await page.Locator("#alert-detail script").CountAsync());

        var second = page.Locator("#alert-rows .alert-row").Nth(1);
        Assert.Null(await second.GetAttributeAsync("role"));
        var selectButton = second.Locator("[data-alert-select]");
        await selectButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(selectButton).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(page.Locator("#alert-detail-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("選択");

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

        readModel.Snapshot = Snapshot([], [], [], snapshotState: "incomplete", omittedReceiptCount: null);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-empty")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-empty")).ToContainTextAsync("0 件とは断定できません");
        await Expect(page.Locator("#alert-recurring")).ToContainTextAsync("0 件とは断定できません");

        readModel.Status = AlertCenterReadStatus.Unavailable;
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#alert-error")).ToBeVisibleAsync();
        await Expect(page.Locator("#alert-error")).ToContainTextAsync("読み込めませんでした");
        await Expect(page.Locator("#alert-empty")).ToBeHiddenAsync();
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
            if (calls == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"schema_version\":\"alert.lifecycle.v1\",\"alert_id\":\"" + AlertA + "\",\"state\":\"acknowledged\",\"revision\":1,\"last_occurred_at\":\"2026-07-23T12:00:00.0000000Z\",\"event\":{},\"idempotent_replay\":false}",
                });
            }
            else
            {
                if (calls == 3) readModel.Status = AlertCenterReadStatus.Unavailable;
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
        using (var body = JsonDocument.Parse(bodies[0]))
        {
            Assert.Equal("alert.lifecycle.v1", body.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("acknowledge", body.RootElement.GetProperty("action").GetString());
            Assert.Equal(0, body.RootElement.GetProperty("expected_revision").GetInt64());
        }

        await page.Locator("[data-alert-action='dismiss']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("更新が競合");
        await Expect(page.Locator("#alert-detail")).ToContainTextAsync("open");
        Assert.Equal(2, calls);

        await page.Locator("[data-alert-action='resolve']").ClickAsync();
        await Expect(page.Locator("#alert-live")).ToContainTextAsync("最新状態を再読み込みできませんでした");
        await Expect(page.Locator("#alert-error")).ToBeVisibleAsync();
        Assert.Equal(3, calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task Overview_UsesSameCriticalAlertDtoAndLinksExactSelection()
    {
        using var temp = NewTemp();
        var readModel = new FixtureReadModel(Snapshot(
            [Alert(AlertA, "session-a", "trace-a", "span-a", "open")],
            [],
            [],
            snapshotState: "incomplete",
            omittedReceiptCount: null));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readModel));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("High tool failure ratio");
        await Expect(page.Locator("#overview-alert-card")).ToContainTextAsync("critical");
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("取得範囲");
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("最新とは断定できません");
        await Expect(page.Locator("#overview-alert-body a")).ToHaveAttributeAsync("href", $"/alerts?alert={AlertA}");

        readModel.Snapshot = Snapshot([], [], [], snapshotState: "incomplete", omittedReceiptCount: null);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("0 件とは断定できません");

        readModel.Snapshot = Snapshot([Alert(AlertA, "session-a", "trace-a", "span-a", "open")], [], []);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#overview-alert-title")).ToContainTextAsync("最新の critical alert");
        await Expect(page.Locator("#overview-alert-body")).ToContainTextAsync("最終観測");

        await page.RouteAsync("**/api/monitor/overview?period=7d", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 503,
            ContentType = "application/json",
            Body = "{}",
        }));
        readModel.Snapshot = Snapshot([Alert(AlertB, "session-b", "trace-b", "span-b", "open")], [], []);
        await page.Locator("#period-toggle .period-btn[data-period='7d']").ClickAsync();
        await Expect(page.Locator("#overview-alert-body a")).ToHaveAttributeAsync("href", $"/alerts?alert={AlertB}");
        await Expect(page.Locator("#overview-alert-card .panel-link")).ToHaveAttributeAsync("href", "/alerts?severity=critical&state=open&period=7d");
    }

    private const string AlertA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AlertB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

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

    private static AlertCenterSnapshot Snapshot(
        IReadOnlyList<AlertCenterAlert> alerts,
        IReadOnlyList<AlertCenterRecurringGroup> recurring,
        IReadOnlyList<AlertCenterCoverageFact> coverage,
        string snapshotState = "complete",
        long? omittedReceiptCount = 0,
        int offset = 0,
        int limit = 50,
        long? totalCount = null) => new(
            AlertCenterContractVersions.Center,
            "2026-07-23T12:00:00.0000000Z",
            new(null, null, null, null, null, null, null, null, null, null, "2026-06-24", "2026-07-23", offset, limit),
            snapshotState,
            omittedReceiptCount,
            totalCount ?? alerts.Count,
            alerts,
            recurring,
            coverage);

    private static AlertCenterAlert Alert(string alertId, string sessionId, string traceId, string spanId, string state) => new(
        alertId,
        "critical",
        "open",
        new(state, state == "open" ? 0 : 1, state == "open" ? null : "2026-07-23T11:00:00.0000000Z", state == "open" ? ["acknowledge", "dismiss", "resolve"] : ["dismiss", "resolve"]),
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
        ],
        3,
        new([], []),
        "partial:schema_drift_detected",
        "evaluation-fixture");

    private static AlertCenterRecurringGroup Recurring(string state = "supported") => new(
        state,
        "high-tool-failure-ratio",
        "1",
        "repo-a",
        "workspace-a",
        "github-copilot-vscode",
        "1.0.4",
        "2026-07-22",
        "2026-06-24",
        "2026-07-23",
        2,
        2,
        "2026-07-22T10:00:00.0000000Z",
        "2026-07-22T10:00:04.0000000Z",
        new Dictionary<string, int> { ["partial"] = 1, ["rich"] = 1 },
        [AlertA, AlertB],
        ["session-a", "session-b"],
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

    private sealed class PagingFixtureReadModel : IAlertCenterReadModel
    {
        internal List<AlertCenterQuery> Queries { get; } = [];

        public AlertCenterReadResult Read(AlertCenterQuery query)
        {
            Queries.Add(query);
            var alerts = query.Offset == 0
                ? Enumerable.Range(1, 100)
                    .Select(index => Alert(index.ToString("x64"), $"session-{index}", $"trace-{index}", $"span-{index}", "open"))
                    .ToArray()
                : [Alert(AlertB, "session-101", "trace-101", "span-101", "open")];
            return new(
                AlertCenterReadStatus.Success,
                Snapshot(alerts, [], [], offset: query.Offset, limit: query.Limit, totalCount: 101));
        }
    }
}

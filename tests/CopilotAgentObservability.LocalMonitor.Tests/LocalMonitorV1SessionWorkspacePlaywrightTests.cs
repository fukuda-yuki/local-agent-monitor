using CopilotAgentObservability.LocalMonitor.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class LocalMonitorV1SessionWorkspacePlaywrightTests
{
    private const string SessionId = "018f0000-0000-7000-8000-000000000001";

    [Fact]
    public async Task LatestExecutionLoadsOnlyItsRootPageAndExactNodeSelectionUsesCanonicalHistory()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var timelineUrls = new List<string>();
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(Json(Summary("summary-full.json"))));
        var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject();
        timeline["workspace_revision"] = "8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a";
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/timeline?*", route =>
        {
            timelineUrls.Add(route.Request.Url);
            return route.FulfillAsync(Json(timeline.ToJsonString()));
        });
        var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject();
        node["workspace_revision"] = "8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a";
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/nodes/*?*", route =>
            route.FulfillAsync(Json(node.ToJsonString())));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-execution-toggle][aria-expanded=true]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-timeline-node]")).ToHaveCountAsync(1);
        Assert.Single(timelineUrls);
        Assert.Contains("workspace_revision=8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a", timelineUrls[0]);
        Assert.Contains("execution_id=9a5590c8-46e3-7069-af48-3844d2bf17a4", timelineUrls[0]);
        Assert.Contains("limit=100", timelineUrls[0]);
        Assert.DoesNotContain("parent_node_id=", timelineUrls[0]);

        await page.Locator("[data-timeline-node]").ClickAsync();
        await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
    }

    [Fact]
    public async Task NormalEntryFetchesSummaryOnceAndShowsStableSessionOverview()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var calls = 0;
        var summary = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "TestData", "LocalMonitorV1SessionDetail", "summary-full.json")));
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route =>
        {
            calls++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json; charset=utf-8",
                Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                Body = summary,
            });
        });
        var emptyTimeline = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject();
        emptyTimeline["workspace_revision"] = "8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a";
        emptyTimeline["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4";
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/timeline?*", route =>
            route.FulfillAsync(Json(emptyTimeline.ToJsonString())));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-session-workspace]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-summary]")).ToContainTextAsync("トークン合計");
        await Expect(page.Locator("[data-session-summary]")).ToContainTextAsync("15");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Review the retained instruction");
        var fixedSummary = await page.Locator("[data-session-summary]").InnerTextAsync();
        Assert.Equal(1, calls);
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ settings: 'ai' })");
        await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}?settings=ai");
        Assert.Equal(fixedSummary, await page.Locator("[data-session-summary]").InnerTextAsync());
        Assert.Null(await page.EvaluateAsync<string?>("() => window.LocalMonitorSessionWorkspace.selectedNodeId"));
    }

    [Fact]
    public async Task ExplorerRowNavigatesToTheExactSessionRoute()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var collection = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "TestData", "LocalMonitorV1SessionCollection", "more-page.json"));
        collection = collection[..collection.LastIndexOf("\"next_cursor\"", StringComparison.Ordinal)] + "\"next_cursor\":null}";
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, Body = collection,
        }));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8",
            Body = SummaryForSession("summary-full.json", "018f0000-0000-7000-8000-000000000002"),
        }));
        await page.Locator("[data-session-row] [data-session-open]").First.ClickAsync();

        await Expect(page).ToHaveURLAsync(host.Url + "/sessions/018f0000-0000-7000-8000-000000000002");
        await Expect(page.Locator("[data-session-workspace]")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task OfficialEmptyAndNonrecordedSummariesRenderTheirExplicitMissingFacts()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var fixture = "summary-empty.json";
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8", Body = Summary(fixture),
        }));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview");
        await Expect(page.Locator("[data-session-source]")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("[data-session-time]")).ToContainTextAsync("今回の記録にはありません");
        foreach (var name in new[] { "input", "output", "cache-read", "new-input", "coverage" })
            await Expect(page.Locator($"[data-session-fixed-{name}]")).ToContainTextAsync("今回の記録にはありません");

        fixture = "summary-nonrecorded-evidence.json";
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview");
        await Expect(page.Locator("[data-session-source]")).ToContainTextAsync("VS Code");
        await Expect(page.Locator("[data-session-time]")).ToContainTextAsync("今回の記録にはありません");
        foreach (var name in new[] { "input", "output", "cache-read", "new-input" })
            await Expect(page.Locator($"[data-session-fixed-{name}]")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("[data-session-fixed-coverage]")).ToContainTextAsync("記録が一部欠けています");
    }

    [Fact]
    public async Task RepresentativeClosedShapeMutationsFailBeforeStateAssignmentOrRendering()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        for (var mutation = 0; mutation < 17; mutation++)
        {
            var page = await browser.NewPageAsync();
            var body = InvalidSummary(mutation);
            await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200, ContentType = "application/json; charset=utf-8", Body = body,
            }));
            await page.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.Locator("[data-session-context-content]")).ToContainTextAsync("読み込めませんでした");
            Assert.Null(await page.EvaluateAsync<object?>("() => window.LocalMonitorSessionWorkspace.summary"));
            await Expect(page.Locator("[data-session-summary]")).ToBeEmptyAsync();
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task InstructionLabelUsesUnicodeScalarLimit()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var valid = JsonNode.Parse(Summary("summary-full.json"))!.AsObject();
        valid["session"]!["instruction"]!["label"] = string.Concat(Enumerable.Repeat("😀", 160));
        var validPage = await browser.NewPageAsync();
        await validPage.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8", Body = valid.ToJsonString(),
        }));

        await validPage.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(validPage.Locator("[data-session-overview]")).ToContainTextAsync("😀😀😀");
        Assert.NotNull(await validPage.EvaluateAsync<object?>("() => window.LocalMonitorSessionWorkspace.summary"));

        var invalid = valid.DeepClone().AsObject();
        invalid["session"]!["instruction"]!["label"] = string.Concat(Enumerable.Repeat("😀", 161));
        var invalidPage = await browser.NewPageAsync();
        await invalidPage.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8", Body = invalid.ToJsonString(),
        }));
        await invalidPage.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(invalidPage.Locator("[data-session-context-content]")).ToContainTextAsync("読み込めませんでした");
        Assert.Null(await invalidPage.EvaluateAsync<object?>("() => window.LocalMonitorSessionWorkspace.summary"));
    }

    [Fact]
    public async Task TokenBarsExposeAccessibleSegmentLabels()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var summary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject();
        var tokens = summary["session"]!["tokens"]!;
        tokens["input"]!["value"] = 10; tokens["output"]!["value"] = 5; tokens["total"]!["value"] = 15;
        tokens["cache_read"]!["state"] = "recorded"; tokens["cache_read"]!["value"] = 4;
        tokens["new_input"]!["state"] = "recorded"; tokens["new_input"]!["value"] = 6;
        tokens["cache_read_ratio_basis_points"]!["state"] = "recorded"; tokens["cache_read_ratio_basis_points"]!["value"] = 4000;
        await page.RouteAsync("**/api/local-monitor/v1/sessions/*/summary", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json; charset=utf-8", Body = summary.ToJsonString(),
        }));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        foreach (var label in new[] { "入力トークン 10", "出力トークン 5", "cache read 4", "new input 6" })
            await Expect(page.GetByLabel(label)).ToHaveCountAsync(1);
    }

    private static string Summary(string name) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "LocalMonitorV1SessionDetail", name)));

    private static RouteFulfillOptions Json(string body) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Body = body,
    };

    private static string SummaryForSession(string name, string sessionId)
    {
        var summary = JsonNode.Parse(Summary(name))!.AsObject();
        summary["session"]!["session_id"] = sessionId;
        return summary.ToJsonString();
    }

    private static string InvalidSummary(int mutation)
    {
        var summary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject();
        switch (mutation)
        {
            case 0: summary["session"]!["completeness"] = "complete"; break;
            case 1: summary["session"]!["tokens"]!.AsObject().Remove("output"); break;
            case 2:
                var coverage = summary["session"]!["capture"]!["coverage"]!.AsArray();
                var first = coverage[0]!.DeepClone();
                coverage[0] = coverage[1]!.DeepClone();
                coverage[1] = first;
                break;
            case 3: summary["executions"]![0]!["latest"] = false; break;
            case 4: summary["session"]!["archive"]!["effectively_eligible"] = false; break;
            case 5: summary["technical_references"]!["trace_ids"]!.AsArray().Add("00000000000000000000000000000001"); break;
            case 6:
                summary["session"]!["tokens"]!["new_input"]!["state"] = "recorded";
                summary["session"]!["tokens"]!["new_input"]!["value"] = 10;
                break;
            case 7:
                summary["session"]!["tokens"]!["cache_read"]!["state"] = "recorded";
                summary["session"]!["tokens"]!["cache_read"]!["value"] = 11;
                break;
            case 8:
                var zeroTokens = summary["session"]!["tokens"]!;
                zeroTokens["input"]!["value"] = 0; zeroTokens["total"]!["value"] = 5;
                zeroTokens["cache_read"]!["state"] = "recorded"; zeroTokens["cache_read"]!["value"] = 0;
                zeroTokens["new_input"]!["state"] = "recorded"; zeroTokens["new_input"]!["value"] = 0;
                zeroTokens["cache_read_ratio_basis_points"]!["state"] = "recorded";
                zeroTokens["cache_read_ratio_basis_points"]!["value"] = 0;
                break;
            case 9:
                summary["session"]!["timing"]!["ended_at"] = null;
                summary["session"]!["timing"]!["duration_ms"] = null;
                break;
            case 10: summary["session"]!["status"] = "active"; break;
            case 11:
                summary["executions"]![0]!["timing"]!["ended_at"] = null;
                summary["executions"]![0]!["timing"]!["duration_ms"] = null;
                break;
            case 12: summary["executions"]![0]!["status"] = "active"; break;
            case 13: AddSecondExecution(summary, "2026-08-26T01:02:02.0000000+00:00", "claude-code", reverse: true); break;
            case 14: AddSecondExecution(summary, "2026-08-26T01:02:03.0000000+00:00", "claude-code", reverse: false); break;
            case 15: AddSecondExecution(summary, "2026-08-26T01:02:03.0000000+00:00", "vscode", reverse: false); break;
            case 16:
                summary["session"]!["tokens"]!["cache_read_ratio_basis_points"]!["state"] = "recorded";
                summary["session"]!["tokens"]!["cache_read_ratio_basis_points"]!["value"] = 0;
                break;
        }
        return summary.ToJsonString();
    }

    private static void AddSecondExecution(JsonObject summary, string startedAt, string source, bool reverse)
    {
        var executions = summary["executions"]!.AsArray();
        var second = executions[0]!.DeepClone().AsObject();
        second["execution_id"] = "8a5590c8-46e3-7069-af48-3844d2bf17a4";
        second["node_id"] = "node-1db4028cf76015c954848d7dcbb5deca";
        second["latest"] = false;
        second["source"] = source;
        second["timing"]!["started_at"] = startedAt;
        second["timing"]!["ended_at"] = startedAt;
        second["timing"]!["duration_ms"] = 0;
        if (reverse) executions.Insert(0, second); else executions.Add(second);
    }

    private static MonitorHostTestOptions Options() => new()
    {
        AdditionalServices = services =>
        {
            services.AddSingleton<ILocalRepositoryScopeSnapshotService>(new ReadyScopeService());
            services.AddSingleton<ILocalRepositorySessionDetailSnapshotService>(new ReadyDetailService());
        },
    };

    private sealed class ReadyScopeService : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request, [], []));
    }

    private sealed class ReadyDetailService : ILocalRepositorySessionDetailSnapshotService
    {
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken)
        {
            var session = new LocalRepositoryScopeSessionSnapshot(request.SessionId, new SessionRow(request.SessionId), 0,
                LocalRepositoryScopeAssignmentState.Unassigned, LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true,
                true, LocalArchiveState.Active, 0, true, null);
            return ValueTask.FromResult(new LocalRepositorySessionDetailSnapshot(session,
                new LocalWorkspaceSessionDetailContribution([], [], [], []), new string('1', 64)));
        }
    }

    private sealed record SessionRow(string SessionId) : ILocalRepositorySessionSnapshotRow;
}

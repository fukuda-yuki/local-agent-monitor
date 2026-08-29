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
        for (var mutation = 0; mutation < 6; mutation++)
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
        }
        return summary.ToJsonString();
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

using CopilotAgentObservability.LocalMonitor.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

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
        var href = await page.Locator("[data-session-row] [data-session-open]").First.GetAttributeAsync("href");

        Assert.Equal("/sessions/018f0000-0000-7000-8000-000000000002", href);
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

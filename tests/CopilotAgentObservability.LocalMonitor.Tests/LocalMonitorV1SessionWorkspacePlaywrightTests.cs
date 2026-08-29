using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.Persistence.Sqlite;
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
    public async Task DirectHttpMismatchedExecutionNodePairIsNotFound()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var response = await page.GotoAsync(host.Url + $"/sessions/{SessionId}?execution=8a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6"); Assert.Equal(404, response!.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MalformedTimelineFailsClosedBeforeRowsOrCacheMutation(int mutation)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null;
        if (mutation == 0) timeline["extra"] = true; else if (mutation == 1) timeline["items"]![0]!["relationship_authority"] = "inferred"; else timeline["items"]![0]!.AsObject().Remove("status");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator("[data-timeline-node]")).ToHaveCountAsync(0); Assert.Equal(0, await page.EvaluateAsync<int>("() => window.LocalMonitorSessionWorkspace.executionState.values().next().value.pages.size"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MalformedNodeFailsClosedAndRestoresCanonicalOverview(int mutation)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision;
        if (mutation == 0) node["node"]!["unexpected"] = true; else if (mutation == 1) node["node"]!["metadata"]!["kind"] = "tool"; else if (mutation == 2) node["node"]!["metadata"]!["source_references"]!["references"]![0]!["extra"] = true; else node["node"]!["metadata"]!["content"]!["available"] = true;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync();
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview"); Assert.Null(await page.EvaluateAsync<string?>("() => window.LocalMonitorSessionWorkspace.selectedNodeId")); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}");
    }

    [Fact]
    public async Task ExecutionScrollPositionSurvivesCollapseAndRerender()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var template = timeline["items"]![0]!.DeepClone(); timeline["items"] = new JsonArray(Enumerable.Range(1, 30).Select(i => { var item = template.DeepClone(); item!["node_id"] = $"node-{i:x32}"; return item; }).ToArray());
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        var scroll = page.Locator("[data-execution-scroll]"); await scroll.EvaluateAsync("element => element.scrollTop = 120"); await page.Locator("[data-execution-toggle]").ClickAsync(); await page.Locator("[data-execution-toggle]").ClickAsync();
        await Expect(scroll).ToHaveJSPropertyAsync("scrollTop", 120);
    }

    [Fact]
    public async Task MultipleExecutionsOpenOnlyLatestAndKeepCollapsedHeaderSummary()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); AddSecondExecution(summary, "2026-08-26T01:02:02.0000000+00:00", "claude-code", reverse: false); var revision = summary["workspace_revision"]!.GetValue<string>();
        var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4"; var urls = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary.ToJsonString()))); await page.RouteAsync("**/timeline?*", r => { urls.Add(r.Request.Url); return r.FulfillAsync(Json(empty.ToJsonString())); }); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator("[data-execution-toggle]")).ToHaveCountAsync(2); await Expect(page.Locator("[data-execution-toggle][aria-expanded=true]")).ToHaveCountAsync(1); await Expect(page.Locator("[data-execution-toggle][aria-expanded=false]")).ToContainTextAsync("2 activity");
        Assert.Single(urls); Assert.DoesNotContain("8a5590c8-46e3-7069-af48-3844d2bf17a4", urls[0]);
    }

    [Fact]
    public async Task CursorLoadMoreUsesExactAfterWithoutLoadingAnotherExecutionOrDescendants()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>();
        var first = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); first["workspace_revision"] = revision;
        var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = first["execution_id"]!.GetValue<string>();
        var urls = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary)));
        await page.RouteAsync("**/timeline?*", r => { urls.Add(r.Request.Url); return r.FulfillAsync(Json(r.Request.Url.Contains("after=") ? empty.ToJsonString() : first.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await page.Locator("[data-timeline-load-more]").ClickAsync();
        await Expect(page.Locator("[data-timeline-load-more]")).ToHaveCountAsync(0);
        Assert.Equal(2, urls.Count); Assert.EndsWith($"/timeline?workspace_revision={revision}&execution_id=9a5590c8-46e3-7069-af48-3844d2bf17a4&after={first["next_cursor"]!.GetValue<string>()}&limit=100", urls[1]);
    }

    [Fact]
    public async Task ExpandingChildUsesOnlyTheExactParentRequest()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>();
        var rootPage = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); rootPage["workspace_revision"] = revision; rootPage["next_cursor"] = null; rootPage["items"]![0]!["child_count"] = 1;
        var childPage = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); childPage["workspace_revision"] = revision; childPage["execution_id"] = rootPage["execution_id"]!.GetValue<string>(); childPage["parent_node_id"] = "node-a8a773d6614d5030f505ff195b452dd6";
        var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; var urls = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/timeline?*", r => { urls.Add(r.Request.Url); return r.FulfillAsync(Json(r.Request.Url.Contains("parent_node_id=") ? childPage.ToJsonString() : rootPage.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync();
        await Expect(page.Locator("[data-timeline-node][aria-expanded=true]")).ToHaveCountAsync(1);
        Assert.Equal(2, urls.Count); Assert.EndsWith($"/timeline?workspace_revision={revision}&execution_id=9a5590c8-46e3-7069-af48-3844d2bf17a4&parent_node_id=node-a8a773d6614d5030f505ff195b452dd6&limit=100", urls[1]);
    }

    [Fact]
    public async Task NonAuthoritativeRelationshipRendersInUnknownGroupWithoutNesting()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; timeline["items"]![0]!["relationship_authority"] = "unknown";
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString())));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator(".local-monitor-session-unknown-group")).ToContainTextAsync("親子関係不明");
        await Expect(page.Locator(".local-monitor-session-unknown-group [data-timeline-node]")).ToHaveCountAsync(1);
    }

    [Theory]
    [InlineData("missing", "時刻なし")]
    [InlineData("invalid", "時刻が無効")]
    public async Task MissingOrInvalidTimingNeverCreatesRecordedTimingBar(string state, string label)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var timing = timeline["items"]![0]!["timing"]!; timing["state"] = state; timing["started_at"] = null; timing["ended_at"] = null; timing["duration_ms"] = null;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator("[data-timeline-node]")).ToContainTextAsync(label); await Expect(page.Locator("[data-timeline-time-bar]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ExactDeepLinkReloadAndHistoryRestoreOnlyReturnedNodeIdentity()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; var nodeCalls = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => { nodeCalls++; return r.FulfillAsync(Json(node.ToJsonString())); });
        var exact = $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6";
        await page.GotoAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); await page.ReloadAsync(); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?node=node-a8a773d6614d5030f505ff195b452dd6"); await Expect(page).ToHaveURLAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: null, node: null })"); await page.GoBackAsync(); await Expect(page).ToHaveURLAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); Assert.True(nodeCalls >= 3);
    }

    [Fact]
    public async Task MismatchedNodeFallsBackToSessionOverviewWithoutSimilarityRepair()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; var mismatch = false; var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4";
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(empty.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => { var response = node.DeepClone(); if (mismatch) response["node"]!["node_id"] = "node-00000000000000000000000000000009"; return r.FulfillAsync(Json(response.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); mismatch = true; await page.EvaluateAsync("() => document.dispatchEvent(new CustomEvent('cao-route-state', { detail: window.LocalMonitorV1History.current() }))");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview"); Assert.Null(await page.EvaluateAsync<string?>("() => window.LocalMonitorSessionWorkspace.selectedNodeId")); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}");
    }

    [Fact]
    public async Task StaleExactNodeRefreshesSummaryAndRetriesSameTargetOnce()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var oldSummary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); var fresh = "123f43a996e323544c67c74cbedeb64c6121b2cdf1455c2947ef56aa654cde76"; var newSummary = oldSummary.DeepClone().AsObject(); newSummary["workspace_revision"] = fresh; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = fresh; var summaries = 0; var nodeUrls = new List<string>();
        await page.RouteAsync("**/summary", r => { summaries++; return r.FulfillAsync(Json((summaries == 1 ? oldSummary : newSummary).ToJsonString())); });
        await page.RouteAsync("**/nodes/*?*", r => { nodeUrls.Add(r.Request.Url); return nodeUrls.Count == 1 ? r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_snapshot_stale\"}" }) : r.FulfillAsync(Json(node.ToJsonString())); });
        await page.RouteAsync("**/timeline?*", r => { var requested = new Uri(r.Request.Url).Query.Split("workspace_revision=")[1].Split('&')[0]; return r.FulfillAsync(Json("{\"schema_version\":\"local-monitor-session-timeline.response.v2\",\"workspace_revision\":\"" + requested + "\",\"session_id\":\"" + SessionId + "\",\"execution_id\":\"9a5590c8-46e3-7069-af48-3844d2bf17a4\",\"parent_node_id\":null,\"items\":[],\"next_cursor\":null}")); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        Assert.Equal(2, summaries); Assert.Equal(2, nodeUrls.Count); Assert.Contains(oldSummary["workspace_revision"]!.GetValue<string>(), nodeUrls[0]); Assert.Contains(fresh, nodeUrls[1]); Assert.All(nodeUrls, u => Assert.Contains("node-a8a773d6614d5030f505ff195b452dd6", u));
    }

    [Fact]
    public async Task NonStaleConflictDoesNotRefreshOrRetryExactNode()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var summaries = 0; var nodes = 0;
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4";
        await page.RouteAsync("**/summary", r => { summaries++; return r.FulfillAsync(Json(summary)); }); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(empty.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => { nodes++; return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_too_large\"}" }); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}"); Assert.Equal(1, summaries); Assert.Equal(1, nodes);
    }

    [Fact]
    public async Task TimelineStaleBudgetsAreIndependentAndRetryOnlySameExactRequest()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var revisions = new[] { new string('1', 64), new string('2', 64), new string('3', 64) }; var summaryTemplate = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); var summaries = 0; var timelineCalls = 0; var order = new List<string>(); var pageBody = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject();
        await page.RouteAsync("**/summary", r => { var revision = revisions[Math.Min(summaries++, 2)]; var body = summaryTemplate.DeepClone(); body["workspace_revision"] = revision; order.Add("summary:" + revision[0]); return r.FulfillAsync(Json(body.ToJsonString())); });
        await page.RouteAsync("**/timeline?*", r => { timelineCalls++; var uri = new Uri(r.Request.Url); var revision = uri.Query.Split("workspace_revision=")[1][0]; var after = uri.Query.Contains("after="); order.Add($"timeline:{revision}:{(after ? "after" : "root")}"); if (timelineCalls is 1 or 3) return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_snapshot_stale\"}" }); var body = pageBody.DeepClone(); body["workspace_revision"] = revisions[revision - '1']; if (after) { body["items"] = new JsonArray(); body["next_cursor"] = null; } return r.FulfillAsync(Json(body.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-load-more]").ClickAsync(); await Expect(page.Locator("[data-timeline-load-more]")).ToHaveCountAsync(0);
        Assert.Equal(new[] { "summary:1", "timeline:1:root", "summary:2", "timeline:2:root", "timeline:2:after", "summary:3", "timeline:3:after" }, order); Assert.Equal(3, summaries); Assert.Equal(4, timelineCalls);
    }

    [Fact]
    public async Task FailedStaleSummaryRefreshDoesNotRetryOldRevisionOrLoadDefaultTimeline()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var summaries = 0; var timelines = 0;
        await page.RouteAsync("**/summary", r => ++summaries == 1 ? r.FulfillAsync(Json(Summary("summary-full.json"))) : r.FulfillAsync(new RouteFulfillOptions { Status = 503, ContentType = "application/json", Body = "{\"error\":\"persistence_busy\"}" }));
        await page.RouteAsync("**/timeline?*", r => { timelines++; return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_snapshot_stale\"}" }); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("Session overview"); await page.WaitForTimeoutAsync(200);
        Assert.Equal(2, summaries); Assert.Equal(1, timelines); Assert.Equal("8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a", await page.EvaluateAsync<string>("() => window.LocalMonitorSessionWorkspace.revision"));
    }

    [Fact]
    public async Task ChildTimelineStaleRetriesOnlySameExactParentWithFreshRevision()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var oldRevision = new string('1', 64); var freshRevision = new string('2', 64); var summaries = 0; var childCalls = 0; var order = new List<string>(); var summaryTemplate = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); var rootPage = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); rootPage["workspace_revision"] = oldRevision; rootPage["next_cursor"] = null; rootPage["items"]![0]!["child_count"] = 1; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = freshRevision;
        await page.RouteAsync("**/summary", r => { var revision = ++summaries == 1 ? oldRevision : freshRevision; var body = summaryTemplate.DeepClone(); body["workspace_revision"] = revision; order.Add("summary:" + revision[0]); return r.FulfillAsync(Json(body.ToJsonString())); });
        await page.RouteAsync("**/timeline?*", r => { var uri = new Uri(r.Request.Url); if (!uri.Query.Contains("parent_node_id=")) { var rootRevision = uri.Query.Split("workspace_revision=")[1][0]; order.Add("root:" + rootRevision); var root = rootPage.DeepClone(); root["workspace_revision"] = rootRevision == '1' ? oldRevision : freshRevision; return r.FulfillAsync(Json(root.ToJsonString())); } var revision = uri.Query.Split("workspace_revision=")[1][0]; order.Add("child:" + revision); if (++childCalls == 1) return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_snapshot_stale\"}" }); var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = freshRevision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4"; empty["parent_node_id"] = "node-a8a773d6614d5030f505ff195b452dd6"; return r.FulfillAsync(Json(empty.ToJsonString())); });
        await page.RouteAsync("**/nodes/*?*", r => { order.Add("node:2"); return r.FulfillAsync(Json(node.ToJsonString())); }); await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        Assert.Equal(new[] { "summary:1", "root:1", "child:1", "summary:2", "child:2", "node:2", "root:2" }, order); Assert.Equal(2, childCalls);
    }

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
        await Expect(page.Locator("[data-timeline-time-bar]")).ToHaveCountAsync(1);
        Assert.Single(timelineUrls);
        Assert.EndsWith("/timeline?workspace_revision=8afe8c6c3beb9278813e087c347d50efe5175c61145bc0984f0b47dc7fbb416a&execution_id=9a5590c8-46e3-7069-af48-3844d2bf17a4&limit=100", timelineUrls[0]);

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
            var fact = new LocalWorkspaceFact<long>("not_observed", null);
            var activity = new LocalWorkspaceActivityFacts(fact, fact, fact, fact, fact);
            var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 1, fact, fact, fact, fact, fact, fact, fact, fact);
            var execution = new LocalWorkspaceExecutionDetail("9a5590c8-46e3-7069-af48-3844d2bf17a4", request.SessionId,
                "session_run", "run", 0, "completed", "completed", null, null, "missing", null, null, null, activity, tokens, ChildCount: 1, Latest: true);
            var node = new LocalWorkspaceNodeDetail("node-a8a773d6614d5030f505ff195b452dd6", request.SessionId, execution.ExecutionId,
                "session_event", "event", 0, null, "exact", "event", "recorded", "user.message", "completed", "completed", "missing", null, null, null,
                activity, tokens, null, null, null);
            return ValueTask.FromResult(new LocalRepositorySessionDetailSnapshot(session,
                new LocalWorkspaceSessionDetailContribution([execution], [node], [], []), new string('1', 64)));
        }
    }

    private sealed record SessionRow(string SessionId) : ILocalRepositorySessionSnapshotRow;
}

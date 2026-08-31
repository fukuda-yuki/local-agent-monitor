using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public sealed class LocalMonitorV1SessionWorkspacePlaywrightTests
{
    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string AiRunId = "018f0000-0000-7000-8000-000000000071";
    private const string AiLatestRunId = "018f0000-0000-7000-8000-000000000073";

    [Fact]
    public async Task ExactRunningSessionAnalysisResumesPollingAndReachesItsTerminalReport()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var context = await browser.NewContextAsync(); await context.AddInitScriptAsync("window.setTimeout = fn => { queueMicrotask(fn); return 1; };"); var page = await context.NewPageAsync();
        var polls = 0; var release = new TaskCompletionSource(); await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json(polls >= 3 ? $$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("restored terminal")}},"snapshot_changed":false}],"next_cursor":null}""" : """{"reports":[],"next_cursor":null}""")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{AiRunId}", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"running","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":null}""")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/session-runs/{AiRunId}", async r => { polls++; if (polls == 2) await release.Task; await r.FulfillAsync(Json(polls < 3 ? $$"""{"run_id":"{{AiRunId}}","state":"running","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":null}""" : $$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("restored terminal")}}}""")); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?analysis={AiRunId}"); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "キャンセル" })).ToBeVisibleAsync(); release.SetResult(); await Expect(dialog).ToContainTextAsync("restored terminal"); Assert.True(polls >= 3);
    }

    [Fact]
    public async Task RemovingAnalysisFromHistoryClosesReportAndReturnsFocusToOverviewAction()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("history report")}},"snapshot_changed":false}],"next_cursor":null}"""))); await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("history report")}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?analysis={AiRunId}"); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog).ToBeVisibleAsync(); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ analysis: null })"); await Expect(dialog).ToBeHiddenAsync(); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" })).ToBeFocusedAsync(); await Expect(page.Locator("[data-session-overview] h2")).ToHaveTextAsync("セッションの概要");
    }

    [Fact]
    public async Task RemovingOnlyAnalysisClosesReportAndPreservesExactExecution()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var (summary, timeline, _, _) = InspectorDocuments("event");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("history report")}},"snapshot_changed":false}],"next_cursor":null}"""))); await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("history report")}}}""")));
        const string executionId = "9a5590c8-46e3-7069-af48-3844d2bf17a4"; await page.GotoAsync(host.Url + $"/sessions/{SessionId}?analysis={AiRunId}&execution={executionId}"); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog).ToBeVisibleAsync();
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ analysis: null })"); await Expect(dialog).ToBeHiddenAsync(); var execution = page.Locator("[data-execution-toggle][aria-expanded=true]"); await Expect(execution).ToHaveCountAsync(1); await Expect(execution).ToBeFocusedAsync(); Assert.Contains($"execution={executionId}", page.Url); Assert.DoesNotContain("analysis=", page.Url); await Expect(page.Locator("[data-session-overview] h2")).ToHaveTextAsync("セッションの概要");
    }

    [Fact]
    public async Task ExactSessionAnalysisRestoresOldExpiredReportAcrossReloadBackAndForward()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page);
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiLatestRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("latest")}},"snapshot_changed":false},{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"expired","result":null,"snapshot_changed":true}],"next_cursor":null}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{(r.Request.Url.EndsWith(AiRunId) ? AiRunId : AiLatestRunId)}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("generic must not replace retained state")}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?analysis={AiRunId}"); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog).ToBeVisibleAsync(); await Expect(dialog).ToContainTextAsync("保存期間を過ぎたため"); await Expect(dialog).ToContainTextAsync("前回の分析後に記録が更新されています"); await Expect(dialog).Not.ToContainTextAsync("generic must not replace");
        await page.ReloadAsync(); await Expect(dialog).ToContainTextAsync("保存期間を過ぎたため");
        await page.EvaluateAsync("run => window.LocalMonitorV1History.push({ analysis: run })", AiLatestRunId); await Expect(dialog).ToContainTextAsync("latest"); await page.GoBackAsync(); await Expect(dialog).ToContainTextAsync("保存期間を過ぎたため"); await page.GoForwardAsync(); await Expect(dialog).ToContainTextAsync("latest");
    }

    [Fact]
    public async Task DelayedReadinessAddsNodeStartNoticeWithoutChangingSelectionOrDispatching()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event"); var readiness = new TaskCompletionSource(); var starts = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", async r => { await readiness.Task; await r.FulfillAsync(Json(Readiness())); }); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}"""))); await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs", r => { starts++; return r.AbortAsync(); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6"); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToHaveCountAsync(1); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" })).ToHaveCountAsync(0);
        readiness.SetResult(); var action = page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" }); await Expect(action).ToBeVisibleAsync(); await Expect(page.Locator("[data-node-ai-start]")).ToContainTextAsync("GitHub Copilot"); Assert.Equal(0, starts); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task PollingContinuesPastOldAttemptLimitAndCancelRemainsAvailable()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var context = await browser.NewContextAsync(); await context.AddInitScriptAsync("window.setTimeout = fn => { queueMicrotask(fn); return 1; };"); var page = await context.NewPageAsync();
        var polls = 0; var canceled = 0; var cancelPhase = false; await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json(polls > 125 && !cancelPhase ? $$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("after old limit")}},"snapshot_changed":false}],"next_cursor":null}""" : """{"reports":[],"next_cursor":null}"""))); await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs", r => r.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" }));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/session-runs/{AiRunId}", async r => { polls++; if (cancelPhase) await Task.Delay(10); await r.FulfillAsync(Json(cancelPhase || polls <= 125 ? $$"""{"run_id":"{{AiRunId}}","state":"running","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":null}""" : $$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("after old limit")}}}""")); }); await page.RouteAsync("**/api/local-monitor/v1/ai/runs/*/cancel", r => { canceled++; return r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"canceled"}""")); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }).ClickAsync(); await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "再分析" }).ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("after old limit"); Assert.True(polls > 120);
        polls = 0; cancelPhase = true; await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "再分析" }).ClickAsync(); var cancel = page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "キャンセル" }); await Expect(cancel).ToBeVisibleAsync(); await cancel.ClickAsync(); Assert.Equal(1, canceled); await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "閉じる" }).ClickAsync();
    }

    [Fact]
    public async Task SessionEvidenceClosesDialogAndPreservesAnalysisWhileSelectingExactNode()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var (summary, timeline, node, _) = InspectorDocuments("event");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("session evidence")}},"snapshot_changed":false}],"next_cursor":null}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }).ClickAsync(); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await dialog.GetByRole(AriaRole.Button, new() { Name = "証拠を表示" }).ClickAsync(); await Expect(dialog).ToBeHiddenAsync(); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToBeFocusedAsync(); Assert.Contains($"analysis={AiRunId}", page.Url); Assert.Contains("execution=9a5590c8-46e3-7069-af48-3844d2bf17a4", page.Url); Assert.Contains("node=node-a8a773d6614d5030f505ff195b452dd6", page.Url);
    }

    [Theory]
    [InlineData("queued", "分析を待っています", "status")]
    [InlineData("running", "分析しています", "status")]
    [InlineData("succeeded", "matrix result", "retained")]
    [InlineData("zero_findings", "指摘はありませんでした", "retained")]
    [InlineData("provider_failed", "AIで分析できませんでした", "status")]
    [InlineData("provider_partial", "不完全な結果のため表示できません", "status")]
    [InlineData("timed_out", "分析がタイムアウトしました", "status")]
    [InlineData("canceled", "分析をキャンセルしました", "status")]
    [InlineData("stale_snapshot", "セッションが更新されたため分析を完了できませんでした", "status")]
    [InlineData("scope_too_large", "分析対象が上限を超えています", "status")]
    [InlineData("invalid_result", "AI結果を安全に確認できません", "status")]
    [InlineData("invalid_evidence", "証拠を確認できないため結果を表示できません", "status")]
    [InlineData("succeeded", "保存期間を過ぎたため分析内容を表示できません", "expired")]
    public async Task SessionAiDisplaysEveryRunAndContentState(string state, string expected, string content)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page); var successful = state is "succeeded" or "zero_findings"; var result = successful && content != "expired" ? state == "zero_findings" ? AiZeroResult("matrix result") : AiResult("matrix result") : "null";
        await page.RouteAsync("**/api/local-monitor/v1/ai/runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"{{state}}","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{result}}}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"{{state}}","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{result}}}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json(successful ? $$"""{"reports":[{"run_id":"{{AiRunId}}","state":"{{state}}","content_state":"{{content}}","result":{{result}},"snapshot_changed":false}],"next_cursor":null}""" : """{"reports":[],"next_cursor":null}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?analysis={AiRunId}"); await Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" })).ToContainTextAsync(expected);
    }

    [Fact]
    public async Task SessionHistoryPagesNewestFirstWithoutStatusOnlyFailures()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var requests = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => { requests++; var older = r.Request.Url.Contains("cursor="); return r.FulfillAsync(Json(older ? $$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("older")}},"snapshot_changed":false}],"next_cursor":null}""" : $$"""{"reports":[{"run_id":"{{AiLatestRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("newest")}},"snapshot_changed":false}],"next_cursor":"Y3Vyc29y"}""")); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }).ClickAsync(); var dialog = page.GetByRole(AriaRole.Dialog); await Expect(dialog).ToContainTextAsync("newest"); var history = dialog.Locator("[data-session-ai-history] button"); await Expect(history).ToHaveCountAsync(1); await Expect(history.Nth(0)).ToHaveTextAsync(AiLatestRunId); await dialog.GetByRole(AriaRole.Button, new() { Name = "さらに読み込む" }).ClickAsync(); await Expect(history).ToHaveCountAsync(2); await Expect(history.Nth(0)).ToHaveTextAsync(AiLatestRunId); await Expect(history.Nth(1)).ToHaveTextAsync(AiRunId); Assert.Equal(2, requests);
    }

    [Fact]
    public async Task AiSurfacesFitDesktopAndNarrowOverlayAndNodeCloseReturnsFocus()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1366, Height = 768 } }); var (summary, timeline, node, _) = InspectorDocuments("event");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}"""))); await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs", r => r.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" })); await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"node","session_id":"{{SessionId}}","node_id":"node-a8a773d6614d5030f505ff195b452dd6","error":null,"result":{{AiResult("layout")}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); var sessionAction = page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }); await sessionAction.ClickAsync(); var box = await page.GetByRole(AriaRole.Dialog).BoundingBoxAsync(); Assert.NotNull(box); Assert.True(box!.Width <= 1334 && box.Height <= 736); await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "閉じる" }).ClickAsync();
        await page.SetViewportSizeAsync(480, 720); await page.GetByRole(AriaRole.Button, new() { Name = "インスペクターを閉じる" }).ClickAsync(); await page.Locator("[data-timeline-node]").ClickAsync(); var nodeAction = page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" }); await nodeAction.ClickAsync(); await page.Locator("[data-node-ai-surface]").GetByRole(AriaRole.Button, new() { Name = "AI分析を閉じる" }).ClickAsync(); await Expect(nodeAction).ToBeFocusedAsync(); Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth")); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "インスペクターを閉じる" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AiActionsAreReadyOnlyAndSessionReportUsesExactRunHistoryAndInertEvidence()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var readiness = "unconfigured"; var reports = 0; var starts = 0; var raw = "<img src=x onerror=window.__aiExecuted=true>";
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json"))));
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", r => r.FulfillAsync(Json($$"""{"provider":"github_copilot","selected_model":null,"selected_configuration":null,"readiness_state":"{{readiness}}","last_check_result":"not_checked","provider_egress_notice":"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action"}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => { reports++; return r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult(raw)}},"snapshot_changed":true}],"next_cursor":"Y3Vyc29y"}""")); });
        await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs", r => { starts++; return r.FulfillAsync(new RouteFulfillOptions { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" }); });
        await page.RouteAsync($"**/api/local-monitor/v1/ai/session-runs/{AiRunId}", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult(raw)}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        var action = page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }); await Expect(action).ToHaveCountAsync(0); Assert.Equal(0, reports);
        readiness = "ready"; await page.ReloadAsync(); await Expect(action).ToBeVisibleAsync(); Assert.Equal(1, reports);
        await action.ClickAsync(); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog).ToContainTextAsync("GitHub Copilot"); await Expect(dialog).ToContainTextAsync(raw); await Expect(dialog).ToContainTextAsync("前回の分析後に記録が更新されています");
        Assert.False(await page.EvaluateAsync<bool>("() => Boolean(window.__aiExecuted)")); Assert.Contains($"analysis={AiRunId}", page.Url); Assert.Equal(0, starts);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "再分析" }).ClickAsync(); Assert.Equal(1, starts);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "閉じる" }).ClickAsync(); await Expect(action).ToBeFocusedAsync();
    }

    [Fact]
    public async Task NodeAiIsTransientResendsOnlyPageMemoryTranscriptAndNavigatesExactEvidence()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event"); var bodies = new List<string>(); var run = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", r => r.FulfillAsync(Json("""{"provider":"github_copilot","selected_model":"synthetic","selected_configuration":"test","readiness_state":"ready","last_check_result":"ready","provider_egress_notice":"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action"}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs", r => { bodies.Add(r.Request.PostData!); run++; return r.FulfillAsync(new RouteFulfillOptions { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"018f0000-0000-7000-8000-00000000007{{run}}"}""" }); });
        await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"018f0000-0000-7000-8000-00000000007{{run}}","state":"succeeded","scope_kind":"node","session_id":"{{SessionId}}","node_id":"node-a8a773d6614d5030f505ff195b452dd6","error":null,"result":{{AiResult("answer", "node-a8a773d6614d5030f505ff195b452dd6")}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); var action = page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" }); await Expect(action).ToBeVisibleAsync(); await action.ClickAsync();
        var surface = page.Locator("[data-node-ai-surface]"); await Expect(page.Locator("[data-node-ai-start]")).ToContainTextAsync("GitHub Copilot"); await Expect(page.Locator("[data-session-executions]")).ToBeVisibleAsync();
        await surface.GetByRole(AriaRole.Textbox).FillAsync("why?"); await surface.GetByRole(AriaRole.Button, new() { Name = "質問する" }).ClickAsync();
        Assert.Equal(2, bodies.Count); Assert.DoesNotContain("prior_turns", bodies[0]); Assert.Contains("\"question\":\"why?\"", bodies[1]); Assert.Contains("\"prior_turns\":[{\"question\":\"\",\"answer\":\"answer\"}]", bodies[1]);
        Assert.DoesNotContain("answer", page.Url); Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('local-ai') ?? sessionStorage.getItem('local-ai')")); await Expect(page.GetByText("過去の分析")).ToHaveCountAsync(0);
        await surface.GetByRole(AriaRole.Button, new() { Name = "証拠を表示" }).ClickAsync(); Assert.Contains("execution=9a5590c8-46e3-7069-af48-3844d2bf17a4", page.Url); Assert.Contains("node=node-a8a773d6614d5030f505ff195b452dd6", page.Url); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToHaveCountAsync(1);
        await page.ReloadAsync(); await Expect(page.Locator("[data-node-ai-surface]")).ToHaveCountAsync(0); Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public async Task NodeAnalysisWritesAndRestoresExactRouteAcrossReloadBackAndForwardWithoutRestarting()
    {
        var routeRun = new LocalAiRunStatusV1(AiRunId, "succeeded", "node", SessionId, "node-a8a773d6614d5030f505ff195b452dd6", null);
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(routeRun));
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event"); var starts = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await ReadyAi(page);
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs", r => { starts++; return r.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" }); });
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{AiRunId}", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"node","session_id":"{{SessionId}}","node_id":"node-a8a773d6614d5030f505ff195b452dd6","error":null,"result":{{AiResult("restored node")}}}""")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/node-runs/{AiRunId}", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"node","session_id":"{{SessionId}}","node_id":"node-a8a773d6614d5030f505ff195b452dd6","error":null,"result":{{AiResult("restored node")}}}""")));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6");
        await page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6&analysis={AiRunId}");
        await Expect(page.Locator("[data-node-ai-result]")).ToContainTextAsync("restored node"); Assert.Equal(1, starts);
        await page.ReloadAsync(); await Expect(page.Locator("[data-node-ai-result]")).ToContainTextAsync("restored node"); Assert.Equal(1, starts);
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ analysis: null })"); await Expect(page.Locator("[data-node-ai-surface]")).ToHaveCountAsync(0);
        await page.GoBackAsync(); await Expect(page.Locator("[data-node-ai-result]")).ToContainTextAsync("restored node");
        await page.GoForwardAsync(); await Expect(page.Locator("[data-node-ai-surface]")).ToHaveCountAsync(0); Assert.Equal(1, starts);
        Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('local-ai') ?? sessionStorage.getItem('local-ai')"));
    }

    [Fact]
    public async Task MissingNodeAnalysisOnHistoryRevisitReloadsExactHumanRouteWithoutWorkspaceFallback()
    {
        var owner = new MutableHumanRouteLocalAiApplication(new(AiRunId, "succeeded", "node", SessionId, "node-a8a773d6614d5030f505ff195b452dd6", null, System.Text.Encoding.UTF8.GetBytes(AiResult("retained until removed"))));
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(owner));
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await ReadyAi(page); await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}""")));
        var exact = host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6&analysis={AiRunId}";
        await page.GotoAsync(exact); await Expect(page.Locator("[data-node-ai-result]")).ToContainTextAsync("retained until removed");
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ analysis: null })"); await Expect(page.Locator("[data-node-ai-surface]")).ToHaveCountAsync(0);

        owner.Run = null;
        await page.GoBackAsync();

        await Expect(page).ToHaveURLAsync(exact); await Expect(page.Locator("[data-page-state='analysis_run_not_found']")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-workspace], [data-timeline-node], [data-session-overview]")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("reports")]
    public async Task ExactSessionRestoreFailureClosesStaleDialogAndKeepsExactRecoveryRoute(string failureSource)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var reportReads = 0; await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page);
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => { reportReads++; return failureSource == "reports" && reportReads > 1
            ? r.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{\"error\":\"unavailable\"}" })
            : r.FulfillAsync(Json($$"""{"reports":[{"run_id":"{{AiLatestRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("stale report B")}},"snapshot_changed":false}],"next_cursor":null}""")); });
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{AiRunId}", r => failureSource == "generic"
            ? r.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{\"error\":\"unavailable\"}" })
            : r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{AiResult("generic A must not bypass retained report")}}}""")));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }).ClickAsync(); var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "セッションのAI分析" }); await Expect(dialog).ToContainTextAsync("stale report B");
        var exact = host.Url + $"/sessions/{SessionId}?analysis={AiRunId}"; await page.EvaluateAsync("run => window.LocalMonitorV1History.push({ analysis: run })", AiRunId);

        await Expect(page).ToHaveURLAsync(exact); await Expect(dialog).ToBeHiddenAsync(); await Expect(page.GetByText("stale report B")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-session-overview] h2")).ToHaveTextAsync("セッション詳細を表示できません"); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AiResultRendersCompleteAcceptedFieldsSuggestionEvidenceAndTerminalFocus()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await ReadyAi(page);
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json("""{"reports":[],"next_cursor":null}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs", r => r.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" }));
        await page.RouteAsync("**/api/local-monitor/v1/ai/node-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"succeeded","scope_kind":"node","session_id":"{{SessionId}}","node_id":"node-a8a773d6614d5030f505ff195b452dd6","error":null,"result":{{FullAiResult()}}}""")));

        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await page.GetByRole(AriaRole.Button, new() { Name = "この項目をAIで分析" }).ClickAsync();
        var result = page.Locator("[data-node-ai-result]");
        foreach (var expected in new[] { "node", "snapshot-coverage", "finding-id", "supported", "finding limitation", "suggestion-id", "skill", "target label", "rationale", "concrete change", "expected effect", "risk detail", "top limitation", "github_copilot_sdk", "synthetic-model", "configuration-hash", "template-v1", "2026-08-30T01:00:00", "included: 1", "excluded: 0", "content available" }) await Expect(result).ToContainTextAsync(expected);
        await Expect(result.GetByRole(AriaRole.Button, new() { Name = "証拠を表示" })).ToHaveCountAsync(2);
        await Expect(result.GetByRole(AriaRole.Heading, new() { Name = "AIによる解釈" })).ToBeFocusedAsync();
        await result.GetByRole(AriaRole.Button, new() { Name = "証拠を表示" }).Nth(1).ClickAsync(); await Expect(page.Locator("[data-timeline-node][aria-selected=true]")).ToBeFocusedAsync();
    }

    [Theory]
    [InlineData("succeeded", "AIによる解釈")]
    [InlineData("provider_failed", "AIで分析できませんでした")]
    public async Task SessionAnalysisMovesFocusToTerminalResultOrFailureHeading(string state, string heading)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await ReadyAi(page);
        await page.RouteAsync("**/api/local-monitor/v1/ai/sessions/*/reports*", r => r.FulfillAsync(Json(state == "succeeded" ? $$"""{"reports":[{"run_id":"{{AiRunId}}","state":"succeeded","content_state":"retained","result":{{AiResult("focused")}},"snapshot_changed":false}],"next_cursor":null}""" : """{"reports":[],"next_cursor":null}""")));
        await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs", r => r.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = $$"""{"run_id":"{{AiRunId}}"}""" }));
        await page.RouteAsync("**/api/local-monitor/v1/ai/session-runs/*", r => r.FulfillAsync(Json($$"""{"run_id":"{{AiRunId}}","state":"{{state}}","scope_kind":"session","session_id":"{{SessionId}}","node_id":null,"error":null,"result":{{(state == "succeeded" ? AiResult("focused") : "null")}}}""")));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.GetByRole(AriaRole.Button, new() { Name = "AIで分析" }).ClickAsync(); await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "再分析" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Heading, new() { Name = heading })).ToBeFocusedAsync();
    }

    [Fact]
    public async Task TimelineTreeSupportsKeyboardNavigationSelectionAndFocusPreservation()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("event"); timeline["next_cursor"] = null;
        var summaryDocument = JsonNode.Parse(summary)!; summaryDocument["executions"]![0]!["child_count"] = 2; summary = summaryDocument.ToJsonString();
        var first = timeline["items"]![0]!.AsObject(); first["child_count"] = 1; first["has_more_children"] = false; first["collapsed_children"]!["count"] = 1;
        var second = first.DeepClone().AsObject(); second["node_id"] = "node-11111111111111111111111111111111"; second["name"]!["text"] = "second"; second["child_count"] = 0; second["collapsed_children"]!["count"] = 0; timeline["items"]!.AsArray().Add(second);
        var childPage = timeline.DeepClone().AsObject(); childPage["parent_node_id"] = first["node_id"]!.GetValue<string>(); childPage["items"] = new JsonArray(second.DeepClone()); childPage["items"]![0]!["node_id"] = "node-22222222222222222222222222222222"; childPage["items"]![0]!["parent_node_id"] = first["node_id"]!.GetValue<string>(); childPage["items"]![0]!["name"]!["text"] = "child";
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary)));
        await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(r.Request.Url.Contains("parent_node_id") ? childPage.ToJsonString() : timeline.ToJsonString())));
        await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}");

        var tree = page.GetByRole(AriaRole.Tree); var rows = tree.GetByRole(AriaRole.Treeitem); await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.First).ToHaveAttributeAsync("aria-level", "1"); await Expect(rows.First).ToHaveAttributeAsync("aria-setsize", "2"); await Expect(rows.First).ToHaveAttributeAsync("aria-posinset", "1"); await Expect(rows.Nth(1)).ToHaveAttributeAsync("aria-posinset", "2");
        Assert.Equal(1, await tree.Locator("[role=treeitem][tabindex='0']").CountAsync()); Assert.Null(await rows.Nth(1).GetAttributeAsync("aria-expanded"));
        await rows.First.FocusAsync(); await page.Keyboard.PressAsync("ArrowRight"); await Expect(rows.First).ToHaveAttributeAsync("aria-expanded", "true"); await Expect(tree.GetByRole(AriaRole.Treeitem)).ToHaveCountAsync(3);
        await Expect(rows.First).ToHaveAttributeAsync("aria-selected", "false"); Assert.DoesNotContain("node=", page.Url);
        await page.Keyboard.PressAsync("ArrowRight"); await Expect(tree.GetByRole(AriaRole.Treeitem).Nth(1)).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowUp"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("End"); await Expect(tree.GetByRole(AriaRole.Treeitem).Last).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Home"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToHaveAttributeAsync("aria-selected", "true"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToBeFocusedAsync(); Assert.Equal(1, await tree.Locator("[role=treeitem][tabindex='0']").CountAsync());
        await page.Keyboard.PressAsync("Space"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToHaveAttributeAsync("aria-selected", "true");
        var child = tree.GetByRole(AriaRole.Treeitem).Nth(1); await page.EvaluateAsync("() => { const rows=document.querySelectorAll('[role=treeitem]'); rows[0].setAttribute('aria-level','9'); rows[1].setAttribute('aria-level','10'); }"); await tree.GetByRole(AriaRole.Treeitem).First.FocusAsync(); await page.Keyboard.PressAsync("ArrowRight"); await Expect(child).ToBeFocusedAsync(); Assert.Equal(1, await tree.Locator("[role=treeitem][tabindex='0']").CountAsync());
        var selectedUrl = page.Url; await page.Keyboard.PressAsync("ArrowRight"); await Expect(child).ToBeFocusedAsync(); Assert.Equal(selectedUrl, page.Url); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToHaveAttributeAsync("aria-selected", "true");
        await page.Keyboard.PressAsync("ArrowLeft"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowLeft"); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToHaveAttributeAsync("aria-expanded", "false"); Assert.Equal(selectedUrl, page.Url); await Expect(tree.GetByRole(AriaRole.Treeitem).First).ToHaveAttributeAsync("aria-selected", "true");
    }

    [Fact]
    public async Task SessionWorkspaceHasOneMainPageHeadingAndExecutionLandmark()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(Summary("summary-full.json")))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.GetByRole(AriaRole.Main)).ToHaveCountAsync(1); await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Review the retained instruction"); await Expect(page.GetByRole(AriaRole.Region, new() { Name = "実行タイムライン" })).ToHaveCountAsync(1); await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "実行タイムライン", Level = 2 })).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task PaginatedTreeKeepsAuthoritativeSetSizeAndStablePositions()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); summary["executions"]![0]!["child_count"] = 3;
        var firstPage = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); firstPage["workspace_revision"] = summary["workspace_revision"]!.GetValue<string>();
        var second = firstPage["items"]![0]!.DeepClone().AsObject(); second["node_id"] = "node-11111111111111111111111111111111"; second["name"]!["text"] = "second"; firstPage["items"]!.AsArray().Add(second);
        var finalPage = firstPage.DeepClone().AsObject(); var third = second.DeepClone().AsObject(); third["node_id"] = "node-22222222222222222222222222222222"; third["name"]!["text"] = "third"; finalPage["items"] = new JsonArray(third); finalPage["next_cursor"] = null;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary.ToJsonString()))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(r.Request.Url.Contains("after=") ? finalPage.ToJsonString() : firstPage.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        var rows = page.GetByRole(AriaRole.Treeitem); await Expect(rows).ToHaveCountAsync(2); await Expect(rows.First).ToHaveAttributeAsync("aria-setsize", "3"); await Expect(rows.Nth(1)).ToHaveAttributeAsync("aria-posinset", "2");
        await page.GetByRole(AriaRole.Button, new() { Name = "さらに読み込む" }).ClickAsync(); await Expect(rows).ToHaveCountAsync(3); await Expect(rows.First).ToHaveAttributeAsync("aria-posinset", "1"); await Expect(rows.Nth(1)).ToHaveAttributeAsync("aria-posinset", "2"); await Expect(rows.Nth(2)).ToHaveAttributeAsync("aria-posinset", "3"); await Expect(rows.Nth(2)).ToHaveAttributeAsync("aria-setsize", "3");
    }

    [Fact]
    public async Task WorkspaceUsesInternalScrollingAndDismissibleNarrowInspectorOverlay()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new() { Width = 1366, Height = 768 } }); var (summary, timeline, node, revision) = InspectorDocuments("event"); timeline["next_cursor"] = null; var summaryBody = summary;
        var timelineItems = timeline["items"]!.AsArray(); var relatedChildren = node["related"]!["children"]!.AsArray();
        for (var index = 1; index <= 18; index++) { var item = timelineItems[0]!.DeepClone().AsObject(); item["node_id"] = $"node-{index:x32}"; item["name"]!["text"] = $"activity {index}"; timelineItems.Add(item); relatedChildren.Add(item.DeepClone()); }
        var tallSummary = JsonNode.Parse(summary)!.AsObject(); tallSummary["executions"]![0]!["child_count"] = timelineItems.Count; summary = tallSummary.ToJsonString(); summaryBody = summary;
        node["content"]!["event_content"] = JsonNode.Parse("""{"state":"available","available":true}""");
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summaryBody))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/content?*", r => r.FulfillAsync(Json(ContentDocument(revision, "event_content", "sanitized screenshot content").ToJsonString())));
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.InRange(await page.Locator("[data-session-overview]").EvaluateAsync<float>("e => e.getBoundingClientRect().width"), 360, 420);
        var executionScroll = page.Locator("[data-execution-scroll]"); Assert.True(await executionScroll.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight")); await executionScroll.EvaluateAsync("e => e.scrollTop = e.scrollHeight"); Assert.True(await executionScroll.EvaluateAsync<double>("e => e.scrollTop") > 0);
        await page.ScreenshotAsync(new() { Path = ArtifactPath("session-workspace-normal-1366x768.png"), FullPage = true });
        await page.Locator("[data-timeline-node]").First.ClickAsync(); await page.ScreenshotAsync(new() { Path = ArtifactPath("session-workspace-deep-link-inspector.png"), FullPage = true });
        var inspectorScroll = page.Locator("[data-session-overview]"); Assert.True(await inspectorScroll.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight")); await inspectorScroll.EvaluateAsync("e => e.scrollTop = e.scrollHeight"); Assert.True(await inspectorScroll.EvaluateAsync<double>("e => e.scrollTop") > 0);
        await page.GetByRole(AriaRole.Button, new() { Name = "イベント内容を表示" }).ClickAsync(); await page.ScreenshotAsync(new() { Path = ArtifactPath("session-workspace-raw-dialog.png"), FullPage = true }); await page.Keyboard.PressAsync("Escape");
        var archived = JsonNode.Parse(summary)!.AsObject(); archived["session"]!["archive"]!["state"] = "archived"; archived["session"]!["archive"]!["revision"] = 1; archived["session"]!["archive"]!["effectively_eligible"] = false; archived["session"]!["archive"]!["exclusion_reason"] = "session_archived"; summaryBody = archived.ToJsonString(); await page.ReloadAsync(); await Expect(page.Locator("[data-session-context-content]")).ToContainTextAsync("アーカイブ済み"); await page.ScreenshotAsync(new() { Path = ArtifactPath("session-workspace-archived.png"), FullPage = true });
        await page.SetViewportSizeAsync(1000, 700); var inspector = page.Locator("[data-session-overview]"); await Expect(inspector).ToHaveAttributeAsync("role", "dialog"); await page.ScreenshotAsync(new() { Path = ArtifactPath("session-workspace-narrow-overlay.png"), FullPage = true }); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "インスペクターを閉じる" })).ToBeVisibleAsync();
        var close = page.GetByRole(AriaRole.Button, new() { Name = "インスペクターを閉じる" }); await Expect(close).ToBeFocusedAsync(); Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('[data-session-executions]').inert && document.querySelector('.monitor-shell-header').inert"));
        var colors = await inspector.EvaluateAsync<string[]>("e => { const s=getComputedStyle(e); return [s.backgroundColor,s.color]; }"); Assert.Equal("rgb(22, 26, 34)", colors[0]); Assert.Equal("rgb(229, 233, 242)", colors[1]);
        Assert.True(await inspector.EvaluateAsync<double>("e => { const parse=v=>v.match(/\\d+/g).slice(0,3).map(Number); const lum=v=>{ const c=parse(v).map(x=>x/255).map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4); return .2126*c[0]+.7152*c[1]+.0722*c[2]; }; const s=getComputedStyle(e), a=lum(s.backgroundColor), b=lum(s.color); return (Math.max(a,b)+.05)/(Math.min(a,b)+.05); }") >= 4.5);
        await page.Keyboard.PressAsync("Shift+Tab"); Assert.True(await inspector.EvaluateAsync<bool>("e => e.contains(document.activeElement)")); await page.Keyboard.PressAsync("Tab"); await Expect(close).ToBeFocusedAsync();
        await close.ClickAsync(); await Expect(inspector).ToHaveAttributeAsync("aria-hidden", "true"); await Expect(page.Locator("[data-timeline-node]").First).ToBeFocusedAsync();
        await page.SetViewportSizeAsync(1366, 768); await Expect(inspector).Not.ToHaveAttributeAsync("role", "dialog"); await Expect(inspector).Not.ToHaveAttributeAsync("aria-hidden", "true"); Assert.False(await page.EvaluateAsync<bool>("() => document.querySelector('[data-session-executions]').inert || document.querySelector('.monitor-shell-header').inert"));
        await page.SetViewportSizeAsync(1000, 700); await Expect(inspector).ToHaveAttributeAsync("role", "dialog"); await Expect(inspector).ToHaveAttributeAsync("aria-modal", "true"); await Expect(close).ToBeFocusedAsync(); Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('[data-session-executions]').inert && document.querySelector('.monitor-shell-header').inert")); await page.Keyboard.PressAsync("Shift+Tab"); Assert.True(await inspector.EvaluateAsync<bool>("e => e.contains(document.activeElement)"));
        await page.Keyboard.PressAsync("Escape"); await Expect(inspector).ToHaveAttributeAsync("aria-hidden", "true"); await Expect(page.Locator("[data-timeline-node]").First).ToBeFocusedAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
    }

    [Fact]
    public async Task DirectHttpMismatchedExecutionNodePairIsNotFound()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var response = await page.GotoAsync(host.Url + $"/sessions/{SessionId}?execution=8a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6"); Assert.Equal(404, response!.Status);
    }

    [Fact]
    public async Task ToolInspectorReadsRawContentOnlyAfterExplicitActionAndRestoresFocus()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>();
        var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null;
        var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; node["node"]!["kind"] = "tool";
        node["node"]!["metadata"] = JsonNode.Parse("""{"kind":"tool","caller":{"state":"recorded","node_id":"node-2db4028cf76015c954848d7dcbb5deca"},"lifecycle":{"state":"recorded","value":"completed"},"status":{"state":"recorded","value":"completed"},"exit":{"state":"recorded"},"mcp_server_identity":{"state":"recorded","value":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"mcp_server_name":{"state":"recorded","value":"must-not-substitute-server-name"},"mcp_tool_name":{"state":"recorded","value":"sample-tool"},"input":{"state":"available","available":true},"result":{"state":"available","available":true},"error":{"state":"not_captured","available":false},"retry":{"state":"recorded","node_ids":[]},"recovery":{"state":"recorded","node_ids":[]},"child_activity":{"skill":{"state":"not_observed","count":null},"tool":{"state":"recorded","count":0},"subagent":{"state":"not_observed","count":null},"error":{"state":"not_observed","count":null},"retry":{"state":"not_observed","count":null}},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"018f0000-0000-7000-8000-000000000004","trace_id":null,"span_id":null,"event_id":"018f0000-0000-7000-8000-000000000004"}]}}""");
        node["content"]!["tool_input"] = JsonNode.Parse("""{"state":"available","available":true}"""); node["content"]!["tool_result"] = JsonNode.Parse("""{"state":"available","available":true}""");
        var raw = "<img src=x onerror=window.__rawExecuted=true> \ud83d\ude80"; var requests = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        var content = JsonNode.Parse("""{"schema_version":"local-monitor-node-content.response.v2","workspace_revision":"REVISION","session_id":"018f0000-0000-7000-8000-000000000001","node_id":"node-a8a773d6614d5030f505ff195b452dd6","part":"tool_input","state":"available","source_reference":{"store_kind":"session_event_content","source_item_id":"synthetic","revision":1},"text":"TEXT","utf8_byte_length":0,"unicode_scalar_length":0,"truncation":false}""")!.AsObject(); content["workspace_revision"] = revision; content["text"] = raw; content["utf8_byte_length"] = System.Text.Encoding.UTF8.GetByteCount(raw); content["unicode_scalar_length"] = raw.EnumerateRunes().Count();
        await page.RouteAsync("**/content?*", r => { requests.Add(r.Request.Url); return r.FulfillAsync(Json(content.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync();
        var toolInspector = page.Locator("[data-inspector-kind=tool]"); await Expect(toolInspector).ToContainTextAsync("sample-tool"); await Expect(toolInspector).ToContainTextAsync("開始: 2026-08-26T01:02:03.0000000+00:00"); await Expect(toolInspector).ToContainTextAsync("終了: 2026-08-26T01:02:03.0000000+00:00"); await Expect(toolInspector).ToContainTextAsync("所要時間: 0 ms"); await Expect(toolInspector).ToContainTextAsync("ライフサイクル: 完了"); await Expect(toolInspector).ToContainTextAsync("状態: 完了"); await Expect(toolInspector).ToContainTextAsync("終了状態: 記録あり"); await Expect(toolInspector).ToContainTextAsync("MCPサーバーID: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"); await Expect(toolInspector).Not.ToContainTextAsync("must-not-substitute-server-name"); Assert.Empty(requests);
        var trigger = page.GetByRole(AriaRole.Button, new() { Name = "ツール入力を表示" }); await trigger.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync(); await Expect(page.GetByRole(AriaRole.Dialog).Locator("pre")).ToHaveTextAsync(raw);
        Assert.False(await page.EvaluateAsync<bool>("() => Boolean(window.__rawExecuted)")); Assert.DoesNotContain(raw, page.Url); Assert.False(await page.EvaluateAsync<bool>("raw => [...document.querySelectorAll('*')].some(e => [...e.attributes].some(a => a.name.startsWith('data-') && a.value.includes(raw)))", raw));
        await page.Keyboard.PressAsync("Escape"); await Expect(page.GetByRole(AriaRole.Dialog)).ToBeHiddenAsync(); await Expect(trigger).ToBeFocusedAsync(); Assert.Single(requests);
    }

    [Fact]
    public async Task SkillInspectorUsesOnlyHistoricalAndCurrentFileRoutesWithoutGenericFallback()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null;
        var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; node["node"]!["kind"] = "skill"; node["node"]!["content_parts"] = new JsonArray();
        node["node"]!["metadata"] = JsonNode.Parse("""{"kind":"skill","current_valid_state":"stale","source":{"state":"recorded","value":"copilot-sdk"},"trigger":{"state":"recorded","value":"explicit"},"inventory_reference":{"state":"recorded","value":"inventory-1"},"historical_snapshot_reference":{"state":"recorded","value":"018f0000-0000-7000-8000-000000000099"}}""");
        var requests = new List<(string Method, Uri Uri, string? ContentType, string? Csrf, string? Body)>(); var genericRequests = 0; await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/skill-invocations/**", async r => { requests.Add((r.Request.Method, new Uri(r.Request.Url), r.Request.Headers.GetValueOrDefault("content-type"), r.Request.Headers.GetValueOrDefault("x-monitor-csrf"), r.Request.PostData)); await r.FulfillAsync(r.Request.Method == "POST" ? Json("""{"schema_version":"local-skill-current-file-read.response.v1","snapshot_id":"018f0000-0000-7000-8000-000000000099","content_kind":"current_file","comparison":"changed","historical_body_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","current_body_sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","current_body_utf8_bytes":7,"body":"current","read_at":"2026-08-29T01:02:03.0000000+00:00"}""") : Json("""{"schema_version":"local-skill-invocation-snapshot.content.v1","snapshot_id":"018f0000-0000-7000-8000-000000000099","content_kind":"historical_snapshot","body":"historical","definition_path":"SKILL.md","body_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","definition_path_sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","captured_at":"2026-08-29T01:02:03.0000000+00:00"}""")); });
        await page.RouteAsync("**/content?*", r => { genericRequests++; return r.AbortAsync(); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await page.GetByRole(AriaRole.Button, new() { Name = "履歴スナップショットを表示" }).ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("履歴スナップショット"); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("定義パス: SKILL.md"); await page.Keyboard.PressAsync("Escape");
        await page.GetByRole(AriaRole.Button, new() { Name = "現在のファイルを読み取る" }).ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("現在のファイル"); Assert.Equal(0, genericRequests); Assert.Equal(2, requests.Count); Assert.Equal(("GET", $"/api/local-monitor/v1/sessions/{SessionId}/skill-invocations/018f0000-0000-7000-8000-000000000099/content", ""), (requests[0].Method, requests[0].Uri.AbsolutePath, requests[0].Uri.Query)); Assert.Equal(("POST", $"/api/local-monitor/v1/sessions/{SessionId}/skill-invocations/018f0000-0000-7000-8000-000000000099/current-file-read", ""), (requests[1].Method, requests[1].Uri.AbsolutePath, requests[1].Uri.Query)); Assert.Equal("application/json", requests[1].ContentType); Assert.Equal("local-monitor", requests[1].Csrf); Assert.Equal("{\"schema_version\":\"local-skill-current-file-read.request.v1\"}", requests[1].Body);
    }

    [Theory]
    [InlineData("subagent", "サブエージェント入力", "子項目: 2")]
    [InlineData("error", "エラーメッセージ", "エラーコード: E_SAMPLE")]
    [InlineData("permission", "指示", "判断: 拒否")]
    [InlineData("event", "イベント内容", "イベント: sample.event")]
    [InlineData("retry", "ツール結果", "試行回数: 3")]
    public async Task ClosedKindInspectorRendersOnlyDocumentedFactsAndExplicitContent(string kind, string contentLabel, string expected)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, revision) = InspectorDocuments(kind); var part = PartForLabel(contentLabel); node["content"]![part] = JsonNode.Parse("""{"state":"available","available":true}""");
        var raw = kind == "subagent" ? "Claude delegated prompt without identifier" : $"{kind} raw"; var requestCount = 0; var console = new List<string>(); page.Console += (_, message) => console.Add(message.Text);
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString())));
        await page.RouteAsync("**/content?*", r => { requestCount++; var response = ContentDocument(revision, part, raw); return r.FulfillAsync(Json(response.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); var inspector = page.Locator($"[data-inspector-kind={kind}]"); await Expect(inspector).ToContainTextAsync(expected); await Expect(inspector).ToContainTextAsync("親項目の経路"); await Expect(inspector).ToContainTextAsync("技術情報"); await Expect(inspector).ToContainTextAsync("再試行"); await Expect(inspector).ToContainTextAsync("復旧"); Assert.Equal(0, requestCount);
        if (kind == "subagent") { await Expect(inspector).ToContainTextAsync("選択: 記録あり"); await Expect(inspector).ToContainTextAsync("開始: 記録あり"); await Expect(inspector).ToContainTextAsync("完了: 記録あり"); await Expect(inspector).ToContainTextAsync("失敗: 今回の記録にはありません"); await Expect(inspector).ToContainTextAsync("選択解除: 今回の記録にはありません"); await Expect(inspector).ToContainTextAsync("スキル活動: 1"); await Expect(inspector).ToContainTextAsync("ツール活動: 2"); await Expect(inspector).ToContainTextAsync("入力トークン: 5"); await Expect(inspector).ToContainTextAsync("出力トークン: 3"); await Expect(inspector).ToContainTextAsync("トークン合計: 8"); await Expect(inspector).ToContainTextAsync("推論トークン: 今回の記録にはありません"); await Expect(inspector).Not.ToContainTextAsync("agent_id"); }
        var trigger = page.GetByRole(AriaRole.Button, new() { Name = $"{contentLabel}を表示" }); await trigger.ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog).Locator("pre")).ToHaveTextAsync(raw); Assert.Equal(1, requestCount); Assert.Empty(console);
        await page.Keyboard.PressAsync("Tab"); await page.Keyboard.PressAsync("Tab"); Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('[data-raw-content-dialog]').contains(document.activeElement)")); await page.Keyboard.PressAsync("Escape"); await Expect(trigger).ToBeFocusedAsync();
        Assert.DoesNotContain(raw, page.Url); Assert.False(await page.EvaluateAsync<bool>("raw => [...document.querySelectorAll('*')].some(e => [...e.attributes].some(a => a.name.startsWith('data-') && a.value.includes(raw)))", raw));
    }

    [Theory]
    [InlineData("instruction")]
    [InlineData("tool_input")]
    [InlineData("tool_result")]
    [InlineData("error_message")]
    [InlineData("subagent_input")]
    [InlineData("event_content")]
    public async Task EveryClosedContentPartUsesExactExplicitReadAndPublishesLengthsAndSource(string part)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, revision) = InspectorDocuments("permission"); node["content"]![part] = JsonNode.Parse("""{"state":"available","available":true}"""); var raw = $"{part} \ud83d\ude80"; var urls = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.RouteAsync("**/content?*", r => { urls.Add(r.Request.Url); return r.FulfillAsync(Json(ContentDocument(revision, part, raw).ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); Assert.Empty(urls); await page.GetByRole(AriaRole.Button, new() { Name = $"{PartLabel(part)}を表示" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync($"{System.Text.Encoding.UTF8.GetByteCount(raw)}バイト"); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync($"{raw.EnumerateRunes().Count()}文字"); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("session_event_content · synthetic · リビジョン 1"); Assert.Single(urls); Assert.EndsWith($"part={part}", urls[0]);
    }

    [Theory]
    [InlineData("not_captured", "内容は記録されていません")]
    [InlineData("expired", "保存期間を過ぎたため表示できません")]
    [InlineData("deleted", "内容は保存されていません")]
    [InlineData("read_denied", "内容を読み取れません")]
    [InlineData("oversized", "内容が大きすぎるため表示できません")]
    [InlineData("invalid", "記録が一部欠けています")]
    public async Task EveryUnavailableContentStateIsDistinctAndNeverFetches(string contentState, string expected)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("permission"); node["content"]!["instruction"] = JsonNode.Parse($$"""{"state":"{{contentState}}","available":false}"""); var requests = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.RouteAsync("**/content?*", r => { requests++; return r.AbortAsync(); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await Expect(page.Locator("[data-inspector-kind=permission]")).ToContainTextAsync(expected); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "指示を表示" })).ToHaveCountAsync(0); Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(404, "内容は記録されていません")]
    [InlineData(410, "保存期間を過ぎたため表示できません")]
    [InlineData(403, "内容を読み取れません")]
    [InlineData(413, "内容が大きすぎるため表示できません")]
    [InlineData(409, "記録内容が更新されました")]
    [InlineData(503, "記録内容を一時的に表示できません")]
    public async Task RawReadHttpStatesStayDistinctWithoutPublishingErrorBodies(int status, string expected)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("permission"); node["content"]!["instruction"] = JsonNode.Parse("""{"state":"available","available":true}"""); var secret = "raw-error-secret"; var count = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.RouteAsync("**/content?*", r => { count++; return r.FulfillAsync(new RouteFulfillOptions { Status = status, ContentType = "application/json", Body = $$"""{"error":"{{secret}}"}""" }); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); var trigger = page.GetByRole(AriaRole.Button, new() { Name = "指示を表示" }); await trigger.ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync(expected); await Expect(page.GetByRole(AriaRole.Dialog)).Not.ToContainTextAsync(secret); Assert.Equal(1, count); await page.Keyboard.PressAsync("Escape"); await Expect(trigger).ToBeFocusedAsync();
    }

    [Fact]
    public async Task SkillWithoutSnapshotOffersNoRawActionAndMakesNoSubstitutionRequest()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, _) = InspectorDocuments("skill"); node["node"]!["metadata"]!["historical_snapshot_reference"] = JsonNode.Parse("""{"state":"not_observed","value":null}"""); var rawRequests = 0;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.RouteAsync("**/content?*", r => { rawRequests++; return r.AbortAsync(); }); await page.RouteAsync("**/skill-invocations/**", r => { rawRequests++; return r.AbortAsync(); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await Expect(page.Locator("[data-inspector-kind=skill]")).ToContainTextAsync("履歴スナップショットはありません"); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "履歴スナップショットを表示" })).ToHaveCountAsync(0); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "現在のファイルを読み取る" })).ToHaveCountAsync(0); Assert.Equal(0, rawRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task MalformedTimelineFailsClosedBeforeRowsOrCacheMutation(int mutation)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null;
        if (mutation == 0) timeline["extra"] = true;
        else if (mutation == 1) timeline["items"]![0]!["relationship_authority"] = "inferred";
        else if (mutation == 2) timeline["items"]![0]!.AsObject().Remove("status");
        else if (mutation == 3) timeline["items"]![0]!["kind"] = "bogus";
        else if (mutation == 4) timeline["items"]![0]!["name"]!["state"] = "expired";
        else if (mutation == 5) timeline["items"]![0]!["collapsed_children"]!["state"] = "unknown";
        else if (mutation == 6) timeline["items"]![0]!["content_parts"] = new JsonArray("tool_result", "tool_input");
        else if (mutation == 7) timeline["items"]![0]!["source_references"]!["references"] = new JsonArray();
        else if (mutation == 8) timeline["next_cursor"] = new string('A', 158) + "B";
        else if (mutation == 9) timeline["items"]![0]!["collapsed_children"]!["count"] = 4097;
        else if (mutation == 10) timeline["items"]![0]!["source_references"]!["references"]!.AsArray().Add(timeline["items"]![0]!["source_references"]!["references"]![0]!.DeepClone());
        else
        {
            var reference = timeline["items"]![0]!["source_references"]!["references"]![0]!;
            reference["source_kind"] = "session_event"; reference["source_identity"] = null; reference["trace_id"] = null; reference["span_id"] = null; reference["event_id"] = null;
        }
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator("[data-timeline-node]")).ToHaveCountAsync(0); Assert.Equal(0, await page.EvaluateAsync<int>("() => window.LocalMonitorSessionWorkspace.executionState.values().next().value.pages.size"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task MalformedNodeFailsClosedAndPreservesExactRecovery(int mutation)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision;
        if (mutation == 0) node["node"]!["unexpected"] = true;
        else if (mutation == 1) node["node"]!["metadata"]!["kind"] = "tool";
        else if (mutation == 2) node["node"]!["metadata"]!["source_references"]!["references"]![0]!["extra"] = true;
        else if (mutation == 3) node["node"]!["metadata"]!["content"]!["available"] = true;
        else if (mutation == 4) node["execution"]!["source"] = 4;
        else if (mutation == 5) node["node"]!["technical_references"]!["trace_id"] = 4;
        else if (mutation == 6) node["content"]!["event_content"]!["state"] = "bogus";
        else if (mutation == 7) node["parent_path"]![0]!["parent_node_id"] = "node-00000000000000000000000000000009";
        else if (mutation == 8) { var related = node["parent_path"]![0]!.DeepClone(); related["relationship_authority"] = "unknown"; node["related"]!["children"]!.AsArray().Add(related); }
        else if (mutation == 9) node["execution"]!["child_count"] = 4097;
        else if (mutation == 10) node["node"]!["collapsed_children"]!["count"] = 4097;
        else if (mutation == 11) node["node"]!["metadata"]!["content"]!["state"] = "source_unsupported";
        else if (mutation == 12) node["node"]!["metadata"]!["source_time"]!["value"] = "2026-08-26T01:02:03Z";
        else if (mutation == 13) node["parent_path"]![0]!["node_id"] = "node-00000000000000000000000000000009";
        else node["parent_path"]![0]!["node_id"] = node["node"]!["node_id"]!.GetValue<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("もう一度お試しください"); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6");
    }

    [Fact]
    public Task RecordedSourceReferenceAllowsNullKindWithExactIdentity() => AssertValidNodeShape(node =>
    {
        node["node"]!["source_references"]!["references"]![0]!["source_kind"] = null;
        node["node"]!["metadata"]!["source_references"]!["references"]![0]!["source_kind"] = null;
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task TechnicalReferencesAllowIndependentNullability(int shape) => AssertValidNodeShape(node =>
    {
        var references = node["node"]!["technical_references"]!;
        if (shape == 0)
        {
            references["source_kind"] = null;
            references["source_identity"] = null;
            references["trace_id"] = "00000000000000000000000000000001";
            references["span_id"] = null;
            references["event_id"] = null;
        }
        else
        {
            references["source_kind"] = "session_event";
            references["source_identity"] = null;
            references["trace_id"] = null;
            references["span_id"] = null;
            references["event_id"] = null;
        }
    });

    [Fact]
    public Task ToolMetadataAcceptsRecordedEmptyNodeSetsAndCompleteChildActivity() => AssertValidNodeShape(node =>
    {
        node["node"]!["kind"] = "tool";
        node["node"]!["metadata"] = JsonNode.Parse("""{"kind":"tool","caller":{"state":"recorded","node_id":"node-2db4028cf76015c954848d7dcbb5deca"},"lifecycle":{"state":"recorded","value":"completed"},"status":{"state":"recorded","value":"completed"},"exit":{"state":"not_observed"},"mcp_server_identity":{"state":"not_observed","value":null},"mcp_server_name":{"state":"not_observed","value":null},"mcp_tool_name":{"state":"recorded","value":"demo"},"input":{"state":"not_captured","available":false},"result":{"state":"not_captured","available":false},"error":{"state":"not_captured","available":false},"retry":{"state":"recorded","node_ids":[]},"recovery":{"state":"recorded","node_ids":[]},"child_activity":{"skill":{"state":"not_observed","count":null},"tool":{"state":"recorded","count":0},"subagent":{"state":"not_observed","count":null},"error":{"state":"not_observed","count":null},"retry":{"state":"not_observed","count":null}},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"018f0000-0000-7000-8000-000000000004","trace_id":null,"span_id":null,"event_id":"018f0000-0000-7000-8000-000000000004"}]}}""");
    });

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("asked")]
    [InlineData("unknown")]
    public Task PermissionMetadataAcceptsClosedDecisionVocabulary(string decision) => AssertValidNodeShape(node =>
    {
        node["node"]!["kind"] = "permission";
        node["node"]!["metadata"] = JsonNode.Parse("""{"kind":"permission","decision":{"state":"recorded","value":"unknown"},"wait":{"state":"not_observed"},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"018f0000-0000-7000-8000-000000000004","trace_id":null,"span_id":null,"event_id":"018f0000-0000-7000-8000-000000000004"}]}}""");
        node["node"]!["metadata"]!["decision"]!["value"] = decision;
    });

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
        await Expect(page.Locator("[data-execution-toggle]")).ToHaveCountAsync(2); await Expect(page.Locator("[data-execution-toggle][aria-expanded=true]")).ToHaveCountAsync(1); var collapsed = page.Locator("[data-execution-toggle][aria-expanded=false]"); await Expect(collapsed).ToContainTextAsync("活動 2件"); await Expect(collapsed).ToContainTextAsync("トークン"); await Expect(collapsed).ToContainTextAsync("エラー"); await Expect(collapsed).ToContainTextAsync("再試行");
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
    public async Task RecordedOpenTimingRendersOpenFactWithoutInferringDurationGeometry()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var item = timeline["items"]![0]!; item["status"] = "active"; item["lifecycle"] = "started"; item["timing"]!["ended_at"] = null; item["timing"]!["duration_ms"] = null; item["collapsed_children"]!["state"] = "unavailable"; item["collapsed_children"]!["count"] = null;
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await Expect(page.Locator("[data-timeline-node]")).ToContainTextAsync("2026-08-26T01:02:03.0000000+00:00"); await Expect(page.Locator("[data-timeline-time-bar]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ZeroDurationUsesInstantMarkerWithoutFabricatedBar()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; timeline["items"]![0]!["timing"]!["duration_ms"] = 0; timeline["items"]![0]!["timing"]!["ended_at"] = timeline["items"]![0]!["timing"]!["started_at"]!.GetValue<string>(); await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-timeline-time-bar]")).ToHaveCountAsync(0); await Expect(page.Locator("[data-timeline-instant]")).ToHaveCountAsync(1);
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
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}?node=node-a8a773d6614d5030f505ff195b452dd6"); await Expect(page).ToHaveURLAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); await page.ReloadAsync(); await Expect(page).ToHaveURLAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: null, node: null })"); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); await page.GoBackAsync(); await Expect(page).ToHaveURLAsync(host.Url + exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); await page.GoForwardAsync(); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); Assert.True(nodeCalls >= 4);
    }

    [Fact]
    public async Task MismatchedNodeFallsBackToSessionOverviewWithoutSimilarityRepair()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; var mismatch = false; var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4";
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(empty.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => { var response = node.DeepClone(); if (mismatch) response["node"]!["node_id"] = "node-00000000000000000000000000000009"; return r.FulfillAsync(Json(response.ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message"); mismatch = true; await page.EvaluateAsync("() => document.dispatchEvent(new CustomEvent('cao-route-state', { detail: window.LocalMonitorV1History.current() }))");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); Assert.Null(await page.EvaluateAsync<string?>("() => window.LocalMonitorSessionWorkspace.selectedNodeId")); await Expect(page).ToHaveURLAsync(host.Url + $"/sessions/{SessionId}");
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
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("user.message");
        Assert.Equal(2, summaries); Assert.Equal(2, nodeUrls.Count); Assert.Contains(oldSummary["workspace_revision"]!.GetValue<string>(), nodeUrls[0]); Assert.Contains(fresh, nodeUrls[1]); Assert.All(nodeUrls, u => Assert.Contains("node-a8a773d6614d5030f505ff195b452dd6", u));
    }

    [Fact]
    public async Task ExecutionOnlyRouteOpensExactExecutionAndKeepsOverview()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = JsonNode.Parse(Summary("summary-full.json"))!.AsObject(); AddSecondExecution(summary, "2026-08-26T01:02:02.0000000+00:00", "claude-code", false); var revision = summary["workspace_revision"]!.GetValue<string>(); var urls = new List<string>();
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary.ToJsonString()))); await page.RouteAsync("**/timeline?*", r => { urls.Add(r.Request.Url); var body = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); body["workspace_revision"] = revision; body["execution_id"] = "8a5590c8-46e3-7069-af48-3844d2bf17a4"; return r.FulfillAsync(Json(body.ToJsonString())); });
        var exact = host.Url + $"/sessions/{SessionId}?execution=8a5590c8-46e3-7069-af48-3844d2bf17a4"; await page.GotoAsync(exact); await Expect(page).ToHaveURLAsync(exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); Assert.Equal("8a5590c8-46e3-7069-af48-3844d2bf17a4", await page.EvaluateAsync<string>("() => window.LocalMonitorSessionWorkspace.selectedExecutionId")); Assert.Single(urls); Assert.Contains("execution_id=8a5590c8-46e3-7069-af48-3844d2bf17a4", urls[0]); await Expect(page.Locator("[data-execution-id='9a5590c8-46e3-7069-af48-3844d2bf17a4'] [data-execution-toggle]")).ToHaveAttributeAsync("aria-expanded", "false"); await page.ReloadAsync(); await Expect(page).ToHaveURLAsync(exact); Assert.Equal("8a5590c8-46e3-7069-af48-3844d2bf17a4", await page.EvaluateAsync<string>("() => window.LocalMonitorSessionWorkspace.selectedExecutionId")); Assert.Equal(2, urls.Count);
    }

    [Fact]
    public async Task GenericContentStaleRefreshesAndRetriesExactPartOnce()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options()); PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var (summary, timeline, node, oldRevision) = InspectorDocuments("permission"); node["content"]!["instruction"] = JsonNode.Parse("""{"state":"available","available":true}"""); var freshRevision = new string('2', 64); var summaries = 0; var urls = new List<string>();
        await page.RouteAsync("**/summary", r => { summaries++; if (summaries == 1) return r.FulfillAsync(Json(summary)); var fresh = JsonNode.Parse(summary)!.AsObject(); fresh["workspace_revision"] = freshRevision; return r.FulfillAsync(Json(fresh.ToJsonString())); }); await page.RouteAsync("**/timeline?*", r => { var body = timeline.DeepClone(); body["workspace_revision"] = r.Request.Url.Contains(freshRevision) ? freshRevision : oldRevision; return r.FulfillAsync(Json(body.ToJsonString())); }); await page.RouteAsync("**/nodes/*?*", r => { var body = node.DeepClone(); body["workspace_revision"] = r.Request.Url.Contains(freshRevision) ? freshRevision : oldRevision; return r.FulfillAsync(Json(body.ToJsonString())); }); await page.RouteAsync("**/content?*", r => { urls.Add(r.Request.Url); if (urls.Count == 1) return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_snapshot_stale\"}" }); return r.FulfillAsync(Json(ContentDocument(freshRevision, "instruction", "fresh raw").ToJsonString())); });
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await page.Locator("[data-timeline-node]").ClickAsync(); await page.GetByRole(AriaRole.Button, new() { Name = "指示を表示" }).ClickAsync(); await Expect(page.GetByRole(AriaRole.Dialog).Locator("pre")).ToHaveTextAsync("fresh raw"); Assert.Equal(2, summaries); Assert.Equal(2, urls.Count); Assert.Contains(oldRevision, urls[0]); Assert.Contains(freshRevision, urls[1]);
    }

    [Fact]
    public async Task NonStaleConflictDoesNotRefreshOrRetryExactNode()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync(); var summaries = 0; var nodes = 0;
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var empty = JsonNode.Parse(Summary("timeline-empty.json"))!.AsObject(); empty["workspace_revision"] = revision; empty["execution_id"] = "9a5590c8-46e3-7069-af48-3844d2bf17a4";
        await page.RouteAsync("**/summary", r => { summaries++; return r.FulfillAsync(Json(summary)); }); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(empty.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => { nodes++; return r.FulfillAsync(new RouteFulfillOptions { Status = 409, ContentType = "application/json", Body = "{\"error\":\"workspace_too_large\"}" }); });
        var exact = host.Url + $"/sessions/{SessionId}?execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6"; await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })"); await Expect(page).ToHaveURLAsync(exact); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッション一覧を開いてください"); Assert.Equal(1, summaries); Assert.Equal(1, nodes);
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
        await page.GotoAsync(host.Url + $"/sessions/{SessionId}"); await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要"); await page.WaitForTimeoutAsync(200);
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

        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("取得元 VS Code");
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("時刻 1,000 ms");
        await Expect(page.Locator("[data-session-overview] details")).ToContainTextAsync("native_session_ids");

        await Expect(page.Locator("[data-execution-toggle][aria-expanded=true]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-timeline-node]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-timeline-time-bar]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-timeline-instant]")).ToHaveCountAsync(1);
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
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要");
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
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要");
        await Expect(page.Locator("[data-session-overview-source]")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("[data-session-overview-time]")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("[data-session-source]")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("[data-session-time]")).ToContainTextAsync("今回の記録にはありません");
        foreach (var name in new[] { "input", "output", "cache-read", "new-input", "coverage" })
            await Expect(page.Locator($"[data-session-fixed-{name}]")).ToContainTextAsync("今回の記録にはありません");

        fixture = "summary-nonrecorded-evidence.json";
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-overview]")).ToContainTextAsync("セッションの概要");
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
        foreach (var label in new[] { "入力 10", "出力 5", "キャッシュから読み込み 4", "新規入力 6" })
            await Expect(page.GetByLabel(label)).ToHaveCountAsync(1);
    }

    private static async Task AssertValidNodeShape(Action<JsonObject> mutate)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault(); using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }); var page = await browser.NewPageAsync();
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; mutate(node);
        await page.RouteAsync("**/summary", r => r.FulfillAsync(Json(summary))); await page.RouteAsync("**/timeline?*", r => r.FulfillAsync(Json(timeline.ToJsonString()))); await page.RouteAsync("**/nodes/*?*", r => r.FulfillAsync(Json(node.ToJsonString()))); await page.GotoAsync(host.Url + $"/sessions/{SessionId}");
        await page.EvaluateAsync("() => window.LocalMonitorV1History.push({ execution: '9a5590c8-46e3-7069-af48-3844d2bf17a4', node: 'node-a8a773d6614d5030f505ff195b452dd6' })");
        await Expect(page.Locator("[data-session-overview] h2")).ToHaveTextAsync("user.message"); Assert.Equal("node-a8a773d6614d5030f505ff195b452dd6", await page.EvaluateAsync<string?>("() => window.LocalMonitorSessionWorkspace.selectedNodeId"));
    }

    private static (string Summary, JsonObject Timeline, JsonObject Node, string Revision) InspectorDocuments(string kind)
    {
        var summary = Summary("summary-full.json"); var revision = JsonNode.Parse(summary)!["workspace_revision"]!.GetValue<string>(); var timeline = JsonNode.Parse(Summary("timeline-page.json"))!.AsObject(); timeline["workspace_revision"] = revision; timeline["next_cursor"] = null; var node = JsonNode.Parse(Summary("node-nested.json"))!.AsObject(); node["workspace_revision"] = revision; node["node"]!["kind"] = kind;
        node["node"]!["metadata"] = kind switch
        {
            "subagent" => JsonNode.Parse("""{"kind":"subagent","lifecycle":{"selected":{"state":"recorded"},"started":{"state":"recorded"},"completed":{"state":"recorded"},"failed":{"state":"not_observed"},"deselected":{"state":"not_observed"}},"input":{"state":"available","available":true},"activity":{"skill":{"state":"recorded","count":1},"tool":{"state":"recorded","count":2},"subagent":{"state":"recorded","count":0},"error":{"state":"recorded","count":0},"retry":{"state":"recorded","count":0}},"tokens":{"authority":"llm_span","state":"recorded","available_execution_count":1,"total_execution_count":1,"input":{"state":"recorded","value":5},"output":{"state":"recorded","value":3},"total":{"state":"recorded","value":8},"reasoning":{"state":"not_observed","value":null},"cache_read":{"state":"recorded","value":0},"cache_creation":{"state":"not_observed","value":null},"new_input":{"state":"recorded","value":5},"cache_read_ratio_basis_points":{"state":"recorded","value":0}},"children":{"state":"recorded","count":2},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"synthetic-subagent","trace_id":null,"span_id":null,"event_id":"synthetic-event"}]}}"""),
            "error" => JsonNode.Parse("""{"kind":"error","error_code":{"state":"recorded","value":"E_SAMPLE"},"message":{"state":"available","available":true},"status":{"state":"recorded","value":"failed"},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"synthetic-error","trace_id":null,"span_id":null,"event_id":"synthetic-event"}]}}"""),
            "permission" => JsonNode.Parse("""{"kind":"permission","decision":{"state":"recorded","value":"denied"},"wait":{"state":"recorded"},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"synthetic-permission","trace_id":null,"span_id":null,"event_id":"synthetic-event"}]}}"""),
            "event" => JsonNode.Parse("""{"kind":"event","event_name":{"state":"recorded","value":"sample.event"},"source_time":{"state":"recorded","value":"2026-08-26T01:02:03.0000000+00:00"},"content":{"state":"available","available":true},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"synthetic-event","trace_id":null,"span_id":null,"event_id":"synthetic-event"}]}}"""),
            "retry" => JsonNode.Parse("""{"kind":"retry","attempt":{"state":"recorded","value":3},"target":{"state":"recorded","node_id":"node-2db4028cf76015c954848d7dcbb5deca"},"recovered":{"state":"recorded","value":true},"source_references":{"state":"recorded","references":[{"source_kind":"session_event","source_identity":"synthetic-retry","trace_id":null,"span_id":null,"event_id":"synthetic-event"}]}}"""),
            "skill" => JsonNode.Parse("""{"kind":"skill","current_valid_state":"stale","source":{"state":"recorded","value":"copilot-sdk"},"trigger":{"state":"recorded","value":"explicit"},"inventory_reference":{"state":"recorded","value":"inventory-1"},"historical_snapshot_reference":{"state":"recorded","value":"018f0000-0000-7000-8000-000000000099"}}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var retry = timeline["items"]![0]!.DeepClone(); retry["node_id"] = "node-11111111111111111111111111111111"; retry["relationship_authority"] = "exact"; var recovery = timeline["items"]![0]!.DeepClone(); recovery["node_id"] = "node-22222222222222222222222222222222"; recovery["relationship_authority"] = "explicit"; node["related"]!["retry"]!.AsArray().Add(retry); node["related"]!["recovery"]!.AsArray().Add(recovery);
        return (summary, timeline, node, revision);
    }

    private static JsonObject ContentDocument(string revision, string part, string text)
    {
        var document = JsonNode.Parse("""{"schema_version":"local-monitor-node-content.response.v2","workspace_revision":"REVISION","session_id":"018f0000-0000-7000-8000-000000000001","node_id":"node-a8a773d6614d5030f505ff195b452dd6","part":"PART","state":"available","source_reference":{"store_kind":"session_event_content","source_item_id":"synthetic","revision":1},"text":"TEXT","utf8_byte_length":0,"unicode_scalar_length":0,"truncation":false}""")!.AsObject(); document["workspace_revision"] = revision; document["part"] = part; document["text"] = text; document["utf8_byte_length"] = System.Text.Encoding.UTF8.GetByteCount(text); document["unicode_scalar_length"] = text.EnumerateRunes().Count(); return document;
    }

    private static string PartForLabel(string label) => label switch { "指示" => "instruction", "ツール入力" => "tool_input", "ツール結果" => "tool_result", "エラーメッセージ" => "error_message", "サブエージェント入力" => "subagent_input", "イベント内容" => "event_content", _ => throw new ArgumentOutOfRangeException(nameof(label)) };
    private static string PartLabel(string part) => part switch { "instruction" => "指示", "tool_input" => "ツール入力", "tool_result" => "ツール結果", "error_message" => "エラーメッセージ", "subagent_input" => "サブエージェント入力", "event_content" => "イベント内容", _ => throw new ArgumentOutOfRangeException(nameof(part)) };

    private static string Summary(string name) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "LocalMonitorV1SessionDetail", name)));

    private static string AiResult(string summary, string evidence = "node-a8a773d6614d5030f505ff195b452dd6") =>
        "{\"scope\":{\"kind\":\"session\",\"session_id\":\"" + SessionId + "\",\"node_id\":null,\"anchor_id\":\"" + SessionId
        + "\"},\"snapshot\":{\"snapshot_id\":\"018f0000-0000-7000-8000-000000000072\",\"payload_sha256\":\"" + new string('a', 64)
        + "\"},\"summary\":" + System.Text.Json.JsonSerializer.Serialize(summary)
        + ",\"findings\":[{\"finding_id\":\"f-1\",\"title\":\"Finding\",\"explanation\":\"Explanation\",\"evidence_state\":\"supported\",\"evidence_refs\":[\""
        + evidence + "\"],\"limitation\":\"none\"}],\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":{\"provider\":\"github_copilot_sdk\",\"model\":\"synthetic\"}}";

    private static string FullAiResult() =>
        "{\"scope\":{\"kind\":\"node\",\"session_id\":\"" + SessionId + "\",\"node_id\":\"node-a8a773d6614d5030f505ff195b452dd6\",\"anchor_id\":\"node-a8a773d6614d5030f505ff195b452dd6\"},\"snapshot\":{\"snapshot_id\":\"snapshot-coverage\",\"payload_sha256\":\"" + new string('a', 64)
        + "\"},\"summary\":\"complete summary\",\"findings\":[{\"finding_id\":\"finding-id\",\"title\":\"Finding title\",\"explanation\":\"Finding explanation\",\"evidence_state\":\"supported\",\"evidence_refs\":[\"node-a8a773d6614d5030f505ff195b452dd6\"],\"limitation\":\"finding limitation\"}],\"improvement_suggestions\":[{\"suggestion_id\":\"suggestion-id\",\"target_kind\":\"skill\",\"target_label\":\"target label\",\"rationale\":\"rationale\",\"concrete_change\":\"concrete change\",\"expected_effect\":\"expected effect\",\"risks_or_limitations\":\"risk detail\",\"evidence_refs\":[\"node-a8a773d6614d5030f505ff195b452dd6\"]}],\"limitations\":[\"top limitation\"],\"provenance\":{\"provider\":\"github_copilot_sdk\",\"model\":\"synthetic-model\",\"configuration_sha256\":\"configuration-hash\",\"prompt_template_version\":\"template-v1\",\"requested_at\":\"2026-08-30T01:00:00.0000000+00:00\",\"started_at\":\"2026-08-30T01:00:01.0000000+00:00\",\"completed_at\":\"2026-08-30T01:00:02.0000000+00:00\",\"snapshot_id\":\"snapshot-coverage\",\"snapshot_sha256\":\"" + new string('a', 64) + "\",\"coverage\":{\"included\":1,\"excluded\":0,\"content_available\":true}}}";

    private static string AiZeroResult(string summary) =>
        "{\"scope\":{\"kind\":\"session\",\"session_id\":\"" + SessionId + "\",\"node_id\":null,\"anchor_id\":\"" + SessionId
        + "\"},\"snapshot\":{\"snapshot_id\":\"018f0000-0000-7000-8000-000000000072\",\"payload_sha256\":\"" + new string('a', 64)
        + "\"},\"summary\":" + System.Text.Json.JsonSerializer.Serialize(summary)
        + ",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":{\"provider\":\"github_copilot_sdk\",\"model\":\"synthetic\"}}";

    private static string Readiness() => """{"provider":"github_copilot","selected_model":"synthetic","selected_configuration":"test","readiness_state":"ready","last_check_result":"ready","provider_egress_notice":"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action"}""";
    private static Task ReadyAi(IPage page) => page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", r => r.FulfillAsync(Json(Readiness())));

    private static string ArtifactPath(string name)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "session-workspace"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

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

    private static MonitorHostTestOptions Options(LocalAiRunStatusV1? run = null) =>
        Options(new HumanRouteLocalAiApplication(run));

    private static MonitorHostTestOptions Options(ILocalAiAnalysisApplicationV1 application) => new()
    {
        LocalAiAnalysisApplication = application,
        AdditionalServices = services =>
        {
            services.AddSingleton<ILocalRepositoryScopeSnapshotService>(new ReadyScopeService());
            services.AddSingleton<ILocalRepositorySessionDetailSnapshotService>(new ReadyDetailService());
        },
    };

    private sealed class HumanRouteLocalAiApplication(LocalAiRunStatusV1? run) : ILocalAiAnalysisApplicationV1
    {
        public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token) => ValueTask.FromResult<LocalAiRunStatusV1?>(run ?? new(runId, "succeeded", "session", SessionId, null, null));
        public ValueTask<bool> CancelAsync(string runId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class MutableHumanRouteLocalAiApplication(LocalAiRunStatusV1? run) : ILocalAiAnalysisApplicationV1
    {
        public LocalAiRunStatusV1? Run { get; set; } = run;
        public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token) => ValueTask.FromResult(Run?.RunId == runId ? Run : null);
        public ValueTask<bool> CancelAsync(string runId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token) => throw new NotSupportedException();
    }

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
            var previousExecution = execution with { ExecutionId = "8a5590c8-46e3-7069-af48-3844d2bf17a4", Latest = false };
            var node = new LocalWorkspaceNodeDetail("node-a8a773d6614d5030f505ff195b452dd6", request.SessionId, execution.ExecutionId,
                "session_event", "event", 0, null, "exact", "event", "recorded", "user.message", "completed", "completed", "missing", null, null, null,
                activity, tokens, null, null, null);
            return ValueTask.FromResult(new LocalRepositorySessionDetailSnapshot(session,
                new LocalWorkspaceSessionDetailContribution([execution, previousExecution], [node], [], []), new string('1', 64)));
        }
    }

    private sealed record SessionRow(string SessionId) : ILocalRepositorySessionSnapshotRow;
}

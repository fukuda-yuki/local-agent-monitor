using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class LocalMonitorV1RepositoryComparePlaywrightTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000100";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000010";
    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string ExecutionId = "018f0000-0000-7000-8000-000000000020";
    private const string NodeId = "node-11111111111111111111111111111111";
    private const string RunId = "018f0000-0000-7000-8000-000000000099";
    private const string SnapshotId = "018f0000-0000-7000-8000-000000000098";

    [Theory]
    [InlineData("{\"readiness_state\":\"ready\"}")]
    [InlineData("{\"provider\":\"github_copilot\",\"selected_model\":\"model\",\"selected_configuration\":\"test\",\"readiness_state\":\"ready\",\"last_check_result\":\"ready\",\"provider_egress_notice\":\"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action\",\"extra\":true}")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRejectsMalformedReadyPayload(string readiness)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(readiness)));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true })).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("scope")]
    [InlineData("external")]
    [InlineData("duplicate")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRejectsMalformedSuccessfulResult(string mutation)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var result = JsonNode.Parse(ValidResult())!.AsObject();
        if (mutation == "extra") result["extra"] = true;
        if (mutation == "scope") result["scope"]!["comparison_id"] = "018f0000-0000-7000-8000-000000000011";
        if (mutation == "external") result["findings"]![0]!["evidence_refs"]![0] = "https://example.com/sessions/018f0000-0000-7000-8000-000000000001";
        if (mutation == "duplicate") result["improvement_suggestions"]![0]!["evidence_refs"] = new JsonArray(
            $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}", $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("succeeded", result.ToJsonString()))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var failure = page.Locator("[data-compare-ai-status]");
        await Expect(failure).ToContainTextAsync("安全に表示できません");
        await AssertVisibleFocusAndBlurCleanup(page, failure);
        Assert.Equal(0, await page.Locator("[data-compare-ai-result] a").CountAsync());
        await Expect(page.Locator(".local-monitor-compare-section")).ToHaveCountAsync(9);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("duplicate")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiCancellationRequiresExactSuccessfulReceipt(string outcome)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        var cancelRequests = 0;
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}/cancel", route =>
        {
            cancelRequests++;
            return outcome switch
            {
                "accepted" => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\",\"state\":\"canceled\"}}")),
                "duplicate" => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\",\"run_id\":\"{RunId}\",\"state\":\"canceled\"}}")),
                _ => route.FulfillAsync(new() { Status = 409, ContentType = "application/json", Body = "{\"error\":\"run_not_cancelable\"}" }),
            };
        });
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("running", "null"))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var cancel = page.GetByRole(AriaRole.Button, new() { Name = "キャンセル", Exact = true });
        await Expect(cancel).ToBeVisibleAsync();
        await cancel.EvaluateAsync("element => { element.click(); element.click(); }");
        var accepted = outcome == "accepted";
        await Expect(page.Locator("[data-compare-ai-status]")).ToContainTextAsync(accepted ? "キャンセルしました" : "キャンセルできませんでした");
        Assert.Equal(1, cancelRequests);
        if (accepted) await Expect(cancel).ToBeHiddenAsync();
        else await Expect(cancel).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("nested_duplicate")]
    [InlineData("oversized")]
    [InlineData("depth")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRejectsInvalidRunWireBeforeRendering(string mutation)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var wire = Run("succeeded", ValidResult());
        if (mutation == "nested_duplicate") wire = wire.Replace("\"kind\":\"comparison\"", "\"kind\":\"comparison\",\"kind\":\"comparison\"", StringComparison.Ordinal);
        if (mutation == "oversized") wire += new string(' ', 1_052_673);
        if (mutation == "depth")
        {
            wire = wire.Replace("\"result\":", "\"result\":" + new string('[', 17), StringComparison.Ordinal);
            wire = wire[..^1] + new string(']', 17) + "}";
        }
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(wire)));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-compare-ai-status]")).ToContainTextAsync("再試行しています");
        Assert.Equal(0, await page.Locator("[data-compare-ai-result] > *").CountAsync());
    }

    [Theory]
    [InlineData("zero_findings", true, true)]
    [InlineData("zero_findings", false, false)]
    [InlineData("succeeded", true, false)]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRequiresStateFindingCardinality(string runState, bool emptyFindings, bool accepted)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var result = JsonNode.Parse(ValidResult())!.AsObject();
        if (emptyFindings) result["findings"] = new JsonArray();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run(runState, result.ToJsonString()))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var status = page.Locator("[data-compare-ai-status]");
        await Expect(status).ToContainTextAsync(accepted ? "指摘はありません" : "安全に表示できません");
        if (!accepted) await AssertVisibleFocusAndBlurCleanup(page, status);
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRouteClearInvalidatesStartedSurfaceAndForwardRestoresOnce()
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var runReads = 0;
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => { runReads++; return route.FulfillAsync(Json(Run("succeeded", ValidResult()))); });
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-compare-ai-result]")).ToContainTextAsync("AIによる解釈");
        await page.EvaluateAsync("document.dispatchEvent(new CustomEvent('cao-route-state', { detail: {} }))");
        Assert.Equal(0, await page.Locator("[data-compare-ai-result] > *").CountAsync());
        await page.EvaluateAsync($"document.dispatchEvent(new CustomEvent('cao-route-state', {{ detail: {{ analysis: '{RunId}' }} }})); document.dispatchEvent(new CustomEvent('cao-route-state', {{ detail: {{ analysis: '{RunId}' }} }}));");
        await Expect(page.Locator("[data-compare-ai-result]")).ToContainTextAsync("AIによる解釈");
        Assert.Equal(2, runReads);
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRouteClearRemovesFocusedFailureLifecycleAndReturnsToInitiator()
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("provider_failed", "null", "provider_failed"))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        var initiator = page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true });
        await initiator.ClickAsync();
        var failure = page.Locator("[data-compare-ai-status]");
        await Expect(failure).ToBeFocusedAsync();

        await page.EvaluateAsync("document.dispatchEvent(new CustomEvent('cao-route-state', { detail: {} }))");

        await Expect(failure).ToHaveTextAsync("");
        await Expect(failure).Not.ToHaveAttributeAsync("data-terminal-failure", "true");
        Assert.Equal("", await failure.EvaluateAsync<string>("element => element.style.outline"));
        await Expect(initiator).ToBeFocusedAsync();
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("zero_findings")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiSuccessfulRestoreReplacesFocusedFailureWithResultFocus(string restoredState)
    {
        const string restoredRunId = "018f0000-0000-7000-8000-000000000097";
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var restoredResult = JsonNode.Parse(ValidResult())!.AsObject();
        if (restoredState == "zero_findings") restoredResult["findings"] = new JsonArray();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("provider_failed", "null", "provider_failed"))));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{restoredRunId}", route => route.FulfillAsync(Json(Run(restoredState, restoredResult.ToJsonString()).Replace(RunId, restoredRunId, StringComparison.Ordinal))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var failure = page.Locator("[data-compare-ai-status]");
        await Expect(failure).ToBeFocusedAsync();

        await page.EvaluateAsync($"document.dispatchEvent(new CustomEvent('cao-route-state', {{ detail: {{ analysis: '{restoredRunId}' }} }}))");

        await Expect(failure).Not.ToHaveAttributeAsync("data-terminal-failure", "true");
        Assert.Equal("", await failure.EvaluateAsync<string>("element => element.style.outline"));
        var resultHeading = page.GetByRole(AriaRole.Heading, new() { Name = "AIによる解釈", Exact = true });
        await Expect(resultHeading).ToBeFocusedAsync();
    }

    [Theory]
    [InlineData("near_limit", true)]
    [InlineData("over_result", false)]
    [InlineData("over_envelope", false)]
    [InlineData("late_result_text", false)]
    [InlineData("escape_heavy", false)]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiUsesSeparateRunEnvelopeAndResultWireLimits(string sizeCase, bool accepted)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var result = sizeCase switch
        {
            "over_result" => SizedValidResult(1_048_577),
            "late_result_text" => SizedValidResult(1_048_577, "\"result\":"),
            "escape_heavy" => EscapeHeavyValidResult(1_048_577),
            _ => SizedValidResult(1_048_576),
        };
        var run = Run("succeeded", result);
        if (sizeCase == "over_envelope") run += new string(' ', 4_097);
        Assert.True(Encoding.UTF8.GetByteCount(run) > 1_048_576);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(run)));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var status = page.Locator("[data-compare-ai-status]");
        await Expect(status).ToContainTextAsync(accepted ? "完了" : sizeCase == "over_envelope" ? "再試行" : "安全に表示できません");
        if (!accepted && sizeCase != "over_envelope") await AssertVisibleFocusAndBlurCleanup(page, status);
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRouteClearMakesDelayedPollFailureANoOp()
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", async route => { requested.TrySetResult(); await release.Task; await route.FulfillAsync(Json("{")); });
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.EvaluateAsync("document.dispatchEvent(new CustomEvent('cao-route-state', { detail: {} }))");
        release.TrySetResult();
        await page.WaitForTimeoutAsync(400);
        await Expect(page.Locator("[data-compare-ai-status]")).ToHaveTextAsync("");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "キャンセル", Exact = true })).ToBeHiddenAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiRouteClearMakesPendingCancelCompletionANoOp(bool success)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}/cancel", async route => { requested.TrySetResult(); await release.Task; if (success) await route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\",\"state\":\"canceled\"}}")); else await route.FulfillAsync(new() { Status = 500, Body = "{}" }); });
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("running", "null"))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var cancel = page.GetByRole(AriaRole.Button, new() { Name = "キャンセル", Exact = true });
        await Expect(cancel).ToBeVisibleAsync();
        await cancel.EvaluateAsync("element => element.click()");
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.EvaluateAsync("document.dispatchEvent(new CustomEvent('cao-route-state', { detail: {} }))");
        release.TrySetResult();
        await page.WaitForTimeoutAsync(400);
        await Expect(page.Locator("[data-compare-ai-status]")).ToHaveTextAsync("");
        await Expect(cancel).ToBeHiddenAsync();
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiIsHiddenUntilReadyThenStartsFromExactReceiptAndRendersSafeResult()
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        string? startBody = null;
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route =>
        {
            startBody = route.Request.PostData;
            return route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}"));
        });
        var completedRun = Run("succeeded", ValidResult());
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(completedRun)));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route =>
            route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        var action = page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true });
        await Expect(action).ToBeVisibleAsync();
        await action.ClickAsync();
        Assert.Equal($$"""{"schema_version":"local-ai-comparison-run.request.v1","repository_id":"{{RepositoryId}}","comparison_id":"{{ComparisonId}}","timeout_seconds":60}""", startBody);
        var result = page.Locator("[data-compare-ai-result]");
        await Expect(result).ToContainTextAsync("AIによる解釈");
        await Expect(result).ToContainTextAsync("分析対象の技術情報");
        await Expect(result).ToContainTextAsync("記録時点の技術情報");
        await Expect(result).ToContainTextAsync("分析の技術情報");
        await Expect(result).ToContainTextAsync("比較ID:");
        await Expect(result).ToContainTextAsync("種類: セッション比較");
        await Expect(result).ToContainTextAsync("内容のSHA-256:");
        await Expect(result).ToContainTextAsync("期待される効果（AIによる提案）:");
        await Expect(result).ToContainTextAsync("<img src=x onerror=alert(1)>");
        Assert.Equal(0, await result.Locator("img").CountAsync());
        var evidence = result.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "正確な根拠", Exact = true }) });
        await Expect(evidence.Locator("a")).ToHaveCountAsync(1);
        await Expect(evidence.Locator("a").First).ToHaveAttributeAsync("href", $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}");
        Assert.DoesNotContain("<img", page.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("repository")]
    [InlineData("comparison")]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiFailureAndWrongOwnershipLeaveDeterministicComparisonUsable(string mismatch)
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        var scope = mismatch == "scope" ? "repository" : "comparison";
        var repository = mismatch == "repository" ? "018f0000-0000-7000-8000-000000000101" : RepositoryId;
        var comparison = mismatch == "comparison" ? "018f0000-0000-7000-8000-000000000011" : ComparisonId;
        var wrongOwner = $"{{\"run_id\":\"{RunId}\",\"state\":\"succeeded\",\"scope_kind\":\"{scope}\",\"session_id\":null,\"node_id\":null,\"repository_id\":\"{repository}\",\"comparison_id\":\"{comparison}\",\"error\":null,\"result\":null}}";
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(wrongOwner)));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var failure = page.Locator("[data-compare-ai-status]");
        await Expect(failure).ToContainTextAsync("表示できません");
        await AssertVisibleFocusAndBlurCleanup(page, failure);
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        await Expect(page.Locator(".local-monitor-compare-section")).ToHaveCountAsync(9);
    }

    private static async Task AssertVisibleFocusAndBlurCleanup(IPage page, ILocator failure)
    {
        await Expect(failure).ToBeFocusedAsync();
        var focusStyle = await failure.EvaluateAsync<string[]>("""
            element => {
              const style = getComputedStyle(element);
              return [style.outlineStyle, style.outlineWidth, style.outlineColor];
            }
            """);
        Assert.NotEqual("none", focusStyle[0]);
        Assert.True(double.Parse(focusStyle[1].Replace("px", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture) >= 2);
        Assert.NotEqual("rgba(0, 0, 0, 0)", focusStyle[2]);
        var settings = page.GetByRole(AriaRole.Button, new() { Name = "設定", Exact = true });
        await settings.FocusAsync();
        await Expect(settings).ToBeFocusedAsync();
        Assert.Equal("none", await failure.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareAiProviderFailureIsVisibleFocusedAndCapturedAtTheHardViewport()
    {
        var readBody = await Golden("local-monitor-comparison-read.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1366, Height = 768 } });
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route => route.FulfillAsync(Json(Readiness())));
        await page.RouteAsync("**/api/local-monitor/v1/ai/comparison-runs", route => route.FulfillAsync(Json($"{{\"run_id\":\"{RunId}\"}}")));
        await page.RouteAsync($"**/api/local-monitor/v1/ai/runs/{RunId}", route => route.FulfillAsync(Json(Run("provider_failed", "null", "provider_failed"))));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}", route => route.FulfillAsync(Json(readBody)));

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true }).ClickAsync();
        var failure = page.Locator("[data-compare-ai-status]");
        await Expect(failure).ToHaveTextAsync("AIで解釈できませんでした。");
        await Expect(failure).ToBeFocusedAsync();
        var focusStyle = await failure.EvaluateAsync<string[]>("""
            element => {
              const style = getComputedStyle(element);
              return [style.outlineStyle, style.outlineWidth, style.outlineColor];
            }
            """);
        Assert.NotEqual("none", focusStyle[0]);
        Assert.True(double.Parse(focusStyle[1].Replace("px", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture) >= 2);
        Assert.NotEqual("rgba(0, 0, 0, 0)", focusStyle[2]);
        var settings = page.GetByRole(AriaRole.Button, new() { Name = "設定", Exact = true });
        await settings.FocusAsync();
        await Expect(settings).ToBeFocusedAsync();
        Assert.Equal("none", await failure.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.True(await page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('.local-monitor-compare-result-heading th')].every(cell => {
              const boundary = cell.getBoundingClientRect().right;
              return [...cell.querySelectorAll('.local-monitor-compare-evidence-actions button')]
                .every(button => button.getBoundingClientRect().right <= boundary + 1);
            })
            """));

        await failure.FocusAsync();
        await Expect(failure).ToBeFocusedAsync();
        var screenshotPath = CompareArtifactPath("compare-ai-provider-failed-1366x768.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
        var png = await File.ReadAllBytesAsync(screenshotPath);
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(1366, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(768, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task CompareDistinguishesClosedUnavailableStateSetFromMissingAndMalformedValues()
    {
        var read = JsonNode.Parse(await Golden("local-monitor-comparison-read.response.json"))!.AsObject();
        read["results"]![0]!["values"] = new JsonArray(
            new JsonObject { ["key"] = "a_total_unavailable_states", ["value"] = "none" },
            new JsonObject { ["key"] = "b_total_unavailable_states", ["value"] = "not_observed=2" },
            new JsonObject { ["key"] = "absolute_difference", ["value"] = "not_available" },
            new JsonObject { ["key"] = "total_unavailable_states", ["value"] = "not_observed=0" });

        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(read.ToJsonString()));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        var cells = page.Locator(".local-monitor-compare-table").First.Locator("tbody > tr").First.Locator(":scope > th, :scope > td");
        var closedZero = cells.Nth(1).Locator(".local-monitor-compare-fact > span:nth-child(2)");
        var unavailableCount = cells.Nth(2).Locator(".local-monitor-compare-fact > span:nth-child(2)");
        var missingDifference = cells.Nth(3).Locator(".local-monitor-compare-fact > span:nth-child(2)");
        var malformed = cells.Nth(0).Locator(".local-monitor-compare-fact > span:nth-child(2)");

        await Expect(closedZero).ToHaveAttributeAsync("data-fact-state", "observed-zero");
        await Expect(closedZero.Locator(".fact-state-primary")).ToHaveTextAsync("0件");
        await Expect(closedZero.Locator("p")).ToHaveTextAsync("取得元: 保存済み比較。保存時点で明示的に 0 です。");
        await Expect(unavailableCount.Locator("[data-fact-state]")).ToHaveAttributeAsync("data-fact-state", "not-observed");
        await Expect(unavailableCount).ToContainTextAsync("今回の記録にはありません");
        await Expect(unavailableCount).ToContainTextAsync("（2件）");
        await Expect(missingDifference).ToHaveAttributeAsync("data-fact-state", "not-observed");
        await Expect(missingDifference).Not.ToContainTextAsync("0件");
        await Expect(malformed).ToHaveAttributeAsync("data-fact-state", "projection-invalid");
        await Expect(malformed).Not.ToContainTextAsync("not_observed");
    }

    [Fact]
    [Trait("ValidationLane", "CriticalSmoke")]
    public async Task ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute()
    {
        var read = JsonNode.Parse(await Golden("local-monitor-comparison-read.response.json"))!.AsObject();
        var values = read["results"]![0]!["values"]!.AsArray();
        values.Add(new JsonObject { ["key"] = "zero", ["value"] = "0" });
        values.Add(new JsonObject { ["key"] = "a_unavailable_states", ["value"] = "not_observed=1" });
        var readBody = read.ToJsonString();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1366, Height = 768 } });
        var rowQueries = new List<string>();
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", route =>
            route.FulfillAsync(Json("{\"readiness_state\":\"unconfigured\"}")));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}**", route =>
        {
            if (route.Request.Url.Contains("/rows?", StringComparison.Ordinal))
            {
                rowQueries.Add(new Uri(route.Request.Url).Query);
                var rows = JsonNode.Parse(Golden("local-monitor-comparison-rows.response.json").GetAwaiter().GetResult())!.AsObject();
                rows["items"]![0]!["display_name"] = "表示ツール";
                rows["items"]![0]!["values"]![0]!["value"] = "stored-display-name";
                rows["items"]![0]!["values"]![1]!["value"] = "hidden-sort-key";
                rows["next_cursor"] = rowQueries.Count <= 2 ? "cursor-one" : null;
                return route.FulfillAsync(Json(rows.ToJsonString()));
            }
            if (route.Request.Url.Contains("/evidence?", StringComparison.Ordinal))
            {
                var evidence = JsonNode.Parse(Golden("local-monitor-comparison-evidence.response.json").GetAwaiter().GetResult())!.AsObject();
                evidence["result_ordinal"] = 1;
                evidence["field_key"] = "median";
                evidence["items"]![0]!["consumed_value"] = "10";
                return route.FulfillAsync(Json(evidence.ToJsonString()));
            }
            return route.FulfillAsync(Json(readBody));
        });

        var url = host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}";
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        Assert.Equal(new[] { "対象", "トークン", "入力トークンの内訳", "時間・実行量", "スキル", "ツール", "サブエージェント", "エラー・再試行", "比較条件" },
            await page.Locator(".local-monitor-compare-section > h2").AllTextContentsAsync());
        await Expect(page.Locator("[data-compare-cohort-count='a']")).ToHaveTextAsync("1件");
        var scalarTable = page.Locator(".local-monitor-compare-table").First;
        Assert.Equal(["指標", "基準", "比較対象", "差"], await scalarTable.Locator("thead th").AllTextContentsAsync());
        await Expect(scalarTable.Locator("tbody > tr")).ToHaveCountAsync(1);
        var scalarCells = scalarTable.Locator("tbody > tr").First.Locator(":scope > th, :scope > td");
        await Expect(scalarCells).ToHaveCountAsync(4);
        await Expect(scalarCells.Nth(0)).ToContainTextAsync("トークン合計");
        Assert.Equal(["セッション数", "利用可能件数", "中央値", "最小値", "最大値", "合計", "利用できない状態"],
            await scalarCells.Nth(1).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["1", "1", "10", "10", "10", "10"],
            await scalarCells.Nth(1).Locator(".local-monitor-compare-fact:nth-child(-n+6) > span:nth-child(2)").AllTextContentsAsync());
        Assert.Equal(["セッション数", "利用可能件数", "中央値", "最小値", "最大値", "合計"],
            await scalarCells.Nth(2).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["1", "1", "12", "12", "12", "12"],
            await scalarCells.Nth(2).Locator(".local-monitor-compare-fact > span:nth-child(2)").AllTextContentsAsync());
        Assert.Equal(["絶対差", "相対差"], await scalarCells.Nth(3).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["2", "20.0"], await scalarCells.Nth(3).Locator(".local-monitor-compare-fact > span:nth-child(2)").AllTextContentsAsync());
        await Expect(page.Locator(".local-monitor-compare-table").First).ToContainTextAsync("0");
        await Expect(page.Locator(".local-monitor-compare-table").First).ToContainTextAsync("今回の記録にはありません");
        var compareBody = page.Locator(".local-monitor-repository-compare-body");
        await Expect(compareBody).Not.ToContainTextAsync("not_observed");
        await Expect(compareBody).Not.ToContainTextAsync("total_tokens");
        await Expect(compareBody).Not.ToContainTextAsync("a_session_count");
        await Expect(compareBody).Not.ToContainTextAsync("おすすめ");
        await Expect(compareBody).Not.ToContainTextAsync("AI");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "AIで解釈", Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "設定", Exact = true })).ToBeVisibleAsync();

        var toolSection = page.Locator(".local-monitor-compare-section").Filter(new LocatorFilterOptions { Has = page.Locator("h2", new PageLocatorOptions { HasText = "ツール" }) });
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "ツールを読み込む" }).ClickAsync();
        await Expect(toolSection).ToContainTextAsync("表示ツール");
        await Expect(toolSection).Not.ToContainTextAsync("stored-display-name");
        await Expect(toolSection).Not.ToContainTextAsync("hidden-sort-key");
        var toolRow = toolSection.Locator("tbody > tr").First;
        await Expect(toolRow.Locator(":scope > th, :scope > td")).ToHaveCountAsync(4);
        var failureRelativeDifference = toolRow.Locator("td").Nth(2).Locator(".local-monitor-compare-fact").Filter(new() { HasText = "失敗回数・相対差" });
        await Expect(failureRelativeDifference).ToContainTextAsync("今回の記録にはありません");
        await Expect(failureRelativeDifference).Not.ToContainTextAsync("失敗回数・相対差0");
        await toolSection.GetByPlaceholder("ツールを検索").FillAsync("synthetic");
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "検索" }).ClickAsync();
        Assert.Contains(rowQueries, query => query.Contains("family=tool&q=synthetic", StringComparison.Ordinal));
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "次のページ" }).ClickAsync();
        Assert.Contains(rowQueries, query => query.Contains("after=cursor-one", StringComparison.Ordinal));

        var evidenceButton = page.GetByRole(AriaRole.Button, new() { Name = "中央値の根拠を表示", Exact = true });
        await evidenceButton.ClickAsync();
        await Expect(page.Locator("#repository-compare-evidence-dialog")).ToBeVisibleAsync();
        await Expect(page.Locator("#repository-compare-evidence-close")).ToBeFocusedAsync();
        await Expect(page.Locator("[data-compare-evidence-items]")).ToContainTextAsync("10");
        await Expect(page.Locator("[data-compare-evidence-items] a")).ToHaveAttributeAsync("href", $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}");
        await page.Keyboard.PressAsync("Escape");
        await Expect(evidenceButton).ToBeFocusedAsync();

        var layout = await page.EvaluateAsync<double[]>("""
            () => {
              const header = document.querySelector('.local-monitor-repository-compare-header').getBoundingClientRect();
              const body = document.querySelector('.local-monitor-repository-compare-body');
              const sticky = getComputedStyle(document.querySelector('.local-monitor-compare-table thead th')).position;
              return [header.height, document.documentElement.scrollWidth, innerWidth, body.scrollHeight, body.clientHeight, sticky === 'sticky' ? 1 : 0];
            }
            """);
        Assert.True(layout[0] <= 112);
        Assert.True(layout[1] <= layout[2]);
        Assert.True(layout[3] >= layout[4]);
        Assert.Equal(1, layout[5]);

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        Assert.Equal(url, page.Url);

        var privacy = await page.EvaluateAsync<string[]>("""
            async () => [
              localStorage.length.toString(),
              sessionStorage.length.toString(),
              JSON.stringify(history.state),
              JSON.stringify(await caches.keys()),
            ]
            """);
        Assert.Equal("0", privacy[0]);
        Assert.Equal("0", privacy[1]);
        Assert.DoesNotContain(ComparisonId, privacy[2], StringComparison.Ordinal);
        Assert.DoesNotContain("results", privacy[2], StringComparison.Ordinal);
        Assert.Equal("[]", privacy[3]);

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        await page.GoForwardAsync(new PageGoForwardOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-explorer]")).ToBeVisibleAsync();
        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task ComparePresentsClosedRegistryAndStructuralValueKeysWithoutExposingMachineKeys()
    {
        var read = JsonNode.Parse(await Golden("local-monitor-comparison-read.response.json"))!.AsObject();
        var rowLabels = new (string Key, string Label)[]
        {
            ("included_session_count", "対象セッション数"), ("excluded_session_count", "除外セッション数"),
            ("available_session_count", "利用可能なセッション数"), ("period", "期間"), ("archived_inclusion", "アーカイブ済みの対象"),
            ("input_tokens", "入力トークン"), ("output_tokens", "出力トークン"), ("total_tokens", "トークン合計"),
            ("cache_read_tokens", "キャッシュから読み込み"), ("new_input_tokens", "新規入力"),
            ("cache_creation_tokens", "キャッシュ書き込み"), ("cache_read_ratio", "キャッシュ読み込み比率"),
            ("session_duration", "セッションの所要時間"), ("execution_count", "実行数"), ("model_turn_count", "モデル応答数"),
            ("tool_call_count", "ツール呼び出し数"), ("skill_invocation_count", "スキル呼び出し数"),
            ("subagent_start_count", "サブエージェント開始数"), ("error_count", "エラー件数"), ("retry_count", "再試行件数"),
            ("subagent_aggregate_start_count", "サブエージェント開始数"), ("subagent_aggregate_completed_count", "サブエージェント完了数"),
            ("subagent_aggregate_failed_count", "サブエージェント失敗数"), ("subagent_aggregate_recorded_tokens", "サブエージェントのトークン合計"),
            ("error_session_count", "エラーのあるセッション数"), ("retry_session_count", "再試行のあるセッション数"),
            ("recovery_relation_count", "復旧関係数"), ("sources", "取得元"), ("models", "モデル"),
            ("source_versions", "取得元のバージョン"), ("adapter_versions", "アダプターのバージョン"),
            ("completeness", "記録の完全性"), ("metric_availability", "指標の利用可能件数"),
        };
        var results = new JsonArray();
        var ordinal = 1;
        foreach (var (key, _) in rowLabels)
        {
            results.Add(new JsonObject
            {
                ["result_ordinal"] = ordinal++, ["section_key"] = "conditions", ["row_kind"] = "condition", ["row_key"] = key,
                ["values"] = new JsonArray(new JsonObject { ["key"] = "count", ["value"] = "1" }),
            });
        }
        results.Add(new JsonObject
        {
            ["result_ordinal"] = ordinal, ["section_key"] = "conditions", ["row_kind"] = "condition", ["row_key"] = "unexpected_row_key",
            ["values"] = new JsonArray(
                new JsonObject { ["key"] = "a_invocation_count", ["value"] = "1" }, new JsonObject { ["key"] = "b_call_count", ["value"] = "2" },
                new JsonObject { ["key"] = "a_failure_count", ["value"] = "3" }, new JsonObject { ["key"] = "b_start_count", ["value"] = "4" },
                new JsonObject { ["key"] = "a_completed_count", ["value"] = "5" }, new JsonObject { ["key"] = "b_failed_count", ["value"] = "6" },
                new JsonObject { ["key"] = "a_recorded_tokens", ["value"] = "7" }, new JsonObject { ["key"] = "a_invoked_session_count", ["value"] = "8" },
                new JsonObject { ["key"] = "b_called_session_count", ["value"] = "9" }, new JsonObject { ["key"] = "a_started_session_count", ["value"] = "10" },
                new JsonObject { ["key"] = "a_available_session_count", ["value"] = "11" }, new JsonObject { ["key"] = "start", ["value"] = "2026-08-01" },
                new JsonObject { ["key"] = "end", ["value"] = "2026-08-31" }, new JsonObject { ["key"] = "relative_difference_percent", ["value"] = "11" },
                new JsonObject { ["key"] = "distribution", ["value"] = "true" }, new JsonObject { ["key"] = "a_call_count_session_count", ["value"] = "1" },
                new JsonObject { ["key"] = "a_failure_count_available_count", ["value"] = "2" }, new JsonObject { ["key"] = "call_count_absolute_difference", ["value"] = "3" },
                new JsonObject { ["key"] = "b_recorded_tokens_unavailable_states", ["value"] = "none" },
                new JsonObject { ["key"] = "a_direct_session_archived_count", ["value"] = "12" }, new JsonObject { ["key"] = "b_assigned_repository_archived_count", ["value"] = "13" },
                new JsonObject { ["key"] = "a_includes_archived", ["value"] = "true" }, new JsonObject { ["key"] = "b_includes_archived", ["value"] = "false" },
                new JsonObject { ["key"] = "display_name", ["value"] = "true" }, new JsonObject { ["key"] = "sort_key", ["value"] = "false" },
                new JsonObject { ["key"] = "a_s2_input_tokens", ["value"] = "14" }, new JsonObject { ["key"] = "b_s8_retry_count", ["value"] = "15" },
                new JsonObject { ["key"] = "unexpected_value_key", ["value"] = "kept-value" }),
        });
        read["results"] = results;

        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(read.ToJsonString()));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");

        var body = page.Locator(".local-monitor-repository-compare-body");
        foreach (var (_, label) in rowLabels) await Expect(body).ToContainTextAsync(label);
        var structuralRow = page.Locator(".local-monitor-compare-result-heading").Last;
        var structuralCells = structuralRow.Locator(":scope > th, :scope > td");
        await Expect(structuralCells).ToHaveCountAsync(4);
        Assert.Equal(["開始", "終了", "内訳", "記録項目"],
            await structuralCells.Nth(0).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["呼び出し回数", "失敗回数", "完了回数", "トークン合計", "呼び出しあり", "開始あり", "利用可能件数",
            "呼び出し回数・セッション数", "失敗回数・利用可能件数", "アーカイブ済みセッション数", "アーカイブ済みを含む", "入力トークン"],
            await structuralCells.Nth(1).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["呼び出し回数", "開始回数", "失敗回数", "呼び出しあり", "トークン合計・利用できない状態",
            "アーカイブ済みリポジトリのセッション数", "アーカイブ済みを含む", "再試行件数"],
            await structuralCells.Nth(2).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        Assert.Equal(["相対差", "呼び出し回数・絶対差"],
            await structuralCells.Nth(3).Locator(".local-monitor-compare-fact > span:first-child").AllTextContentsAsync());
        await Expect(body).ToContainTextAsync("比較項目");
        await Expect(structuralCells.Nth(0)).ToContainTextAsync("kept-value");
        await Expect(structuralCells.Nth(1)).ToContainTextAsync("はい");
        await Expect(structuralCells.Nth(2)).ToContainTextAsync("いいえ");
        await Expect(structuralCells.Nth(3)).ToContainTextAsync("3");
        await Expect(structuralRow).Not.ToContainTextAsync("truefalse");
        await Expect(body).Not.ToContainTextAsync("unexpected_row_key");
        await Expect(body).Not.ToContainTextAsync("unexpected_value_key");
        await Expect(body).Not.ToContainTextAsync("a_s2_input_tokens");
        await Expect(body).Not.ToContainTextAsync("b_s8_retry_count");
    }

    [Fact]
    [Trait("ValidationLane", "Nightly")]
    public async Task EvidenceActionsUseAcceptedResultFieldsAndPagingKeepsRowsInsideTheirTables()
    {
        var read = JsonNode.Parse(await Golden("local-monitor-comparison-read.response.json"))!.AsObject();
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 2, ["section_key"] = "target", ["row_kind"] = "scalar", ["row_key"] = "included_session_count",
            ["values"] = new JsonArray(new JsonObject { ["key"] = "a_session_count", ["value"] = "1" }),
        });
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 3, ["section_key"] = "target", ["row_kind"] = "scalar", ["row_key"] = "archived_inclusion",
            ["values"] = new JsonArray(
                new JsonObject { ["key"] = "a_included_count", ["value"] = "0" },
                new JsonObject { ["key"] = "a_includes_archived", ["value"] = "false" },
                new JsonObject { ["key"] = "b_included_count", ["value"] = "1" },
                new JsonObject { ["key"] = "b_includes_archived", ["value"] = "true" },
                new JsonObject { ["key"] = "absolute_difference", ["value"] = "1" }),
        });
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 4, ["section_key"] = "target", ["row_kind"] = "scalar", ["row_key"] = "unexpected_condition",
            ["values"] = new JsonArray(new JsonObject { ["key"] = "a_includes_archived", ["value"] = "false" }),
        });
        var conditionRows = new[] { "sources", "models", "source_versions", "adapter_versions", "completeness" };
        foreach (var (rowKey, index) in conditionRows.Select((rowKey, index) => (rowKey, index)))
        {
            read["results"]!.AsArray().Add(new JsonObject
            {
                ["result_ordinal"] = 5 + index, ["section_key"] = "conditions", ["row_kind"] = "scalar", ["row_key"] = rowKey,
                ["values"] = new JsonArray(new JsonObject { ["key"] = "distribution", ["value"] = "synthetic" }),
            });
        }
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 10, ["section_key"] = "conditions", ["row_kind"] = "scalar", ["row_key"] = "metric_availability",
            ["values"] = new JsonArray(new JsonObject { ["key"] = "a_s2_input_tokens", ["value"] = "1" }),
        });
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 11, ["section_key"] = "target", ["row_kind"] = "scalar", ["row_key"] = "period",
            ["values"] = new JsonArray(new JsonObject { ["key"] = "start", ["value"] = "2026-08-01" }),
        });
        var readBody = read.ToJsonString();
        var evidenceGolden = await Golden("local-monitor-comparison-evidence.response.json");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(readBody));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var evidenceQueries = new List<string>();
        var rowQueries = new List<string>();
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}**", route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.AbsolutePath.EndsWith("/rows", StringComparison.Ordinal))
            {
                rowQueries.Add(uri.Query);
                var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                var family = parameters["family"].ToString();
                var after = parameters.TryGetValue("after", out var afterValue) ? afterValue.ToString() : string.Empty;
                var ordinal = family switch { "skill" => 21, "subagent" => 22, _ when after.Length > 0 => 24, _ => 20 };
                var display = ordinal == 24 ? "Second tool" : family switch { "skill" => "Synthetic skill", "subagent" => "Synthetic sub-agent", _ => "Synthetic tool" };
                var rows = new JsonObject
                {
                    ["schema_version"] = "local-monitor-comparison-rows.response.v1",
                    ["comparison_id"] = ComparisonId,
                    ["family"] = family,
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["result_ordinal"] = ordinal,
                        ["row_key"] = $"{family}.{ordinal}",
                        ["display_name"] = display,
                        ["values"] = new JsonArray(
                            new JsonObject { ["key"] = "display_name", ["value"] = display },
                            new JsonObject { ["key"] = "sort_key", ["value"] = $"{family}.{ordinal}" }),
                    }),
                    ["next_cursor"] = family == "tool" && after.Length == 0 ? "rows-cursor" : null,
                };
                return route.FulfillAsync(Json(rows.ToJsonString()));
            }
            if (uri.AbsolutePath.EndsWith("/evidence", StringComparison.Ordinal))
            {
                evidenceQueries.Add(uri.Query);
                var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                var ordinal = int.Parse(parameters["result_ordinal"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
                var field = parameters["field_key"].ToString();
                var accepted = ordinal switch
                {
                    1 => field is "median" or "total_tokens",
                    2 => field == "count",
                    3 => field == "condition",
                    >= 5 and <= 9 => field == "condition",
                    11 => field == "condition",
                    20 => field is "count" or "error_count" or "retry_count",
                    21 => field == "count",
                    22 => field is "count" or "total_tokens",
                    _ => false,
                };
                if (!accepted)
                {
                    return route.FulfillAsync(new() { Status = 404, ContentType = "application/json", Body = "{\"error\":\"comparison_not_found\"}" });
                }
                var evidence = JsonNode.Parse(evidenceGolden)!.AsObject();
                evidence["result_ordinal"] = ordinal;
                evidence["field_key"] = field;
                var secondPage = parameters.ContainsKey("after");
                evidence["items"]![0]!["evidence_ordinal"] = secondPage ? 2 : 1;
                evidence["items"]![0]!["session_id"] = secondPage ? "018f0000-0000-7000-8000-000000000002" : SessionId;
                evidence["items"]![0]!["consumed_value"] = secondPage ? "second" : "first";
                evidence["items"]![0]!["execution_id"] = null;
                evidence["items"]![0]!["node_id"] = NodeId;
                evidence["items"]![0]!["session_location"] = $"/sessions/{(secondPage ? "018f0000-0000-7000-8000-000000000002" : SessionId)}?node={NodeId}";
                evidence["next_cursor"] = ordinal == 2 && !secondPage ? "evidence-cursor" : null;
                return route.FulfillAsync(Json(evidence.ToJsonString()));
            }
            return route.FulfillAsync(Json(readBody));
        });

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("保存済み");
        var tokenSection = page.Locator(".local-monitor-compare-section").Filter(new() { Has = page.Locator("h2", new() { HasText = "トークン" }) });
        await Expect(tokenSection.GetByRole(AriaRole.Button, new() { Name = "中央値の根拠を表示", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "件数の根拠を表示", Exact = true })).ToBeVisibleAsync();
        var targetSection = page.Locator(".local-monitor-compare-section").First;
        var archivedRows = targetSection.Locator(".local-monitor-compare-result-heading").Filter(new() { Has = page.Locator("th", new() { HasText = "アーカイブ済みの対象" }) });
        await Expect(archivedRows).ToHaveCountAsync(1);
        var validArchivedRow = archivedRows.First;
        var invalidArchivedRow = targetSection.Locator(".local-monitor-compare-result-heading").Filter(new() { Has = page.Locator("th", new() { HasText = "比較項目" }) });
        await Expect(validArchivedRow.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true })).ToBeVisibleAsync();
        var periodRow = targetSection.Locator(".local-monitor-compare-result-heading").Filter(new() { Has = page.Locator("th", new() { HasText = "期間" }) });
        await Expect(periodRow.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await invalidArchivedRow.GetByRole(AriaRole.Button).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "a_session_countの根拠を表示", Exact = true }).CountAsync());

        await tokenSection.GetByRole(AriaRole.Button, new() { Name = "中央値の根拠を表示", Exact = true }).ClickAsync();
        await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
        await page.Keyboard.PressAsync("Escape");
        await validArchivedRow.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true }).ClickAsync();
        await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
        await page.Keyboard.PressAsync("Escape");
        Assert.DoesNotContain(evidenceQueries, query => query.Contains("result_ordinal=4", StringComparison.Ordinal));
        var conditions = page.Locator(".local-monitor-compare-section").Filter(new() { Has = page.Locator("h2", new() { HasText = "比較条件" }) });
        var conditionEvidence = conditions.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true });
        await Expect(conditionEvidence).ToHaveCountAsync(conditionRows.Length);
        var metricAvailability = conditions.Locator(".local-monitor-compare-result-heading").Filter(new() { Has = page.Locator("th", new() { HasText = "指標の利用可能件数" }) });
        Assert.Equal(0, await metricAvailability.GetByRole(AriaRole.Button).CountAsync());
        for (var index = 0; index < conditionRows.Length; index++)
        {
            await conditionEvidence.Nth(index).ClickAsync();
            await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
            await page.Keyboard.PressAsync("Escape");
        }
        Assert.DoesNotContain(evidenceQueries, query => query.Contains("result_ordinal=10", StringComparison.Ordinal));

        var targetEvidence = page.Locator(".local-monitor-compare-section").First.GetByRole(AriaRole.Button, new() { Name = "件数の根拠を表示", Exact = true });
        await targetEvidence.ClickAsync();
        await Expect(page.Locator("[data-compare-evidence-items] li")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-compare-evidence-items] a")).ToHaveAttributeAsync("href", $"/sessions/{SessionId}?node={NodeId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "さらに表示", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-compare-evidence-items] li")).ToHaveCountAsync(2);
        Assert.Equal(new[] { "first / 採用 / revision " + new string('6', 64) + " / セッションを開く", "second / 採用 / revision " + new string('6', 64) + " / セッションを開く" },
            await page.Locator("[data-compare-evidence-items] li").AllTextContentsAsync());
        Assert.Contains(evidenceQueries, query => query.Contains("after=evidence-cursor", StringComparison.Ordinal));
        await page.Keyboard.PressAsync("Escape");
        await Expect(targetEvidence).ToBeFocusedAsync();

        foreach (var (label, load, action) in new[]
                 {
                     ("スキル", "スキルを読み込む", "件数の根拠を表示"),
                     ("ツール", "ツールを読み込む", "エラー件数の根拠を表示"),
                     ("サブエージェント", "サブエージェントを読み込む", "トークン合計の根拠を表示"),
                 })
        {
            var section = page.Locator(".local-monitor-compare-section").Filter(new() { Has = page.Locator("h2", new() { HasText = label }) });
            await section.GetByRole(AriaRole.Button, new() { Name = load, Exact = true }).ClickAsync();
            await Expect(section.GetByRole(AriaRole.Status)).ToContainTextAsync("1件を読み込みました");
            var button = section.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).First;
            await button.ClickAsync();
            await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
            await page.Keyboard.PressAsync("Escape");
        }

        var toolSection = page.Locator(".local-monitor-compare-section").Filter(new() { Has = page.Locator("h2", new() { HasText = "ツール" }) });
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "次のページ", Exact = true }).ClickAsync();
        await Expect(toolSection.Locator("table tbody .local-monitor-compare-result-heading")).ToHaveCountAsync(2);
        Assert.Equal(new[] { "Synthetic tool", "Second tool" },
            await toolSection.Locator("table tbody .local-monitor-compare-result-heading th > div:first-child").AllTextContentsAsync());
        Assert.Equal(0, await toolSection.Locator(":scope > div > div > tr").CountAsync());
        Assert.Contains(rowQueries, query => query.Contains("after=rows-cursor", StringComparison.Ordinal));
        Assert.DoesNotContain(evidenceQueries, query => query.Contains("field_key=a_", StringComparison.Ordinal));

        await targetEvidence.ClickAsync();
        await page.Locator("[data-compare-evidence-items] a").First.ClickAsync();
        await page.WaitForURLAsync($"**/sessions/{SessionId}?node={NodeId}");
    }

    private static Task<string> Golden(string name) => File.ReadAllTextAsync(Path.Combine(
        AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison", name));

    private static string CompareArtifactPath(string name)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "repository-compare"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private static string Readiness() => """{"provider":"github_copilot","selected_model":"model","selected_configuration":"test","readiness_state":"ready","last_check_result":"ready","provider_egress_notice":"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action"}""";

    private static string Run(string state, string result, string? error = null) => $$"""{"run_id":"{{RunId}}","state":"{{state}}","scope_kind":"comparison","session_id":null,"node_id":null,"repository_id":"{{RepositoryId}}","comparison_id":"{{ComparisonId}}","error":{{(error is null ? "null" : $"\"{error}\"")}},"result":{{result}}}""";

    private static string ValidResult() => """
        {"scope":{"kind":"comparison","repository_id":"$REPOSITORY$","comparison_id":"$COMPARISON$","anchor_id":"$COMPARISON$"},"snapshot":{"snapshot_id":"$SNAPSHOT$","payload_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"summary":"<img src=x onerror=alert(1)>","findings":[{"finding_id":"finding-1","title":"要確認","explanation":"解釈です","evidence_state":"supported","evidence_refs":["/sessions/$SESSION$?execution=$EXECUTION$&node=$NODE$"],"limitation":"制約"}],"improvement_suggestions":[{"suggestion_id":"suggestion-1","target_kind":"skill","target_label":"案","concrete_change":"変更","rationale":"理由","expected_effect":"予想","risks_or_limitations":"制約","evidence_refs":["/sessions/$SESSION$?execution=$EXECUTION$&node=$NODE$"]}],"limitations":["制約"],"provenance":{"provider":"github_copilot_sdk","model":"model-a","configuration_sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","prompt_template_version":"compare.v1","requested_at":"2026-08-30T01:00:00.0000000+00:00","started_at":"2026-08-30T01:00:01.0000000+00:00","completed_at":"2026-08-30T01:00:02.0000000+00:00","snapshot_id":"$SNAPSHOT$","snapshot_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","coverage":{"included":2,"excluded":0,"content_available":true}}}
        """.Replace("$REPOSITORY$", RepositoryId, StringComparison.Ordinal).Replace("$COMPARISON$", ComparisonId, StringComparison.Ordinal)
            .Replace("$SNAPSHOT$", SnapshotId, StringComparison.Ordinal).Replace("$SESSION$", SessionId, StringComparison.Ordinal)
            .Replace("$EXECUTION$", ExecutionId, StringComparison.Ordinal).Replace("$NODE$", NodeId, StringComparison.Ordinal);

    private static string SizedValidResult(int byteCount, string suffix = "")
    {
        var result = JsonNode.Parse(ValidResult())!.AsObject();
        result["summary"] = suffix;
        var empty = result.ToJsonString();
        result["summary"] = new string('x', byteCount - Encoding.UTF8.GetByteCount(empty)) + suffix;
        var sized = result.ToJsonString();
        if (suffix.Length > 0)
        {
            sized = sized.Replace("\\u0022result\\u0022:", "\\\"result\\\":", StringComparison.Ordinal);
            var missing = byteCount - Encoding.UTF8.GetByteCount(sized);
            sized = sized.Replace("\\\"result\\\":", new string('x', missing) + "\\\"result\\\":", StringComparison.Ordinal);
        }
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(sized));
        return sized;
    }

    private static string EscapeHeavyValidResult(int byteCount)
    {
        var result = JsonNode.Parse(ValidResult())!.AsObject();
        result["summary"] = string.Empty;
        var empty = result.ToJsonString();
        const string marker = "\"summary\":\"\"";
        var contentBytes = byteCount - Encoding.UTF8.GetByteCount(empty);
        var escaped = string.Concat(Enumerable.Repeat("\\u0061", contentBytes / 6)) + new string('x', contentBytes % 6);
        var sized = empty.Replace(marker, $"\"summary\":\"{escaped}\"", StringComparison.Ordinal);
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(sized));
        return sized;
    }

    private static RouteFulfillOptions Json(string body) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
        Body = body,
    };

    private static MonitorHostTestOptions Options(string readBody) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false,
        StartLocalComparisonCleanupHostedService = false,
        UseUserSecrets = false,
        LocalRepositoryScopeSnapshotService = new ScopeService(),
        LocalMonitorV1ComparisonApplication = new ComparisonApplication(readBody),
    };

    private sealed class ScopeService : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request,
                [new(RepositoryId, "対象リポジトリ", 1, null, 0, LocalArchiveState.Active, 1)], []));
    }

    private sealed class ComparisonApplication(string readBody) : ILocalMonitorV1ComparisonApplication
    {
        public ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(LocalMonitorV1ComparisonOperation operation, string repositoryId, string? comparisonId, ReadOnlyMemory<byte> requestBody, string query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalMonitorV1ComparisonResponse(200, Encoding.UTF8.GetBytes(readBody)));
    }
}

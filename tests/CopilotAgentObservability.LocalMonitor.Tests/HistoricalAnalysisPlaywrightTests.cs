using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class HistoricalAnalysisPlaywrightTests
{
    private const string ExtractionId = "historical-extraction-11111111111111111111111111111111";
    private const string SafeSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RawSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TraceRef = "trace-ref-11111111111111111111111111111111";
    private const string ExpiredRef = "span-ref-22222222222222222222222222222222";
    private const string MissingRef = "session-ref-33333333333333333333333333333333";
    private const string RawMarker = "RAW_HISTORICAL_ANALYSIS_ARGUMENT_MARKER";
    private const string SecretMarker = "SYNTHETIC_SECRET_CARRIER_MARKER";
    private const string PathMarker = "LOCAL_PATH_CARRIER_MARKER/a/b";
    private const string QualityRef = "span-ref-55555555555555555555555555555555";
    private const string MitigationRef = "span-ref-66666666666666666666666666666666";

    [Fact(Timeout = 60_000)]
    public async Task PreviewAndIndependentStarts_AreSemanticKeyboardOperableAndBounded()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync(
            """
            (() => {
              const originalFreeze = Object.freeze;
              Object.freeze = value => {
                if (value
                    && typeof value === "object"
                    && Object.prototype.hasOwnProperty.call(value, "extraction_id")
                    && Object.prototype.hasOwnProperty.call(value, "raw_local_sha256")
                    && Object.prototype.hasOwnProperty.call(value, "repository_safe_sha256")) {
                  window.__historicalAnalysisRetainedPreviewBinding = JSON.stringify(value);
                }
                return originalFreeze(value);
              };
            })();
            """);
        var requests = new ConcurrentQueue<IRequest>();
        page.Request += (_, request) => requests.Enqueue(request);
        await RoutePreview(page);
        await page.RouteAsync("**/api/historical-analysis/v1/instruction-runs", route =>
            route.FulfillAsync(JsonResponse("""{"schema_version":"historical-analysis-error.v1","error":"provider_unavailable"}""", 503)));
        await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs", route =>
            route.FulfillAsync(JsonResponse(
                """{"schema_version":"historical-analysis-efficiency-start.response.v1","analysis_run_id":"historical-efficiency-run-11111111111111111111111111111111","state":"queued"}""",
                202)));
        await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs/*", route =>
            route.FulfillAsync(JsonResponse(EfficiencySucceeded)));

        await page.GotoAsync($"{host.Url}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "履歴分析" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "履歴スコープ" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Instruction analysis" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Efficiency analysis" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" })).ToBeDisabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" })).ToBeDisabledAsync();
        await Expect(page.GetByLabel("Repository")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Workspace")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("開始（UTC）")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("終了（UTC）")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Session ID（1 行 1 件）")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Source surfaces（1 行 1 件）")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Task label")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Experiment label")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("最大 Session 数")).ToHaveValueAsync("50");
        await Expect(page.GetByLabel("sanitized-only")).ToBeCheckedAsync();
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));

        await page.GetByLabel("Repository").FillAsync("repo-safe");
        await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).PressAsync("Enter");
        await Expect(page.Locator("#historical-analysis-preview")).ToBeVisibleAsync();
        await Expect(page.Locator("#historical-analysis-preview-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#historical-analysis-included tbody tr")).ToHaveCountAsync(2);
        await Expect(page.Locator("#historical-analysis-excluded tbody tr")).ToHaveCountAsync(4);
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("partial");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("not_captured");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("source_version=safe-v1");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("adapter_version=safe-adapter");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("source_surfaces=copilot_sdk, claude_code");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("copilot_sdk/safe-v1/safe-adapter");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("token_rollup=true");
        await Expect(page.Locator("#historical-analysis-included")).ToContainTextAsync("quality_reference=false");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("filter_mismatch");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("window_truncated");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("missing_evidence_reference");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("missing_session_reference");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("excluded-reason");
        await Expect(page.Locator("#historical-analysis-excluded")).ToContainTextAsync("metadata_omitted");
        await Expect(page.Locator("#historical-analysis-warnings")).ToContainTextAsync("mixed");
        await Expect(page.Locator("#historical-analysis-warnings")).ToContainTextAsync("truncated_before");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" })).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" })).ToBeEnabledAsync();

        var retainedBinding = await page.EvaluateAsync<string?>(
            "() => window.__historicalAnalysisRetainedPreviewBinding ?? null");
        Assert.NotNull(retainedBinding);
        Assert.DoesNotContain(RawMarker, retainedBinding, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, retainedBinding, StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, retainedBinding, StringComparison.Ordinal);
        using (var binding = JsonDocument.Parse(retainedBinding))
        {
            Assert.Equal(
                ["extraction_id", "raw_local_sha256", "repository_safe_sha256"],
                binding.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(ExtractionId, binding.RootElement.GetProperty("extraction_id").GetString());
            Assert.Equal(RawSha, binding.RootElement.GetProperty("raw_local_sha256").GetString());
            Assert.Equal(SafeSha, binding.RootElement.GetProperty("repository_safe_sha256").GetString());
        }

        var previewRequest = requests.Single(request => request.Url.EndsWith("/api/historical-analysis/v1/preview", StringComparison.Ordinal));
        using (var body = JsonDocument.Parse(previewRequest.PostData!))
        {
            var selection = body.RootElement.GetProperty("selection");
            Assert.Equal(
                ["repository", "workspace", "from", "to", "explicit_session_ids", "source_surfaces", "task_label", "experiment_label", "maximum_session_count", "sanitized_only"],
                selection.EnumerateObject().Select(property => property.Name));
            Assert.Equal("local-monitor", previewRequest.Headers["x-monitor-csrf"]);
        }

        var instruction = page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" });
        await instruction.PressAsync("Space");
        await Expect(page.Locator("#historical-analysis-instruction-state")).ToContainTextAsync("provider_unavailable");
        await Expect(instruction).ToBeFocusedAsync();
        await Expect(page.Locator("#historical-analysis-live")).ToContainTextAsync("provider_unavailable");

        await page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" }).ClickAsync();
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("succeeded");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("supported");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("incomplete");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("coverage_reason");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("not monetary cost");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("subject Session: session-ref-77777777777777777777777777777777");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("source Sessions: session-ref-77777777777777777777777777777777");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("source surfaces: copilot_sdk=2");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("completeness: full=2");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("cohort median: tokens=9 tokens");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("cohort percentile: p75 tokens=10 tokens");
        await Expect(page.Locator($"button[data-evidence-reference='{QualityRef}']")).ToContainTextAsync("Quality evidence");
        await Expect(page.Locator($"button[data-evidence-reference='{MitigationRef}']")).ToContainTextAsync("Mitigation evidence");
        await Expect(page.Locator("#historical-analysis-efficiency-heading")).ToBeFocusedAsync();

        Assert.All(
            requests.Where(request => request.Method == "POST" && request.Url.Contains("/api/historical-analysis/v1/", StringComparison.Ordinal)),
            request => Assert.Equal("local-monitor", request.Headers["x-monitor-csrf"]));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));

        await page.GetByLabel("Workspace").FillAsync("changed-after-preview");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" })).ToBeDisabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" })).ToBeDisabledAsync();
        await Expect(page.Locator("#historical-analysis-live")).ToContainTextAsync("再プレビュー");
        await Expect(page.Locator("#historical-analysis-preview")).ToBeHiddenAsync();
        await Expect(page.Locator("#historical-analysis-instruction-result")).ToBeHiddenAsync();
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToBeHiddenAsync();
        await Expect(page.Locator("#historical-analysis-included tbody tr")).ToHaveCountAsync(0);
        await Expect(page.Locator("#historical-analysis-excluded tbody tr")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-evidence-resolution]")).ToHaveCountAsync(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task InstructionStates_FindingsAndZeroRemainDistinctAndFocusIsManaged()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        await AssertInstructionState(browser, host.Url, "succeeded", InstructionSucceeded, resultFocus: true,
            expected: ["supported", "weak", "eligible", "single_session", "goal_clarity", "acceptance_criteria_missing", "gap summary", "next-time instruction", TraceRef]);
        await AssertInstructionState(browser, host.Url, "zero_findings", InstructionZero, resultFocus: true,
            expected: ["zero_findings", "0 findings"]);

        foreach (var state in new[]
        {
            "content_unavailable", "stale_extraction", "extraction_invalid", "invalid_citation",
            "provider_partial", "provider_failed", "timed_out", "canceled", "no_eligible_sessions",
        })
        {
            await AssertInstructionState(browser, host.Url, state, InstructionTerminal(state), resultFocus: false,
                expected: [state]);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task EvidenceResolution_SeparatesExpiredMissingAndUnresolvedAndKeepsMarkupInert()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var console = new ConcurrentQueue<string>();
        var requests = new ConcurrentQueue<IRequest>();
        page.Console += (_, message) => console.Enqueue(message.Text);
        page.Request += (_, request) => requests.Enqueue(request);
        await RoutePreview(page);
        await RouteInstruction(page, InstructionSucceeded);
        await page.RouteAsync("**/api/historical-analysis/v1/evidence/resolve", async route =>
        {
            using var request = JsonDocument.Parse(route.Request.PostData!);
            var reference = request.RootElement.GetProperty("references")[0].GetString();
            var (state, content, target) = reference switch
            {
                TraceRef => ("resolved", "available", "\"/traces/trace-safe\""),
                ExpiredRef => ("expired", "expired_pending_deletion", "null"),
                MissingRef => ("missing", "not_applicable", "null"),
                _ => ("unresolved", "unsupported", "null"),
            };
            await route.FulfillAsync(JsonResponse(
                $$"""{"schema_version":"historical-analysis-evidence-resolve.response.v1","resolutions":[{"reference":"{{reference}}","resolution_state":"{{state}}","content_state":"{{content}}","target":{{target}}}]}"""));
        });

        await page.GotoAsync($"{host.Url}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("Repository").FillAsync("repo-safe");
        await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" }).ClickAsync();

        foreach (var reference in new[] { TraceRef, ExpiredRef, MissingRef, "span-ref-44444444444444444444444444444444" })
        {
            await page.Locator($"button[data-evidence-reference='{reference}']").First.ClickAsync();
        }
        await Expect(page.Locator($"[data-evidence-resolution='{TraceRef}'] a").First).ToHaveAttributeAsync("href", "/traces/trace-safe");
        await Expect(page.Locator($"[data-evidence-resolution='{TraceRef}'] a").First).ToHaveTextAsync($"Evidence: {TraceRef}");
        await Expect(page.Locator($"[data-evidence-resolution='{ExpiredRef}']").First).ToContainTextAsync("expired");
        await Expect(page.Locator($"[data-evidence-resolution='{ExpiredRef}']").First).ToContainTextAsync("expired_pending_deletion");
        await Expect(page.Locator($"[data-evidence-resolution='{MissingRef}']").First).ToContainTextAsync("missing");
        await Expect(page.Locator("[data-evidence-resolution='span-ref-44444444444444444444444444444444']").First).ToContainTextAsync("unresolved");

        await Expect(page.Locator("#historical-analysis-instruction-result img")).ToHaveCountAsync(0);
        await Expect(page.Locator("#historical-analysis-instruction-result")).ToContainTextAsync(
            "gap summary <img src=x onerror=window.__historicalAnalysisInjected=true>");
        Assert.False(await page.EvaluateAsync<bool>("() => window.__historicalAnalysisInjected === true"));
        var output = await page.ContentAsync();
        foreach (var marker in new[] { RawMarker, SecretMarker, PathMarker })
        {
            Assert.DoesNotContain(marker, output, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, string.Join("\n", console), StringComparison.Ordinal);
            Assert.DoesNotContain(marker, string.Join("\n", requests.Select(request => request.PostData)), StringComparison.Ordinal);
            Assert.DoesNotContain(marker, await page.EvaluateAsync<string>(
            "() => JSON.stringify({local: Object.keys(localStorage), session: Object.keys(sessionStorage), url: location.href})"),
            StringComparison.Ordinal);
            Assert.DoesNotContain(
                marker,
                Encoding.Latin1.GetString(await page.ScreenshotAsync()),
                StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ScopeEditDuringDelayedPreview_CannotAcceptTheStaleResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/historical-analysis/v1/preview", async route =>
        {
            entered.TrySetResult();
            await release.Task;
            await route.FulfillAsync(JsonResponse(Preview));
        });

        try
        {
            await page.GotoAsync($"{host.Url}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByLabel("Repository").FillAsync("scope-before-request");
            await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByLabel("Workspace").FillAsync("scope-edited-while-preview-in-flight");
            release.TrySetResult();

            await Expect(page.Locator("#historical-analysis-live")).ToContainTextAsync("再プレビュー");
            await Expect(page.Locator("#historical-analysis-preview")).ToBeHiddenAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" })).ToBeDisabledAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" })).ToBeDisabledAsync();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StartingNewPreview_RemovesThePriorProjectionBeforeResponseArrives()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewCount = 0;
        await page.RouteAsync("**/api/historical-analysis/v1/preview", async route =>
        {
            previewCount++;
            if (previewCount == 2)
            {
                secondEntered.TrySetResult();
                await releaseSecond.Task;
            }
            await route.FulfillAsync(JsonResponse(Preview));
        });
        await RouteInstruction(page, InstructionSucceeded);

        try
        {
            await page.GotoAsync($"{host.Url}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByLabel("Repository").FillAsync("repo-safe");
            await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" }).ClickAsync();
            await Expect(page.Locator("#historical-analysis-instruction-result")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-evidence-resolution]")).Not.ToHaveCountAsync(0);

            await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Expect(page.Locator("#historical-analysis-preview")).ToBeHiddenAsync();
            await Expect(page.Locator("#historical-analysis-instruction-result")).ToBeHiddenAsync();
            await Expect(page.Locator("#historical-analysis-included tbody tr")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-evidence-resolution]")).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" })).ToBeDisabledAsync();

            releaseSecond.TrySetResult();
            await Expect(page.Locator("#historical-analysis-preview")).ToBeVisibleAsync();
            await Expect(page.Locator("#historical-analysis-instruction-result")).ToBeHiddenAsync();
        }
        finally
        {
            releaseSecond.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PollingBudgetExhaustion_PreservesLastServerStateAndReportsRetryableStop()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await RoutePreview(page);
        await RouteInstruction(page, InstructionTerminal("queued"));
        await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs", route =>
            route.FulfillAsync(JsonResponse(
                """{"schema_version":"historical-analysis-efficiency-start.response.v1","analysis_run_id":"historical-efficiency-run-11111111111111111111111111111111","state":"queued"}""",
                202)));
        await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs/*", route =>
            route.FulfillAsync(JsonResponse(JsonSerializer.Serialize(new
            {
                schema_version = "historical-analysis-efficiency-status.v1",
                analysis_run_id = "historical-efficiency-run-11111111111111111111111111111111",
                extraction_id = ExtractionId,
                repository_safe_sha256 = SafeSha,
                state = "running",
                requested_at = "2026-07-23T00:00:00Z",
                started_at = "2026-07-23T00:00:01Z",
                completed_at = (string?)null,
                receipt = (object?)null,
                receipt_payload_sha256 = (string?)null,
            }))));

        await page.GotoAsync($"{host.Url}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("Repository").FillAsync("repo-safe");
        await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" }).ClickAsync();

        await Expect(page.Locator("#historical-analysis-instruction-state")).ToContainTextAsync(
            "queued · polling_stopped · retryable",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await Expect(page.Locator("#historical-analysis-efficiency-state")).ToContainTextAsync(
            "running · polling_stopped · retryable",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await Expect(page.Locator("#historical-analysis-instruction-result")).ToContainTextAsync("queued");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("running");
        await Expect(page.Locator("#historical-analysis-instruction-result")).Not.ToContainTextAsync("timed_out");
        await Expect(page.Locator("#historical-analysis-efficiency-result")).Not.ToContainTextAsync("timed_out");
    }

    [Fact(Timeout = 60_000)]
    public async Task EfficiencyZeroAndFailureStatesRemainDistinctWithoutEffectOrPricingClaims()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        await AssertEfficiencyState(browser, host.Url, "zero_drivers", receipt: new
        {
            schema_version = "historical-efficiency-receipt.v1",
            state = "zero_drivers",
            coverage = new
            {
                included_session_count = 1,
                excluded_session_count = 0,
                truncated_before = false,
                truncated_session_count = 0,
                completeness = Array.Empty<object>(),
                source_kinds = Array.Empty<object>(),
                capabilities = Array.Empty<object>(),
            },
            quality_availability = "unavailable",
            comparison_notes = Array.Empty<string>(),
            category_coverage = Array.Empty<object>(),
            drivers = Array.Empty<object>(),
        }, resultFocus: true);
        foreach (var state in new[] { "analysis_failed", "stale_extraction", "timed_out", "canceled" })
        {
            await AssertEfficiencyState(browser, host.Url, state, receipt: null, resultFocus: false);
        }
    }

    private static async Task AssertInstructionState(
        IBrowser browser,
        string hostUrl,
        string state,
        string statusJson,
        bool resultFocus,
        IReadOnlyList<string> expected)
    {
        var page = await browser.NewPageAsync();
        try
        {
            await RoutePreview(page);
            await RouteInstruction(page, statusJson);
            await page.GotoAsync($"{hostUrl}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByLabel("Repository").FillAsync("repo-safe");
            await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Instruction 分析を開始" });
            await button.ClickAsync();
            foreach (var value in expected)
            {
                await Expect(page.Locator("#historical-analysis-instruction-result")).ToContainTextAsync(value);
            }
            if (resultFocus)
            {
                await Expect(page.Locator("#historical-analysis-instruction-heading")).ToBeFocusedAsync();
            }
            else
            {
                await Expect(button).ToBeFocusedAsync();
            }
            await Expect(page.Locator("#historical-analysis-live")).ToContainTextAsync(state);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task AssertEfficiencyState(
        IBrowser browser,
        string hostUrl,
        string state,
        object? receipt,
        bool resultFocus)
    {
        var page = await browser.NewPageAsync();
        try
        {
            await RoutePreview(page);
            await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs", route =>
                route.FulfillAsync(JsonResponse(
                    """{"schema_version":"historical-analysis-efficiency-start.response.v1","analysis_run_id":"historical-efficiency-run-11111111111111111111111111111111","state":"queued"}""",
                    202)));
            await page.RouteAsync("**/api/historical-analysis/v1/efficiency-runs/*", route =>
                route.FulfillAsync(JsonResponse(JsonSerializer.Serialize(new
                {
                    schema_version = "historical-analysis-efficiency-status.v1",
                    analysis_run_id = "historical-efficiency-run-11111111111111111111111111111111",
                    extraction_id = ExtractionId,
                    repository_safe_sha256 = SafeSha,
                    state,
                    requested_at = "2026-07-23T00:00:00Z",
                    started_at = "2026-07-23T00:00:01Z",
                    completed_at = "2026-07-23T00:00:02Z",
                    receipt,
                    receipt_payload_sha256 = receipt is null ? null : SafeSha,
                }))));
            await page.GotoAsync($"{hostUrl}/historical-analysis", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByLabel("Repository").FillAsync("repo-safe");
            await page.GetByRole(AriaRole.Button, new() { Name = "スコープをプレビュー" }).ClickAsync();
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Efficiency 分析を開始" });
            await button.ClickAsync();
            await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync(state);
            await Expect(page.Locator("#historical-analysis-efficiency-result")).ToContainTextAsync("not monetary cost");
            await Expect(page.Locator("#historical-analysis-efficiency-result")).Not.ToContainTextAsync("¥");
            await Expect(page.Locator("#historical-analysis-efficiency-result")).Not.ToContainTextAsync("verified improvement");
            if (resultFocus)
            {
                await Expect(page.Locator("#historical-analysis-efficiency-heading")).ToBeFocusedAsync();
            }
            else
            {
                await Expect(button).ToBeFocusedAsync();
            }
            await Expect(page.Locator("#historical-analysis-live")).ToContainTextAsync(state);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static Task RoutePreview(IPage page) =>
        page.RouteAsync("**/api/historical-analysis/v1/preview", route => route.FulfillAsync(JsonResponse(Preview)));

    private static async Task RouteInstruction(IPage page, string statusJson)
    {
        await page.RouteAsync("**/api/historical-analysis/v1/instruction-runs", route =>
            route.FulfillAsync(JsonResponse(
                """{"schema_version":"historical-analysis-instruction-start.response.v1","analysis_run_id":"1","state":"queued"}""",
                202)));
        await page.RouteAsync("**/api/historical-analysis/v1/instruction-runs/*", route =>
            route.FulfillAsync(JsonResponse(statusJson)));
    }

    private static RouteFulfillOptions JsonResponse(string body, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json",
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
        Body = body,
    };

    private static MonitorHostTestOptions QuietHostOptions() => new()
    {
        StartProjectionWorker = false,
        StartWriter = false,
        StartRetentionCleanupWorker = false,
    };

    private static readonly string Preview = JsonSerializer.Serialize(new
    {
        schema_version = "historical-analysis-preview.response.v1",
        extraction_id = ExtractionId,
        raw_local_sha256 = RawSha,
        repository_safe_sha256 = SafeSha,
        selection = new
        {
            repository = "repo-safe",
            workspace = (string?)null,
            from = (string?)null,
            to = (string?)null,
            explicit_session_ids = Array.Empty<string>(),
            source_surfaces = Array.Empty<string>(),
            task_label = (string?)null,
            experiment_label = (string?)null,
            maximum_session_count = 50,
            sanitized_only = true,
        },
        included = new object[]
        {
            new
            {
                session_id = "session-ref-11111111111111111111111111111111", source_surface = "copilot_sdk",
                source_version = "safe-v1", adapter_version = "safe-adapter", completeness = "full",
                completeness_reasons = Array.Empty<string>(), source_kind = "live_otel", content_state = "available",
                descriptor_state = "not_requested",
                raw_local_descriptor = $"{RawMarker}|{SecretMarker}|{PathMarker}",
                capabilities = new
                {
                    turn_rollup = true, token_rollup = true, cache_rollup = false, error_span = false,
                    retry_chain = false, repeated_tool_call = false, permission_wait = false,
                    subagent_fan_out = false, raw_local_descriptor = false, quality_reference = false,
                    source_comparison = false, instruction_finding_reference = false,
                },
                metadata = new
                {
                    repository = "repo-safe",
                    workspace = (string?)null,
                    source_surfaces = new[] { "copilot_sdk", "claude_code" },
                    source_provenance = new[]
                    {
                        new { source_surface = "copilot_sdk", source_application_version = "safe-v1", adapter_version = "safe-adapter" },
                        new { source_surface = "claude_code", source_application_version = "safe-v2", adapter_version = "safe-adapter" },
                    },
                },
            },
            new
            {
                session_id = "session-ref-22222222222222222222222222222222", source_surface = "claude_code",
                source_version = "safe-v2", adapter_version = "safe-adapter", completeness = "partial",
                completeness_reasons = new[] { "missing_session_reference" }, source_kind = "historical_summary",
                content_state = "not_captured", descriptor_state = "unavailable", raw_local_descriptor = (string?)null,
                capabilities = new
                {
                    turn_rollup = true, token_rollup = false, cache_rollup = false, error_span = false,
                    retry_chain = false, repeated_tool_call = false, permission_wait = false,
                    subagent_fan_out = false, raw_local_descriptor = false, quality_reference = false,
                    source_comparison = false, instruction_finding_reference = false,
                },
                metadata = new
                {
                    repository = "repo-safe",
                    workspace = (string?)null,
                    source_surfaces = new[] { "claude_code" },
                    source_provenance = new[]
                    {
                        new { source_surface = "claude_code", source_application_version = "safe-v2", adapter_version = "safe-adapter" },
                    },
                },
            },
        },
        excluded = new object[]
        {
            new
            {
                session_id = "session-ref-33333333333333333333333333333333",
                reason = "filter_mismatch",
                metadata = ExcludedMetadata("excluded-reason", "partial"),
            },
            new
            {
                session_id = "session-ref-44444444444444444444444444444444",
                reason = "window_truncated",
                metadata = ExcludedMetadata("window-reason", "full"),
            },
            new
            {
                session_id = "session-ref-55555555555555555555555555555555",
                reason = "missing_evidence_reference",
                metadata = ExcludedMetadata("no-exact-evidence", "partial"),
            },
            new
            {
                session_id = "session-ref-66666666666666666666666666666666",
                reason = "missing_session_reference",
                metadata = (object?)null,
            },
        },
        truncated_before = true,
        truncated_session_count = 3,
    });

    private static object ExcludedMetadata(string reason, string completeness) => new
    {
        repository = "repo-safe",
        workspace = (string?)null,
        started_at = "2026-07-23T00:00:00Z",
        ended_at = "2026-07-23T00:01:00Z",
        last_seen_at = "2026-07-23T00:01:00Z",
        source_surfaces = new[] { "copilot_sdk" },
        source_provenance = new[]
        {
            new { source_surface = "copilot_sdk", source_application_version = "safe-v1", adapter_version = "safe-adapter" },
        },
        model_observations = Array.Empty<object>(),
        duration_observations = Array.Empty<object>(),
        completeness,
        completeness_reasons = new[] { reason },
        source_kind = "live_otel",
        content_state = "not_captured",
        capabilities = new
        {
            turn_rollup = false, token_rollup = false, cache_rollup = false, error_span = false,
            retry_chain = false, repeated_tool_call = false, permission_wait = false,
            subagent_fan_out = false, raw_local_descriptor = false, quality_reference = false,
            source_comparison = false, instruction_finding_reference = false,
        },
    };

    private static readonly string InstructionSucceeded = InstructionStatus(
        "succeeded",
        new object[]
        {
            new
            {
                finding_id = "finding-supported", verdict = "supported", candidate_eligibility = "eligible",
                support_kind = "recurring", supporting_session_ids = new[] { "session-ref-a", "session-ref-b" },
                supporting_group_ids = new[] { "group-ref-a" }, recurring_count = 2,
                source_surface_distribution = Array.Empty<object>(), source_version_distribution = Array.Empty<object>(),
                source_kind_distribution = Array.Empty<object>(), completeness_distribution = Array.Empty<object>(),
                evidence_refs = new object[] { Evidence(TraceRef), Evidence(ExpiredRef), Evidence(MissingRef) },
            },
            new
            {
                finding_id = "finding-weak", verdict = "weak", candidate_eligibility = "ineligible",
                support_kind = "single_session", supporting_session_ids = new[] { "session-ref-a" },
                supporting_group_ids = new[] { "group-ref-b" }, recurring_count = 1,
                source_surface_distribution = Array.Empty<object>(), source_version_distribution = Array.Empty<object>(),
                source_kind_distribution = Array.Empty<object>(), completeness_distribution = Array.Empty<object>(),
                evidence_refs = new object[] { Evidence("span-ref-44444444444444444444444444444444") },
            },
        },
        new
        {
            schema_version = "instruction-finding-handoff.v1",
            analysis_run_id = 1,
            findings = new object[]
            {
                new
                {
                    finding_id = "finding-supported", category = "goal_clarity", verdict = "supported",
                    candidate_eligibility = "eligible", evidence_refs = new object[] { FindingEvidence(TraceRef), FindingEvidence(ExpiredRef), FindingEvidence(MissingRef) },
                    gap_summary = "gap summary <img src=x onerror=window.__historicalAnalysisInjected=true>",
                    suggested_instruction = "next-time instruction",
                },
                new
                {
                    finding_id = "finding-weak", category = "acceptance_criteria_missing", verdict = "weak",
                    candidate_eligibility = "ineligible", evidence_refs = new object[] { FindingEvidence("span-ref-44444444444444444444444444444444") },
                    gap_summary = "weak gap", suggested_instruction = "weak next-time instruction",
                },
            },
            candidates = Array.Empty<object>(),
        });

    private static readonly string InstructionZero = InstructionStatus("zero_findings", Array.Empty<object>(), new
    {
        schema_version = "instruction-finding-handoff.v1",
        analysis_run_id = 1,
        findings = Array.Empty<object>(),
        candidates = Array.Empty<object>(),
    });

    private static string InstructionTerminal(string state) => InstructionStatus(state, null, null);

    private static string InstructionStatus(string state, object? findings, object? handoff)
    {
        var receipt = findings is null ? null : new
        {
            schema_version = "historical-instruction-analysis.receipt.v1",
            run_id = 1,
            extraction_id = ExtractionId,
            extraction_sha256 = RawSha,
            state,
            model = "model-safe",
            provider = "provider-safe",
            configuration_sha256 = SafeSha,
            timeout_ms = 1000,
            prompt_template_version = "historical-instruction-analysis.prompt.v1",
            truncated_before = false,
            sanitized_only = true,
            content_available = true,
            dataset_distribution = new { completeness = Array.Empty<object>(), source_kinds = Array.Empty<object>(), capabilities = Array.Empty<object>() },
            handoff_sha256 = SafeSha,
            findings,
        };
        return JsonSerializer.Serialize(new
        {
            schema_version = "historical-instruction-analysis.read.v1",
            run_id = 1,
            request = new
            {
                schema_version = "historical-instruction-analysis.request.v1",
                extraction_id = ExtractionId,
                extraction_sha256 = RawSha,
                model = "model-safe",
                provider = "provider-safe",
                configuration_sha256 = SafeSha,
                timeout_ms = 1000,
                prompt_template_version = "historical-instruction-analysis.prompt.v1",
            },
            dataset_projection = new
            {
                truncated_before = false,
                sanitized_only = true,
                content_available = state != "content_unavailable",
                dataset_distribution = new { completeness = Array.Empty<object>(), source_kinds = Array.Empty<object>(), capabilities = Array.Empty<object>() },
            },
            state,
            requested_at = "2026-07-23T00:00:00Z",
            started_at = "2026-07-23T00:00:01Z",
            completed_at = "2026-07-23T00:00:02Z",
            receipt,
            handoff_bytes = handoff is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(handoff))),
        });
    }

    private static object Evidence(string token) => new
    {
        session_id = token.StartsWith("session-", StringComparison.Ordinal) ? token : null,
        trace_id = token.StartsWith("trace-", StringComparison.Ordinal) ? token : TraceRef,
        span_id = token.StartsWith("span-", StringComparison.Ordinal) ? token : null,
        turn_index = 1,
        relative_position = "anchor",
    };

    private static object FindingEvidence(string token) => Evidence(token);

    private static readonly string EfficiencySucceeded = JsonSerializer.Serialize(new
    {
        schema_version = "historical-analysis-efficiency-status.v1",
        analysis_run_id = "historical-efficiency-run-11111111111111111111111111111111",
        extraction_id = ExtractionId,
        repository_safe_sha256 = SafeSha,
        state = "succeeded",
        requested_at = "2026-07-23T00:00:00Z",
        started_at = "2026-07-23T00:00:01Z",
        completed_at = "2026-07-23T00:00:02Z",
        receipt = new
        {
            schema_version = "historical-efficiency-receipt.v1",
            receipt_id = "historical-efficiency-receipt-safe",
            registry_version = "historical-efficiency-driver-registry.v1",
            extraction_id = ExtractionId,
            extraction_sha256 = SafeSha,
            state = "succeeded",
            coverage = new { included_session_count = 2, excluded_session_count = 2, truncated_before = true, truncated_session_count = 3 },
            quality_availability = "partial",
            comparison_notes = new[] { "mixed_source_surface" },
            category_coverage = new object[]
            {
                new { category = "token_volume", driver_version = 1, rule_source = "safe-rule", required_capabilities = new[] { "token_rollup" }, formula = "safe-formula", threshold = "safe-threshold", state = "matched", eligible_session_count = 2, observed_sample_count = 2, minimum_sample = 2, reasons = Array.Empty<string>() },
                new { category = "tool_failure_overhead", driver_version = 1, rule_source = "safe-rule", required_capabilities = Array.Empty<string>(), formula = "unavailable", threshold = "unavailable", state = "unavailable", eligible_session_count = 0, observed_sample_count = 0, minimum_sample = 0, reasons = new[] { "coverage_reason" } },
            },
            drivers = new object[]
            {
                new
                {
                    driver_id = "driver-safe", category = "token_volume", formula = "safe-formula", threshold = "safe-threshold",
                    subject_session_id = "session-ref-77777777777777777777777777777777",
                    source_sessions = new[] { "session-ref-77777777777777777777777777777777" },
                    evidence_refs = new[] { Evidence(TraceRef) }, quality_evidence_refs = new[] { Evidence(QualityRef) },
                    observed_values = new[] { new { name = "tokens", value = 10, unit = "tokens" } },
                    cohort_median = new { name = "tokens", value = 9, unit = "tokens" },
                    cohort_percentile = new { percentile = 75, name = "tokens", value = 10, unit = "tokens" },
                    source_distribution = new
                    {
                        source_surfaces = new[] { new { key = "copilot_sdk", count = 2 } },
                        source_kinds = new[] { new { key = "live_otel", count = 2 } },
                    },
                    completeness_distribution = new[] { new { key = "full", count = 2 } },
                    quality_availability = "available", verdict = "supported", comparison_notes = Array.Empty<string>(),
                    summary = "fixed summary", mitigation = new { code = "review", summary = "fixed mitigation", evidence_refs = new[] { Evidence(MitigationRef) } },
                },
                new
                {
                    driver_id = "driver-incomplete", category = "context_growth", formula = "safe-formula", threshold = "safe-threshold",
                    subject_session_id = (string?)null, source_sessions = Array.Empty<string>(),
                    evidence_refs = Array.Empty<object>(), quality_evidence_refs = Array.Empty<object>(), observed_values = Array.Empty<object>(),
                    cohort_median = (object?)null, cohort_percentile = (object?)null,
                    source_distribution = new { source_surfaces = Array.Empty<object>(), source_kinds = Array.Empty<object>() },
                    completeness_distribution = Array.Empty<object>(),
                    quality_availability = "unavailable", verdict = "incomplete", comparison_notes = new[] { "quality_unavailable" },
                    summary = "fixed incomplete summary", mitigation = new { code = "review", summary = "fixed incomplete mitigation", evidence_refs = Array.Empty<object>() },
                },
            },
        },
        receipt_payload_sha256 = SafeSha,
    });
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class HistoricalImportUiPlaywrightTests
{
    [Fact(Timeout = 60_000)]
    public async Task CurrentUnsupportedSource_RequiresExplicitPreviewAndNeverOffersCommit()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new ConcurrentQueue<IRequest>();
        page.Request += (_, request) => requests.Enqueue(request);
        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = UnsupportedPreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyHistory,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyObservations,
        }));

        await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "履歴インポート" })).ToBeVisibleAsync();
        await Expect(page.Locator("#historical-import-source-legend")).ToContainTextAsync("Live OTel");
        await Expect(page.Locator("#historical-import-source-legend")).ToContainTextAsync("Saved raw");
        await Expect(page.Locator("#historical-import-source-legend")).ToContainTextAsync("Hook / SDK");
        await Expect(page.Locator("#historical-import-source-legend")).ToContainTextAsync("Historical");
        await Expect(page.Locator("#historical-import-source-legend")).ToContainTextAsync("Unsupported");
        var sourceCards = page.Locator(".historical-import-source-card");
        await Expect(sourceCards).ToHaveCountAsync(2);
        await Expect(sourceCards).ToContainTextAsync(["GitHub Copilot CLI", "Claude Code"]);
        await Expect(sourceCards.First).ToContainTextAsync("Unsupported");
        await Expect(sourceCards.First).ToContainTextAsync("metadata_only");
        await Expect(sourceCards.First).ToContainTextAsync("not_read");
        Assert.DoesNotContain(requests, request => request.Method == "POST");

        await page.GetByLabel("履歴ソース").SelectOptionAsync("claude-code");
        await Expect(page.GetByLabel("Session ID")).ToBeHiddenAsync();
        await page.GetByLabel("参照方法").SelectOptionAsync("explicit_user_selection");

        const string privateReference = "C:\\Users\\person\\history\\events.jsonl";
        await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
        await Expect(page.GetByLabel("Session ID")).ToBeVisibleAsync();
        var consentCheckbox = page.GetByLabel("メタデータだけを読み取ることに同意します");
        await Expect(consentCheckbox).ToBeDisabledAsync();
        await page.GetByLabel("参照方法").SelectOptionAsync("selected_root");
        await page.GetByLabel("正確なローカル参照").FillAsync(privateReference);
        await page.GetByLabel("Session ID").FillAsync("session-1");
        await Expect(consentCheckbox).ToBeDisabledAsync();
        await page.GetByLabel("ソースアプリケーション版").FillAsync("1.0.71");
        await Expect(consentCheckbox).ToBeEnabledAsync();
        await consentCheckbox.CheckAsync();
        await page.GetByLabel("ソースアプリケーション版").FillAsync("1.0.72");
        await Expect(consentCheckbox).Not.ToBeCheckedAsync();
        await consentCheckbox.CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();

        await Expect(page.Locator("#historical-import-preview")).ToBeVisibleAsync();
        await Expect(page.Locator("#historical-import-preview-badge")).ToHaveTextAsync("Unsupported");
        await Expect(page.Locator("#historical-import-preview-details")).ToContainTextAsync("historical_import_no_eligible_candidates");
        await Expect(page.Locator("#historical-import-preview-details")).ToContainTextAsync("not_read");
        await Expect(page.Locator("#historical-import-preview-details")).ToContainTextAsync("not_applicable");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" })).ToBeDisabledAsync();
        await Expect(page.GetByLabel("正確なローカル参照")).ToHaveValueAsync(string.Empty);
        Assert.DoesNotContain(privateReference, await page.ContentAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(requests, request => request.Url.Contains(privateReference, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests, request => request.Url.Contains("confirmations", StringComparison.Ordinal)
            || request.Url.EndsWith("/imports", StringComparison.Ordinal));
    }

    [Fact(Timeout = 60_000)]
    public async Task TrustedAdmission_FlowsThroughPreviewConfirmationProgressResultAndSeparateSourceTabs()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new ConcurrentQueue<IRequest>();
        page.Request += (_, request) => requests.Enqueue(request);
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = EligiblePreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/confirmations", async route =>
        {
            confirmationStarted.TrySetResult();
            await releaseConfirmation.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = Confirmation,
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/imports", async route =>
        {
            await releaseImport.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                Body = ImportResult,
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = History,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Observations,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations/hob_22222222222222222222222222222222", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ObservationDetail,
        }));
        await page.RouteAsync("**/api/session-workspace/sessions?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = LiveSessions,
        }));

        try
        {
            await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Assert.DoesNotContain(requests, request => request.Method == "POST");

            const string privateReference = "C:\\Users\\person\\SECRET_HISTORY_PATH.jsonl";
            await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
            await page.GetByLabel("参照方法").SelectOptionAsync("selected_root");
            await page.GetByLabel("正確なローカル参照").FillAsync(privateReference);
            await page.GetByLabel("Session ID").FillAsync("session-1");
            await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
            await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();

            var preview = page.Locator("#historical-import-preview-details");
            await Expect(preview).ToContainTextAsync("3");
            await Expect(preview).ToContainTextAsync("partial");
            await Expect(preview).ToContainTextAsync("historical_summary_only");
            await Expect(preview).ToContainTextAsync("trace_identity");
            await Expect(preview).ToContainTextAsync("not_applicable");
            await Expect(page.Locator("#historical-import-preview-badge")).ToHaveTextAsync("Historical");

            await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).EvaluateAsync("button => button.click()");
            await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Expect(page.GetByLabel("履歴ソース")).ToBeDisabledAsync();
                await Expect(page.GetByLabel("正確なローカル参照")).ToBeDisabledAsync();
                await Expect(page.GetByLabel("ソースアプリケーション版")).ToBeDisabledAsync();
                await Expect(page.Locator(".historical-import-source-card").First).ToBeDisabledAsync();
                await Expect(page.Locator("#historical-import-progress")).ToBeVisibleAsync();
            }
            finally
            {
                releaseConfirmation.TrySetResult();
            }
            await Expect(page.Locator("#historical-import-progress")).ToContainTextAsync("トランザクションを実行しています");
            releaseImport.SetResult();

            await Expect(page.Locator("#historical-import-result")).ToBeVisibleAsync();
            await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("succeeded");
            await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("committed");
            await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("1");
            await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("分析は自動実行されません");

            await page.GetByRole(AriaRole.Tab, new() { Name = "Historical" }).ClickAsync();
            await Expect(page.Locator("#historical-import-observation-list")).ToContainTextAsync("Historical");
            await Expect(page.Locator("#historical-import-observation-list")).ToContainTextAsync("partial");
            await Expect(page.Locator("#historical-import-observation-list")).ToContainTextAsync("event_identity");
            await page.GetByRole(AriaRole.Button, new() { Name = "詳細を表示" }).ClickAsync();
            await Expect(page.Locator("#historical-import-observation-detail")).ToContainTextAsync("historical_summary_only");
            await Expect(page.Locator("#historical-import-observation-detail")).ToContainTextAsync("synthetic-contract-profile");
            await Expect(page.Locator("#historical-import-observation-detail")).ToContainTextAsync("model_tokens.model");
            await Expect(page.Locator("#historical-import-observation-detail img")).ToHaveCountAsync(0);
            Assert.False(await page.EvaluateAsync<bool>("Boolean(window.__historicalInjected)"));
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "トレースを開く" })).ToBeDisabledAsync();
            await Expect(page.Locator("#historical-import-trace-unavailable")).ToContainTextAsync("ナビゲーション専用");

            await page.GetByRole(AriaRole.Tab, new() { Name = "Live" }).ClickAsync();
            await Expect(page.Locator("#historical-import-observation-list")).ToContainTextAsync("Live OTel");
            await Expect(page.Locator("#historical-import-observation-list")).ToContainTextAsync("Hook / SDK");
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Session 詳細を開く" })).ToHaveAttributeAsync(
                "href",
                "/diagnostics?session_id=0198a5ac-7180-7c85-b0d8-000000000001#doctor-session");
            Assert.DoesNotContain(requests, request => request.Url.Contains("analysis", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(privateReference, await page.ContentAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(privateReference, await page.EvaluateAsync<string>("JSON.stringify({local: localStorage, session: sessionStorage, url: location.href})"), StringComparison.Ordinal);
            Assert.DoesNotContain(requests, request => request.Url.Contains(privateReference, StringComparison.OrdinalIgnoreCase));

            var importPosts = requests.Where(request => request.Method == "POST" && request.Url.Contains("/api/historical-import/v1/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, importPosts.Length);
            Assert.All(importPosts, request => Assert.Equal("local-monitor", request.Headers["x-monitor-csrf"]));
        }
        finally
        {
            releaseConfirmation.TrySetResult();
            releaseImport.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AmbiguousCommitResponseReplaysTheExactRequestAndRecoversOneCommittedObservation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var importBodies = new ConcurrentQueue<string>();
        var committedObservationCount = 0;
        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EligiblePreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/confirmations", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Confirmation,
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports", async route =>
        {
            importBodies.Enqueue(route.Request.PostData ?? string.Empty);
            if (importBodies.Count == 1)
            {
                committedObservationCount++;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"committed_but_response_lost\":" ,
                });
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = ImportResult.Replace("\"idempotency_outcome\":\"first_application\"", "\"idempotency_outcome\":\"replayed\"", StringComparison.Ordinal),
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = History,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Observations,
        }));

        await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
        await page.GetByLabel("正確なローカル参照").FillAsync("C:\\synthetic-history");
        await page.GetByLabel("Session ID").FillAsync("session-1");
        await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
        await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).ClickAsync();

        await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("replayed");
        Assert.Equal(1, committedObservationCount);
        Assert.Equal(2, importBodies.Count);
        Assert.Single(importBodies.Distinct(StringComparer.Ordinal));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConsumedResponsePollsQueuedRunningStatusThenRefreshesRecoveredResult()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var statusReads = 0;
        var historyReads = 0;
        var observationReads = 0;
        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EligiblePreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/confirmations", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Confirmation,
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 409,
            ContentType = "application/json",
            Headers = new Dictionary<string, string>
            {
                ["Location"] = "/api/historical-import/v1/imports/hop_22222222222222222222222222222222",
            },
            Body = "{\"error\":\"historical_import_confirmation_consumed\"}",
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports/hop_22222222222222222222222222222222/result", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = ImportResult.Replace("\"idempotency_outcome\":\"first_application\"", "\"idempotency_outcome\":\"replayed\"", StringComparison.Ordinal),
            }));
        await page.RouteAsync("**/api/historical-import/v1/imports/hop_22222222222222222222222222222222", route =>
        {
            statusReads++;
            var state = statusReads switch
            {
                1 => "queued",
                2 => "running",
                _ => "succeeded",
            };
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = Status(state),
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route =>
        {
            historyReads++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = History,
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route =>
        {
            observationReads++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = Observations,
            });
        });

        await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
        await page.GetByLabel("正確なローカル参照").FillAsync("C:\\synthetic-history");
        await page.GetByLabel("Session ID").FillAsync("session-1");
        await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
        await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).ClickAsync();

        await Expect(page.Locator("#historical-import-result")).ToContainTextAsync("replayed");
        await Expect(page.Locator("#historical-import-live")).ToContainTextAsync("復元しました");
        await Expect(page.GetByLabel("履歴ソース")).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" })).ToBeEnabledAsync();
        Assert.Equal(3, statusReads);
        Assert.True(historyReads >= 2);
        Assert.True(observationReads >= 2);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("rejected", "historical_import_store_busy", "保存先が使用中です", 0)]
    [InlineData("rejected", "historical_import_store_unavailable", "historical_import_store_unavailable", 0)]
    [InlineData("failed", "historical_import_transaction_failed", "ロールバックされました", 0)]
    [InlineData("succeeded", null, "historical_import_store_unavailable", 1)]
    public async Task TerminalRecoveryClearsTheExactPendingRequestAndAllowsANewPreview(
        string terminalState,
        string? failureCode,
        string expectedError,
        int expectedResultReads)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var previewPosts = 0;
        var importPosts = 0;
        var resultReads = 0;
        await page.RouteAsync("**/api/historical-import/v1/previews", route =>
        {
            previewPosts++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = EligiblePreview,
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/confirmations", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Confirmation,
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports", route =>
        {
            importPosts++;
            return importPosts == 1
                ? route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"response_lost\":",
                })
                : route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/json",
                    Headers = new Dictionary<string, string>
                    {
                        ["Location"] = "/api/historical-import/v1/imports/hop_22222222222222222222222222222222",
                    },
                    Body = "{\"error\":\"historical_import_confirmation_consumed\"}",
                });
        });
        await page.RouteAsync("**/api/historical-import/v1/imports/hop_22222222222222222222222222222222/result", route =>
        {
            resultReads++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 503,
                ContentType = "application/json",
                Body = "{\"error\":\"historical_import_store_unavailable\"}",
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/imports/hop_22222222222222222222222222222222", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = TerminalStatus(terminalState, failureCode),
            }));
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyHistory,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyObservations,
        }));

        await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
        await page.GetByLabel("正確なローカル参照").FillAsync("C:\\synthetic-history");
        await page.GetByLabel("Session ID").FillAsync("session-1");
        await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
        await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).ClickAsync();

        await Expect(page.Locator("#historical-import-error")).ToContainTextAsync(expectedError);
        await Expect(page.Locator("#historical-import-progress")).ToBeHiddenAsync();
        await Expect(page.GetByLabel("履歴ソース")).ToBeEnabledAsync();
        await Expect(page.GetByLabel("正確なローカル参照")).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" })).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" })).ToBeDisabledAsync();
        Assert.Equal(2, importPosts);
        Assert.Equal(expectedResultReads, resultReads);

        await page.GetByLabel("正確なローカル参照").FillAsync("C:\\synthetic-history-2");
        await page.GetByLabel("Session ID").FillAsync("session-2");
        await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
        await Expect(page.Locator("#historical-import-preview")).ToBeVisibleAsync();
        Assert.Equal(2, previewPosts);
    }

    [Fact(Timeout = 60_000)]
    public async Task ChangingSelectionDuringPreview_AbortsAndDiscardsTheOldResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/historical-import/v1/previews", async route =>
        {
            previewStarted.TrySetResult();
            await releasePreview.Task;
            try
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = EligiblePreview,
                });
            }
            catch (PlaywrightException)
            {
            }
        });
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyHistory,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyObservations,
        }));

        try
        {
            await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            const string privateReference = "C:\\Users\\person\\SECRET_IN_FLIGHT_HISTORY.jsonl";
            await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
            await page.GetByLabel("正確なローカル参照").FillAsync(privateReference);
            await page.GetByLabel("Session ID").FillAsync("session-1");
            await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
            await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
            var previewButton = page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" });
            await previewButton.ClickAsync();
            await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Expect(previewButton).ToBeDisabledAsync();

            try
            {
                await page.GetByLabel("ソースアプリケーション版").FillAsync("changed-after-request");
                await Expect(previewButton).ToBeEnabledAsync();
            }
            finally
            {
                releasePreview.TrySetResult();
            }

            await Expect(page.Locator("#historical-import-preview")).ToBeHiddenAsync();
            await Expect(page.Locator("#historical-import-confirm-button")).ToBeDisabledAsync();
            await Expect(page.Locator("#historical-import-error")).ToBeHiddenAsync();
            await Expect(page.Locator("#historical-import-live")).ToContainTextAsync("入力が変更されたため");
            Assert.DoesNotContain(privateReference, await page.ContentAsync(), StringComparison.Ordinal);
        }
        finally
        {
            releasePreview.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RapidSourceTabSwitch_DiscardsTheStaleHistoricalResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var historicalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistorical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historicalFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", async route =>
        {
            historicalStarted.TrySetResult();
            await releaseHistorical.Task;
            try
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = Observations,
                });
            }
            catch (PlaywrightException)
            {
            }
            finally
            {
                historicalFinished.TrySetResult();
            }
        });
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyHistory,
        }));
        await page.RouteAsync("**/api/session-workspace/sessions?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = LiveSessions,
        }));

        try
        {
            await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await historicalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByRole(AriaRole.Tab, new() { Name = "Live" }).ClickAsync();
            var list = page.Locator("#historical-import-observation-list");
            await Expect(list).ToContainTextAsync("Live OTel");
            releaseHistorical.TrySetResult();
            await historicalFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("hob_22222222222222222222222222222222", await list.InnerTextAsync(), StringComparison.Ordinal);
        }
        finally
        {
            releaseHistorical.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PostImportHistoryRefreshDiscardsTheSlowerInitialResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var initialHistoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialHistoryFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyReads = 0;
        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EligiblePreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/confirmations", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Confirmation,
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ImportResult,
        }));
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", async route =>
        {
            historyReads++;
            if (historyReads == 1)
            {
                initialHistoryStarted.TrySetResult();
                await releaseInitialHistory.Task;
                try
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "application/json",
                        Body = EmptyHistory,
                    });
                }
                catch (PlaywrightException)
                {
                }
                finally
                {
                    initialHistoryFinished.TrySetResult();
                }
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = History,
            });
        });
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyObservations,
        }));

        try
        {
            await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await initialHistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
            await page.GetByLabel("正確なローカル参照").FillAsync("C:\\synthetic-history");
            await page.GetByLabel("Session ID").FillAsync("session-1");
            await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
            await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).ClickAsync();
            await Expect(page.Locator("#historical-import-history-list .historical-import-history-card")).ToHaveCountAsync(1);

            releaseInitialHistory.TrySetResult();
            await initialHistoryFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Expect(page.Locator("#historical-import-history-list .historical-import-history-card")).ToHaveCountAsync(1);
            await Expect(page.Locator("#historical-import-history-list")).Not.ToContainTextAsync("インポート履歴はまだありません");
            Assert.Equal(2, historyReads);
        }
        finally
        {
            releaseInitialHistory.TrySetResult();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Diagnostics_ProvidesContextualEntryWithoutChangingTwoItemSidebar()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var link = page.GetByRole(AriaRole.Link, new() { Name = "履歴インポートを開く" });
        await Expect(link).ToHaveAttributeAsync("href", "/historical-import");
        await Expect(page.Locator(".sidebar-nav > a")).ToHaveCountAsync(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task StaleCommit_ShowsFixedErrorAndDoesNotRetryOrRetainPrivateReference()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHostOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var posts = new ConcurrentQueue<IRequest>();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST") posts.Enqueue(request);
        };
        await page.RouteAsync("**/api/historical-import/v1/previews", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EligiblePreview,
        }));
        await page.RouteAsync("**/api/historical-import/v1/confirmations", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = Confirmation,
        }));
        await page.RouteAsync("**/api/historical-import/v1/imports", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 409,
            ContentType = "application/json",
            Body = "{\"error\":\"historical_import_preview_stale\"}",
        }));
        await page.RouteAsync("**/api/historical-import/v1/history?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyHistory,
        }));
        await page.RouteAsync("**/api/historical-import/v1/observations?limit=50", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = EmptyObservations,
        }));

        await page.GotoAsync($"{host.Url}/historical-import", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        const string privateReference = "C:\\Users\\person\\SECRET_STALE_HISTORY.jsonl";
        await page.GetByLabel("履歴ソース").SelectOptionAsync("github-copilot-cli");
        await page.GetByLabel("正確なローカル参照").FillAsync(privateReference);
        await page.GetByLabel("Session ID").FillAsync("session-1");
        await page.GetByLabel("ソースアプリケーション版").FillAsync("9.9.9-synthetic");
        await page.GetByLabel("メタデータだけを読み取ることに同意します").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "プレビューを作成" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" }).ClickAsync();

        await Expect(page.Locator("#historical-import-error")).ToContainTextAsync("プレビューが古くなりました");
        await Expect(page.Locator("#historical-import-progress")).ToBeHiddenAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "確認してインポート" })).ToBeDisabledAsync();
        Assert.Equal(3, posts.Count);
        Assert.DoesNotContain(privateReference, await page.ContentAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(posts, request => request.Url.Contains(privateReference, StringComparison.OrdinalIgnoreCase));
    }

    private static MonitorHostTestOptions QuietHostOptions() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static string Status(string state)
    {
        var (version, lifecycle, outcome, resultAvailable) = state switch
        {
            "queued" => (1, "[\"queued\"]", "pending", false),
            "running" => (2, "[\"queued\",\"running\"]", "pending", false),
            "succeeded" => (3, "[\"queued\",\"running\",\"succeeded\"]", "committed", true),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        return $$"""
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-status/v1","operation_id":"hop_22222222222222222222222222222222","request_id":"hir_22222222222222222222222222222222","operation_version":{{version}},"state":"{{state}}","lifecycle":{{lifecycle}},"transaction_outcome":"{{outcome}}","counts":{"total":3,"processed":{{(resultAvailable ? 3 : 0)}},"new_observations":{{(resultAvailable ? 1 : 0)}},"duplicates":{{(resultAvailable ? 1 : 0)}},"conflicts":{{(resultAvailable ? 1 : 0)}},"record_rejections":{{(resultAvailable ? 1 : 0)}}},"result_available":{{resultAvailable.ToString().ToLowerInvariant()}},"failure_code":null}
            """;
    }

    private static string TerminalStatus(string state, string? failureCode)
    {
        var (lifecycle, outcome, resultAvailable) = state switch
        {
            "rejected" => ("[\"queued\",\"running\",\"rejected\"]", "not_started", false),
            "failed" => ("[\"queued\",\"running\",\"failed\"]", "rolled_back", false),
            "succeeded" => ("[\"queued\",\"running\",\"succeeded\"]", "committed", true),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        if (resultAvailable != (failureCode is null)) throw new ArgumentException("Terminal failure code does not match state.", nameof(failureCode));
        var failureJson = failureCode is null ? "null" : JsonSerializer.Serialize(failureCode);
        return $$"""
            {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-status/v1","operation_id":"hop_22222222222222222222222222222222","request_id":"hir_22222222222222222222222222222222","operation_version":3,"state":"{{state}}","lifecycle":{{lifecycle}},"transaction_outcome":"{{outcome}}","counts":{"total":3,"processed":{{(resultAvailable ? 3 : 0)}},"new_observations":{{(resultAvailable ? 1 : 0)}},"duplicates":{{(resultAvailable ? 1 : 0)}},"conflicts":{{(resultAvailable ? 1 : 0)}},"record_rejections":{{(resultAvailable ? 1 : 0)}}},"result_available":{{resultAvailable.ToString().ToLowerInvariant()}},"failure_code":{{failureJson}}}
            """;
    }

    private const string UnsupportedPreview = """
        {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-preview/v1","preview_id":"hip_0123456789abcdef0123456789abcdef","preview_digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000","snapshot_version":"hsv_1","expires_after_seconds":300,"source_selection_id":"hss_0123456789abcdef0123456789abcdef","source_kind":"historical","source_surface":"github-copilot-cli","source_badge":"unsupported","source_tier":"tier_b","profile_id":"github-copilot-cli-session-state","adapter_id":"github-copilot-cli-history-v1","adapter_state":"unsupported","adapter_diagnostics":["historical_source_format_unsupported"],"evidence_status":"production","source_application_version":"1.0.71","source_format":{"name":null,"version":null},"requested_capture":"metadata_only","source_time_range":{"availability":"unavailable","start":null,"end":null},"counts":{"total":{"availability":"unavailable","value":null},"eligible":{"availability":"available","value":0},"unsupported":{"availability":"unavailable","value":null},"malformed":{"availability":"unavailable","value":null},"duplicates":{"availability":"unavailable","value":null},"conflicts":{"availability":"unavailable","value":null},"new_observations":{"availability":"unavailable","value":null},"new_sessions":{"availability":"unavailable","value":null},"new_events":{"availability":"unavailable","value":null},"merge_candidates":{"availability":"unavailable","value":null},"excluded":{"availability":"unavailable","value":null}},"content_risk":"not_read","completeness_ceiling":"none","completeness_reasons":[],"missing_capabilities":[],"merge_candidates":[],"retention_impact":{"disposition":"not_applicable","created_item_count":0,"default_ttl_days":null,"automatic_pin":false},"exclusions":[{"code":"historical_source_format_unsupported","count":1}],"commit_allowed":false,"rejection_code":"historical_import_no_eligible_candidates"}
        """;

    private const string EligiblePreview = """
        {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-preview/v1","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1","expires_after_seconds":300,"source_selection_id":"hss_11111111111111111111111111111111","source_kind":"historical","source_surface":"github-copilot-cli","source_badge":"historical","source_tier":"tier_b","profile_id":"synthetic-contract-profile","adapter_id":"synthetic-contract-adapter-v1","adapter_state":"available","adapter_diagnostics":[],"evidence_status":"production","source_application_version":"9.9.9-synthetic","source_format":{"name":"synthetic-contract-format","version":"1.0.0"},"requested_capture":"metadata_only","source_time_range":{"availability":"unavailable","start":null,"end":null},"counts":{"total":{"availability":"available","value":3},"eligible":{"availability":"available","value":3},"unsupported":{"availability":"available","value":0},"malformed":{"availability":"available","value":0},"duplicates":{"availability":"available","value":1},"conflicts":{"availability":"available","value":1},"new_observations":{"availability":"available","value":1},"new_sessions":{"availability":"unavailable","value":null},"new_events":{"availability":"unavailable","value":null},"merge_candidates":{"availability":"available","value":0},"excluded":{"availability":"available","value":0}},"content_risk":"source_read_metadata_only","completeness_ceiling":"partial","completeness_reasons":["historical_summary_only"],"missing_capabilities":["content","event_identity","lifecycle","session_identity","timing","trace_identity"],"merge_candidates":[],"retention_impact":{"disposition":"not_applicable","created_item_count":0,"default_ttl_days":null,"automatic_pin":false},"exclusions":[],"commit_allowed":true,"rejection_code":null}
        """;

    private const string Confirmation = """
        {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-confirmation/v1","confirmation_id":"hic_22222222222222222222222222222222","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1","eligibility":"eligible","decision":"confirm","expires_after_seconds":300}
        """;

    private const string ImportResult = """
        {"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-result/v1","operation_id":"hop_22222222222222222222222222222222","request_id":"hir_22222222222222222222222222222222","preview_id":"hip_11111111111111111111111111111111","preview_digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","snapshot_version":"hsv_1","outcome":"succeeded","transaction_outcome":"committed","idempotency_outcome":"first_application","source_kind":"historical","source_surface":"github-copilot-cli","source_tier":"tier_b","profile_id":"synthetic-contract-profile","adapter_id":"synthetic-contract-adapter-v1","evidence_status":"production","counts":{"total":{"availability":"available","value":3},"new_observations":{"availability":"available","value":1},"new_sessions":{"availability":"unavailable","value":null},"new_events":{"availability":"unavailable","value":null},"duplicates":{"availability":"available","value":1},"conflicts":{"availability":"available","value":1},"record_rejections":{"availability":"available","value":1}},"observations":[{"observation_id":"hob_22222222222222222222222222222222","identity_resolution":"distinct_unbound","binding_basis":"none","completeness":"partial","completeness_reasons":["historical_summary_only"],"missing_capabilities":["content","event_identity","lifecycle","session_identity","timing","trace_identity"],"content_state":"not_captured"}],"duplicates":[],"conflicts":[],"retention":{"disposition":"not_applicable","created_item_count":0,"pin_state":"not_applicable","deletion_state":"not_applicable"}}
        """;

    private const string EmptyHistory = """{"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-history/v1","items":[]}""";
    private const string History = """{"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-import-history/v1","items":[{"operation_id":"hop_22222222222222222222222222222222","state":"succeeded","outcome":"committed","source_kind":"historical","source_surface":"github-copilot-cli","source_badge":"historical","source_tier":"tier_b","profile_id":"synthetic-contract-profile","adapter_id":"synthetic-contract-adapter-v1","new_observation_count":1,"duplicate_count":1,"conflict_count":1,"completeness":"partial","completeness_reasons":["historical_summary_only"],"content_state":"not_captured","retention_disposition":"not_applicable"}]}""";
    private const string EmptyObservations = """{"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-observation-list/v1","source_filter":"historical","items":[],"next_cursor":null}""";
    private const string Observations = """{"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-observation-list/v1","source_filter":"historical","items":[{"observation_id":"hob_22222222222222222222222222222222","source_kind":"historical","source_surface":"github-copilot-cli","source_badge":"historical","source_tier":"tier_b","completeness":"partial","completeness_reasons":["historical_summary_only"],"missing_capabilities":["content","event_identity","lifecycle","timing"],"content_state":"not_captured","trace_controls_enabled":false}],"next_cursor":null}""";
    private const string ObservationDetail = """{"contract_version":"historical-import-workflow/v1","schema_version":"historical-import-workflow-observation-detail/v1","observation_id":"hob_22222222222222222222222222222222","source_kind":"historical","source_surface":"github-copilot-cli","source_badge":"historical","source_tier":"tier_b","profile_id":"synthetic-contract-profile","adapter_id":"synthetic-contract-adapter-v1","identity_resolution":"attached_exact","binding_basis":"exact_trace_id","completeness":"partial","completeness_reasons":["historical_summary_only"],"missing_capabilities":["content","event_identity","lifecycle","timing"],"content_state":"not_captured","summary_fields":["model_tokens.model"],"trace_controls_enabled":false,"retention_disposition":"not_applicable"}""";
    private const string LiveSessions = """{"items":[{"session_id":"0198a5ac-7180-7c85-b0d8-000000000001","status":"active","completeness":"full","source_surfaces":["copilot-cli"],"repository":null,"workspace":null,"started_at":"2026-07-22T00:00:00Z","ended_at":null,"last_seen_at":"2026-07-22T00:00:00Z","raw_retention_state":"not_captured","source_diagnostic":null,"binding_state":"exact_linked","completeness_reason_codes":[],"content_state":"not_captured"}]}""";
}

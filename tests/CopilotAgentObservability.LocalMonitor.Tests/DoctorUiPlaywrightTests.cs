using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public sealed class DoctorUiPlaywrightTests
{
    [Fact]
    public async Task Diagnostics_RendersCanonicalDoctorResultAndServerNavigationAsInertText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = VerificationJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-current-state")).ToContainTextAsync("最初の記録を確認できました");
        await Expect(page.Locator("#doctor-severity")).ToHaveTextAsync("info");
        await Expect(page.Locator("#doctor-result-source")).ToHaveTextAsync("claude-code");
        await Expect(page.Locator("#doctor-next-action")).ToContainTextAsync("確認済みの Trace または Session を開いてください");
        await Expect(page.Locator("#doctor-retryability")).ToHaveTextAsync("none");
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        await Expect(page.Locator("#doctor-evidence-list li")).ToHaveCountAsync(1);
        await Expect(page.Locator("#doctor-evidence-list")).ToContainTextAsync("ev:<b>unsafe</b>");
        await Expect(page.Locator("#doctor-evidence-list a")).ToHaveAttributeAsync("href", "/traces/trace-exact-1");
        Assert.DoesNotContain("<b>unsafe</b></a>", await page.ContentAsync());
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
        await Expect(page.Locator("#doctor-live")).ToContainTextAsync("completed");
    }

    [Fact]
    public async Task Diagnostics_ShowsNoDetectedSourcesWithoutSelectingOne()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson.Replace("\"detected\"", "\"not_detected\"", StringComparison.Ordinal),
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#doctor-source-state")).ToHaveTextAsync("検出されたソースはありません。ソースを選択して確認できます。");
        await Expect(page.Locator("#doctor-source")).ToHaveValueAsync("");
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Diagnostics_FailedGetOffersOneExplicitRetryAndRestoresHeadingFocus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var attempts = 0;

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route =>
        {
            attempts += 1;
            return attempts == 1
                ? route.FulfillAsync(new RouteFulfillOptions { Status = 503, ContentType = "application/json", Body = "{\"code\":\"doctor_store_unavailable\"}" })
                : route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = SourcesJson });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("再試行");
        await Expect(page.Locator("#doctor-live")).ToHaveTextAsync("Doctor の状態を読み込めませんでした。");
        await Expect(page.Locator("#doctor-result-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#doctor-actions button:visible")).ToHaveCountAsync(1);

        await page.Locator("#doctor-primary-action").PressAsync("Enter");

        await Expect(page.Locator("#doctor-source option")).ToHaveCountAsync(3);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Diagnostics_ActiveVerificationRetriesOnlyTheExactStatusGet()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var statusAttempts = 0;
        var statusUrls = new List<string>();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveVerificationJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*", route =>
        {
            statusAttempts += 1;
            statusUrls.Add(route.Request.Url);
            return statusAttempts == 1
                ? route.FulfillAsync(new RouteFulfillOptions { Status = 503, ContentType = "application/json", Body = "{\"error\":\"doctor_store_unavailable\"}" })
                : route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = VerificationJson });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("active");
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("状態を確認");

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("再試行");
        Assert.Equal(1, statusAttempts);

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        Assert.Equal(2, statusAttempts);
        Assert.All(statusUrls, url => Assert.EndsWith(
            "/api/doctor/ui/v1/verifications/018f0c57-7b34-7cc3-8a13-8a90a2345678",
            url,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_ActiveCandidatesRequireExplicitSelectionAndCompleteWithCurrentRevision()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        string? completeBody = null;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/complete", async route =>
        {
            completeBody = route.Request.PostData;
            await route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = VerificationJson });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveWithCandidatesJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-candidate-list input[type=checkbox]")).ToHaveCountAsync(2);
        await Expect(page.Locator("#doctor-primary-action")).ToBeDisabledAsync();
        await Expect(page.Locator("#doctor-cancel-action")).ToBeVisibleAsync();
        await Expect(page.Locator("#doctor-actions .monitor-btn-primary:visible")).ToHaveCountAsync(1);

        await page.GetByLabel("候補 evidence:one を選択").CheckAsync();
        await page.GetByLabel("候補 evidence:<img src=x onerror=alert(1)> を選択").PressAsync("Space");
        await Expect(page.Locator("#doctor-primary-action")).ToBeEnabledAsync();
        await page.Locator("#doctor-primary-action").ClickAsync();

        Assert.Equal(
            "{\"expected_revision\":2,\"accepted_evidence_refs\":[\"evidence:one\",\"evidence:<img src=x onerror=alert(1)>\"]}",
            completeBody);
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
        await Expect(page.Locator("#doctor-cancel-action")).ToBeHiddenAsync();
        Assert.DoesNotContain("<img src=x", await page.ContentAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostics_ActiveWithoutCandidatesUsesExactStatusGetAndCanCancel()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        string? cancelBody = null;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/cancel", async route =>
        {
            cancelBody = route.Request.PostData;
            await route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = CancelledVerificationJson });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveVerificationJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("状態を確認");
        await Expect(page.Locator("#doctor-cancel-action")).ToHaveTextAsync("検証をキャンセル");
        await page.Locator("#doctor-cancel-action").ClickAsync();

        Assert.Equal("{\"expected_revision\":2}", cancelBody);
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("cancelled");
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("ロールバック後の状態を更新");
        await Expect(page.Locator("#doctor-cancel-action")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Diagnostics_LostMutationResponseRecoversWithGetBeforeOfferingAnotherMutation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var completePosts = 0;
        var statusGets = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/complete", route =>
        {
            completePosts += 1;
            return route.AbortAsync();
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*", route =>
        {
            statusGets += 1;
            return route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = VerificationJson });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveWithCandidatesJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await page.GetByLabel("候補 evidence:one を選択").CheckAsync();
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("現在の状態を確認");
        Assert.Equal(1, completePosts);
        Assert.Equal(0, statusGets);

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        Assert.Equal(1, completePosts);
        Assert.Equal(1, statusGets);
    }

    [Fact]
    public async Task Diagnostics_StaleMutationEnvelopeIsShownButRequiresExactGetBeforeAnotherMutation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var completePosts = 0;
        var statusGets = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/complete", route =>
        {
            completePosts += 1;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 409,
                ContentType = "application/json",
                Body = ActiveWithCandidatesJson.Replace("\"code\":\"verification_completed\"", "\"code\":\"verification_stale\"", StringComparison.Ordinal),
            });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*", route =>
        {
            statusGets += 1;
            return route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = VerificationJson });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveWithCandidatesJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await page.GetByLabel("候補 evidence:one を選択").CheckAsync();
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-current-state")).ToContainTextAsync("最初の記録を確認できました");
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("現在の状態を確認");
        Assert.Equal(1, completePosts);
        Assert.Equal(0, statusGets);

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        Assert.Equal(1, completePosts);
        Assert.Equal(1, statusGets);
    }

    [Fact]
    public async Task Diagnostics_ExpiredMutationEnvelopeRemainsTerminalWithoutRetryActions()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/cancel", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 410,
            ContentType = "application/json",
            Body = TerminalVerificationJson("expired"),
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveVerificationJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await page.Locator("#doctor-cancel-action").ClickAsync();

        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("expired");
        await Expect(page.Locator("#doctor-actions button:visible")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task Diagnostics_TerminalLifecycleExposesNoMutation(string lifecycle)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = TerminalVerificationJson(lifecycle),
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync(lifecycle);
        await Expect(page.Locator("#doctor-actions button:visible")).ToHaveCountAsync(lifecycle == "cancelled" ? 1 : 0);
        await Expect(page.Locator("#doctor-live")).ToContainTextAsync(lifecycle);
    }

    [Fact]
    public async Task Diagnostics_RendersBoundedCrossSurfaceStateMatrixWithoutCarryingSourceFacts()
    {
        var rows = new[]
        {
            ("monitor_not_installed", "Monitor がインストールされていません", "install_monitor", "Monitor をインストールしてください", "error", "after_action"),
            ("monitor_not_running", "Monitor が起動していません", "start_monitor", "Monitor を起動してください", "error", "after_action"),
            ("receiver_not_bound", "受信ポートを利用できません", "restart_monitor", "Monitor を再起動してください", "error", "after_action"),
            ("port_owned_by_foreign_process", "受信ポートを別のプロセスが使用しています", "free_or_change_port", "ポートを解放するか変更してください", "error", "after_action"),
            ("endpoint_mismatch", "送信先が Monitor と一致しません", "update_source_endpoint", "送信元の接続先を更新してください", "error", "after_action"),
            ("protocol_mismatch", "送信プロトコルが一致しません", "use_http_protobuf", "HTTP/Protobuf を使用してください", "error", "after_action"),
            ("signal_disabled", "トレース送信が無効です", "enable_trace_signal", "トレース送信を有効にしてください", "error", "after_action"),
            ("unsupported_source_version", "この取得元のバージョンには対応していません", "use_supported_source_version", "対応する取得元バージョンを使用してください", "error", "after_action"),
            ("feature_unavailable", "必要な機能をこの取得元では利用できません", "use_supported_source_surface", "対応する取得元を使用してください", "error", "after_action"),
            ("agent_restart_required", "取得元の再起動が必要です", "restart_source_process", "取得元のプロセスを再起動してください", "warning", "after_action"),
            ("endpoint_unreachable", "Monitor の受信先へ接続できません", "verify_endpoint_reachability", "受信先への接続を確認してください", "error", "after_action"),
            ("payload_rejected", "送信データを受け付けられませんでした", "inspect_rejected_payload", "拒否された送信データの診断を確認してください", "error", "after_action"),
            ("raw_persisted_projection_pending", "記録済みデータの反映を待っています", "wait_for_projection", "反映の完了を待ってください", "warning", "automatic"),
            ("projection_failed", "記録済みデータを画面へ反映できませんでした", "open_projection_diagnostics", "反映処理の診断を確認してください", "error", "after_action"),
            ("session_unbound", "記録を Session に結び付けられません", "select_exact_session", "対象の Session を選択してください", "error", "after_action"),
            ("content_capture_disabled", "内容の記録が無効です", "enable_content_capture_if_desired", "必要な場合は内容の記録を有効にしてください", "warning", "after_action"),
            ("sanitized_only_raw_unavailable", "内容は記録されていません", "restart_without_sanitized_only_if_desired", "必要な場合は通常モードで Monitor を再起動してください", "warning", "after_action"),
            ("schema_drift_detected", "取得元のスキーマ変更を検出しました", "review_source_diagnostics", "取得元の診断を確認してください", "warning", "after_action"),
            ("ready_no_real_trace", "接続確認のための記録がまだありません", "run_bounded_source_interaction", "取得元で確認用の操作を実行してください", "info", "after_action"),
            ("first_trace_ready", "最初の記録を確認できました", "open_verified_trace_or_session", "確認済みの Trace または Session を開いてください", "info", "none"),
        };
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var rowIndex = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route =>
        {
            var row = rows[rowIndex++];
            var source = route.Request.PostData?.Contains("github-copilot-cli", StringComparison.Ordinal) == true
                ? "github-copilot-cli"
                : "claude-code";
            return route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = MatrixVerificationJson(row.Item1, row.Item5, row.Item3, row.Item6, source),
            });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var source = index % 2 == 0 ? "claude-code" : "github-copilot-cli";
            await page.Locator("#doctor-source").SelectOptionAsync(source);
            await Expect(page.Locator("#doctor-current-state")).ToHaveTextAsync("—");
            await page.Locator("#doctor-primary-action").ClickAsync();
            await Expect(page.Locator("#doctor-current-state")).ToContainTextAsync(row.Item2);
            await Expect(page.Locator("#doctor-next-action")).ToContainTextAsync(row.Item4);
            await Expect(page.Locator("#doctor-result-source")).ToHaveTextAsync(source);
            await Expect(page.Locator("#doctor-current-state details summary")).ToHaveTextAsync("技術情報");
            await page.Locator("#doctor-current-state details summary").PressAsync("Enter");
            await Expect(page.Locator("#doctor-current-state code")).ToHaveTextAsync(row.Item1);
            await Expect(page.Locator("#doctor-next-action code")).ToHaveTextAsync(row.Item3);
        }

        Assert.Equal(rows.Length, rowIndex);
    }

    [Fact]
    public async Task Diagnostics_DelayedBeginLocksSourceAndPreservesAuthoritativeResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginPosts = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", async route =>
        {
            beginPosts += 1;
            arrived.SetResult();
            await release.Task;
            await route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = ActiveVerificationJson });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expect(page.Locator("#doctor-source")).ToBeDisabledAsync();
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
        release.SetResult();

        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("active");
        await Expect(page.Locator("#doctor-result-source")).ToHaveTextAsync("claude-code");
        await Expect(page.Locator("#doctor-source")).ToBeDisabledAsync();
        Assert.Equal(1, beginPosts);
    }

    [Fact]
    public async Task Diagnostics_LostBeginResponseLocksSourceWithoutBlindRetry()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var beginPosts = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route =>
        {
            beginPosts += 1;
            return route.AbortAsync();
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-source")).ToBeDisabledAsync();
        await Expect(page.Locator("#doctor-actions button:visible")).ToHaveCountAsync(0);
        await Expect(page.Locator("#doctor-live")).ToHaveTextAsync("検証開始の結果を確認できませんでした。ページを再読み込みしてください。");
        Assert.Equal(1, beginPosts);
    }

    [Fact]
    public async Task Diagnostics_CancelThenPostRollbackExactGetRetainsLifecycleAndRefreshesFacts()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var afterRollback = false;
        var statusGets = 0;

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/cancel", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = CancelledVerificationJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*", route =>
        {
            statusGets += 1;
            var body = afterRollback
                ? CancelledVerificationJson
                    .Replace("first_trace_ready", "signal_disabled", StringComparison.Ordinal)
                    .Replace("open_verified_trace_or_session", "enable_trace_signal", StringComparison.Ordinal)
                : CancelledVerificationJson;
            return route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = body });
        });
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveVerificationJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await page.Locator("#doctor-cancel-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("cancelled");
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("ロールバック後の状態を更新");

        afterRollback = true;
        await page.Locator("#doctor-primary-action").ClickAsync();

        Assert.True(afterRollback);
        Assert.Equal(1, statusGets);
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("cancelled");
        await Expect(page.Locator("#doctor-current-state")).ToContainTextAsync("トレース送信が無効です");
        await Expect(page.Locator("#doctor-next-action")).ToContainTextAsync("トレース送信を有効にしてください");
    }

    [Fact]
    public async Task Diagnostics_KeyboardOnlyCompletesExplicitCandidateFlow()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await RouteSourcesAsync(page);
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*/complete", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = VerificationJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveWithCandidatesJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").FocusAsync();
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#doctor-primary-action")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");

        await Expect(page.Locator("#doctor-result-heading")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#doctor-current-state summary")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#doctor-next-action summary")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#doctor-candidate-list input").First).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#doctor-primary-action")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");

        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        await Expect(page.Locator("#doctor-live")).ToContainTextAsync("completed");
    }

    [Fact]
    public async Task Diagnostics_ExactEvidenceQueriesRenderOnlySanitizedInertSummaries()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string sessionId = "018f0c57-7b34-7cc3-8a13-8a90a2345678";
        const string observationId = "obs-safe-1";

        await RouteSourcesAsync(page);
        await page.RouteAsync($"**/api/doctor/ui/v1/sessions/{sessionId}", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = "{\"schema_version\":\"doctor.ui.v1\",\"session\":{\"session_id\":\"" + sessionId + "\",\"status\":\"complete\",\"completeness\":\"full\",\"last_seen_at\":\"2026-07-21T00:03:00.0000000Z\"}}",
        }));
        await page.RouteAsync($"**/api/doctor/ui/v1/source-diagnostics/{observationId}", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = "{\"schema_version\":\"doctor.ui.v1\",\"observation\":{\"observation_id\":\"obs:<svg/onload=alert(1)>\",\"observed_at\":\"2026-07-21T00:02:00.0000000Z\",\"source_diagnostic\":{\"source_surface\":\"claude-code\",\"source_adapter\":\"claude-code-otel\",\"compatibility_state\":\"supported\",\"next_action\":\"none\"}}}",
        }));

        await page.GotoAsync($"{host.Url}/diagnostics?session_id={sessionId}&observation_id={observationId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#doctor-session-target")).ToContainTextAsync(sessionId);
        await Expect(page.Locator("#doctor-session-target")).ToContainTextAsync("full");
        await Expect(page.Locator("#doctor-source-target")).ToContainTextAsync("obs:<svg/onload=alert(1)>");
        await Expect(page.Locator("#doctor-source-target")).ToContainTextAsync("対応済み");
        await Expect(page.Locator("#doctor-source-target details summary").First).ToHaveTextAsync("技術情報");
        await page.Locator("#doctor-source-target details summary").First.PressAsync("Enter");
        await Expect(page.Locator("#doctor-source-target")).ToContainTextAsync("supported");
        await Expect(page.Locator("#doctor-source-target svg")).ToHaveCountAsync(0);
        var content = await page.ContentAsync();
        Assert.DoesNotContain("raw_prompt", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", content, StringComparison.OrdinalIgnoreCase);
    }

    private static Task RouteSourcesAsync(IPage page) =>
        page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson,
        }));

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string SourcesJson = """
        {"schema_version":"doctor.ui.v1","sources":[
          {"source_id":"github-copilot-cli","display_label":"GitHub Copilot CLI","setup_ownership":"managed_on_windows","detection_state":"not_detected"},
          {"source_id":"claude-code","display_label":"Claude <Code>","setup_ownership":"managed_cli","detection_state":"detected"}
        ]}
        """;

    private const string VerificationJson = """
        {"schema_version":"doctor.ui.v1","envelope":{
          "contract_version":"first-trace.v1","command":"begin","success":true,"code":"verification_completed",
          "adapter":"claude-code","source_surface":"claude-code","verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678",
          "doctor":{"schema_version":"doctor.v1","success":true,"code":"verification_completed","evaluation":{
            "source_surface":"claude-code","primary_state":{"schema_version":"doctor.v1","state_code":"first_trace_ready","severity":"info",
            "source_surface":"claude-code","evidence_refs":["ev:<b>unsafe</b>"],"reason_codes":["first_trace_ready"],"next_action":"open_verified_trace_or_session",
            "retryability":"none","observed_at":"2026-07-21T00:00:00.0000000Z","verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678"},
            "states":[],"missing_fact_families":[]},"verification":{"verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678",
            "expected_source_surface":"claude-code","expected_source_adapter":"claude-code","state":"completed","revision":2,
            "started_at":"2026-07-21T00:00:00.0000000Z","expires_at":"2026-07-21T00:10:00.0000000Z",
            "completed_at":"2026-07-21T00:01:00.0000000Z","cancelled_at":null,"accepted_evidence_refs":["ev:<b>unsafe</b>"]}},
          "evaluation_preview":null,"guidance":[],"candidates":[],"truncated":false},
          "navigation_targets":[{"evidence_ref":"ev:<b>unsafe</b>","target_kind":"trace","target_id":"trace-exact-1","href":"/traces/trace-exact-1"}]}
        """;

    private static string ActiveVerificationJson => VerificationJson
        .Replace("\"state\":\"completed\"", "\"state\":\"active\"", StringComparison.Ordinal)
        .Replace("\"completed_at\":\"2026-07-21T00:01:00.0000000Z\"", "\"completed_at\":null", StringComparison.Ordinal);

    private static string ActiveWithCandidatesJson => ActiveVerificationJson.Replace(
        "\"candidates\":[]",
        "\"candidates\":[{\"candidate_id\":\"018f0c57-7b34-7cc3-8a13-8a90a2345679\",\"evidence_class\":\"real_source\",\"evidence_kind\":\"ingest\",\"source_surface\":\"claude-code\",\"source_adapter\":\"claude-code\",\"evidence_ref\":\"evidence:one\",\"observed_at\":\"2026-07-21T00:00:01.0000000Z\",\"expires_at\":\"2026-07-21T00:10:00.0000000Z\"},{\"candidate_id\":\"018f0c57-7b34-7cc3-8a13-8a90a2345680\",\"evidence_class\":\"real_source\",\"evidence_kind\":\"projection\",\"source_surface\":\"claude-code\",\"source_adapter\":\"claude-code\",\"evidence_ref\":\"evidence:<img src=x onerror=alert(1)>\",\"observed_at\":\"2026-07-21T00:00:02.0000000Z\",\"expires_at\":\"2026-07-21T00:10:00.0000000Z\"}]",
        StringComparison.Ordinal);

    private static string CancelledVerificationJson => TerminalVerificationJson("cancelled");

    private static string TerminalVerificationJson(string lifecycle) => ActiveVerificationJson
        .Replace("\"state\":\"active\"", $"\"state\":\"{lifecycle}\"", StringComparison.Ordinal)
        .Replace("\"cancelled_at\":null", lifecycle == "cancelled"
            ? "\"cancelled_at\":\"2026-07-21T00:02:00.0000000Z\""
            : "\"cancelled_at\":null", StringComparison.Ordinal);

    private static string MatrixVerificationJson(
        string state,
        string severity,
        string nextAction,
        string retryability,
        string source) => VerificationJson
            .Replace("first_trace_ready", state, StringComparison.Ordinal)
            .Replace("\"severity\":\"info\"", $"\"severity\":\"{severity}\"", StringComparison.Ordinal)
            .Replace("open_verified_trace_or_session", nextAction, StringComparison.Ordinal)
            .Replace("\"retryability\":\"none\"", $"\"retryability\":\"{retryability}\"", StringComparison.Ordinal)
            .Replace("claude-code", source, StringComparison.Ordinal);
}

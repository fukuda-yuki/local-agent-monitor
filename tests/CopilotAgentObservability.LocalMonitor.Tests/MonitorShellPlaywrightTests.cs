using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Local Monitor v1 shared shell: full-width breadcrumb header, receiver and
/// Settings actions, and the extension-only Unified Settings modal host.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public class MonitorShellPlaywrightTests
{
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string ComparisonId = "018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073";
    private const string ExecutionId = "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6074";
    private const string AnalysisId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6075";
    private const string NodeId = "node-0123456789abcdef0123456789abcdef";
    private const string Cursor = "AZvvJSfubUCDILx2dEkk4j_S1wLGQUOW4o1TpZMGBmrYAAAAAZ_mZOZ7MDE4ZjJiNGUtN2MxYS03ZjFhLTlhMmItNmMzZDRlNWY2MDcyZb_UESMy6-2NWv8kzNcu3qwsgZxvWyIdPDe5nrnqQaw";

    [Fact]
    public async Task SharedPaths_MatchCanonicalBuildersAndRejectEveryNonIdentityWithoutSideEffects()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{host.Url}/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var result = await page.EvaluateAsync<string[]>(
            $$"""
            () => {
              const paths = window.LocalMonitorV1Paths;
              const snapshot = () => JSON.stringify({
                href: location.href,
                historyLength: history.length,
                historyState: history.state,
                localStorage: [...Object.entries(localStorage)],
                sessionStorage: [...Object.entries(sessionStorage)]
              });
              const before = snapshot();
              const outputs = [
                paths.repositorySelection(),
                paths.repositorySessions("{{RepositoryId}}"),
                paths.allSessions(),
                paths.unassignedSessions(),
                paths.session("{{SessionId}}"),
                paths.comparison("{{RepositoryId}}", "{{ComparisonId}}"),
                paths.comparison("{{SessionId}}", "{{SessionId}}")
              ];
              const invalid = [
                null,
                undefined,
                42,
                "",
                "{{SessionId.ToUpperInvariant()}}",
                "018f2b4e-7c1a-4f1a-9a2b-6c3d4e5f6072",
                " {{SessionId}}",
                "/sessions/{{SessionId}}",
                "https://monitor.invalid/{{SessionId}}",
                "Repository Name",
                "owner/repository"
              ];
              const attempts = invalid.flatMap(value => [
                () => paths.repositorySessions(value),
                () => paths.session(value),
                () => paths.comparison(value, "{{ComparisonId}}"),
                () => paths.comparison("{{RepositoryId}}", value)
              ]);
              attempts.push(
                () => paths.repositorySelection("extra"),
                () => paths.allSessions("extra"),
                () => paths.unassignedSessions("extra"),
                () => paths.repositorySessions(),
                () => paths.repositorySessions("{{RepositoryId}}", "extra"),
                () => paths.session(),
                () => paths.session("{{SessionId}}", "extra"),
                () => paths.comparison("{{RepositoryId}}"),
                () => paths.comparison("{{RepositoryId}}", "{{ComparisonId}}", "extra")
              );
              const rejected = attempts.every(attempt => {
                try { attempt(); return false; }
                catch { return true; }
              });
              return [
                ...outputs,
                String(Object.isFrozen(paths)),
                Object.keys(paths).join(","),
                String(rejected),
                String(before === snapshot())
              ];
            }
            """);

        Assert.Equal(
            [
                "/",
                $"/repositories/{RepositoryId}/sessions",
                "/sessions",
                "/sessions/unassigned",
                $"/sessions/{SessionId}",
                $"/repositories/{RepositoryId}/comparisons/{ComparisonId}",
                $"/repositories/{SessionId}/comparisons/{SessionId}",
                "true",
                "repositorySelection,repositorySessions,allSessions,unassignedSessions,session,comparison",
                "true",
                "true",
            ],
            result);

        foreach (var path in new[]
                 {
                     "/",
                     $"/repositories/{RepositoryId}/sessions",
                     "/sessions",
                     "/sessions/unassigned",
                     $"/sessions/{SessionId}",
                     $"/repositories/{RepositoryId}/comparisons/{ComparisonId}",
                 })
        {
            await page.GotoAsync(host.Url + path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Assert.Equal(
                "/sessions",
                await page.EvaluateAsync<string>("() => window.LocalMonitorV1Paths.allSessions()"));
        }
    }

    [Fact]
    public async Task SharedHistory_RestoresOnlyCanonicalUrlSafeStateAndSettings()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var consoleMessages = new List<string>();
        page.Console += (_, message) => consoleMessages.Add(message.Text);

        await page.GotoAsync($"{host.Url}/sessions?source=vscode&status=failed", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForFunctionAsync(
            "() => typeof window.LocalMonitorV1History?.push === 'function' && typeof window.LocalMonitorV1FactState?.render === 'function'");
        await page.EvaluateAsync(
            """
            () => window.LocalMonitorV1History.push({
              source: ["claude-code", "vscode"],
              has_error: "true",
              settings: "storage"
            })
            """);

        Assert.Equal(
            "/sessions?source=claude-code&source=vscode&status=failed&has_error=true&settings=storage",
            await page.EvaluateAsync<string>("() => location.pathname + location.search"));
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();

        await page.Locator("#settings-modal-close").ClickAsync();
        Assert.Equal(
            "/sessions?source=claude-code&source=vscode&status=failed&has_error=true",
            await page.EvaluateAsync<string>("() => location.pathname + location.search"));
        await page.GoBackAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await page.GoForwardAsync();
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await page.GoBackAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();

        var rejected = await page.EvaluateAsync<bool>(
            """
            () => {
              for (const key of ["q", "model", "limit", "draft", "raw", "repository_path"]) {
                try { window.LocalMonitorV1History.push({ [key]: "secret" }); return false; }
                catch { }
              }
              return true;
            }
            """);
        Assert.True(rejected);
        Assert.DoesNotContain("secret", await page.EvaluateAsync<string>("() => location.href"), StringComparison.Ordinal);
        Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('q')"));
        Assert.Null(await page.EvaluateAsync<string?>("() => sessionStorage.getItem('q')"));
        Assert.DoesNotContain(consoleMessages, message => message.Contains("secret", StringComparison.Ordinal));

        var rendered = await page.EvaluateAsync<string[]>(
            """
            () => {
              const target = document.createElement("div");
              document.body.append(target);
              const result = window.LocalMonitorV1FactState.render(target, {
                state: "projection_invalid",
                reasonText: "投影結果を安全に表示できません。"
              });
              return [result.primaryText, target.textContent];
            }
            """);
        Assert.Equal("記録が一部欠けています", rendered[0]);
        Assert.DoesNotContain("projection_invalid", rendered[1], StringComparison.OrdinalIgnoreCase);

        var zeroStates = await page.EvaluateAsync<string[]>(
            """
            () => {
              const unproved = document.createElement("div");
              const unprovedResult = window.LocalMonitorV1FactState.render(unproved, {
                state: "observed_zero",
                recordedCount: 0,
                hasCompleteCoverageProof: false
              });
              let missingRejected = false;
              try {
                window.LocalMonitorV1FactState.render(document.createElement("div"), {
                  state: "observed_zero",
                  hasCompleteCoverageProof: true,
                  sourceText: "GitHub Copilot CLI",
                  reasonText: "対象記録を完全に確認しました。"
                });
              } catch { missingRejected = true; }
              return [unprovedResult.primaryText, unproved.textContent, String(missingRejected)];
            }
            """);
        Assert.Equal("今回の記録にはありません", zeroStates[0]);
        Assert.Contains("実際に使われなかったとは断定できません", zeroStates[1], StringComparison.Ordinal);
        Assert.Equal("true", zeroStates[2]);

        var factParity = await page.EvaluateAsync<string[]>(
            """
            () => {
              const attempt = fact => {
                try {
                  const result = window.LocalMonitorV1FactState.render(document.createElement("div"), fact);
                  return `accepted:${result.primaryText}`;
                } catch { return "rejected"; }
              };
              return [
                attempt({
                  state: "unsupported",
                  sourceText: "Unsupported provider",
                  reasonText: "この取得元には対象記録がありません。"
                }),
                attempt({
                  state: "unsupported",
                  sourceText: "ExpiredPendingDeletion",
                  reasonText: "この取得元には対象記録がありません。"
                }),
                attempt({ state: "observed_positive", recordedCount: 1, hasCompleteCoverageProof: false }),
                attempt({ state: "observed_positive", recordedCount: 1, hasCompleteCoverageProof: true }),
                attempt({ state: "observed_positive", recordedCount: 1, hasCompleteCoverageProof: "false" }),
                attempt({ state: "observed_zero", recordedCount: 0, hasCompleteCoverageProof: "true" }),
                attempt({
                  state: "observed_zero",
                  recordedCount: 1,
                  hasCompleteCoverageProof: true,
                  sourceText: "GitHub Copilot CLI",
                  reasonText: "対象記録を完全に確認しました。"
                }),
                attempt({
                  state: "unsupported",
                  hasCompleteCoverageProof: true,
                  sourceText: "GitHub Copilot CLI",
                  reasonText: "この取得元には対象記録がありません。"
                })
              ];
            }
            """);
        Assert.Equal(
            [
                "accepted:この取得元では記録できません",
                "rejected",
                "accepted:1件を記録",
                "rejected",
                "rejected",
                "rejected",
                "rejected",
                "rejected",
            ],
            factParity);

        foreach (var rejectedQuery in new[] { "?settings=AI", "?unknown=secret", "?settings=ai&settings=storage" })
        {
            var rejectedQueryPage = await browser.NewPageAsync();
            await rejectedQueryPage.GotoAsync(
                $"{host.Url}/sessions{rejectedQuery}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Assert.Equal(rejectedQuery, await rejectedQueryPage.EvaluateAsync<string>("() => location.search"));
            Assert.False(await rejectedQueryPage.EvaluateAsync<bool>("() => 'LocalMonitorV1History' in window"));
            Assert.False(await rejectedQueryPage.EvaluateAsync<bool>("() => 'LocalMonitorV1Paths' in window"));
            await Expect(rejectedQueryPage.Locator("script[src='/local-monitor-v1-shared.js']")).ToHaveCountAsync(0);
            await Expect(rejectedQueryPage.Locator("[data-route-kind], [data-requested-section], [data-execution-id], [data-node-id], [data-analysis-id]")).ToHaveCountAsync(0);
            var visibleText = await rejectedQueryPage.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("settings=AI", visibleText, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", visibleText, StringComparison.Ordinal);
            Assert.DoesNotContain("settings=storage", visibleText, StringComparison.Ordinal);
        }

        var explorerPage = await browser.NewPageAsync();
        var explorerResponse = await explorerPage.GotoAsync(
            $"{host.Url}/sessions?settings=ai",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.Equal(200, explorerResponse?.Status);
        Assert.Equal("?settings=ai", await explorerPage.EvaluateAsync<string>("() => location.search"));
        Assert.True(await explorerPage.EvaluateAsync<bool>("() => 'LocalMonitorV1History' in window"));
        Assert.True(await explorerPage.EvaluateAsync<bool>("() => 'LocalMonitorV1Paths' in window"));
        await Expect(explorerPage.Locator("script[src='/local-monitor-v1-shared.js']")).ToHaveCountAsync(1);
        await Expect(explorerPage.Locator("script[src='/local-monitor-explorer.js']")).ToHaveCountAsync(1);
        await Expect(explorerPage.Locator("[data-route-kind='AllSessions']")).ToHaveCountAsync(1);
        await Expect(explorerPage.Locator("[data-session-explorer]")).ToHaveCountAsync(1);
        await Expect(explorerPage.Locator("#settings-modal[data-requested-section='ai']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SharedHistory_MatchesTheAuthoritativeExplorerAndSessionBuildersAndRejectsUnprovedState()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var explorer = await browser.NewPageAsync();
        await explorer.GotoAsync($"{host.Url}/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await explorer.EvaluateAsync(
            $$"""
            () => window.LocalMonitorV1History.push({
              from: "2026-08-01T00:00:00.0000000+00:00",
              to: "2026-08-09T12:00:00.0000000+00:00",
              source: ["vscode", "claude-code"],
              status: ["failed"],
              has_skill: "true",
              has_subagent: "true",
              has_error: "false",
              has_retry: "false",
              archive_scope: "include_archived",
              cursor: "{{Cursor}}",
              mode: "compare",
              settings: "storage"
            }, { q: null, model: [], limit: null })
            """);
        Assert.Equal(
            $"/sessions?from=2026-08-01T00:00:00.0000000%2B00:00&to=2026-08-09T12:00:00.0000000%2B00:00&source=claude-code&source=vscode&status=failed&has_skill=true&has_subagent=true&has_error=false&has_retry=false&archive_scope=include_archived&cursor={Cursor}&mode=compare&settings=storage",
            await explorer.EvaluateAsync<string>("() => location.pathname + location.search"));

        var invalidExplorerStates = await explorer.EvaluateAsync<string[]>(
            $$"""
            () => {
              const attempts = [
                () => window.LocalMonitorV1History.push({ workspace_revision: "{{new string('1', 64)}}" }),
                () => window.LocalMonitorV1History.push({ from: "2026-02-30T00:00:00.0000000+00:00" }),
                () => window.LocalMonitorV1History.push({ from: "2026-08-09T12:00:00.0000000+00:00", to: "2026-08-01T00:00:00.0000000+00:00" }),
                () => window.LocalMonitorV1History.push({ cursor: "{{Cursor}}" }),
                () => window.LocalMonitorV1History.push({ cursor: "{{Cursor}}" }, { q: "secret", model: [], limit: null }),
                () => window.LocalMonitorV1History.push({ cursor: "{{Cursor}}" }, { q: null, model: ["model-a"], limit: null }),
                () => window.LocalMonitorV1History.push({ cursor: "{{Cursor}}" }, { q: null, model: [], limit: 50 }),
                () => window.LocalMonitorV1History.push({ cursor: "{{new string('A', 147)}}" }, { q: null, model: [], limit: null })
              ];
              return attempts.map(attempt => { try { attempt(); return "accepted"; } catch { return "rejected"; } });
            }
            """);
        Assert.All(invalidExplorerStates, state => Assert.Equal("rejected", state));
        Assert.DoesNotContain("secret", await explorer.EvaluateAsync<string>("() => JSON.stringify(history.state)"), StringComparison.Ordinal);

        var structuralCursorParity = await explorer.EvaluateAsync<string[]>(
            $$"""
            () => {
              const mutate = mutateBytes => {
                const binary = atob("{{Cursor}}".replaceAll("-", "+").replaceAll("_", "/") + "=");
                const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
                mutateBytes(bytes);
                let encoded = "";
                for (const value of bytes) encoded += String.fromCharCode(value);
                return btoa(encoded).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
              };
              const candidates = [
                mutate(bytes => { bytes[0] = 2; }),
                mutate(bytes => { bytes[33] = 2; }),
                mutate(bytes => { bytes[33] = 1; bytes[41] = 1; }),
                mutate(bytes => { bytes[56] = "6".charCodeAt(0); })
              ];
              return candidates.map(cursor => {
                try {
                  window.LocalMonitorV1History.push({ cursor }, { q: null, model: [], limit: null });
                  return "accepted";
                } catch { return "rejected"; }
              });
            }
            """);
        Assert.All(structuralCursorParity, state => Assert.Equal("rejected", state));

        var session = await browser.NewPageAsync();
        await session.GotoAsync(
            $"{host.Url}/sessions/{SessionId}?node={NodeId}&execution={ExecutionId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await session.EvaluateAsync(
            $$"""
            () => window.LocalMonitorV1History.push({
              analysis: "{{AnalysisId}}",
              settings: "ai"
            })
            """);
        Assert.Equal(
            $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}&analysis={AnalysisId}&settings=ai",
            await session.EvaluateAsync<string>("() => location.pathname + location.search"));
    }

    [Fact]
    public async Task Shell_AtReferenceViewport_UsesNoSidebarHeaderGeometry()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });

        const string untrustedBreadcrumb = "<img src=x onerror=window.__breadcrumbExecuted=true>";
        await page.GotoAsync(
            $"{host.Url}/?breadcrumb={Uri.EscapeDataString(untrustedBreadcrumb)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var header = page.Locator(".monitor-shell-header");
        await Expect(header).ToHaveCSSAsync("height", "48px");
        await Expect(page.Locator(".monitor-content")).ToHaveCSSAsync("padding-left", "24px");
        await Expect(page.Locator(".monitor-content")).ToHaveCSSAsync("padding-right", "24px");
        await Expect(page.Locator(".monitor-sidebar, .sidebar-nav")).ToHaveCountAsync(0);
        await Expect(header.Locator("a, input, [role='search'], [data-ai-status], .kpi-grid")).ToHaveCountAsync(0);
        await Expect(header.Locator("button")).ToHaveCountAsync(2);
        await Expect(page.Locator("#shell-breadcrumb[data-breadcrumb-host]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#shell-breadcrumb a")).ToHaveCountAsync(0);
        await Expect(page.Locator("#shell-breadcrumb")).ToHaveTextAsync("Local Monitor");
        await Expect(page.Locator("#shell-breadcrumb img, #shell-breadcrumb script")).ToHaveCountAsync(0);
        Assert.False(await page.EvaluateAsync<bool>("() => window.__breadcrumbExecuted === true"));
        await Expect(page.Locator("#receiver-status-action")).ToContainTextAsync("正常 · 受信中");
        await Expect(page.Locator("#settings-action")).ToHaveTextAsync("設定");

        var positions = await page.EvaluateAsync<float[]>(
            """
            () => {
              const breadcrumb = document.querySelector("#shell-breadcrumb").getBoundingClientRect();
              const actions = document.querySelector(".monitor-shell-actions").getBoundingClientRect();
              return [breadcrumb.left, breadcrumb.right, actions.left, actions.right];
            }
            """);
        Assert.Equal(24f, positions[0]);
        Assert.True(positions[1] <= positions[2]);
        Assert.Equal(1342f, positions[3]);
    }

    [Fact]
    public async Task Shell_SettingsActions_OpenRequestedExtensionHooks()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync(
            """
            () => {
              window.__settingsRequests = [];
              document.addEventListener("cao-settings-open", event => {
                window.__settingsRequests.push(event.detail.section ?? null);
              });
            }
            """);

        var modal = page.Locator("#settings-modal");
        await Expect(modal).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(modal).ToHaveAttributeAsync("aria-labelledby", "settings-modal-title");
        await Expect(modal.Locator("[data-settings-navigation-host]")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-settings-content-host]")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-repository-settings-navigation]")).ToHaveCountAsync(2);
        await Expect(modal.Locator("[data-repository-settings-section]")).ToHaveCountAsync(2);
        await Expect(modal.Locator("#repository-management-result")).ToHaveCountAsync(1);

        await page.Locator("#receiver-status-action").ClickAsync();

        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal).ToHaveAttributeAsync("data-requested-section", "receiver");
        await Expect(page.Locator(".monitor-content")).ToBeVisibleAsync();
        var modalSize = await modal.EvaluateAsync<float[]>(
            "element => { const rect = element.getBoundingClientRect(); return [rect.width, rect.height]; }");
        Assert.Equal(960f, modalSize[0]);
        Assert.Equal(640f, modalSize[1]);

        await page.Locator("#settings-modal-close").ClickAsync();
        await page.Locator("#settings-action").ClickAsync();

        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal).Not.ToHaveAttributeAsync("data-requested-section", "receiver");
        var requests = await page.EvaluateAsync<string?[]>("() => window.__settingsRequests");
        Assert.Equal(2, requests.Length);
        Assert.Equal("receiver", requests[0]);
        Assert.Null(requests[1]);
    }

    [Fact]
    public async Task Shell_UnifiedSettings_ComposesSevenSectionsOnEveryPrimaryPage()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        await page.RouteAsync("**/api/local-monitor/v1/settings/storage", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 503,
            ContentType = "application/json",
            Body = "{\"error\":\"SYNTHETIC_SECRET_PATH\"}",
        }));

        await page.GotoAsync($"{host.Url}/sessions?settings=storage", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var modal = page.Locator("#settings-modal");
        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal.Locator("[data-settings-navigation]")).ToHaveCountAsync(7);
        await Expect(modal.Locator("[data-settings-navigation='storage']")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(modal.Locator("[data-settings-section='storage']")).ToBeVisibleAsync();
        await Expect(modal.Locator("[data-settings-section='storage'] a[href='/backup-restore']")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-settings-section='storage'] a[href='/diagnostics#retention-diagnostics']")).ToHaveCountAsync(1);
        await Expect(modal.Locator("[data-settings-section='storage'] a[href='/historical-import']")).ToHaveCountAsync(1);
        await Expect(modal).Not.ToContainTextAsync("SYNTHETIC_SECRET_PATH");
        await Expect(modal).ToContainTextAsync("保存状態を読み込めませんでした。");

        await modal.Locator("[data-settings-navigation='diagnostics']").ClickAsync();
        Assert.Equal("/sessions?settings=diagnostics", await page.EvaluateAsync<string>("() => location.pathname + location.search"));
        await Expect(modal.Locator("[data-settings-section='diagnostics'] a[href='/diagnostics']")).ToHaveCountAsync(1);
        await page.GoBackAsync();
        await Expect(modal.Locator("[data-settings-section='storage']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Shell_UnifiedSettings_SourceDiagnosticsTransitionsClearStaleSharedState()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var sourceResponse = "empty";
        await page.RouteAsync("**/api/monitor/source-diagnostics?limit=1", route => route.FulfillAsync(new()
        {
            Status = sourceResponse == "error" ? 503 : 200,
            ContentType = "application/json",
            Body = sourceResponse == "supported"
                ? "{\"items\":[{\"observation_id\":\"observation-1\",\"ingest_batch_id\":\"batch-1\",\"source_surface\":\"claude-code\",\"source_application_version\":null,\"source_adapter\":\"claude-code-otel\",\"adapter_version\":\"1\",\"schema_fingerprint\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"inventory_hash\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"compatibility_state\":\"supported\",\"reason_codes\":[],\"unknown_span_count\":0,\"unknown_event_count\":0,\"unknown_attribute_count\":0,\"observed_at\":\"2026-08-30T01:00:00.0000000+00:00\",\"next_action\":\"none\"}],\"next_cursor\":null}"
                : "{\"items\":[],\"next_cursor\":null}",
        }));

        await page.GotoAsync($"{host.Url}/sessions?settings=receiver", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var receiver = page.Locator("[data-settings-receiver-source]");
        await Expect(receiver.Locator("[data-fact-state='not-observed']")).ToHaveCountAsync(1);
        await Expect(receiver).ToContainTextAsync("今回の記録にはありません");
        await Expect(receiver).ToContainTextAsync("実際に使われなかったとは断定できません");
        await Expect(receiver).Not.ToContainTextAsync("not_observed");
        await Expect(receiver).Not.ToContainTextAsync("0件");
        Assert.False(await receiver.EvaluateAsync<bool>("node => Boolean(node.querySelector('p p, button button, a a, button a, a button'))"));

        sourceResponse = "supported";
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='receiver']").ClickAsync();
        await Expect(receiver).ToContainTextAsync("取得元の互換性を確認済みです。");
        await Expect(receiver.Locator("[data-fact-state]")).ToHaveCountAsync(0);
        await Expect(receiver).Not.ToContainTextAsync("今回の記録にはありません");

        sourceResponse = "empty";
        await page.Locator("[data-settings-navigation='diagnostics']").ClickAsync();
        var diagnostics = page.Locator("[data-settings-diagnostics-source]");
        await Expect(diagnostics.Locator("[data-fact-state='not-observed']")).ToHaveCountAsync(1);

        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='receiver']").ClickAsync();
        await Expect(receiver.Locator("[data-fact-state='not-observed']")).ToHaveCountAsync(1);

        sourceResponse = "error";
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='diagnostics']").ClickAsync();
        await Expect(diagnostics).ToContainTextAsync("取得元の状態を読み込めませんでした。");
        await Expect(diagnostics.Locator("[data-fact-state]")).ToHaveCountAsync(0);
        await Expect(diagnostics).Not.ToContainTextAsync("今回の記録にはありません");
    }

    [Fact]
    public async Task Shell_UnifiedSettings_UsesExactOwnerTransportsWithoutRenderingHostileFields()
    {
        const string archivedSession = "01890f65-4c31-7f42-8a7d-111111111111";
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);
        await page.RouteAsync("**/health/ready", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"status\":\"degraded\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":1,\"projection_backlog\":7,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[\"projection_lag\"]}",
        }));
        await page.RouteAsync("**/api/session-workspace/status", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"schema_version\":1,\"normalizer_status\":\"degraded\",\"unsupported_event_version_count\":0,\"projection_cursor\":null,\"projection_backlog\":7}",
        }));
        IRequest? aiCheckRequest = null;
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", async route =>
        {
            if (route.Request.Method == "POST") aiCheckRequest = route.Request;
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = $"{{\"provider\":\"github_copilot\",\"selected_model\":\"gpt-5\",\"selected_configuration\":\"standard\",\"readiness_state\":\"{(route.Request.Method == "POST" ? "ready" : "configured_not_checked")}\",\"last_check_result\":\"{(route.Request.Method == "POST" ? "ready" : "not_checked")}\",\"provider_egress_notice\":\"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action\"}}",
            });
        });
        await page.RouteAsync("**/api/monitor/source-diagnostics?limit=1", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"items\":[{\"observation_id\":\"SECRET_ID\",\"ingest_batch_id\":\"SECRET_BATCH\",\"source_surface\":\"SECRET_SOURCE\",\"source_application_version\":null,\"source_adapter\":\"SECRET_ADAPTER\",\"adapter_version\":\"SECRET_VERSION\",\"schema_fingerprint\":\"SECRET_SCHEMA\",\"inventory_hash\":\"SECRET_HASH\",\"compatibility_state\":\"supported\",\"reason_codes\":[],\"unknown_span_count\":0,\"unknown_event_count\":0,\"unknown_attribute_count\":0,\"observed_at\":\"SECRET_TIME\",\"next_action\":\"none\"}],\"next_cursor\":null}",
        }));
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{archivedSession}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":null}""",
        }));
        await page.RouteAsync("**/api/local-monitor/v1/repositories?archive_scope=include_archived&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"schema_version\":\"local-monitor-repositories.response.v1\",\"workspace_revision\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"repositories\":[],\"all_session_count\":4,\"unassigned_active_session_count\":2,\"archived_repository_count\":1,\"next_cursor\":null}",
        }));
        IRequest? restoreRequest = null;
        await page.RouteAsync("**/api/local-monitor/v1/archive-actions", async route =>
        {
            restoreRequest = route.Request;
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = $$"""{"schema_version":"local-archive-action.response.v1","action":"restore","target_kind":"session","targets":[{"target_id":"{{archivedSession}}","state":"active","revision":2,"archived_at":null,"updated_at":"2026-08-09T12:35:56.1234567+00:00"}]}""",
            });
        });
        await page.RouteAsync("**/api/retention/v1/status", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"schema_version\":1,\"pending_count\":2,\"queued_count\":0,\"deleting_count\":0,\"failed_count\":1,\"retry_exhausted_count\":1,\"orphan_or_unexpected_missing_count\":0,\"expired_but_readable_violation_count\":0,\"oldest_pending_age_seconds\":3,\"worker_state\":\"idle\",\"last_successful_run_at\":null,\"inventory_version\":1,\"adapter_coverage_version\":1,\"items\":[]}",
        }));
        await page.RouteAsync("**/api/local-monitor/v1/settings/runtime", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"application_started_at\":\"2026-08-30T01:02:03.0000000+00:00\",\"receiver_readiness\":\"degraded\",\"endpoint\":{\"transport\":\"http\",\"scope\":\"loopback\",\"port\":4320},\"activity_state\":\"available\",\"latest_received_at\":\"2026-08-30T01:06:00.0000000+00:00\",\"recent_received_count\":4,\"projection_backlog\":7,\"capture_reasons\":[\"ingestion_backpressure\"],\"projection_reasons\":[\"projection_lag\"],\"restart_requirement\":\"unavailable\"}",
        }));
        var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/settings/storage", async route =>
        {
            await releaseHistory.Task;
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = "{\"schema_version\":\"settings-storage-summary.v1\",\"database_file_size_bytes\":4096,\"retention\":{\"state\":\"idle\"},\"backup\":{\"state\":\"idle\",\"last_successful_at\":null,\"validation_state\":\"unknown\"},\"historical_import\":{\"state\":\"running\"},\"restart_requirement\":\"not_required\"}",
            });
        });
        IRequest? backupRequest = null;
        var backupCalls = 0;
        await page.RouteAsync("**/api/runtime-backup/v1/backups", async route =>
        {
            backupRequest = route.Request;
            backupCalls++;
            await route.FulfillAsync(new()
            {
                Status = 201, ContentType = "application/json",
                Body = backupCalls == 1
                    ? "{\"backup_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"error_code\":null,\"archive_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"warnings\":[\"raw_content_included\",\"not_repository_safe\",\"retention_backup_not_purged\"],\"download_path\":\"/api/runtime-backup/v1/backups/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/archive\"}"
                    : "{\"backup_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"error_code\":null,\"archive_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"warnings\":[\"hostile-warning\"],\"download_path\":\"/api/runtime-backup/v1/backups/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/archive\"}",
            });
        });

        await page.GotoAsync($"{host.Url}/sessions?settings=receiver", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-settings-receiver-health]")).ToContainTextAsync("受信状態に注意が必要です");
        await Expect(page.Locator("[data-settings-receiver-health]")).ToContainTextAsync("投影に遅れがあります");
        await Expect(page.Locator("[data-settings-section='receiver']")).ToContainTextAsync("投影待ち 7件");
        await Expect(page.Locator("[data-settings-receiver-source]")).ToContainTextAsync("互換性を確認済み");
        await Expect(page.Locator("[data-settings-receiver-runtime]")).ToContainTextAsync("開始 2026-08-30 01:02:03 UTC");
        await Expect(page.Locator("[data-settings-receiver-runtime]")).ToContainTextAsync("受信先 HTTP · ループバック · ポート 4320");
        await Expect(page.Locator("[data-settings-receiver-runtime]")).ToContainTextAsync("直近5分 4件");
        await Expect(page.Locator("[data-settings-receiver-runtime]")).ToContainTextAsync("最新受信 2026-08-30 01:06:00 UTC");
        await Expect(page.Locator("[data-settings-receiver-runtime]")).ToContainTextAsync("再起動要否は確認できません");
        await Expect(page.Locator("#settings-modal")).Not.ToContainTextAsync("SECRET_");
        Assert.Contains(requestedUrls, url => url.EndsWith("/api/monitor/source-diagnostics?limit=1", StringComparison.Ordinal));
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await Expect(page.Locator("[data-settings-state-receiver]")).Not.ToContainTextAsync("確認しています");
        await Expect(page.Locator("[data-settings-state-projection]")).ToContainTextAsync("投影待ち 7件");
        await Expect(page.Locator("[data-settings-state-ai]")).ToContainTextAsync("未確認");
        await Expect(page.Locator("[data-settings-state-ai]")).Not.ToContainTextAsync("gpt-5");
        await Expect(page.Locator("[data-settings-state-data]")).ToContainTextAsync("保留 2件");
        await page.Locator("[data-settings-navigation='ai']").ClickAsync();
        await Expect(page.Locator("[data-settings-section='ai']")).ToContainTextAsync("gpt-5");
        await Expect(page.Locator("[data-settings-section='ai']")).ToContainTextAsync("未確認");
        await page.Locator("[data-settings-ai-check]").ClickAsync();
        await Expect(page.Locator("[data-settings-section='ai']")).ToContainTextAsync("接続できます");
        Assert.NotNull(aiCheckRequest);
        Assert.Equal("local-monitor", aiCheckRequest.Headers["x-monitor-csrf"]);
        await page.Locator("[data-settings-navigation='archive']").ClickAsync();
        await Expect(page.Locator("[data-archived-session-id]")).ToHaveTextAsync(archivedSession);
        await Expect(page.Locator("[data-archived-session-id]")).ToHaveAttributeAsync("href", $"/sessions/{archivedSession}");
        await page.Locator("[data-settings-archived-sessions]").GetByRole(AriaRole.Button, new() { Name = "復元", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-settings-archived-sessions]")).ToContainTextAsync("0件を表示しています");
        Assert.NotNull(restoreRequest);
        Assert.Equal("POST", restoreRequest.Method);
        Assert.Equal("application/json", restoreRequest.Headers["content-type"]);
        Assert.Equal("local-monitor", restoreRequest.Headers["x-monitor-csrf"]);
        Assert.Equal($$"""{"schema_version":"local-archive-action.v1","action":"restore","target_kind":"session","targets":[{"target_id":"{{archivedSession}}","expected_revision":1}]}""", restoreRequest.PostData);
        releaseHistory.TrySetResult();
        await page.Locator("[data-settings-navigation='storage']").ClickAsync();
        await Expect(page.Locator("[data-settings-section='storage']")).ToContainTextAsync("保持 待機中");
        await Expect(page.Locator("[data-settings-section='storage']")).ToContainTextAsync("データベース 4096バイト");
        await Expect(page.Locator("[data-settings-section='storage']")).ToContainTextAsync("直近の履歴取り込みは実行中です");
        await Expect(page.Locator("[data-settings-section='storage']")).Not.ToContainTextAsync("running");
        await Expect(page.Locator("[data-settings-section='storage']")).ToContainTextAsync("自動バックアップ: この画面では確認できません。");
        Assert.Contains(requestedUrls, url => url.EndsWith("/api/local-monitor/v1/settings/storage", StringComparison.Ordinal));
        await Expect(page.Locator("[data-settings-section='storage'] a[href='/diagnostics#retention-diagnostics']")).ToHaveCountAsync(1);
        await page.Locator("[data-settings-backup-now]").ClickAsync();
        await Expect(page.Locator("[data-settings-backup-download]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-settings-backup-result]")).ToContainTextAsync("生の記録を含むため、リポジトリへ保存せず安全な場所で管理してください。保持処理では削除されません。");
        await Expect(page.Locator("[data-settings-backup-download]")).ToBeVisibleAsync();
        await page.Locator("[data-settings-navigation='diagnostics']").ClickAsync();
        await Expect(page.Locator("[data-settings-diagnostics-health]")).ToContainTextAsync("受信状態に注意が必要です");
        await Expect(page.Locator("[data-settings-diagnostics-projection]")).ToContainTextAsync("投影待ち 7件");
        await Expect(page.Locator("[data-settings-diagnostics-source]")).ToContainTextAsync("互換性を確認済み");
        await Expect(page.Locator("[data-settings-diagnostics-repositories]")).ToContainTextAsync("先頭ページ 0件 · アーカイブ 1件 · リポジトリ未設定のセッション 2件");
        await Expect(page.Locator("[data-settings-section='diagnostics'] a[href='/diagnostics']")).ToHaveCountAsync(1);
        await page.Locator("[data-settings-navigation='storage']").ClickAsync();
        await page.Locator("[data-settings-backup-now]").ClickAsync();
        await Expect(page.Locator("[data-settings-backup-result]")).ToHaveTextAsync("バックアップを作成できませんでした。");
        await Expect(page.Locator("#settings-modal")).Not.ToContainTextAsync("hostile-warning");
        Assert.NotNull(backupRequest);
        Assert.Equal("POST", backupRequest.Method);
        Assert.Equal("{}", backupRequest.PostData);
        Assert.Equal("application/json", backupRequest.Headers["content-type"]);
        Assert.Equal("local-monitor", backupRequest.Headers["x-monitor-csrf"]);

        await page.UnrouteAsync("**/api/local-monitor/v1/settings/storage");
        await page.RouteAsync("**/api/local-monitor/v1/settings/storage", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"schema_version\":\"settings-storage-summary.v1\",\"hostile\":\"SECRET_TIMESTAMP\"}",
        }));
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='storage']").ClickAsync();
        await Expect(page.Locator("[data-settings-section='storage']")).ToContainTextAsync("保存状態を読み込めませんでした");
        await Expect(page.Locator("#settings-modal")).Not.ToContainTextAsync("SECRET_TIMESTAMP");

        await page.UnrouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50");
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{archivedSession}}","state":"archived","revision":2,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":null}""",
        }));
        await page.Locator("[data-settings-navigation='archive']").ClickAsync();
        await Expect(page.Locator("[data-settings-archived-sessions]")).ToContainTextAsync("読み込めませんでした");
        await Expect(page.Locator($"a[href='/sessions/{archivedSession}']")).ToHaveCountAsync(0);

        await page.UnrouteAsync("**/health/ready");
        await page.RouteAsync("**/health/ready", route => route.FulfillAsync(new()
        {
            Status = 503, ContentType = "application/json",
            Body = "{\"status\":\"degraded\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[\"SECRET_READINESS_REASON\"]}",
        }));
        await page.Locator("[data-settings-navigation='receiver']").ClickAsync();
        await Expect(page.Locator("[data-settings-receiver-health]")).ToContainTextAsync("受信状態を読み込めませんでした");
        await Expect(page.Locator("#settings-modal")).Not.ToContainTextAsync("SECRET_READINESS_REASON");
    }

    [Fact]
    public async Task Shell_UnifiedSettings_ArchivedSessionExactSearchUsesDirectSafeClosedStatesAndDirectRevision()
    {
        const string searchedSession = "01890f65-4c31-7f42-8a7d-333333333333";
        const string otherSession = "01890f65-4c31-7f42-8a7d-444444444444";
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var directCalls = 0;
        var mode = "found";
        IRequest? restoreRequest = null;
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = "{\"schema_version\":\"local-archived-items.response.v1\",\"target_kind\":\"session\",\"items\":[],\"next_cursor\":null}",
        }));
        await page.RouteAsync("**/api/local-monitor/v1/archive?target_kind=session&target_id=*", route =>
        {
            directCalls++;
            return mode switch
            {
                "found" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"{{searchedSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" }),
                "active" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"{{searchedSession}}","state":"active","revision":4,"archived_at":null,"updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" }),
                "missing" => route.FulfillAsync(new() { Status = 404, ContentType = "application/json", Body = "{\"error\":\"target_not_found\"}" }),
                "wrong-schema" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"hostile-schema","target_kind":"session","target_id":"{{searchedSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" }),
                "wrong-kind" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"repository","target_id":"{{searchedSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" }),
                "wrong-id" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"{{otherSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" }),
                "hostile-extra" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"{{searchedSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00","path":"SECRET_PATH","label":"SECRET_LABEL"}""" }),
                "busy" => route.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{\"error\":\"persistence_busy\"}" }),
                "malformed" => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "{\"schema_version\":\"SECRET_MALFORMED\"" }),
                "network" => route.AbortAsync("failed"),
                _ => route.FulfillAsync(new() { Status = 500, ContentType = "text/plain", Body = "SECRET_FAILURE" }),
            };
        });
        await page.RouteAsync("**/api/local-monitor/v1/archive-actions", async route =>
        {
            restoreRequest = route.Request;
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = $$"""{"schema_version":"local-archive-action.response.v1","action":"restore","target_kind":"session","targets":[{"target_id":"{{searchedSession}}","state":"active","revision":4,"archived_at":null,"updated_at":"2026-08-09T12:35:56.1234567+00:00"}]}""",
            });
        });

        await page.GotoAsync($"{host.Url}/sessions?settings=archive", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "アーカイブ済みセッションID" });
        var result = page.Locator("[data-settings-archived-session-search-result]");
        await search.FillAsync(searchedSession.ToUpperInvariant());
        await Expect(result).ToHaveTextAsync("正しいセッションIDを入力してください。");
        Assert.Equal(0, directCalls);

        await search.FillAsync(searchedSession);
        await Expect(result.GetByRole(AriaRole.Link, new() { Name = searchedSession, Exact = true })).ToHaveAttributeAsync("href", $"/sessions/{searchedSession}");
        await Expect(result).ToContainTextAsync("アーカイブ済みです。");
        await Expect(result).Not.ToContainTextAsync("2026-08-09");
        await result.GetByRole(AriaRole.Button, new() { Name = "復元", Exact = true }).ClickAsync();
        Assert.NotNull(restoreRequest);
        Assert.Equal($$"""{"schema_version":"local-archive-action.v1","action":"restore","target_kind":"session","targets":[{"target_id":"{{searchedSession}}","expected_revision":3}]}""", restoreRequest.PostData);

        foreach (var state in new[] { "active", "missing", "wrong-schema", "wrong-kind", "wrong-id", "hostile-extra", "busy", "malformed", "network", "failure" })
        {
            mode = state;
            await search.FillAsync("");
            await search.FillAsync(searchedSession);
            var expected = state switch
            {
                "active" => "このセッションはアクティブで、アーカイブされていません。",
                "missing" => "セッションが見つかりません。",
                "wrong-schema" or "wrong-kind" or "wrong-id" or "hostile-extra" => "検索結果が指定したセッションと一致しません。",
                "busy" => "保存先が使用中です。しばらくしてからもう一度お試しください。",
                _ => "セッションを読み込めませんでした。",
            };
            await Expect(result).ToHaveTextAsync(expected);
        }
        await Expect(result).Not.ToContainTextAsync("SECRET_");
        Assert.Equal(11, directCalls);
    }

    [Fact]
    public async Task Shell_UnifiedSettings_ArchivedSessionExactSearchPreservesListCursorAndDropsStaleResponses()
    {
        const string listedSession = "01890f65-4c31-7f42-8a7d-111111111111";
        const string searchedSession = "01890f65-4c31-7f42-8a7d-222222222222";
        const string replacementSession = "01890f65-4c31-7f42-8a7d-333333333333";
        const string leaveSession = "01890f65-4c31-7f42-8a7d-444444444444";
        const string closeSession = "01890f65-4c31-7f42-8a7d-555555555555";
        var paginationCursor = new string('A', 136);
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync("""
            window.__archiveExactAbortCount = 0;
            const originalFetch = window.fetch;
            window.fetch = (input, init = {}) => {
              const url = typeof input === 'string' ? input : input.url;
              if (url.includes('/api/local-monitor/v1/archive?') && init.signal)
                init.signal.addEventListener('abort', () => window.__archiveExactAbortCount++);
              return originalFetch(input, init);
            };
            """);
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{listedSession}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":"{{paginationCursor}}"}""",
        }));
        var cursorCalls = 0;
        await page.RouteAsync($"**/api/local-monitor/v1/archived-items?target_kind=session&limit=50&after={paginationCursor}", route =>
        {
            cursorCalls++;
            return route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "{\"schema_version\":\"local-archived-items.response.v1\",\"target_kind\":\"session\",\"items\":[],\"next_cursor\":null}" });
        });
        var releases = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
        {
            [searchedSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [replacementSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [leaveSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [closeSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var started = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
        {
            [searchedSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [replacementSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [leaveSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [closeSession] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await page.RouteAsync("**/api/local-monitor/v1/archive?target_kind=session&target_id=*", async route =>
        {
            var id = route.Request.Url.Split("target_id=", StringSplitOptions.None)[1];
            started[id].TrySetResult();
            await releases[id].Task;
            try
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = $$"""{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"{{id}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}""" });
            }
            catch (PlaywrightException) { }
        });

        await page.GotoAsync($"{host.Url}/sessions?settings=archive", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var archive = page.Locator("[data-settings-archived-sessions]");
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "アーカイブ済みセッションID" });
        var result = page.Locator("[data-settings-archived-session-search-result]");
        var more = archive.GetByRole(AriaRole.Button, new() { Name = "さらに読み込む" });
        await Expect(archive.GetByRole(AriaRole.Link, new() { Name = listedSession, Exact = true })).ToBeVisibleAsync();
        await Expect(more).ToBeEnabledAsync();

        await search.FillAsync(searchedSession);
        await started[searchedSession].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await search.FillAsync(replacementSession);
        await started[replacementSession].Task.WaitAsync(TimeSpan.FromSeconds(5));
        releases[searchedSession].TrySetResult();
        await Expect(result).Not.ToContainTextAsync(searchedSession);
        await search.FillAsync("");
        releases[replacementSession].TrySetResult();
        await Expect(result).ToBeEmptyAsync();
        await Expect(archive.GetByRole(AriaRole.Link, new() { Name = listedSession, Exact = true })).ToBeVisibleAsync();
        await Expect(more).ToBeEnabledAsync();
        await more.ClickAsync();
        Assert.Equal(1, cursorCalls);

        await search.FillAsync(leaveSession);
        await started[leaveSession].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='archive']").ClickAsync();
        await Expect(result).ToBeEmptyAsync();
        releases[leaveSession].TrySetResult();
        await search.FillAsync(closeSession);
        await started[closeSession].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Locator("#settings-modal-close").ClickAsync();
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        releases[closeSession].TrySetResult();
        Assert.True(await page.EvaluateAsync<int>("window.__archiveExactAbortCount") >= 4);
    }

    [Fact]
    public async Task Shell_UnifiedSettings_ArchivePaginationCancelsStalePagesAndSerializesLoadMore()
    {
        const string firstSession = "01890f65-4c31-7f42-8a7d-111111111111";
        const string staleSession = "01890f65-4c31-7f42-8a7d-222222222222";
        var paginationCursor = new string('A', 136);
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var initialRequests = 0;
        var cursorRequests = 0;
        var releaseCursor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursorRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursorHandlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route =>
        {
            initialRequests++;
            return route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = initialRequests == 1
                    ? $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{firstSession}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":"{{paginationCursor}}"}"""
                    : "{\"schema_version\":\"local-archived-items.response.v1\",\"target_kind\":\"session\",\"items\":[],\"next_cursor\":null}",
            });
        });
        await page.RouteAsync($"**/api/local-monitor/v1/archived-items?target_kind=session&limit=50&after={paginationCursor}", async route =>
        {
            cursorRequests++;
            cursorRequestStarted.TrySetResult();
            await releaseCursor.Task;
            try
            {
                await route.FulfillAsync(new()
                {
                    Status = 200, ContentType = "application/json",
                    Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{staleSession}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:35:56.1234567+00:00","updated_at":"2026-08-09T12:35:56.1234567+00:00"}],"next_cursor":null}""",
                });
            }
            catch (PlaywrightException) { }
            finally { cursorHandlerFinished.TrySetResult(); }
        });

        await page.GotoAsync($"{host.Url}/sessions?settings=archive", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var more = page.Locator("[data-settings-archived-sessions]").GetByRole(AriaRole.Button, new() { Name = "さらに読み込む" });
        await Expect(more).ToBeEnabledAsync();
        await more.EvaluateAsync("button => { button.click(); button.click(); }");
        await Expect(more).ToBeDisabledAsync();
        await cursorRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cursorRequests);

        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await Expect(page.Locator("[data-settings-section='state']")).ToBeVisibleAsync();
        await page.Locator("[data-settings-navigation='archive']").ClickAsync();
        await Expect(page.Locator("[data-settings-archived-sessions]")).ToContainTextAsync("0件を表示しています");
        releaseCursor.TrySetResult();
        await cursorHandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(page.Locator($"a[href='/sessions/{staleSession}']")).ToHaveCountAsync(0);
        Assert.Equal(2, initialRequests);
        Assert.Equal(1, cursorRequests);

        await page.UnrouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50");
        await page.UnrouteAsync($"**/api/local-monitor/v1/archived-items?target_kind=session&limit=50&after={paginationCursor}");
        await page.RouteAsync("**/api/local-monitor/v1/archived-items?target_kind=session&limit=50", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{firstSession}}","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":"{{paginationCursor}}"}""",
        }));
        await page.RouteAsync($"**/api/local-monitor/v1/archived-items?target_kind=session&limit=50&after={paginationCursor}", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = $$"""{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"{{firstSession}}","state":"archived","revision":3,"archived_at":"2026-08-09T12:36:56.1234567+00:00","updated_at":"2026-08-09T12:36:56.1234567+00:00"}],"next_cursor":null}""",
        }));
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.Locator("[data-settings-navigation='archive']").ClickAsync();
        await Expect(more).ToBeEnabledAsync();
        await more.ClickAsync();
        await Expect(page.Locator("[data-settings-archived-sessions]")).ToContainTextAsync("読み込めませんでした");
        await Expect(page.Locator($"a[href='/sessions/{firstSession}']")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Shell_UnifiedSettings_StorageCancelsAndDropsDelayedResponseAfterLeaveAndReplacement()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync("""
            window.__storageAbortCount = 0;
            const originalFetch = window.fetch;
            window.fetch = (input, init = {}) => {
              const url = typeof input === 'string' ? input : input.url;
              if (url.includes('/api/local-monitor/v1/settings/storage') && init.signal)
                init.signal.addEventListener('abort', () => window.__storageAbortCount++);
              return originalFetch(input, init);
            };
            """);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/settings/storage", async route =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                try { await route.FulfillAsync(StorageSummary("succeeded")); }
                catch (PlaywrightException) { }
                finally { firstFinished.TrySetResult(); }
                return;
            }
            await route.FulfillAsync(StorageSummary("not_run"));
        });

        await page.GotoAsync($"{host.Url}/sessions?settings=storage", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expect(page.Locator("[data-settings-storage-summary]")).ToContainTextAsync("確認しています");
        await page.Locator("[data-settings-navigation='diagnostics']").ClickAsync();
        await page.Locator("[data-settings-navigation='storage']").ClickAsync();
        await Expect(page.Locator("[data-settings-storage-operations]")).ToContainTextAsync("履歴取り込みはまだありません");
        releaseFirst.TrySetResult();
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await page.EvaluateAsync<int>("() => window.__storageAbortCount") >= 1);
        await Expect(page.Locator("[data-settings-storage-operations]")).ToContainTextAsync("履歴取り込みはまだありません");
        await Expect(page.Locator("[data-settings-storage-operations]")).Not.ToContainTextAsync("完了しています");
    }

    private static RouteFulfillOptions StorageSummary(string importState) => new()
    {
        Status = 200,
        ContentType = "application/json",
        Body = $"{{\"schema_version\":\"settings-storage-summary.v1\",\"database_file_size_bytes\":4096,\"retention\":{{\"state\":\"idle\"}},\"backup\":{{\"state\":\"idle\",\"last_successful_at\":null,\"validation_state\":\"unknown\"}},\"historical_import\":{{\"state\":\"{importState}\"}},\"restart_requirement\":\"not_required\"}}",
    };

    [Fact]
    public async Task Shell_UnifiedSettings_CancelsReceiverRuntimeReadWhenLeavingOrClosing()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync(
            """
            (() => {
              const original = window.fetch.bind(window);
              window.runtimeRequests = 0;
              window.runtimeAborts = 0;
              window.fetch = (input, init = {}) => {
                if (String(input).endsWith("/api/local-monitor/v1/settings/runtime")) {
                  window.runtimeRequests++;
                  return new Promise((resolve, reject) => {
                    init.signal?.addEventListener("abort", () => {
                      window.runtimeAborts++;
                      reject(new DOMException("Aborted", "AbortError"));
                    }, { once: true });
                  });
                }
                return original(input, init);
              };
            })();
            """);

        await page.GotoAsync($"{host.Url}/sessions?settings=receiver", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-settings-section='receiver']")).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => window.runtimeRequests === 1");
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.runtimeAborts === 1");

        await page.Locator("[data-settings-navigation='receiver']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.runtimeRequests === 2");
        await page.Locator("#settings-modal-close").ClickAsync();
        await page.WaitForFunctionAsync("() => window.runtimeAborts === 2");
    }

    [Fact]
    public async Task Shell_SettingsModal_ContainsBidirectionalFocus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync(
            """
            () => {
              const first = document.createElement("button");
              first.type = "button";
              first.id = "settings-extension-first";
              first.textContent = "first";
              document.querySelector("[data-settings-navigation-host]").append(first);

              const last = document.createElement("button");
              last.type = "button";
              last.id = "settings-extension-last";
              last.textContent = "last";

              const selectedRadio = document.createElement("input");
              selectedRadio.type = "radio";
              selectedRadio.name = "settings-extension-mode";
              selectedRadio.id = "settings-extension-radio-selected";
              selectedRadio.checked = true;

              const unselectedRadio = document.createElement("input");
              unselectedRadio.type = "radio";
              unselectedRadio.name = "settings-extension-mode";
              unselectedRadio.id = "settings-extension-radio-unselected";

              const inertContainer = document.createElement("div");
              inertContainer.inert = true;
              const inertButton = document.createElement("button");
              inertButton.type = "button";
              inertButton.id = "settings-extension-inert";
              inertContainer.append(inertButton);

              const negativeTabIndex = document.createElement("button");
              negativeTabIndex.type = "button";
              negativeTabIndex.id = "settings-extension-negative";
              negativeTabIndex.tabIndex = -2;

              document.querySelector("[data-settings-navigation-host]")
                .append(selectedRadio, unselectedRadio);
              document.querySelector("[data-settings-content-host]")
                .append(last, inertContainer, negativeTabIndex);
            }
            """);

        await page.Locator("#settings-action").ClickAsync();

        await Expect(page.Locator("#settings-modal-close")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.Locator("#settings-extension-last")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-modal-close")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-settings-navigation='state']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-settings-navigation='receiver']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-settings-navigation='ai']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-repository-settings-navigation='repositories']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-repository-settings-navigation='archive']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-settings-navigation='storage']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-settings-navigation='diagnostics']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-first")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-radio-selected")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#settings-extension-last")).ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-radio-unselected")).Not.ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-inert")).Not.ToBeFocusedAsync();
        await Expect(page.Locator("#settings-extension-negative")).Not.ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#settings-action")).ToBeFocusedAsync();

        await page.Locator("#receiver-status-action").ClickAsync();
        await page.Locator("#settings-modal-close").ClickAsync();
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#receiver-status-action")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Shell_NarrowHealthFailure_RemainsReachableWithoutSensitiveRequests()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 360, Height = 640 },
        });
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);
        var healthRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/health/ready", async route =>
        {
            healthRequested.TrySetResult(true);
            await releaseHealth.Task;
            await route.AbortAsync();
        });

        try
        {
            await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await healthRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var headerBefore = await page.Locator(".monitor-shell-header").EvaluateAsync<float[]>(
                "element => { const rect = element.getBoundingClientRect(); return [rect.top, rect.height]; }");

            releaseHealth.TrySetResult(true);
            await Expect(page.Locator("#receiver-status-action")).ToContainTextAsync("未接続");
            var headerAfter = await page.Locator(".monitor-shell-header").EvaluateAsync<float[]>(
                "element => { const rect = element.getBoundingClientRect(); return [rect.top, rect.height]; }");

            Assert.Equal(headerBefore, headerAfter);
            Assert.Equal(0, await page.EvaluateAsync<int>(
                "() => document.documentElement.scrollWidth - document.documentElement.clientWidth"));
            var actionRight = await page.Locator(".monitor-shell-actions").EvaluateAsync<float>(
                "element => element.getBoundingClientRect().right");
            Assert.True(actionRight <= 348f);

            await page.Locator("#settings-action").ClickAsync();
            var modalBounds = await page.Locator("#settings-modal").EvaluateAsync<float[]>(
                """
                element => {
                  const rect = element.getBoundingClientRect();
                  return [rect.left, rect.top, rect.right, rect.bottom];
                }
                """);
            Assert.True(modalBounds[0] >= 20f);
            Assert.True(modalBounds[1] >= 20f);
            Assert.True(modalBounds[2] <= 340f);
            Assert.True(modalBounds[3] <= 620f);
            Assert.Equal(0, await page.EvaluateAsync<int>(
                "() => document.documentElement.scrollWidth - document.documentElement.clientWidth"));

            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("prompt-label", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                requestedUrls,
                url => url.Contains("/api/monitor/trace-list?limit=1", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("user.email", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requestedUrls, url => url.Contains("/health/ready", StringComparison.Ordinal));
        }
        finally
        {
            releaseHealth.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Shell_AiReadinessCheck_DropsDelayedResponsesAfterSectionChangeAndClose()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartReadyHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        TaskCompletionSource? release = null;
        var postSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/settings/ai-readiness", async route =>
        {
            if (route.Request.Method == "POST")
            {
                postSeen.TrySetResult();
                await release!.Task;
            }
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = $"{{\"provider\":\"github_copilot\",\"selected_model\":\"gpt-5\",\"selected_configuration\":\"standard\",\"readiness_state\":\"{(route.Request.Method == "POST" ? "ready" : "configured_not_checked")}\",\"last_check_result\":\"{(route.Request.Method == "POST" ? "ready" : "not_checked")}\",\"provider_egress_notice\":\"selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action\"}}",
            });
        });
        await page.GotoAsync($"{host.Url}/sessions?settings=ai", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Locator("[data-settings-ai-check]").ClickAsync();
        await postSeen.Task;
        await page.Locator("[data-settings-navigation='state']").ClickAsync();
        release.SetResult();
        await page.Locator("[data-settings-navigation='ai']").ClickAsync();
        await Expect(page.Locator("[data-settings-ai]")).ToContainTextAsync("未確認");
        await Expect(page.Locator("[data-settings-ai-check]")).ToBeEnabledAsync();

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        postSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Locator("[data-settings-ai-check]").ClickAsync();
        await postSeen.Task;
        await page.Locator("#settings-modal-close").ClickAsync();
        release.SetResult();
        await page.Locator("#settings-action").ClickAsync();
        await page.Locator("[data-settings-navigation='ai']").ClickAsync();
        await Expect(page.Locator("[data-settings-ai]")).ToContainTextAsync("未確認");
        await Expect(page.Locator("[data-settings-ai-check]")).ToBeEnabledAsync();
    }

    private static async Task<RunningMonitorHost> StartReadyHostAsync(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        return await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            Health = MonitorTestHealth.Ready(time),
        });
    }

}

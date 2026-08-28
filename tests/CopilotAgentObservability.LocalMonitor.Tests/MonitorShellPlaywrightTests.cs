using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Local Monitor v1 shared shell: full-width breadcrumb header, receiver and
/// Settings actions, and the extension-only Unified Settings modal host.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
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
        await Expect(page.Locator("[data-repository-settings-navigation='repositories']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-repository-settings-navigation='archive']")).ToBeFocusedAsync();
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

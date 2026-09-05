using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public sealed class LocalMonitorV1RepositorySelectionPlaywrightTests
{
    private const string FirstRepositoryId = "018f0000-0000-7000-8000-000000000101";
    private const string SecondRepositoryId = "018f0000-0000-7000-8000-000000000102";
    private const string ArchivedRepositoryId = "018f0000-0000-7000-8000-000000000103";

    [Fact]
    public async Task Root_RendersExactCardsVirtualScopesSafeFactsAndOpaqueNavigation()
    {
        const string hostileName = "同じ名前 <img src=x onerror=window.__repositoryNameExecuted=true>";
        var repositories = new[]
        {
            new LocalRepositoryCatalogSnapshot(SecondRepositoryId, hostileName, 4, null, 0, LocalArchiveState.Active, 0),
            new LocalRepositoryCatalogSnapshot(FirstRepositoryId, hostileName, 3, null, 2, LocalArchiveState.Active, 0),
            new LocalRepositoryCatalogSnapshot(ArchivedRepositoryId, "Archived", 2, null, 0, LocalArchiveState.Archived, 1),
        };
        var sessions = new[]
        {
            Session(1, FirstRepositoryId, "2026-08-28T01:02:03.0000000+00:00"),
            Session(2, FirstRepositoryId, "2026-08-28T02:03:04.0000000+00:00"),
            Session(3, null, null),
            Session(4, SecondRepositoryId, "0001-01-01T00:00:00.0000000+00:00"),
        };
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(new FixedSnapshotService(new(
                new(LocalRepositoryScopeKind.All, null), repositories, sessions))));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            TimezoneId = "Asia/Tokyo",
        });
        var requestUrls = new List<string>();
        page.Request += (_, request) => requestUrls.Add(request.Url);

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#local-monitor-repository-selection"))
            .ToContainTextAsync("GitHub URL、または Copilot CLI が記録したワークスペースの Git 情報");
        var cards = page.Locator("[data-repository-card]");
        await Expect(cards).ToHaveCountAsync(2);
        await Expect(cards.Nth(0).Locator("[data-repository-name]")).ToHaveTextAsync(hostileName);
        await Expect(cards.Nth(1).Locator("[data-repository-name]")).ToHaveTextAsync(hostileName);
        await Expect(cards.Nth(0).Locator("[data-repository-session-count]")).ToHaveTextAsync("2件");
        await Expect(cards.Nth(1).Locator("[data-repository-session-count]")).ToHaveTextAsync("1件");
        await Expect(cards.Nth(0).Locator("[data-repository-last-observed] time"))
            .ToHaveAttributeAsync("datetime", "2026-08-28T02:03:04.0000000+00:00");
        await Expect(cards.Nth(0).Locator("[data-repository-last-observed]"))
            .ToContainTextAsync("2026年8月28日");
        await Expect(cards.Nth(0).Locator(".local-monitor-repository-fact-label"))
            .ToHaveTextAsync("最終記録");
        await Expect(cards.Nth(1).Locator("[data-repository-last-observed] time"))
            .ToHaveAttributeAsync("datetime", "0001-01-01T00:00:00.0000000+00:00");
        await Expect(cards.Nth(0).Locator("[data-repository-open]"))
            .ToHaveAttributeAsync("href", $"/repositories/{FirstRepositoryId}/sessions");
        await Expect(cards.Nth(1).Locator("[data-repository-open]"))
            .ToHaveAttributeAsync("href", $"/repositories/{SecondRepositoryId}/sessions");
        await Expect(cards.Nth(0).Locator("[data-repository-conflict-entry]"))
            .ToHaveAttributeAsync("href", "/sessions/unassigned");
        await Expect(cards.Nth(0).Locator("[data-repository-conflict-entry]"))
            .ToContainTextAsync("2件");
        await Expect(page.Locator("#all-sessions-entry")).ToContainTextAsync("4件");
        await Expect(page.Locator("#unassigned-sessions-entry")).ToBeVisibleAsync();
        await Expect(page.Locator("#unassigned-sessions-entry")).ToContainTextAsync("1件");
        await Expect(page.Locator("#archived-repositories-action")).ToContainTextAsync("1件");
        await Expect(page.Locator("#all-sessions-entry[data-repository-card], #unassigned-sessions-entry[data-repository-card]"))
            .ToHaveCountAsync(0);
        Assert.False(await page.EvaluateAsync<bool>("() => window.__repositoryNameExecuted === true"));
        Assert.DoesNotContain(requestUrls, url => url.Contains("/locators", StringComparison.Ordinal));
        Assert.DoesNotContain(requestUrls, url => url.Contains("/archive-actions", StringComparison.Ordinal));
        Assert.DoesNotContain(requestUrls, url => url.Contains("/session-repository-actions", StringComparison.Ordinal));
        Assert.DoesNotContain(hostileName, page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_CreateRenameArchiveAndRestoreUseExactAuthoritiesWithoutChangingRouteIdentity()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: ProductionOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requests = new List<(string Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers)>();
        page.Request += (_, request) => requests.Add((request.Method, request.Url, request.PostData, request.Headers));

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#repository-selection-status"))
            .ToContainTextAsync("登録されたアクティブなリポジトリはありません");
        Assert.DoesNotContain(requests, request => request.Url.Contains("/locators", StringComparison.Ordinal));

        await page.Locator("#add-repository-action").ClickAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-repository-settings-section='repositories']")).ToBeVisibleAsync();
        await Expect(page.Locator("#repository-create-display-name")).ToBeFocusedAsync();
        await page.Locator("#repository-create-display-name").FillAsync("同じ名前");
        await page.Locator("#repository-create-github-locator").FillAsync("https://github.com/synthetic/task5");
        await page.Locator("#repository-create-form button[type='submit']").ClickAsync();

        await Expect(page.Locator("#repository-management-result")).ToBeFocusedAsync();
        await Expect(page.Locator("#repository-management-result")).ToContainTextAsync("リポジトリを追加しました");
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        var repositoryId = await page.Locator("[data-repository-card]").GetAttributeAsync("data-repository-id");
        Assert.NotNull(repositoryId);
        var exactHref = $"/repositories/{repositoryId}/sessions";
        await Expect(page.Locator("[data-repository-open]")).ToHaveAttributeAsync("href", exactHref);
        Assert.DoesNotContain("github.com/synthetic/task5", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/synthetic/task5", page.Url, StringComparison.Ordinal);

        await page.Locator("#settings-modal-close").ClickAsync();
        await page.Locator("[data-repository-manage]").ClickAsync();
        await Expect(page.Locator("#repository-rename-display-name")).ToBeFocusedAsync();
        await Expect(page.Locator("#repository-rename-display-name")).ToHaveValueAsync("同じ名前");
        Assert.Contains(requests, request => request.Method == "GET"
            && request.Url.EndsWith($"/api/local-monitor/v1/repositories/{repositoryId}/locators", StringComparison.Ordinal));
        await page.Locator("#repository-rename-display-name").FillAsync("同じ名前を変更");
        await page.Locator("[data-repository-rename]").ClickAsync();

        await Expect(page.Locator("#repository-management-result")).ToBeFocusedAsync();
        await Expect(page.Locator("[data-repository-name]")).ToHaveTextAsync("同じ名前を変更");
        await Expect(page.Locator("[data-repository-open]")).ToHaveAttributeAsync("href", exactHref);
        Assert.DoesNotContain("同じ名前を変更", page.Url, StringComparison.Ordinal);

        await Expect(page.Locator("[data-repository-archive]")).ToBeDisabledAsync();
        await Expect(page.Locator("#repository-archive-confirmation-description"))
            .ToContainTextAsync("セッションのアーカイブ状態や割り当ては変更しません");
        await page.Locator("#repository-archive-confirmation").CheckAsync();
        await page.Locator("[data-repository-archive]").ClickAsync();
        await Expect(page.Locator("#repository-management-result")).ToBeFocusedAsync();
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#archived-repositories-action")).ToContainTextAsync("1件");

        await page.Locator("#settings-modal-close").ClickAsync();
        await page.Locator("#archived-repositories-action").ClickAsync();
        await Expect(page.Locator("[data-repository-settings-section='archive']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-repository-restore]")).ToHaveCountAsync(1);
        await page.Locator("[data-repository-restore]").ClickAsync();

        await Expect(page.Locator("#repository-management-result")).ToBeFocusedAsync();
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-repository-open]")).ToHaveAttributeAsync("href", exactHref);

        var create = Assert.Single(requests, request => request.Method == "POST"
            && request.Url.EndsWith("/api/local-monitor/v1/repositories", StringComparison.Ordinal));
        Assert.Equal(
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"同じ名前\",\"github_locator\":\"https://github.com/synthetic/task5\"}",
            create.Body);
        Assert.Equal("local-monitor", create.Headers["x-monitor-csrf"]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", create.Headers["idempotency-key"]);

        var rename = Assert.Single(requests, request => request.Method == "PATCH");
        Assert.Equal(
            "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"同じ名前を変更\",\"github_locator\":null}",
            rename.Body);
        Assert.Equal("local-monitor", rename.Headers["x-monitor-csrf"]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", rename.Headers["idempotency-key"]);

        var archiveActions = requests.Where(request => request.Method == "POST"
            && request.Url.EndsWith("/api/local-monitor/v1/archive-actions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, archiveActions.Length);
        Assert.Equal(
            $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"repository\",\"targets\":[{{\"target_id\":\"{repositoryId}\",\"expected_revision\":0}}]}}",
            archiveActions[0].Body);
        Assert.Equal(
            $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"restore\",\"target_kind\":\"repository\",\"targets\":[{{\"target_id\":\"{repositoryId}\",\"expected_revision\":1}}]}}",
            archiveActions[1].Body);
        Assert.All(archiveActions, request =>
        {
            Assert.Equal("local-monitor", request.Headers["x-monitor-csrf"]);
            Assert.False(request.Headers.ContainsKey("idempotency-key"));
        });
    }

    [Fact]
    public async Task Settings_ShowsLocalGitIdentityWithoutExposingItsOpaqueLocatorOrTreatingItAsGitHub()
    {
        const string repositoryId = "018f0000-0000-7000-8000-000000000101";
        const string locatorId = "018f0000-0000-7000-8000-000000000201";
        const string opaqueLocator = "local-git:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string privateWorkingDirectory = "C:\\Users\\person\\source\\PrivateWorkspace";
        var repositories = new[]
        {
            new LocalRepositoryCatalogSnapshot(repositoryId, "PrivateWorkspace", 1, null, 1, LocalArchiveState.Active, 0),
        };
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(new FixedSnapshotService(new(
                new(LocalRepositoryScopeKind.All, null), repositories, []))));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{repositoryId}/locators", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schema_version = "local-repository-locators.v1",
                    repository_id = repositoryId,
                    repository_revision = 1,
                    locators = new[]
                    {
                        new
                        {
                            locator_id = locatorId,
                            kind = "local_git_repository",
                            canonical_locator = opaqueLocator,
                            display_owner = "Local",
                            display_repository = "PrivateWorkspace",
                            source = "observed",
                            is_current = true,
                            created_at = "2026-09-05T01:02:03.0000000+00:00",
                            provenance = new
                            {
                                source_surface = "github-copilot-cli",
                                source_application_version = "1.0.0",
                                trace_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                                span_id = "bbbbbbbbbbbbbbbb",
                                observed_at = "2026-09-05T01:02:03.0000000+00:00",
                                source_content_availability = "available",
                            },
                        },
                    },
                }),
            }));

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-repository-manage]").ClickAsync();

        var identity = page.Locator("[data-repository-identity-kind='local_git_repository']");
        await Expect(identity).ToContainTextAsync("ローカル Git / PrivateWorkspace");
        await Expect(identity).ToContainTextAsync("現在");
        await Expect(identity.Locator("a")).ToHaveCountAsync(0);
        await Expect(page.Locator("#repository-create-github-locator")).ToHaveValueAsync("");
        await Expect(page.Locator("[data-repository-open]"))
            .ToHaveAttributeAsync("href", $"/repositories/{repositoryId}/sessions");
        var body = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain(opaqueLocator, body, StringComparison.Ordinal);
        Assert.DoesNotContain(privateWorkingDirectory, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_PagesOneBoundCursorAndKeepsTheAcceptedLayoutAtWideAndNarrowWidths()
    {
        var repositories = Enumerable.Range(1, 51)
            .Select(index => new LocalRepositoryCatalogSnapshot(
                $"018f0000-0000-7000-8000-{index:x12}",
                index == 1 ? "すべてのセッション" : $"Repository {index:D2}",
                index,
                null,
                0,
                LocalArchiveState.Active,
                0))
            .ToArray();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(new FixedSnapshotService(new(
                new(LocalRepositoryScopeKind.All, null), repositories, []))));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        var collectionUrls = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/local-monitor/v1/repositories?", StringComparison.Ordinal))
                collectionUrls.Add(request.Url);
        };

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(50);
        await Expect(page.Locator("[data-repository-card] [data-repository-name]", new() { HasText = "すべてのセッション" }))
            .ToHaveCountAsync(1);
        await Expect(page.Locator("#all-sessions-entry")).ToHaveCountAsync(1);
        await Expect(page.Locator("#repository-load-more")).ToBeVisibleAsync();
        Assert.DoesNotContain("after=", page.Url, StringComparison.Ordinal);
        var wide = await page.EvaluateAsync<double[]>("""
            () => {
              const cards = [...document.querySelectorAll('[data-repository-card]')];
              const first = cards[0].getBoundingClientRect();
              const fourth = cards[3].getBoundingClientRect();
              const content = document.querySelector('.monitor-content');
              return [
                document.querySelector('.monitor-shell-header').getBoundingClientRect().height,
                parseFloat(getComputedStyle(content).paddingLeft),
                document.documentElement.scrollWidth,
                innerWidth,
                first.width,
                fourth.top - first.top,
                parseFloat(getComputedStyle(document.querySelector('#repository-grid')).columnGap)
              ];
            }
            """);
        Assert.Equal(48, wide[0]);
        Assert.Equal(24, wide[1]);
        Assert.True(wide[2] <= wide[3]);
        Assert.InRange(wide[4], 300, 380);
        Assert.True(wide[5] > 0);
        Assert.Equal(16, wide[6]);

        await page.Locator("#repository-load-more").ClickAsync();

        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(51);
        await Expect(page.Locator("#repository-load-more")).ToBeHiddenAsync();
        Assert.Equal(2, collectionUrls.Count);
        Assert.Contains("archive_scope=active_only&after=", collectionUrls[1], StringComparison.Ordinal);
        Assert.DoesNotContain("after=", page.Url, StringComparison.Ordinal);

        await page.SetViewportSizeAsync(360, 768);
        var narrow = await page.EvaluateAsync<double[]>("""
            () => {
              const cards = [...document.querySelectorAll('[data-repository-card]')];
              const first = cards[0].getBoundingClientRect();
              const second = cards[1].getBoundingClientRect();
              return [document.documentElement.scrollWidth, innerWidth, first.width, first.top, second.top];
            }
            """);
        Assert.True(narrow[0] <= narrow[1]);
        Assert.InRange(narrow[2], 300, 380);
        Assert.True(narrow[4] > narrow[3]);
    }

    [Fact]
    public async Task Root_KeyboardSettingsHistoryReloadAndExactRepositoryNavigationRestoreState()
    {
        var repository = new LocalRepositoryCatalogSnapshot(
            FirstRepositoryId, "同じ名前", 1, null, 0, LocalArchiveState.Active, 0);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(new FixedSnapshotService(new(
                new(LocalRepositoryScopeKind.All, null), [repository], []))));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        var add = page.Locator("#add-repository-action");
        await add.FocusAsync();
        await add.PressAsync("Enter");
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await Expect(page.Locator("#repository-create-display-name")).ToBeFocusedAsync();
        Assert.EndsWith("/?settings=repositories", page.Url, StringComparison.Ordinal);

        await page.Locator("#repository-create-display-name").PressAsync("Escape");
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(add).ToBeFocusedAsync();
        Assert.EndsWith("/", page.Url, StringComparison.Ordinal);

        await page.GoBackAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        Assert.EndsWith("/?settings=repositories", page.Url, StringComparison.Ordinal);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-repository-settings-section='repositories']")).ToBeVisibleAsync();
        await page.GoForwardAsync();
        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();

        var open = page.Locator("[data-repository-open]");
        await open.FocusAsync();
        await open.PressAsync("Enter");
        await page.WaitForURLAsync($"**/repositories/{FirstRepositoryId}/sessions");
        Assert.EndsWith($"/repositories/{FirstRepositoryId}/sessions", page.Url, StringComparison.Ordinal);
        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-repository-open]")).ToHaveAttributeAsync(
            "href", $"/repositories/{FirstRepositoryId}/sessions");
    }

    [Fact]
    public async Task Root_CollectionFailureUsesBoundedCopyAndExplicitRetryWithoutRenderingTheErrorToken()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: ProductionOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var collectionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/repositories?*", async route =>
        {
            if (Interlocked.Increment(ref collectionCalls) == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json",
                    Body = "{\"error\":\"persistence_busy\"}",
                });
                return;
            }
            await route.ContinueAsync();
        });

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#repository-selection-status")).ToContainTextAsync("リポジトリを読み込めませんでした");
        await Expect(page.Locator("#repository-selection-status button")).ToHaveTextAsync("もう一度読み込む");
        Assert.DoesNotContain("persistence_busy", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        await page.Locator("#repository-selection-status button").ClickAsync();
        await Expect(page.Locator("#repository-selection-status"))
            .ToContainTextAsync("登録されたアクティブなリポジトリはありません");
        Assert.Equal(2, collectionCalls);
    }

    [Fact]
    public async Task CreateFailurePreservesTheDraftAndReusesTheExactKeyForAnUnchangedExplicitRetry()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: ProductionOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var keys = new List<string>();
        var createCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/repositories", async route =>
        {
            keys.Add(route.Request.Headers["idempotency-key"]);
            if (Interlocked.Increment(ref createCalls) == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json",
                    Body = "{\"error\":\"persistence_busy\"}",
                });
                return;
            }
            await route.ContinueAsync();
        });

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#add-repository-action").ClickAsync();
        await page.Locator("#repository-create-display-name").FillAsync("再試行する名前");
        await page.Locator("#repository-create-form button[type='submit']").ClickAsync();

        await Expect(page.Locator("#repository-management-result"))
            .ToContainTextAsync("リポジトリを追加できませんでした");
        await Expect(page.Locator("#repository-create-display-name")).ToHaveValueAsync("再試行する名前");
        Assert.DoesNotContain("persistence_busy", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        await page.Locator("#repository-create-form button[type='submit']").ClickAsync();
        await Expect(page.Locator("#repository-management-result")).ToContainTextAsync("リポジトリを追加しました");
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", keys[0]);
    }

    [Fact]
    public async Task SupersededCreateDoesNotPublishOrFocusItsLateResultInANewerSettingsSection()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: ProductionOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var activeCollectionCalls = 0;
        var delayedRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelayedRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/repositories?archive_scope=active_only&limit=50", async route =>
        {
            if (Interlocked.Increment(ref activeCollectionCalls) == 2)
            {
                delayedRefreshStarted.TrySetResult();
                await releaseDelayedRefresh.Task;
            }
            await route.ContinueAsync();
        });

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#add-repository-action").ClickAsync();
        await page.Locator("#repository-create-display-name").FillAsync("遅延する追加");
        await page.Locator("#repository-create-form button[type='submit']").ClickAsync();
        await delayedRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await page.Locator("[data-repository-settings-navigation='archive']").ClickAsync();
        await Expect(page.Locator("[data-repository-settings-section='archive']")).ToBeVisibleAsync();
        releaseDelayedRefresh.TrySetResult();
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        await page.WaitForTimeoutAsync(100);

        await Expect(page.Locator("[data-repository-settings-section='archive'] h3")).ToBeFocusedAsync();
        await Expect(page.Locator("#repository-management-result")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task SupersededCreateDoesNotPublishOrFocusItsLateResultOverANewerRepositoryTarget()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: ProductionOptions());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var activeCollectionCalls = 0;
        var delayedRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelayedRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/repositories?archive_scope=active_only&limit=50", async route =>
        {
            if (Interlocked.Increment(ref activeCollectionCalls) == 2)
            {
                delayedRefreshStarted.TrySetResult();
                await releaseDelayedRefresh.Task;
            }
            await route.ContinueAsync();
        });

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#add-repository-action").ClickAsync();
        await page.Locator("#repository-create-display-name").FillAsync("管理対象へ切り替える追加");
        await page.Locator("#repository-create-form button[type='submit']").ClickAsync();
        await delayedRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expect(page.Locator("[data-repository-settings-manage]")).ToHaveCountAsync(1);

        await page.Locator("[data-repository-settings-manage]").ClickAsync();
        await Expect(page.Locator("#repository-rename-display-name")).ToBeFocusedAsync();
        releaseDelayedRefresh.TrySetResult();
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(1);
        await page.WaitForTimeoutAsync(100);

        await Expect(page.Locator("#repository-rename-display-name")).ToBeFocusedAsync();
        await Expect(page.Locator("#repository-management-result")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Root_DiscardsAnIncoherentNextPageAndRetriesTheSameBoundCursor()
    {
        var repositories = Enumerable.Range(1, 51)
            .Select(index => new LocalRepositoryCatalogSnapshot(
                $"018f0000-0000-7000-8000-{index:x12}",
                $"Repository {index:D2}",
                index,
                null,
                0,
                LocalArchiveState.Active,
                0))
            .ToArray();
        var first = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All, null), repositories, []);
        var changed = first with
        {
            Repositories = repositories
                .Select((item, index) => index == 50 ? item with { DisplayName = "Changed" } : item)
                .ToArray(),
        };
        var service = new SequenceSnapshotService(first, changed, first);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(service, fixedRepositoryRevision: false));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var afterUrls = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("&after=", StringComparison.Ordinal)) afterUrls.Add(request.Url);
        };

        await page.GotoAsync(host.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(50);
        await page.Locator("#repository-load-more").ClickAsync();

        await Expect(page.Locator("#repository-selection-status")).ToContainTextAsync("続きを読み込めませんでした");
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(50);
        await Expect(page.Locator("#repository-selection-status button")).ToHaveTextAsync("もう一度読み込む");
        await page.Locator("#repository-selection-status button").ClickAsync();
        await Expect(page.Locator("#repository-selection-status")).ToContainTextAsync("続きを読み込めませんでした");
        await Expect(page.Locator("[data-repository-card]")).ToHaveCountAsync(50);
        Assert.Equal(2, afterUrls.Count);
        Assert.Equal(afterUrls[0], afterUrls[1]);
        Assert.DoesNotContain("after=", page.Url, StringComparison.Ordinal);
    }

    private static LocalRepositoryScopeSessionSnapshot Session(int index, string? repositoryId, string? lastObservedAt)
    {
        var missing = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var projection = new LocalWorkspaceProjectionRow(
            $"018f1000-0000-7000-8000-{index:x12}",
            0,
            1_777_344_000_000 + index,
            "not_observed",
            null,
            "completed",
            "partial",
            new("not_observed", []),
            new("not_observed", []),
            new(zero, zero, zero, zero, zero),
            new("none", "not_observed", 0, 0, missing, missing, missing, missing, missing, missing, missing, missing),
            "not_observed",
            null,
            null,
            lastObservedAt,
            null,
            [],
            "synthetic");
        var assigned = repositoryId is not null;
        return new(
            projection.SessionId,
            projection,
            0,
            assigned ? LocalRepositoryScopeAssignmentState.Assigned : LocalRepositoryScopeAssignmentState.Unassigned,
            assigned ? LocalRepositoryScopeAssignmentAuthority.Automatic : LocalRepositoryScopeAssignmentAuthority.None,
            repositoryId,
            [],
            true,
            !assigned,
            assigned,
            LocalArchiveState.Active,
            0,
            true,
            null);
    }

    private static MonitorHostTestOptions Options(
        ILocalRepositoryScopeSnapshotService service,
        bool fixedRepositoryRevision = true) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false,
        UseUserSecrets = false,
        LocalRepositoryScopeSnapshotService = service,
        LocalMonitorV1CollectionOverrides = new(
            new byte[32], null, null, new byte[32],
            fixedRepositoryRevision ? new string('a', 64) : null,
            new string('b', 64)),
    };

    private static MonitorHostTestOptions ProductionOptions() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false,
        UseUserSecrets = false,
    };

    private sealed class FixedSnapshotService(LocalRepositoryScopeSnapshot snapshot)
        : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot with { Request = request });
    }

    private sealed class SequenceSnapshotService(params LocalRepositoryScopeSnapshot[] snapshots)
        : ILocalRepositoryScopeSnapshotService
    {
        private int readIndex;

        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref readIndex) - 1, snapshots.Length - 1);
            return ValueTask.FromResult(snapshots[index] with { Request = request });
        }
    }
}

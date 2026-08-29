using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class LocalMonitorV1SessionExplorerPlaywrightTests
{
    private const string SessionId = "018f0000-0000-7000-8000-000000000002";
    private const string SecondSessionId = "018f0000-0000-7000-8000-000000000003";
    private const string ThirdSessionId = "018f0000-0000-7000-8000-000000000004";
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000138";
    private const string CandidateRepositoryOne = "018f0000-0000-7000-8000-000000000101";
    private const string CandidateRepositoryTwo = "018f0000-0000-7000-8000-000000000102";
    private const string ArchivedRepository = "018f0000-0000-7000-8000-000000000103";
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(includeRepository: true));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, message) => console.Add(message.Text);
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(TwoSessionsAsync().GetAwaiter().GetResult())));
        string? previewBody = null, createBody = null;
        var previewCalls = 0;
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/preview", route =>
        {
            previewCalls++;
            previewBody = route.Request.PostData;
            return route.FulfillAsync(Json(ComparisonPreview(previewCalls >= 3 ? '5' : '1', previewCalls >= 3 ? '6' : '2')));
        });
        var createCalls = 0;
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons", route =>
        {
            createCalls++;
            createBody = route.Request.PostData;
            if (createCalls == 1) return route.FulfillAsync(Error(409, "comparison_preview_stale"));
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 201,
                ContentType = "application/json; charset=utf-8",
                Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["Location"] = $"/repositories/{RepositoryId}/comparisons/018f0000-0000-7000-8000-000000000010" },
                Body = $$"""{"schema_version":"local-monitor-comparison-create.response.v1","comparison_id":"018f0000-0000-7000-8000-000000000010","location":"/repositories/{{RepositoryId}}/comparisons/018f0000-0000-7000-8000-000000000010","receipt_sha256":"{{new string('7', 64)}}","created_at":"2026-08-29T00:00:00.0000000+00:00","expires_at":"2026-08-30T00:00:00.0000000+00:00"}""",
            });
        });

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/sessions?mode=compare", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-include-archived").CheckAsync();
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        await page.Locator("#session-compare-preview").PressAsync("Enter");
        await Expect(page.Locator("#session-comparison-preview-dialog")).ToBeVisibleAsync();
        await Expect(page.Locator("#session-comparison-create")).ToBeEnabledAsync();
        await Expect(page.Locator("#session-comparison-cancel")).ToBeFocusedAsync();

        using (var preview = JsonDocument.Parse(previewBody!))
        {
            Assert.Equal(new[] { "schema_version", "cohorts", "include_archived" }, preview.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.Equal([SessionId], preview.RootElement.GetProperty("cohorts").GetProperty("a").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal([SecondSessionId], preview.RootElement.GetProperty("cohorts").GetProperty("b").EnumerateArray().Select(x => x.GetString()));
            Assert.True(preview.RootElement.GetProperty("include_archived").GetBoolean());
        }

        await page.Locator("#session-comparison-cancel").ClickAsync();
        Assert.Equal(2, await page.Locator("[data-cohort]:checked").CountAsync());
        await page.Locator("#session-compare-preview").ClickAsync();
        await Expect(page.Locator("#session-comparison-create")).ToBeEnabledAsync();
        await page.Locator("#session-comparison-create").ClickAsync();
        await Expect(page.Locator("#session-comparison-preview-status")).ToContainTextAsync("確認してください");
        Assert.Equal(3, previewCalls);
        await Expect(page.Locator("#session-comparison-create")).ToBeEnabledAsync();
        await page.Locator("#session-comparison-create").ClickAsync();
        await page.WaitForURLAsync(host.Url + $"/repositories/{RepositoryId}/comparisons/018f0000-0000-7000-8000-000000000010");
        using (var create = JsonDocument.Parse(createBody!))
        {
            Assert.Equal(new[] { "schema_version", "cohorts", "include_archived", "selection_sha256", "preview_revision" }, create.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.True(create.RootElement.GetProperty("include_archived").GetBoolean());
            Assert.Equal(new string('5', 64), create.RootElement.GetProperty("selection_sha256").GetString());
            Assert.Equal(new string('6', 64), create.RootElement.GetProperty("preview_revision").GetString());
        }
        Assert.DoesNotContain(SessionId, page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondSessionId, page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(console, value => value.Contains(SessionId, StringComparison.Ordinal) || value.Contains(SecondSessionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComparePreviewLocalizesExclusionsAndEscapeRetainsOnlyTransientSelection()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(includeRepository: true));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(ThreeSessionsAsync().GetAwaiter().GetResult())));
        var previewCalls = 0;
        string? previewBody = null;
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/preview", route =>
        {
            previewCalls++;
            previewBody = route.Request.PostData;
            return route.FulfillAsync(Json(ComparisonPreviewWithExclusion()));
        });

        await page.GotoAsync(host.Url + $"/repositories/{RepositoryId}/sessions?mode=compare", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{ThirdSessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        await Expect(page.Locator("#session-compare-preview")).ToHaveAttributeAsync("aria-disabled", "true");
        await page.Locator("#session-compare-preview").PressAsync("Enter");
        Assert.Equal(0, previewCalls);
        await page.Locator("#session-include-archived").CheckAsync();
        await Expect(page.Locator("#session-compare-preview")).ToHaveAttributeAsync("aria-disabled", "false");
        await page.Locator("#session-compare-preview").ClickAsync();

        using (var request = JsonDocument.Parse(previewBody!))
        {
            Assert.True(request.RootElement.GetProperty("include_archived").GetBoolean());
            Assert.Contains(ThirdSessionId, request.RootElement.GetProperty("cohorts").GetProperty("a").EnumerateArray().Select(value => value.GetString()));
        }
        await Expect(page.Locator("[data-comparison-preview-included='a']")).ToContainTextAsync("Archived session");
        await Expect(page.Locator("[data-comparison-preview-excluded-list]")).ToContainTextAsync("比較用データを利用できません");
        await Expect(page.Locator("#session-comparison-create")).ToBeEnabledAsync();
        Assert.False(await page.Locator("#session-comparison-preview-dialog").EvaluateAsync<bool>(
            "(dialog, ids) => [...dialog.querySelectorAll('*')].some(node => [...node.attributes].some(attribute => ids.includes(attribute.value)))",
            new[] { SessionId, SecondSessionId, ThirdSessionId }));
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#session-comparison-preview-dialog")).Not.ToBeVisibleAsync();
        Assert.Equal(3, await page.Locator("[data-cohort]:checked").CountAsync());
        Assert.False(await page.EvaluateAsync<bool>(
            "ids => [...Object.values(localStorage), ...Object.values(sessionStorage), JSON.stringify(history.state)].some(value => ids.some(id => value.includes(id)))",
            new[] { SessionId, SecondSessionId, ThirdSessionId }));
        Assert.Empty(await page.EvaluateAsync<string[]>("() => caches.keys()"));
    }

    [Fact]
    public async Task CompareCreateRejectsExtraAndMalformedSuccessResponsesWithoutNavigating()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(includeRepository: true));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(TwoSessionsAsync().GetAwaiter().GetResult())));
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/preview", route => route.FulfillAsync(Json(ComparisonPreview())));
        var createCalls = 0;
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons", route =>
        {
            createCalls++;
            var body = createCalls == 1
                ? $$"""{"schema_version":"local-monitor-comparison-create.response.v1","comparison_id":"018f0000-0000-7000-8000-000000000010","location":"/repositories/{{RepositoryId}}/comparisons/018f0000-0000-7000-8000-000000000010","receipt_sha256":"{{new string('7', 64)}}","created_at":"2026-08-29T00:00:00.0000000+00:00","expires_at":"2026-08-30T00:00:00.0000000+00:00","extra":true}"""
                : $$"""{"schema_version":"local-monitor-comparison-create.response.v1","comparison_id":"018f0000-0000-7000-8000-000000000010","location":"/repositories/{{RepositoryId}}/comparisons/018f0000-0000-7000-8000-000000000010","receipt_sha256":"INVALID","created_at":"2026-08-29T00:00:00.0000000+00:00","expires_at":"2026-08-30T00:00:00.0000000+00:00"}""";
            return route.FulfillAsync(new RouteFulfillOptions { Status = 201, ContentType = "application/json; charset=utf-8", Headers = new Dictionary<string, string> { ["Location"] = $"/repositories/{RepositoryId}/comparisons/018f0000-0000-7000-8000-000000000010" }, Body = body });
        });

        var explorerUrl = host.Url + $"/repositories/{RepositoryId}/sessions?mode=compare";
        await page.GotoAsync(explorerUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        await page.Locator("#session-compare-preview").ClickAsync();
        await page.Locator("#session-comparison-create").ClickAsync();
        await Expect(page.Locator("#session-comparison-preview-status")).ToContainTextAsync("作成できませんでした");
        Assert.Equal(explorerUrl, page.Url);
        await page.Locator("#session-comparison-create").ClickAsync();
        await Expect(page.Locator("#session-comparison-preview-status")).ToContainTextAsync("作成できませんでした");
        Assert.Equal(explorerUrl, page.Url);
        Assert.Equal(2, createCalls);
    }

    [Fact]
    public async Task AllSessionsPostsTheClosedRequestAndRendersADenseDirectOpenRow()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        var requests = new List<IRequest>();
        var responseBody = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            requests.Add(route.Request);
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json; charset=utf-8",
                Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                Body = responseBody,
            });
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        Assert.Equal("FORM", await page.Locator("#session-explorer-filters")
            .EvaluateAsync<string>("node => node.tagName"));
        var row = page.Locator("[data-session-row]");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row.Locator("[data-session-open]")).ToHaveAttributeAsync("href", $"/sessions/{SessionId}");
        await Expect(row.Locator("[data-session-label]")).ToHaveTextAsync("Synthetic session");
        await Expect(row.Locator("[data-session-status]")).ToHaveTextAsync("実行中");
        await Expect(row.Locator("[data-session-summary]")).ToContainTextAsync("スキル 1件");
        await Expect(row.Locator("[data-session-tokens]")).ToContainTextAsync("125");
        await Expect(row.Locator("[data-session-tokens]")).ToContainTextAsync("40%");
        await Expect(row.Locator("[data-session-started] time"))
            .ToHaveAttributeAsync("datetime", "2026-01-02T00:00:00.0000000+00:00");
        await Expect(page.Locator("[data-session-preview-pane]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-load-more")).ToBeHiddenAsync();

        var request = Assert.Single(requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(host.Url + "/api/local-monitor/v1/sessions", request.Url);
        Assert.Equal("local-monitor", request.Headers["x-monitor-csrf"]);
        using var body = JsonDocument.Parse(request.PostData!);
        Assert.Equal(
            new[]
            {
                "schema_version", "scope", "repository_id", "archive_scope", "from", "to",
                "source", "model", "status", "has_skill", "has_subagent", "has_error",
                "has_retry", "q", "cursor", "limit",
            },
            body.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.Equal("local-monitor-session-search.request.v1", body.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("all", body.RootElement.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("repository_id").ValueKind);
        Assert.Equal("active_only", body.RootElement.GetProperty("archive_scope").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("q").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("cursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("limit").ValueKind);

        var keyboardSubmit = page.WaitForRequestAsync(item =>
            item.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-search").FillAsync("keyboard-submit");
        await page.Locator("#session-search").PressAsync("Enter");
        var keyboardRequest = await keyboardSubmit;
        using var keyboardBody = JsonDocument.Parse(keyboardRequest.PostData!);
        Assert.Equal("keyboard-submit", keyboardBody.RootElement.GetProperty("q").GetString());
        Assert.Equal(host.Url + "/sessions", page.Url);

        var layout = await page.EvaluateAsync<double[]>("""
            () => {
              const title = document.querySelector('.local-monitor-session-explorer-header').getBoundingClientRect();
              const filters = document.querySelector('.local-monitor-session-explorer-filters').getBoundingClientRect();
              const region = document.querySelector('.local-monitor-session-table-region');
              return [title.height, filters.height, document.documentElement.scrollWidth, innerWidth,
                region.scrollHeight, region.clientHeight];
            }
            """);
        Assert.True(layout[0] <= 64);
        Assert.True(layout[1] <= 88);
        Assert.True(layout[2] <= layout[3]);
        Assert.True(layout[4] >= layout[5]);
        var rowBox = Assert.IsType<LocatorBoundingBoxResult>(await row.BoundingBoxAsync());
        Assert.InRange(rowBox.Height, 52, 64);
        var identityDisclosure = row.Locator(".local-monitor-session-identity .local-monitor-session-fact-disclosure > summary");
        await identityDisclosure.PressAsync("Enter");
        var identityPanel = row.Locator(".local-monitor-session-identity .local-monitor-session-fact-panel");
        await Expect(identityPanel).ToBeVisibleAsync();
        await Expect(identityPanel).ToContainTextAsync("Copilot SDK / VS Code");
        await Expect(identityPanel).Not.ToContainTextAsync("copilot-sdk");
        var disclosureLayout = await identityPanel.EvaluateAsync<double[]>("""
            panel => {
              const box = panel.getBoundingClientRect();
              return [box.left, box.right, box.top, box.bottom, innerWidth, innerHeight,
                document.documentElement.scrollWidth];
            }
            """);
        Assert.True(disclosureLayout[0] >= 0);
        Assert.True(disclosureLayout[1] <= disclosureLayout[4]);
        Assert.True(disclosureLayout[2] >= 0);
        Assert.True(disclosureLayout[3] <= disclosureLayout[5]);
        Assert.True(disclosureLayout[6] <= disclosureLayout[4]);
        await identityDisclosure.PressAsync("Enter");

        await page.SetViewportSizeAsync(1000, 768);
        await Expect(page.Locator("#session-model")).ToBeVisibleAsync();
        var narrowLayout = await page.EvaluateAsync<double[]>("""
            () => {
              const region = document.querySelector('.local-monitor-session-table-region');
              return [document.documentElement.scrollWidth, innerWidth, region.scrollWidth, region.clientWidth];
            }
            """);
        Assert.True(narrowLayout[0] <= narrowLayout[1]);
        Assert.True(narrowLayout[2] >= narrowLayout[3]);

        await page.SetViewportSizeAsync(800, 768);
        await Expect(page.Locator("#session-model")).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));

        await page.Locator("#session-search").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#session-model")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("details:has(#session-from) summary")).ToBeFocusedAsync();

        await row.Locator("[data-session-status]").ClickAsync();
        await page.WaitForURLAsync(host.Url + $"/sessions/{SessionId}");
        Assert.Equal(host.Url + $"/sessions/{SessionId}", page.Url);
        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);

        var open = row.Locator("[data-session-open]");
        await open.FocusAsync();
        Assert.True(await open.EvaluateAsync<bool>("node => node === document.activeElement"));
        await open.PressAsync("Enter");
        await page.WaitForURLAsync(host.Url + $"/sessions/{SessionId}");
        Assert.Equal(host.Url + $"/sessions/{SessionId}", page.Url);
    }

    [Fact]
    public async Task LongRepositoryNameKeepsTheCompareActionVisibleAndKeyboardReachableAt1366()
    {
        var displayName = new string('長', 200);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(includeRepository: true, repositoryDisplayName: displayName));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(FinalGoldenAsync().GetAwaiter().GetResult())));

        await page.GotoAsync(
            host.Url + $"/repositories/{RepositoryId}/sessions",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var title = page.Locator("#session-explorer-title");
        var compare = page.Locator("#session-compare-mode");
        await Expect(title).ToHaveTextAsync(displayName);
        await Expect(compare).ToBeVisibleAsync();
        await compare.FocusAsync();
        await Expect(compare).ToBeFocusedAsync();
        var compareBox = Assert.IsType<LocatorBoundingBoxResult>(await compare.BoundingBoxAsync());
        Assert.True(compareBox.X >= 0 && compareBox.X + compareBox.Width <= 1366);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.Equal(host.Url + $"/repositories/{RepositoryId}/sessions", page.Url);
    }

    [Fact]
    public async Task UnassignedScopeUsesTheSameControllerAndPostsOnlyItsExactScope()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        string? requestBody = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            requestBody = route.Request.PostData;
            return route.FulfillAsync(Json("""{"schema_version":"local-monitor-sessions.response.v1","workspace_revision":"0000000000000000000000000000000000000000000000000000000000000000","items":[],"next_cursor":null}"""));
        });

        await page.GotoAsync(host.Url + "/sessions/unassigned", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("この範囲にはセッションがありません");

        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal("unassigned", body.RootElement.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("repository_id").ValueKind);
        Assert.Equal("active_only", body.RootElement.GetProperty("archive_scope").GetString());
    }

    [Fact]
    public async Task MissingExplorerAssetCannotNativeSubmitTransientValuesIntoNavigationOrNetworkUrls()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestUrls = new List<string>();
        page.Request += (_, request) => requestUrls.Add(request.Url);
        await page.RouteAsync("**/local-monitor-explorer.js", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 404,
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = "",
        }));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-search").FillAsync("transient-sensitive-query");
        await page.Locator("#session-model").FillAsync("transient-sensitive-model");
        await page.Locator("#session-search").PressAsync("Enter");
        await page.Locator("#session-explorer-filters button[type='submit']").PressAsync("Enter");
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");

        Assert.Equal(host.Url + "/sessions", page.Url);
        Assert.DoesNotContain(requestUrls, url => Uri.UnescapeDataString(url)
            .Contains("transient-sensitive", StringComparison.Ordinal));
        Assert.DoesNotContain(requestUrls, url => url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyboardOrderReachesFiltersRowsOverflowPaginationAndCompareActions()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        var firstPage = await SessionPageAsync(50, 0x5000, SessionCursor(DefaultSessionRequest(), 0x5001));
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(firstPage)));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        async Task TabToAsync(string selector)
        {
            await page.Keyboard.PressAsync("Tab");
            var expected = page.Locator(selector);
            var focused = await expected.EvaluateAsync<bool>("node => node === document.activeElement");
            var active = await page.EvaluateAsync<string>(
                "() => `${document.activeElement.tagName}#${document.activeElement.id}.${document.activeElement.className}`");
            Assert.True(focused, $"Expected keyboard focus on {selector}; active element was {active}.");
        }

        await page.Locator("#session-compare-mode").FocusAsync();
        await TabToAsync("#session-search");
        Assert.NotEqual("none", await page.Locator("#session-search")
            .EvaluateAsync<string>("node => getComputedStyle(node).outlineStyle"));
        await TabToAsync("#session-model");
        await TabToAsync("details:has(#session-from) > summary");
        await TabToAsync("details:has(#session-source) > summary");
        await TabToAsync("details:has(#session-status) > summary");
        await TabToAsync("details:has(#session-has-skill) > summary");
        await TabToAsync("#session-limit");
        await TabToAsync("#session-include-archived");
        await TabToAsync("#session-explorer-filters button[type='submit']");
        await TabToAsync(".local-monitor-session-table-region");
        await TabToAsync("[data-session-row]:first-child [data-session-open]");
        await TabToAsync("[data-session-row]:first-child .local-monitor-session-identity .local-monitor-session-fact-disclosure > summary");
        await TabToAsync("[data-session-row]:first-child [data-session-summary] .local-monitor-session-fact-disclosure > summary");
        await TabToAsync("[data-session-row]:first-child .local-monitor-session-row-actions > summary");
        await TabToAsync("[data-session-row]:nth-child(2) [data-session-open]");
        await page.Locator("[data-session-row]:last-child .local-monitor-session-row-actions > summary").FocusAsync();
        await TabToAsync("#session-load-more");

        await page.Locator("#session-compare-mode").FocusAsync();
        await page.Locator("#session-compare-mode").PressAsync("Enter");
        await Expect(page.Locator("[data-session-row]:first-child [data-cohort='a']")).ToBeFocusedAsync();
        await TabToAsync("[data-session-row]:first-child [data-cohort='b']");
        await TabToAsync("[data-session-row]:first-child [data-session-open]");
        await TabToAsync("[data-session-row]:first-child .local-monitor-session-identity .local-monitor-session-fact-disclosure > summary");
        await TabToAsync("[data-session-row]:first-child [data-session-summary] .local-monitor-session-fact-disclosure > summary");
        await TabToAsync("[data-session-row]:first-child .local-monitor-session-row-actions > summary");
        await TabToAsync("[data-session-row]:nth-child(2) [data-cohort='a']");
        await page.Locator("[data-session-row]:last-child .local-monitor-session-row-actions > summary").FocusAsync();
        await TabToAsync("#session-load-more");
        await TabToAsync("#session-compare-cancel");
        await TabToAsync("#session-compare-preview");
    }

    [Fact]
    public async Task SafeFiltersCursorBackAndReloadRoundTripWhileSearchAndModelRemainTransient()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(includeRepository: true));
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var bodies = new List<string>();
        var requestUrls = new List<string>();
        var consoleMessages = new List<string>();
        var pageErrors = new List<string>();
        page.Request += (_, request) => requestUrls.Add(request.Url);
        page.Console += (_, message) => consoleMessages.Add(message.Text);
        page.PageError += (_, error) => pageErrors.Add(error);
        await page.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            bodies.Add(route.Request.PostData!);
            using var request = JsonDocument.Parse(route.Request.PostData!);
            Assert.Equal(
                LocalMonitorV1SessionSearchParseStatus.Success,
                LocalMonitorV1SessionSearchRequestParser.Parse(
                    Encoding.UTF8.GetBytes(route.Request.PostData!), out var parsedRequest));
            var effectiveLimit = request.RootElement.GetProperty("limit").ValueKind == JsonValueKind.Null
                ? 50
                : request.RootElement.GetProperty("limit").GetInt32();
            var cursor = request.RootElement.GetProperty("cursor");
            var responseBody = cursor.ValueKind == JsonValueKind.Null
                ? await SessionPageAsync(effectiveLimit, 0x3000, SessionCursor(parsedRequest!, 0x3001))
                : await SessionPageAsync(1, 0x2000, null);
            await route.FulfillAsync(Json(responseBody));
        });

        await page.GotoAsync(
            host.Url + $"/repositories/{RepositoryId}/sessions?source=vscode&status=failed",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        using (var first = JsonDocument.Parse(Assert.Single(bodies)))
        {
            Assert.Equal("repository", first.RootElement.GetProperty("scope").GetString());
            Assert.Equal(RepositoryId, first.RootElement.GetProperty("repository_id").GetString());
            Assert.Equal("vscode", first.RootElement.GetProperty("source")[0].GetString());
            Assert.Equal("failed", first.RootElement.GetProperty("status")[0].GetString());
        }

        await page.Locator("#session-search").FillAsync("  機密にならない検索語  ");
        await page.Locator("#session-model").FillAsync("model,with,comma\n model-b ");
        await page.Locator("details:has(#session-from) summary").ClickAsync();
        await page.Locator("#session-from").FillAsync("2026-01-01T00:00:00.0000000+00:00");
        await page.Locator("#session-to").FillAsync("2026-02-01T00:00:00.0000000+00:00");
        await page.Locator("details:has(#session-from) summary").ClickAsync();
        await page.Locator("details:has(#session-source) summary").ClickAsync();
        await page.Locator("#session-source").SelectOptionAsync(["claude-code", "vscode"]);
        await page.Locator("details:has(#session-source) summary").ClickAsync();
        await page.Locator("details:has(#session-status) summary").ClickAsync();
        await page.Locator("#session-status").SelectOptionAsync("completed");
        await page.Locator("details:has(#session-status) summary").ClickAsync();
        await page.Locator("details:has(#session-has-skill) summary").ClickAsync();
        await page.Locator("#session-has-skill").SelectOptionAsync("true");
        await page.Locator("#session-has-subagent").SelectOptionAsync("false");
        await page.Locator("#session-has-error").SelectOptionAsync("true");
        await page.Locator("#session-has-retry").SelectOptionAsync("false");
        await page.Locator("details:has(#session-has-skill) summary").ClickAsync();
        await page.Locator("#session-limit").SelectOptionAsync("100");
        await page.Locator("#session-include-archived").CheckAsync();
        var filterRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await filterRequest;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(100);

        Assert.Contains("status=completed", page.Url, StringComparison.Ordinal);
        Assert.Contains("from=2026-01-01T00:00:00.0000000%2B00:00", page.Url, StringComparison.Ordinal);
        Assert.Contains("to=2026-02-01T00:00:00.0000000%2B00:00", page.Url, StringComparison.Ordinal);
        Assert.Contains("source=claude-code&source=vscode", page.Url, StringComparison.Ordinal);
        Assert.Contains("has_skill=true", page.Url, StringComparison.Ordinal);
        Assert.Contains("has_subagent=false", page.Url, StringComparison.Ordinal);
        Assert.Contains("has_error=true", page.Url, StringComparison.Ordinal);
        Assert.Contains("has_retry=false", page.Url, StringComparison.Ordinal);
        Assert.Contains("archive_scope=include_archived", page.Url, StringComparison.Ordinal);
        Assert.Contains("source=vscode", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("機密にならない検索語", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("model,with,comma", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("limit=", page.Url, StringComparison.Ordinal);
        using (var dynamicBody = JsonDocument.Parse(bodies[^1]))
        {
            Assert.Equal("  機密にならない検索語  ", dynamicBody.RootElement.GetProperty("q").GetString());
            Assert.Equal(new[] { "model,with,comma", " model-b " }, dynamicBody.RootElement.GetProperty("model").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("2026-01-01T00:00:00.0000000+00:00", dynamicBody.RootElement.GetProperty("from").GetString());
            Assert.Equal("2026-02-01T00:00:00.0000000+00:00", dynamicBody.RootElement.GetProperty("to").GetString());
            Assert.Equal(new[] { "claude-code", "vscode" }, dynamicBody.RootElement.GetProperty("source").EnumerateArray().Select(item => item.GetString()));
            Assert.True(dynamicBody.RootElement.GetProperty("has_skill").GetBoolean());
            Assert.False(dynamicBody.RootElement.GetProperty("has_subagent").GetBoolean());
            Assert.True(dynamicBody.RootElement.GetProperty("has_error").GetBoolean());
            Assert.False(dynamicBody.RootElement.GetProperty("has_retry").GetBoolean());
            Assert.Equal(100, dynamicBody.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(JsonValueKind.Null, dynamicBody.RootElement.GetProperty("cursor").ValueKind);
        }
        var browserState = await page.EvaluateAsync<string>("""
            async () => JSON.stringify({
              history: history.state,
              localStorage: Object.entries(localStorage),
              sessionStorage: Object.entries(sessionStorage),
              cacheKeys: "caches" in window ? await caches.keys() : [],
              databaseNames: indexedDB.databases
                ? (await indexedDB.databases()).map(database => database.name)
                : []
            })
            """);
        foreach (var transient in new[] { "機密にならない検索語", "model,with,comma", " model-b " })
        {
            Assert.DoesNotContain(transient, browserState, StringComparison.Ordinal);
            Assert.DoesNotContain(requestUrls, url =>
                Uri.UnescapeDataString(url).Contains(transient, StringComparison.Ordinal));
            Assert.DoesNotContain(consoleMessages, message => message.Contains(transient, StringComparison.Ordinal));
            Assert.DoesNotContain(pageErrors, message => message.Contains(transient, StringComparison.Ordinal));
        }
        Assert.DoesNotContain(await page.Context.CookiesAsync(), cookie =>
            cookie.Value.Contains("機密にならない検索語", StringComparison.Ordinal)
            || cookie.Value.Contains("model,with,comma", StringComparison.Ordinal));

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        await Expect(page.Locator("#session-search")).ToHaveValueAsync("");
        await Expect(page.Locator("#session-model")).ToHaveValueAsync("");
        using (var reloaded = JsonDocument.Parse(bodies[^1]))
        {
            Assert.Equal(JsonValueKind.Null, reloaded.RootElement.GetProperty("q").ValueKind);
            Assert.Empty(reloaded.RootElement.GetProperty("model").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, reloaded.RootElement.GetProperty("limit").ValueKind);
            Assert.Equal("2026-01-01T00:00:00.0000000+00:00", reloaded.RootElement.GetProperty("from").GetString());
            Assert.Equal("2026-02-01T00:00:00.0000000+00:00", reloaded.RootElement.GetProperty("to").GetString());
            Assert.Equal(new[] { "claude-code", "vscode" }, reloaded.RootElement.GetProperty("source").EnumerateArray().Select(item => item.GetString()));
        }

        var pageRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-load-more").ClickAsync();
        await pageRequest;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        Assert.Contains("cursor=", page.Url, StringComparison.Ordinal);
        using (var paged = JsonDocument.Parse(bodies[^1]))
            Assert.Equal(JsonValueKind.String, paged.RootElement.GetProperty("cursor").ValueKind);

        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
        await page.Locator("#session-search").FillAsync("memory-only");
        var memoryFilterRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await memoryFilterRequest;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        var memoryPageRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-load-more").ClickAsync();
        await memoryPageRequest;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("memory-only", page.Url, StringComparison.Ordinal);
        using (var memoryPage = JsonDocument.Parse(bodies[^1]))
        {
            Assert.Equal("memory-only", memoryPage.RootElement.GetProperty("q").GetString());
            Assert.Equal(JsonValueKind.String, memoryPage.RootElement.GetProperty("cursor").ValueKind);
        }
    }

    [Fact]
    public async Task InvalidServerCursorOffersAnExplicitFirstPageRecovery()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var responseBody = await SessionPageAsync(50, 0x5000, SessionCursor(DefaultSessionRequest(), 0x5001));
        var calls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            calls++;
            return calls == 2
                ? route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 400,
                    ContentType = "application/json; charset=utf-8",
                    Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                    Body = "{\"error\":\"invalid_cursor\"}",
                })
                : route.FulfillAsync(Json(responseBody));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        await page.Locator("#session-load-more").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("ページ情報を使用できません");
        Assert.Contains("cursor=", page.Url, StringComparison.Ordinal);

        await page.Locator("[data-clear-stale-cursor]").ClickAsync();

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(50);
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task InvalidRequestDoesNotMasqueradeAsAStaleCursorRecovery()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var cursor = SessionCursor(DefaultSessionRequest(), 0x5001);
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 400,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = "{\"error\":\"invalid_request\"}",
        }));

        await page.GotoAsync(
            host.Url + $"/sessions?cursor={cursor}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await Expect(page.Locator("[data-clear-stale-cursor]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task FailedPaginationRetriesTheExactAttemptedCursorForUrlAndDocumentOnlyPaging()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var urlPage = await browser.NewPageAsync();
        var urlBodies = new List<string>();
        await urlPage.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            urlBodies.Add(route.Request.PostData!);
            Assert.Equal(
                LocalMonitorV1SessionSearchParseStatus.Success,
                LocalMonitorV1SessionSearchRequestParser.Parse(
                    Encoding.UTF8.GetBytes(route.Request.PostData!), out var parsed));
            using var request = JsonDocument.Parse(route.Request.PostData!);
            var cursor = request.RootElement.GetProperty("cursor");
            if (cursor.ValueKind == JsonValueKind.Null)
                await route.FulfillAsync(Json(await SessionPageAsync(50, 0x5000, SessionCursor(parsed!, 0x5001))));
            else if (urlBodies.Count == 2)
                await route.FulfillAsync(Error(503, "persistence_busy"));
            else
                await route.FulfillAsync(Json(await SessionPageAsync(1, 0x4000, null)));
        });

        await urlPage.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await urlPage.Locator("#session-load-more").ClickAsync();
        await Expect(urlPage.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await urlPage.Locator("#session-explorer-status button").PressAsync("Enter");
        await Expect(urlPage.Locator("[data-session-row]")).ToHaveCountAsync(1);
        using (var failed = JsonDocument.Parse(urlBodies[^2]))
        using (var retried = JsonDocument.Parse(urlBodies[^1]))
            Assert.Equal(
                failed.RootElement.GetProperty("cursor").GetString(),
                retried.RootElement.GetProperty("cursor").GetString());
        Assert.Contains("cursor=", urlPage.Url, StringComparison.Ordinal);

        var memoryPage = await browser.NewPageAsync();
        var memoryBodies = new List<string>();
        await memoryPage.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            memoryBodies.Add(route.Request.PostData!);
            Assert.Equal(
                LocalMonitorV1SessionSearchParseStatus.Success,
                LocalMonitorV1SessionSearchRequestParser.Parse(
                    Encoding.UTF8.GetBytes(route.Request.PostData!), out var parsed));
            using var request = JsonDocument.Parse(route.Request.PostData!);
            var cursor = request.RootElement.GetProperty("cursor");
            var query = request.RootElement.GetProperty("q");
            if (query.ValueKind == JsonValueKind.Null)
                await route.FulfillAsync(Json(await FinalGoldenAsync()));
            else if (cursor.ValueKind == JsonValueKind.Null)
                await route.FulfillAsync(Json(await SessionPageAsync(50, 0x3000, SessionCursor(parsed!, 0x3001))));
            else if (memoryBodies.Count == 3)
                await route.FulfillAsync(Error(503, "persistence_busy"));
            else
                await route.FulfillAsync(Json(await SessionPageAsync(1, 0x2000, null)));
        });

        await memoryPage.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await memoryPage.Locator("#session-search").FillAsync("document-only");
        await memoryPage.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(memoryPage.Locator("[data-session-row]")).ToHaveCountAsync(50);
        await memoryPage.Locator("#session-load-more").ClickAsync();
        await Expect(memoryPage.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await memoryPage.Locator("#session-explorer-status button").PressAsync("Enter");
        await Expect(memoryPage.Locator("[data-session-row]")).ToHaveCountAsync(1);
        using (var failed = JsonDocument.Parse(memoryBodies[^2]))
        using (var retried = JsonDocument.Parse(memoryBodies[^1]))
        {
            Assert.Equal("document-only", retried.RootElement.GetProperty("q").GetString());
            Assert.Equal(
                failed.RootElement.GetProperty("cursor").GetString(),
                retried.RootElement.GetProperty("cursor").GetString());
        }
        Assert.DoesNotContain("cursor=", memoryPage.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADelayedFilterReadCannotReuseThePreviousPageCursorOrRestoreItsRows()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var delayedStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectionCalls = 0;
        var initialPage = await SessionPageAsync(50, 0x5000, SessionCursor(DefaultSessionRequest(), 0x5001));
        var filteredPage = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            collectionCalls++;
            using var body = JsonDocument.Parse(route.Request.PostData!);
            if (body.RootElement.GetProperty("q").ValueKind == JsonValueKind.Null)
            {
                await route.FulfillAsync(Json(initialPage));
                return;
            }
            delayedStarted.TrySetResult(true);
            await releaseDelayed.Task;
            await route.FulfillAsync(Json(filteredPage));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#session-load-more")).ToBeVisibleAsync();
        await page.Locator("#session-search").FillAsync("delayed");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await delayedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Expect(page.Locator("#session-load-more")).ToBeHiddenAsync();
            await Expect(page.Locator("#session-load-more")).ToBeDisabledAsync();
            await page.Locator("#session-load-more").DispatchEventAsync("click");
            Assert.Equal(2, collectionCalls);
        }
        finally
        {
            releaseDelayed.TrySetResult(true);
        }

        await Expect(page.Locator($"[data-session-row][data-session-id='{SessionId}']")).ToHaveCountAsync(1);
        Assert.Equal(2, collectionCalls);
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareModeUsesExplicitABDraftsAndRequiresRepositoryScope()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestUrls = new List<string>();
        page.Request += (_, request) => requestUrls.Add(request.Url);
        var responseBody = await TwoSessionsAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(responseBody)));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(2);
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(0);

        await page.Locator("#session-compare-mode").PressAsync("Enter");
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(4);
        await Expect(page.Locator("#session-compare-bar")).ToBeVisibleAsync();
        Assert.True(await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']")
            .EvaluateAsync<bool>("node => node === document.activeElement"));
        Assert.Contains("mode=compare", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionId, page.Url, StringComparison.Ordinal);

        await page.Locator("#session-compare-cancel").PressAsync("Enter");
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(2);
        Assert.True(await page.Locator("#session-compare-mode")
            .EvaluateAsync<bool>("node => node === document.activeElement"));
        await page.Locator("#session-compare-mode").PressAsync("Enter");
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(4);

        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='b']").CheckAsync();
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("同じセッション");
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='b']").UncheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 1件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 1件");
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("リポジトリ別の一覧");
        await Expect(page.Locator("#session-compare-preview")).ToHaveAttributeAsync("aria-disabled", "true");
        await page.Locator("#session-compare-preview").FocusAsync();
        await Expect(page.Locator("#session-compare-preview")).ToBeFocusedAsync();
        await page.Locator("#session-compare-preview").PressAsync("Enter");
        await Expect(page.Locator("#session-compare-preview")).Not.ToHaveAttributeAsync("data-owner-boundary", "missing");
        Assert.DoesNotContain(requestUrls, url => url.Contains("comparison", StringComparison.OrdinalIgnoreCase));

        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(0);
        await page.GoForwardAsync(new PageGoForwardOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(4);
        Assert.Equal(0, await page.Locator("[data-cohort]:checked").CountAsync());

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-cohort]")).ToHaveCountAsync(4);
        Assert.Equal(0, await page.Locator("[data-cohort]:checked").CountAsync());
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 0件");
        Assert.False(await page.EvaluateAsync<bool>(
            "ids => [...Object.values(localStorage), ...Object.values(sessionStorage), JSON.stringify(history.state)].some(value => ids.some(id => value.includes(id)))",
            new[] { SessionId, SecondSessionId }));
    }

    [Fact]
    public async Task InvalidOwnerSuccessResponseDoesNotOptimisticallyMutateOrRefreshTheRow()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var collectionCalls = 0;
        string? actionBody = null;
        IReadOnlyDictionary<string, string>? actionHeaders = null;
        var responseBody = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(responseBody));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            actionBody = route.Request.PostData;
            actionHeaders = route.Request.Headers;
            return route.FulfillAsync(Json("{}"));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").PressAsync("Enter");
        await page.Locator("[data-session-assignment]").ClickAsync();

        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("操作を完了できませんでした");
        Assert.Equal(1, collectionCalls);
        Assert.Equal(
            $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SessionId}\",\"expected_revision\":2,\"action\":\"explicitly_unassign\",\"repository_id\":null}}",
            actionBody);
        Assert.NotNull(actionHeaders);
        Assert.Equal("local-monitor", actionHeaders["x-monitor-csrf"]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", actionHeaders["idempotency-key"]);
        await Expect(page.Locator("[data-session-status]")).ToContainTextAsync("実行中");
        await Expect(page.Locator("[data-session-assignment]")).ToHaveTextAsync("割り当てを解除");
    }

    [Fact]
    public async Task ResumeAutomaticRejectsAContradictoryManualOwnerSuccess()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var collectionCalls = 0;
        string? ownerBody = null;
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var assignment = document["items"]![0]!["assignment"]!;
        assignment["state"] = "explicitly_unassigned";
        assignment["authority"] = "manual";
        assignment["revision"] = 2;
        assignment["repository_id"] = null;
        assignment["candidate_repository_ids"] = new JsonArray();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(Canonical(document)));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            ownerBody = route.Request.PostData;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":3,"state":"assigned","authority":"manual","repository_id":"{{CandidateRepositoryOne}}","conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000+00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        await page.Locator("[data-session-assignment]").ClickAsync();

        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("操作を完了できませんでした");
        Assert.Equal(1, collectionCalls);
        Assert.Contains("\"action\":\"resume_automatic\"", ownerBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContradictoryArchiveRelationsFailClosedBeforeRenderingRows()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var activeWithSessionReason = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        activeWithSessionReason["items"]![0]!["archive"]!["effectively_eligible"] = false;
        activeWithSessionReason["items"]![0]!["archive"]!["exclusion_reason"] = "session_archived";
        var archivedWithoutSessionReason = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        archivedWithoutSessionReason["items"]![0]!["archive"]!["state"] = "archived";
        archivedWithoutSessionReason["items"]![0]!["archive"]!["revision"] = 1;
        var repositoryReasonWithoutAssignment = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        repositoryReasonWithoutAssignment["items"]![0]!["archive"]!["effectively_eligible"] = false;
        repositoryReasonWithoutAssignment["items"]![0]!["archive"]!["exclusion_reason"] = "repository_archived";
        var assignment = repositoryReasonWithoutAssignment["items"]![0]!["assignment"]!;
        assignment["state"] = "unassigned";
        assignment["authority"] = "none";
        assignment["repository_id"] = null;
        assignment["candidate_repository_ids"] = new JsonArray();
        var responses = new[]
        {
            Canonical(activeWithSessionReason),
            Canonical(archivedWithoutSessionReason),
            Canonical(repositoryReasonWithoutAssignment),
        };
        var calls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(responses[calls++])));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        for (var index = 0; index < responses.Length; index++)
        {
            await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
            await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
            if (index + 1 < responses.Length)
                await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ArchivedAndInconsistentFactsRemainDistinctAndAreExcludedFromCompare()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var responseBody = await ArchivedAndEligibleAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route => route.FulfillAsync(Json(responseBody)));

        await page.GotoAsync(
            host.Url + "/sessions?archive_scope=include_archived",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(3);
        var archived = page.Locator("[data-session-id='018f0000-0000-7000-8000-000000000001']");
        var repositoryArchived = page.Locator("[data-session-id='018f0000-0000-7000-8000-000000000004']");
        await Expect(archived.Locator("[data-session-label]")).ToContainTextAsync("2026");
        await Expect(archived.Locator("[data-session-status]")).ToContainTextAsync("セッションをアーカイブ済み");
        await Expect(repositoryArchived.Locator("[data-session-status]"))
            .ToContainTextAsync("リポジトリをアーカイブ済み");
        foreach (var status in new[] { archived, repositoryArchived })
        {
            var reason = status.Locator("[data-session-status] small");
            Assert.True(await reason.EvaluateAsync<bool>(
                "node => node.scrollWidth <= node.clientWidth && getComputedStyle(node).whiteSpace === 'normal' && getComputedStyle(node.parentElement).overflow === 'visible'"));
        }
        await Expect(archived.Locator("[data-session-tokens]")).ToContainTextAsync("内訳を表示できません");
        var archivedFacts = archived.Locator(".local-monitor-session-identity .local-monitor-session-fact-disclosure > summary");
        await archivedFacts.PressAsync("Enter");
        await Expect(archived.Locator("[data-capture-note='token_inconsistent']"))
            .ToContainTextAsync("内訳を表示できません");

        await page.Locator("#session-compare-mode").ClickAsync();
        await archived.Locator("[data-cohort='a']").CheckAsync();
        await repositoryArchived.Locator("[data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='b']").CheckAsync();
        await Expect(page.Locator("#session-compare-validation")).Not.ToContainTextAsync("セッションのアーカイブ除外");
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("リポジトリのアーカイブ除外 1件");
        var reasonDetails = page.Locator("[data-compare-validation-details]");
        await Expect(reasonDetails).ToBeHiddenAsync();
        var compareLayout = await page.EvaluateAsync<double[]>("""
            () => {
              const bar = document.querySelector('#session-compare-bar').getBoundingClientRect();
              return [bar.height, bar.left, bar.right, innerWidth, document.documentElement.scrollWidth];
            }
            """);
        Assert.Equal(56, compareLayout[0]);
        Assert.True(compareLayout[1] >= 0 && compareLayout[2] <= compareLayout[3]);
        Assert.True(compareLayout[4] <= compareLayout[3]);
    }

    [Fact]
    public async Task ConfirmedArchiveAndAssignmentOwnerActionsUseExactIdentityRevisionThenRefresh()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var collectionCalls = 0;
        string? archiveBody = null;
        string? assignmentBody = null;
        IReadOnlyDictionary<string, string>? assignmentHeaders = null;
        var responseBody = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(responseBody));
        });
        await page.RouteAsync("**/api/local-monitor/v1/archive-actions", route =>
        {
            archiveBody = route.Request.PostData;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-archive-action.response.v1","action":"archive","target_kind":"session","targets":[{"target_id":"{{SessionId}}","state":"archived","revision":1,"archived_at":"2026-01-03T00:00:00.0000000+00:00","updated_at":"2026-01-03T00:00:00.0000000+00:00"}]}"""));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            assignmentBody = route.Request.PostData;
            assignmentHeaders = route.Request.Headers;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":3,"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000+00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        var archiveRefresh = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("[data-session-archive]").PressAsync("Enter");
        await archiveRefresh;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("表示しています");
        await Expect(page.Locator("[data-session-archive]")).ToBeFocusedAsync();
        Assert.Equal(2, collectionCalls);
        Assert.Equal(
            $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{SessionId}\",\"expected_revision\":0}}]}}",
            archiveBody);

        var assignmentRefresh = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("[data-session-assignment]").PressAsync("Enter");
        await assignmentRefresh;
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("表示しています");
        await Expect(page.Locator("[data-session-assignment]")).ToBeFocusedAsync();
        Assert.Equal(3, collectionCalls);
        Assert.Equal(
            $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SessionId}\",\"expected_revision\":2,\"action\":\"explicitly_unassign\",\"repository_id\":null}}",
            assignmentBody);
        Assert.NotNull(assignmentHeaders);
        Assert.Equal("local-monitor", assignmentHeaders["x-monitor-csrf"]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", assignmentHeaders["idempotency-key"]);
    }

    [Fact]
    public async Task RestoreKeepsThePriorCollectionAuthorityWithoutBlockingExplicitArchivedInclusion()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var collectionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            collectionCalls++;
            if (collectionCalls <= 2) await route.FulfillAsync(Json(await ArchivedAndEligibleAsync()));
            else await route.FulfillAsync(Error(503, "persistence_busy"));
        });
        await page.RouteAsync("**/api/local-monitor/v1/archive-actions", route => route.FulfillAsync(Json(
            """{"schema_version":"local-archive-action.response.v1","action":"restore","target_kind":"session","targets":[{"target_id":"018f0000-0000-7000-8000-000000000001","state":"active","revision":2,"archived_at":null,"updated_at":"2026-01-03T00:00:00.0000000+00:00"}]}""")));

        await page.GotoAsync(host.Url + "/sessions?archive_scope=include_archived",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-compare-mode").ClickAsync();
        var archived = page.Locator("[data-session-row][data-session-id='018f0000-0000-7000-8000-000000000001']");
        await archived.Locator("[data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-row][data-session-id='{SessionId}'] [data-cohort='b']").CheckAsync();
        await Expect(page.Locator("#session-compare-validation")).Not.ToContainTextAsync("セッションのアーカイブ除外");
        await archived.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        await archived.Locator("[data-session-archive]").ClickAsync();

        await Expect(page.Locator("#session-explorer-status"))
            .ToContainTextAsync("操作は完了しましたが、一覧を更新できませんでした");
        await Expect(page.Locator("#session-compare-validation")).Not.ToContainTextAsync("セッションのアーカイブ除外");
        Assert.Equal(3, collectionCalls);
    }

    [Fact]
    public async Task AssignmentPickerUsesExactOwnerCollectionsCandidatesAndSameNameRepositoryIdentity()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var sessionDocument = JsonNode.Parse(await TwoSessionsAsync())!.AsObject();
        var unassigned = sessionDocument["items"]![1]!["assignment"]!;
        unassigned["state"] = "unassigned";
        unassigned["authority"] = "none";
        unassigned["revision"] = 0;
        unassigned["repository_id"] = null;
        unassigned["candidate_repository_ids"] = new JsonArray();
        var sessionBytes = Canonical(sessionDocument);
        var repositoryBytes = RepositoryCollection();
        var repositoryRequests = new List<string>();
        var collectionCalls = 0;
        var ownerActionCalls = 0;
        string? ownerBody = null;
        IReadOnlyDictionary<string, string>? ownerHeaders = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(sessionBytes));
        });
        await page.RouteAsync("**/api/local-monitor/v1/repositories?*", route =>
        {
            repositoryRequests.Add(route.Request.Url);
            return route.FulfillAsync(Json(repositoryBytes));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            ownerActionCalls++;
            ownerBody = route.Request.PostData;
            ownerHeaders = route.Request.Headers;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SecondSessionId}}","assignment_revision":1,"state":"assigned","authority":"manual","repository_id":"{{CandidateRepositoryTwo}}","conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000\u002B00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions/unassigned", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var conflictRow = page.Locator($"[data-session-id='{SessionId}']");
        await conflictRow.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        var conflictPicker = conflictRow.Locator("[data-session-assignment-picker]");
        await conflictPicker.PressAsync("Enter");
        await Expect(page.Locator("#session-assignment-dialog")).ToBeVisibleAsync();
        await Expect(page.Locator("#session-assignment-choices input")).ToHaveCountAsync(3);
        await Expect(page.Locator("#session-assignment-choices .local-monitor-session-assignment-name"))
            .ToHaveTextAsync(["同じ名前", "同じ名前", "同じ名前"]);
        await Expect(page.Locator("#session-assignment-choices small").Filter(new() { HasText = "記録された候補" }))
            .ToHaveCountAsync(2);
        await Expect(page.Locator($"#session-assignment-choices input[value='{ArchivedRepository}']"))
            .ToBeDisabledAsync();
        Assert.Equal(0, await page.Locator("#session-assignment-choices input:checked").CountAsync());
        await page.Locator("#session-assignment-cancel").PressAsync("Enter");
        await Expect(conflictPicker).ToBeFocusedAsync();
        await conflictRow.Locator(".local-monitor-session-row-actions > summary").PressAsync("Enter");

        var unassignedRow = page.Locator($"[data-session-id='{SecondSessionId}']");
        await unassignedRow.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        var assignPicker = unassignedRow.Locator("[data-session-assignment-picker]");
        await assignPicker.PressAsync("Enter");
        await page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryTwo}']").CheckAsync();
        var refresh = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-assignment-submit").PressAsync("Enter");
        await refresh;

        await Expect(assignPicker).ToBeFocusedAsync();
        Assert.Equal(2, collectionCalls);
        Assert.Equal(1, ownerActionCalls);
        Assert.Equal(
            $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SecondSessionId}\",\"expected_revision\":0,\"action\":\"assign\",\"repository_id\":\"{CandidateRepositoryTwo}\"}}",
            ownerBody);
        Assert.NotNull(ownerHeaders);
        Assert.Equal("local-monitor", ownerHeaders["x-monitor-csrf"]);
        Assert.Matches("^lrc1_[A-Za-z0-9_-]{43}$", ownerHeaders["idempotency-key"]);
        Assert.All(repositoryRequests, request => Assert.Equal(
            host.Url + "/api/local-monitor/v1/repositories?archive_scope=include_archived&limit=200",
            request));
        Assert.DoesNotContain("同じ名前", string.Join(' ', repositoryRequests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignmentPickerCannotSubmitTheAlreadyCurrentExactRepository()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var sessionDocument = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var assignment = sessionDocument["items"]![0]!["assignment"]!;
        assignment["state"] = "assigned";
        assignment["authority"] = "manual";
        assignment["revision"] = 4;
        assignment["repository_id"] = CandidateRepositoryOne;
        assignment["candidate_repository_ids"] = new JsonArray();
        var ownerActionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(sessionDocument))));
        await page.RouteAsync("**/api/local-monitor/v1/repositories?*", route =>
            route.FulfillAsync(Json(RepositoryCollection())));
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            ownerActionCalls++;
            return route.FulfillAsync(Error(500, "must_not_submit"));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-compare-mode").ClickAsync();
        await page.WaitForURLAsync(host.Url + "/sessions?mode=compare");
        var row = page.Locator($"[data-session-id='{SessionId}']");
        await row.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        await row.Locator("[data-session-assignment-picker]").ClickAsync();

        var submit = page.Locator("#session-assignment-submit");
        await Expect(page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryOne}']"))
            .ToBeCheckedAsync();
        await Expect(submit).ToBeDisabledAsync();
        await page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryTwo}']").CheckAsync();
        await Expect(submit).ToBeEnabledAsync();
        await Expect(page.Locator("#session-assignment-choices"))
            .ToContainTextAsync($"ローカルID {CandidateRepositoryOne}");
        await Expect(page.Locator("#session-assignment-choices"))
            .ToContainTextAsync($"ローカルID {CandidateRepositoryTwo}");
        await page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryOne}']").CheckAsync();
        await Expect(submit).ToBeDisabledAsync();
        Assert.Equal(0, ownerActionCalls);
        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#session-assignment-dialog")).Not.ToBeVisibleAsync();
        Assert.Equal(host.Url + "/sessions", page.Url);
    }

    [Fact]
    public async Task AssignmentPickerKeepsTheExactSelectedRepositoryAcrossCandidatePagination()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var sessionDocument = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var assignment = sessionDocument["items"]![0]!["assignment"]!;
        assignment["state"] = "assigned";
        assignment["authority"] = "manual";
        assignment["revision"] = 4;
        assignment["repository_id"] = CandidateRepositoryOne;
        assignment["candidate_repository_ids"] = new JsonArray();
        var cursor = new string('C', 135);
        var repositoryRequests = 0;

        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(sessionDocument))));
        await page.RouteAsync("**/api/local-monitor/v1/repositories?*", route =>
        {
            repositoryRequests++;
            var secondPage = route.Request.Url.Contains("after=", StringComparison.Ordinal);
            var repositories = new JsonArray();
            if (secondPage)
            {
                repositories.Add(Repository("018f0000-0000-7000-8000-000000000103"));
            }
            else
            {
                for (var index = 1; index <= 198; index++)
                    repositories.Add(Repository($"018f0000-0000-7000-8000-{index:x12}"));
                repositories.Add(Repository(CandidateRepositoryOne));
                repositories.Add(Repository(CandidateRepositoryTwo));
            }

            var response = new JsonObject
            {
                ["schema_version"] = "local-monitor-repositories.response.v1",
                ["workspace_revision"] = new string('b', 64),
                ["repositories"] = repositories,
                ["all_session_count"] = 2,
                ["unassigned_active_session_count"] = 2,
                ["archived_repository_count"] = 0,
                ["next_cursor"] = secondPage ? null : cursor,
            };
            return route.FulfillAsync(Json(Canonical(response)));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var row = page.Locator($"[data-session-id='{SessionId}']");
        await row.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        await row.Locator("[data-session-assignment-picker]").ClickAsync();
        await page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryTwo}']").CheckAsync();
        await page.Locator("#session-assignment-load-more").PressAsync("Enter");

        await Expect(page.Locator($"#session-assignment-choices input[value='{CandidateRepositoryTwo}']"))
            .ToBeCheckedAsync();
        await Expect(page.Locator("#session-assignment-submit")).ToBeEnabledAsync();
        Assert.Equal(2, repositoryRequests);

        static JsonObject Repository(string id) => new()
        {
            ["repository_id"] = id,
            ["display_name"] = "同じ名前",
            ["archive_state"] = "active",
            ["archive_revision"] = 0,
            ["active_session_count"] = 1,
            ["last_observed_at"] = "2026-01-02T00:00:02.0000000+00:00",
            ["assignment_conflict_count"] = 0,
            ["repository_revision"] = new string(id[^1], 64),
        };
    }

    [Fact]
    public async Task ConfirmedOwnerMutationKeepsAnExplicitRefreshRecoveryWhenTheCollectionRefreshFails()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var collectionCalls = 0;
        var ownerActionCalls = 0;
        var responseBody = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return collectionCalls == 2
                ? route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json; charset=utf-8",
                    Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                    Body = "{\"error\":\"persistence_busy\"}",
                })
                : route.FulfillAsync(Json(responseBody));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            ownerActionCalls++;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":3,"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000\u002B00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        await page.Locator("[data-session-assignment]").ClickAsync();

        await Expect(page.Locator("#session-explorer-status"))
            .ToContainTextAsync("操作は完了しましたが、一覧を更新できませんでした");
        await Expect(page.Locator("[data-refresh-after-owner-action]")).ToBeVisibleAsync();
        await page.Locator("[data-refresh-after-owner-action]").PressAsync("Enter");
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-assignment]")).ToBeFocusedAsync();
        Assert.Equal(3, collectionCalls);
        Assert.Equal(1, ownerActionCalls);
    }

    [Fact]
    public async Task FilterNavigationDuringOwnerMutationStillRefreshesTheConfirmedCurrentScope()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var initial = await FinalGoldenAsync();
        var updated = JsonNode.Parse(initial)!.AsObject();
        var updatedAssignment = updated["items"]![0]!["assignment"]!;
        updatedAssignment["state"] = "explicitly_unassigned";
        updatedAssignment["authority"] = "manual";
        updatedAssignment["revision"] = 3;
        updatedAssignment["repository_id"] = null;
        updatedAssignment["candidate_repository_ids"] = new JsonArray();
        var collectionCalls = 0;
        var ownerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            var call = Interlocked.Increment(ref collectionCalls);
            return route.FulfillAsync(Json(call >= 3 ? Canonical(updated) : initial));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", async route =>
        {
            ownerStarted.TrySetResult(true);
            await releaseOwner.Task;
            await route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":3,"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000+00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        await page.Locator("[data-session-assignment]").ClickAsync();
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var routeRead = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-include-archived").CheckAsync();
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await routeRead;
        await page.WaitForURLAsync(host.Url + "/sessions?archive_scope=include_archived");
        await Expect(page.Locator("[data-session-archive]")).ToBeDisabledAsync();
        await Expect(page.Locator("[data-session-assignment]")).ToBeDisabledAsync();
        await Expect(page.Locator("[data-session-assignment-picker]")).ToBeDisabledAsync();
        await Expect(page.Locator("#session-assignment-dialog")).Not.ToBeVisibleAsync();
        releaseOwner.TrySetResult(true);

        await Expect(page.Locator("[data-session-assignment]")).ToHaveTextAsync("自動割り当てを再開");
        await Expect(page.Locator("[data-session-assignment]")).ToBeFocusedAsync();
        Assert.Equal(3, collectionCalls);
    }

    [Fact]
    public async Task TransientPageTwoMutationRefreshUsesTheNewFilterFirstPageNotThePriorCursor()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var oldCursor = SessionCursor(CohortRequest(), 0x4001);
        var firstPage = await SessionPageAsync(100, 0x5000, oldCursor);
        var secondPage = await SessionPageAsync(1, 0x4000, null);
        var refreshedPage = JsonNode.Parse(secondPage)!.AsObject();
        var refreshedAssignment = refreshedPage["items"]![0]!["assignment"]!;
        refreshedAssignment["state"] = "explicitly_unassigned";
        refreshedAssignment["authority"] = "manual";
        refreshedAssignment["revision"] = 3;
        refreshedAssignment["repository_id"] = null;
        refreshedAssignment["candidate_repository_ids"] = new JsonArray();
        var pageTwoSessionId = $"018f0000-0000-7000-8000-{0x4001:x12}";
        var collectionCalls = 0;
        string? confirmedRefreshBody = null;
        var ownerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newFilterReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSupersededRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmedRefreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/local-monitor/v1/sessions", async route =>
        {
            var call = Interlocked.Increment(ref collectionCalls);
            switch (call)
            {
                case 1:
                    await route.FulfillAsync(Json(await FinalGoldenAsync()));
                    break;
                case 2:
                    await route.FulfillAsync(Json(firstPage));
                    break;
                case 3:
                    await route.FulfillAsync(Json(secondPage));
                    break;
                case 4:
                    newFilterReadStarted.TrySetResult(true);
                    await releaseSupersededRead.Task;
                    try { await route.FulfillAsync(Json(secondPage)); }
                    catch (PlaywrightException) { }
                    break;
                default:
                    confirmedRefreshBody = route.Request.PostData;
                    confirmedRefreshStarted.TrySetResult(true);
                    await route.FulfillAsync(Json(Canonical(refreshedPage)));
                    break;
            }
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", async route =>
        {
            ownerStarted.TrySetResult(true);
            await releaseOwner.Task;
            await route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{pageTwoSessionId}}","assignment_revision":3,"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000+00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-limit").SelectOptionAsync("100");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await page.Locator("#session-load-more").ClickAsync();
        var pageTwo = page.Locator($"[data-session-id='{pageTwoSessionId}']");
        await Expect(pageTwo).ToHaveCountAsync(1);
        await pageTwo.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        await pageTwo.Locator("[data-session-assignment]").ClickAsync();
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await page.Locator("#session-search").FillAsync("new-filter");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await newFilterReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOwner.TrySetResult(true);
        await confirmedRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSupersededRead.TrySetResult(true);

        await Expect(page.Locator($"[data-session-id='{pageTwoSessionId}'] [data-session-assignment]"))
            .ToHaveTextAsync("自動割り当てを再開");
        using var refresh = JsonDocument.Parse(confirmedRefreshBody!);
        Assert.Equal("new-filter", refresh.RootElement.GetProperty("q").GetString());
        Assert.Equal(JsonValueKind.Null, refresh.RootElement.GetProperty("cursor").ValueKind);
        Assert.Equal(5, collectionCalls);
    }

    [Fact]
    public async Task ConfirmedOwnerMutationAcknowledgesSuccessWhenItsPagedRefreshCursorExpires()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var cursor = SessionCursor(DefaultSessionRequest(), 0x5001);
        var responseBody = await FinalGoldenAsync();
        var collectionCalls = 0;
        var ownerActionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return collectionCalls == 2
                ? route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 400,
                    ContentType = "application/json; charset=utf-8",
                    Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                    Body = "{\"error\":\"invalid_cursor\"}",
                })
                : route.FulfillAsync(Json(responseBody));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            ownerActionCalls++;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":3,"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000\u002B00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions?cursor=" + cursor,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        await page.Locator("[data-session-assignment]").ClickAsync();

        await Expect(page.Locator("#session-explorer-status"))
            .ToContainTextAsync("操作は完了しましたが、ページ情報を使用できません");
        Assert.Equal(1, ownerActionCalls);
        await page.Locator("[data-clear-stale-cursor]").PressAsync("Enter");

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-assignment]")).ToBeFocusedAsync();
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
        Assert.Equal(3, collectionCalls);
        Assert.Equal(1, ownerActionCalls);
    }

    [Fact]
    public async Task CertificationPendingSummaryAndTokensKeepTheirDedicatedSharedVocabulary()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var item = document["items"]![0]!;
        item["summary"]!["subagent"]!["state"] = "certification_pending";
        item["tokens"]!["state"] = "certification_pending";
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(document))));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-session-summary]")).ToContainTextAsync("安定して取得できるか未確認です");
        await Expect(page.Locator("[data-session-tokens]")).ToContainTextAsync("安定して取得できるか未確認です");
        await Expect(page.Locator("[data-session-tokens]")).ToContainTextAsync("125");
        await Expect(page.Locator("[data-fact-state='certification-pending']")).ToHaveCountAsync(2);
        await Expect(page.Locator("[data-fact-state='certification-pending'] .fact-state-primary"))
            .ToHaveTextAsync(["安定して取得できるか未確認です", "安定して取得できるか未確認です"]);
    }

    [Fact]
    public async Task MaximumPositiveSummaryAndTokenStateRemainWithinTheDense1366RowTarget()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var item = document["items"]![0]!;
        foreach (var name in new[] { "skill", "tool", "subagent", "error", "retry" })
        {
            item["summary"]![name]!["state"] = "recorded";
            item["summary"]![name]!["count"] = 123456789;
        }
        item["tokens"]!["state"] = "certification_pending";
        item["tokens"]!["cache_read_ratio_basis_points"]!["state"] = "source_unsupported";
        item["tokens"]!["cache_read_ratio_basis_points"]!["value"] = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(document))));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var row = page.Locator("[data-session-row]");
        await Expect(row.Locator("[data-session-summary]")).ToContainTextAsync("再試行: 123,456,789件");
        await Expect(row.Locator("[data-session-tokens]")).ToContainTextAsync("安定して取得できるか未確認です");
        var rowBox = Assert.IsType<LocatorBoundingBoxResult>(await row.BoundingBoxAsync());
        Assert.InRange(rowBox.Height, 52, 64);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var summaryDisclosure = row.Locator("[data-session-summary] .local-monitor-session-fact-disclosure");
        await summaryDisclosure.Locator("summary").PressAsync("Enter");
        var summaryPanel = summaryDisclosure.Locator(".local-monitor-session-fact-panel");
        await Expect(summaryPanel).ToBeVisibleAsync();
        var summaryPanelBox = Assert.IsType<LocatorBoundingBoxResult>(await summaryPanel.BoundingBoxAsync());
        Assert.True(summaryPanelBox.Height > rowBox.Height);
        Assert.True(await summaryPanel.EvaluateAsync<bool>(
            "panel => { const box = panel.getBoundingClientRect(); const point = document.elementFromPoint(box.left + 8, box.top + Math.min(40, box.height - 1)); return point === panel || panel.contains(point); }"));
        await summaryDisclosure.Locator("summary").PressAsync("Enter");
        var tokenDisclosure = row.Locator("[data-session-tokens] .local-monitor-session-fact-disclosure");
        await tokenDisclosure.Locator("summary").PressAsync("Enter");
        await Expect(tokenDisclosure.Locator(".local-monitor-session-fact-panel"))
            .ToContainTextAsync("キャッシュから読み込み");
        await Expect(tokenDisclosure.Locator(".local-monitor-session-fact-panel"))
            .ToContainTextAsync("安定して取得できるか未確認です");
    }

    [Fact]
    public async Task EveryUnavailableFactFamilyKeepsItsDistinctSharedPresentation()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var item = document["items"]![0]!;
        item["summary"]!["tool"]!["state"] = "not_observed";
        item["summary"]!["subagent"]!["state"] = "source_unsupported";
        item["summary"]!["error"]!["state"] = "malformed";
        item["summary"]!["retry"]!["state"] = "oversized";
        foreach (var name in new[] { "tool", "subagent", "error", "retry" })
            item["summary"]![name]!["count"] = null;
        item["source"]!["state"] = "capture_gap";
        item["source"]!["values"] = new JsonArray("vscode");
        item["model"]!["state"] = "certification_pending";
        item["model"]!["values"] = new JsonArray("model-partial");
        item["timing"]!["state"] = "projection_invalid";
        item["timing"]!["ended_at"] = null;
        item["timing"]!["duration_ms"] = null;
        item["tokens"]!["state"] = "capture_gap";
        item["tokens"]!["total"]!["state"] = "capture_gap";
        item["tokens"]!["total"]!["value"] = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(document))));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var summary = page.Locator("[data-session-summary]");
        await Expect(summary.Locator("[data-summary-family]")).ToHaveCountAsync(4);
        await Expect(summary.Locator(".local-monitor-session-fact-disclosure > summary"))
            .ToHaveTextAsync("記録状態を確認");
        await summary.Locator(".local-monitor-session-fact-disclosure > summary").PressAsync("Enter");
        await Expect(summary.Locator(".local-monitor-session-fact-panel")).ToBeVisibleAsync();
        await Expect(summary).ToContainTextAsync("ツール: 今回の記録にはありません");
        await Expect(summary).ToContainTextAsync("サブエージェント: この取得元では記録できません");
        await Expect(summary).ToContainTextAsync("エラー: 記録が一部欠けています");
        await Expect(summary).ToContainTextAsync("再試行: 記録が一部欠けています");
        await Expect(summary.Locator("[data-fact-state='not-observed']"))
            .ToContainTextAsync("今回の記録にはありません");
        await Expect(summary.Locator("[data-fact-state='unsupported']"))
            .ToContainTextAsync("この取得元では記録できません");
        await Expect(summary.Locator("[data-summary-family='error'] [data-fact-state='projection-invalid']"))
            .ToContainTextAsync("記録された形式を安全に確認できません");
        await Expect(summary.Locator("[data-summary-family='retry'] [data-fact-state='projection-invalid']"))
            .ToContainTextAsync("表示可能な範囲を超えています");
        var identity = page.Locator(".local-monitor-session-identity small");
        await identity.Locator(".local-monitor-session-fact-disclosure > summary").PressAsync("Enter");
        await Expect(identity.Locator(".local-monitor-session-fact-panel")).ToBeVisibleAsync();
        await Expect(identity).ToContainTextAsync("取得元: VS Code · 記録が一部欠けています");
        await Expect(identity).ToContainTextAsync("モデル: model-partial · 安定して取得できるか未確認です");
        var tokenDisclosure = page.Locator("[data-session-tokens] .local-monitor-session-fact-disclosure");
        await tokenDisclosure.Locator("summary").PressAsync("Enter");
        await Expect(tokenDisclosure.Locator(".local-monitor-session-fact-panel")).ToBeVisibleAsync();
        await Expect(tokenDisclosure.Locator(".local-monitor-session-fact-panel"))
            .ToContainTextAsync("記録が一部欠けています");
        var timingDisclosure = page.Locator("[data-session-started] .local-monitor-session-fact-disclosure");
        await timingDisclosure.Locator("summary").PressAsync("Enter");
        await Expect(timingDisclosure.Locator(".local-monitor-session-fact-panel")).ToBeVisibleAsync();
        await Expect(timingDisclosure.Locator(".local-monitor-session-fact-panel"))
            .ToContainTextAsync("記録が一部欠けています");
        await Expect(timingDisclosure.Locator(".local-monitor-session-fact-panel"))
            .ToContainTextAsync("検証できません");
        var rowBox = Assert.IsType<LocatorBoundingBoxResult>(
            await page.Locator("[data-session-row]").BoundingBoxAsync());
        Assert.InRange(rowBox.Height, 52, 64);
    }

    [Fact]
    public async Task SettingsOnlyNavigationPreservesTransientFiltersAndExactCohortIds()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var collectionCalls = 0;
        var responseBody = await TwoSessionsAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(responseBody));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-search").FillAsync(" exact q ");
        await page.Locator("#session-model").FillAsync("model,one\n model two ");
        await page.Locator("#session-limit").SelectOptionAsync("100");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(2);
        await page.Locator("#session-compare-mode").ClickAsync();
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        var callsBeforeSettings = collectionCalls;

        await page.Locator("#settings-action").ClickAsync();
        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await Expect(page.Locator("#session-search")).ToHaveValueAsync(" exact q ");
        await Expect(page.Locator("#session-model")).ToHaveValueAsync("model,one\n model two ");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 1件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 1件");
        await page.Locator("#settings-modal-close").ClickAsync();

        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#session-search")).ToHaveValueAsync(" exact q ");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 1件");
        Assert.Equal(callsBeforeSettings, collectionCalls);

        await page.GoBackAsync();

        await Expect(page.Locator("#settings-modal")).ToBeVisibleAsync();
        await Expect(page.Locator("#session-search")).ToHaveValueAsync("");
        await Expect(page.Locator("#session-model")).ToHaveValueAsync("");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 0件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 0件");
        Assert.Equal(callsBeforeSettings + 1, collectionCalls);

        await page.GoForwardAsync();

        await Expect(page.Locator("#settings-modal")).ToBeHiddenAsync();
        await Expect(page.Locator("#session-search")).ToHaveValueAsync("");
        await Expect(page.Locator("#session-model")).ToHaveValueAsync("");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 0件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 0件");
        Assert.Equal(callsBeforeSettings + 2, collectionCalls);
    }

    [Fact]
    public async Task InvalidTransientInputsFailLocallyWithoutRepairOrValueDisclosure()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var collectionCalls = 0;
        var responseBody = await FinalGoldenAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(responseBody));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var boundaryQuery = string.Concat(Enumerable.Repeat("😀", 200));
        var boundaryModel = string.Concat(Enumerable.Repeat("😀", 64));
        await page.Locator("#session-search").FillAsync(boundaryQuery);
        await page.Locator("#session-model").FillAsync(boundaryModel);
        var boundaryRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        var accepted = await boundaryRequest;
        using (var acceptedBody = JsonDocument.Parse(accepted.PostData!))
        {
            Assert.Equal(boundaryQuery, acceptedBody.RootElement.GetProperty("q").GetString());
            Assert.Equal(boundaryModel, acceptedBody.RootElement.GetProperty("model")[0].GetString());
        }
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("表示しています");

        await page.Locator("#session-model").FillAsync(string.Concat(Enumerable.Repeat("😀", 65)));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("各128文字");

        await page.Locator("#session-search").FillAsync("valid");
        await page.Locator("#session-model").FillAsync(new string('m', 128));
        var scalarBoundaryRequest = page.WaitForRequestAsync(request =>
            request.Url.EndsWith("/api/local-monitor/v1/sessions", StringComparison.Ordinal));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await scalarBoundaryRequest;
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("表示しています");
        await page.Locator("#session-model").FillAsync(new string('m', 129));
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("各128文字");

        var q = "sensitive-" + new string('x', 191);
        await page.Locator("#session-search").FillAsync(q);
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("検索条件を使用できません");
        await Expect(page.Locator("#session-explorer-status")).Not.ToContainTextAsync("sensitive");

        await page.Locator("#session-search").FillAsync("valid");
        await page.Locator("#session-model").FillAsync("invalid\u2028model");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("各128文字");

        await page.Locator("#session-model").FillAsync("");
        await page.Locator(".local-monitor-session-filter-menu:has(#session-from) > summary").ClickAsync();
        await page.Locator("#session-from").FillAsync(" 2026-01-01T00:00:00.0000000+00:00");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("正しいUTC日時");
        Assert.Equal(3, collectionCalls);
        Assert.DoesNotContain("sensitive", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivingASelectedSessionMarksTheExactIdExcludedWhenTheRowDisappears()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var firstPage = JsonNode.Parse(await TwoSessionsAsync())!.AsObject();
        var afterArchive = firstPage.DeepClone().AsObject();
        afterArchive["items"]!.AsArray().RemoveAt(0);
        var collectionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(Canonical(collectionCalls == 1 ? firstPage : afterArchive)));
        });
        await page.RouteAsync("**/api/local-monitor/v1/archive-actions", route =>
            route.FulfillAsync(Json(
                $$"""{"schema_version":"local-archive-action.response.v1","action":"archive","target_kind":"session","targets":[{"target_id":"{{SessionId}}","state":"archived","revision":1,"archived_at":"2026-01-03T00:00:00.0000000+00:00","updated_at":"2026-01-03T00:00:00.0000000+00:00"}]}""")));

        await page.GotoAsync(host.Url + "/sessions?mode=compare", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        var selectedRow = page.Locator($"[data-session-id='{SessionId}']");
        await selectedRow.Locator(".local-monitor-session-row-actions > summary").ClickAsync();
        await selectedRow.Locator("[data-session-archive]").ClickAsync();

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("セッションのアーカイブ除外 1件");
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("除外後に基準が空になります");
        await Expect(page.Locator("#session-explorer-status")).ToBeFocusedAsync();
        Assert.Equal(2, collectionCalls);
    }

    [Fact]
    public async Task FilterChangesExplicitlyClearTheDraftWithoutGuessingOffPageEligibility()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var initial = JsonNode.Parse(await TwoSessionsAsync())!.AsObject();
        var afterFilter = initial.DeepClone().AsObject();
        afterFilter["items"]!.AsArray().RemoveAt(0);
        var collectionCalls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(Canonical(collectionCalls <= 2 ? initial : afterFilter)));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-compare-mode").ClickAsync();
        await page.Locator($"[data-session-id='{SessionId}'] [data-cohort='a']").CheckAsync();
        await page.Locator($"[data-session-id='{SecondSessionId}'] [data-cohort='b']").CheckAsync();
        await page.Locator("#session-search").FillAsync("second");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("条件変更のため比較対象の選択をクリアしました");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 0件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 0件");
    }

    [Fact]
    public async Task RecordedTokenCoverageDoesNotTurnAnUnobservedProducerTotalIntoZero()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var tokens = document["items"]![0]!["tokens"]!;
        tokens["total"]!["state"] = "not_observed";
        tokens["total"]!["value"] = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
            route.FulfillAsync(Json(Canonical(document))));

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var tokenCell = page.Locator("[data-session-tokens]");
        await Expect(tokenCell).ToContainTextAsync("今回の記録にはありません");
        await Expect(tokenCell).Not.ToContainTextAsync("0件");
    }

    [Fact]
    public async Task ResponseIntegersAboveTheJavascriptSafeRangeRemainExactThroughOwnerActions()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        const long revision = 9_007_199_254_740_993;
        var document = JsonNode.Parse(await FinalGoldenAsync())!.AsObject();
        var item = document["items"]![0]!;
        item["assignment"]!["revision"] = revision;
        item["tokens"]!["input"]!["value"] = revision;
        item["tokens"]!["total"]!["value"] = revision;
        item["tokens"]!["new_input"]!["value"] = revision;
        item["tokens"]!["cache_read"]!["value"] = 0;
        item["tokens"]!["cache_read_ratio_basis_points"]!["value"] = 0;
        var collectionCalls = 0;
        string? actionBody = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            collectionCalls++;
            return route.FulfillAsync(Json(Canonical(document)));
        });
        await page.RouteAsync("**/api/local-monitor/v1/session-repository-actions", route =>
        {
            actionBody = route.Request.PostData;
            return route.FulfillAsync(Json(
                $$"""{"schema_version":"local-session-repository-assignment.v1","session_id":"{{SessionId}}","assignment_revision":{{revision + 1}},"state":"explicitly_unassigned","authority":"manual","repository_id":null,"conflicting_repository_ids":[],"observed_label_candidates":[],"updated_at":"2026-01-03T00:00:01.0000000+00:00"}"""));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-tokens]")).ToContainTextAsync("9,007,199,254,740,993");
        await page.Locator("[data-session-row] .local-monitor-session-row-actions > summary").ClickAsync();
        await page.Locator("[data-session-assignment]").ClickAsync();
        await Expect(page.Locator("[data-session-assignment]")).ToBeFocusedAsync();
        Assert.Equal(2, collectionCalls);
        Assert.Contains("\"expected_revision\":9007199254740993", actionBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoncanonicalSuccessBytesAndLexicallyInvalidServerCursorsFailClosed()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var golden = await SessionPageAsync(50, 0x5000, SessionCursor(DefaultSessionRequest(), 0x5001));
        var duplicate = golden.Replace(
            "{\"schema_version\":",
            "{\"schema_version\":\"duplicate\",\"schema_version\":",
            StringComparison.Ordinal);
        var invalidCursor = JsonNode.Parse(golden)!.AsObject();
        invalidCursor["next_cursor"] = new string('A', 146);
        var reordered = JsonNode.Parse(golden)!.AsObject();
        var schemaVersion = reordered["schema_version"]!.DeepClone();
        reordered.Remove("schema_version");
        reordered.Add("schema_version", schemaVersion);
        var reversedTiming = JsonNode.Parse(golden)!.AsObject();
        reversedTiming["items"]![0]!["timing"]!["started_at"] = "2026-01-02T00:00:00.0000001+00:00";
        reversedTiming["items"]![0]!["timing"]!["ended_at"] = "2026-01-02T00:00:00.0000000+00:00";
        reversedTiming["items"]![0]!["timing"]!["duration_ms"] = 0;
        var contradictoryTokens = JsonNode.Parse(golden)!.AsObject();
        contradictoryTokens["items"]![0]!["tokens"]!["input"]!["value"] = 100;
        contradictoryTokens["items"]![0]!["tokens"]!["cache_read"]!["value"] = 101;
        contradictoryTokens["items"]![0]!["tokens"]!["new_input"]!["state"] = "not_observed";
        contradictoryTokens["items"]![0]!["tokens"]!["new_input"]!["value"] = null;
        contradictoryTokens["items"]![0]!["tokens"]!["cache_read_ratio_basis_points"]!["state"] = "not_observed";
        contradictoryTokens["items"]![0]!["tokens"]!["cache_read_ratio_basis_points"]!["value"] = null;
        var calls = 0;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            calls++;
            var body = calls switch
            {
                1 => duplicate,
                2 => golden + " ",
                3 => Canonical(invalidCursor),
                4 => Canonical(reordered),
                5 => Canonical(reversedTiming),
                _ => Canonical(contradictoryTokens),
            };
            return route.FulfillAsync(Json(body));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await Expect(page.Locator("#session-explorer-status")).ToContainTextAsync("読み込めませんでした");
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task OpaqueServerCursorIsPassedBackExactlyWithoutExplorerDecoding()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var opaqueCursor = new string('A', 147);
        var firstPage = await SessionPageAsync(100, 0x5000, opaqueCursor);
        string? returnedCursor = null;
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            using var request = JsonDocument.Parse(route.Request.PostData!);
            var limitValue = request.RootElement.GetProperty("limit");
            var cursorValue = request.RootElement.GetProperty("cursor");
            if (limitValue.ValueKind == JsonValueKind.Null)
                return route.FulfillAsync(Json(FinalGoldenAsync().GetAwaiter().GetResult()));
            if (cursorValue.ValueKind == JsonValueKind.Null)
                return route.FulfillAsync(Json(firstPage));
            returnedCursor = cursorValue.GetString();
            return route.FulfillAsync(Json(FinalGoldenAsync().GetAwaiter().GetResult()));
        });

        await page.GotoAsync(host.Url + "/sessions", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-limit").SelectOptionAsync("100");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(100);
        await page.Locator("#session-load-more").PressAsync("Enter");

        Assert.Equal(opaqueCursor, returnedCursor);
        Assert.DoesNotContain("cursor=", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareDraftSpansServerPagesAndRejectsACombinedSelectionAboveTwoHundred()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
        });
        var (firstPage, secondPage, thirdPage) = await PagedCohortsAsync();
        await page.RouteAsync("**/api/local-monitor/v1/sessions", route =>
        {
            using var body = JsonDocument.Parse(route.Request.PostData!);
            var requestLimit = body.RootElement.GetProperty("limit");
            var cursor = body.RootElement.GetProperty("cursor");
            var pageBody = requestLimit.ValueKind == JsonValueKind.Null
                ? FinalGoldenAsync().GetAwaiter().GetResult()
                : cursor.ValueKind == JsonValueKind.Null
                    ? firstPage
                    : cursor.GetString() == SessionCursor(CohortRequest(), 0x3001) ? secondPage : thirdPage;
            return route.FulfillAsync(Json(pageBody));
        });

        await page.GotoAsync(host.Url + "/sessions?mode=compare", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#session-limit").SelectOptionAsync("100");
        await page.Locator("#session-explorer-filters button[type='submit']").ClickAsync();
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(100);
        var firstPageLayout = await page.EvaluateAsync<double[]>("""
            () => {
              const region = document.querySelector('.local-monitor-session-table-region');
              return [document.documentElement.scrollWidth, innerWidth, region.scrollHeight, region.clientHeight];
            }
            """);
        Assert.True(firstPageLayout[0] <= firstPageLayout[1]);
        Assert.True(firstPageLayout[2] > firstPageLayout[3]);
        await page.EvaluateAsync("() => document.querySelectorAll('[data-cohort=a]').forEach(node => node.click())");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 100件");

        await page.Locator("#session-load-more").PressAsync("Enter");
        await Expect(page.Locator("[data-session-row][data-session-id='018f0000-0000-7000-8000-000000002064']"))
            .ToHaveCountAsync(1);
        await page.EvaluateAsync("() => document.querySelectorAll('[data-cohort=b]').forEach(node => node.click())");
        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 100件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 100件");

        await page.Locator("#session-load-more").PressAsync("Enter");
        await Expect(page.Locator("[data-session-row][data-session-id='018f0000-0000-7000-8000-000000001001']"))
            .ToHaveCountAsync(1);
        await Expect(page.Locator("#session-explorer-status")).ToBeFocusedAsync();
        await page.Locator("[data-cohort='b']").CheckAsync();

        await Expect(page.Locator("[data-cohort-count='a']")).ToHaveTextAsync("基準 100件");
        await Expect(page.Locator("[data-cohort-count='b']")).ToHaveTextAsync("比較対象 101件");
        await Expect(page.Locator("#session-compare-validation")).ToContainTextAsync("合計200件まで");
        await Expect(page.Locator("#session-compare-preview")).ToHaveAttributeAsync("aria-disabled", "true");
    }

    private static RouteFulfillOptions Json(string body) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
        Body = body,
    };

    private static string ComparisonPreview(char selection = '1', char revision = '2') => """
        {"schema_version":"local-monitor-comparison-preview.response.v1","valid":true,"selection_sha256":"1111111111111111111111111111111111111111111111111111111111111111","preview_revision":"2222222222222222222222222222222222222222222222222222222222222222","cohorts":{"a":{"label":"基準","requested_count":1,"included_count":1,"excluded_count":0},"b":{"label":"比較対象","requested_count":1,"included_count":1,"excluded_count":0}},"requested":[{"cohort":"a","request_ordinal":1,"session_id":"{SESSION}"},{"cohort":"b","request_ordinal":1,"session_id":"{SECOND}"}],"included":[{"cohort":"a","session_id":"{SESSION}","metadata":{"archive_state":"active","source":"synthetic","model":"test","projection_version":1,"completeness":"full","metric_coverage":[],"session_revision":1,"projection_revision":"3333333333333333333333333333333333333333333333333333333333333333"}},{"cohort":"b","session_id":"{SECOND}","metadata":{"archive_state":"active","source":null,"model":null,"projection_version":1,"completeness":"partial","metric_coverage":[],"session_revision":2,"projection_revision":"4444444444444444444444444444444444444444444444444444444444444444"}}],"excluded":[]}
        """.Replace("{SESSION}", SessionId, StringComparison.Ordinal)
            .Replace("{SECOND}", SecondSessionId, StringComparison.Ordinal)
            .Replace(new string('1', 64), new string(selection, 64), StringComparison.Ordinal)
            .Replace(new string('2', 64), new string(revision, 64), StringComparison.Ordinal);

    private static string ComparisonPreviewWithExclusion() => """
        {"schema_version":"local-monitor-comparison-preview.response.v1","valid":true,"selection_sha256":"1111111111111111111111111111111111111111111111111111111111111111","preview_revision":"2222222222222222222222222222222222222222222222222222222222222222","cohorts":{"a":{"label":"基準","requested_count":2,"included_count":1,"excluded_count":1},"b":{"label":"比較対象","requested_count":1,"included_count":1,"excluded_count":0}},"requested":[{"cohort":"a","request_ordinal":1,"session_id":"{SESSION}"},{"cohort":"a","request_ordinal":2,"session_id":"{THIRD}"},{"cohort":"b","request_ordinal":1,"session_id":"{SECOND}"}],"included":[{"cohort":"a","session_id":"{THIRD}","metadata":{"archive_state":"archived","source":"synthetic","model":"test","projection_version":1,"completeness":"full","metric_coverage":[],"session_revision":1,"projection_revision":"3333333333333333333333333333333333333333333333333333333333333333"}},{"cohort":"b","session_id":"{SECOND}","metadata":{"archive_state":"active","source":null,"model":null,"projection_version":1,"completeness":"partial","metric_coverage":[],"session_revision":2,"projection_revision":"4444444444444444444444444444444444444444444444444444444444444444"}}],"excluded":[{"cohort":"a","request_ordinal":1,"session_id":"{SESSION}","reason":"projection_unavailable","metadata":null}]}
        """.Replace("{SESSION}", SessionId, StringComparison.Ordinal)
            .Replace("{SECOND}", SecondSessionId, StringComparison.Ordinal)
            .Replace("{THIRD}", ThirdSessionId, StringComparison.Ordinal);

    private static RouteFulfillOptions Error(int status, string code) => new()
    {
        Status = status,
        ContentType = "application/json; charset=utf-8",
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
        Body = $$"""{"error":"{{code}}"}""",
    };

    private static string Canonical(JsonNode node) => node.ToJsonString(CanonicalJson);

    private static string RepositoryCollection()
    {
        var root = new JsonObject
        {
            ["schema_version"] = "local-monitor-repositories.response.v1",
            ["workspace_revision"] = new string('b', 64),
            ["repositories"] = new JsonArray(),
            ["all_session_count"] = 2,
            ["unassigned_active_session_count"] = 2,
            ["archived_repository_count"] = 1,
            ["next_cursor"] = null,
        };
        var repositories = root["repositories"]!.AsArray();
        repositories.Add(Repository(CandidateRepositoryOne, "active", 2, 1));
        repositories.Add(Repository(CandidateRepositoryTwo, "active", 3, 1));
        repositories.Add(Repository(ArchivedRepository, "archived", 0, 0));
        return Canonical(root);

        static JsonObject Repository(string id, string archiveState, int activeSessions, int conflicts) => new()
        {
            ["repository_id"] = id,
            ["display_name"] = "同じ名前",
            ["archive_state"] = archiveState,
            ["archive_revision"] = archiveState == "archived" ? 1 : 0,
            ["active_session_count"] = activeSessions,
            ["last_observed_at"] = "2026-01-02T00:00:02.0000000+00:00",
            ["assignment_conflict_count"] = conflicts,
            ["repository_revision"] = new string(id[^1], 64),
        };
    }

    private static Task<string> GoldenAsync() => File.ReadAllTextAsync(Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "LocalMonitorV1SessionCollection",
        "more-page.json"));

    private static async Task<string> FinalGoldenAsync()
    {
        var document = JsonNode.Parse(await GoldenAsync())!.AsObject();
        document["next_cursor"] = null;
        return Canonical(document);
    }

    private static async Task<string> SessionPageAsync(int count, int firstSuffix, string? nextCursor)
    {
        var golden = JsonNode.Parse(await GoldenAsync())!.AsObject();
        var template = golden["items"]![0]!;
        var page = new JsonObject
        {
            ["schema_version"] = "local-monitor-sessions.response.v1",
            ["workspace_revision"] = new string('a', 64),
            ["items"] = new JsonArray(),
            ["next_cursor"] = nextCursor,
        };
        var items = page["items"]!.AsArray();
        for (var index = 0; index < count; index++)
        {
            var suffix = firstSuffix + count - index;
            var item = template.DeepClone().AsObject();
            item["session_id"] = $"018f0000-0000-7000-8000-{suffix:x12}";
            item["label"]!["text"] = $"Session {suffix}";
            items.Add(item);
        }
        return Canonical(page);
    }

    private static async Task<string> TwoSessionsAsync()
    {
        var document = JsonNode.Parse(await GoldenAsync())!.AsObject();
        var items = document["items"]!.AsArray();
        var second = items[0]!.DeepClone().AsObject();
        second["session_id"] = SecondSessionId;
        second["workspace_revision"] = new string('5', 64);
        second["label"]!["text"] = "Second session";
        second["timing"]!["started_at"] = "2026-01-01T00:00:00.0000000+00:00";
        second["timing"]!["ended_at"] = "2026-01-01T00:00:02.0000000+00:00";
        items.Add(second);
        document["next_cursor"] = null;
        return Canonical(document);
    }

    private static async Task<string> ThreeSessionsAsync()
    {
        var document = JsonNode.Parse(await TwoSessionsAsync())!.AsObject();
        var third = document["items"]![0]!.DeepClone().AsObject();
        third["session_id"] = ThirdSessionId;
        third["workspace_revision"] = new string('6', 64);
        third["label"]!["text"] = "Archived session";
        third["archive"]!["state"] = "archived";
        third["archive"]!["revision"] = 1;
        third["archive"]!["effectively_eligible"] = false;
        third["archive"]!["exclusion_reason"] = "session_archived";
        document["items"]!.AsArray().Add(third);
        return Canonical(document);
    }

    private static async Task<string> ArchivedAndEligibleAsync()
    {
        var archived = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "LocalMonitorV1SessionCollection",
            "final-page.json")))!.AsObject();
        var originalArchived = archived["items"]![0]!.DeepClone();
        var eligible = JsonNode.Parse(await GoldenAsync())!.AsObject()["items"]![0]!.DeepClone();
        var repositoryArchived = originalArchived.DeepClone();
        repositoryArchived!["session_id"] = "018f0000-0000-7000-8000-000000000004";
        repositoryArchived["label"]!["state"] = "recorded";
        repositoryArchived["label"]!["text"] = "Repository archived session";
        repositoryArchived["assignment"]!["state"] = "assigned";
        repositoryArchived["assignment"]!["authority"] = "automatic";
        repositoryArchived["assignment"]!["repository_id"] = CandidateRepositoryOne;
        repositoryArchived["archive"]!["state"] = "active";
        repositoryArchived["archive"]!["revision"] = 0;
        repositoryArchived["archive"]!["exclusion_reason"] = "repository_archived";
        var items = archived["items"]!.AsArray();
        items.Clear();
        items.Add(eligible);
        items.Add(repositoryArchived);
        items.Add(originalArchived);
        return Canonical(archived);
    }

    private static async Task<(string First, string Second, string Third)> PagedCohortsAsync()
    {
        return (
            await SessionPageAsync(100, 0x3000, SessionCursor(CohortRequest(), 0x3001)),
            await SessionPageAsync(100, 0x2000, SessionCursor(CohortRequest(), 0x2001)),
            await SessionPageAsync(1, 0x1000, null));
    }

    private static LocalMonitorV1SessionSearchRequest DefaultSessionRequest() => new(
        "all", null, "active_only", null, null, [], [], [],
        null, null, null, null, null, null, null, null);

    private static LocalMonitorV1SessionSearchRequest CohortRequest() => DefaultSessionRequest() with { Limit = 100 };

    private static string SessionCursor(LocalMonitorV1SessionSearchRequest request, int suffix)
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        return LocalMonitorV1SessionCursorCodec.Encode(
            key,
            request,
            new(
                LocalMonitorV1SessionSortGroup.ValidTime,
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                $"018f0000-0000-7000-8000-{suffix:x12}"));
    }

    private static MonitorHostTestOptions Options(
        bool includeRepository = false,
        string repositoryDisplayName = "対象リポジトリ") => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false,
        UseUserSecrets = false,
        LocalRepositoryScopeSnapshotService = new EmptyScopeService(includeRepository, repositoryDisplayName),
    };

    private sealed class EmptyScopeService(bool includeRepository, string repositoryDisplayName) : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<LocalRepositoryCatalogSnapshot> repositories = includeRepository
                ? [new(RepositoryId, repositoryDisplayName, 1, null, 0, LocalArchiveState.Active, 0)]
                : [];
            return ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request, repositories, []));
        }
    }
}

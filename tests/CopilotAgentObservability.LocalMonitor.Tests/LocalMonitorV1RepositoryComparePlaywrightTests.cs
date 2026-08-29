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

    [Fact]
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
        await page.RouteAsync($"**/api/local-monitor/v1/repositories/{RepositoryId}/comparisons/{ComparisonId}**", route =>
        {
            if (route.Request.Url.Contains("/rows?", StringComparison.Ordinal))
            {
                rowQueries.Add(new Uri(route.Request.Url).Query);
                var rows = JsonNode.Parse(Golden("local-monitor-comparison-rows.response.json").GetAwaiter().GetResult())!.AsObject();
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
        await Expect(page.Locator(".local-monitor-compare-table").First).ToContainTextAsync("0");
        await Expect(page.Locator(".local-monitor-compare-table").First).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("body")).Not.ToContainTextAsync("not_observed");
        await Expect(page.Locator("body")).Not.ToContainTextAsync("おすすめ");
        await Expect(page.Locator("body")).Not.ToContainTextAsync("AI");

        var toolSection = page.Locator(".local-monitor-compare-section").Filter(new LocatorFilterOptions { Has = page.Locator("h2", new PageLocatorOptions { HasText = "ツール" }) });
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "ツールを読み込む" }).ClickAsync();
        await Expect(toolSection).ToContainTextAsync("Synthetic tool");
        await toolSection.GetByPlaceholder("ツールを検索").FillAsync("synthetic");
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "検索" }).ClickAsync();
        Assert.Contains(rowQueries, query => query.Contains("family=tool&q=synthetic", StringComparison.Ordinal));
        await toolSection.GetByRole(AriaRole.Button, new() { Name = "次のページ" }).ClickAsync();
        Assert.Contains(rowQueries, query => query.Contains("after=cursor-one", StringComparison.Ordinal));

        var evidenceButton = page.Locator(".local-monitor-compare-table").First.GetByRole(AriaRole.Button, new() { Name = "根拠を表示" }).Nth(2);
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

    private static Task<string> Golden(string name) => File.ReadAllTextAsync(Path.Combine(
        AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison", name));

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

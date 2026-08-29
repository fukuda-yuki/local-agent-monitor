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
            ["result_ordinal"] = 3, ["section_key"] = "target", ["row_kind"] = "condition", ["row_key"] = "archived_inclusion",
            ["values"] = new JsonArray(
                new JsonObject { ["key"] = "a_included_count", ["value"] = "0" },
                new JsonObject { ["key"] = "a_includes_archived", ["value"] = "false" },
                new JsonObject { ["key"] = "b_included_count", ["value"] = "1" },
                new JsonObject { ["key"] = "b_includes_archived", ["value"] = "true" },
                new JsonObject { ["key"] = "absolute_difference", ["value"] = "1" }),
        });
        read["results"]!.AsArray().Add(new JsonObject
        {
            ["result_ordinal"] = 4, ["section_key"] = "target", ["row_kind"] = "scalar", ["row_key"] = "archived_inclusion",
            ["values"] = new JsonArray(new JsonObject { ["key"] = "a_includes_archived", ["value"] = "false" }),
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
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "中央値の根拠を表示", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "件数の根拠を表示", Exact = true })).ToBeVisibleAsync();
        var archivedRows = page.Locator(".local-monitor-compare-result-heading").Filter(new() { Has = page.Locator("th", new() { HasText = "archived_inclusion" }) });
        await Expect(archivedRows).ToHaveCountAsync(2);
        var validArchivedRow = archivedRows.Nth(0);
        var invalidArchivedRow = archivedRows.Nth(1);
        await Expect(validArchivedRow.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await invalidArchivedRow.GetByRole(AriaRole.Button).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "a_session_countの根拠を表示", Exact = true }).CountAsync());

        await page.GetByRole(AriaRole.Button, new() { Name = "中央値の根拠を表示", Exact = true }).ClickAsync();
        await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
        await page.Keyboard.PressAsync("Escape");
        await validArchivedRow.GetByRole(AriaRole.Button, new() { Name = "条件の根拠を表示", Exact = true }).ClickAsync();
        await Expect(page.Locator("#repository-compare-evidence-status")).ToContainTextAsync("1件の根拠を表示しています");
        await page.Keyboard.PressAsync("Escape");
        Assert.DoesNotContain(evidenceQueries, query => query.Contains("result_ordinal=4", StringComparison.Ordinal));

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
                     ("サブエージェント", "サブエージェントを読み込む", "合計トークンの根拠を表示"),
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
            await toolSection.Locator("table tbody .local-monitor-compare-result-heading th").AllTextContentsAsync());
        Assert.Equal(0, await toolSection.Locator(":scope > div > div > tr").CountAsync());
        Assert.Contains(rowQueries, query => query.Contains("after=rows-cursor", StringComparison.Ordinal));
        Assert.DoesNotContain(evidenceQueries, query => query.Contains("field_key=a_", StringComparison.Ordinal));

        await targetEvidence.ClickAsync();
        await page.Locator("[data-compare-evidence-items] a").First.ClickAsync();
        await page.WaitForURLAsync($"**/sessions/{SessionId}?node={NodeId}");
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

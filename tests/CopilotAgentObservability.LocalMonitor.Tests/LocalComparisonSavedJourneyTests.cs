using System.Net;
using System.Text.Json;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class LocalComparisonSavedJourneyTests
{
    [Fact]
    public async Task SavedComparison_NormalBrowserPathReopensOriginalReceiptAfterHostRestartAndTwentyFourHours()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new MutableTimeProvider(LocalComparisonStoreTests.Snapshot().CreatedAt);
        temp.TimeProvider = clock;
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        LocalComparisonSnapshotWrite snapshot;
        byte[] original;
        var errors = new List<string>();
        await using (var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(), compareAiEnabled: false))
        {
            snapshot = await Seed(host);
            original = await host.Client.GetByteArrayAsync(Api(snapshot));
            var page = await browser.NewPageAsync(); page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(host.Url + Human(snapshot));
            await Expect(page.Locator("[data-comparison-save-status]")).ToContainTextAsync("未保存");
            await Expect(page.Locator("[data-comparison-save]")).ToContainTextAsync("この端末に30日間保存。バックアップ・復元の対象外");
            await page.Locator("[data-comparison-save-button]").ClickAsync();
            await Expect(page.Locator("[data-comparison-save-status]")).ToContainTextAsync("保存済み");
            using var get = await host.Client.GetAsync(Api(snapshot) + "/saved");
            using var head = await host.Client.SendAsync(new(HttpMethod.Head, Api(snapshot) + "/saved"));
            Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
            Assert.True(head.Headers.CacheControl?.NoStore); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            await page.CloseAsync();
        }
        clock.Advance(TimeSpan.FromDays(2));
        await using (var restarted = await MonitorTestHost.StartAsync(temp, testOptions: Options(), compareAiEnabled: false))
        {
            Assert.Equal(original, await restarted.Client.GetByteArrayAsync(Api(snapshot)));
            var page = await browser.NewPageAsync(); page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync($"{restarted.Url}/repositories/{snapshot.RepositoryId}/sessions");
            await page.Locator("[data-saved-comparisons] > summary").ClickAsync();
            await Expect(page.Locator("[data-saved-comparisons-status]")).ToContainTextAsync("1件の保存した比較");
            var link = page.Locator("[data-saved-comparisons-list] a");
            await Expect(link).ToHaveAttributeAsync("href", Human(snapshot));
            await Expect(link).ToContainTextAsync("基準 1件 / 比較対象 1件");
            await link.ClickAsync();
            await Expect(page.Locator("#repository-compare-status")).ToContainTextAsync("作成時の比較結果");
            await Expect(page.Locator("[data-comparison-save-status]")).ToContainTextAsync("保存済み");
            await page.Locator("[data-comparison-unsave-button]").ClickAsync();
            await Expect(page.Locator("[data-comparison-save-status]")).ToContainTextAsync("元の24時間の期限が終了");
            await Expect(page.Locator("[data-compare-sections]")).ToBeEmptyAsync();
            using var expired = await restarted.Client.GetAsync(Api(snapshot));
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            await page.GotoAsync($"{restarted.Url}/repositories/{snapshot.RepositoryId}/sessions");
            await page.Locator("[data-saved-comparisons] > summary").ClickAsync();
            await Expect(page.Locator("[data-saved-comparisons-status]")).ToContainTextAsync("0件の保存した比較");
            await page.CloseAsync();
        }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task HostRestart_ExactV1ComparisonUpgradesThroughNormalPreflightWithoutChangingReceipt()
    {
        using var temp = new MonitorTempDirectory();
        temp.TimeProvider = new MutableTimeProvider(LocalComparisonStoreTests.Snapshot().CreatedAt);
        LocalComparisonSnapshotWrite snapshot; byte[] original;
        await using (var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(), compareAiEnabled: false))
        {
            snapshot = await Seed(host); original = await host.Client.GetByteArrayAsync(Api(snapshot));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE local_comparison_saved_lifetimes; UPDATE schema_version SET version=1 WHERE component='local_comparison';";
            command.ExecuteNonQuery();
        }
        await using var restarted = await MonitorTestHost.StartAsync(temp, testOptions: Options(), compareAiEnabled: false);
        Assert.Equal(original, await restarted.Client.GetByteArrayAsync(Api(snapshot)));
        using var saved = JsonDocument.Parse(await restarted.Client.GetByteArrayAsync(Api(snapshot) + "/saved"));
        Assert.False(saved.RootElement.GetProperty("is_saved").GetBoolean());
    }

    private static async Task<LocalComparisonSnapshotWrite> Seed(RunningMonitorHost host)
    {
        var application = host.Services.GetRequiredService<LocalRepositoryCatalogApplication>();
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            application.PrepareCreate(new("Synthetic saved comparison", null))).Prepared;
        var result = await application.ExecutePreparedAsync(prepared,
            "lrc1_" + Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            LocalRepositoryCatalogFixture.RepositoryEntity, default);
        using var repository = JsonDocument.Parse(Assert.IsType<LocalRepositoryMutationSucceeded>(result).Response.CopyEntity());
        var snapshot = LocalComparisonStoreTests.Snapshot(repositoryId: repository.RootElement.GetProperty("repository_id").GetString());
        Assert.Equal(LocalComparisonAcceptStatus.Accepted, host.Services.GetRequiredService<SqliteLocalComparisonStore>().Accept(snapshot, default));
        return snapshot;
    }

    private static string Api(LocalComparisonSnapshotWrite snapshot) => $"/api/local-monitor/v1/repositories/{snapshot.RepositoryId}/comparisons/{snapshot.ComparisonId}";
    private static string Human(LocalComparisonSnapshotWrite snapshot) => $"/repositories/{snapshot.RepositoryId}/comparisons/{snapshot.ComparisonId}";
    private static MonitorHostTestOptions Options() => new()
    {
        StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
        StartSessionOtelEnrichment = false, StartRetentionCleanupWorker = false,
        StartLocalRepositoryCatalogHostedService = false, StartLocalComparisonCleanupHostedService = false,
        UseUserSecrets = false,
    };
}

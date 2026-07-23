using Microsoft.Playwright;
using System.Text.Json;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class RuntimeBackupPlaywrightTests
{
    [Fact(Timeout = 60_000)]
    public async Task Backup_restore_page_creates_downloads_and_previews_without_exposing_web_restore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#runtime-backup-link").ClickAsync();
        await page.WaitForURLAsync($"{host.Url}/backup-restore");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "runtime backup と restore", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("raw_content_included", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("not_repository_safe", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("retention_backup_not_purged", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByLabel("検査する backup archive")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToHaveAttributeAsync("aria-labelledby", "result-heading");
        await Expect(page.GetByText("config-cli runtime-backup restore --bundle <bundle.zip> --database <monitor.db>", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("restore", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })).ToHaveCountAsync(0);

        await page.Locator("#preview").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("archive_required");
        await Expect(page.Locator("#bundle")).ToBeFocusedAsync();

        var createResponseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" && response.Url.EndsWith("/api/runtime-backup/v1/backups", StringComparison.Ordinal));
        await page.Locator("#create").ClickAsync();
        var createResponse = await createResponseTask;
        Assert.Equal(201, createResponse.Status);
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("backup_id");
        var download = page.Locator("#download");
        await Expect(download).ToBeVisibleAsync();
        var downloadPath = await download.GetAttributeAsync("href");
        Assert.NotNull(downloadPath);
        Assert.Matches("^/api/runtime-backup/v1/backups/[0-9a-f]{64}/archive$", downloadPath);
        var browserDownload = await page.RunAndWaitForDownloadAsync(() => download.ClickAsync());
        var browserDownloadPath = await browserDownload.PathAsync();
        Assert.NotNull(browserDownloadPath);
        var archive = await File.ReadAllBytesAsync(browserDownloadPath);
        Assert.NotEmpty(archive);

        await page.Locator("#bundle").SetInputFilesAsync(new FilePayload
        {
            Name = "runtime-backup.zip",
            MimeType = "application/zip",
            Buffer = archive,
        });
        var previewResponseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" && response.Url.EndsWith("/api/runtime-backup/v1/previews", StringComparison.Ordinal));
        await page.Locator("#preview").ClickAsync();
        var previewResponse = await previewResponseTask;
        Assert.Equal(200, previewResponse.Status);
        using (var previewJson = JsonDocument.Parse(await previewResponse.BodyAsync()))
        {
            var result = previewJson.RootElement;
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.True(result.GetProperty("compatible").GetBoolean());
            Assert.True(result.GetProperty("overwrites_existing").GetBoolean());
            Assert.True(result.GetProperty("monitor_stop_required").GetBoolean());
            Assert.True(result.GetProperty("restart_required").GetBoolean());
            Assert.False(result.GetProperty("requires_confirmation").GetBoolean());
            Assert.NotEmpty(result.GetProperty("source_component_versions").EnumerateObject());
            Assert.NotEmpty(result.GetProperty("row_counts").EnumerateObject());
            Assert.Equal(5, result.GetProperty("retention").GetProperty("store_kind_counts").EnumerateObject().Count());
            Assert.Equal(4, result.GetProperty("external_state").GetArrayLength());
            Assert.Contains("monitor_stopped", result.GetProperty("destination_prerequisites").EnumerateArray().Select(item => item.GetString()));
        }
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("\"compatible\": true");

        await page.Locator("#bundle").SetInputFilesAsync(new FilePayload
        {
            Name = "malformed.zip",
            MimeType = "application/zip",
            Buffer = [0x50, 0x4b, 0x03, 0x04],
        });
        await Expect(page.GetByRole(AriaRole.Status)).ToBeEmptyAsync();
        var malformedResponseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" && response.Url.EndsWith("/api/runtime-backup/v1/previews", StringComparison.Ordinal));
        await page.Locator("#preview").ClickAsync();
        var malformedResponse = await malformedResponseTask;
        Assert.Equal(400, malformedResponse.Status);
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("archive_invalid");

        Assert.DoesNotContain(requestedUrls, url => url.Contains("/restores", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
    }

    private static MonitorHostTestOptions DisabledWorkers() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
    };
}

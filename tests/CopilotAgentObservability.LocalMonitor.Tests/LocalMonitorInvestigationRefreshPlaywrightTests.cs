using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public sealed class LocalMonitorInvestigationRefreshPlaywrightTests
{
    [Fact(Timeout = 90_000)]
    public async Task FreshSessionIngestion_AnnouncesWithoutLegacyFetchAndRefreshesActualSnapshot()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, repositoryAiEnabled: false);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        var legacyReads = new List<string>();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/monitor/ingestions?", StringComparison.Ordinal)
                || request.Url.Contains("/api/monitor/traces?", StringComparison.Ordinal)) legacyReads.Add(request.Url);
        };
        var eventsConnected = page.WaitForResponseAsync(response => response.Url.EndsWith("/events", StringComparison.Ordinal));
        await page.GotoAsync(host.Url + "/sessions");
        await eventsConnected;
        await Expect(page.Locator("[data-snapshot-observed-at]")).Not.ToContainTextAsync("読み込み中");

        await Ingest(host, "first", "fresh investigation");
        await Expect(page.Locator("[data-new-record-notice]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(0);
        await page.Locator("[data-snapshot-refresh]").ClickAsync();
        await Expect(page.Locator("[data-session-row]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-session-open]")).ToContainTextAsync("fresh investigation");

        await page.Locator("[data-session-open]").ClickAsync();
        await Expect(page.Locator("[data-conversation-node]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-snapshot-observed-at]")).Not.ToContainTextAsync("読み込み中");
        await Ingest(host, "followup", "additional investigation");
        await Expect(page.Locator("[data-new-record-notice]")).ToBeVisibleAsync();
        await page.Locator("[data-snapshot-refresh]").ClickAsync();
        await Expect(page.Locator("[data-conversation-node]")).ToHaveCountAsync(2);
        Assert.Empty(legacyReads);
        Assert.Empty(errors);
    }

    private static async Task Ingest(RunningMonitorHost host, string eventId, string instruction)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var body = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            source_adapter = "copilot-compatible-hook",
            source_surface = "copilot-cli",
            native_session_id = "synthetic-refresh-investigation",
            events = new[]
            {
                new { source_event_id = eventId, type = "user.message", occurred_at = now,
                    run_native_id = "synthetic-run", payload = new { value = instruction } },
            },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CAO-Session-Event-Version", "1");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 overview page (§6.1): KPI cards, period toggle → sanitized
/// /api/monitor/overview refetch, TOP5 / recent lists, and the raw/sanitized
/// prompt-label split (prompt labels render only in the raw-default posture and
/// client JS never calls the prompt-label route under --sanitized-only).
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorOverviewPlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overview_PeriodToggleAndLists_RespectSanitizedBoundary(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        SeedRecentTrace(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Server-rendered structure: 4 KPI cards, panels, and the recent list with
        // the prompt label (raw-default) or the shortened TraceId (sanitized).
        await Expect(page.Locator(".kpi-grid .kpi-card")).ToHaveCountAsync(4);
        await Expect(page.Locator("#recent-traces .recent-trace-row")).ToHaveCountAsync(1);
        var recentText = await page.Locator("#recent-traces").InnerTextAsync();
        if (sanitizedOnly)
        {
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", recentText);
            Assert.Contains("trace-ov", recentText);
        }
        else
        {
            Assert.Contains("SECRET_PROMPT_TEXT_MARKER", recentText);
        }

        // The error KPI links to the error-filtered trace list.
        var errorHref = await page.Locator("#kpi-error-card").GetAttributeAsync("href");
        Assert.Contains("status=error", errorHref);

        // Period toggle refetches the sanitized overview endpoint for 7d.
        await page.Locator("#period-toggle .period-btn[data-period='7d']").ClickAsync();
        await Expect(page.Locator("#period-toggle .period-btn[data-period='7d']")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
        await Expect(page.Locator("#kpi-tokens-label")).ToHaveTextAsync("7日のトークン（実消費）");
        // The seeded trace is recent, so the 7d window includes its tokens.
        // 実消費 = (1000 input − 700 cache read) + 200 output = 500.
        await Expect(page.Locator("#kpi-tokens-value")).ToHaveTextAsync("500");
        await Expect(page.Locator("#kpi-tokens-breakdown")).ToContainTextAsync("総量 1.2K");
        await Expect(page.Locator("#top-traces .top-trace-row")).ToHaveCountAsync(1);
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/overview?period=7d", StringComparison.Ordinal));
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/trace-list?period=7d", StringComparison.Ordinal));

        var topText = await page.Locator("#top-traces").InnerTextAsync();
        if (sanitizedOnly)
        {
            // Sanitized context: no prompt content and no raw-bearing fetches.
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", topText);
            Assert.DoesNotContain(requestedUrls, url => url.Contains("prompt-label", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        }
        else
        {
            // Raw-default: the client labels TOP5 rows via the prompt-label route.
            Assert.Contains("SECRET_PROMPT_TEXT_MARKER", topText);
            Assert.Contains(requestedUrls, url => url.Contains("/traces/trace-ov/prompt-label", StringComparison.Ordinal));
        }
    }

    /// <summary>One chat trace received a minute ago (inside every period window): 1000 input / 200 output with cache usage and a prompt marker.</summary>
    private static void SeedRecentTrace(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-ov",
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: Payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), receivedAt);
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), receivedAt);
    }

    private const string Payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-ov","spanId":"1111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
            {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"700"}},
            {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"90"}}
          ]}
        ]}]}]}
        """;
}

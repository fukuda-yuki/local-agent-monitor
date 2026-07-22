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
    private static readonly TimeSpan EventBarrierTimeout = TimeSpan.FromSeconds(10);

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
        var requestedUrls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var responseDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var failureDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var alertRequestReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Request += (_, request) =>
        {
            requestedUrls.Enqueue(request.Url);
            if (IsSevenDayAlertRequest(request.Url)) alertRequestReached.TrySetResult(true);
        };
        page.Response += (_, response) =>
        {
            if (IsOverviewRequest(response.Url)) responseDiagnostics.Enqueue($"{response.Status} {response.Url}");
        };
        page.RequestFailed += (_, request) =>
        {
            if (IsOverviewRequest(request.Url)) failureDiagnostics.Enqueue($"{request.Failure} {request.Url}");
        };
        var initialOverviewRequestReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialOverviewRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sevenDayTraceListRequestReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSevenDayTraceListRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync($"{host.Url}/api/monitor/overview?period=today", async route =>
        {
            initialOverviewRequestReached.TrySetResult(true);
            await releaseInitialOverviewRequest.Task;
            try
            {
                await route.AbortAsync();
            }
            catch (PlaywrightException)
            {
                // The 7d click normally cancels this held request first.
            }
        });
        await page.RouteAsync($"{host.Url}/api/monitor/trace-list?period=7d&limit=5", async route =>
        {
            sevenDayTraceListRequestReached.TrySetResult(true);
            await releaseSevenDayTraceListRequest.Task;
            await route.ContinueAsync();
        });

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

        // Hold the automatic today refresh so the 7d action deterministically
        // supersedes it before either refresh can request a raw prompt label.
        await WaitForEventAsync(
            initialOverviewRequestReached.Task,
            "Timed out waiting for the automatic today overview request.");
        try
        {
            // Period toggle refetches the sanitized overview endpoint for 7d.
            await page.EvaluateAsync("""
                () => {
                    const list = document.getElementById("top-traces");
                    const oldRows = new Set(list.querySelectorAll(".top-trace-row"));
                    if (oldRows.size === 0) {
                        throw new Error("Expected a server-rendered TOP5 row before refresh.");
                    }

                    let oldRowRemoved = false;
                    window.__topTraceRefreshState = { mutationCount: 0, oldRowRemoved: false, newRowAdded: false };
                    window.__topTraceRefreshCompleted = new Promise((resolve) => {
                        const observer = new MutationObserver((mutations) => {
                            window.__topTraceRefreshState.mutationCount += mutations.length;
                            oldRowRemoved ||= mutations.some((mutation) =>
                                Array.from(mutation.removedNodes).some((node) => oldRows.has(node)));
                            window.__topTraceRefreshState.oldRowRemoved = oldRowRemoved;
                            const newRow = Array.from(list.querySelectorAll(".top-trace-row"))
                                .find((row) => !oldRows.has(row));
                            window.__topTraceRefreshState.newRowAdded = Boolean(newRow);
                            if (oldRowRemoved && newRow) {
                                observer.disconnect();
                                resolve(list.innerText);
                            }
                        });
                        observer.observe(list, { childList: true });
                    });

                    document.querySelector("#period-toggle .period-btn[data-period='7d']").click();
                }
                """);
        }
        finally
        {
            releaseInitialOverviewRequest.TrySetResult(true);
        }

        try
        {
            await WaitForEventAsync(
                sevenDayTraceListRequestReached.Task,
                "Timed out waiting for the 7d TOP5 request.");
            Assert.False(
                alertRequestReached.Task.IsCompleted,
                "Alert Center requests must not compete with the overview and TOP5 reads.");
        }
        finally
        {
            releaseSevenDayTraceListRequest.TrySetResult(true);
        }

        string topText;
        try
        {
            topText = await WaitForEventAsync(
                page.EvaluateAsync<string>("() => window.__topTraceRefreshCompleted"),
                "Timed out waiting for the 7d TOP5 refresh to replace the previous row.");
        }
        catch (TimeoutException exception)
        {
            var browserState = await page.EvaluateAsync<string>("""
                () => JSON.stringify({
                    refresh: window.__topTraceRefreshState,
                    topText: document.getElementById("top-traces")?.innerText,
                    period: document.querySelector("#period-toggle .period-btn.active")?.dataset.period,
                    kpiLabel: document.getElementById("kpi-tokens-label")?.innerText
                })
                """);
            throw new TimeoutException(
                $"{exception.Message} Browser={browserState}; responses=[{string.Join(", ", responseDiagnostics)}]; failures=[{string.Join(", ", failureDiagnostics)}]; requests=[{string.Join(", ", requestedUrls.Where(IsOverviewRequest))}]",
                exception);
        }
        await WaitForEventAsync(
            alertRequestReached.Task,
            "Timed out waiting for the Alert Center refresh after the 7d TOP5 refresh.");
        await Expect(page.Locator("#period-toggle .period-btn[data-period='7d']")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
        await Expect(page.Locator("#kpi-tokens-label")).ToHaveTextAsync("7日のトークン（実消費）");
        // The seeded trace is recent, so the 7d window includes its tokens.
        // 実消費 = (1000 input − 700 cache read) + 200 output = 500.
        await Expect(page.Locator("#kpi-tokens-value")).ToHaveTextAsync("500");
        await Expect(page.Locator("#kpi-tokens-breakdown")).ToContainTextAsync("総量 1.2K");
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/overview?period=7d", StringComparison.Ordinal));
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/trace-list?period=7d", StringComparison.Ordinal));

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

    private static async Task<T> WaitForEventAsync<T>(Task<T> eventTask, string timeoutDiagnostic)
    {
        try
        {
            return await eventTask.WaitAsync(EventBarrierTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(timeoutDiagnostic, exception);
        }
    }

    private static bool IsOverviewRequest(string url) =>
        url.Contains("/api/monitor/overview", StringComparison.Ordinal)
        || url.Contains("/api/monitor/trace-list", StringComparison.Ordinal)
        || url.Contains("/prompt-label", StringComparison.Ordinal)
        || url.Contains("/api/alert-center", StringComparison.Ordinal);

    private static bool IsSevenDayAlertRequest(string url) =>
        url.Contains("/api/alert-center", StringComparison.Ordinal)
        && url.Contains("period=7d", StringComparison.Ordinal);

    /// <summary>One chat trace received a minute ago (inside every period window): 1000 input / 200 output with cache usage and a prompt marker.</summary>
    private static void SeedRecentTrace(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var receivedAt = temp.TimeProvider.GetUtcNow().AddMinutes(-1);
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

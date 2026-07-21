using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class DoctorUiPlaywrightTests
{
    [Fact]
    public async Task Diagnostics_RendersCanonicalDoctorResultAndServerNavigationAsInertText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = VerificationJson,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();

        await Expect(page.Locator("#doctor-current-state")).ToHaveTextAsync("first_trace_ready");
        await Expect(page.Locator("#doctor-severity")).ToHaveTextAsync("info");
        await Expect(page.Locator("#doctor-result-source")).ToHaveTextAsync("claude-code");
        await Expect(page.Locator("#doctor-next-action")).ToHaveTextAsync("open_verified_trace_or_session");
        await Expect(page.Locator("#doctor-retryability")).ToHaveTextAsync("none");
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        await Expect(page.Locator("#doctor-evidence-list li")).ToHaveCountAsync(1);
        await Expect(page.Locator("#doctor-evidence-list")).ToContainTextAsync("ev:<b>unsafe</b>");
        await Expect(page.Locator("#doctor-evidence-list a")).ToHaveAttributeAsync("href", "/traces/trace-exact-1");
        Assert.DoesNotContain("<b>unsafe</b></a>", await page.ContentAsync());
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
        await Expect(page.Locator("#doctor-live")).ToContainTextAsync("completed");
    }

    [Fact]
    public async Task Diagnostics_ShowsNoDetectedSourcesWithoutSelectingOne()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson.Replace("\"detected\"", "\"not_detected\"", StringComparison.Ordinal),
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#doctor-source-state")).ToHaveTextAsync("検出されたソースはありません。ソースを選択して確認できます。");
        await Expect(page.Locator("#doctor-source")).ToHaveValueAsync("");
        await Expect(page.Locator("#doctor-primary-action")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Diagnostics_FailedGetOffersOneExplicitRetryAndRestoresHeadingFocus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var attempts = 0;

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route =>
        {
            attempts += 1;
            return attempts == 1
                ? route.FulfillAsync(new RouteFulfillOptions { Status = 503, ContentType = "application/json", Body = "{\"code\":\"doctor_store_unavailable\"}" })
                : route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = SourcesJson });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("再試行");
        await Expect(page.Locator("#doctor-live")).ToHaveTextAsync("Doctor の状態を読み込めませんでした。");
        await Expect(page.Locator("#doctor-result-heading")).ToBeFocusedAsync();
        await Expect(page.Locator("#doctor-actions button:visible")).ToHaveCountAsync(1);

        await page.Locator("#doctor-primary-action").PressAsync("Enter");

        await Expect(page.Locator("#doctor-source option")).ToHaveCountAsync(3);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Diagnostics_ActiveVerificationRetriesOnlyTheExactStatusGet()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var statusAttempts = 0;
        var statusUrls = new List<string>();

        await page.RouteAsync("**/api/doctor/ui/v1/sources", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = SourcesJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = ActiveVerificationJson,
        }));
        await page.RouteAsync("**/api/doctor/ui/v1/verifications/*", route =>
        {
            statusAttempts += 1;
            statusUrls.Add(route.Request.Url);
            return statusAttempts == 1
                ? route.FulfillAsync(new RouteFulfillOptions { Status = 503, ContentType = "application/json", Body = "{\"error\":\"doctor_store_unavailable\"}" })
                : route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = VerificationJson });
        });

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#doctor-source").SelectOptionAsync("claude-code");
        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("active");
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("状態を更新");

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-primary-action")).ToHaveTextAsync("再試行");
        Assert.Equal(1, statusAttempts);

        await page.Locator("#doctor-primary-action").ClickAsync();
        await Expect(page.Locator("#doctor-lifecycle")).ToHaveTextAsync("completed");
        Assert.Equal(2, statusAttempts);
        Assert.All(statusUrls, url => Assert.EndsWith(
            "/api/doctor/ui/v1/verifications/018f0c57-7b34-7cc3-8a13-8a90a2345678",
            url,
            StringComparison.Ordinal));
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string SourcesJson = """
        {"schema_version":"doctor.ui.v1","sources":[
          {"source_id":"github-copilot-cli","display_label":"GitHub Copilot CLI","setup_ownership":"managed_on_windows","detection_state":"not_detected"},
          {"source_id":"claude-code","display_label":"Claude <Code>","setup_ownership":"managed_cli","detection_state":"detected"}
        ]}
        """;

    private const string VerificationJson = """
        {"schema_version":"doctor.ui.v1","envelope":{
          "contract_version":"first-trace.v1","command":"begin","success":true,"code":"verification_completed",
          "adapter":"claude-code","source_surface":"claude-code","verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678",
          "doctor":{"schema_version":"doctor.v1","success":true,"code":"verification_completed","evaluation":{
            "source_surface":"claude-code","primary_state":{"schema_version":"doctor.v1","state_code":"first_trace_ready","severity":"info",
            "source_surface":"claude-code","evidence_refs":["ev:<b>unsafe</b>"],"reason_codes":[],"next_action":"open_verified_trace_or_session",
            "retryability":"none","observed_at":"2026-07-21T00:00:00.0000000Z","verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678"},
            "states":[],"missing_fact_families":[]},"verification":{"verification_id":"018f0c57-7b34-7cc3-8a13-8a90a2345678",
            "expected_source_surface":"claude-code","expected_source_adapter":"claude-code","state":"completed","revision":2,
            "started_at":"2026-07-21T00:00:00.0000000Z","expires_at":"2026-07-21T00:10:00.0000000Z",
            "completed_at":"2026-07-21T00:01:00.0000000Z","cancelled_at":null,"accepted_evidence_refs":["ev:<b>unsafe</b>"]}},
          "evaluation_preview":null,"guidance":[],"candidates":[],"truncated":false},
          "navigation_targets":[{"evidence_ref":"ev:<b>unsafe</b>","target_kind":"trace","target_id":"trace-exact-1","href":"/traces/trace-exact-1"}]}
        """;

    private static string ActiveVerificationJson => VerificationJson
        .Replace("\"state\":\"completed\"", "\"state\":\"active\"", StringComparison.Ordinal)
        .Replace("\"completed_at\":\"2026-07-21T00:01:00.0000000Z\"", "\"completed_at\":null", StringComparison.Ordinal);
}

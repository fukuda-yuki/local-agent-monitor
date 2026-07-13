using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class SourceDiagnosticsPlaywrightTests
{
    [Fact]
    public async Task Diagnostics_ShowsEmptySourceDiagnosticsView()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new BrowserCompatibilityStore([]),
                StartWriter = false,
                StartProjectionWorker = false,
            });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#source-diagnostics-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#source-diagnostics-rows")).ToContainTextAsync("ソース互換性の観測はまだありません。");
    }

    [Fact]
    public async Task Diagnostics_ShowsSourceDiagnosticsErrorViewWithoutExceptionText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new ThrowingBrowserStore(new InvalidOperationException("SECRET_PROMPT_TEXT_MARKER")),
                StartWriter = false,
                StartProjectionWorker = false,
            });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#source-diagnostics-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#source-diagnostics-rows")).ToContainTextAsync("ソース互換性の診断を読み込めませんでした。");
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        Assert.DoesNotContain("InvalidOperationException", await page.ContentAsync());
    }

    [Fact]
    public async Task Diagnostics_StopsOnARepeatedSourceDiagnosticsCursor()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new RepeatingBrowserStore(CreateRows(50)),
                StartWriter = false,
                StartProjectionWorker = false,
            });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#source-diagnostics-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#source-diagnostics-rows")).ToContainTextAsync("ソース互換性の診断を読み込めませんでした。");
    }

    [Fact]
    public async Task Diagnostics_DrainsEverySourceDiagnosticsCursorPageAsInertText()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new BrowserCompatibilityStore(CreateRows(51)),
                StartWriter = false,
                StartProjectionWorker = false,
            });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var rows = page.Locator("#source-diagnostics-rows tr");
        await Expect(rows).ToHaveCountAsync(51);
        await Expect(rows.First).ToContainTextAsync("observation-1");
        await Expect(rows.Last).ToContainTextAsync("observation-51");
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/source-diagnostics?limit=50", StringComparison.Ordinal));
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/source-diagnostics?limit=50&after=50", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        Assert.DoesNotContain("leak-marker@example.com", await page.ContentAsync());
        Assert.DoesNotContain("sk-live-SECRET", await page.ContentAsync());
        Assert.DoesNotContain("C:\\Users\\victim\\secret.txt", await page.ContentAsync());
    }

    private static IReadOnlyList<SourceCompatibilityRow> CreateRows(int count) =>
        Enumerable.Range(1, count).Select(id => new SourceCompatibilityRow(
            Id: id,
            ObservationId: $"observation-{id}",
            RawRecordId: id,
            IngestBatchId: $"batch-{id}",
            SourceSurface: "claude-code",
            SourceApplicationVersion: "1.0.0",
            SourceAdapter: "claude-code-otel",
            AdapterVersion: "1",
            SchemaFingerprint: $"sha256:{id:x64}",
            InventoryHash: $"sha256:{(id + 100):x64}",
            CompatibilityState: SourceCompatibilityState.SchemaDriftDetected,
            ReasonCodes: [SourceCompatibilityReasonCodes.SchemaDriftDetected],
            NextAction: SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping,
            CaptureContentState: SourceCaptureContentState.NotCaptured,
            UnknownSpanCount: 0,
            UnknownEventCount: 0,
            UnknownAttributeCount: 0,
            OverflowDistinctCount: 0,
            OverflowOccurrenceCount: 0,
            ObservedAt: DateTimeOffset.UnixEpoch.AddMinutes(id),
            UnknownObservations:
            [
                new SourceUnknownObservationRow(
                    Id: id,
                    SourceObservationId: id,
                    Kind: SourceUnknownKind.Attribute,
                    Name: "SECRET_PROMPT_TEXT_MARKER leak-marker@example.com sk-live-SECRET C:\\Users\\victim\\secret.txt",
                    Count: 1,
                    SourceVersionLabel: null,
                    FirstObservedAt: DateTimeOffset.UnixEpoch,
                    LastObservedAt: DateTimeOffset.UnixEpoch,
                    OpaqueSampleReference: "sample:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            ])).ToArray();

    private sealed class BrowserCompatibilityStore(IReadOnlyList<SourceCompatibilityRow> rows) : ISourceCompatibilityStore
    {
        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw new NotSupportedException();

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) =>
            rows.SingleOrDefault(row => row.RawRecordId == rawRecordId);

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) =>
            rows.Where(row => row.Id > (after ?? 0)).Take(limit).ToArray();
    }

    private sealed class ThrowingBrowserStore(Exception exception) : ISourceCompatibilityStore
    {
        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw exception;

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => throw exception;

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => throw exception;
    }

    private sealed class RepeatingBrowserStore(IReadOnlyList<SourceCompatibilityRow> rows) : ISourceCompatibilityStore
    {
        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw new NotSupportedException();

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) =>
            rows.SingleOrDefault(row => row.RawRecordId == rawRecordId);

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => rows.Take(limit).ToArray();
    }
}

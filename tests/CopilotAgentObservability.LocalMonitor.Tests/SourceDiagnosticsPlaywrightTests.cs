using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
[Trait("ValidationLane", "Nightly")]
public sealed class SourceDiagnosticsPlaywrightTests
{
    [Fact]
    public async Task Diagnostics_EmptyIngestionHistoryUsesSharedHonestAbsence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/ingestions?limit=50", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = "{\"items\":[]}",
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var row = page.Locator("#ingestion-history-rows tr");
        await Expect(row.Locator("[data-fact-state='not-observed']")).ToHaveCountAsync(1);
        await Expect(row).ToContainTextAsync("今回の記録にはありません");
        await Expect(row.Locator("p")).ToContainTextAsync("取り込み履歴をこの記録で確認できません");
        await Expect(row).Not.ToContainTextAsync("0件");
        await Expect(row).Not.ToContainTextAsync("まだ取り込みがありません");
        await Expect(row.Locator("td > div > p")).ToHaveCountAsync(1);
        await Expect(row.Locator("span > p, span > details")).ToHaveCountAsync(0);
    }

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

        Assert.True(await page.EvaluateAsync<bool>("() => typeof window.LocalMonitorV1FactState?.render === 'function'"));
        await Expect(page.Locator("#source-diagnostics-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#source-diagnostics-rows")).ToContainTextAsync("今回の記録にはありません");
        await Expect(page.Locator("#source-diagnostics-rows")).ToContainTextAsync("実際に使われなかったとは断定できません");
        await Expect(page.Locator("#source-diagnostics-rows [data-fact-state='not-observed']")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task Diagnostics_IngestionHistoryUsesSharedAbsenceForEveryNullableFact()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/ingestions?limit=50", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = "{\"items\":[{\"raw_record_id\":1,\"received_at\":null,\"source\":null,\"trace_id\":null,\"span_count\":null}]}",
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var row = page.Locator("#ingestion-history-rows tr");
        await Expect(row.Locator("[data-fact-state='not-observed']")).ToHaveCountAsync(4);
        await Expect(row).Not.ToContainTextAsync("—");
        await Expect(row).ToContainTextAsync("受信時刻をこの記録で確認できません");
        await Expect(row).ToContainTextAsync("取得元をこの記録で確認できません");
        await Expect(row).ToContainTextAsync("Trace IDをこの記録で確認できません");
        await Expect(row).ToContainTextAsync("Span 数をこの記録で確認できません");
    }

    [Fact]
    public async Task Diagnostics_PresentsEverySourceDiagnosticEnumAsJapanesePrimaryAndRawTechnicalDetail()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string body = """
            {"items":[
              {"observation_id":"obs-1","source_surface":"claude-code","source_application_version":"1","source_adapter":"otel","adapter_version":"1","compatibility_state":"supported","reason_codes":[],"next_action":"none","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-2","source_surface":"claude-code","source_application_version":"1","source_adapter":"otel","adapter_version":"1","compatibility_state":"supported_with_unknown_fields","reason_codes":["unknown_fields_observed"],"next_action":"review_unknown_fields","unknown_span_count":1,"unknown_event_count":2,"unknown_attribute_count":3,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-3","source_surface":"claude-code","source_application_version":"1","source_adapter":"otel","adapter_version":"1","compatibility_state":"unsupported_source_version","reason_codes":["unsupported_source_version"],"next_action":"use_compatible_source_or_update_adapter","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-4","source_surface":"claude-code","source_application_version":"1","source_adapter":"otel","adapter_version":"1","compatibility_state":"schema_drift_detected","reason_codes":["schema_drift_detected"],"next_action":"capture_fixture_and_review_mapping","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-5","source_surface":"claude-code","source_application_version":"1","source_adapter":"otel","adapter_version":"1","compatibility_state":"recognized_record_drop_detected","reason_codes":["recognized_record_drop_detected"],"next_action":"restore_mapping_or_update_versioned_golden","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-6","source_surface":"hostile <img src=x onerror=alert(1)>","source_application_version":null,"source_adapter":null,"adapter_version":null,"compatibility_state":"adapter_failure","reason_codes":["adapter_parse_failure"],"next_action":"validate_payload_and_protocol","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"},
              {"observation_id":"obs-7","source_surface":null,"source_application_version":null,"source_adapter":null,"adapter_version":null,"compatibility_state":"adapter_failure","reason_codes":["adapter_exception"],"next_action":"inspect_sanitized_adapter_failure","unknown_span_count":0,"unknown_event_count":0,"unknown_attribute_count":0,"observed_at":"2026-01-01T00:00:00Z"}
            ],"next_cursor":null}
            """;
        await page.RouteAsync("**/api/monitor/source-diagnostics?limit=50", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = body,
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var table = page.Locator("#source-diagnostics-rows");
        foreach (var expected in new[]
        {
            "対応済み", "対応済み（未知フィールドあり）", "この取得元では記録できません", "記録が一部欠けています",
            "認識済みレコードの欠落を検出しました", "未知フィールドがあります",
            "payload を解析できませんでした", "アダプター処理に失敗しました", "対応は不要です",
            "未知フィールドを確認してください", "sanitized なアダプター診断を確認してください",
        })
        {
            await Expect(table).ToContainTextAsync(expected);
        }
        await Expect(table.Locator("details summary").First).ToHaveTextAsync("技術情報");
        await Expect(table.Locator("span > details, span > p")).ToHaveCountAsync(0);
        await Expect(table.Locator("tr").First.Locator("td").Nth(4)).ToContainTextAsync("0件");
        await Expect(table.Locator("tr").First.Locator("td").Nth(4)).ToContainTextAsync("追加の互換性理由はありません");
        await Expect(table.Locator("code").First).Not.ToBeVisibleAsync();
        await table.Locator("details summary").First.PressAsync("Enter");
        await Expect(table.Locator("code").First).ToBeVisibleAsync();
        await Expect(table).ToContainTextAsync("supported");
        await Expect(table).ToContainTextAsync("adapter_exception");
        await Expect(table).ToContainTextAsync("hostile <img src=x onerror=alert(1)>");
        await Expect(table.Locator("img")).ToHaveCountAsync(0);
        await Expect(table).ToContainTextAsync("1");
        await Expect(table).ToContainTextAsync("2");
        await Expect(table).ToContainTextAsync("3");
        await Expect(table.Locator("tr").Nth(1).Locator("td").Last.Locator("span").Nth(0)).ToHaveTextAsync("1");
        await Expect(table.Locator("tr").Nth(1).Locator("td").Last.Locator("span").Nth(1)).ToHaveTextAsync("2");
        await Expect(table.Locator("tr").Nth(1).Locator("td").Last.Locator("span").Nth(2)).ToHaveTextAsync("3");
        await Expect(table).ToContainTextAsync("今回の記録にはありません");
    }

    [Fact]
    public async Task Diagnostics_RejectsAnInconsistentSourceDiagnosticTupleWithoutPartialDom()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/source-diagnostics?limit=50", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = "{\"items\":[{\"observation_id\":\"obs-unknown\",\"source_surface\":\"claude-code\",\"source_application_version\":\"1\",\"source_adapter\":\"otel\",\"adapter_version\":\"1\",\"compatibility_state\":\"supported\",\"reason_codes\":[\"unknown_fields_observed\"],\"next_action\":\"review_unknown_fields\",\"unknown_span_count\":0,\"unknown_event_count\":0,\"unknown_attribute_count\":0,\"observed_at\":\"2026-01-01T00:00:00Z\"}],\"next_cursor\":null}",
        }));

        await page.GotoAsync($"{host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#source-diagnostics-rows")).ToHaveTextAsync("ソース互換性の診断を読み込めませんでした。");
        await Expect(page.Locator("#source-diagnostics-rows tr")).ToHaveCountAsync(1);
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

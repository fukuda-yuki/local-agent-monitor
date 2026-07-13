using Microsoft.Playwright;
using System.Text.Json;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 trace list master-detail (§6.2): row selection drives the preview
/// panel, filters refetch the sanitized trace-list endpoint, and prompt labels
/// stay out of sanitized contexts.
/// </summary>
[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorTraceListPlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceList_ClaudeSourceEvidenceUsesDtoFactsAndContentDisabledHidesRaw(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
        });
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/trace-list?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ClaudeTraceListBody(
                compatibilityState: "supported_with_unknown_fields",
                nextAction: "review_unknown_fields",
                contentState: "not_captured"),
        }));

        await page.GotoAsync($"{host.Url}/traces?period=all", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var source = page.Locator("#preview-source-evidence");
        await Expect(source).ToBeVisibleAsync();
        await Expect(source).ToContainTextAsync("Claude Code");
        await Expect(source).ToContainTextAsync("未知フィールドあり");
        await Expect(source).ToContainTextAsync("Hook のみ");
        await Expect(source).ToContainTextAsync("部分的");
        await Expect(source).ToContainTextAsync("内容は取得されていません");
        await Expect(source).ToContainTextAsync("未知フィールドを確認してください");
        await Expect(page.Locator("#preview-body .preview-raw")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceList_SelectionPreviewAndFilters_RespectSanitizedBoundary(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp);
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

        await page.GotoAsync($"{host.Url}/traces?period=all", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Default sort: tokens descending — the big trace is first and auto-selected.
        await Expect(page.Locator("#trace-rows .trace-row")).ToHaveCountAsync(2);
        var firstRow = page.Locator("#trace-rows .trace-row").First;
        Assert.Equal("trace-list-big", await firstRow.GetAttributeAsync("data-trace-id"));
        await Expect(firstRow).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("selected"));
        await Expect(page.Locator("#preview-body")).ToBeVisibleAsync();
        await Expect(page.Locator("#preview-body .preview-kpis")).ToContainTextAsync("5K");

        if (sanitizedOnly)
        {
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        }
        else
        {
            await Expect(page.Locator("#preview-body .preview-title")).ToContainTextAsync("SECRET_PROMPT_TEXT_MARKER");
            // The preview offers the raw record link only in the raw-default posture.
            await Expect(page.Locator("#preview-body .preview-raw")).ToHaveCountAsync(1);
        }

        // Selecting the second row swaps the preview without navigation.
        await page.Locator("#trace-rows .trace-row").Nth(1).ClickAsync();
        await Expect(page.Locator("#preview-body .preview-status")).ToContainTextAsync("エラー · 異常終了");
        Assert.EndsWith("/traces?period=all", page.Url);

        // Status filter refetches from the sanitized trace-list endpoint.
        await page.Locator("#filter-status").SelectOptionAsync("unrecovered");
        await Expect(page.Locator("#trace-rows .trace-row")).ToHaveCountAsync(1);
        Assert.Contains(requestedUrls, url => url.Contains("/api/monitor/trace-list?", StringComparison.Ordinal) && url.Contains("status=unrecovered", StringComparison.Ordinal));
        Assert.Contains("status=unrecovered", page.Url);

        if (sanitizedOnly)
        {
            // Sanitized context: the client never touches raw-bearing routes.
            Assert.DoesNotContain(requestedUrls, url => url.Contains("prompt-label", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void Seed(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedTrace(store, PromptlessBigPayload, minute: 0);
        SeedTrace(store, BigPayload, minute: 1);
        SeedTrace(store, ErrorPayload, minute: 2);
    }

    private static void SeedTrace(RawTelemetryStore store, string payloadJson, int minute)
    {
        var receivedAt = DateTimeOffset.UnixEpoch.AddMinutes(minute);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: null,
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), receivedAt);
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), receivedAt);
    }

    private static string ClaudeTraceListBody(string compatibilityState, string nextAction, string contentState) =>
        JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    trace_id = "trace-list-big",
                    total_tokens = 5000,
                    input_tokens = 4000,
                    output_tokens = 1000,
                    cache_read_tokens = 2000,
                    cache_creation_tokens = (int?)null,
                    duration_ms = (double?)null,
                    turn_count = 1,
                    tool_call_count = 0,
                    trace_status = "ok",
                    primary_model = "claude-sonnet-4-5",
                    client_kind = (string?)null,
                    last_seen_at = DateTimeOffset.UnixEpoch.ToString("O"),
                    source_diagnostic = new
                    {
                        source_surface = "claude-code",
                        source_application_version = (string?)null,
                        source_adapter = "claude-code-otel",
                        adapter_version = "1",
                        schema_fingerprint = (string?)null,
                        compatibility_state = compatibilityState,
                        reason_codes = new[] { "unknown_fields_observed" },
                        next_action = nextAction,
                    },
                    binding_state = "hook_only",
                    completeness = "partial",
                    completeness_reason_codes = new[] { "hook_only", "content_capture_disabled" },
                    content_state = contentState,
                },
            },
            total_matched = 1,
            total_matched_tokens = 5000,
            offset = 0,
            limit = 50,
        });

    private const string BigPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-list-big","spanId":"1111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"4000"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"1000"}},
            {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"2000"}}
          ]}
        ]}]}]}
        """;

    private const string PromptlessBigPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-list-big","spanId":"0001","name":"setup",
           "startTimeUnixNano":"900000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"setup"}}
           ]}
        ]}]}]}
        """;

    private const string ErrorPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-list-err","spanId":"1111","name":"chat gpt-4.1",
           "startTimeUnixNano":"1000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4.1"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"800"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"trace-list-err","spanId":"2222","parentSpanId":"1111","name":"execute_tool run_tests",
           "startTimeUnixNano":"2000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]}
        ]}]}]}
        """;
}

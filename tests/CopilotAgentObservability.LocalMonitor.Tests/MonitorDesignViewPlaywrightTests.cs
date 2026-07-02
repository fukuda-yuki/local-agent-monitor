using Microsoft.Playwright;
using System.Net;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorDesignViewPlaywrightTests
{
    private const string TraceId = "trace-design";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceDetailDesignViews_RenderFromSanitizedSpansOnly(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (sanitizedOnly)
        {
            Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", await page.ContentAsync());
        }

        Assert.Equal(
            "#14171e",
            await page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).getPropertyValue('--monitor-bg').trim()"));
        Assert.Equal("true", await page.Locator("#tab-summary").GetAttributeAsync("aria-selected"));

        await page.Locator("#tab-timeline").ClickAsync();
        await Expect(page.Locator("#tab-timeline")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.Locator("#timeline-count")).ToContainTextAsync("5 / 5 スパン");
        await page.Locator("#timeline-errors-only").CheckAsync();
        await Expect(page.Locator("#timeline-count")).ToContainTextAsync("1 / 5 スパン");
        await Expect(page.Locator("#timeline-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#timeline-rows")).ToContainTextAsync("tool_failure");

        await page.Locator("input[name='timeline-sort'][value='tokens']").CheckAsync();
        await page.Locator("#timeline-errors-only").UncheckAsync();
        await Expect(page.Locator("#timeline-rows tr").First).ToContainTextAsync("chat");
        await Expect(page.Locator("#timeline-rows tr").First).ToContainTextAsync("300 / 150 / 450");

        // Span Tree + Flow (D033: plain DOM, no Cytoscape canvas).
        await page.Locator("#tab-tree").ClickAsync();
        await Expect(page.Locator("#tab-tree")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.Locator("#spantree-status")).ToContainTextAsync("5 スパン");
        await Expect(page.Locator("#spantree-view table.span-table tbody tr")).ToHaveCountAsync(5);
        await Expect(page.Locator("#spantree-view")).ToBeVisibleAsync();
        // Toggle to the DOM flow view.
        await page.Locator("#view-flow-btn").ClickAsync();
        await Expect(page.Locator("#flow-view")).ToBeVisibleAsync();
        await Expect(page.Locator("#flow-view .flow-node").First).ToBeVisibleAsync();
        await Expect(page.Locator("#spantree-view")).ToBeHiddenAsync();
        // No Cytoscape canvas exists anymore.
        await Expect(page.Locator("#spantree-view canvas")).ToHaveCountAsync(0);
        await Expect(page.Locator("#flow-view canvas")).ToHaveCountAsync(0);

        await page.Locator("#tab-cache").ClickAsync();
        await Expect(page.Locator("#tab-cache")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.Locator("#cache-status")).ToContainTextAsync("1 request group; 1 chat turn");
        await Expect(page.Locator("#cache-groups")).ToContainTextAsync("Cache hit rate");
        await Expect(page.Locator("#cache-groups")).ToContainTextAsync("25%");
        await Expect(page.Locator("#cache-groups")).ToContainTextAsync("75 / 40");
        await Expect(page.Locator("#cache-groups")).ToContainTextAsync("300 / 150 / 450");

        Assert.Contains(requestedUrls, url => url.Contains($"/api/monitor/traces/{TraceId}/spans", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/raw", StringComparison.OrdinalIgnoreCase));
    }

    private static long SeedProjectedTrace(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: TraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: AgentTracePayload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string AgentTracePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-design","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"trace-design","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"150"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"75"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"40"}}
           ]},
          {"traceId":"trace-design","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
           ]},
          {"traceId":"trace-design","spanId":"4000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000003000000000","endTimeUnixNano":"1710000004000000000",
           "status":{"code":2,"message":"failed"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"write_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}},
             {"key":"error.type","value":{"stringValue":"tool_failure"}}
           ]},
          {"traceId":"trace-design","spanId":"5000","parentSpanId":"1000","name":"post_tool_hook",
           "startTimeUnixNano":"1710000004000000000","endTimeUnixNano":"1710000005000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"post_tool_hook"}}
           ]}
        ]}]}]}
        """;

}

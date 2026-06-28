using Microsoft.Playwright;
using System.Net;
using System.Net.Sockets;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorDesignViewPlaywrightTests
{
    private const string TraceId = "trace-design";

    [Fact]
    public async Task TraceDetailDesignViews_RenderFromSanitizedSpansOnly()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        Assert.Equal("rgb(30, 30, 30)", await page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        Assert.Equal("true", await page.Locator("#tab-summary").GetAttributeAsync("aria-selected"));

        await page.Locator("#tab-timeline").ClickAsync();
        await Expect(page.Locator("#tab-timeline")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.Locator("#timeline-count")).ToContainTextAsync("5 of 5 spans");
        await page.Locator("#timeline-errors-only").CheckAsync();
        await Expect(page.Locator("#timeline-count")).ToContainTextAsync("1 of 5 spans");
        await Expect(page.Locator("#timeline-rows tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("#timeline-rows")).ToContainTextAsync("tool_failure");

        await page.Locator("input[name='timeline-sort'][value='tokens']").CheckAsync();
        await page.Locator("#timeline-errors-only").UncheckAsync();
        await Expect(page.Locator("#timeline-rows tr").First).ToContainTextAsync("chat");
        await Expect(page.Locator("#timeline-rows tr").First).ToContainTextAsync("300 / 150 / 450");

        await page.Locator("#tab-flow").ClickAsync();
        await Expect(page.Locator("#tab-flow")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.Locator("#flow-status")).ToContainTextAsync("5 spans");
        await Expect(page.Locator("#flow-chart canvas").First).ToBeVisibleAsync();
        Assert.True(await FlowChartHasDrawnPixelsAsync(page), "Flow Chart canvas should contain rendered graph pixels.");

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

    private static async Task<bool> FlowChartHasDrawnPixelsAsync(IPage page) =>
        await page.Locator("#flow-chart").EvaluateAsync<bool>(
            """
            element => {
                for (const canvas of element.querySelectorAll('canvas')) {
                    const context = canvas.getContext('2d');
                    if (!context || canvas.width === 0 || canvas.height === 0) {
                        continue;
                    }

                    const sampleWidth = Math.min(canvas.width, 400);
                    const sampleHeight = Math.min(canvas.height, 300);
                    const data = context.getImageData(0, 0, sampleWidth, sampleHeight).data;
                    for (let i = 3; i < data.length; i += 4) {
                        if (data[i] !== 0) {
                            return true;
                        }
                    }
                }

                return false;
            }
            """);

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

    private static async Task<RunningHost> StartHostAsync(MonitorTempDirectory temp)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, SanitizedOnly: false, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) }, url);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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

    private sealed class RunningHost(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await app.StopAsync();
            }
            catch
            {
                // Ignore stop faults during teardown.
            }

            await app.DisposeAsync();
        }
    }
}

using Microsoft.Playwright;
using System.Text.Json;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public class MonitorAgentExecutionPlaywrightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceDetail_ClaudeContentDisabledHidesRawControlsAndNullTokenCard(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/trace-list?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ClaudeTraceListBody(contentState: "not_captured"),
        }));

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#trace-source-evidence")).ToContainTextAsync("内容は取得されていません");
        await Expect(page.Locator("#token-total-card")).ToBeHiddenAsync();
        await Expect(page.Locator("#copilot-open")).ToBeHiddenAsync();
        await Expect(page.Locator("#raw-section")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task TraceDetail_ClaudeSchemaDriftShowsReasonAndJapaneseNextAction()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/trace-list?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ClaudeTraceListBody(
                contentState: "available",
                compatibilityState: "schema_drift_detected",
                reasonCodes: ["schema_drift_detected"],
                nextAction: "capture_fixture_and_review_mapping"),
        }));

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var source = page.Locator("#trace-source-evidence");
        await Expect(source).ToContainTextAsync("スキーマ変更を検出");
        await Expect(source).ToContainTextAsync("スキーマ変更のため完全性を確認できません");
        await Expect(source).ToContainTextAsync("fixture を取得してマッピングを確認してください");
    }

    [Fact]
    public async Task TraceDetail_SourceFactsFailureIsShownWithoutBreakingFlow()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/trace-list?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 503,
            ContentType = "application/json",
            Body = "{\"accepted\":false,\"error\":\"persistence_busy\"}",
        }));

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#trace-source-evidence")).ToContainTextAsync("ソース情報を読み込めませんでした");
        await Expect(page.Locator("#flow-view .agent-container")).ToHaveCountAsync(4);
    }

    [Theory]
    [InlineData("unknown_parent", "親スパンが見つかりません")]
    [InlineData("ambiguous", "親子関係を一意に決められません")]
    [InlineData("duplicate_span_id", "親子関係を一意に決められません")]
    [InlineData("cycle_detected", "親子関係に循環があります")]
    public async Task TraceDetail_ClaudeExactOnlyUnresolvedStatesNeverShowInference(string graphState, string expectedLabel)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/api/monitor/trace-list?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ClaudeTraceListBody(contentState: "not_captured"),
        }));
        await page.RouteAsync("**/agent-graph", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = ClaudeUnresolvedGraphBody(graphState),
        }));

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var source = page.Locator("#trace-source-evidence");
        await Expect(source).ToBeVisibleAsync();
        await Expect(source).ToContainTextAsync("Claude Code");
        await Expect(source).ToContainTextAsync("親子関係は送信元の親スパンだけで判定します");
        await Expect(source).ToContainTextAsync(expectedLabel);
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("関係 判定不能");
        await Expect(page.Locator("#flow-view .relationship-badge")).ToContainTextAsync("判定不能");
        await Expect(page.Locator("#trace-detail-root")).Not.ToContainTextAsync("推定");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceDetail_RendersNestedParallelAgentsAndAgentInspector(bool sanitizedOnly)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: DisabledWorkers);
        var graphResponse = await host.Client.GetAsync($"/api/monitor/traces/{TraceId}/agent-graph");
        Assert.True(graphResponse.IsSuccessStatusCode, await graphResponse.Content.ReadAsStringAsync());
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var requestedUrls = new List<string>();
        var pageErrors = new List<string>();
        page.Request += (_, request) => requestedUrls.Add(request.Url);
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForTimeoutAsync(250);
        Assert.True(pageErrors.Count == 0, string.Join(" | ", pageErrors));
        await Expect(page.Locator("#agent-summary-state")).ToHaveTextAsync("Sub-agent 3回検出");
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("main-agent");
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("ユニーク 3");
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("最大深度 2");
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("Agent並行 1");
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("関係 exact");

        await Expect(page.Locator("#flow-view .agent-container")).ToHaveCountAsync(4);
        await Expect(page.Locator("#flow-view .agent-container[data-agent-id='a100'] .agent-container[data-agent-id='a110']")).ToHaveCountAsync(1);
        await Expect(page.Locator("#flow-view .agent-container[data-agent-id='a100'] .turn-card[data-span-id='l101']")).ToHaveCountAsync(1);
        await Expect(page.Locator("#flow-view .agent-container[data-agent-id='a110'] .tool-card[data-span-id='x112']")).ToHaveCountAsync(1);
        await Expect(page.Locator("#flow-view .agent-parallel-group")).ToContainTextAsync("Agent並行 2件");
        await Expect(page.Locator("#flow-view .parallel-badge")).ToHaveCountAsync(0);

        var research = page.Locator("#flow-view .agent-container[data-agent-id='a100']");
        await Expect(research).ToContainTextAsync("sub · research-agent");
        await Expect(research).ToContainTextAsync("caller main-agent");
        await Expect(research).ToContainTextAsync("gpt-research");
        await Expect(research).ToContainTextAsync("子Agent 1");
        var flowCollapse = research.Locator(":scope > .agent-container-head > .agent-collapse");
        await Expect(flowCollapse).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(flowCollapse).ToHaveAttributeAsync("aria-label", "Agent セクションを折りたたむ");
        await flowCollapse.ClickAsync();
        await Expect(flowCollapse).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(flowCollapse).ToHaveAttributeAsync("aria-label", "Agent セクションを展開する");
        await Expect(research.Locator(":scope > .agent-owned-content")).ToBeHiddenAsync();
        await flowCollapse.ClickAsync();

        await research.Locator(":scope > .agent-container-head > .agent-select").ClickAsync();
        await Expect(page.Locator("#span-inspector")).ToBeVisibleAsync();
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("Agent 詳細");
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("所有ターン");
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("所有ツール");
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("relationship source");
        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("parent_span");

        if (sanitizedOnly)
        {
            await Expect(page.Locator("#span-inspector")).ToContainTextAsync("sanitized な Agent 詳細");
            Assert.DoesNotContain(requestedUrls, url => url.Contains("/spans/a100/detail", StringComparison.Ordinal));
            Assert.DoesNotContain("RAW_AGENT_INSTRUCTION", await page.ContentAsync());
        }
        else
        {
            Assert.Contains(requestedUrls, url => url.Contains($"/traces/{TraceId}/spans/a100/detail", StringComparison.Ordinal));
            await Expect(page.Locator("#span-inspector")).ToContainTextAsync("RAW_AGENT_INSTRUCTION");
            await Expect(page.Locator("#span-inspector")).ToContainTextAsync("RAW_AGENT_RESPONSE");
        }

        await page.Locator("#view-toggle .view-btn[data-view='waterfall']").ClickAsync();
        await Expect(page.Locator("#waterfall-view .wf-agent")).ToHaveCountAsync(4);
        await Expect(page.Locator("#waterfall-view .wf-agent[data-span-id='a110'] .wf-name")).ToHaveCSSAsync("padding-left", "48px");
        await Expect(page.Locator("#waterfall-view .wf-owned[data-owner-agent-id='a110']")).ToHaveCountAsync(2);
        await Expect(page.Locator("#waterfall-view .wf-agent-parallel")).ToHaveCountAsync(2);
        await Expect(page.Locator("#waterfall-view .wf-inconsistent[data-span-id='x203']")).ToContainTextAsync("時刻矛盾");
        var waterfallCollapse = page.Locator("#waterfall-view .wf-agent[data-span-id='a100'] .wf-collapse");
        await Expect(waterfallCollapse).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(waterfallCollapse).ToHaveAttributeAsync("aria-label", "Agent セクションを折りたたむ");
        await waterfallCollapse.ClickAsync();
        await Expect(waterfallCollapse).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(waterfallCollapse).ToHaveAttributeAsync("aria-label", "Agent セクションを展開する");
        Assert.Empty(pageErrors);
    }

    [Fact]
    public async Task TraceDetail_DistinguishesInferredAndUnresolvedRelationships()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, InferredGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("関係 判定不能");
        await Expect(page.Locator("#flow-view .agent-container[data-agent-id='a100'] .relationship-badge")).ToHaveTextAsync("推定");
        await Expect(page.Locator("#flow-view [data-span-id='u101'] .relationship-badge")).ToHaveTextAsync("判定不能");
        await page.Locator("#view-toggle .view-btn[data-view='waterfall']").ClickAsync();
        await Expect(page.Locator("#waterfall-view .wf-agent[data-span-id='a100']")).ToContainTextAsync("推定");
        await Expect(page.Locator("#waterfall-view .wf-unresolved[data-span-id='u101']")).ToContainTextAsync("判定不能");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task TraceDetail_GraphFailureKeepsExistingSpanViewAndHonestSummary(int statusCode)
    {
        using var temp = new MonitorTempDirectory();
        MonitorRichTrace.Seed(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/agent-graph", async route =>
        {
            if (statusCode == 0)
            {
                await route.AbortAsync();
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = statusCode,
                ContentType = "application/json",
                Body = "{\"accepted\":false,\"error\":\"graph_unavailable\"}",
            });
        });

        await page.GotoAsync($"{host.Url}/traces/{MonitorRichTrace.TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#errors-only").UncheckAsync();

        await Expect(page.Locator("#agent-summary-state")).ToHaveTextAsync("Sub-agent利用を判定できません");
        await Expect(page.Locator("#flow-view .turn-card")).ToHaveCountAsync(3);
        await Expect(page.Locator("#flow-view .agent-container")).ToHaveCountAsync(0);
        await Expect(page.Locator("#flow-status")).ToBeHiddenAsync();
        await page.Locator("#view-toggle .view-btn[data-view='waterfall']").ClickAsync();
        await Expect(page.Locator("#waterfall-view .wf-agent")).ToHaveCountAsync(0);
        await Expect(page.Locator("#waterfall-view .wf-llm").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TraceDetail_MultipleRootsShowsRootNamesAndChronologicalTopLevelWaterfall()
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, MultiRootChronologicalPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("#agent-summary")).ToContainTextAsync("root 2 alpha-agent, beta-agent");
        await Expect(page.Locator("#agent-summary")).Not.ToContainTextAsync("main alpha-agent");
        await page.Locator("#view-toggle .view-btn[data-view='waterfall']").ClickAsync();

        var order = await page.Locator("#waterfall-view .wf-row[data-span-id]").EvaluateAllAsync<string[]>(
            "rows => rows.map(row => row.dataset.spanId)");
        Assert.Equal(new[] { "u000", "a100", "u200", "a300" }, order);
    }

    [Theory]
    [InlineData("non-2xx")]
    [InlineData("network")]
    [InlineData("parse")]
    public async Task TraceDetail_AgentRawDetailFailureTerminatesAsUnavailable(string failure)
    {
        using var temp = new MonitorTempDirectory();
        Seed(temp, ExactGraphPayload);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync($"**/traces/{TraceId}/spans/a100/detail", async route =>
        {
            if (failure == "network")
            {
                await route.AbortAsync();
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = failure == "non-2xx" ? 503 : 200,
                    ContentType = "application/json",
                    Body = failure == "parse" ? "not-json" : "{\"accepted\":false}",
                });
            }
        });

        await page.GotoAsync($"{host.Url}/traces/{TraceId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#flow-view .agent-container[data-agent-id='a100'] > .agent-container-head > .agent-select").ClickAsync();

        await Expect(page.Locator("#span-inspector")).ToContainTextAsync("Sub-agent の指示・応答を取得できませんでした");
        await Expect(page.Locator("#span-inspector")).Not.ToContainTextAsync("読み込み中");
    }

    private static MonitorHostTestOptions DisabledWorkers => new() { StartWriter = false, StartProjectionWorker = false };

    private static void Seed(MonitorTempDirectory temp, string payload)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, TraceId, DateTimeOffset.UnixEpoch.AddMinutes(1), null, payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(3));
    }

    private static string ClaudeTraceListBody(
        string contentState,
        string compatibilityState = "supported",
        string[]? reasonCodes = null,
        string nextAction = "none") => JsonSerializer.Serialize(new
    {
        items = new[]
        {
            new
            {
                trace_id = TraceId,
                source_diagnostic = new
                {
                    source_surface = "claude-code",
                    source_application_version = (string?)null,
                    source_adapter = "claude-code-otel",
                    adapter_version = "1",
                    schema_fingerprint = (string?)null,
                    compatibility_state = compatibilityState,
                    reason_codes = reasonCodes ?? Array.Empty<string>(),
                    next_action = nextAction,
                },
                binding_state = "otel_only",
                completeness = "unbound",
                completeness_reason_codes = new[] { "missing_native_session_id" },
                content_state = contentState,
            },
        },
        total_matched = 1,
        total_matched_tokens = 0,
        offset = 0,
        limit = 50,
    });

    private static string ClaudeUnresolvedGraphBody(string graphState) => JsonSerializer.Serialize(new
    {
        summary = new
        {
            main_agent_name = "main-agent",
            root_agent_count = 1,
            subagent_invocation_count = 0,
            unique_subagent_count = 0,
            max_agent_depth = 0,
            parallel_agent_group_count = 0,
            relationship_quality = "undeterminable",
            agent_presence = "detected",
        },
        agents = new[]
        {
            new
            {
                span_id = "a000",
                agent_name = "main-agent",
                agent_role = "main",
                caller_agent_span_id = (string?)null,
                model = (string?)null,
                started_at = (string?)null,
                ended_at = (string?)null,
                duration_ms = (double?)null,
                input_tokens = (int?)null,
                output_tokens = (int?)null,
                total_tokens = (int?)null,
                status = "ok",
                child_agent_count = 0,
                agent_depth = 0,
                relationship_source = "parent_span",
                relationship_confidence = "exact",
            },
        },
        span_ownership = new[]
        {
            new
            {
                span_id = "l001",
                owning_agent_span_id = (string?)null,
                relationship_source = "unresolved",
                relationship_confidence = "unknown",
            },
        },
        parallel_groups = Array.Empty<string[]>(),
        graph_warnings = graphState == "ambiguous" ? Array.Empty<string>() : new[] { graphState },
    });

    private const string TraceId = "agent-ui";

    private const string ExactGraphPayload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"traceId":"agent-ui","spanId":"a000","name":"invoke_agent","startTimeUnixNano":"1751500000000000000","endTimeUnixNano":"1751500060000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"main-agent"}}]},
          {"traceId":"agent-ui","spanId":"l001","parentSpanId":"a000","name":"chat","startTimeUnixNano":"1751500001000000000","endTimeUnixNano":"1751500005000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"a100","parentSpanId":"a000","name":"invoke_agent","startTimeUnixNano":"1751500010000000000","endTimeUnixNano":"1751500040000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"research-agent"}},{"key":"gen_ai.response.model","value":{"stringValue":"gpt-research"}},{"key":"gen_ai.prompt","value":{"stringValue":"RAW_AGENT_INSTRUCTION"}},{"key":"gen_ai.response.text","value":{"stringValue":"RAW_AGENT_RESPONSE"}},{"key":"gen_ai.usage.input_tokens","value":{"intValue":"120"}},{"key":"gen_ai.usage.output_tokens","value":{"intValue":"30"}}]},
          {"traceId":"agent-ui","spanId":"l101","parentSpanId":"a100","name":"chat","startTimeUnixNano":"1751500011000000000","endTimeUnixNano":"1751500015000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"x102","parentSpanId":"l101","name":"execute_tool","startTimeUnixNano":"1751500015000000000","endTimeUnixNano":"1751500017000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},{"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}}]},
          {"traceId":"agent-ui","spanId":"a110","parentSpanId":"a100","name":"invoke_agent","startTimeUnixNano":"1751500020000000000","endTimeUnixNano":"1751500030000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"nested-agent"}}]},
          {"traceId":"agent-ui","spanId":"l111","parentSpanId":"a110","name":"chat","startTimeUnixNano":"1751500021000000000","endTimeUnixNano":"1751500024000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"x112","parentSpanId":"l111","name":"execute_tool","startTimeUnixNano":"1751500024000000000","endTimeUnixNano":"1751500026000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},{"key":"gen_ai.tool.name","value":{"stringValue":"grep_search"}}]},
          {"traceId":"agent-ui","spanId":"a200","parentSpanId":"a000","name":"invoke_agent","startTimeUnixNano":"1751500012000000000","endTimeUnixNano":"1751500035000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"review-agent"}}]},
          {"traceId":"agent-ui","spanId":"l201","parentSpanId":"a200","name":"chat","startTimeUnixNano":"1751500013000000000","endTimeUnixNano":"1751500018000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"x202","parentSpanId":"l201","name":"execute_tool","startTimeUnixNano":"1751500018000000000","endTimeUnixNano":"1751500020000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},{"key":"gen_ai.tool.name","value":{"stringValue":"run_tests"}}]},
          {"traceId":"agent-ui","spanId":"x203","parentSpanId":"a200","name":"execute_tool","startTimeUnixNano":"1751500040000000000","endTimeUnixNano":"1751500042000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},{"key":"gen_ai.tool.name","value":{"stringValue":"late_tool"}}]}
        ]}]}]}
        """;

    private const string InferredGraphPayload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"traceId":"agent-ui","spanId":"a000","name":"invoke_agent","startTimeUnixNano":"1751500000000000000","endTimeUnixNano":"1751500060000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"main-agent"}}]},
          {"traceId":"agent-ui","spanId":"a100","parentSpanId":"missing-parent","name":"invoke_agent","startTimeUnixNano":"1751500010000000000","endTimeUnixNano":"1751500030000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"inferred-agent"}}]},
          {"traceId":"agent-ui","spanId":"l101","parentSpanId":"a100","name":"chat","startTimeUnixNano":"1751500011000000000","endTimeUnixNano":"1751500015000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"u101","parentSpanId":"unknown","name":"execute_tool","startTimeUnixNano":"1751500012000000000","endTimeUnixNano":"1751500013000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},{"key":"gen_ai.tool.name","value":{"stringValue":"ambiguous_tool"}}]}
        ]}]}]}
        """;

    private const string MultiRootChronologicalPayload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"traceId":"agent-ui","spanId":"u000","name":"chat","startTimeUnixNano":"1751500000000000000","endTimeUnixNano":"1751500001000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"a100","name":"invoke_agent","startTimeUnixNano":"1751500010000000000","endTimeUnixNano":"1751500015000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"alpha-agent"}}]},
          {"traceId":"agent-ui","spanId":"u200","name":"chat","startTimeUnixNano":"1751500020000000000","endTimeUnixNano":"1751500021000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"agent-ui","spanId":"a300","name":"invoke_agent","startTimeUnixNano":"1751500030000000000","endTimeUnixNano":"1751500035000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},{"key":"gen_ai.agent.name","value":{"stringValue":"beta-agent"}}]}
        ]}]}]}
        """;
}

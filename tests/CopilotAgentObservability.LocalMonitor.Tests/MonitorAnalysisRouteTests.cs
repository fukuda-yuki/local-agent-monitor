using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisRouteTests
{
    private const string TraceId = "trace-analysis-route";

    [Fact]
    public async Task SanitizedOnly_RemovesRawAnalysisSurfaces()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var start = await host.Client.PostAsJsonAsync(
            $"/traces/{TraceId}/analysis",
            new { focus = "latency" });
        var result = await host.Client.GetAsync($"/traces/{TraceId}/analysis/runs/1");

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task StartRun_RequiresCsrfHeaderAndCreatesQueuedRun()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);

        var blocked = await host.Client.PostAsJsonAsync(
            $"/traces/{TraceId}/analysis",
            new { focus = "tool-usage" });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/traces/{TraceId}/analysis")
        {
            Content = JsonContent.Create(new { focus = "tool-usage", spanId = "span-tool" }),
        };
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        var accepted = await host.Client.SendAsync(request);
        var body = await accepted.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(accepted.Headers.CacheControl?.NoStore);
        Assert.Contains("\"status\":\"queued\"", body);
        Assert.DoesNotContain("bridge_token", body);
    }

    [Fact]
    public async Task BridgeToolRoute_IsNotExposed()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);
        var runId = await StartRunAsync(host);

        var response = await host.Client.GetAsync($"/traces/{TraceId}/analysis/runs/{runId}/tools/get_raw_trace");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RepositorySafeSummary_DoesNotIncludeRawMarkers()
    {
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        await using var host = await StartHostAsync(temp);
        var runId = await StartRunAsync(host);

        var summary = await host.Client.GetStringAsync($"/traces/{TraceId}/analysis/runs/{runId}/safe-summary");

        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", summary);
        Assert.DoesNotContain("leak-marker@example.com", summary);
        Assert.Contains("trace-analysis-route", summary);
        Assert.Contains("repository_safe", summary);
    }

    private static async Task<long> StartRunAsync(RunningMonitorHost host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/traces/{TraceId}/analysis")
        {
            Content = JsonContent.Create(new { focus = "latency" }),
        };
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        var response = await host.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("run_id").GetInt64();
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

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false)
    {
        var analysisStore = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        return MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions
            {
                StartWriter = false,
                StartProjectionWorker = false,
                AnalysisStore = analysisStore,
                AnalysisRunner = new CompletingAnalysisRunner(analysisStore),
            });
    }

    private sealed class CompletingAnalysisRunner : IMonitorAnalysisRunner
    {
        private readonly IMonitorAnalysisStore analysisStore;

        public CompletingAnalysisRunner(IMonitorAnalysisStore analysisStore)
        {
            this.analysisStore = analysisStore;
        }

        public Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
        {
            analysisStore.MarkRunning(context.RunId, DateTimeOffset.UnixEpoch.AddMinutes(4));
            analysisStore.CompleteRun(
                context.RunId,
                "SECRET_PROMPT_TEXT_MARKER leak-marker@example.com",
                DateTimeOffset.UnixEpoch.AddMinutes(5));
            return Task.CompletedTask;
        }
    }

    private const string AgentTracePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-analysis-route","spanId":"span-chat","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"trace-analysis-route","spanId":"span-tool","parentSpanId":"span-chat","name":"execute_tool",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}}
           ]}
        ]}]}]}
        """;
}

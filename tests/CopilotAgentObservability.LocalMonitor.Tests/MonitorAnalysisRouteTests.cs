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

    [Fact]
    public async Task StartRun_CapturesQuestionAndHistoryForTheRunner()
    {
        // D045 (history resend): the drawer's follow-up chat sends question +
        // prior Q&A with each new run; the runner receives them in the context
        // and no chat state is persisted server-side.
        using var temp = new MonitorTempDirectory();
        SeedProjectedTrace(temp);
        var analysisStore = new SqliteMonitorAnalysisStore(temp.DatabasePath);
        var runner = new CompletingAnalysisRunner(analysisStore);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            AnalysisStore = analysisStore,
            AnalysisRunner = runner,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/traces/{TraceId}/analysis")
        {
            Content = JsonContent.Create(new
            {
                focus = "tokens",
                question = "FOLLOWUP_QUESTION_MARKER",
                history = new[]
                {
                    new { question = "PRIOR_Q_MARKER", answer = "PRIOR_A_MARKER" },
                },
            }),
        };
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = Assert.Single(runner.Contexts);
        Assert.Equal("FOLLOWUP_QUESTION_MARKER", context.Question);
        var turn = Assert.Single(context.History!);
        Assert.Equal("PRIOR_Q_MARKER", turn.Question);
        Assert.Equal("PRIOR_A_MARKER", turn.Answer);
    }

    [Fact]
    public void BuildPrompt_EmbedsHistoryAndFollowUpQuestion()
    {
        var context = new MonitorAnalysisContext(
            RunId: 1,
            TraceId: "trace-x",
            RawRecordId: null,
            SpanId: null,
            Focus: MonitorAnalysisFocus.Tokens,
            Question: "FOLLOWUP_QUESTION_MARKER",
            History:
            [
                new AnalysisHistoryTurn("PRIOR_Q_MARKER", "PRIOR_A_MARKER"),
            ]);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.Contains("trace-x", prompt);
        Assert.Contains("Q: PRIOR_Q_MARKER", prompt);
        Assert.Contains("A: PRIOR_A_MARKER", prompt);
        Assert.Contains("Follow-up question: FOLLOWUP_QUESTION_MARKER", prompt);
    }

    [Fact]
    public void BuildPrompt_WithoutQuestion_OmitsChatBlocks()
    {
        var context = new MonitorAnalysisContext(1, "trace-x", null, null, MonitorAnalysisFocus.Cache);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.DoesNotContain("Prior Q&A", prompt);
        Assert.DoesNotContain("Follow-up question", prompt);
    }

    [Fact]
    public void BuildPrompt_InstructionDiagnosis_EmbedsTaxonomyAndFindingContract()
    {
        var context = new MonitorAnalysisContext(1, "trace-x", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.Contains("focus instruction-diagnosis", prompt);
        Assert.Contains(DotNetCopilotRawAnalysisRunner.InstructionDiagnosisPromptBlock, prompt);
        Assert.Contains("trace-internal evidence only", prompt);
        Assert.Contains("goal-clarity", prompt);
        Assert.Contains("ambiguity", prompt);
        Assert.Contains("missing-acceptance-criteria", prompt);
        Assert.Contains("task-size-split", prompt);
        Assert.Contains("missing-context-constraints", prompt);
        Assert.Contains("exactly these four parts", prompt);
        Assert.Contains("a finding without a citable evidence reference is forbidden", prompt);
        Assert.Contains("Zero findings is a valid result and must be stated explicitly", prompt);
        Assert.Contains("in Japanese", prompt);
        Assert.DoesNotContain("Return concise findings, likely causes, and recommended next checks.", prompt);
    }

    [Fact]
    public void BuildPrompt_ExistingFocuses_KeepGenericPromptWithoutTaxonomy()
    {
        MonitorAnalysisFocus[] existingFocuses =
        [
            MonitorAnalysisFocus.Latency,
            MonitorAnalysisFocus.Tokens,
            MonitorAnalysisFocus.Cache,
            MonitorAnalysisFocus.Errors,
            MonitorAnalysisFocus.ToolUsage,
            MonitorAnalysisFocus.AgentFlow,
        ];

        foreach (var focus in existingFocuses)
        {
            var context = new MonitorAnalysisContext(1, "trace-x", null, null, focus);

            var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

            var expected =
                $"Analyze Local Monitor trace trace-x with focus {focus.ToWireValue()}.{Environment.NewLine}" +
                $"Use the available tools to inspect the trace. For raw evidence, cite trace/span/raw-record ids instead of copying long raw bodies.{Environment.NewLine}" +
                "Return concise findings, likely causes, and recommended next checks.";
            Assert.Equal(expected, prompt);
        }
    }

    [Fact]
    public void BuildPrompt_InstructionDiagnosis_KeepsHistoryAndFollowUpBlocks()
    {
        var context = new MonitorAnalysisContext(
            RunId: 1,
            TraceId: "trace-x",
            RawRecordId: null,
            SpanId: null,
            Focus: MonitorAnalysisFocus.InstructionDiagnosis,
            Question: "FOLLOWUP_QUESTION_MARKER",
            History:
            [
                new AnalysisHistoryTurn("PRIOR_Q_MARKER", "PRIOR_A_MARKER"),
            ]);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.Contains("goal-clarity", prompt);
        Assert.Contains("Q: PRIOR_Q_MARKER", prompt);
        Assert.Contains("A: PRIOR_A_MARKER", prompt);
        Assert.Contains("Follow-up question: FOLLOWUP_QUESTION_MARKER", prompt);
        Assert.True(
            prompt.IndexOf("goal-clarity", StringComparison.Ordinal)
                < prompt.IndexOf("Prior Q&A", StringComparison.Ordinal),
            "The taxonomy block must precede the D045 chat blocks.");
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

        public List<MonitorAnalysisContext> Contexts { get; } = new();

        public Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
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

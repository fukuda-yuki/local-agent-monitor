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
        var instructionDiagnosis = await host.Client.PostAsJsonAsync(
            $"/traces/{TraceId}/analysis",
            new { focus = "instruction-diagnosis" });
        var result = await host.Client.GetAsync($"/traces/{TraceId}/analysis/runs/1");

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, instructionDiagnosis.StatusCode);
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

    [Fact]
    public void MonitorAnalysisToolData_Create_PopulatesInstructionEvidence()
    {
        // Sprint20 M3 (D047): Create runs the deterministic extractor and resolves
        // siblings through ListConversationTraces using the analyzed trace's
        // conversation_id. The analyzed trace (start 00:02) shares conversation
        // conv-ie with an earlier sibling (start 00:01), so the sibling sorts first
        // and the analyzed trace is position 2 of 2.
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedEvidenceTrace(store, "trace-ie", conversationId: "conv-ie", startTime: "2026-07-01T00:02:00.000+00:00", withError: true);
        SeedEvidenceTrace(store, "trace-sib", conversationId: "conv-ie", startTime: "2026-07-01T00:01:00.000+00:00", withError: false);
        var projectionStore = new RawTelemetryStoreProjectionStore(store);
        var context = new MonitorAnalysisContext(1, "trace-ie", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var data = MonitorAnalysisToolData.Create(projectionStore, context);

        var errorSpan = Assert.Single(data.InstructionEvidence.ErrorSpans);
        Assert.Equal("trace-ie-span-err", errorSpan.SpanId);
        var conversation = data.InstructionEvidence.Conversation;
        Assert.NotNull(conversation);
        Assert.Equal("conv-ie", conversation!.ConversationId);
        Assert.Equal(new[] { "trace-sib", "trace-ie" }, conversation.TraceIds.ToArray());
        Assert.Equal(2, conversation.TraceCount);
        Assert.Equal(2, conversation.AnalyzedTraceIndex);
        var conversationContext = data.InstructionEvidence.ConversationContext;
        Assert.NotNull(conversationContext);
        Assert.Equal(new[] { "trace-sib", "trace-ie" }, conversationContext!.Traces.Select(trace => trace.TraceId).ToArray());
        Assert.Equal(new[] { -1, 0 }, conversationContext.Traces.Select(trace => trace.RelativePosition).ToArray());
    }

    [Fact]
    public void MonitorAnalysisToolData_Create_NoConversationId_ProducesNullConversation()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedEvidenceTrace(store, "trace-nc", conversationId: null, startTime: "2026-07-01T00:02:00.000+00:00", withError: true);
        var projectionStore = new RawTelemetryStoreProjectionStore(store);
        var context = new MonitorAnalysisContext(1, "trace-nc", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var data = MonitorAnalysisToolData.Create(projectionStore, context);

        Assert.Single(data.InstructionEvidence.ErrorSpans);
        Assert.Null(data.InstructionEvidence.Conversation);
        Assert.Null(data.InstructionEvidence.ConversationContext);
    }

    [Fact]
    public void MonitorAnalysisToolData_Create_LoadsOnlyBoundedConversationWindow()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        for (var index = 0; index < 7; index++)
        {
            SeedEvidenceTrace(
                store,
                $"trace-{index}",
                conversationId: "conv-window",
                startTime: $"2026-07-01T00:{index:00}:00.000+00:00",
                withError: index == 3);
        }

        var projectionStore = new CountingProjectionStore(new RawTelemetryStoreProjectionStore(store));
        var context = new MonitorAnalysisContext(1, "trace-3", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var data = MonitorAnalysisToolData.Create(projectionStore, context);

        var conversationContext = Assert.IsType<InstructionEvidenceConversationContext>(data.InstructionEvidence.ConversationContext);
        Assert.Equal(new[] { "trace-1", "trace-2", "trace-3", "trace-4", "trace-5" }, conversationContext.Traces.Select(trace => trace.TraceId).ToArray());
        Assert.DoesNotContain("trace-0", projectionStore.SpansForTraceCalls);
        Assert.DoesNotContain("trace-6", projectionStore.SpansForTraceCalls);
        Assert.DoesNotContain("trace-0", projectionStore.RawRecordsByTraceCalls);
        Assert.DoesNotContain("trace-6", projectionStore.RawRecordsByTraceCalls);
    }

    [Fact]
    public void MonitorAnalysisToolData_Create_ExistingFocusDoesNotLoadConversationContext()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedEvidenceTrace(store, "trace-current", conversationId: "conv-existing", startTime: "2026-07-01T00:02:00.000+00:00", withError: false);
        SeedEvidenceTrace(store, "trace-sibling", conversationId: "conv-existing", startTime: "2026-07-01T00:01:00.000+00:00", withError: false);
        var projectionStore = new CountingProjectionStore(new RawTelemetryStoreProjectionStore(store));
        var context = new MonitorAnalysisContext(1, "trace-current", null, null, MonitorAnalysisFocus.Tokens);

        var data = MonitorAnalysisToolData.Create(projectionStore, context);

        Assert.Null(data.InstructionEvidence.ConversationContext);
        Assert.DoesNotContain("trace-sibling", projectionStore.SpansForTraceCalls);
        Assert.DoesNotContain("trace-sibling", projectionStore.RawRecordsByTraceCalls);
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

    private static void SeedEvidenceTrace(
        RawTelemetryStore store,
        string traceId,
        string? conversationId,
        string startTime,
        bool withError)
    {
        var received = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: received,
            ResourceAttributesJson: null,
            PayloadJson: "{}");
        var id = store.Insert(record);
        var spanCount = withError ? 2 : 1;
        store.ApplyProjection(
            id,
            record.Source,
            received,
            new MonitorRecordProjection(
                TraceId: traceId,
                ClientKind: "vscode-copilot-chat",
                SpanCount: spanCount,
                TraceContributions:
                [
                    new MonitorTraceContribution(
                        TraceId: traceId,
                        ClientKind: "vscode-copilot-chat",
                        ExperimentId: null,
                        TaskId: null,
                        TaskCategory: null,
                        AgentVariant: null,
                        PromptVersion: null,
                        SpanCount: spanCount,
                        ToolCallCount: withError ? 1 : 0,
                        ErrorCount: withError ? 1 : 0,
                        RepositoryName: null,
                        WorkspaceLabel: null,
                        RepoSnapshot: null),
                ]),
            DateTimeOffset.UnixEpoch.AddMinutes(2));

        var spans = new List<MonitorSpanProjection>
        {
            EvidenceSpan(traceId, $"{traceId}-span-chat", ordinal: 0, operation: "chat", category: "llm_call",
                toolName: null, status: "ok", errorType: null, conversationId, startTime),
        };
        if (withError)
        {
            spans.Add(EvidenceSpan(traceId, $"{traceId}-span-err", ordinal: 1, operation: "execute_tool", category: "tool",
                toolName: "read_file", status: "error", errorType: "io_error", conversationId, startTime));
        }

        store.ApplySpanProjection(id, spans, DateTimeOffset.UnixEpoch.AddMinutes(3));
    }

    private static MonitorSpanProjection EvidenceSpan(
        string traceId,
        string spanId,
        int ordinal,
        string operation,
        string category,
        string? toolName,
        string status,
        string? errorType,
        string? conversationId,
        string startTime) =>
        new(
            TraceId: traceId, SpanId: spanId, ParentSpanId: null, SpanOrdinal: ordinal,
            Operation: operation, Category: category,
            ToolName: toolName, ToolType: null, McpToolName: null, McpServerHash: null,
            AgentName: null, RequestModel: null, ResponseModel: null,
            InputTokens: null, OutputTokens: null, TotalTokens: null,
            ReasoningTokens: null, CacheReadTokens: null, CacheCreationTokens: null,
            Status: status, ErrorType: errorType, FinishReasons: null,
            ConversationId: conversationId, DurationMs: null,
            StartTime: startTime, EndTime: null);

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
        Assert.Contains("analyzed trace", prompt);
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
    public void BuildPrompt_InstructionDiagnosis_EmbedsEvidenceGroundingRules()
    {
        // Sprint20 M3 (D047 / prompt template v3): the instruction-diagnosis block
        // must direct the model to get_instruction_evidence and pin the per-category
        // coupling plus the raw-verified escape hatch.
        var context = new MonitorAnalysisContext(1, "trace-x", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.Contains("get_instruction_evidence", prompt);
        Assert.Contains("error_spans", prompt);
        Assert.Contains("retry_chains", prompt);
        Assert.Contains("turn_tokens", prompt);
        Assert.Contains("user_instruction", prompt);
        Assert.Contains("conversation", prompt);
        Assert.Contains("raw-verified", prompt);
    }

    [Fact]
    public void BuildPrompt_InstructionDiagnosis_EmbedsConversationScopeRules()
    {
        var context = new MonitorAnalysisContext(1, "trace-x", null, null, MonitorAnalysisFocus.InstructionDiagnosis);

        var prompt = DotNetCopilotRawAnalysisRunner.BuildPrompt(context);

        Assert.Contains("conversation_context", prompt);
        Assert.Contains("analyzed trace", prompt);
        Assert.Contains("sibling trace", prompt);
        Assert.Contains("trace_id", prompt);
        Assert.Contains("bounded window", prompt);
        Assert.Contains("outside the bounded window", prompt);
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

    private sealed class CountingProjectionStore : IMonitorProjectionStore
    {
        private readonly IMonitorProjectionStore inner;

        public CountingProjectionStore(IMonitorProjectionStore inner)
        {
            this.inner = inner;
        }

        public List<string> SpansForTraceCalls { get; } = new();

        public List<string> RawRecordsByTraceCalls { get; } = new();

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) =>
            inner.ListUnprocessedForProjection(limit);

        public bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt) =>
            inner.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt);

        public ProjectionDisposition? GetProjectionDisposition(long rawRecordId) =>
            inner.GetProjectionDisposition(rawRecordId);

        public bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
            inner.TryBeginProjection(rawRecordId, expectedRevision, updatedAt);

        public bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
            inner.RecordProjectionFailure(rawRecordId, expectedRevision, updatedAt);

        public bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt, int expectedDispositionRevision) =>
            inner.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt, expectedDispositionRevision);

        public MonitorProjectionStatus GetProjectionStatus() =>
            inner.GetProjectionStatus();

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) =>
            inner.ListUnprocessedForSpanProjection(limit);

        public bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) =>
            inner.ApplySpanProjection(rawRecordId, spans, projectedAt);

        public MonitorProjectionStatus GetSpanProjectionStatus() =>
            inner.GetSpanProjectionStatus();

        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            inner.ListMonitorIngestions(afterRawRecordId, limit);

        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
            inner.ListMonitorTraces(afterId, limit);

        public MonitorTraceRow? GetMonitorTrace(string traceId) =>
            inner.GetMonitorTrace(traceId);

        public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) =>
            inner.ListMonitorSpans(traceId, afterId, limit);

        public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId)
        {
            SpansForTraceCalls.Add(traceId);
            return inner.GetSpansForTrace(traceId);
        }

        public RawTelemetryRecord? GetRawRecordById(long id) =>
            inner.GetRawRecordById(id);

        public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit)
        {
            RawRecordsByTraceCalls.Add(traceId);
            return inner.ListRawRecordsByTraceId(traceId, limit);
        }

        public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) =>
            inner.GetPeriodSummary(startInclusive, endExclusive);

        public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) =>
            inner.GetPerModelPeriodSummary(startInclusive, endExclusive);

        public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) =>
            inner.GetHourlyTokenDistribution(startInclusive, endExclusive);

        public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) =>
            inner.ListTopTokenTraces(startInclusive, endExclusive, limit);

        public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) =>
            inner.ListRecentMonitorTraces(limit);

        public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) =>
            inner.ListMonitorTracesFiltered(query);

        public MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) =>
            inner.GetMonitorSpan(traceId, spanId);

        public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) =>
            inner.ListConversationTraces(conversationId);
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

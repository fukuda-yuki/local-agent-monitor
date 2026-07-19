using System.Text;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using GitHub.Copilot;
using Microsoft.Extensions.Configuration;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class DotNetCopilotRawAnalysisRunner : IMonitorAnalysisRunner
{
    private readonly IMonitorAnalysisStore analysisStore;
    private readonly IMonitorProjectionStore projectionStore;
    private readonly IConfiguration configuration;
    private readonly IAnalysisSdkDirectoryOwner directoryOwner;
    private readonly ICopilotAnalysisSdkExecutor executor;
    private readonly TimeProvider timeProvider;

    public DotNetCopilotRawAnalysisRunner(
        IMonitorAnalysisStore analysisStore,
        IMonitorProjectionStore projectionStore,
        IConfiguration configuration)
        : this(analysisStore, projectionStore, configuration, new UnconfiguredDirectoryOwner(), new CopilotAnalysisSdkExecutor(), TimeProvider.System)
    {
    }

    internal DotNetCopilotRawAnalysisRunner(
        IMonitorAnalysisStore analysisStore,
        IMonitorProjectionStore projectionStore,
        IConfiguration configuration,
        IAnalysisSdkDirectoryOwner directoryOwner,
        ICopilotAnalysisSdkExecutor executor,
        TimeProvider timeProvider)
    {
        this.analysisStore = analysisStore;
        this.projectionStore = projectionStore;
        this.configuration = configuration;
        this.directoryOwner = directoryOwner;
        this.executor = executor;
        this.timeProvider = timeProvider;
    }

    public Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunAsync(context, CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    internal async Task RunAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
    {
        var operationToken = context.OperationToken ?? throw new InvalidOperationException("Analysis operation token is required.");
        RetentionRevisionFence? fence = null;
        var startedAt = timeProvider.GetUtcNow();
        analysisStore.MarkRunning(context.RunId, startedAt);
        fence = analysisStore.AppendEvent(context.RunId, operationToken, fence, "running", ".NET GitHub Copilot SDK analysis started.", startedAt);

        try
        {
            var settings = CopilotAnalysisSettings.From(configuration);
            if (!settings.Enabled) throw new InvalidOperationException("CopilotAnalysis is disabled by local configuration.");
            var run = analysisStore.GetRun(context.RunId);
            if (run is null || !MatchesContext(run, context))
                throw new AnalysisOwnershipException();
            if (!DateTimeOffset.TryParseExact(run.RequestedAt, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var requestedAt)
                || requestedAt.Offset != TimeSpan.Zero
                || !string.Equals(run.RequestedAt, requestedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new AnalysisOwnershipException();
            IAnalysisSdkDirectoryScope scope;
            try
            {
                scope = await directoryOwner.OpenAsync(context.RunId, requestedAt, settings.BaseDirectory, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AnalysisOwnershipException(exception);
            }
            Exception? primaryFailure = null;
            string result;
            try
            {
                using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.LeaseLostToken);
                fence = analysisStore.AppendEvent(context.RunId, operationToken, fence, "sdk_phase", "loading_local_tool_data", timeProvider.GetUtcNow());
                await using var data = await MonitorAnalysisToolData.CreateAsync(projectionStore, context, leaseCancellation.Token);
                result = await executor.ExecuteAsync(scope.ChildDirectory, settings.ToExecutionSettings(), new CopilotAnalysisToolRequest(BuildPrompt(context), data), leaseCancellation.Token);
            if (scope.IsLeaseLost) throw new AnalysisOwnershipException();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && (scope.IsLeaseLost || scope.LeaseLostToken.IsCancellationRequested))
            {
                primaryFailure = new AnalysisOwnershipException(exception);
                throw primaryFailure;
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await scope.DisposeAsync();
                }
                catch when (primaryFailure is not null)
                {
                }
                catch (Exception exception)
                {
                    throw new AnalysisOwnershipException(exception);
                }
            }
            if (scope.IsLeaseLost) throw new AnalysisOwnershipException();
            fence = analysisStore.CompleteRun(context.RunId, operationToken, fence, result, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            _ = analysisStore.FinishRun(
                context.RunId,
                operationToken,
                fence,
                MonitorAnalysisStatus.Canceled,
                "Analysis was canceled.",
                timeProvider.GetUtcNow());
        }
        catch (PersistenceBusyException)
        {
            _ = analysisStore.FinishRun(
                context.RunId,
                operationToken,
                fence,
                MonitorAnalysisStatus.Failed,
                "The local monitor raw store is busy. Retry the analysis.",
                timeProvider.GetUtcNow());
        }
        catch (AnalysisOwnershipException)
        {
            _ = analysisStore.FinishRun(context.RunId, operationToken, fence, MonitorAnalysisStatus.Failed, "Local analysis ownership could not be established.", timeProvider.GetUtcNow());
        }
        catch (Exception)
        {
            const string message = "SDK analysis failed.";
            fence = analysisStore.AppendEvent(context.RunId, operationToken, fence, "sdk_error", message, timeProvider.GetUtcNow());
            _ = analysisStore.FinishRun(
                context.RunId,
                operationToken,
                fence,
                MonitorAnalysisStatus.Failed,
                message,
                timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Instruction-diagnosis prompt block (D046 + D047 + D048, prompt template v4):
    /// taxonomy v1 with the category=evidence coupling, the per-category
    /// required-evidence rules grounding each finding in the
    /// <c>get_instruction_evidence</c> output (with a raw-verified escape hatch),
    /// the fixed 4-part finding format, and the no-evidence-no-finding rule,
    /// mirroring docs/specifications/interfaces/instruction-diagnosis-analysis.md.
    /// Internal for tests.
    /// </summary>
    internal const string InstructionDiagnosisPromptBlock =
        """
        Diagnose the implementation instructions the user gave the agent, using the analyzed trace as the anchor plus bounded same-conversation sibling trace evidence when available.
        Classify each finding into exactly one taxonomy v1 category:
        - goal-clarity: the instruction does not state the intended outcome. Evidence: user follow-up turns that redirect or redefine the goal after work started; discarded or redone agent output (rework turns, tokens spent on abandoned work).
        - ambiguity: the instruction admits multiple readings. Evidence: a rephrased or clarified instruction in a later user turn; agent clarifying-question turns; divergent exploration before the user disambiguates.
        - missing-acceptance-criteria: the instruction has no verifiable done-condition. Evidence: user correction turns after the agent declares completion; extra user-initiated verification turns.
        - task-size-split: the instruction bundles too much work for one run. Evidence: a long multi-goal trace with mid-trace error spans or retries; token totals concentrated in retried segments; follow-up turns re-scoping the work to a subset.
        - missing-context-constraints: the instruction omits environment facts or constraints the agent needed. Evidence: failed or retried tool calls, or error spans, that resolve only after a user turn supplies the missing information. (Distinguished from ambiguity by evidence type: execution failure resolved by supplied information, not instruction rephrasing.)
        Evidence grounding rules (v4): call get_instruction_evidence first. It returns deterministic, code-extracted evidence: error_spans, retry_chains, turn_tokens, user_instruction, conversation, and conversation_context. Each finding must ground its category in that output as follows:
        - missing-context-constraints: cite at least one error_spans or retry_chains entry by span id.
        - task-size-split: cite both a multi-goal user_instruction descriptor and a turn_tokens concentration (name the concentrated turns).
        - ambiguity: cite user rephrase evidence - conversation_context sibling metadata plus the corrective wording inside the analyzed trace or a bounded sibling trace.
        - goal-clarity and missing-acceptance-criteria: cite turn-level evidence of the analyzed trace (turn_tokens entries and/or spans you verified through the raw tools).
        Conversation scope rules: treat the analyzed trace as the anchor. Use conversation_context.traces[] only as a bounded window of supporting evidence. A sibling trace citation must include the sibling trace_id and relative_position and must explain how that previous or following sibling trace relates to the analyzed trace. Do not cite or imply evidence outside the bounded window. If the only proof would be outside the bounded window, state that the bounded evidence is insufficient instead of inferring from missing context. Do not copy sibling instruction descriptors verbatim beyond short factual references.
        - Escape hatch: a finding grounded outside the extractor output is allowed only with a raw-verified span id you explicitly checked through the raw tools in this session; state that raw-verified citation in the finding. Sibling raw-verified evidence must still belong to a trace_id emitted in conversation_context.traces[]. Discovery of evidence the extractor cannot see stays possible through this hatch.
        Report each finding with exactly these four parts:
        1. Category: exactly one taxonomy category id.
        2. Evidence citation: span id(s), turn number(s), and/or sibling trace_id(s) that exist in the analyzed trace or emitted conversation_context.traces[], with a short factual descriptor. Do not copy long raw bodies.
        3. Gap explanation: what the instruction lacked, tied to the cited evidence.
        4. Improved next-time instruction: a concrete rewrite the user could give next time.
        Rules: a finding without a citable evidence reference is forbidden. Citations must refer to spans/turns present in the analyzed trace or trace_id values present in the emitted bounded window. Zero findings is a valid result and must be stated explicitly.
        Output language rule: the entire final report - headings, findings, gap explanations, improved next-time instructions, and the assessment of non-applicable categories - must be written in Japanese. Do not write the report in Chinese or English. 最終レポート全体（見出し・所見・改善指示・非該当カテゴリの評価を含む）は必ず日本語で書くこと。
        Respond with the final markdown report only; do not narrate tool usage before it.
        """;

    /// <summary>
    /// Base analysis instruction (per-focus output block: D046 instruction
    /// diagnosis gets its taxonomy/contract block, every other focus keeps the
    /// generic findings line), plus — for drawer follow-ups (D045) — the prior
    /// Q&amp;A transcript block and the follow-up question. Internal for tests.
    /// </summary>
    internal static string BuildPrompt(MonitorAnalysisContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Analyze Local Monitor trace {context.TraceId} with focus {context.Focus.ToWireValue()}.");
        builder.AppendLine("Use the available tools to inspect the trace. For raw evidence, cite trace/span/raw-record ids instead of copying long raw bodies.");
        if (context.Focus == MonitorAnalysisFocus.InstructionDiagnosis)
        {
            builder.AppendLine(InstructionDiagnosisPromptBlock);
        }
        else
        {
            builder.AppendLine("Return concise findings, likely causes, and recommended next checks.");
        }

        if (context.History is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("Prior Q&A from this local analysis chat (context only; the transcript lives on the client):");
            foreach (var turn in context.History)
            {
                if (!string.IsNullOrWhiteSpace(turn.Question))
                {
                    builder.AppendLine($"Q: {turn.Question}");
                }

                if (!string.IsNullOrWhiteSpace(turn.Answer))
                {
                    builder.AppendLine($"A: {turn.Answer}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(context.Question))
        {
            builder.AppendLine();
            builder.AppendLine($"Follow-up question: {context.Question}");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool MatchesContext(MonitorAnalysisRun run, MonitorAnalysisContext context) =>
        string.Equals(run.TraceId, context.TraceId, StringComparison.Ordinal)
        && run.RawRecordId == context.RawRecordId
        && string.Equals(run.SpanId, context.SpanId, StringComparison.Ordinal)
        && run.Focus == context.Focus;

}

internal sealed class AnalysisOwnershipException : Exception
{
    public AnalysisOwnershipException() { }
    public AnalysisOwnershipException(Exception innerException) : base(null, innerException) { }
}

internal sealed class UnconfiguredDirectoryOwner : IAnalysisSdkDirectoryOwner
{
    public ValueTask<IAnalysisSdkDirectoryScope> OpenAsync(long runId, DateTimeOffset exactRequestedAt, string configuredParent, CancellationToken cancellationToken) =>
        ValueTask.FromException<IAnalysisSdkDirectoryScope>(new AnalysisOwnershipException());
}

internal sealed record CopilotAnalysisSettings(bool Enabled, string Model, int TimeoutSeconds, string BaseDirectory, ProviderConfig? Provider)
{
    public CopilotAnalysisExecutionSettings ToExecutionSettings() => new(Model, TimeoutSeconds, Provider);
    public static CopilotAnalysisSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("CopilotAnalysis");
        var enabled = !bool.TryParse(section["Enabled"], out var parsedEnabled) || parsedEnabled;
        var model = section["Model"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-5";
        }

        if (!int.TryParse(section["TimeoutSeconds"], out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            timeoutSeconds = 60;
        }

        var baseDirectory = section["BaseDirectory"];
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(
                Path.GetTempPath(),
                "CopilotAgentObservability",
                "LocalMonitor",
                "CopilotSdk");
        }

        var providerSection = section.GetSection("Provider");
        var type = providerSection["Type"];
        var baseUrl = providerSection["BaseUrl"];
        var apiKey = providerSection["ApiKey"];
        if (string.IsNullOrWhiteSpace(type)
            && string.IsNullOrWhiteSpace(baseUrl)
            && string.IsNullOrWhiteSpace(apiKey))
        {
            return new CopilotAnalysisSettings(enabled, model, timeoutSeconds, baseDirectory, Provider: null);
        }

        if (string.IsNullOrWhiteSpace(type)
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("CopilotAnalysis:Provider requires Type, BaseUrl, and ApiKey when any provider setting is present.");
        }

        var wireApi = providerSection["WireApi"];
        if (string.IsNullOrWhiteSpace(wireApi))
        {
            wireApi = "completions";
        }

        if (wireApi is not ("completions" or "responses"))
        {
            throw new InvalidOperationException("CopilotAnalysis:Provider:WireApi must be 'completions' or 'responses'.");
        }

        return new CopilotAnalysisSettings(
            enabled,
            model,
            timeoutSeconds,
            baseDirectory,
            new ProviderConfig
            {
                Type = type,
                BaseUrl = baseUrl.TrimEnd('/'),
                ApiKey = apiKey,
                WireApi = wireApi,
            });
    }
}

internal sealed class MonitorAnalysisToolData : IAsyncDisposable
{
    private readonly RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>? rawLease;

    private MonitorAnalysisToolData(
        object rawTrace,
        object? rawRecord,
        object? rawSpanContext,
        object? traceSummary,
        object traceSpanTree,
        object cacheSummary,
        InstructionEvidence instructionEvidence,
        RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>? rawLease)
    {
        RawTrace = rawTrace;
        RawRecord = rawRecord;
        RawSpanContext = rawSpanContext;
        TraceSummary = traceSummary;
        TraceSpanTree = traceSpanTree;
        CacheSummary = cacheSummary;
        InstructionEvidence = instructionEvidence;
        this.rawLease = rawLease;
    }

    public object RawTrace { get; }
    public object? RawRecord { get; }
    public object? RawSpanContext { get; }
    public object? TraceSummary { get; }
    public object TraceSpanTree { get; }
    public object CacheSummary { get; }
    public InstructionEvidence InstructionEvidence { get; }

    public ValueTask DisposeAsync() => rawLease?.DisposeAsync() ?? ValueTask.CompletedTask;

    private const int RawRecordLimit = 50;
    private const int ConversationContextSiblingRadius = 2;

    public static async ValueTask<MonitorAnalysisToolData> CreateAsync(
        IMonitorProjectionStore projectionStore,
        MonitorAnalysisContext context,
        CancellationToken cancellationToken)
    {
        var spans = projectionStore.GetSpansForTrace(context.TraceId);
        var selectedSpan = string.IsNullOrWhiteSpace(context.SpanId)
            ? null
            : spans.FirstOrDefault(row => string.Equals(row.SpanId, context.SpanId, StringComparison.Ordinal));
        var trace = projectionStore.GetMonitorTrace(context.TraceId);
        var turns = spans.Where(span => span.Operation == "chat" || span.Category == "llm_call").ToList();

        // Sprint20/Sprint21 (D047/D048): deterministic instruction-evidence
        // conversation context is scoped to instruction-diagnosis. Other
        // focuses keep their existing generic prompt path and do not load
        // sibling trace rows or raw records.
        var conversationId = context.Focus == MonitorAnalysisFocus.InstructionDiagnosis
            ? spans.Select(span => span.ConversationId).FirstOrDefault(id => !string.IsNullOrEmpty(id))
            : null;
        var conversationTraces = string.IsNullOrEmpty(conversationId)
            ? Array.Empty<MonitorConversationTraceRow>()
            : projectionStore.ListConversationTraces(conversationId);
        var conversationTraceInputs = BuildConversationTraceInputs(
            context.TraceId,
            spans,
            conversationTraces,
            projectionStore);
        var rawRecordIds = CollectRawRecordIds(
            spans,
            context.RawRecordId,
            selectedSpan,
            conversationTraceInputs);
        var rawResult = await projectionStore.ReadRawRecordsAsync(rawRecordIds, RetentionReadKind.Operation, cancellationToken);
        if (rawResult.Disposition == RetentionReadDisposition.Busy)
        {
            throw new PersistenceBusyException();
        }

        var rawLease = rawResult.Lease;
        try
        {
            var rawRecordsById = (rawLease?.Value ?? [])
                .Where(record => record.Id is not null)
                .ToDictionary(record => record.Id!.Value);
            var rawRecords = spans
                .Select(span => span.RawRecordId)
                .Distinct()
                .Order()
                .Take(RawRecordLimit)
                .Where(rawRecordsById.ContainsKey)
                .Select(id => rawRecordsById[id])
                .ToArray();
            var selectedRawRecord = context.RawRecordId is { } rawRecordId && rawRecordsById.TryGetValue(rawRecordId, out var selected)
                ? selected
                : null;
            var selectedSpanRawRecord = selectedSpan is not null && rawRecordsById.TryGetValue(selectedSpan.RawRecordId, out var selectedSpanRaw)
                ? selectedSpanRaw
                : null;
            var materializedConversationInputs = conversationTraceInputs
                .Select(input => input with
                {
                    RawRecords = input.Spans
                        .Select(span => span.RawRecordId)
                        .Distinct()
                        .Order()
                        .Take(RawRecordLimit)
                        .Where(rawRecordsById.ContainsKey)
                        .Select(id => rawRecordsById[id])
                        .ToArray(),
                })
                .ToArray();
            var instructionEvidence = InstructionEvidenceExtractor.Extract(
                context.TraceId,
                spans,
                rawRecords,
                conversationTraces,
                materializedConversationInputs);

            return new MonitorAnalysisToolData(
                new
                {
                    trace_id = context.TraceId,
                    raw_records = rawRecords.Select(record => new
                    {
                        raw_record_id = record.Id,
                        source = record.Source,
                        received_at = record.ReceivedAt,
                        payload_json = record.PayloadJson,
                    }),
                },
                selectedRawRecord is null ? null : new
                {
                    raw_record_id = selectedRawRecord.Id,
                    source = selectedRawRecord.Source,
                    trace_id = selectedRawRecord.TraceId,
                    received_at = selectedRawRecord.ReceivedAt,
                    payload_json = selectedRawRecord.PayloadJson,
                },
                selectedSpan is null ? null : new
                {
                    trace_id = context.TraceId,
                    span_id = selectedSpan.SpanId,
                    raw_record = selectedSpanRawRecord?.PayloadJson,
                },
                trace is null ? null : new
                {
                    trace_id = trace.TraceId,
                    span_count = trace.SpanCount,
                    tool_call_count = trace.ToolCallCount,
                    error_count = trace.ErrorCount,
                    duration_ms = trace.DurationMs,
                    input_tokens = trace.InputTokens,
                    output_tokens = trace.OutputTokens,
                    total_tokens = trace.TotalTokens,
                    primary_model = trace.PrimaryModel,
                },
                new
                {
                    trace_id = context.TraceId,
                    span_count = spans.Count,
                    spans = spans.Select(span => new
                    {
                        span_id = span.SpanId,
                        parent_span_id = span.ParentSpanId,
                        operation = span.Operation,
                        category = span.Category,
                        tool_name = span.ToolName,
                        status = span.Status,
                        duration_ms = span.DurationMs,
                    }),
                },
                new
                {
                    trace_id = context.TraceId,
                    turn_count = turns.Count,
                    totals = new
                    {
                        input_tokens = turns.Sum(span => span.InputTokens ?? 0),
                        output_tokens = turns.Sum(span => span.OutputTokens ?? 0),
                        total_tokens = turns.Sum(span => span.TotalTokens ?? 0),
                        cache_read_tokens = turns.Sum(span => span.CacheReadTokens ?? 0),
                        cache_creation_tokens = turns.Sum(span => span.CacheCreationTokens ?? 0),
                    },
                },
                instructionEvidence,
                rawLease);
        }
        catch
        {
            if (rawLease is not null)
            {
                await rawLease.DisposeAsync();
            }

            throw;
        }
    }

    private static IReadOnlyList<InstructionEvidenceConversationTraceInput> BuildConversationTraceInputs(
        string analyzedTraceId,
        IReadOnlyList<MonitorSpanRow> analyzedSpans,
        IReadOnlyList<MonitorConversationTraceRow> conversationTraces,
        IMonitorProjectionStore projectionStore)
    {
        if (conversationTraces.Count == 0)
        {
            return [];
        }

        var analyzedIndex = -1;
        for (var index = 0; index < conversationTraces.Count; index++)
        {
            if (string.Equals(conversationTraces[index].TraceId, analyzedTraceId, StringComparison.Ordinal))
            {
                analyzedIndex = index;
                break;
            }
        }

        if (analyzedIndex < 0)
        {
            return [];
        }

        var windowStart = Math.Max(0, analyzedIndex - ConversationContextSiblingRadius);
        var windowEnd = Math.Min(conversationTraces.Count - 1, analyzedIndex + ConversationContextSiblingRadius);
        var inputs = new List<InstructionEvidenceConversationTraceInput>();
        for (var index = windowStart; index <= windowEnd; index++)
        {
            var trace = conversationTraces[index];
            var isAnalyzedTrace = string.Equals(trace.TraceId, analyzedTraceId, StringComparison.Ordinal);
            var spans = isAnalyzedTrace
                ? analyzedSpans
                : projectionStore.GetSpansForTrace(trace.TraceId);
            inputs.Add(new InstructionEvidenceConversationTraceInput(
                TraceId: trace.TraceId,
                RelativePosition: index - analyzedIndex,
                IsAnalyzedTrace: isAnalyzedTrace,
                FirstStartTime: trace.FirstStartTime,
                Spans: spans,
                RawRecords: []));
        }

        return inputs;
    }

    private static IReadOnlyList<long> CollectRawRecordIds(
        IReadOnlyList<MonitorSpanRow> spans,
        long? selectedRawRecordId,
        MonitorSpanRow? selectedSpan,
        IReadOnlyList<InstructionEvidenceConversationTraceInput> conversationTraceInputs) =>
        spans.Select(span => span.RawRecordId)
            .Concat(selectedRawRecordId is { } rawRecordId ? [rawRecordId] : [])
            .Concat(selectedSpan is null ? [] : [selectedSpan.RawRecordId])
            .Concat(conversationTraceInputs.SelectMany(input => input.Spans.Select(span => span.RawRecordId)))
            .Distinct()
            .Order()
            .ToArray();
}

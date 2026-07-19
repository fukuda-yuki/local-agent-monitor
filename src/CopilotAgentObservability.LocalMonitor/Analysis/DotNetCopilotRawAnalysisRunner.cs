using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class DotNetCopilotRawAnalysisRunner : IMonitorAnalysisRunner
{
    private readonly IMonitorAnalysisStore analysisStore;
    private readonly IMonitorProjectionStore projectionStore;
    private readonly IConfiguration configuration;

    public DotNetCopilotRawAnalysisRunner(
        IMonitorAnalysisStore analysisStore,
        IMonitorProjectionStore projectionStore,
        IConfiguration configuration)
    {
        this.analysisStore = analysisStore;
        this.projectionStore = projectionStore;
        this.configuration = configuration;
    }

    public Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunAsync(context, CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(MonitorAnalysisContext context, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        analysisStore.MarkRunning(context.RunId, startedAt);
        analysisStore.AppendEvent(context.RunId, "running", ".NET GitHub Copilot SDK analysis started.", startedAt);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            analysisStore.AppendEvent(context.RunId, "sdk_phase", "loading_local_tool_data", DateTimeOffset.UtcNow);
            await using var data = await MonitorAnalysisToolData.CreateAsync(projectionStore, context, cancellationToken);
            var result = await RunCopilotSessionAsync(context, data, cancellationToken);
            analysisStore.CompleteRun(context.RunId, result, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            analysisStore.FinishRun(
                context.RunId,
                MonitorAnalysisStatus.Canceled,
                "Analysis was canceled.",
                DateTimeOffset.UtcNow);
        }
        catch (PersistenceBusyException)
        {
            analysisStore.FinishRun(
                context.RunId,
                MonitorAnalysisStatus.Failed,
                "The local monitor raw store is busy. Retry the analysis.",
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            var message = SanitizedExceptionMessage(exception, configuration["CopilotAnalysis:Provider:ApiKey"]);
            analysisStore.AppendEvent(context.RunId, "sdk_error", message, DateTimeOffset.UtcNow);
            analysisStore.FinishRun(
                context.RunId,
                MonitorAnalysisStatus.Failed,
                message,
                DateTimeOffset.UtcNow);
        }
    }

    private async Task<string> RunCopilotSessionAsync(
        MonitorAnalysisContext context,
        MonitorAnalysisToolData data,
        CancellationToken cancellationToken)
    {
        var settings = CopilotAnalysisSettings.From(configuration);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("CopilotAnalysis is disabled by local configuration.");
        }

        Directory.CreateDirectory(settings.BaseDirectory);
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            BaseDirectory = settings.BaseDirectory,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        });
        analysisStore.AppendEvent(context.RunId, "sdk_phase", "starting_client", DateTimeOffset.UtcNow);
        await client.StartAsync(cancellationToken);
        analysisStore.AppendEvent(context.RunId, "sdk_phase", "creating_session", DateTimeOffset.UtcNow);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = settings.Model,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Provider = settings.Provider,
            Tools =
            [
                DefineTool("get_raw_trace", "Return the raw trace records for this Local Monitor analysis run.", () => Serialize(data.RawTrace)),
                DefineTool("get_raw_record", "Return the selected raw record for this Local Monitor analysis run.", () => Serialize(data.RawRecord)),
                DefineTool("get_raw_span_context", "Return the selected raw span context for this Local Monitor analysis run.", () => Serialize(data.RawSpanContext)),
                DefineTool("get_trace_summary", "Return the sanitized trace summary for this Local Monitor analysis run.", () => Serialize(data.TraceSummary)),
                DefineTool("get_trace_span_tree", "Return the sanitized span tree for this Local Monitor analysis run.", () => Serialize(data.TraceSpanTree)),
                DefineTool("get_cache_summary", "Return the sanitized cache summary for this Local Monitor analysis run.", () => Serialize(data.CacheSummary)),
                DefineTool("get_instruction_evidence", "Return deterministic, code-extracted instruction evidence (error spans, retry chains, turn tokens, user instruction, conversation metadata, and bounded conversation_context) for this Local Monitor analysis run.", () => Serialize(data.InstructionEvidence)),
            ],
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = """
                    You are analyzing a local Copilot/agent observability trace.
                    You may inspect raw data through the provided tools. Your response is local runtime data and may mention raw-derived findings.
                    Do not claim the response is repository-safe. Repository-safe export is generated separately by Local Monitor.
                    """,
            },
        }, cancellationToken);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new StringBuilder();
        using var subscription = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    final.Append(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent message when final.Length == 0:
                    final.Append(message.Data.Content);
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent error:
                    done.TrySetException(new InvalidOperationException(error.Data.Message));
                    break;
            }
        });

        analysisStore.AppendEvent(context.RunId, "sdk_phase", "sending_message", DateTimeOffset.UtcNow);
        await session.SendAndWaitAsync(
            new MessageOptions { Prompt = BuildPrompt(context) },
            TimeSpan.FromSeconds(settings.TimeoutSeconds),
            cancellationToken);
        done.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        analysisStore.AppendEvent(context.RunId, "sdk_phase", "session_completed", DateTimeOffset.UtcNow);
        return final.Length == 0
            ? "Copilot SDK analysis completed without a textual result."
            : final.ToString();
    }

    private static AIFunction DefineTool(string name, string description, Func<string> tool) =>
        CopilotTool.DefineTool(
            ([Description("No input is required for this run-scoped Local Monitor tool.")] string? _ = null) => tool(),
            new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
            });

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

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        });

    private static string SanitizedExceptionMessage(Exception exception, string? apiKey)
    {
        var typeName = exception.GetType().Name;
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return $"{typeName}: SDK analysis failed.";
        }

        message = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message = message.Replace(apiKey, "[redacted-api-key]", StringComparison.Ordinal);
        }

        return message.Length > 500
            ? $"{typeName}: {message[..500]}..."
            : $"{typeName}: {message}";
    }
}

internal sealed record CopilotAnalysisSettings(bool Enabled, string Model, int TimeoutSeconds, string BaseDirectory, ProviderConfig? Provider)
{
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

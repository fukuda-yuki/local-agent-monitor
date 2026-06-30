using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Projection;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class DotNetCopilotRawAnalysisRunner : IMonitorAnalysisRunner
{
    private readonly IMonitorAnalysisStore analysisStore;
    private readonly IMonitorProjectionStore projectionStore;

    public DotNetCopilotRawAnalysisRunner(
        IMonitorAnalysisStore analysisStore,
        IMonitorProjectionStore projectionStore)
    {
        this.analysisStore = analysisStore;
        this.projectionStore = projectionStore;
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
            var data = MonitorAnalysisToolData.Create(projectionStore, context);
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
        catch (Exception)
        {
            analysisStore.FinishRun(
                context.RunId,
                MonitorAnalysisStatus.Failed,
                "The .NET Copilot SDK analysis runner failed before returning a result.",
                DateTimeOffset.UtcNow);
        }
    }

    private async Task<string> RunCopilotSessionAsync(
        MonitorAnalysisContext context,
        MonitorAnalysisToolData data,
        CancellationToken cancellationToken)
    {
        await using var client = new CopilotClient();
        await client.StartAsync(cancellationToken);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-5",
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools =
            [
                DefineTool("get_raw_trace", "Return the raw trace records for this Local Monitor analysis run.", () => Serialize(data.RawTrace)),
                DefineTool("get_raw_record", "Return the selected raw record for this Local Monitor analysis run.", () => Serialize(data.RawRecord)),
                DefineTool("get_raw_span_context", "Return the selected raw span context for this Local Monitor analysis run.", () => Serialize(data.RawSpanContext)),
                DefineTool("get_trace_summary", "Return the sanitized trace summary for this Local Monitor analysis run.", () => Serialize(data.TraceSummary)),
                DefineTool("get_trace_span_tree", "Return the sanitized span tree for this Local Monitor analysis run.", () => Serialize(data.TraceSpanTree)),
                DefineTool("get_cache_summary", "Return the sanitized cache summary for this Local Monitor analysis run.", () => Serialize(data.CacheSummary)),
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

        await session.SendAsync(new MessageOptions { Prompt = BuildPrompt(context) }, cancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
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

    private static string BuildPrompt(MonitorAnalysisContext context) =>
        $"""
        Analyze Local Monitor trace {context.TraceId} with focus {context.Focus.ToWireValue()}.
        Use the available tools to inspect the trace. For raw evidence, cite trace/span/raw-record ids instead of copying long raw bodies.
        Return concise findings, likely causes, and recommended next checks.
        """;

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        });
}

internal sealed record MonitorAnalysisToolData(
    object RawTrace,
    object? RawRecord,
    object? RawSpanContext,
    object? TraceSummary,
    object TraceSpanTree,
    object CacheSummary)
{
    public static MonitorAnalysisToolData Create(
        IMonitorProjectionStore projectionStore,
        MonitorAnalysisContext context)
    {
        var rawRecords = projectionStore.ListRawRecordsByTraceId(context.TraceId, limit: 50);
        var spans = projectionStore.GetSpansForTrace(context.TraceId);
        var selectedRawRecord = context.RawRecordId is { } rawRecordId
            ? projectionStore.GetRawRecordById(rawRecordId)
            : null;
        var selectedSpan = string.IsNullOrWhiteSpace(context.SpanId)
            ? null
            : spans.FirstOrDefault(row => string.Equals(row.SpanId, context.SpanId, StringComparison.Ordinal));
        var trace = projectionStore.GetMonitorTrace(context.TraceId);
        var turns = spans.Where(span => span.Operation == "chat" || span.Category == "llm_call").ToList();

        return new MonitorAnalysisToolData(
            RawTrace: new
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
            RawRecord: selectedRawRecord is null ? null : new
            {
                raw_record_id = selectedRawRecord.Id,
                source = selectedRawRecord.Source,
                trace_id = selectedRawRecord.TraceId,
                received_at = selectedRawRecord.ReceivedAt,
                payload_json = selectedRawRecord.PayloadJson,
            },
            RawSpanContext: selectedSpan is null ? null : new
            {
                trace_id = context.TraceId,
                span_id = selectedSpan.SpanId,
                raw_record = projectionStore.GetRawRecordById(selectedSpan.RawRecordId)?.PayloadJson,
            },
            TraceSummary: trace is null ? null : new
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
            TraceSpanTree: new
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
            CacheSummary: new
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
            });
    }
}

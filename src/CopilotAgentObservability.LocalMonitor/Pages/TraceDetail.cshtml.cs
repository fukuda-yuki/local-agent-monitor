using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Agent-execution view for one trace: Summary panel, sub-agent span tree, and
/// per-turn token rollup from the sanitized projection, plus the raw OTLP
/// payload(s) inline (escaped inert text). This is a raw-bearing route: it enforces
/// same-origin and <c>Cache-Control: no-store</c>, and under <c>--sanitized-only</c>
/// the whole page is removed (returns <c>404</c>). Sanitized span data stays
/// reachable via <c>/api/monitor/traces/{traceId}/spans</c>.
/// </summary>
public sealed class TraceDetailModel : PageModel
{
    private const int RawPreviewRecordLimit = 5;
    private const int RawPreviewCharLimit = 4096;

    public string TraceId { get; private set; } = string.Empty;

    internal MonitorTraceRow Trace { get; private set; } = null!;

    internal IReadOnlyList<SpanTreeNode> Tree { get; private set; } = Array.Empty<SpanTreeNode>();

    internal IReadOnlyList<MonitorSpanRow> Turns { get; private set; } = Array.Empty<MonitorSpanRow>();

    internal IReadOnlyList<RawRecordPreview> RawRecords { get; private set; } = Array.Empty<RawRecordPreview>();

    public IActionResult OnGet(string traceId)
    {
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();

        // Raw / PII must not be left in the browser cache after process exit or a
        // --sanitized-only restart.
        Response.Headers["Cache-Control"] = "no-store";

        // The trace-detail page is raw-bearing; --sanitized-only removes it.
        if (options.SanitizedOnly)
        {
            return NotFound();
        }

        // Same-origin only: a cross-site / foreign-origin browser read is refused
        // so another origin cannot use the user's browser to exfiltrate raw / PII.
        if (MonitorHost.IsCrossSiteRequest(HttpContext))
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentType = "application/json",
                Content = "{\"accepted\":false,\"error\":\"cross_origin_forbidden\",\"message\":\"The agent-execution view is same-origin only.\"}",
            };
        }

        TraceId = traceId;
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();

        MonitorTraceRow? trace;
        IReadOnlyList<MonitorSpanRow> spans;
        IReadOnlyList<RawTelemetryRecord> rawRecords;
        try
        {
            trace = store.GetMonitorTrace(traceId);
            if (trace is null)
            {
                return NotFound();
            }

            spans = store.GetSpansForTrace(traceId);
            rawRecords = store.ListRawRecordsByTraceId(traceId, RawPreviewRecordLimit);
        }
        catch (PersistenceBusyException)
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                ContentType = "application/json",
                Content = "{\"accepted\":false,\"error\":\"persistence_busy\",\"message\":\"The local monitor raw store is busy.\"}",
            };
        }

        Trace = trace;
        Tree = BuildTree(spans);
        Turns = spans
            .Where(span => string.Equals(span.Category, "llm_call", StringComparison.Ordinal)
                || string.Equals(span.Operation, "chat", StringComparison.Ordinal))
            .ToList();
        RawRecords = rawRecords.Select(ToPreview).ToList();

        return Page();
    }

    private static RawRecordPreview ToPreview(RawTelemetryRecord record)
    {
        var payload = record.PayloadJson;
        var truncated = payload.Length > RawPreviewCharLimit;
        return new RawRecordPreview(
            Id: record.Id!.Value,
            Preview: truncated ? payload[..RawPreviewCharLimit] : payload,
            PayloadLength: payload.Length,
            IsTruncated: truncated);
    }

    /// <summary>
    /// Flattens the spans into a depth-annotated pre-order list using
    /// <c>parent_span_id</c>. A span whose parent id is null or not present in the
    /// set is treated as a root, so the page stays functional on the
    /// M6-unconfirmed hierarchy (the parent-absent fallback).
    /// </summary>
    private static IReadOnlyList<SpanTreeNode> BuildTree(IReadOnlyList<MonitorSpanRow> spans)
    {
        var bySpanId = new Dictionary<string, MonitorSpanRow>(StringComparer.Ordinal);
        foreach (var span in spans)
        {
            if (span.SpanId is { Length: > 0 })
            {
                bySpanId.TryAdd(span.SpanId, span);
            }
        }

        var children = new Dictionary<string, List<MonitorSpanRow>>(StringComparer.Ordinal);
        var roots = new List<MonitorSpanRow>();
        foreach (var span in spans)
        {
            if (span.ParentSpanId is { Length: > 0 } parentId && bySpanId.ContainsKey(parentId))
            {
                if (!children.TryGetValue(parentId, out var list))
                {
                    list = new List<MonitorSpanRow>();
                    children[parentId] = list;
                }

                list.Add(span);
            }
            else
            {
                roots.Add(span);
            }
        }

        static int Order(MonitorSpanRow a, MonitorSpanRow b)
        {
            var byOrdinal = a.SpanOrdinal.CompareTo(b.SpanOrdinal);
            return byOrdinal != 0 ? byOrdinal : a.Id.CompareTo(b.Id);
        }

        roots.Sort(Order);
        foreach (var list in children.Values)
        {
            list.Sort(Order);
        }

        var nodes = new List<SpanTreeNode>(spans.Count);
        var visited = new HashSet<long>();

        void Walk(MonitorSpanRow span, int depth)
        {
            if (!visited.Add(span.Id))
            {
                return; // Defensive: ignore cycles / duplicate span ids.
            }

            nodes.Add(new SpanTreeNode(span, depth));
            if (span.SpanId is { Length: > 0 } spanId && children.TryGetValue(spanId, out var kids))
            {
                foreach (var kid in kids)
                {
                    Walk(kid, depth + 1);
                }
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return nodes;
    }
}

internal sealed record SpanTreeNode(MonitorSpanRow Span, int Depth);

internal sealed record RawRecordPreview(long Id, string Preview, int PayloadLength, bool IsTruncated);

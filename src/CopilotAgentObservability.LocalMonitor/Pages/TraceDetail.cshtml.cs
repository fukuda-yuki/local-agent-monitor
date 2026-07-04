using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Trace detail (Sprint18 §6.3, D042): breadcrumb + prev/next, title with
/// status pill and meta line, a token-total card, and a 2-column body — the
/// 実行の流れ card (flow | waterfall segment toggle rendered client-side from
/// the sanitized spans API by monitor-flow.js / monitor-waterfall.js) and the
/// cache column (monitor-cache-panel.js). The span inspector, error-analysis
/// mode, and Copilot drawer are in-page states, not routes. By default the page
/// also renders raw OTLP payload previews inline (escaped inert text). Under
/// <c>--sanitized-only</c>, the sanitized shell remains available and the raw
/// section is omitted.
/// </summary>
public sealed class TraceDetailModel : PageModel
{
    private const int RawPreviewRecordLimit = 5;
    private const int RawPreviewCharLimit = 4096;
    private const int NeighborWindow = 500;

    public string TraceId { get; private set; } = string.Empty;

    internal MonitorTraceRow Trace { get; private set; } = null!;

    internal bool RawAvailable { get; private set; }

    /// <summary>Adjacent trace ids in last-seen-DESC order (前 = newer, 次 = older); null at the ends.</summary>
    internal string? PrevTraceId { get; private set; }

    internal string? NextTraceId { get; private set; }

    /// <summary>
    /// Representative user prompt for the breadcrumb / page title, extracted
    /// server-side from this trace's raw OTLP payload (D032). Null under
    /// <c>--sanitized-only</c> or when no prompt is present, in which case the view
    /// falls back to a shortened TraceId.
    /// </summary>
    internal string? PromptLabel { get; private set; }

    internal IReadOnlyList<RawRecordPreview> RawRecords { get; private set; } = Array.Empty<RawRecordPreview>();

    public IActionResult OnGet(string traceId)
    {
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        RawAvailable = !options.SanitizedOnly;

        // Raw / PII must not be left in the browser cache after process exit or a
        // --sanitized-only restart.
        Response.Headers["Cache-Control"] = "no-store";

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
        IReadOnlyList<RawTelemetryRecord> rawRecords = Array.Empty<RawTelemetryRecord>();
        try
        {
            trace = store.GetMonitorTrace(traceId);
            if (trace is null)
            {
                return NotFound();
            }

            if (RawAvailable)
            {
                rawRecords = store.ListRawRecordsByTraceId(traceId, RawPreviewRecordLimit);
            }

            // 前 / 次 navigation over the recent-first ordering (§6.3 breadcrumb).
            var recent = store.ListRecentMonitorTraces(NeighborWindow);
            for (var i = 0; i < recent.Count; i++)
            {
                if (string.Equals(recent[i].TraceId, traceId, StringComparison.Ordinal))
                {
                    PrevTraceId = i > 0 ? recent[i - 1].TraceId : null;
                    NextTraceId = i + 1 < recent.Count ? recent[i + 1].TraceId : null;
                    break;
                }
            }
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
        RawRecords = RawAvailable ? rawRecords.Select(ToPreview).ToList() : Array.Empty<RawRecordPreview>();
        PromptLabel = RawAvailable
            ? rawRecords
                .Select(record => MonitorPromptExtractor.ExtractPromptLabel(record.PayloadJson, traceId))
                .FirstOrDefault(prompt => prompt is not null)
            : null;

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

}

internal sealed record RawRecordPreview(long Id, string Preview, int PayloadLength, bool IsTruncated);

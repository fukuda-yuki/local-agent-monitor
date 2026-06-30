using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Agent-execution view for one trace: a server-rendered Summary panel and JS
/// Timeline / Flow / Cache tab containers backed by the sanitized spans API. By
/// default it also renders raw OTLP payload previews inline (escaped inert text).
/// Under <c>--sanitized-only</c>, the sanitized shell remains available and the
/// raw section is omitted.
/// </summary>
public sealed class TraceDetailModel : PageModel
{
    private const int RawPreviewRecordLimit = 5;
    private const int RawPreviewCharLimit = 4096;

    public string TraceId { get; private set; } = string.Empty;

    internal MonitorTraceRow Trace { get; private set; } = null!;

    internal bool RawAvailable { get; private set; }

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

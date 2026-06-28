using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Agent-execution view for one trace: a server-rendered Summary panel, JS
/// Timeline / Flow / Cache tab containers backed by the sanitized spans API, and
/// the raw OTLP payload(s) inline (escaped inert text). This is a raw-bearing
/// route: it enforces same-origin and <c>Cache-Control: no-store</c>, and under
/// <c>--sanitized-only</c> the whole page is removed (returns <c>404</c>).
/// Sanitized span data stays reachable via
/// <c>/api/monitor/traces/{traceId}/spans</c>.
/// </summary>
public sealed class TraceDetailModel : PageModel
{
    private const int RawPreviewRecordLimit = 5;
    private const int RawPreviewCharLimit = 4096;

    public string TraceId { get; private set; } = string.Empty;

    internal MonitorTraceRow Trace { get; private set; } = null!;

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
        IReadOnlyList<RawTelemetryRecord> rawRecords;
        try
        {
            trace = store.GetMonitorTrace(traceId);
            if (trace is null)
            {
                return NotFound();
            }

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

}

internal sealed record RawRecordPreview(long Id, string Preview, int PayloadLength, bool IsTruncated);

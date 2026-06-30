using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Trace list. When raw is available it labels each trace with the user prompt
/// extracted server-side from the raw OTLP payload (D032), which makes the page a
/// raw-bearing surface: it then enforces same-origin and <c>no-store</c>. Under
/// <c>--sanitized-only</c> no prompt is shown and a shortened TraceId is used.
/// </summary>
public sealed class TracesModel : PageModel
{
    private const int PageSize = 50;

    private readonly Dictionary<string, string?> promptByTraceId = new(StringComparer.Ordinal);

    [BindProperty(SupportsGet = true)]
    public long After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ClientKind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TraceId { get; set; }

    internal MonitorProjectionPage<MonitorTraceRow> Result { get; private set; } = null!;

    internal bool RawAvailable { get; private set; }

    public IActionResult OnGet()
    {
        if (After < 0)
        {
            return BadRequest();
        }

        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();

        RawAvailable = !options.SanitizedOnly;
        if (RawAvailable)
        {
            // The trace list becomes a raw-bearing surface when it shows prompt
            // labels (D032): keep raw out of the browser cache and refuse a
            // cross-site / foreign-origin browser read.
            Response.Headers["Cache-Control"] = "no-store";
            if (MonitorHost.IsCrossSiteRequest(HttpContext))
            {
                return CrossOriginForbidden();
            }
        }

        try
        {
            Result = store.ListMonitorTraces(After, PageSize);
            if (RawAvailable)
            {
                PopulatePrompts(store);
            }
        }
        catch (PersistenceBusyException)
        {
            return PersistenceBusy();
        }

        return Page();
    }

    /// <summary>Prompt label for a trace, or null to fall back to a shortened TraceId.</summary>
    internal string? PromptFor(string? traceId) =>
        traceId is not null && promptByTraceId.TryGetValue(traceId, out var prompt) ? prompt : null;

    private void PopulatePrompts(IMonitorProjectionStore store)
    {
        foreach (var row in Result.Items)
        {
            if (string.IsNullOrEmpty(row.TraceId) || promptByTraceId.ContainsKey(row.TraceId))
            {
                continue;
            }

            string? prompt = null;
            var records = store.ListRawRecordsByTraceId(row.TraceId, 1);
            if (records.Count > 0)
            {
                prompt = MonitorPromptExtractor.ExtractPromptLabel(records[0].PayloadJson, row.TraceId);
            }

            promptByTraceId[row.TraceId] = prompt;
        }
    }

    private static ContentResult CrossOriginForbidden() => new()
    {
        StatusCode = StatusCodes.Status403Forbidden,
        ContentType = "application/json",
        Content = "{\"accepted\":false,\"error\":\"cross_origin_forbidden\",\"message\":\"The trace list is same-origin only.\"}",
    };

    private static ContentResult PersistenceBusy() => new()
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
        ContentType = "application/json",
        Content = "{\"accepted\":false,\"error\":\"persistence_busy\",\"message\":\"The local monitor raw store is busy.\"}",
    };
}

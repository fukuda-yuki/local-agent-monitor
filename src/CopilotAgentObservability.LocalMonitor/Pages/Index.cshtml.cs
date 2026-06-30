using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Dashboard: readiness, highlight cards (latest / top-token / has-error), and the
/// recent ingestion list (folded in from the retired /ingestions page). When raw is
/// available it labels each trace with the user prompt extracted server-side from
/// the raw OTLP payload (D032), which makes the page a raw-bearing surface: it then
/// enforces same-origin and <c>no-store</c>. Under <c>--sanitized-only</c> no prompt
/// is shown and a shortened TraceId is used instead.
/// </summary>
public sealed class IndexModel : PageModel
{
    private const int RecentLimit = 10;

    private readonly Dictionary<string, string?> promptByTraceId = new(StringComparer.Ordinal);

    internal MonitorReadiness Readiness { get; private set; } = null!;

    internal IReadOnlyList<MonitorIngestionRow> RecentIngestions { get; private set; } = [];

    internal IReadOnlyList<MonitorTraceRow> RecentTraces { get; private set; } = [];

    internal bool RawAvailable { get; private set; }

    internal MonitorTraceRow? LatestTrace { get; private set; }

    internal MonitorTraceRow? TopTokenTrace { get; private set; }

    internal MonitorTraceRow? ErrorTrace { get; private set; }

    public IActionResult OnGet()
    {
        var health = HttpContext.RequestServices.GetRequiredService<MonitorHealthState>();
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();

        RawAvailable = !options.SanitizedOnly;
        if (RawAvailable)
        {
            // The dashboard becomes a raw-bearing surface when it shows prompt
            // labels (D032): keep raw out of the browser cache and refuse a
            // cross-site / foreign-origin browser read.
            Response.Headers["Cache-Control"] = "no-store";
            if (MonitorHost.IsCrossSiteRequest(HttpContext))
            {
                return CrossOriginForbidden();
            }
        }

        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);

        try
        {
            RecentIngestions = store.ListMonitorIngestions(0, RecentLimit).Items;
            RecentTraces = store.ListMonitorTraces(0, RecentLimit).Items;
        }
        catch (PersistenceBusyException)
        {
            return PersistenceBusy();
        }

        // Highlight cards: derived server-side from the same bounded window via the
        // shared summary service (also used by GET /api/monitor/summary, D037 child B).
        var summaryService = HttpContext.RequestServices.GetRequiredService<MonitorSummaryService>();
        var summary = summaryService.BuildSummary(RecentLimit);
        LatestTrace = summary.LatestTrace;
        TopTokenTrace = summary.TopTokenTrace;
        ErrorTrace = summary.ErrorTrace;

        if (RawAvailable)
        {
            PopulatePrompts(store);
        }

        return Page();
    }

    /// <summary>Prompt label for a trace, or null to fall back to a shortened TraceId.</summary>
    internal string? PromptFor(string? traceId) =>
        traceId is not null && promptByTraceId.TryGetValue(traceId, out var prompt) ? prompt : null;

    private void PopulatePrompts(IMonitorProjectionStore store)
    {
        var traceIds = RecentTraces
            .Select(trace => trace.TraceId)
            .Concat(RecentIngestions.Select(ingestion => ingestion.TraceId))
            .Where(traceId => !string.IsNullOrEmpty(traceId))
            .Select(traceId => traceId!)
            .Distinct(StringComparer.Ordinal);

        foreach (var traceId in traceIds)
        {
            string? prompt = null;
            try
            {
                var records = store.ListRawRecordsByTraceId(traceId, 1);
                if (records.Count > 0)
                {
                    prompt = MonitorPromptExtractor.ExtractPromptLabel(records[0].PayloadJson, traceId);
                }
            }
            catch (PersistenceBusyException)
            {
                // Best-effort label only; on a busy store the view falls back to the TraceId.
            }

            promptByTraceId[traceId] = prompt;
        }
    }

    private static ContentResult CrossOriginForbidden() => new()
    {
        StatusCode = StatusCodes.Status403Forbidden,
        ContentType = "application/json",
        Content = "{\"accepted\":false,\"error\":\"cross_origin_forbidden\",\"message\":\"The dashboard is same-origin only.\"}",
    };

    private static ContentResult PersistenceBusy() => new()
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
        ContentType = "application/json",
        Content = "{\"accepted\":false,\"error\":\"persistence_busy\",\"message\":\"The local monitor raw store is busy.\"}",
    };
}

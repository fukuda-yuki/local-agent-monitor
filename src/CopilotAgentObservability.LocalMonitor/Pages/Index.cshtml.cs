using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Overview dashboard (Sprint18 §6.1, D042): token-cost KPIs, per-model
/// breakdown, cache efficiency, top-token TOP5, hourly distribution, and the 5
/// most recent traces. The initial "today" aggregates are server-rendered from
/// <see cref="MonitorOverviewService"/>; monitor-overview.js refreshes them from
/// the sanitized `GET /api/monitor/overview` when the period toggle changes.
/// When raw is available the TOP5 / recent rows are labelled with the user
/// prompt extracted server-side (D032), which makes the page a raw-bearing
/// surface: it then enforces same-origin and <c>no-store</c>. Under
/// <c>--sanitized-only</c> no prompt is shown and a shortened TraceId is used.
/// </summary>
public sealed class IndexModel : PageModel
{
    private const int TopTraceLimit = 5;
    private const int RecentTraceLimit = 5;

    private readonly Dictionary<string, string?> promptByTraceId = new(StringComparer.Ordinal);

    internal MonitorOverview Overview { get; private set; } = null!;

    internal IReadOnlyList<MonitorTraceRow> TopTraces { get; private set; } = [];

    internal IReadOnlyList<MonitorTraceRow> RecentTraces { get; private set; } = [];

    internal bool RawAvailable { get; private set; }

    public IActionResult OnGet()
    {
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        var overviewService = HttpContext.RequestServices.GetRequiredService<MonitorOverviewService>();

        RawAvailable = !options.SanitizedOnly;
        if (RawAvailable)
        {
            // The overview becomes a raw-bearing surface when it shows prompt
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
            Overview = overviewService.BuildOverview("today");
            var range = overviewService.ResolvePeriod("today");
            TopTraces = store.ListTopTokenTraces(
                MonitorOverviewService.FormatUtc(range.Start),
                MonitorOverviewService.FormatUtc(range.End),
                TopTraceLimit);
            RecentTraces = store.ListRecentMonitorTraces(RecentTraceLimit);
        }
        catch (PersistenceBusyException)
        {
            return PersistenceBusy();
        }

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
        var traceIds = TopTraces
            .Select(trace => trace.TraceId)
            .Concat(RecentTraces.Select(trace => trace.TraceId))
            .Where(traceId => !string.IsNullOrEmpty(traceId))
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
        Content = "{\"accepted\":false,\"error\":\"cross_origin_forbidden\",\"message\":\"The overview is same-origin only.\"}",
    };

    private static ContentResult PersistenceBusy() => new()
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
        ContentType = "application/json",
        Content = "{\"accepted\":false,\"error\":\"persistence_busy\",\"message\":\"The local monitor raw store is busy.\"}",
    };
}

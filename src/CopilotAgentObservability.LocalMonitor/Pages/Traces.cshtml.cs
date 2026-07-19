using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Trace list master-detail (Sprint18 §6.2, D042 C4): toolbar filters, a
/// token-sorted table, offset paging, and a selection-driven preview panel.
/// The initial page is server-rendered through the same filtered query the
/// sanitized `GET /api/monitor/trace-list` endpoint uses; monitor-tracelist.js
/// refetches on filter changes and appends further pages. When raw is available
/// each row is labelled with the user prompt extracted server-side (D032),
/// which makes the page a raw-bearing surface: it then enforces same-origin and
/// <c>no-store</c>. Under <c>--sanitized-only</c> no prompt is shown and a
/// shortened TraceId is used.
/// </summary>
public sealed class TracesModel : PageModel
{
    private const int PageSize = 50;

    private readonly Dictionary<string, string?> promptByTraceId = new(StringComparer.Ordinal);

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Model { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Period { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    internal MonitorTraceListPage Result { get; private set; } = null!;

    internal IReadOnlyList<string> ModelOptions { get; private set; } = [];

    internal bool RawAvailable { get; private set; }

    internal string EffectivePeriod { get; private set; } = "today";

    internal string EffectiveSort { get; private set; } = "tokens";

    public async Task<IActionResult> OnGetAsync()
    {
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        var overviewService = HttpContext.RequestServices.GetRequiredService<MonitorOverviewService>();

        EffectivePeriod = string.IsNullOrEmpty(Period) ? "today" : Period;
        if (EffectivePeriod != "all" && !MonitorOverviewService.IsSupportedPeriod(EffectivePeriod))
        {
            return BadRequest();
        }

        EffectiveSort = string.IsNullOrEmpty(Sort) ? "tokens" : Sort;
        if (EffectiveSort is not ("tokens" or "time" or "duration"))
        {
            return BadRequest();
        }

        if (!string.IsNullOrEmpty(Status)
            && Status is not ("ok" or "recovered" or "unrecovered" or "unknown" or "error"))
        {
            return BadRequest();
        }

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

        string? startInclusive = null;
        string? endExclusive = null;
        if (EffectivePeriod != "all")
        {
            var range = overviewService.ResolvePeriod(EffectivePeriod);
            startInclusive = MonitorOverviewService.FormatUtc(range.Start);
            endExclusive = MonitorOverviewService.FormatUtc(range.End);
        }

        try
        {
            Result = store.ListMonitorTracesFiltered(new MonitorTraceListQuery(
                TraceIdSearch: string.IsNullOrWhiteSpace(Q) ? null : Q.Trim(),
                Model: string.IsNullOrWhiteSpace(Model) ? null : Model,
                Status: string.IsNullOrEmpty(Status) ? null : Status,
                StartInclusive: startInclusive,
                EndExclusive: endExclusive,
                Sort: EffectiveSort,
                Offset: 0,
                Limit: PageSize));

            // Model filter options come from the all-time per-model aggregate so a
            // filtered page still offers every known model.
            ModelOptions = store
                .GetPerModelPeriodSummary(
                    MonitorOverviewService.FormatUtc(DateTimeOffset.UnixEpoch),
                    MonitorOverviewService.FormatUtc(DateTimeOffset.UtcNow.AddDays(2)))
                .Select(model => model.Model)
                .Where(model => !string.IsNullOrEmpty(model))
                .Select(model => model!)
                .ToList();

            if (RawAvailable)
            {
                await PopulatePromptsAsync(store, HttpContext.RequestAborted);
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

    private async Task PopulatePromptsAsync(IMonitorProjectionStore store, CancellationToken cancellationToken)
    {
        foreach (var row in Result.Items)
        {
            if (string.IsNullOrEmpty(row.TraceId) || promptByTraceId.ContainsKey(row.TraceId))
            {
                continue;
            }

            var result = await store.ListRawRecordsByTraceIdAsync(row.TraceId, MonitorPromptExtractor.RecordScanLimit, RetentionReadKind.Access, cancellationToken);
            if (result.Disposition == RetentionReadDisposition.Busy) throw new PersistenceBusyException();
            if (result.Lease is null) { promptByTraceId[row.TraceId] = null; continue; }
            await using var lease = result.Lease;
            promptByTraceId[row.TraceId] = MonitorPromptExtractor.ExtractFirstPromptLabel(lease.Value.Select(record => record.PayloadJson), row.TraceId);
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

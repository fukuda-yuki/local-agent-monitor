namespace CopilotAgentObservability.LocalMonitor.Projection;

/// <summary>
/// Sanitized dashboard summary computed in-memory over the same bounded
/// <c>limit</c>-row window <see cref="IMonitorProjectionStore.ListMonitorTraces"/>
/// already returns to <c>/api/monitor/traces</c> (D037 child B). No new SQL
/// surface and no field outside the existing <see cref="MonitorTraceRow"/>
/// allowlist.
/// </summary>
internal sealed record MonitorSummary(
    int Limit,
    int TraceCount,
    MonitorTraceRow? LatestTrace,
    MonitorTraceRow? TopTokenTrace,
    MonitorTraceRow? ErrorTrace,
    IReadOnlyList<MonitorModelSummary> PerModelSummary,
    IReadOnlyList<MonitorClientKindSummary> PerClientKindSummary);

/// <summary>Per-model aggregate over the summary window. <c>TotalTokens</c> is a sum of <c>TotalTokens ?? 0</c>; <c>ErrorCount</c> is a sum of <c>ErrorCount ?? 0</c> (bounded by the row window, max 200, so an <c>int</c> sum cannot overflow).</summary>
internal sealed record MonitorModelSummary(string Model, int TraceCount, long TotalTokens, int ErrorCount);

/// <summary>Per-client-kind aggregate over the summary window. <c>TotalTokens</c> is a sum of <c>TotalTokens ?? 0</c>; <c>ErrorCount</c> is a sum of <c>ErrorCount ?? 0</c> (bounded by the row window, max 200, so an <c>int</c> sum cannot overflow).</summary>
internal sealed record MonitorClientKindSummary(string ClientKind, int TraceCount, long TotalTokens, int ErrorCount);

/// <summary>
/// Builds <see cref="MonitorSummary"/> for the Razor <c>Index</c> page and the
/// <c>GET /api/monitor/summary</c> route (D037 child B). Both consumers share this
/// service so the dashboard highlight cards and the API response stay consistent.
/// </summary>
internal sealed class MonitorSummaryService
{
    private readonly IMonitorProjectionStore store;

    public MonitorSummaryService(IMonitorProjectionStore store)
    {
        this.store = store;
    }

    public MonitorSummary BuildSummary(int limit)
    {
        var page = store.ListMonitorTraces(0, limit);
        var traces = page.Items;

        var latestTrace = traces.Count > 0 ? traces[0] : null;
        var topTokenTrace = traces
            .Where(trace => (trace.TotalTokens ?? 0) > 0)
            .OrderByDescending(trace => trace.TotalTokens ?? 0)
            .FirstOrDefault();
        var errorTrace = traces.FirstOrDefault(trace => (trace.ErrorCount ?? 0) > 0);

        var perModelSummary = traces
            .GroupBy(trace => trace.PrimaryModel ?? "unknown", StringComparer.Ordinal)
            .Select(group => new MonitorModelSummary(
                group.Key,
                group.Count(),
                group.Sum(trace => (long)(trace.TotalTokens ?? 0)),
                group.Sum(trace => trace.ErrorCount ?? 0)))
            .OrderByDescending(summary => summary.TraceCount)
            .ThenByDescending(summary => summary.TotalTokens)
            .ToList();

        var perClientKindSummary = traces
            .GroupBy(trace => trace.ClientKind ?? "unknown", StringComparer.Ordinal)
            .Select(group => new MonitorClientKindSummary(
                group.Key,
                group.Count(),
                group.Sum(trace => (long)(trace.TotalTokens ?? 0)),
                group.Sum(trace => trace.ErrorCount ?? 0)))
            .OrderByDescending(summary => summary.TraceCount)
            .ThenByDescending(summary => summary.TotalTokens)
            .ToList();

        return new MonitorSummary(
            limit,
            traces.Count,
            latestTrace,
            topTokenTrace,
            errorTrace,
            perModelSummary,
            perClientKindSummary);
    }
}

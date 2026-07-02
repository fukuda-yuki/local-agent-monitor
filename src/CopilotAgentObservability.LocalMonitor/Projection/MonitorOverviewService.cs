using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Projection;

/// <summary>
/// A resolved overview period: local-calendar window [Start, End) plus the
/// immediately preceding window of the same length for the comparison KPI.
/// </summary>
internal sealed record MonitorOverviewPeriod(
    string Period,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset PreviousStart);

/// <summary>Sanitized overview aggregate for one period (Sprint18 §6.1, D044).</summary>
internal sealed record MonitorOverview(
    MonitorOverviewPeriod Period,
    MonitorOverviewKpi Kpi,
    IReadOnlyList<MonitorOverviewModel> PerModel,
    IReadOnlyList<MonitorOverviewHour> HourlyTokens);

/// <summary>
/// KPI block. EffectiveInputTokens = cache_read × 0.1 + (input − cache_read).
/// Cache percentages use the cache-aware input sum only, so pre-v4 rows with
/// NULL cache columns never skew the rates (D044); they are null when no row in
/// the window carries cache data.
/// </summary>
internal sealed record MonitorOverviewKpi(
    long TokensTotal,
    long TokensPreviousPeriod,
    double? TokensChangePct,
    long EffectiveInputTokens,
    double? CacheCompressionPct,
    double? CacheReadRatePct,
    int ErrorTraceCount,
    int TraceCount);

internal sealed record MonitorOverviewModel(
    string Model,
    int TraceCount,
    int ErrorTraceCount,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    double? CacheReadRatePct);

internal sealed record MonitorOverviewHour(
    int Hour,
    long TotalTokens);

/// <summary>
/// Builds the sanitized `/api/monitor/overview` aggregate (mirrors the
/// <see cref="MonitorSummaryService"/> DI pattern). Periods are local calendar
/// windows: today = local midnight → tomorrow midnight; 7d / 30d = the N local
/// days ending tomorrow midnight. Hour buckets are local hours-of-day.
/// </summary>
internal sealed class MonitorOverviewService
{
    private readonly IMonitorProjectionStore store;
    private readonly TimeProvider timeProvider;

    public MonitorOverviewService(IMonitorProjectionStore store, TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool IsSupportedPeriod(string period) =>
        period is "today" or "7d" or "30d";

    public MonitorOverview BuildOverview(string period)
    {
        var resolved = ResolvePeriod(period);
        var start = FormatUtc(resolved.Start);
        var end = FormatUtc(resolved.End);
        var previousStart = FormatUtc(resolved.PreviousStart);

        var current = store.GetPeriodSummary(start, end);
        var previous = store.GetPeriodSummary(previousStart, start);
        var perModel = store.GetPerModelPeriodSummary(start, end);
        var hourly = store.GetHourlyTokenDistribution(start, end);

        return new MonitorOverview(
            resolved,
            BuildKpi(current, previous),
            perModel.Select(BuildModel).ToList(),
            BuildLocalHours(hourly));
    }

    public MonitorOverviewPeriod ResolvePeriod(string period)
    {
        var localZone = timeProvider.LocalTimeZone;
        var nowLocal = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), localZone);
        var todayMidnight = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var end = todayMidnight.AddDays(1);
        var days = period switch
        {
            "7d" => 7,
            "30d" => 30,
            _ => 1,
        };
        var start = end.AddDays(-days);
        return new MonitorOverviewPeriod(period, start, end, start.AddDays(-days));
    }

    private static MonitorOverviewKpi BuildKpi(MonitorPeriodSummaryRow current, MonitorPeriodSummaryRow previous)
    {
        var effectiveInput = current.InputTokens - current.CacheReadTokens
            + (long)Math.Round(current.CacheReadTokens * 0.1);

        double? changePct = previous.TotalTokens > 0
            ? Math.Round((current.TotalTokens - previous.TotalTokens) * 100.0 / previous.TotalTokens, 1)
            : null;

        return new MonitorOverviewKpi(
            TokensTotal: current.TotalTokens,
            TokensPreviousPeriod: previous.TotalTokens,
            TokensChangePct: changePct,
            EffectiveInputTokens: effectiveInput,
            CacheCompressionPct: CachePct(current.CacheReadTokens * 0.9, current.CacheAwareInputTokens),
            CacheReadRatePct: CachePct(current.CacheReadTokens, current.CacheAwareInputTokens),
            ErrorTraceCount: current.ErrorTraceCount,
            TraceCount: current.TraceCount);
    }

    private static MonitorOverviewModel BuildModel(MonitorModelPeriodSummaryRow row) => new(
        Model: row.Model ?? "unknown",
        TraceCount: row.TraceCount,
        ErrorTraceCount: row.ErrorTraceCount,
        TotalTokens: row.TotalTokens,
        InputTokens: row.InputTokens,
        OutputTokens: row.OutputTokens,
        CacheReadTokens: row.CacheReadTokens,
        CacheCreationTokens: row.CacheCreationTokens,
        CacheReadRatePct: CachePct(row.CacheReadTokens, row.CacheAwareInputTokens));

    private static double? CachePct(double cachePortion, long cacheAwareInput) =>
        cacheAwareInput > 0 ? Math.Round(cachePortion * 100.0 / cacheAwareInput, 1) : null;

    /// <summary>
    /// Shifts the store's UTC hour buckets into local hours-of-day using the
    /// current local offset (whole hours; a local single-user tool, so the
    /// server-local zone is the user's zone).
    /// </summary>
    private IReadOnlyList<MonitorOverviewHour> BuildLocalHours(IReadOnlyList<MonitorHourlyTokensRow> utcRows)
    {
        var offsetHours = (int)Math.Round(
            timeProvider.LocalTimeZone.GetUtcOffset(timeProvider.GetUtcNow()).TotalHours);
        var buckets = new long[24];
        foreach (var row in utcRows)
        {
            var localHour = ((row.UtcHour + offsetHours) % 24 + 24) % 24;
            buckets[localHour] += row.TotalTokens;
        }

        return Enumerable.Range(0, 24)
            .Select(hour => new MonitorOverviewHour(hour, buckets[hour]))
            .ToList();
    }

    internal static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

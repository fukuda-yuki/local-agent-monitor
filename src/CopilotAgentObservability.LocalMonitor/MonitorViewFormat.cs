namespace CopilotAgentObservability.LocalMonitor;

/// <summary>
/// Server-side display formatting for the monitor's Razor pages: digit-grouped
/// token counts, shortened trace ids, relative timestamps, and human durations.
/// Presentation only — operates on already-sanitized projection fields. Parse
/// failures fall back to the raw value so the page never throws on odd data.
/// </summary>
internal static class MonitorViewFormat
{
    private const string Dash = "—";

    private static readonly TimeProvider Clock = TimeProvider.System;

    /// <summary>First 8 characters of a trace id plus an ellipsis, or a dash.</summary>
    internal static string ShortTraceId(string? traceId)
    {
        if (string.IsNullOrEmpty(traceId))
        {
            return Dash;
        }

        return traceId.Length <= 8 ? traceId : traceId[..8] + "…";
    }

    /// <summary>Digit-grouped integer (e.g. <c>31,851</c>), or a dash for null.</summary>
    internal static string Tokens(int? value) =>
        value is null ? Dash : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Digit-grouped non-token integer, or a dash for null.</summary>
    internal static string Count(int? value) =>
        value is null ? Dash : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Human duration: <c>618 ms</c> / <c>2.3 秒</c> / <c>1分 12秒</c>.</summary>
    internal static string Duration(double? milliseconds)
    {
        if (milliseconds is null || double.IsNaN(milliseconds.Value) || milliseconds.Value < 0)
        {
            return Dash;
        }

        var ms = milliseconds.Value;
        if (ms < 1000)
        {
            return $"{Math.Round(ms)} ms";
        }

        var totalSeconds = ms / 1000d;
        if (totalSeconds < 60)
        {
            return $"{totalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒";
        }

        var minutes = (int)(totalSeconds / 60);
        var seconds = (int)Math.Round(totalSeconds - (minutes * 60));
        if (seconds == 60)
        {
            minutes++;
            seconds = 0;
        }

        return $"{minutes}分 {seconds}秒";
    }

    /// <summary>Relative time in Japanese (e.g. <c>5分前</c>), or the raw value on parse failure.</summary>
    internal static string RelativeTime(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
        {
            return Dash;
        }

        if (!DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return timestamp;
        }

        var delta = Clock.GetUtcNow() - parsed;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "たった今";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}分前";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}時間前";
        }

        return $"{(int)delta.TotalDays}日前";
    }
}

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal enum MonitorAnalysisFocus
{
    Latency,
    Tokens,
    Cache,
    Errors,
    ToolUsage,
    AgentFlow,
}

internal enum MonitorAnalysisStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    TimedOut,
}

internal sealed record MonitorAnalysisStartResult(long RunId);

internal sealed record MonitorAnalysisContext(
    long RunId,
    string TraceId,
    long? RawRecordId,
    string? SpanId,
    MonitorAnalysisFocus Focus);

internal interface IMonitorAnalysisRunner
{
    Task StartAsync(MonitorAnalysisContext context, CancellationToken cancellationToken);
}

internal sealed record MonitorAnalysisRun(
    long Id,
    string TraceId,
    long? RawRecordId,
    string? SpanId,
    MonitorAnalysisFocus Focus,
    MonitorAnalysisStatus Status,
    string RequestedAt,
    string? StartedAt,
    string? CompletedAt,
    string? ResultMarkdown,
    string? ErrorMessage);

internal sealed record MonitorAnalysisSafeSummary(
    long RunId,
    string Markdown,
    string GeneratedAt);

internal static class MonitorAnalysisWire
{
    public static string ToWireValue(this MonitorAnalysisFocus focus) =>
        focus switch
        {
            MonitorAnalysisFocus.Latency => "latency",
            MonitorAnalysisFocus.Tokens => "tokens",
            MonitorAnalysisFocus.Cache => "cache",
            MonitorAnalysisFocus.Errors => "errors",
            MonitorAnalysisFocus.ToolUsage => "tool-usage",
            MonitorAnalysisFocus.AgentFlow => "agent-flow",
            _ => throw new ArgumentOutOfRangeException(nameof(focus)),
        };

    public static string ToWireValue(this MonitorAnalysisStatus status) =>
        status switch
        {
            MonitorAnalysisStatus.Queued => "queued",
            MonitorAnalysisStatus.Running => "running",
            MonitorAnalysisStatus.Succeeded => "succeeded",
            MonitorAnalysisStatus.Failed => "failed",
            MonitorAnalysisStatus.Canceled => "canceled",
            MonitorAnalysisStatus.TimedOut => "timed_out",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    public static bool TryParseFocus(string? value, out MonitorAnalysisFocus focus)
    {
        focus = value switch
        {
            "latency" => MonitorAnalysisFocus.Latency,
            "tokens" => MonitorAnalysisFocus.Tokens,
            "cache" => MonitorAnalysisFocus.Cache,
            "errors" => MonitorAnalysisFocus.Errors,
            "tool-usage" => MonitorAnalysisFocus.ToolUsage,
            "agent-flow" => MonitorAnalysisFocus.AgentFlow,
            _ => default,
        };
        return value is "latency" or "tokens" or "cache" or "errors" or "tool-usage" or "agent-flow";
    }

    public static bool TryParseTerminalStatus(string? value, out MonitorAnalysisStatus status)
    {
        status = value switch
        {
            "succeeded" => MonitorAnalysisStatus.Succeeded,
            "failed" => MonitorAnalysisStatus.Failed,
            "canceled" => MonitorAnalysisStatus.Canceled,
            "timed_out" => MonitorAnalysisStatus.TimedOut,
            _ => default,
        };
        return value is "succeeded" or "failed" or "canceled" or "timed_out";
    }
}

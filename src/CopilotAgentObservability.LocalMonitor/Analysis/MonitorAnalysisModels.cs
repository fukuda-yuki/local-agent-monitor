namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal enum MonitorAnalysisFocus
{
    Latency,
    Tokens,
    Cache,
    Errors,
    ToolUsage,
    AgentFlow,
    InstructionDiagnosis,
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

internal sealed class MonitorAnalysisOperationToken
{
    private readonly byte[] value;

    internal MonitorAnalysisOperationToken(byte[] value) => this.value = value.ToArray();

    internal bool Matches(byte[] candidate) => value.AsSpan().SequenceEqual(candidate);
    internal byte[] Copy() => value.ToArray();
}

internal sealed record MonitorAnalysisStartResult(long RunId, MonitorAnalysisOperationToken OperationToken);

/// <summary>One prior Q&amp;A turn of the drawer chat, re-sent with each follow-up (history resend, D045).</summary>
internal sealed record AnalysisHistoryTurn(
    string? Question,
    string? Answer);

/// <summary>
/// Per-run analysis input. <see cref="Question"/> / <see cref="History"/> carry
/// the drawer's follow-up chat (D045): the client holds the transcript and each
/// follow-up creates a new run whose prompt embeds the prior Q&amp;A. Nothing is
/// persisted server-side beyond the run row itself.
/// </summary>
internal sealed record MonitorAnalysisContext(
    long RunId,
    string TraceId,
    long? RawRecordId,
    string? SpanId,
    MonitorAnalysisFocus Focus,
    string? Question = null,
    IReadOnlyList<AnalysisHistoryTurn>? History = null,
    MonitorAnalysisOperationToken? OperationToken = null);

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
    string? CompletedAt);

internal sealed record AnalysisRunRawEvent(string EventType, string Message, string OccurredAt);

internal sealed record AnalysisRunRawSnapshot(
    string? ResultMarkdown,
    string? ErrorMessage,
    IReadOnlyList<AnalysisRunRawEvent> Events);

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
            MonitorAnalysisFocus.InstructionDiagnosis => "instruction-diagnosis",
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
            "instruction-diagnosis" => MonitorAnalysisFocus.InstructionDiagnosis,
            _ => default,
        };
        return value is "latency" or "tokens" or "cache" or "errors" or "tool-usage" or "agent-flow"
            or "instruction-diagnosis";
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

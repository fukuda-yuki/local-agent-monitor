namespace CopilotAgentObservability.Telemetry;

/// <summary>
/// Sanitized projection of one ingested raw record for the Local Ingestion
/// Monitor. Carries allowlisted metadata only — never raw prompt / response /
/// tool content or PII (<c>user.id</c> / <c>user.email</c>). One record yields a
/// single ingestion-level summary plus one <see cref="MonitorTraceContribution"/>
/// per non-blank <c>trace_id</c> in the payload (multi-trace fan-out).
/// </summary>
internal sealed record MonitorRecordProjection(
    string? TraceId,
    string? ClientKind,
    int SpanCount,
    IReadOnlyList<MonitorTraceContribution> TraceContributions);

/// <summary>
/// Sanitized per-trace contribution of one raw record. Aggregated into
/// <c>monitor_traces</c> exactly once per raw record (idempotency guard in the
/// persistence layer). Counts only; no raw content.
/// </summary>
internal sealed record MonitorTraceContribution(
    string TraceId,
    string? ClientKind,
    string? ExperimentId,
    string? TaskId,
    string? TaskCategory,
    string? AgentVariant,
    string? PromptVersion,
    int SpanCount,
    int ToolCallCount,
    int ErrorCount);

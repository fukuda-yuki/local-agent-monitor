namespace CopilotAgentObservability.Telemetry;

internal sealed record MonitorSkillInvocationProjection(
    string TraceId,
    string? SpanId,
    int SpanOrdinal,
    string? NativeSessionId,
    string SkillName,
    string? SkillSource,
    string? InvocationTrigger,
    string SourceApplicationVersion);

internal sealed record MonitorSkillInventoryProjection(
    string TraceId,
    string? NativeSessionId,
    int ObservedNameCount,
    IReadOnlyList<string> RetainedNames,
    bool NamesTruncated,
    string SourceApplicationVersion);

internal sealed record MonitorSkillProjectionBatch(
    IReadOnlyList<MonitorSkillInvocationProjection> Invocations,
    IReadOnlyList<MonitorSkillInventoryProjection> Inventories)
{
    public static MonitorSkillProjectionBatch Empty { get; } = new([], []);
}

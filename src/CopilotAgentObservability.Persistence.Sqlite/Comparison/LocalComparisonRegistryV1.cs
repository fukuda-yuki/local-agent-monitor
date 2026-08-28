namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalComparisonSectionDefinition(
    int Ordinal,
    string Token);

internal sealed record LocalComparisonMetricDefinition(
    int SectionOrdinal,
    string Key,
    bool IsNamedFamily = false,
    bool IncludeTotal = true);

internal static class LocalComparisonRegistryV1
{
    internal static IReadOnlyList<LocalComparisonSectionDefinition> Sections { get; } =
        Array.AsReadOnly(new[]
        {
            new LocalComparisonSectionDefinition(1, "target"),
            new LocalComparisonSectionDefinition(2, "tokens"),
            new LocalComparisonSectionDefinition(3, "input_token_breakdown"),
            new LocalComparisonSectionDefinition(4, "time_and_execution"),
            new LocalComparisonSectionDefinition(5, "skills"),
            new LocalComparisonSectionDefinition(6, "tools"),
            new LocalComparisonSectionDefinition(7, "subagents"),
            new LocalComparisonSectionDefinition(8, "errors_and_retries"),
            new LocalComparisonSectionDefinition(9, "conditions"),
        });

    internal static IReadOnlyList<LocalComparisonMetricDefinition> Metrics { get; } =
        Array.AsReadOnly(new[]
        {
            new LocalComparisonMetricDefinition(1, "included_session_count"),
            new LocalComparisonMetricDefinition(1, "excluded_session_count"),
            new LocalComparisonMetricDefinition(1, "available_session_count"),
            new LocalComparisonMetricDefinition(1, "period"),
            new LocalComparisonMetricDefinition(1, "archived_inclusion"),
            new LocalComparisonMetricDefinition(2, "input_tokens"),
            new LocalComparisonMetricDefinition(2, "output_tokens"),
            new LocalComparisonMetricDefinition(2, "total_tokens"),
            new LocalComparisonMetricDefinition(3, "cache_read_tokens"),
            new LocalComparisonMetricDefinition(3, "new_input_tokens"),
            new LocalComparisonMetricDefinition(3, "cache_creation_tokens"),
            new LocalComparisonMetricDefinition(3, "cache_read_ratio", IncludeTotal: false),
            new LocalComparisonMetricDefinition(4, "session_duration"),
            new LocalComparisonMetricDefinition(4, "execution_count"),
            new LocalComparisonMetricDefinition(4, "model_turn_count"),
            new LocalComparisonMetricDefinition(4, "tool_call_count"),
            new LocalComparisonMetricDefinition(4, "skill_invocation_count"),
            new LocalComparisonMetricDefinition(4, "subagent_start_count"),
            new LocalComparisonMetricDefinition(4, "error_count"),
            new LocalComparisonMetricDefinition(4, "retry_count"),
            new LocalComparisonMetricDefinition(7, "subagent_aggregate_start_count"),
            new LocalComparisonMetricDefinition(7, "subagent_aggregate_completed_count"),
            new LocalComparisonMetricDefinition(7, "subagent_aggregate_failed_count"),
            new LocalComparisonMetricDefinition(7, "subagent_aggregate_recorded_tokens"),
            new LocalComparisonMetricDefinition(8, "error_session_count"),
            new LocalComparisonMetricDefinition(8, "error_count"),
            new LocalComparisonMetricDefinition(8, "retry_session_count"),
            new LocalComparisonMetricDefinition(8, "retry_count"),
            new LocalComparisonMetricDefinition(8, "recovery_relation_count"),
            new LocalComparisonMetricDefinition(9, "sources"),
            new LocalComparisonMetricDefinition(9, "models"),
            new LocalComparisonMetricDefinition(9, "source_versions"),
            new LocalComparisonMetricDefinition(9, "adapter_versions"),
            new LocalComparisonMetricDefinition(9, "completeness"),
            new LocalComparisonMetricDefinition(9, "metric_availability"),
        });

    internal static IReadOnlyList<LocalComparisonMetricDefinition> NamedFamilies { get; } =
        Array.AsReadOnly(new[]
        {
            new LocalComparisonMetricDefinition(5, "skill", IsNamedFamily: true),
            new LocalComparisonMetricDefinition(6, "tool", IsNamedFamily: true),
            new LocalComparisonMetricDefinition(7, "subagent", IsNamedFamily: true),
        });

    internal static IReadOnlyList<string> RequiredSessionScalarKeys { get; } =
        Array.AsReadOnly(new[]
        {
            "input_tokens", "output_tokens", "total_tokens", "cache_read_tokens",
            "cache_creation_tokens", "session_duration", "execution_count",
            "model_turn_count", "tool_call_count", "skill_invocation_count",
            "subagent_start_count", "error_count", "retry_count",
            "subagent_completed_count", "subagent_failed_count", "subagent_recorded_tokens",
            "error_session_count", "retry_session_count", "recovery_relation_count",
        });

    internal static IReadOnlyList<string> ConditionKeys { get; } =
        Array.AsReadOnly(new[]
        {
            "sources", "models", "source_versions", "adapter_versions", "completeness",
        });

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> NamedFieldKeys { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["skill"] = Array.AsReadOnly(new[] { "invocation_count" }),
            ["tool"] = Array.AsReadOnly(new[] { "call_count", "failure_count", "retry_count" }),
            ["subagent"] = Array.AsReadOnly(new[]
            {
                "start_count", "completed_count", "failed_count", "recorded_tokens",
            }),
        };
}

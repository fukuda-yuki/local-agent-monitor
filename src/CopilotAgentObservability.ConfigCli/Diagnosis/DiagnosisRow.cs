namespace CopilotAgentObservability.ConfigCli;

internal sealed record DiagnosisRow(
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("task_category")] string? TaskCategory,
    [property: JsonPropertyName("client_kind")] string? ClientKind,
    [property: JsonPropertyName("comparison_id")] string? ComparisonId,
    [property: JsonPropertyName("experiment_id")] string? ExperimentId,
    [property: JsonPropertyName("experiment_condition")] string? ExperimentCondition,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("agent_variant")] string? AgentVariant,
    [property: JsonPropertyName("task_run_index")] int? TaskRunIndex,
    [property: JsonPropertyName("failure_category_id")] string? FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("evidence_summary")] string? EvidenceSummary,
    [property: JsonPropertyName("recommended_improvement_target")] string? RecommendedImprovementTarget,
    [property: JsonPropertyName("review_status")] string? ReviewStatus);

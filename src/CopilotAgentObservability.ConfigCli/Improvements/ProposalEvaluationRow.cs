namespace CopilotAgentObservability.ConfigCli;

internal sealed record ProposalEvaluationRow(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("source_diagnosis_index")] int SourceDiagnosisIndex,
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
    [property: JsonPropertyName("improvement_target")] string? ImprovementTarget,
    [property: JsonPropertyName("proposal_title")] string ProposalTitle,
    [property: JsonPropertyName("proposal_evaluation_status")] string ProposalEvaluationStatus,
    [property: JsonPropertyName("evaluator_findings")] string EvaluatorFindings,
    [property: JsonPropertyName("required_human_checks")] string RequiredHumanChecks,
    [property: JsonPropertyName("evaluator_notes")] string EvaluatorNotes);

namespace CopilotAgentObservability.ConfigCli;

internal sealed record ImprovementCandidateRow(
    [property: JsonPropertyName("improvement_candidate_id")] string ImprovementCandidateId,
    [property: JsonPropertyName("source_diagnosis_candidate_id")] string SourceDiagnosisCandidateId,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("failure_category_id")] string FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("improvement_target")] string ImprovementTarget,
    [property: JsonPropertyName("proposal_title")] string ProposalTitle,
    [property: JsonPropertyName("proposal_summary")] string ProposalSummary,
    [property: JsonPropertyName("proposed_change_kind")] string ProposedChangeKind,
    [property: JsonPropertyName("evidence_ref")] string EvidenceRef,
    [property: JsonPropertyName("sensitive_bundle_path")] string? SensitiveBundlePath,
    [property: JsonPropertyName("candidate_status")] string CandidateStatus);

internal sealed record AutoDecisionRow(
    [property: JsonPropertyName("auto_decision_id")] string AutoDecisionId,
    [property: JsonPropertyName("source_improvement_candidate_id")] string SourceImprovementCandidateId,
    [property: JsonPropertyName("source_diagnosis_candidate_id")] string SourceDiagnosisCandidateId,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("decision_status")] string DecisionStatus,
    [property: JsonPropertyName("decision_rule_id")] string DecisionRuleId,
    [property: JsonPropertyName("decision_reason")] string DecisionReason,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("blocking_risk_checks")] string BlockingRiskChecks,
    [property: JsonPropertyName("sensitive_content_included")] bool SensitiveContentIncluded,
    [property: JsonPropertyName("sensitive_bundle_path")] string? SensitiveBundlePath,
    [property: JsonPropertyName("implementation_target")] string ImplementationTarget,
    [property: JsonPropertyName("next_action")] string NextAction);

namespace CopilotAgentObservability.ConfigCli;

internal sealed record DiagnosisCandidateRow(
    [property: JsonPropertyName("diagnosis_candidate_id")] string DiagnosisCandidateId,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("source_record_ref")] string SourceRecordRef,
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("failure_category_id")] string FailureCategoryId,
    [property: JsonPropertyName("anti_pattern_id")] string? AntiPatternId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("recommended_improvement_target")] string RecommendedImprovementTarget,
    [property: JsonPropertyName("evidence_summary")] string EvidenceSummary,
    [property: JsonPropertyName("evidence_ref")] string EvidenceRef,
    [property: JsonPropertyName("content_included")] bool ContentIncluded,
    [property: JsonPropertyName("sensitive_bundle_path")] string? SensitiveBundlePath,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("required_human_checks")] string RequiredHumanChecks,
    [property: JsonPropertyName("candidate_status")] string CandidateStatus);

internal sealed record DiagnosisCandidateDraft(
    string? TraceId,
    string SourceRecordRef,
    string RuleId,
    string FailureCategoryId,
    string? AntiPatternId,
    string Severity,
    string RecommendedImprovementTarget,
    string EvidenceSummary,
    string EvidenceRef,
    string Confidence,
    string RequiredHumanChecks,
    string CandidateStatus,
    IReadOnlyList<RawEvidenceFragment> Fragments);

internal sealed record RawEvidenceFragment(
    string ContentKind,
    string SourceLocator,
    string SourcePath,
    string Value);

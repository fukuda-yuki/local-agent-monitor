namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementCandidateOutputWriter
{
    public static readonly string[] Columns =
    [
        "improvement_candidate_id",
        "source_diagnosis_candidate_id",
        "trace_id",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "improvement_target",
        "proposal_title",
        "proposal_summary",
        "proposed_change_kind",
        "evidence_ref",
        "sensitive_bundle_path",
        "candidate_status",
    ];

    public static string WriteJson(IReadOnlyList<ImprovementCandidateRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<ImprovementCandidateRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(ImprovementCandidateRow row, string column)
    {
        return column switch
        {
            "improvement_candidate_id" => row.ImprovementCandidateId,
            "source_diagnosis_candidate_id" => row.SourceDiagnosisCandidateId,
            "trace_id" => row.TraceId,
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "improvement_target" => row.ImprovementTarget,
            "proposal_title" => row.ProposalTitle,
            "proposal_summary" => row.ProposalSummary,
            "proposed_change_kind" => row.ProposedChangeKind,
            "evidence_ref" => row.EvidenceRef,
            "sensitive_bundle_path" => row.SensitiveBundlePath,
            "candidate_status" => row.CandidateStatus,
            _ => null,
        };
    }
}

internal static class AutoDecisionOutputWriter
{
    public static readonly string[] Columns =
    [
        "auto_decision_id",
        "source_improvement_candidate_id",
        "source_diagnosis_candidate_id",
        "trace_id",
        "decision_status",
        "decision_rule_id",
        "decision_reason",
        "confidence",
        "blocking_risk_checks",
        "sensitive_content_included",
        "sensitive_bundle_path",
        "implementation_target",
        "next_action",
    ];

    public static string WriteJson(IReadOnlyList<AutoDecisionRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<AutoDecisionRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(AutoDecisionRow row, string column)
    {
        return column switch
        {
            "auto_decision_id" => row.AutoDecisionId,
            "source_improvement_candidate_id" => row.SourceImprovementCandidateId,
            "source_diagnosis_candidate_id" => row.SourceDiagnosisCandidateId,
            "trace_id" => row.TraceId,
            "decision_status" => row.DecisionStatus,
            "decision_rule_id" => row.DecisionRuleId,
            "decision_reason" => row.DecisionReason,
            "confidence" => row.Confidence,
            "blocking_risk_checks" => row.BlockingRiskChecks,
            "sensitive_content_included" => row.SensitiveContentIncluded ? "true" : "false",
            "sensitive_bundle_path" => row.SensitiveBundlePath,
            "implementation_target" => row.ImplementationTarget,
            "next_action" => row.NextAction,
            _ => null,
        };
    }
}

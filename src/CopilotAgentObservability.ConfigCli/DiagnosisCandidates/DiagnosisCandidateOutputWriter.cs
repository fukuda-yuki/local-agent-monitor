namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisCandidateOutputWriter
{
    public static readonly string[] Columns =
    [
        "diagnosis_candidate_id",
        "trace_id",
        "source_record_ref",
        "rule_id",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "recommended_improvement_target",
        "evidence_summary",
        "evidence_ref",
        "content_included",
        "sensitive_bundle_path",
        "confidence",
        "required_human_checks",
        "candidate_status",
    ];

    public static string WriteJson(IReadOnlyList<DiagnosisCandidateRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<DiagnosisCandidateRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(DiagnosisCandidateRow row, string column)
    {
        return column switch
        {
            "diagnosis_candidate_id" => row.DiagnosisCandidateId,
            "trace_id" => row.TraceId,
            "source_record_ref" => row.SourceRecordRef,
            "rule_id" => row.RuleId,
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "recommended_improvement_target" => row.RecommendedImprovementTarget,
            "evidence_summary" => row.EvidenceSummary,
            "evidence_ref" => row.EvidenceRef,
            "content_included" => row.ContentIncluded ? "true" : "false",
            "sensitive_bundle_path" => row.SensitiveBundlePath,
            "confidence" => row.Confidence,
            "required_human_checks" => row.RequiredHumanChecks,
            "candidate_status" => row.CandidateStatus,
            _ => null,
        };
    }
}

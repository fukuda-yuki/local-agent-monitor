namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisOutputWriter
{
    public static readonly string[] Columns =
    [
        "trace_id",
        "task_id",
        "task_category",
        "client_kind",
        "comparison_id",
        "experiment_id",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "task_run_index",
        "failure_category_id",
        "anti_pattern_id",
        "severity",
        "evidence_summary",
        "recommended_improvement_target",
        "review_status",
    ];

    public static string WriteJson(IReadOnlyList<DiagnosisRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<DiagnosisRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(DiagnosisRow row, string column)
    {
        return column switch
        {
            "trace_id" => row.TraceId,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "client_kind" => row.ClientKind,
            "comparison_id" => row.ComparisonId,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "task_run_index" => row.TaskRunIndex?.ToString(CultureInfo.InvariantCulture),
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "severity" => row.Severity,
            "evidence_summary" => row.EvidenceSummary,
            "recommended_improvement_target" => row.RecommendedImprovementTarget,
            "review_status" => row.ReviewStatus,
            _ => null,
        };
    }

}

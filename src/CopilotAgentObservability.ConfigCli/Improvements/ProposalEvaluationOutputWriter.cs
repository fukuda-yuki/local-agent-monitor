namespace CopilotAgentObservability.ConfigCli;

internal static class ProposalEvaluationOutputWriter
{
    public static readonly string[] Columns =
    [
        "proposal_id",
        "source_diagnosis_index",
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
        "improvement_target",
        "proposal_title",
        "proposal_evaluation_status",
        "evaluator_findings",
        "required_human_checks",
        "evaluator_notes",
    ];

    public static string WriteJson(IReadOnlyList<ProposalEvaluationRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<ProposalEvaluationRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(ProposalEvaluationRow row, string column)
    {
        return column switch
        {
            "proposal_id" => row.ProposalId,
            "source_diagnosis_index" => row.SourceDiagnosisIndex.ToString(CultureInfo.InvariantCulture),
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
            "improvement_target" => row.ImprovementTarget,
            "proposal_title" => row.ProposalTitle,
            "proposal_evaluation_status" => row.ProposalEvaluationStatus,
            "evaluator_findings" => row.EvaluatorFindings,
            "required_human_checks" => row.RequiredHumanChecks,
            "evaluator_notes" => row.EvaluatorNotes,
            _ => null,
        };
    }

}

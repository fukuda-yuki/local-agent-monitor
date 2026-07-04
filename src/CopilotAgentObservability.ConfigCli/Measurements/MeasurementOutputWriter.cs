namespace CopilotAgentObservability.ConfigCli;

internal static class MeasurementOutputWriter
{
    public static readonly string[] Columns =
    [
        "trace_id",
        "experiment_id",
        "client_kind",
        "task_id",
        "task_category",
        "task_run_index",
        "experiment_condition",
        "prompt_version",
        "agent_variant",
        "repo_snapshot",
        "input_tokens",
        "output_tokens",
        "total_tokens",
        "turn_count",
        "tool_call_count",
        "duration_ms",
        "error_count",
        "success_status",
        "evaluator_id",
        "evaluation_notes",
        "evaluated_at",
        "unknown_spans_json",
        "unknown_attributes_json",
        "aggregation_notes",
    ];

    public static string WriteJson(IReadOnlyList<MeasurementRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<MeasurementRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(MeasurementRow row, string column)
    {
        return column switch
        {
            "trace_id" => row.TraceId,
            "experiment_id" => row.ExperimentId,
            "client_kind" => row.ClientKind,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "task_run_index" => row.TaskRunIndex?.ToString(),
            "experiment_condition" => row.ExperimentCondition,
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "repo_snapshot" => row.RepoSnapshot,
            "input_tokens" => row.InputTokens?.ToString(),
            "output_tokens" => row.OutputTokens?.ToString(),
            "total_tokens" => row.TotalTokens?.ToString(),
            "turn_count" => row.TurnCount?.ToString(),
            "tool_call_count" => row.ToolCallCount?.ToString(),
            "duration_ms" => row.DurationMs?.ToString(),
            "error_count" => row.ErrorCount?.ToString(),
            "success_status" => row.SuccessStatus,
            "evaluator_id" => row.EvaluatorId,
            "evaluation_notes" => row.EvaluationNotes,
            "evaluated_at" => row.EvaluatedAt,
            "unknown_spans_json" => row.UnknownSpansJson?.ToJsonString(),
            "unknown_attributes_json" => row.UnknownAttributesJson?.ToJsonString(),
            "aggregation_notes" => row.AggregationNotes,
            _ => null,
        };
    }

}

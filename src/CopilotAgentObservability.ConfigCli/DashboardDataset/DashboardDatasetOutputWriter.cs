namespace CopilotAgentObservability.ConfigCli;

internal static class DashboardDatasetOutputWriter
{
    public static readonly string[] RunSummaryColumns =
    [
        "schema_version",
        "time_bucket_start_utc",
        "time_bucket_granularity",
        "trace_id",
        "langfuse_trace_id",
        "measurement_record_ref",
        "user_id",
        "user_email",
        "client_kind",
        "experiment_id",
        "experiment_condition",
        "task_id",
        "task_category",
        "task_run_index",
        "prompt_version",
        "agent_variant",
        "skill_version",
        "mcp_profile",
        "repo_snapshot",
        "model",
        "status",
        "success_status",
        "duration_ms",
        "ttft_ms",
        "ttft_source",
        "input_tokens",
        "output_tokens",
        "total_tokens",
        "turn_count",
        "llm_call_count",
        "tool_call_count",
        "error_count",
        "estimated_cost",
        "cost_source",
        "long_running_trace",
        "stuck_session",
        "sensitive_bundle_present",
        "drilldown_ref",
    ];

    public static readonly string[] OperationSummaryColumns =
    [
        "schema_version",
        "time_bucket_start_utc",
        "time_bucket_granularity",
        "trace_id",
        "user_id",
        "user_email",
        "client_kind",
        "experiment_id",
        "experiment_condition",
        "task_id",
        "repo_snapshot",
        "operation_kind",
        "tool_name",
        "model",
        "status",
        "call_count",
        "error_count",
        "timeout_count",
        "retry_count",
        "total_duration_ms",
        "p50_duration_ms",
        "p95_duration_ms",
        "approval_wait_ms",
        "permission_result",
        "subagent_call_count",
        "nested_agent_call_count",
        "long_running_tool",
        "sensitive_bundle_present",
        "drilldown_ref",
    ];

    public static readonly string[] CandidateSummaryColumns =
    [
        "schema_version",
        "time_bucket_start_utc",
        "time_bucket_granularity",
        "trace_id",
        "user_id",
        "user_email",
        "client_kind",
        "experiment_id",
        "experiment_condition",
        "task_id",
        "repo_snapshot",
        "candidate_kind",
        "diagnosis_candidate_id",
        "improvement_candidate_id",
        "auto_decision_id",
        "proposal_id",
        "candidate_rule",
        "failure_category_id",
        "anti_pattern_id",
        "candidate_severity",
        "improvement_target",
        "proposed_change_kind",
        "candidate_status",
        "decision_status",
        "review_status",
        "human_decision",
        "backlog_age_hours",
        "evidence_ref",
        "sensitive_bundle_present",
        "drilldown_ref",
    ];

    public static readonly string[] CollectionHealthColumns =
    [
        "schema_version",
        "time_bucket_start_utc",
        "time_bucket_granularity",
        "input_ref",
        "trace_id",
        "user_id",
        "user_email",
        "client_kind",
        "experiment_id",
        "health_check_kind",
        "health_status",
        "missing_attribute_name",
        "unknown_span_count",
        "unknown_attribute_count",
        "normalization_failure_count",
        "mapping_failure_count",
        "candidate_generation_failure_count",
        "affected_record_count",
        "details_ref",
    ];

    public static string WriteJson(DashboardDataset dataset)
    {
        return JsonOutput.WriteIndented(dataset);
    }

    public static void WriteCsvDirectory(DashboardDataset dataset, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "dashboard_run_summary.csv"), WriteRunSummaryCsv(dataset.RunSummary), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "dashboard_operation_summary.csv"), WriteOperationSummaryCsv(dataset.OperationSummary), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "dashboard_candidate_summary.csv"), WriteCandidateSummaryCsv(dataset.CandidateSummary), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "dashboard_collection_health.csv"), WriteCollectionHealthCsv(dataset.CollectionHealth), Encoding.UTF8);
    }

    private static string WriteRunSummaryCsv(IReadOnlyList<DashboardRunSummaryRow> rows)
    {
        return WriteCsv(RunSummaryColumns, rows, GetRunSummaryValue);
    }

    private static string WriteOperationSummaryCsv(IReadOnlyList<DashboardOperationSummaryRow> rows)
    {
        return WriteCsv(OperationSummaryColumns, rows, GetOperationSummaryValue);
    }

    private static string WriteCandidateSummaryCsv(IReadOnlyList<DashboardCandidateSummaryRow> rows)
    {
        return WriteCsv(CandidateSummaryColumns, rows, GetCandidateSummaryValue);
    }

    private static string WriteCollectionHealthCsv(IReadOnlyList<DashboardCollectionHealthRow> rows)
    {
        return WriteCsv(CollectionHealthColumns, rows, GetCollectionHealthValue);
    }

    private static string WriteCsv<T>(IReadOnlyList<string> columns, IReadOnlyList<T> rows, Func<T, string, string?> getValue)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', columns.Select(column => CsvEscaper.Escape(getValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetRunSummaryValue(DashboardRunSummaryRow row, string column)
    {
        return column switch
        {
            "schema_version" => row.SchemaVersion,
            "time_bucket_start_utc" => row.TimeBucketStartUtc,
            "time_bucket_granularity" => row.TimeBucketGranularity,
            "trace_id" => row.TraceId,
            "langfuse_trace_id" => row.LangfuseTraceId,
            "measurement_record_ref" => row.MeasurementRecordRef,
            "user_id" => row.UserId,
            "user_email" => row.UserEmail,
            "client_kind" => row.ClientKind,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "task_id" => row.TaskId,
            "task_category" => row.TaskCategory,
            "task_run_index" => Format(row.TaskRunIndex),
            "prompt_version" => row.PromptVersion,
            "agent_variant" => row.AgentVariant,
            "skill_version" => row.SkillVersion,
            "mcp_profile" => row.McpProfile,
            "repo_snapshot" => row.RepoSnapshot,
            "model" => row.Model,
            "status" => row.Status,
            "success_status" => row.SuccessStatus,
            "duration_ms" => Format(row.DurationMs),
            "ttft_ms" => Format(row.TtftMs),
            "ttft_source" => row.TtftSource,
            "input_tokens" => Format(row.InputTokens),
            "output_tokens" => Format(row.OutputTokens),
            "total_tokens" => Format(row.TotalTokens),
            "turn_count" => Format(row.TurnCount),
            "llm_call_count" => Format(row.LlmCallCount),
            "tool_call_count" => Format(row.ToolCallCount),
            "error_count" => Format(row.ErrorCount),
            "estimated_cost" => Format(row.EstimatedCost),
            "cost_source" => row.CostSource,
            "long_running_trace" => Format(row.LongRunningTrace),
            "stuck_session" => Format(row.StuckSession),
            "sensitive_bundle_present" => Format(row.SensitiveBundlePresent),
            "drilldown_ref" => row.DrilldownRef,
            _ => null,
        };
    }

    private static string? GetOperationSummaryValue(DashboardOperationSummaryRow row, string column)
    {
        return column switch
        {
            "schema_version" => row.SchemaVersion,
            "time_bucket_start_utc" => row.TimeBucketStartUtc,
            "time_bucket_granularity" => row.TimeBucketGranularity,
            "trace_id" => row.TraceId,
            "user_id" => row.UserId,
            "user_email" => row.UserEmail,
            "client_kind" => row.ClientKind,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "task_id" => row.TaskId,
            "repo_snapshot" => row.RepoSnapshot,
            "operation_kind" => row.OperationKind,
            "tool_name" => row.ToolName,
            "model" => row.Model,
            "status" => row.Status,
            "call_count" => Format(row.CallCount),
            "error_count" => Format(row.ErrorCount),
            "timeout_count" => Format(row.TimeoutCount),
            "retry_count" => Format(row.RetryCount),
            "total_duration_ms" => Format(row.TotalDurationMs),
            "p50_duration_ms" => Format(row.P50DurationMs),
            "p95_duration_ms" => Format(row.P95DurationMs),
            "approval_wait_ms" => Format(row.ApprovalWaitMs),
            "permission_result" => row.PermissionResult,
            "subagent_call_count" => Format(row.SubagentCallCount),
            "nested_agent_call_count" => Format(row.NestedAgentCallCount),
            "long_running_tool" => Format(row.LongRunningTool),
            "sensitive_bundle_present" => Format(row.SensitiveBundlePresent),
            "drilldown_ref" => row.DrilldownRef,
            _ => null,
        };
    }

    private static string? GetCandidateSummaryValue(DashboardCandidateSummaryRow row, string column)
    {
        return column switch
        {
            "schema_version" => row.SchemaVersion,
            "time_bucket_start_utc" => row.TimeBucketStartUtc,
            "time_bucket_granularity" => row.TimeBucketGranularity,
            "trace_id" => row.TraceId,
            "user_id" => row.UserId,
            "user_email" => row.UserEmail,
            "client_kind" => row.ClientKind,
            "experiment_id" => row.ExperimentId,
            "experiment_condition" => row.ExperimentCondition,
            "task_id" => row.TaskId,
            "repo_snapshot" => row.RepoSnapshot,
            "candidate_kind" => row.CandidateKind,
            "diagnosis_candidate_id" => row.DiagnosisCandidateId,
            "improvement_candidate_id" => row.ImprovementCandidateId,
            "auto_decision_id" => row.AutoDecisionId,
            "proposal_id" => row.ProposalId,
            "candidate_rule" => row.CandidateRule,
            "failure_category_id" => row.FailureCategoryId,
            "anti_pattern_id" => row.AntiPatternId,
            "candidate_severity" => row.CandidateSeverity,
            "improvement_target" => row.ImprovementTarget,
            "proposed_change_kind" => row.ProposedChangeKind,
            "candidate_status" => row.CandidateStatus,
            "decision_status" => row.DecisionStatus,
            "review_status" => row.ReviewStatus,
            "human_decision" => row.HumanDecision,
            "backlog_age_hours" => Format(row.BacklogAgeHours),
            "evidence_ref" => row.EvidenceRef,
            "sensitive_bundle_present" => Format(row.SensitiveBundlePresent),
            "drilldown_ref" => row.DrilldownRef,
            _ => null,
        };
    }

    private static string? GetCollectionHealthValue(DashboardCollectionHealthRow row, string column)
    {
        return column switch
        {
            "schema_version" => row.SchemaVersion,
            "time_bucket_start_utc" => row.TimeBucketStartUtc,
            "time_bucket_granularity" => row.TimeBucketGranularity,
            "input_ref" => row.InputRef,
            "trace_id" => row.TraceId,
            "user_id" => row.UserId,
            "user_email" => row.UserEmail,
            "client_kind" => row.ClientKind,
            "experiment_id" => row.ExperimentId,
            "health_check_kind" => row.HealthCheckKind,
            "health_status" => row.HealthStatus,
            "missing_attribute_name" => row.MissingAttributeName,
            "unknown_span_count" => Format(row.UnknownSpanCount),
            "unknown_attribute_count" => Format(row.UnknownAttributeCount),
            "normalization_failure_count" => Format(row.NormalizationFailureCount),
            "mapping_failure_count" => Format(row.MappingFailureCount),
            "candidate_generation_failure_count" => Format(row.CandidateGenerationFailureCount),
            "affected_record_count" => Format(row.AffectedRecordCount),
            "details_ref" => row.DetailsRef,
            _ => null,
        };
    }

    private static string Format(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Format(decimal? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Format(bool value)
    {
        return value ? "true" : "false";
    }
}

using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalEfficiencyContractsV1
{
    internal const string RegistryVersion = "historical-efficiency-driver-registry.v1";
    internal const string ReceiptSchemaVersion = "historical-efficiency-receipt.v1";
}

internal enum HistoricalEfficiencyDriverCategoryV1
{
    TokenVolume,
    ContextGrowth,
    CacheInefficiency,
    RetryOverhead,
    ToolCallVolume,
    ToolFailureOverhead,
    PermissionWait,
    SubagentFanout,
    DurationOutlier,
    ModelMixObservation,
}

internal enum HistoricalEfficiencyAnalysisStateV1 { Succeeded, ZeroDrivers }
internal enum HistoricalEfficiencyCoverageStateV1 { Matched, NoMatch, Insufficient, Unavailable }
internal enum HistoricalEfficiencyCoverageReasonV1
{
    ExactToolFailureStatusUnavailable,
    MissingRequiredCapability,
    MissingRequiredMetric,
    MixedEvaluationDimension,
    MinimumSampleUnmet,
    NoThresholdMatch,
}

internal enum HistoricalEfficiencyQualityAvailabilityV1 { Unavailable, Partial, Available, RegressionObserved }
internal enum HistoricalEfficiencyDriverVerdictV1 { Supported, Weak, Incomplete }
internal enum HistoricalEfficiencyComparisonNoteV1
{
    TruncatedWindow,
    PartialCompleteness,
    HistoricalSummaryPresent,
    MixedSourceSurface,
    MixedSourceVersion,
    MixedAdapterVersion,
    MixedModel,
    QualityUnavailable,
    QualityPartial,
    QualityRegressionObserved,
}

internal enum HistoricalEfficiencyValidationCodeV1 { InvalidHistoricalEfficiencyInput }

internal sealed class HistoricalEfficiencyValidationException : Exception
{
    internal HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1 code)
        : base("Historical efficiency input is invalid.") => Code = code;

    internal HistoricalEfficiencyValidationCodeV1 Code { get; }
}

internal sealed record HistoricalEfficiencyScalarV1(
    [property: JsonPropertyOrder(0)] string Name,
    [property: JsonPropertyOrder(1)] decimal Value,
    [property: JsonPropertyOrder(2)] string Unit);

internal sealed record HistoricalEfficiencyPercentileV1(
    [property: JsonPropertyOrder(0)] int Percentile,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] decimal Value,
    [property: JsonPropertyOrder(3)] string Unit);

internal sealed record HistoricalEfficiencyCoverageV1(
    [property: JsonPropertyOrder(0)] int IncludedSessionCount,
    [property: JsonPropertyOrder(1)] int ExcludedSessionCount,
    [property: JsonPropertyOrder(2)] bool TruncatedBefore,
    [property: JsonPropertyOrder(3)] long TruncatedSessionCount,
    [property: JsonPropertyOrder(4)] IReadOnlyList<HistoricalDistributionCountV1> Completeness,
    [property: JsonPropertyOrder(5)] IReadOnlyList<HistoricalDistributionCountV1> SourceKinds,
    [property: JsonPropertyOrder(6)] IReadOnlyList<HistoricalDistributionCountV1> Capabilities);

internal sealed record HistoricalEfficiencyCategoryCoverageV1(
    [property: JsonPropertyOrder(0)] HistoricalEfficiencyDriverCategoryV1 Category,
    [property: JsonPropertyOrder(1)] int DriverVersion,
    [property: JsonPropertyOrder(2)] string RuleSource,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> RequiredCapabilities,
    [property: JsonPropertyOrder(4)] string Formula,
    [property: JsonPropertyOrder(5)] string Threshold,
    [property: JsonPropertyOrder(6)] HistoricalEfficiencyCoverageStateV1 State,
    [property: JsonPropertyOrder(7)] int EligibleSessionCount,
    [property: JsonPropertyOrder(8)] int ObservedSampleCount,
    [property: JsonPropertyOrder(9)] int MinimumSample,
    [property: JsonPropertyOrder(10)] IReadOnlyList<HistoricalEfficiencyCoverageReasonV1> Reasons);

internal sealed record HistoricalEfficiencySourceDistributionV1(
    [property: JsonPropertyOrder(0)] IReadOnlyList<HistoricalDistributionCountV1> SourceSurfaces,
    [property: JsonPropertyOrder(1)] IReadOnlyList<HistoricalDistributionCountV1> SourceKinds);

internal sealed record HistoricalEfficiencyMitigationV1(
    [property: JsonPropertyOrder(0)] string Code,
    [property: JsonPropertyOrder(1)] string Summary,
    [property: JsonPropertyOrder(2)] IReadOnlyList<HistoricalEvidenceReferenceV1> EvidenceRefs);

internal sealed record HistoricalEfficiencyDriverV1(
    [property: JsonPropertyOrder(0)] string DriverId,
    [property: JsonPropertyOrder(1)] HistoricalEfficiencyDriverCategoryV1 Category,
    [property: JsonPropertyOrder(2)] int DriverVersion,
    [property: JsonPropertyOrder(3)] string RuleSource,
    [property: JsonPropertyOrder(4)] string Formula,
    [property: JsonPropertyOrder(5)] string Threshold,
    [property: JsonPropertyOrder(6)] string? SubjectSessionId,
    [property: JsonPropertyOrder(7)] IReadOnlyList<string> SourceSessions,
    [property: JsonPropertyOrder(8)] IReadOnlyList<HistoricalEvidenceReferenceV1> EvidenceRefs,
    [property: JsonPropertyOrder(9)] IReadOnlyList<HistoricalEvidenceReferenceV1> QualityEvidenceRefs,
    [property: JsonPropertyOrder(10)] IReadOnlyList<HistoricalEfficiencyScalarV1> ObservedValues,
    [property: JsonPropertyOrder(11)] HistoricalEfficiencyScalarV1? CohortMedian,
    [property: JsonPropertyOrder(12)] HistoricalEfficiencyPercentileV1? CohortPercentile,
    [property: JsonPropertyOrder(13)] HistoricalEfficiencySourceDistributionV1 SourceDistribution,
    [property: JsonPropertyOrder(14)] IReadOnlyList<HistoricalDistributionCountV1> CompletenessDistribution,
    [property: JsonPropertyOrder(15)] HistoricalEfficiencyQualityAvailabilityV1 QualityAvailability,
    [property: JsonPropertyOrder(16)] HistoricalEfficiencyDriverVerdictV1 Verdict,
    [property: JsonPropertyOrder(17)] IReadOnlyList<HistoricalEfficiencyComparisonNoteV1> ComparisonNotes,
    [property: JsonPropertyOrder(18)] string Summary,
    [property: JsonPropertyOrder(19)] HistoricalEfficiencyMitigationV1 Mitigation);

internal sealed record HistoricalEfficiencyReceiptV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ReceiptId,
    [property: JsonPropertyOrder(2)] string RegistryVersion,
    [property: JsonPropertyOrder(3)] string ExtractionId,
    [property: JsonPropertyOrder(4)] string ExtractionSha256,
    [property: JsonPropertyOrder(5)] HistoricalEfficiencyAnalysisStateV1 State,
    [property: JsonPropertyOrder(6)] HistoricalEfficiencyCoverageV1 Coverage,
    [property: JsonPropertyOrder(7)] HistoricalEfficiencyQualityAvailabilityV1 QualityAvailability,
    [property: JsonPropertyOrder(8)] IReadOnlyList<HistoricalEfficiencyComparisonNoteV1> ComparisonNotes,
    [property: JsonPropertyOrder(9)] IReadOnlyList<HistoricalEfficiencyCategoryCoverageV1> CategoryCoverage,
    [property: JsonPropertyOrder(10)] IReadOnlyList<HistoricalEfficiencyDriverV1> Drivers);

internal sealed record HistoricalEfficiencyAnalysisV1(
    HistoricalEfficiencyReceiptV1 Receipt,
    byte[] CanonicalBytes,
    string PayloadSha256);

internal sealed record HistoricalEfficiencyDriverRuleV1(
    HistoricalEfficiencyDriverCategoryV1 Category,
    int DriverVersion,
    string RuleSource,
    IReadOnlyList<string> RequiredCapabilities,
    string Formula,
    string Threshold,
    int MinimumSample,
    string? UnsupportedReason,
    string MitigationCode);

internal static class HistoricalEfficiencyDriverRegistryV1
{
    internal static IReadOnlyList<HistoricalEfficiencyDriverRuleV1> Rules { get; } =
    [
        Rule(HistoricalEfficiencyDriverCategoryV1.TokenVolume, "token-volume", ["token_rollup"],
            "session_total_gt_p75_and_ratio_gte_1_50", "relative_outlier_1_50x_median_above_nearest_rank_p75", 4, null, "review_high_token_sessions"),
        Rule(HistoricalEfficiencyDriverCategoryV1.ContextGrowth, "context-growth", ["turn_rollup", "token_rollup"],
            "maximal_nondecreasing_input_run_ratio_gte_1_75", "context_growth_ratio_gte_1_75", 3, null, "bound_conversation_context"),
        Rule(HistoricalEfficiencyDriverCategoryV1.CacheInefficiency, "cache-inefficiency", ["turn_rollup", "token_rollup", "cache_rollup"],
            "post_first_cache_read_ratio_lt_0_20", "cache_read_ratio_lt_0_20_after_10000_input_tokens", 3, null, "review_cache_reuse"),
        Rule(HistoricalEfficiencyDriverCategoryV1.RetryOverhead, "retry-overhead", ["retry_chain"],
            "distinct_retry_attempts_minus_one", "retry_attempt_count_gte_2", 2, null, "review_retry_trigger"),
        Rule(HistoricalEfficiencyDriverCategoryV1.ToolCallVolume, "tool-call-volume", ["repeated_tool_call"],
            "exact_repeated_call_count_gte_3", "repeated_exact_call_count_gte_3", 3, null, "review_repeated_tool_calls"),
        Rule(HistoricalEfficiencyDriverCategoryV1.ToolFailureOverhead, "tool-failure-overhead", [],
            "exact_tool_failure_ratio_unavailable", "unavailable_in_historical_evidence_v1", 0,
            "exact_tool_failure_status_unavailable", "review_tool_failure_evidence"),
        Rule(HistoricalEfficiencyDriverCategoryV1.PermissionWait, "permission-wait", ["permission_wait"],
            "maximum_gte_30_or_total_gte_60_seconds", "individual_wait_gte_30_or_total_wait_gte_60_seconds", 1, null, "review_permission_plan"),
        Rule(HistoricalEfficiencyDriverCategoryV1.SubagentFanout, "subagent-fanout", ["subagent_fan_out"],
            "distinct_exact_ownership_count_gte_2", "exact_ownership_count_gte_2", 2, null, "review_subagent_scope"),
        Rule(HistoricalEfficiencyDriverCategoryV1.DurationOutlier, "duration-outlier", [],
            "session_max_duration_gt_p75_and_ratio_gte_1_50", "relative_outlier_1_50x_median_above_nearest_rank_p75", 4, null, "review_duration_outlier"),
        Rule(HistoricalEfficiencyDriverCategoryV1.ModelMixObservation, "model-mix-observation", [],
            "distinct_model_ref_count_gte_2", "distinct_model_ref_count_gte_2", 2, null, "separate_model_cohorts"),
    ];

    private static HistoricalEfficiencyDriverRuleV1 Rule(
        HistoricalEfficiencyDriverCategoryV1 category,
        string slug,
        IReadOnlyList<string> requiredCapabilities,
        string formula,
        string threshold,
        int minimumSample,
        string? unsupportedReason,
        string mitigationCode) =>
        new(category, 1, $"historical-efficiency/{slug}@1", requiredCapabilities, formula, threshold,
            minimumSample, unsupportedReason, mitigationCode);

    internal static string Summary(HistoricalEfficiencyDriverCategoryV1 category) => category switch
    {
        HistoricalEfficiencyDriverCategoryV1.TokenVolume => "Historical token volume exceeded the frozen cohort threshold; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.ContextGrowth => "Historical input context grew across an exact turn run; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.CacheInefficiency => "Historical cache reuse was below the frozen threshold; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.RetryOverhead => "Historical exact retry evidence contains repeated attempts; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.ToolCallVolume => "Historical exact-call evidence contains repeated calls; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.PermissionWait => "Historical permission waits crossed the frozen threshold; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.SubagentFanout => "Historical exact ownership evidence shows subagent fan-out; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.DurationOutlier => "Historical duration exceeded the frozen cohort threshold; this observation does not establish improvement.",
        HistoricalEfficiencyDriverCategoryV1.ModelMixObservation => "Historical evidence spans multiple opaque model references; this observation does not establish improvement.",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    internal static string MitigationSummary(HistoricalEfficiencyDriverCategoryV1 category) => category switch
    {
        HistoricalEfficiencyDriverCategoryV1.TokenVolume => "Review high-token Sessions and their exact evidence references.",
        HistoricalEfficiencyDriverCategoryV1.ContextGrowth => "Review whether conversation context can be bounded at the cited turns.",
        HistoricalEfficiencyDriverCategoryV1.CacheInefficiency => "Review cache reuse behavior at the cited turns.",
        HistoricalEfficiencyDriverCategoryV1.RetryOverhead => "Review the trigger for the cited retry attempts.",
        HistoricalEfficiencyDriverCategoryV1.ToolCallVolume => "Review why the cited exact call was repeated.",
        HistoricalEfficiencyDriverCategoryV1.PermissionWait => "Review permission planning around the cited waits.",
        HistoricalEfficiencyDriverCategoryV1.SubagentFanout => "Review the scope represented by the cited ownership groups.",
        HistoricalEfficiencyDriverCategoryV1.DurationOutlier => "Review the cited duration observations in their exact cohort.",
        HistoricalEfficiencyDriverCategoryV1.ModelMixObservation => "Keep opaque model-reference cohorts separate when comparing observations.",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}

using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEfficiencyAnalysisRegistryTests
{
    [Fact]
    public void Rules_VersionOne_HasExactBindingOrderAndContracts()
    {
        var rules = HistoricalEfficiencyDriverRegistryV1.Rules;

        Assert.Equal("historical-efficiency-driver-registry.v1", HistoricalEfficiencyContractsV1.RegistryVersion);
        Assert.Equal(
        [
            HistoricalEfficiencyDriverCategoryV1.TokenVolume,
            HistoricalEfficiencyDriverCategoryV1.ContextGrowth,
            HistoricalEfficiencyDriverCategoryV1.CacheInefficiency,
            HistoricalEfficiencyDriverCategoryV1.RetryOverhead,
            HistoricalEfficiencyDriverCategoryV1.ToolCallVolume,
            HistoricalEfficiencyDriverCategoryV1.ToolFailureOverhead,
            HistoricalEfficiencyDriverCategoryV1.PermissionWait,
            HistoricalEfficiencyDriverCategoryV1.SubagentFanout,
            HistoricalEfficiencyDriverCategoryV1.DurationOutlier,
            HistoricalEfficiencyDriverCategoryV1.ModelMixObservation,
        ], rules.Select(rule => rule.Category));
        Assert.All(rules, rule => Assert.Equal(1, rule.DriverVersion));
        Assert.Equal(rules.Count, rules.Select(rule => rule.RuleSource).Distinct(StringComparer.Ordinal).Count());

        AssertRule(rules[0], ["token_rollup"], "session_total_gt_p75_and_ratio_gte_1_50",
            "relative_outlier_1_50x_median_above_nearest_rank_p75", 4, null, "review_high_token_sessions");
        AssertRule(rules[1], ["turn_rollup", "token_rollup"], "maximal_nondecreasing_input_run_ratio_gte_1_75",
            "context_growth_ratio_gte_1_75", 3, null, "bound_conversation_context");
        AssertRule(rules[2], ["turn_rollup", "token_rollup", "cache_rollup"], "post_first_cache_read_ratio_lt_0_20",
            "cache_read_ratio_lt_0_20_after_10000_input_tokens", 3, null, "review_cache_reuse");
        AssertRule(rules[3], ["retry_chain"], "distinct_retry_attempts_minus_one",
            "retry_attempt_count_gte_2", 1, null, "review_retry_trigger");
        AssertRule(rules[4], [], "producer_authored_repeated_call_identity_unavailable",
            "unavailable_in_historical_evidence_v1", 0,
            HistoricalEfficiencyCoverageReasonV1.ProducerAuthoredRepeatedCallIdentityUnavailable, "review_repeated_tool_calls");
        AssertRule(rules[5], [], "exact_tool_failure_ratio_unavailable",
            "unavailable_in_historical_evidence_v1", 0,
            HistoricalEfficiencyCoverageReasonV1.ExactToolFailureStatusUnavailable, "review_tool_failure_evidence");
        AssertRule(rules[6], [], "permission_wait_duration_unavailable",
            "unavailable_in_historical_evidence_v1", 0,
            HistoricalEfficiencyCoverageReasonV1.PermissionWaitDurationUnavailable, "review_permission_plan");
        AssertRule(rules[7], [], "exact_subagent_ownership_unavailable",
            "unavailable_in_historical_evidence_v1", 0,
            HistoricalEfficiencyCoverageReasonV1.ExactSubagentOwnershipUnavailable, "review_subagent_scope");
        AssertRule(rules[8], [], "session_max_duration_gt_p75_and_ratio_gte_1_50",
            "relative_outlier_1_50x_median_above_nearest_rank_p75", 4, null, "review_duration_outlier");
        AssertRule(rules[9], [], "distinct_model_ref_count_gte_2",
            "distinct_model_ref_count_gte_2", 2, null, "separate_model_cohorts");
    }

    private static void AssertRule(
        HistoricalEfficiencyDriverRuleV1 rule,
        string[] requiredCapabilities,
        string formula,
        string threshold,
        int minimumSample,
        HistoricalEfficiencyCoverageReasonV1? unsupportedReason,
        string mitigationCode)
    {
        Assert.Equal(requiredCapabilities, rule.RequiredCapabilities);
        Assert.Equal(formula, rule.Formula);
        Assert.Equal(threshold, rule.Threshold);
        Assert.Equal(minimumSample, rule.MinimumSample);
        Assert.Equal(unsupportedReason, rule.UnsupportedReason);
        Assert.Equal(mitigationCode, rule.MitigationCode);
    }
}

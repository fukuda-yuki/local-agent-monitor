namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementProposalSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ImprovementProposalRow> rows)
    {
        var errors = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowNumber = index + 1;
            foreach (var field in GetStringFields(row))
            {
                if (!string.IsNullOrWhiteSpace(field.Value)
                    && DiagnosisValidator.ContainsUnsafeMaterial(field.Value))
                {
                    errors.Add($"row {rowNumber}: proposal field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(ImprovementProposalRow row)
    {
        yield return ("proposal_id", row.ProposalId);
        yield return ("trace_id", row.TraceId);
        yield return ("task_id", row.TaskId);
        yield return ("task_category", row.TaskCategory);
        yield return ("client_kind", row.ClientKind);
        yield return ("comparison_id", row.ComparisonId);
        yield return ("experiment_id", row.ExperimentId);
        yield return ("experiment_condition", row.ExperimentCondition);
        yield return ("prompt_version", row.PromptVersion);
        yield return ("agent_variant", row.AgentVariant);
        yield return ("failure_category_id", row.FailureCategoryId);
        yield return ("anti_pattern_id", row.AntiPatternId);
        yield return ("severity", row.Severity);
        yield return ("improvement_target", row.ImprovementTarget);
        yield return ("evidence_summary", row.EvidenceSummary);
        yield return ("proposal_title", row.ProposalTitle);
        yield return ("proposal_summary", row.ProposalSummary);
        yield return ("proposed_change", row.ProposedChange);
        yield return ("acceptance_check", row.AcceptanceCheck);
        yield return ("human_review_status", row.HumanReviewStatus);
    }
}

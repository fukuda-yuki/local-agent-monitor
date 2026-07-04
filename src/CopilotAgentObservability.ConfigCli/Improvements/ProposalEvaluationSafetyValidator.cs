namespace CopilotAgentObservability.ConfigCli;

internal static class ProposalEvaluationSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ProposalEvaluationRow> rows)
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
                    errors.Add($"row {rowNumber}: proposal evaluation field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(ProposalEvaluationRow row)
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
        yield return ("proposal_title", row.ProposalTitle);
        yield return ("proposal_evaluation_status", row.ProposalEvaluationStatus);
        yield return ("evaluator_findings", row.EvaluatorFindings);
        yield return ("required_human_checks", row.RequiredHumanChecks);
        yield return ("evaluator_notes", row.EvaluatorNotes);
    }
}

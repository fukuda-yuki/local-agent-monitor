namespace CopilotAgentObservability.ConfigCli;

internal static class HumanDecisionValidator
{
    private static readonly HashSet<string> ValidDecisions = new(StringComparer.Ordinal)
    {
        "approved",
        "rejected",
        "deferred",
    };

    public static IReadOnlyList<string> Validate(
        IReadOnlyList<HumanDecisionRow> decisions,
        IReadOnlyList<ProposalEvaluationRow> evaluations)
    {
        var errors = new List<string>();
        var evaluationsByProposalId = evaluations
            .ToDictionary(evaluation => evaluation.ProposalId, StringComparer.Ordinal);

        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            var rowNumber = index + 1;

            if (string.IsNullOrWhiteSpace(decision.ProposalId))
            {
                errors.Add($"row {rowNumber}: proposal_id is required.");
            }

            if (string.IsNullOrWhiteSpace(decision.HumanDecision))
            {
                errors.Add($"row {rowNumber}: human_decision is required.");
            }
            else if (!ValidDecisions.Contains(decision.HumanDecision))
            {
                errors.Add($"row {rowNumber}: human_decision '{decision.HumanDecision}' is not allowed. Must be 'approved', 'rejected', or 'deferred'.");
            }

            if (string.IsNullOrWhiteSpace(decision.DecisionRationale))
            {
                errors.Add($"row {rowNumber}: decision_rationale is required.");
            }

            if (!string.IsNullOrWhiteSpace(decision.ProposalId))
            {
                if (!evaluationsByProposalId.TryGetValue(decision.ProposalId, out var evaluation))
                {
                    errors.Add($"row {rowNumber}: proposal_id '{decision.ProposalId}' not found in evaluations.");
                }
                else if (string.Equals(decision.HumanDecision, "approved", StringComparison.Ordinal)
                    && !string.Equals(evaluation.ProposalEvaluationStatus, "ready-for-human-approval", StringComparison.Ordinal))
                {
                    errors.Add($"row {rowNumber}: cannot approve proposal '{decision.ProposalId}' with evaluation status '{evaluation.ProposalEvaluationStatus}'. Only 'ready-for-human-approval' proposals can be approved.");
                }
            }
        }

        return errors;
    }
}

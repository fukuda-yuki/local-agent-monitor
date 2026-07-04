namespace CopilotAgentObservability.ConfigCli;

internal static class DecisionTemplateGenerator
{
    public static IReadOnlyList<HumanDecisionRow> Generate(IReadOnlyList<ProposalEvaluationRow> evaluations)
    {
        var rows = new List<HumanDecisionRow>();
        foreach (var evaluation in evaluations)
        {
            if (!string.Equals(evaluation.ProposalEvaluationStatus, "ready-for-human-approval", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new HumanDecisionRow(
                ProposalId: evaluation.ProposalId,
                HumanDecision: string.Empty,
                DecisionRationale: string.Empty,
                ApproverId: null,
                ApprovedAt: null,
                ConditionsOrNotes: null));
        }

        return rows;
    }
}

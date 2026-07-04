namespace CopilotAgentObservability.ConfigCli;

internal static class HumanDecisionSafetyValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<HumanDecisionRow> rows)
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
                    errors.Add($"row {rowNumber}: decision field '{field.Name}' appears to contain raw content, credential, secret, token, or identity-bearing material.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<(string Name, string? Value)> GetStringFields(HumanDecisionRow row)
    {
        yield return ("proposal_id", row.ProposalId);
        yield return ("human_decision", row.HumanDecision);
        yield return ("decision_rationale", row.DecisionRationale);
        yield return ("approver_id", row.ApproverId);
        yield return ("approved_at", row.ApprovedAt);
        yield return ("conditions_or_notes", row.ConditionsOrNotes);
    }
}

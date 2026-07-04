namespace CopilotAgentObservability.ConfigCli;

internal sealed record HumanDecisionRow(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("human_decision")] string HumanDecision,
    [property: JsonPropertyName("decision_rationale")] string DecisionRationale,
    [property: JsonPropertyName("approver_id")] string? ApproverId,
    [property: JsonPropertyName("approved_at")] string? ApprovedAt,
    [property: JsonPropertyName("conditions_or_notes")] string? ConditionsOrNotes);

namespace CopilotAgentObservability.ConfigCli;

internal static class HumanDecisionOutputWriter
{
    public static readonly string[] Columns =
    [
        "proposal_id",
        "human_decision",
        "decision_rationale",
        "approver_id",
        "approved_at",
        "conditions_or_notes",
    ];

    public static string WriteJson(IReadOnlyList<HumanDecisionRow> rows)
    {
        return JsonOutput.WriteIndented(rows);
    }

    public static string WriteCsv(IReadOnlyList<HumanDecisionRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', Columns));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Columns.Select(column => CsvEscaper.Escape(GetValue(row, column)))));
        }

        return builder.ToString();
    }

    private static string? GetValue(HumanDecisionRow row, string column)
    {
        return column switch
        {
            "proposal_id" => row.ProposalId,
            "human_decision" => row.HumanDecision,
            "decision_rationale" => row.DecisionRationale,
            "approver_id" => row.ApproverId,
            "approved_at" => row.ApprovedAt,
            "conditions_or_notes" => row.ConditionsOrNotes,
            _ => null,
        };
    }

}

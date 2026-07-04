namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementProposalEvaluator
{
    private const string ReadyForHumanApproval = "ready-for-human-approval";
    private const string NeedsRevision = "needs-revision";
    private const string Blocked = "blocked";

    public static IReadOnlyList<ProposalEvaluationRow> Evaluate(IReadOnlyList<ImprovementProposalRow> proposals)
    {
        var rows = new List<ProposalEvaluationRow>();
        foreach (var proposal in proposals)
        {
            var combinedText = string.Join(
                ' ',
                proposal.ProposalTitle,
                proposal.ProposalSummary,
                proposal.ProposedChange,
                proposal.AcceptanceCheck);

            string status;
            string findings;
            string requiredHumanChecks;
            string notes;

            if (ContainsOutOfScopeAction(combinedText))
            {
                status = Blocked;
                findings = "Proposal text appears to request out-of-scope automated implementation or decision.";
                requiredHumanChecks = "Revise the proposal so it remains a human-reviewed recommendation only.";
                notes = "Blocked before human approval workflow.";
            }
            else if (!ContainsHumanReviewIntent(combinedText)
                || string.IsNullOrWhiteSpace(proposal.TaskCategory)
                || string.IsNullOrWhiteSpace(proposal.ClientKind))
            {
                status = NeedsRevision;
                findings = "Proposal needs clearer review context before approval.";
                requiredHumanChecks = "Confirm task category, client kind, target, sanitized evidence, and approval boundaries.";
                notes = "No adoption or repository modification is performed.";
            }
            else
            {
                status = ReadyForHumanApproval;
                findings = "Proposal is schema-valid and limited to human review.";
                requiredHumanChecks = "Confirm target, sanitized evidence, and non-scope boundaries before approval.";
                notes = "No automatic adoption or repository modification is performed.";
            }

            rows.Add(new ProposalEvaluationRow(
                ProposalId: proposal.ProposalId,
                SourceDiagnosisIndex: proposal.SourceDiagnosisIndex,
                TraceId: proposal.TraceId,
                TaskId: proposal.TaskId,
                TaskCategory: proposal.TaskCategory,
                ClientKind: proposal.ClientKind,
                ComparisonId: proposal.ComparisonId,
                ExperimentId: proposal.ExperimentId,
                ExperimentCondition: proposal.ExperimentCondition,
                PromptVersion: proposal.PromptVersion,
                AgentVariant: proposal.AgentVariant,
                TaskRunIndex: proposal.TaskRunIndex,
                FailureCategoryId: proposal.FailureCategoryId,
                AntiPatternId: proposal.AntiPatternId,
                Severity: proposal.Severity,
                ImprovementTarget: proposal.ImprovementTarget,
                ProposalTitle: proposal.ProposalTitle,
                ProposalEvaluationStatus: status,
                EvaluatorFindings: findings,
                RequiredHumanChecks: requiredHumanChecks,
                EvaluatorNotes: notes));
        }

        return rows;
    }

    private static bool ContainsHumanReviewIntent(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("human", StringComparison.Ordinal)
            && (normalized.Contains("review", StringComparison.Ordinal)
                || normalized.Contains("approval", StringComparison.Ordinal));
    }

    private static bool ContainsOutOfScopeAction(string value)
    {
        var normalized = value.ToLowerInvariant();
        string[] blockedPhrases =
        [
            "automatically adopt",
            "auto-adopt",
            "auto adopt",
            "adopt this proposal",
            "adopt the proposal",
            "auto-merge",
            "auto merge",
            "generate patch",
            "create patch",
            "apply patch",
            "generate diff",
            "create diff",
            "apply diff",
            "edit repository",
            "edit repositories",
            "modify repository",
            "modify repositories",
            "make repository changes",
            "create commit",
            "commit changes",
            "push branch",
            "push to remote",
            "create pr",
            "open pr",
            "submit pr",
            "raise a pr",
            "open pull request",
            "create pull request",
            "submit pull request",
            "raise a pull request",
            "decide winner",
            "select winner",
            "win/loss",
            "evaluate improvement effectiveness",
            "judge improvement effect",
            "determine improvement effect",
            "implement automatically",
            "automatically implement",
            "implement this improvement",
            "implement the improvement",
        ];

        return blockedPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }
}

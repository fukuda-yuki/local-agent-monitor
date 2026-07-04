namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementProposalGenerator
{
    private const string AcceptedForProposal = "accepted-for-proposal";
    private const string HumanReviewStatus = "needs-human-review";

    public static IReadOnlyList<ImprovementProposalRow> Generate(IReadOnlyList<DiagnosisRow> diagnoses)
    {
        var proposals = new List<ImprovementProposalRow>();
        for (var index = 0; index < diagnoses.Count; index++)
        {
            var diagnosis = diagnoses[index];
            if (!string.Equals(diagnosis.ReviewStatus, AcceptedForProposal, StringComparison.Ordinal))
            {
                continue;
            }

            var sourceDiagnosisIndex = index + 1;
            var proposalId = $"proposal-{proposals.Count + 1:0000}";
            var descriptor = CreateDescriptor(diagnosis);
            var target = diagnosis.RecommendedImprovementTarget ?? "unknown";

            proposals.Add(new ImprovementProposalRow(
                ProposalId: proposalId,
                SourceDiagnosisIndex: sourceDiagnosisIndex,
                TraceId: diagnosis.TraceId,
                TaskId: diagnosis.TaskId,
                TaskCategory: diagnosis.TaskCategory,
                ClientKind: diagnosis.ClientKind,
                ComparisonId: diagnosis.ComparisonId,
                ExperimentId: diagnosis.ExperimentId,
                ExperimentCondition: diagnosis.ExperimentCondition,
                PromptVersion: diagnosis.PromptVersion,
                AgentVariant: diagnosis.AgentVariant,
                TaskRunIndex: diagnosis.TaskRunIndex,
                FailureCategoryId: diagnosis.FailureCategoryId,
                AntiPatternId: diagnosis.AntiPatternId,
                Severity: diagnosis.Severity,
                ImprovementTarget: diagnosis.RecommendedImprovementTarget,
                EvidenceSummary: diagnosis.EvidenceSummary,
                ProposalTitle: $"Review {target} improvement for {descriptor}",
                ProposalSummary: $"A {diagnosis.Severity} diagnosis for {descriptor} was accepted for proposal. Evidence: {diagnosis.EvidenceSummary}",
                ProposedChange: $"Prepare a human-reviewed {target} improvement that addresses {descriptor} using only the sanitized evidence summary.",
                AcceptanceCheck: $"A human reviewer can confirm the {target} proposal addresses {descriptor} without automatic adoption, repository edits, patches, diffs, or sensitive material exposure.",
                HumanReviewStatus: HumanReviewStatus));
        }

        return proposals;
    }

    private static string CreateDescriptor(DiagnosisRow diagnosis)
    {
        return string.IsNullOrWhiteSpace(diagnosis.AntiPatternId)
            ? diagnosis.FailureCategoryId ?? "unknown diagnosis"
            : $"{diagnosis.FailureCategoryId} / {diagnosis.AntiPatternId}";
    }
}

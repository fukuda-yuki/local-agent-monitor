namespace CopilotAgentObservability.ConfigCli;

internal static class ImprovementCandidateGenerator
{
    private const string Blocked = "blocked";

    public static IReadOnlyList<ImprovementCandidateRow> Generate(IReadOnlyList<DiagnosisCandidateRow> diagnoses)
    {
        var candidates = new List<ImprovementCandidateRow>();
        foreach (var diagnosis in diagnoses)
        {
            if (string.Equals(diagnosis.CandidateStatus, Blocked, StringComparison.Ordinal))
            {
                continue;
            }

            var descriptor = CreateDescriptor(diagnosis.FailureCategoryId, diagnosis.AntiPatternId);
            var target = diagnosis.RecommendedImprovementTarget;

            candidates.Add(new ImprovementCandidateRow(
                ImprovementCandidateId: $"impcand-{candidates.Count + 1:0000}",
                SourceDiagnosisCandidateId: diagnosis.DiagnosisCandidateId,
                TraceId: diagnosis.TraceId,
                FailureCategoryId: diagnosis.FailureCategoryId,
                AntiPatternId: diagnosis.AntiPatternId,
                Severity: diagnosis.Severity,
                ImprovementTarget: target,
                ProposalTitle: $"Review {target} candidate for {descriptor}",
                ProposalSummary: $"A {diagnosis.Severity} diagnosis candidate for {descriptor} suggests a {target} improvement. Evidence ref: {diagnosis.EvidenceRef}",
                ProposedChangeKind: target,
                EvidenceRef: diagnosis.EvidenceRef,
                SensitiveBundlePath: diagnosis.SensitiveBundlePath,
                CandidateStatus: diagnosis.CandidateStatus));
        }

        return candidates;
    }

    private static string CreateDescriptor(string failureCategoryId, string? antiPatternId)
    {
        return string.IsNullOrWhiteSpace(antiPatternId)
            ? failureCategoryId
            : $"{failureCategoryId} / {antiPatternId}";
    }
}

internal static class AutoDecisionGenerator
{
    private static readonly HashSet<string> AllowedTargets = new(StringComparer.Ordinal)
    {
        "prompt",
        "instruction",
        "skill",
        "tool schema",
        "workflow",
        "eval",
    };

    private static readonly string[] ScopeOverreachPhrases =
    [
        "repository file",
        "repository changes",
        "modify files",
        "modify repository",
        "modify repositories",
        "edit files",
        "edit repository",
        "edit repositories",
        "apply patch",
        "create patch",
        "generate patch",
        "patch",
        "apply diff",
        "create diff",
        "generate diff",
        "diff",
        "commit",
        "push",
        "pull request",
        "create pr",
        "open pr",
        "submit pr",
        "raise a pr",
        "merge",
        "auto-merge",
        "auto merge",
        "winner",
        "win/loss",
        "automatic winner",
        "auto decide experiment",
        "live service",
        "network endpoint",
        "production data",
    ];

    public static IReadOnlyList<AutoDecisionRow> Generate(IReadOnlyList<ImprovementCandidateRow> candidates)
    {
        var decisions = new List<AutoDecisionRow>();
        foreach (var candidate in candidates)
        {
            decisions.Add(CreateDecision(candidate, decisions.Count + 1));
        }

        return decisions;
    }

    private static AutoDecisionRow CreateDecision(ImprovementCandidateRow candidate, int sequence)
    {
        var target = candidate.ProposedChangeKind;
        var sensitiveContentIncluded = !string.IsNullOrWhiteSpace(candidate.SensitiveBundlePath);
        var hasScopeOverreach = IsScopeOverreach(candidate) || !AllowedTargets.Contains(target);

        if (string.Equals(candidate.CandidateStatus, "blocked", StringComparison.Ordinal) || hasScopeOverreach)
        {
            var reason = string.Equals(candidate.CandidateStatus, "blocked", StringComparison.Ordinal)
                ? "The improvement candidate is already blocked."
                : "The improvement candidate requests work outside the Sprint3 auto-decision boundary.";

            return Row(
                candidate,
                sequence,
                decisionStatus: "blocked",
                decisionRuleId: "DEC-BLOCK-SCOPE-OVERREACH-V1",
                decisionReason: reason,
                confidence: "high",
                blockingRiskChecks: hasScopeOverreach ? "scope-overreach" : "candidate-status-blocked",
                sensitiveContentIncluded: sensitiveContentIncluded,
                implementationTarget: target,
                nextAction: "do-not-implement");
        }

        if (sensitiveContentIncluded)
        {
            return Row(
                candidate,
                sequence,
                decisionStatus: "needs-human-review",
                decisionRuleId: "DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1",
                decisionReason: "Sensitive bundle reference requires human review before any follow-up.",
                confidence: "medium",
                blockingRiskChecks: "sensitive-content",
                sensitiveContentIncluded: true,
                implementationTarget: target,
                nextAction: "request-human-review");
        }

        if (IsSafeSeverity(candidate.Severity) && AllowedTargets.Contains(target))
        {
            return Row(
                candidate,
                sequence,
                decisionStatus: "auto-approved",
                decisionRuleId: "DEC-AUTO-APPROVE-SAFE-METADATA-V1",
                decisionReason: "Candidate uses sanitized metadata, allowed severity, and an allowed Sprint3 target.",
                confidence: "high",
                blockingRiskChecks: string.Empty,
                sensitiveContentIncluded: false,
                implementationTarget: target,
                nextAction: "record-for-sprint4-planning");
        }

        return Row(
            candidate,
            sequence,
            decisionStatus: "needs-human-review",
            decisionRuleId: "DEC-HUMAN-REVIEW-DEFAULT-V1",
            decisionReason: "Candidate does not satisfy the safe metadata auto-approval rule.",
            confidence: "low",
            blockingRiskChecks: "default-human-review",
            sensitiveContentIncluded: false,
            implementationTarget: target,
            nextAction: "request-human-review");
    }

    private static AutoDecisionRow Row(
        ImprovementCandidateRow candidate,
        int sequence,
        string decisionStatus,
        string decisionRuleId,
        string decisionReason,
        string confidence,
        string blockingRiskChecks,
        bool sensitiveContentIncluded,
        string implementationTarget,
        string nextAction)
    {
        return new AutoDecisionRow(
            AutoDecisionId: $"autodec-{sequence:0000}",
            SourceImprovementCandidateId: candidate.ImprovementCandidateId,
            SourceDiagnosisCandidateId: candidate.SourceDiagnosisCandidateId,
            TraceId: candidate.TraceId,
            DecisionStatus: decisionStatus,
            DecisionRuleId: decisionRuleId,
            DecisionReason: decisionReason,
            Confidence: confidence,
            BlockingRiskChecks: blockingRiskChecks,
            SensitiveContentIncluded: sensitiveContentIncluded,
            SensitiveBundlePath: candidate.SensitiveBundlePath,
            ImplementationTarget: implementationTarget,
            NextAction: nextAction);
    }

    private static bool IsSafeSeverity(string severity)
    {
        return string.Equals(severity, "minor", StringComparison.Ordinal)
            || string.Equals(severity, "major", StringComparison.Ordinal);
    }

    private static bool IsScopeOverreach(ImprovementCandidateRow candidate)
    {
        var text = string.Join(
            '\n',
            candidate.ProposalTitle,
            candidate.ProposalSummary,
            candidate.ProposedChangeKind,
            candidate.ImprovementTarget);
        var normalized = text.ToLowerInvariant();
        return ScopeOverreachPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }
}

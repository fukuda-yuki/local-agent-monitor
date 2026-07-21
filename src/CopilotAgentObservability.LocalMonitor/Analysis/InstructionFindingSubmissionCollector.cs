namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class InstructionFindingSubmissionCollectorV1
{
    private readonly long analysisRunId;
    private readonly InstructionFindingEvidenceIndexV1 evidenceIndex;
    private readonly object gate = new();
    private readonly List<InstructionFindingDraftV1> drafts = [];

    internal InstructionFindingSubmissionCollectorV1(
        long analysisRunId,
        InstructionFindingEvidenceIndexV1 evidenceIndex)
    {
        this.analysisRunId = analysisRunId;
        this.evidenceIndex = evidenceIndex;
    }

    internal string SubmitWire(
        string category,
        string verdict,
        string extractorSource,
        string evidenceRefsJson)
    {
        try
        {
            var draft = new InstructionFindingDraftV1(
                ParseCategory(category),
                ParseVerdict(verdict),
                ParseExtractorSource(extractorSource),
                InstructionFindingJsonV1.DeserializeEvidenceReferences(evidenceRefsJson));
            _ = InstructionFindingPipelineV1.Generate(analysisRunId, evidenceIndex, [draft]);
            lock (gate)
            {
                drafts.Add(draft);
            }

            return "{\"status\":\"accepted\"}";
        }
        catch (InstructionFindingValidationException exception)
        {
            return $"{{\"status\":\"rejected\",\"code\":\"{exception.Code.ToWireValue()}\"}}";
        }
    }

    internal InstructionFindingHandoffV1 BuildHandoff()
    {
        InstructionFindingDraftV1[] snapshot;
        lock (gate)
        {
            snapshot = drafts.ToArray();
        }

        return InstructionFindingPipelineV1.Generate(analysisRunId, evidenceIndex, snapshot);
    }

    private static InstructionFindingCategoryV1 ParseCategory(string value) => value switch
    {
        "goal_clarity" => InstructionFindingCategoryV1.GoalClarity,
        "ambiguity" => InstructionFindingCategoryV1.Ambiguity,
        "acceptance_criteria_missing" => InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
        "scope_boundary_missing" => InstructionFindingCategoryV1.ScopeBoundaryMissing,
        "task_too_large" => InstructionFindingCategoryV1.TaskTooLarge,
        "test_requirement_missing" => InstructionFindingCategoryV1.TestRequirementMissing,
        "evidence_requirement_missing" => InstructionFindingCategoryV1.EvidenceRequirementMissing,
        "environment_assumption_missing" => InstructionFindingCategoryV1.EnvironmentAssumptionMissing,
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    private static InstructionFindingVerdictV1 ParseVerdict(string value) => value switch
    {
        "supported" => InstructionFindingVerdictV1.Supported,
        "weak" => InstructionFindingVerdictV1.Weak,
        "incomplete" => InstructionFindingVerdictV1.Incomplete,
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    private static InstructionFindingExtractorSourceV1 ParseExtractorSource(string value) => value switch
    {
        "deterministic_prepass" => InstructionFindingExtractorSourceV1.DeterministicPrepass,
        "prompt_only" => InstructionFindingExtractorSourceV1.PromptOnly,
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };
}

internal static class InstructionFindingValidationWireV1
{
    internal static string ToWireValue(this InstructionFindingValidationCodeV1 code) => code switch
    {
        InstructionFindingValidationCodeV1.InvalidContract => "invalid_contract",
        InstructionFindingValidationCodeV1.UnresolvedEvidenceReference => "unresolved_evidence_reference",
        InstructionFindingValidationCodeV1.InvalidSerialization => "invalid_serialization",
        InstructionFindingValidationCodeV1.InvalidDerivedIdentity => "invalid_derived_identity",
        InstructionFindingValidationCodeV1.ConflictingPersistence => "conflicting_persistence",
        InstructionFindingValidationCodeV1.InvalidPersistence => "invalid_persistence",
        _ => "invalid_contract",
    };
}

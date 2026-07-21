using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class InstructionFindingContractsV1
{
    internal const string FindingSchemaVersion = "instruction-finding.v1";
    internal const string CandidateSchemaVersion = "instruction-rule-candidate.v1";
    internal const string HandoffSchemaVersion = "instruction-finding-handoff.v1";
    internal const string RuleTemplateVersion = "instruction-rule-template.v1";
}

internal enum InstructionFindingCategoryV1
{
    GoalClarity,
    Ambiguity,
    AcceptanceCriteriaMissing,
    ScopeBoundaryMissing,
    TaskTooLarge,
    TestRequirementMissing,
    EvidenceRequirementMissing,
    EnvironmentAssumptionMissing,
}

internal enum InstructionFindingVerdictV1
{
    Supported,
    Weak,
    Incomplete,
}

internal enum InstructionFindingExtractorSourceV1
{
    DeterministicPrepass,
    PromptOnly,
}

internal enum InstructionEvidenceRelativePositionV1
{
    Anchor,
    Previous,
    Following,
}

internal enum InstructionEvidenceQuoteStateV1
{
    RawLocalOnly,
}

internal enum InstructionCandidateEligibilityV1
{
    Eligible,
    Ineligible,
}

internal enum InstructionRuleTargetKindV1
{
    PromptInstruction,
}

internal enum InstructionRuleScopeHintV1
{
    Task,
    Repository,
}

internal enum InstructionFindingEvidenceKindV1
{
    Turn,
    ErrorOrRetrySpan,
    InstructionSpan,
}

internal enum InstructionFindingValidationCodeV1
{
    InvalidContract,
    UnresolvedEvidenceReference,
    InvalidSerialization,
    InvalidDerivedIdentity,
    ConflictingPersistence,
    InvalidPersistence,
}

internal sealed class InstructionFindingValidationException : Exception
{
    internal InstructionFindingValidationException(InstructionFindingValidationCodeV1 code)
        : base(code.ToString()) => Code = code;

    internal InstructionFindingValidationException(InstructionFindingValidationCodeV1 code, Exception innerException)
        : base(code.ToString(), innerException) => Code = code;

    internal InstructionFindingValidationCodeV1 Code { get; }
}

internal sealed record InstructionRawEvidenceReferenceV1(
    string? SessionId,
    string TraceId,
    string? SpanId,
    int? TurnIndex,
    InstructionEvidenceRelativePositionV1 RelativePosition);

internal sealed record InstructionEvidenceReferenceV1(
    [property: JsonPropertyOrder(0)] string? SessionId,
    [property: JsonPropertyOrder(1)] string TraceId,
    [property: JsonPropertyOrder(2)] string? SpanId,
    [property: JsonPropertyOrder(3)] int? TurnIndex,
    [property: JsonPropertyOrder(4)] InstructionEvidenceRelativePositionV1 RelativePosition);

internal sealed record InstructionFindingDraftV1(
    InstructionFindingCategoryV1 Category,
    InstructionFindingVerdictV1 AssessedVerdict,
    InstructionFindingExtractorSourceV1 ExtractorSource,
    IReadOnlyList<InstructionRawEvidenceReferenceV1> EvidenceRefs);

internal sealed record InstructionFindingReceiptV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string FindingId,
    [property: JsonPropertyOrder(2)] long AnalysisRunId,
    [property: JsonPropertyOrder(3)] InstructionFindingCategoryV1 Category,
    [property: JsonPropertyOrder(4)] InstructionFindingVerdictV1 Verdict,
    [property: JsonPropertyOrder(5)] InstructionFindingExtractorSourceV1 ExtractorSource,
    [property: JsonPropertyOrder(6)] string AnchorTraceId,
    [property: JsonPropertyOrder(7)] IReadOnlyList<InstructionEvidenceReferenceV1> EvidenceRefs,
    [property: JsonPropertyOrder(8)] InstructionEvidenceQuoteStateV1 EvidenceQuoteState,
    [property: JsonPropertyOrder(9)] string GapSummary,
    [property: JsonPropertyOrder(10)] string SuggestedInstruction,
    [property: JsonPropertyOrder(11)] InstructionCandidateEligibilityV1 CandidateEligibility);

internal sealed record InstructionRuleProvenanceV1(
    [property: JsonPropertyOrder(0)] long AnalysisRunId,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string> TraceRefs);

internal sealed record InstructionRuleCandidateV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string CandidateId,
    [property: JsonPropertyOrder(2)] string DeduplicationKey,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> SourceFindingIds,
    [property: JsonPropertyOrder(4)] string Title,
    [property: JsonPropertyOrder(5)] string RuleText,
    [property: JsonPropertyOrder(6)] InstructionRuleTargetKindV1 TargetKind,
    [property: JsonPropertyOrder(7)] string TargetHint,
    [property: JsonPropertyOrder(8)] InstructionRuleScopeHintV1 ScopeHint,
    [property: JsonPropertyOrder(9)] InstructionRuleProvenanceV1 Provenance);

internal sealed record InstructionFindingHandoffV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] long AnalysisRunId,
    [property: JsonPropertyOrder(2)] IReadOnlyList<InstructionFindingReceiptV1> Findings,
    [property: JsonPropertyOrder(3)] IReadOnlyList<InstructionRuleCandidateV1> Candidates);

internal sealed record InstructionFindingEvidenceLocationV1(
    string? SessionId,
    string TraceId,
    string? SpanId,
    int? TurnIndex,
    InstructionEvidenceRelativePositionV1 RelativePosition,
    InstructionFindingEvidenceKindV1 Kind)
{
    internal InstructionRawEvidenceReferenceV1 ToReference() =>
        new(SessionId, TraceId, SpanId, TurnIndex, RelativePosition);
}

internal sealed class InstructionFindingEvidenceIndexV1
{
    private readonly Dictionary<InstructionRawEvidenceReferenceV1, HashSet<InstructionFindingEvidenceKindV1>> kindsByReference;

    internal InstructionFindingEvidenceIndexV1(
        string anchorTraceId,
        IReadOnlyList<InstructionFindingEvidenceLocationV1> locations)
    {
        InstructionFindingContractValidationV1.ValidateRawId(anchorTraceId);
        AnchorTraceId = anchorTraceId;
        kindsByReference = new Dictionary<InstructionRawEvidenceReferenceV1, HashSet<InstructionFindingEvidenceKindV1>>();
        foreach (var location in locations)
        {
            InstructionFindingContractValidationV1.ValidateRawLocation(anchorTraceId, location);
            var reference = location.ToReference();
            if (!kindsByReference.TryGetValue(reference, out var kinds))
            {
                kinds = [];
                kindsByReference.Add(reference, kinds);
            }

            kinds.Add(location.Kind);
        }
    }

    internal string AnchorTraceId { get; }

    internal static InstructionFindingEvidenceIndexV1 FromInstructionEvidence(
        string anchorTraceId,
        InstructionEvidence evidence)
    {
        var locations = new List<InstructionFindingEvidenceLocationV1>();
        foreach (var turn in evidence.TurnTokens)
        {
            locations.Add(new(null, anchorTraceId, turn.SpanId, turn.TurnIndex, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn));
            locations.Add(new(null, anchorTraceId, null, turn.TurnIndex, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn));
        }

        foreach (var spanId in evidence.ErrorSpans.Select(error => error.SpanId)
                     .Concat(evidence.RetryChains.SelectMany(chain => chain.SpanIds))
                     .Where(spanId => spanId is not null)
                     .Distinct(StringComparer.Ordinal))
        {
            locations.Add(new(null, anchorTraceId, spanId, null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.ErrorOrRetrySpan));
        }

        if (evidence.UserInstruction?.SpanId is { } instructionSpanId)
        {
            locations.Add(new(null, anchorTraceId, instructionSpanId, null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.InstructionSpan));
        }

        foreach (var trace in evidence.ConversationContext?.Traces.Where(trace => !trace.IsAnalyzedTrace) ?? [])
        {
            var relativePosition = trace.RelativePosition < 0
                ? InstructionEvidenceRelativePositionV1.Previous
                : InstructionEvidenceRelativePositionV1.Following;
            for (var turnIndex = 1; turnIndex <= trace.TurnCount; turnIndex++)
            {
                locations.Add(new(null, trace.TraceId, null, turnIndex, relativePosition, InstructionFindingEvidenceKindV1.Turn));
            }

            foreach (var spanId in trace.ErrorSpanIds.Where(spanId => spanId is not null).Distinct(StringComparer.Ordinal))
            {
                locations.Add(new(null, trace.TraceId, spanId, null, relativePosition, InstructionFindingEvidenceKindV1.ErrorOrRetrySpan));
            }
        }

        return new InstructionFindingEvidenceIndexV1(anchorTraceId, locations);
    }

    internal bool TryResolve(
        InstructionRawEvidenceReferenceV1 reference,
        out IReadOnlySet<InstructionFindingEvidenceKindV1> kinds)
    {
        if (kindsByReference.TryGetValue(reference, out var resolved))
        {
            kinds = resolved;
            return true;
        }

        kinds = new HashSet<InstructionFindingEvidenceKindV1>();
        return false;
    }
}

internal static class InstructionFindingContractValidationV1
{
    internal static void ValidateRawId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
    }

    internal static void ValidateRawReference(string anchorTraceId, InstructionRawEvidenceReferenceV1 reference)
    {
        if (!Enum.IsDefined(reference.RelativePosition))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        if (reference.SessionId is not null) ValidateRawId(reference.SessionId);
        ValidateRawId(reference.TraceId);
        if (reference.SpanId is not null) ValidateRawId(reference.SpanId);
        if (reference.SpanId is null && reference.TurnIndex is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        if (reference.TurnIndex is <= 0)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        if (reference.RelativePosition == InstructionEvidenceRelativePositionV1.Anchor
            != string.Equals(reference.TraceId, anchorTraceId, StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
    }

    internal static void ValidateSafeReference(string anchorTraceId, InstructionEvidenceReferenceV1 reference)
    {
        if (!Enum.IsDefined(reference.RelativePosition)
            || reference.SessionId is not null && !InstructionFindingReferenceTokenizationV1.IsSessionReference(reference.SessionId)
            || !InstructionFindingReferenceTokenizationV1.IsTraceReference(reference.TraceId)
            || reference.SpanId is not null && !InstructionFindingReferenceTokenizationV1.IsSpanReference(reference.SpanId)
            || reference.SpanId is null && reference.TurnIndex is null
            || reference.TurnIndex is <= 0
            || reference.RelativePosition == InstructionEvidenceRelativePositionV1.Anchor
                != string.Equals(reference.TraceId, anchorTraceId, StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
    }

    internal static void ValidateRawLocation(string anchorTraceId, InstructionFindingEvidenceLocationV1 location)
    {
        if (!Enum.IsDefined(location.Kind))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        ValidateRawReference(anchorTraceId, location.ToReference());
        if (location.Kind == InstructionFindingEvidenceKindV1.Turn && location.TurnIndex is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        if (location.Kind is InstructionFindingEvidenceKindV1.ErrorOrRetrySpan or InstructionFindingEvidenceKindV1.InstructionSpan
            && location.SpanId is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
    }

}

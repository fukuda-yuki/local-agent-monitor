namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class InstructionFindingEvidenceIndexFactoryV1
{
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
}

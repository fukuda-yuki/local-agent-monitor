using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class HistoricalAnalysisEvidenceResolverV1
{
    internal HistoricalAnalysisEvidenceResolveResponseV1 Resolve(
        HistoricalEvidenceExtractionV1 extraction,
        IReadOnlyList<string> references)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(references);

        var candidates = BuildCandidates(extraction);
        return new(
            HistoricalAnalysisContractsV1.EvidenceResolveResponseSchemaVersion,
            references.Select(reference => ResolveOne(reference, candidates)).ToArray());
    }

    private static HistoricalAnalysisEvidenceResolutionV1 ResolveOne(
        string reference,
        IReadOnlyDictionary<string, HashSet<Candidate>> candidates)
    {
        if (!candidates.TryGetValue(reference, out var matches) || matches.Count == 0)
            return new(reference, "missing", "not_applicable", null);
        if (matches.Count != 1)
            return new(reference, "unresolved", "not_applicable", null);

        var match = matches.Single();
        var state = match.ContentState == "expired_pending_deletion" ? "expired" : "resolved";
        return new(reference, state, match.ContentState, match.Target);
    }

    private static IReadOnlyDictionary<string, HashSet<Candidate>> BuildCandidates(
        HistoricalEvidenceExtractionV1 extraction)
    {
        var candidates = new Dictionary<string, HashSet<Candidate>>(StringComparer.Ordinal);
        for (var index = 0; index < extraction.RepositorySafe.Sessions.Count; index++)
        {
            var safe = extraction.RepositorySafe.Sessions[index];
            var raw = extraction.RawLocal.Sessions[index];
            Add(
                candidates,
                safe.SessionId,
                new(
                    $"/diagnostics?session_id={Uri.EscapeDataString(raw.SessionId)}",
                    SessionWire.ToWire(safe.ContentState)));
        }

        for (var groupIndex = 0; groupIndex < extraction.RepositorySafe.EvidenceGroups.Count; groupIndex++)
        {
            var safeGroup = extraction.RepositorySafe.EvidenceGroups[groupIndex];
            var rawGroup = extraction.RawLocal.EvidenceGroups[groupIndex];
            var safeReferences = safeGroup.References.ToHashSet();
            foreach (var raw in rawGroup.References)
            {
                var tokenized = InstructionFindingReferenceTokenizationV1.Tokenize(new(
                    raw.SessionId,
                    raw.TraceId,
                    raw.SpanId,
                    raw.TurnIndex,
                    (InstructionEvidenceRelativePositionV1)(int)raw.RelativePosition));
                var safe = new HistoricalEvidenceReferenceV1(
                    tokenized.SessionId!,
                    tokenized.TraceId,
                    tokenized.SpanId,
                    tokenized.TurnIndex,
                    raw.RelativePosition);
                if (!safeReferences.Contains(safe))
                    throw new HistoricalEvidenceValidationException(
                        HistoricalEvidenceValidationCodeV1.InvalidPersistence);
                Add(
                    candidates,
                    safe.TraceId,
                    new($"/traces/{Uri.EscapeDataString(raw.TraceId)}", "not_applicable"));
                if (safe.SpanId is not null && raw.SpanId is not null)
                {
                    Add(
                        candidates,
                        safe.SpanId,
                        new(
                            $"/traces/{Uri.EscapeDataString(raw.TraceId)}?span={Uri.EscapeDataString(raw.SpanId)}",
                            "not_applicable"));
                }
            }
        }
        return candidates;
    }

    private static void Add(
        IDictionary<string, HashSet<Candidate>> candidates,
        string reference,
        Candidate candidate)
    {
        if (!candidates.TryGetValue(reference, out var matches))
        {
            matches = [];
            candidates.Add(reference, matches);
        }
        matches.Add(candidate);
    }

    private sealed record Candidate(string Target, string ContentState);
}

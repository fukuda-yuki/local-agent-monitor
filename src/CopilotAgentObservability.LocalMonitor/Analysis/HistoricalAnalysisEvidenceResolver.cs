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
            AddSessionCandidate(candidates, raw.SessionId, safe.SessionId, safe.ContentState);
            AddMetadataCandidates(candidates, raw.Metadata, safe.Metadata);
        }

        AddExcludedSessionCandidates(candidates, extraction);

        for (var groupIndex = 0; groupIndex < extraction.RepositorySafe.EvidenceGroups.Count; groupIndex++)
        {
            var safeGroup = extraction.RepositorySafe.EvidenceGroups[groupIndex];
            var rawGroup = extraction.RawLocal.EvidenceGroups[groupIndex];
            var safeReferences = safeGroup.References.ToHashSet();
            foreach (var raw in rawGroup.References)
            {
                var safe = TokenizeReference(raw);
                if (!safeReferences.Contains(safe))
                    throw new HistoricalEvidenceValidationException(
                        HistoricalEvidenceValidationCodeV1.InvalidPersistence);
                AddReferenceCandidates(candidates, raw, safe);
            }
        }
        return candidates;
    }

    private static void AddMetadataCandidates(
        IDictionary<string, HashSet<Candidate>> candidates,
        HistoricalDecisionMetadataV1 raw,
        HistoricalDecisionMetadataV1 safe)
    {
        var safeModels = safe.ModelObservations.ToHashSet();
        foreach (var observation in raw.ModelObservations)
        {
            var safeReference = TokenizeReference(observation.EvidenceRef);
            var safeObservation = new HistoricalModelObservationV1(
                HistoricalEvidenceExtractorV1.TokenizeLabel("model", observation.Model)!,
                safeReference);
            if (!safeModels.Contains(safeObservation))
                throw new HistoricalEvidenceValidationException(
                    HistoricalEvidenceValidationCodeV1.InvalidPersistence);
            AddReferenceCandidates(candidates, observation.EvidenceRef, safeReference);
        }

        var safeDurations = safe.DurationObservations.ToHashSet();
        foreach (var observation in raw.DurationObservations)
        {
            var safeReference = TokenizeReference(observation.EvidenceRef);
            var safeObservation = new HistoricalDurationObservationV1(
                observation.DurationMs,
                safeReference);
            if (!safeDurations.Contains(safeObservation))
                throw new HistoricalEvidenceValidationException(
                    HistoricalEvidenceValidationCodeV1.InvalidPersistence);
            AddReferenceCandidates(candidates, observation.EvidenceRef, safeReference);
        }
    }

    private static void AddExcludedSessionCandidates(
        IDictionary<string, HashSet<Candidate>> candidates,
        HistoricalEvidenceExtractionV1 extraction)
    {
        for (var index = 0; index < extraction.RepositorySafe.ExcludedSessions.Count; index++)
        {
            var safe = extraction.RepositorySafe.ExcludedSessions[index];
            var raw = extraction.RawLocal.ExcludedSessions[index];
            if (safe.Reason is not HistoricalSessionExclusionReasonV1.FilterMismatch
                and not HistoricalSessionExclusionReasonV1.WindowTruncated)
                continue;
            if (raw.Metadata is null || safe.Metadata is null)
                throw new HistoricalEvidenceValidationException(
                    HistoricalEvidenceValidationCodeV1.InvalidPersistence);
            AddSessionCandidate(
                candidates,
                raw.SessionId,
                safe.SessionId,
                safe.Metadata.ContentState);
        }
    }

    private static void AddSessionCandidate(
        IDictionary<string, HashSet<Candidate>> candidates,
        string rawSessionId,
        string safeSessionId,
        SessionContentState contentState) =>
        Add(
            candidates,
            safeSessionId,
            new(
                $"/diagnostics?session_id={Uri.EscapeDataString(rawSessionId)}",
                SessionWire.ToWire(contentState)));

    private static void AddReferenceCandidates(
        IDictionary<string, HashSet<Candidate>> candidates,
        HistoricalEvidenceReferenceV1 raw,
        HistoricalEvidenceReferenceV1 safe)
    {
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

    private static HistoricalEvidenceReferenceV1 TokenizeReference(
        HistoricalEvidenceReferenceV1 raw)
    {
        var tokenized = InstructionFindingReferenceTokenizationV1.Tokenize(new(
            raw.SessionId,
            raw.TraceId,
            raw.SpanId,
            raw.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)raw.RelativePosition));
        return new(
            tokenized.SessionId!,
            tokenized.TraceId,
            tokenized.SpanId,
            tokenized.TurnIndex,
            raw.RelativePosition);
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

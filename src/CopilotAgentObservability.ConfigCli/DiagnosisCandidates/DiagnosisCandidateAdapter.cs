namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisCandidateAdapter
{
    public static IReadOnlyList<DiagnosisRow> Adapt(
        IReadOnlyList<DiagnosisCandidateRow> candidates,
        IReadOnlyList<MeasurementInputRow> measurements)
    {
        var measurementsByTraceId = measurements
            .Where(measurement => !string.IsNullOrWhiteSpace(measurement.Row.TraceId))
            .GroupBy(measurement => measurement.Row.TraceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return candidates
            .Select(candidate => Adapt(candidate, FindMeasurement(candidate, measurementsByTraceId)))
            .ToArray();
    }

    private static DiagnosisRow Adapt(DiagnosisCandidateRow candidate, MeasurementInputRow? measurement)
    {
        return new DiagnosisRow(
            TraceId: string.IsNullOrWhiteSpace(candidate.TraceId) ? $"missing-trace-{candidate.DiagnosisCandidateId}" : candidate.TraceId,
            TaskId: measurement?.Row.TaskId,
            TaskCategory: measurement?.Row.TaskCategory,
            ClientKind: measurement?.Row.ClientKind,
            ComparisonId: null,
            ExperimentId: measurement?.Row.ExperimentId,
            ExperimentCondition: measurement?.Row.ExperimentCondition,
            PromptVersion: measurement?.Row.PromptVersion,
            AgentVariant: measurement?.Row.AgentVariant,
            TaskRunIndex: measurement?.Row.TaskRunIndex,
            FailureCategoryId: candidate.FailureCategoryId,
            AntiPatternId: candidate.AntiPatternId,
            Severity: candidate.Severity,
            EvidenceSummary: BuildEvidenceSummary(candidate),
            RecommendedImprovementTarget: candidate.RecommendedImprovementTarget,
            ReviewStatus: MapReviewStatus(candidate.CandidateStatus));
    }

    private static MeasurementInputRow? FindMeasurement(
        DiagnosisCandidateRow candidate,
        IReadOnlyDictionary<string, MeasurementInputRow[]> measurementsByTraceId)
    {
        if (string.IsNullOrWhiteSpace(candidate.TraceId)
            || !measurementsByTraceId.TryGetValue(candidate.TraceId, out var matches))
        {
            return null;
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        var sourceRefMatches = matches
            .Where(measurement => string.Equals(measurement.SourceRecordRef, candidate.SourceRecordRef, StringComparison.Ordinal))
            .ToArray();

        return sourceRefMatches.Length == 1 ? sourceRefMatches[0] : null;
    }

    private static string BuildEvidenceSummary(DiagnosisCandidateRow candidate)
    {
        return $"{candidate.EvidenceSummary} rule_id={candidate.RuleId}; evidence_ref={SanitizeEvidenceRef(candidate.EvidenceRef)}";
    }

    private static string SanitizeEvidenceRef(string evidenceRef)
    {
        const string measurementPrefix = "measurement:";
        if (!evidenceRef.StartsWith(measurementPrefix, StringComparison.Ordinal))
        {
            return evidenceRef;
        }

        return measurementPrefix + SanitizeSourceRef(evidenceRef[measurementPrefix.Length..]);
    }

    private static string SanitizeSourceRef(string sourceRef)
    {
        var rowMarkerIndex = sourceRef.IndexOf("#row=", StringComparison.Ordinal);
        if (rowMarkerIndex < 0)
        {
            return Path.GetFileName(sourceRef);
        }

        var path = sourceRef[..rowMarkerIndex];
        var rowSuffix = sourceRef[rowMarkerIndex..];
        return Path.GetFileName(path) + rowSuffix;
    }

    private static string MapReviewStatus(string candidateStatus)
    {
        return candidateStatus switch
        {
            "auto-eligible" => "accepted-for-proposal",
            "candidate" => "needs-human-review",
            "blocked" => "rejected",
            _ => throw new InvalidDataException($"candidate_status '{candidateStatus}' is not allowed."),
        };
    }
}

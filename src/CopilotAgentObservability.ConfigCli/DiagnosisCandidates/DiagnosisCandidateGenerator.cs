namespace CopilotAgentObservability.ConfigCli;

internal static class DiagnosisCandidateGenerator
{
    public static IReadOnlyList<DiagnosisCandidateRow> Generate(
        IReadOnlyList<MeasurementInputRow> measurements,
        RawEvidenceIndex? rawEvidence,
        bool includeSensitiveContent,
        string? sensitiveOutputDir,
        RetentionSensitiveBundleStore? retentionBundleStore)
    {
        var drafts = new List<DiagnosisCandidateDraft>();
        foreach (var measurement in measurements)
        {
            var row = measurement.Row;
            RawTraceEvidence? traceEvidence = null;
            rawEvidence?.ByTraceId.TryGetValue(row.TraceId ?? string.Empty, out traceEvidence);

            if (row.ErrorCount.HasValue && row.ErrorCount.Value > 0)
            {
                drafts.Add(new DiagnosisCandidateDraft(
                    row.TraceId,
                    measurement.SourceRecordRef,
                    "DIAG-METRIC-ERROR-COUNT-V1",
                    "F-ERROR",
                    null,
                    "major",
                    "workflow",
                    "Measurement reports one or more errors.",
                    $"measurement:{measurement.SourceRecordRef}",
                    "high",
                    "Confirm the failing operation and whether the workflow should surface or prevent the error.",
                    "auto-eligible",
                    []));
            }

            if (row.ToolCallCount.HasValue
                && row.ToolCallCount.Value >= 10
                && !string.Equals(row.SuccessStatus, "pass", StringComparison.Ordinal))
            {
                drafts.Add(new DiagnosisCandidateDraft(
                    row.TraceId,
                    measurement.SourceRecordRef,
                    "DIAG-METRIC-TOOL-LOOP-V1",
                    "F-TOOL",
                    "AP-TOOL-LOOP",
                    "major",
                    "workflow",
                    "Measurement reports high tool call volume without a passing success status.",
                    $"measurement:{measurement.SourceRecordRef}",
                    "medium",
                    "Review trace steps to confirm whether repeated tool use indicates a loop or expected exploration.",
                    "candidate",
                    []));
            }

            if (traceEvidence?.ErrorMatch is { } errorMatch)
            {
                drafts.Add(new DiagnosisCandidateDraft(
                    row.TraceId,
                    errorMatch.SourceLocator,
                    "DIAG-CONTENT-ERROR-MESSAGE-V1",
                    "F-ERROR",
                    "AP-ERROR-BLIND",
                    "major",
                    "workflow",
                    "Raw telemetry contains an error status, exception marker, or deterministic error text pattern.",
                    errorMatch.EvidenceRef,
                    "medium",
                    "Inspect the referenced raw evidence and decide whether the workflow needs clearer error handling.",
                    "candidate",
                    errorMatch.Fragments));
            }

            if (traceEvidence?.SensitiveMatch is { } sensitiveMatch)
            {
                drafts.Add(new DiagnosisCandidateDraft(
                    row.TraceId,
                    sensitiveMatch.SourceLocator,
                    "DIAG-CONTENT-SENSITIVE-LEAK-V1",
                    "F-DATA",
                    "AP-RAW-CONTENT",
                    "blocking",
                    "workflow",
                    "Raw telemetry matches a deterministic sensitive key, sensitive text, or Base64 credential predicate.",
                    sensitiveMatch.EvidenceRef,
                    "high",
                    "Review sensitive output handling before using this trace in shared artifacts.",
                    "blocked",
                    sensitiveMatch.Fragments));
            }

            if (string.IsNullOrWhiteSpace(row.TraceId)
                || string.IsNullOrWhiteSpace(row.ClientKind)
                || string.IsNullOrWhiteSpace(row.ExperimentId))
            {
                drafts.Add(new DiagnosisCandidateDraft(
                    row.TraceId,
                    measurement.SourceRecordRef,
                    "DIAG-METADATA-MISSING-TRACE-CONTEXT-V1",
                    "F-MEASURE",
                    "AP-SCHEMA-DRIFT",
                    "major",
                    "eval",
                    "Measurement is missing trace_id, client_kind, or experiment_id.",
                    $"measurement:{measurement.SourceRecordRef}",
                    "high",
                    "Confirm the ingest or normalization source preserves required trace context fields.",
                    "auto-eligible",
                    []));
            }
        }

        var candidateIds = drafts
            .Select((draft, index) => new { Draft = draft, CandidateId = $"diagcand-{index + 1:0000}" })
            .ToArray();

        RetentionSensitiveBundleCaptureResult? bundle = null;
        if (includeSensitiveContent)
        {
            var candidatesWithFragments = candidateIds
                .Where(item => item.Draft.Fragments.Count > 0)
                .Select(item => (item.CandidateId, item.Draft.TraceId, item.Draft.Fragments))
                .ToArray();
            if (candidatesWithFragments.Length > 0)
            {
                bundle = (retentionBundleStore ?? throw new InvalidOperationException()).Capture(
                    candidatesWithFragments.Select(static candidate => new SensitiveBundlePlanCandidate(candidate.CandidateId, candidate.TraceId, candidate.Fragments)).ToArray(),
                    rawEvidence?.SourceInputs ?? [],
                    sensitiveOutputDir);
            }
        }

        var rows = new List<DiagnosisCandidateRow>();
        foreach (var item in candidateIds)
        {
            var draft = item.Draft;
            var contentIncluded = false;
            string? sensitiveBundlePath = null;
            var evidenceRef = draft.EvidenceRef;

            if (includeSensitiveContent
                && bundle is not null
                && bundle.EntriesByCandidateId.ContainsKey(item.CandidateId))
            {
                contentIncluded = true;
                sensitiveBundlePath = bundle.FinalPath;
                evidenceRef = bundle.EntriesByCandidateId[item.CandidateId].EvidenceRef;
            }

            rows.Add(new DiagnosisCandidateRow(
                item.CandidateId,
                draft.TraceId,
                draft.SourceRecordRef,
                draft.RuleId,
                draft.FailureCategoryId,
                draft.AntiPatternId,
                draft.Severity,
                draft.RecommendedImprovementTarget,
                draft.EvidenceSummary,
                evidenceRef,
                contentIncluded,
                sensitiveBundlePath,
                draft.Confidence,
                draft.RequiredHumanChecks,
                draft.CandidateStatus));
        }

        return rows;
    }
}

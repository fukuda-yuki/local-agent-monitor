using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalEfficiencyAnalyzerV1
{
    private const string ReceiptDomain = "copilot-agent-observability/historical-efficiency-receipt/v1";
    private const string DriverDomain = "copilot-agent-observability/historical-efficiency-driver/v1";

    internal static HistoricalEfficiencyAnalysisV1 Analyze(HistoricalEvidenceExtractionV1 extraction)
    {
        try
        {
            return AnalyzeCore(extraction);
        }
        catch (HistoricalEfficiencyValidationException) { throw; }
        catch (Exception exception) when (exception is HistoricalEvidenceValidationException or ArgumentException
            or ArithmeticException or InvalidOperationException or NullReferenceException)
        {
            throw InvalidInput();
        }
    }

    private static HistoricalEfficiencyAnalysisV1 AnalyzeCore(HistoricalEvidenceExtractionV1 extraction)
    {
        if (!IsHash(extraction.RepositorySafeSha256)
            || !string.Equals(HistoricalEvidenceExtractorV1.Sha256(extraction.RepositorySafeBytes), extraction.RepositorySafeSha256,
                StringComparison.Ordinal))
            throw InvalidInput();

        var dataset = HistoricalEvidenceJsonV1.Deserialize(extraction.RepositorySafeBytes);
        if (dataset.SchemaVersion != HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion
            || dataset.Representation != HistoricalEvidenceRepresentationV1.RepositorySafe)
            throw InvalidInput();

        var groupsBySession = dataset.EvidenceGroups.ToLookup(value => value.SessionId, StringComparer.Ordinal);
        var sessions = dataset.Sessions.Select(value => new SessionContext(value, groupsBySession[value.SessionId].ToArray()))
            .OrderBy(value => value.Session.SessionId, StringComparer.Ordinal)
            .ToArray();
        var analysisQuality = QualityAvailability(sessions);
        var globalNotes = ComparisonNotes(dataset, sessions, analysisQuality);
        var analysis = new AnalysisContext(dataset, extraction.RepositorySafeSha256, sessions);
        var trackers = HistoricalEfficiencyDriverRegistryV1.Rules.ToDictionary(
            value => value.Category,
            value => new CoverageTracker(value, value.UnsupportedReason is null
                ? EligibleSessionCount(sessions, value.RequiredCapabilities)
                : 0));
        var drivers = new List<HistoricalEfficiencyDriverV1>();

        AnalyzeTokenVolume(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.TokenVolume], drivers);
        AnalyzeContextGrowth(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.ContextGrowth], drivers);
        AnalyzeCacheInefficiency(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.CacheInefficiency], drivers);
        AnalyzeRetryOverhead(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.RetryOverhead], drivers);
        AnalyzeDurationOutlier(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.DurationOutlier], drivers);
        AnalyzeModelMix(analysis, trackers[HistoricalEfficiencyDriverCategoryV1.ModelMixObservation], drivers);

        var orderedDrivers = drivers
            .OrderBy(value => value.Category)
            .ThenBy(value => value.SubjectSessionId, StringComparer.Ordinal)
            .ThenBy(value => value.DriverId, StringComparer.Ordinal)
            .ToArray();
        var categoryCoverage = HistoricalEfficiencyDriverRegistryV1.Rules
            .Select(value => trackers[value.Category].Build())
            .ToArray();
        var receipt = new HistoricalEfficiencyReceiptV1(
            HistoricalEfficiencyContractsV1.ReceiptSchemaVersion,
            CalculateReceiptId(dataset.ExtractionId, extraction.RepositorySafeSha256),
            HistoricalEfficiencyContractsV1.RegistryVersion,
            dataset.ExtractionId,
            extraction.RepositorySafeSha256,
            orderedDrivers.Length == 0 ? HistoricalEfficiencyAnalysisStateV1.ZeroDrivers : HistoricalEfficiencyAnalysisStateV1.Succeeded,
            new HistoricalEfficiencyCoverageV1(
                sessions.Length,
                dataset.ExcludedSessions.Count,
                dataset.TruncatedBefore,
                dataset.TruncatedSessionCount,
                dataset.Distribution.Completeness,
                dataset.Distribution.SourceKinds,
                dataset.Distribution.Capabilities),
            analysisQuality,
            globalNotes,
            categoryCoverage,
            orderedDrivers);
        var canonicalBytes = HistoricalEfficiencyJsonV1.Serialize(receipt);
        return new(receipt, canonicalBytes, HistoricalEvidenceExtractorV1.Sha256(canonicalBytes));
    }

    private static void AnalyzeTokenVolume(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        var observations = new List<ComparativeObservation>();
        var matches = new List<ComparativeMatch>();
        var excludedRequiredMetric = false;
        foreach (var session in analysis.Sessions.Where(value => HasCapabilities(value.Session.Capabilities, tracker.Rule.RequiredCapabilities)))
        {
            var scalar = SessionTokenTotal(session);
            if (scalar is null)
            {
                tracker.MissingMetric = true;
                excludedRequiredMetric = true;
                continue;
            }
            var key = GetComparisonKey(session);
            if (key is null)
            {
                tracker.MixedDimension = true;
                excludedRequiredMetric = true;
                continue;
            }
            observations.Add(new(session, scalar.Value.Value, scalar.Value.References, key));
        }

        foreach (var cohort in observations.GroupBy(value => value.Key).OrderBy(value => value.Key.SortKey, StringComparer.Ordinal))
        {
            var values = cohort.OrderBy(value => value.Session.Session.SessionId, StringComparer.Ordinal).ToArray();
            tracker.Observe(values.Length);
            if (values.Length < tracker.Rule.MinimumSample) continue;
            var median = Median(values.Select(value => value.Value));
            var percentile = NearestRank75(values.Select(value => value.Value));
            if (median <= 0)
            {
                tracker.MissingMetric = true;
                excludedRequiredMetric = true;
                continue;
            }
            tracker.CompleteEvaluation = true;
            foreach (var subject in values.Where(value => value.Value > percentile && value.Value / median >= 1.50m))
                matches.Add(new(subject, values, median, percentile));
        }
        foreach (var match in matches)
        {
            var sources = match.Cohort.Select(value => value.Session).ToArray();
            var evidence = SortReferences(match.Cohort.SelectMany(value => value.References));
            drivers.Add(BuildDriver(analysis, tracker.Rule, match.Subject.Session.Session.SessionId, sources, evidence,
                [Scalar("session_total", match.Subject.Value, "token"),
                    Scalar("median_ratio", match.Subject.Value / match.Median, "ratio")],
                Scalar("cohort_median", match.Median, "token"),
                Percentile("cohort_p75", match.Percentile, "token"), excludedRequiredMetric));
            tracker.Matched = true;
        }
    }

    private static void AnalyzeContextGrowth(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        foreach (var session in analysis.Sessions.Where(value => HasCapabilities(value.Session.Capabilities, tracker.Rule.RequiredCapabilities)))
        {
            var inputs = ScalarByReference(session, HistoricalEvidenceGroupKindV1.TokenRollup, HistoricalEvidenceScalarUnitsV1.InputToken);
            var turnReferences = session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
                .SelectMany(value => value.References).Distinct(ReferenceEqualityComparer.Instance)
                .GroupBy(value => value.TraceId, StringComparer.Ordinal);
            var missingInSession = false;
            var matches = new List<IReadOnlyList<(HistoricalEvidenceReferenceV1 Reference, decimal Input)>>();
            foreach (var trace in turnReferences.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var ordered = trace.OrderBy(value => value.TurnIndex).ThenBy(ReferenceSortKey, StringComparer.Ordinal).ToArray();
                var run = new List<(HistoricalEvidenceReferenceV1 Reference, decimal Input)>();
                int? previousTurn = null;
                foreach (var reference in ordered)
                {
                    var hasInput = inputs.TryGetValue(reference, out var input);
                    var contiguous = reference.TurnIndex is > 0 && previousTurn is not null && reference.TurnIndex == previousTurn + 1;
                    if (!hasInput || reference.TurnIndex is not > 0 || (run.Count > 0 && (!contiguous || input < run[^1].Input)))
                    {
                        EvaluateRun(run);
                        run.Clear();
                    }
                    if (!hasInput || reference.TurnIndex is not > 0)
                    {
                        missingInSession = true;
                        previousTurn = reference.TurnIndex;
                        continue;
                    }
                    run.Add((reference, input));
                    previousTurn = reference.TurnIndex;
                }
                EvaluateRun(run);

                void EvaluateRun(IReadOnlyList<(HistoricalEvidenceReferenceV1 Reference, decimal Input)> candidate)
                {
                    tracker.Observe(candidate.Count);
                    if (candidate.Count < tracker.Rule.MinimumSample) return;
                    if (candidate[0].Input <= 0) { tracker.MissingMetric = true; return; }
                    tracker.CompleteEvaluation = true;
                    var ratio = candidate[^1].Input / candidate[0].Input;
                    if (ratio < 1.75m) return;
                    matches.Add(candidate.ToArray());
                }
            }
            tracker.MissingMetric |= missingInSession;
            foreach (var match in matches)
            {
                var ratio = match[^1].Input / match[0].Input;
                drivers.Add(BuildDriver(analysis, tracker.Rule, session.Session.SessionId, [session],
                    SortReferences(match.Select(value => value.Reference)),
                    [Scalar("first_input_tokens", match[0].Input, "input_token"),
                        Scalar("last_input_tokens", match[^1].Input, "input_token"),
                        Scalar("context_growth_ratio", ratio, "ratio")],
                    null, null));
                tracker.Matched = true;
            }
        }
    }

    private static void AnalyzeCacheInefficiency(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        foreach (var session in analysis.Sessions)
        {
            if (!HasCapabilities(session.Session.Capabilities, tracker.Rule.RequiredCapabilities))
            {
                if (session.Session.Capabilities.TurnRollup && session.Session.Capabilities.TokenRollup)
                    tracker.MissingMetric = true;
                continue;
            }
            var inputs = ScalarByReference(session, HistoricalEvidenceGroupKindV1.TokenRollup, HistoricalEvidenceScalarUnitsV1.InputToken);
            var caches = ScalarByReference(session, HistoricalEvidenceGroupKindV1.CacheRollup, HistoricalEvidenceScalarUnitsV1.CacheReadToken);
            var turnReferences = session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
                .SelectMany(value => value.References).Distinct(ReferenceEqualityComparer.Instance)
                .GroupBy(value => value.TraceId, StringComparer.Ordinal);
            foreach (var trace in turnReferences.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var missing = false;
                var eligible = new List<(HistoricalEvidenceReferenceV1 Reference, decimal Input, decimal Cache)>();
                foreach (var reference in trace.OrderBy(value => value.TurnIndex).ThenBy(ReferenceSortKey, StringComparer.Ordinal))
                {
                    if (inputs.TryGetValue(reference, out var input) && caches.TryGetValue(reference, out var cache))
                        eligible.Add((reference, input, cache));
                    else
                        missing = true;
                }
                tracker.MissingMetric |= missing;
                tracker.Observe(eligible.Count);
                if (eligible.Count < tracker.Rule.MinimumSample) continue;
                var included = eligible.Skip(1).ToArray();
                var inputTotal = included.Aggregate(0m, (sum, value) => checked(sum + value.Input));
                var cacheTotal = included.Aggregate(0m, (sum, value) => checked(sum + value.Cache));
                if (inputTotal == 0) { tracker.MissingMetric = true; continue; }
                tracker.CompleteEvaluation = true;
                if (inputTotal < 10_000m) continue;
                var ratio = cacheTotal / inputTotal;
                if (ratio >= 0.20m) continue;
                drivers.Add(BuildDriver(analysis, tracker.Rule, session.Session.SessionId, [session],
                    SortReferences(included.Select(value => value.Reference)),
                    [Scalar("included_input_tokens", inputTotal, "input_token"),
                        Scalar("included_cache_read_tokens", cacheTotal, "cache_read_token"),
                        Scalar("cache_read_ratio", ratio, "ratio")],
                    null, null));
                tracker.Matched = true;
            }
        }
    }

    private static void AnalyzeRetryOverhead(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        var seenChains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in analysis.Sessions.Where(value => value.Session.Capabilities.RetryChain))
        {
            foreach (var group in session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.RetryChain))
            {
                var references = SortReferences(group.References);
                var identity = string.Join('|', references.Select(ReferenceSortKey));
                if (!seenChains.Add(identity)) continue;
                if (references.Count == 0) { tracker.MissingMetric = true; continue; }
                tracker.Observe(1);
                tracker.CompleteEvaluation = true;
                if (references.Count < 2) continue;
                drivers.Add(BuildDriver(analysis, tracker.Rule, session.Session.SessionId, [session], references,
                    [Scalar("attempt_count", references.Count, "attempt"), Scalar("retry_overhead", references.Count - 1, "attempt")],
                    null, null));
                tracker.Matched = true;
            }
        }
    }

    private static void AnalyzeDurationOutlier(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        var observations = new List<ComparativeObservation>();
        var matches = new List<ComparativeMatch>();
        var excludedRequiredMetric = false;
        foreach (var session in analysis.Sessions)
        {
            if (session.Session.Metadata.DurationObservations.Count == 0)
            {
                tracker.MissingMetric = true;
                excludedRequiredMetric = true;
                continue;
            }
            var key = GetComparisonKey(session);
            if (key is null)
            {
                tracker.MixedDimension = true;
                excludedRequiredMetric = true;
                continue;
            }
            var maximum = session.Session.Metadata.DurationObservations.Max(value => checked((decimal)value.DurationMs));
            observations.Add(new(session, maximum,
                SortReferences(session.Session.Metadata.DurationObservations.Select(value => value.EvidenceRef)), key));
        }
        foreach (var cohort in observations.GroupBy(value => value.Key).OrderBy(value => value.Key.SortKey, StringComparer.Ordinal))
        {
            var values = cohort.OrderBy(value => value.Session.Session.SessionId, StringComparer.Ordinal).ToArray();
            tracker.Observe(values.Length);
            if (values.Length < tracker.Rule.MinimumSample) continue;
            var median = Median(values.Select(value => value.Value));
            var percentile = NearestRank75(values.Select(value => value.Value));
            if (median <= 0)
            {
                tracker.MissingMetric = true;
                excludedRequiredMetric = true;
                continue;
            }
            tracker.CompleteEvaluation = true;
            foreach (var subject in values.Where(value => value.Value > percentile && value.Value / median >= 1.50m))
                matches.Add(new(subject, values, median, percentile));
        }
        foreach (var match in matches)
        {
            var sources = match.Cohort.Select(value => value.Session).ToArray();
            var evidence = SortReferences(match.Cohort.SelectMany(value => value.References));
            drivers.Add(BuildDriver(analysis, tracker.Rule, match.Subject.Session.Session.SessionId, sources, evidence,
                [Scalar("session_duration", match.Subject.Value, "millisecond"),
                    Scalar("median_ratio", match.Subject.Value / match.Median, "ratio")],
                Scalar("cohort_median", match.Median, "millisecond"),
                Percentile("cohort_p75", match.Percentile, "millisecond"), excludedRequiredMetric));
            tracker.Matched = true;
        }
    }

    private static void AnalyzeModelMix(
        AnalysisContext analysis,
        CoverageTracker tracker,
        List<HistoricalEfficiencyDriverV1> drivers)
    {
        var observations = analysis.Sessions
            .SelectMany(session => session.Session.Metadata.ModelObservations.Select(value => (Session: session, Observation: value)))
            .ToArray();
        var distinctModels = observations.Select(value => value.Observation.Model).Distinct(StringComparer.Ordinal).Count();
        tracker.Observe(distinctModels);
        if (distinctModels < tracker.Rule.MinimumSample)
        {
            if (observations.Length == 0) tracker.MissingMetric = true;
            return;
        }
        tracker.CompleteEvaluation = true;
        var sources = observations.Select(value => value.Session).DistinctBy(value => value.Session.SessionId)
            .OrderBy(value => value.Session.SessionId, StringComparer.Ordinal).ToArray();
        var evidence = SortReferences(observations.Select(value => value.Observation.EvidenceRef));
        drivers.Add(BuildDriver(analysis, tracker.Rule, null, sources, evidence,
            [Scalar("distinct_model_count", distinctModels, "model")], null, null));
        tracker.Matched = true;
    }

    private static HistoricalEfficiencyDriverV1 BuildDriver(
        AnalysisContext analysis,
        HistoricalEfficiencyDriverRuleV1 rule,
        string? subjectSessionId,
        IReadOnlyList<SessionContext> sourceContexts,
        IReadOnlyList<HistoricalEvidenceReferenceV1> evidenceRefs,
        IReadOnlyList<HistoricalEfficiencyScalarV1> observedValues,
        HistoricalEfficiencyScalarV1? cohortMedian,
        HistoricalEfficiencyPercentileV1? cohortPercentile,
        bool excludedRequiredMetric = false)
    {
        var orderedSources = sourceContexts.DistinctBy(value => value.Session.SessionId)
            .OrderBy(value => value.Session.SessionId, StringComparer.Ordinal).ToArray();
        var sourceSessions = orderedSources.Select(value => value.Session.SessionId).ToArray();
        var quality = QualityAvailability(orderedSources);
        var notes = ComparisonNotes(analysis.Dataset, orderedSources, quality);
        var incomplete = excludedRequiredMetric || orderedSources.Any(value =>
            value.Session.Completeness == SessionCompleteness.Partial
            || value.Session.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary);
        var mixed = notes.Any(value => value is HistoricalEfficiencyComparisonNoteV1.MixedSourceSurface
            or HistoricalEfficiencyComparisonNoteV1.MixedSourceVersion
            or HistoricalEfficiencyComparisonNoteV1.MixedAdapterVersion
            or HistoricalEfficiencyComparisonNoteV1.MixedModel);
        var verdict = incomplete
            ? HistoricalEfficiencyDriverVerdictV1.Incomplete
            : quality != HistoricalEfficiencyQualityAvailabilityV1.Available || mixed
                || rule.Category == HistoricalEfficiencyDriverCategoryV1.ModelMixObservation
                ? HistoricalEfficiencyDriverVerdictV1.Weak
                : HistoricalEfficiencyDriverVerdictV1.Supported;
        var sortedEvidence = SortReferences(evidenceRefs);
        var qualityEvidence = SortReferences(orderedSources.SelectMany(value => value.Groups
            .Where(group => group.Kind == HistoricalEvidenceGroupKindV1.QualityReference)
            .SelectMany(group => group.References)));
        var sourceDistribution = new HistoricalEfficiencySourceDistributionV1(
            Counts(orderedSources.Select(value => SessionWire.ToWire(value.Session.SourceSurface))),
            Counts(orderedSources.Select(value => Snake(value.Session.SourceKind))));
        var completeness = Counts(orderedSources.Select(value => SessionWire.ToWire(value.Session.Completeness)));
        var summary = HistoricalEfficiencyDriverRegistryV1.Summary(rule.Category);
        var mitigation = new HistoricalEfficiencyMitigationV1(rule.MitigationCode,
            HistoricalEfficiencyDriverRegistryV1.MitigationSummary(rule.Category), sortedEvidence);
        var draft = new HistoricalEfficiencyDriverV1(
            string.Empty,
            analysis.Dataset.ExtractionId,
            analysis.ExtractionSha256,
            rule.Category,
            rule.DriverVersion,
            rule.RuleSource,
            rule.Formula,
            rule.Threshold,
            subjectSessionId,
            sourceSessions,
            sortedEvidence,
            qualityEvidence,
            observedValues,
            cohortMedian,
            cohortPercentile,
            sourceDistribution,
            completeness,
            quality,
            verdict,
            notes,
            summary,
            mitigation);
        return draft with { DriverId = CalculateDriverId(draft) };
    }

    private static (decimal Value, IReadOnlyList<HistoricalEvidenceReferenceV1> References)? SessionTokenTotal(SessionContext session)
    {
        var components = new Dictionary<HistoricalEvidenceReferenceV1, Dictionary<string, decimal>>(ReferenceEqualityComparer.Instance);
        foreach (var group in session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.TokenRollup
            && value.Unit is HistoricalEvidenceScalarUnitsV1.TotalToken or HistoricalEvidenceScalarUnitsV1.InputToken
                or HistoricalEvidenceScalarUnitsV1.OutputToken))
        {
            if (group.NumericValue is not >= 0 || group.Unit is null) continue;
            var value = checked((decimal)group.NumericValue.Value);
            foreach (var reference in group.References)
            {
                if (!components.TryGetValue(reference, out var byUnit)) components[reference] = byUnit = new(StringComparer.Ordinal);
                if (byUnit.TryGetValue(group.Unit, out var prior) && prior != value) throw InvalidInput();
                byUnit[group.Unit] = value;
            }
        }
        var totals = new Dictionary<HistoricalEvidenceReferenceV1, decimal>(ReferenceEqualityComparer.Instance);
        foreach (var (reference, values) in components)
        {
            if (values.TryGetValue(HistoricalEvidenceScalarUnitsV1.TotalToken, out var total))
                totals[reference] = total;
            else if (values.TryGetValue(HistoricalEvidenceScalarUnitsV1.InputToken, out var input)
                && values.TryGetValue(HistoricalEvidenceScalarUnitsV1.OutputToken, out var output))
                totals[reference] = checked(input + output);
        }
        var turns = session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
            .SelectMany(value => value.References).Distinct(ReferenceEqualityComparer.Instance).ToArray();
        if (totals.Count == 0 || turns.Any(value => !totals.ContainsKey(value))) return null;
        return (totals.Values.Aggregate(0m, (sum, value) => checked(sum + value)), SortReferences(totals.Keys));
    }

    private static Dictionary<HistoricalEvidenceReferenceV1, decimal> ScalarByReference(
        SessionContext session,
        HistoricalEvidenceGroupKindV1 kind,
        string unit)
    {
        var result = new Dictionary<HistoricalEvidenceReferenceV1, decimal>(ReferenceEqualityComparer.Instance);
        foreach (var group in session.Groups.Where(value => value.Kind == kind && value.Unit == unit && value.NumericValue is >= 0))
        {
            var scalar = checked((decimal)group.NumericValue!.Value);
            foreach (var reference in group.References)
            {
                if (result.TryGetValue(reference, out var prior) && prior != scalar) throw InvalidInput();
                result[reference] = scalar;
            }
        }
        return result;
    }

    private static ComparisonKey? GetComparisonKey(SessionContext session)
    {
        var models = session.Session.Metadata.ModelObservations.Select(value => value.Model).Distinct(StringComparer.Ordinal).ToArray();
        if (models.Length != 1) return null;
        return new(SessionWire.ToWire(session.Session.SourceSurface), session.Session.SourceVersion,
            session.Session.AdapterVersion, models[0]);
    }

    private static IReadOnlyList<HistoricalEfficiencyComparisonNoteV1> ComparisonNotes(
        HistoricalEvidenceDatasetV1 dataset,
        IReadOnlyList<SessionContext> sessions,
        HistoricalEfficiencyQualityAvailabilityV1 quality)
    {
        var result = new List<HistoricalEfficiencyComparisonNoteV1>();
        if (dataset.TruncatedBefore) result.Add(HistoricalEfficiencyComparisonNoteV1.TruncatedWindow);
        if (sessions.Any(value => value.Session.Completeness == SessionCompleteness.Partial))
            result.Add(HistoricalEfficiencyComparisonNoteV1.PartialCompleteness);
        if (sessions.Any(value => value.Session.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary))
            result.Add(HistoricalEfficiencyComparisonNoteV1.HistoricalSummaryPresent);
        if (sessions.Select(value => value.Session.SourceSurface).Distinct().Count() > 1)
            result.Add(HistoricalEfficiencyComparisonNoteV1.MixedSourceSurface);
        if (sessions.Select(value => value.Session.SourceVersion ?? "\0").Distinct(StringComparer.Ordinal).Count() > 1)
            result.Add(HistoricalEfficiencyComparisonNoteV1.MixedSourceVersion);
        if (sessions.Select(value => value.Session.AdapterVersion ?? "\0").Distinct(StringComparer.Ordinal).Count() > 1)
            result.Add(HistoricalEfficiencyComparisonNoteV1.MixedAdapterVersion);
        if (sessions.SelectMany(value => value.Session.Metadata.ModelObservations).Select(value => value.Model)
            .Distinct(StringComparer.Ordinal).Count() > 1)
            result.Add(HistoricalEfficiencyComparisonNoteV1.MixedModel);
        result.Add(quality switch
        {
            HistoricalEfficiencyQualityAvailabilityV1.Unavailable => HistoricalEfficiencyComparisonNoteV1.QualityUnavailable,
            HistoricalEfficiencyQualityAvailabilityV1.Partial => HistoricalEfficiencyComparisonNoteV1.QualityPartial,
            HistoricalEfficiencyQualityAvailabilityV1.RegressionObserved => HistoricalEfficiencyComparisonNoteV1.QualityRegressionObserved,
            _ => (HistoricalEfficiencyComparisonNoteV1)(-1),
        });
        return result.Where(value => (int)value >= 0).ToArray();
    }

    private static HistoricalEfficiencyQualityAvailabilityV1 QualityAvailability(IReadOnlyList<SessionContext> sessions)
    {
        if (sessions.Count == 0) return HistoricalEfficiencyQualityAvailabilityV1.Unavailable;
        var states = sessions.Select(SessionQuality).ToArray();
        if (states.Any(value => value == SessionQualityState.Fail)) return HistoricalEfficiencyQualityAvailabilityV1.RegressionObserved;
        if (states.All(value => value == SessionQualityState.Pass)) return HistoricalEfficiencyQualityAvailabilityV1.Available;
        if (states.Any(value => value is SessionQualityState.Pass or SessionQualityState.Unknown))
            return HistoricalEfficiencyQualityAvailabilityV1.Partial;
        return HistoricalEfficiencyQualityAvailabilityV1.Unavailable;
    }

    private static SessionQualityState SessionQuality(SessionContext session)
    {
        var statuses = session.Groups.Where(value => value.Kind == HistoricalEvidenceGroupKindV1.QualityReference)
            .Select(value => value.Status).ToArray();
        if (statuses.Any(value => value == "fail")) return SessionQualityState.Fail;
        var anyPass = statuses.Any(value => value == "pass");
        var anyUnknown = statuses.Any(value => value is not "pass" and not "fail");
        if (anyPass && !anyUnknown) return SessionQualityState.Pass;
        if (statuses.Length > 0) return SessionQualityState.Unknown;
        return SessionQualityState.Missing;
    }

    private static int EligibleSessionCount(IReadOnlyList<SessionContext> sessions, IReadOnlyList<string> requiredCapabilities) =>
        sessions.Count(value => HasCapabilities(value.Session.Capabilities, requiredCapabilities));

    private static bool HasCapabilities(HistoricalSessionCapabilitiesV1 value, IReadOnlyList<string> required) =>
        required.All(capability => capability switch
        {
            "turn_rollup" => value.TurnRollup,
            "token_rollup" => value.TokenRollup,
            "cache_rollup" => value.CacheRollup,
            "retry_chain" => value.RetryChain,
            _ => false,
        });

    private static decimal Median(IEnumerable<decimal> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) throw InvalidInput();
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : checked(values[middle - 1] + (values[middle] - values[middle - 1]) / 2m);
    }

    private static decimal NearestRank75(IEnumerable<decimal> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) throw InvalidInput();
        var rank = (3 * values.Length + 3) / 4;
        return values[rank - 1];
    }

    private static HistoricalEfficiencyScalarV1 Scalar(string name, decimal value, string unit) => new(name, value, unit);
    private static HistoricalEfficiencyPercentileV1 Percentile(string name, decimal value, string unit) => new(75, name, value, unit);

    private static IReadOnlyList<HistoricalEvidenceReferenceV1> SortReferences(IEnumerable<HistoricalEvidenceReferenceV1> source) =>
        source.Distinct(ReferenceEqualityComparer.Instance).OrderBy(ReferenceSortKey, StringComparer.Ordinal).ToArray();

    private static string ReferenceSortKey(HistoricalEvidenceReferenceV1 value) => string.Join('\u001f',
        value.SessionId, value.TraceId, value.SpanId ?? string.Empty,
        value.TurnIndex?.ToString("D10", CultureInfo.InvariantCulture) ?? string.Empty,
        ((int)value.RelativePosition).ToString("D2", CultureInfo.InvariantCulture));

    private static IReadOnlyList<HistoricalDistributionCountV1> Counts(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new HistoricalDistributionCountV1(value.Key, value.Count())).ToArray();

    private static string Snake<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    internal static string CalculateReceiptId(string extractionId, string extractionSha256) =>
        StableId("historical-efficiency-receipt-", ReceiptDomain,
            HistoricalEfficiencyContractsV1.RegistryVersion, extractionId, extractionSha256);

    internal static string CalculateDriverId(HistoricalEfficiencyDriverV1 driver)
    {
        var fields = new List<string?>
        {
            "extraction_id", driver.ExtractionId,
            "extraction_sha256", driver.ExtractionSha256,
            "rule_source", driver.RuleSource,
            "subject_session", driver.SubjectSessionId,
            "category", Snake(driver.Category),
            "formula", driver.Formula,
            "threshold", driver.Threshold,
            "source_sessions", driver.SourceSessions.Count.ToString(CultureInfo.InvariantCulture),
        };
        fields.AddRange(driver.SourceSessions);
        AddReferences("evidence_refs", driver.EvidenceRefs);
        AddReferences("quality_evidence_refs", driver.QualityEvidenceRefs);
        fields.Add("observed_values");
        fields.Add(driver.ObservedValues.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var scalar in driver.ObservedValues)
        {
            fields.Add(scalar.Name); fields.Add(scalar.Value.ToString(CultureInfo.InvariantCulture)); fields.Add(scalar.Unit);
        }
        fields.Add("cohort_median");
        fields.Add(driver.CohortMedian?.Name);
        fields.Add(driver.CohortMedian?.Value.ToString(CultureInfo.InvariantCulture));
        fields.Add(driver.CohortMedian?.Unit);
        fields.Add("cohort_percentile");
        fields.Add(driver.CohortPercentile?.Percentile.ToString(CultureInfo.InvariantCulture));
        fields.Add(driver.CohortPercentile?.Name);
        fields.Add(driver.CohortPercentile?.Value.ToString(CultureInfo.InvariantCulture));
        fields.Add(driver.CohortPercentile?.Unit);
        fields.Add("quality_availability");
        fields.Add(Snake(driver.QualityAvailability));
        fields.Add("verdict");
        fields.Add(Snake(driver.Verdict));
        fields.Add("comparison_notes");
        fields.Add(driver.ComparisonNotes.Count.ToString(CultureInfo.InvariantCulture));
        fields.AddRange(driver.ComparisonNotes.Select(Snake));
        return StableId("historical-efficiency-driver-", DriverDomain, [.. fields]);

        void AddReferences(string section, IReadOnlyList<HistoricalEvidenceReferenceV1> references)
        {
            fields.Add(section);
            fields.Add(references.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var reference in references)
            {
                fields.Add(reference.SessionId); fields.Add(reference.TraceId); fields.Add(reference.SpanId);
                fields.Add(reference.TurnIndex?.ToString(CultureInfo.InvariantCulture)); fields.Add(Snake(reference.RelativePosition));
            }
        }
    }

    private static string StableId(string prefix, string domain, params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field ?? string.Empty);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return prefix + Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    private static bool IsHash(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static HistoricalEfficiencyValidationException InvalidInput() =>
        new(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);

    private sealed record SessionContext(HistoricalEvidenceSessionV1 Session, IReadOnlyList<HistoricalEvidenceGroupV1> Groups);
    private sealed record AnalysisContext(
        HistoricalEvidenceDatasetV1 Dataset,
        string ExtractionSha256,
        IReadOnlyList<SessionContext> Sessions);

    private sealed record ComparisonKey(string SourceSurface, string? SourceVersion, string? AdapterVersion, string Model)
    {
        internal string SortKey => string.Join('\u001f', SourceSurface, SourceVersion ?? string.Empty, AdapterVersion ?? string.Empty, Model);
    }

    private sealed record ComparativeObservation(
        SessionContext Session,
        decimal Value,
        IReadOnlyList<HistoricalEvidenceReferenceV1> References,
        ComparisonKey Key);

    private sealed record ComparativeMatch(
        ComparativeObservation Subject,
        IReadOnlyList<ComparativeObservation> Cohort,
        decimal Median,
        decimal Percentile);

    private enum SessionQualityState { Missing, Unknown, Pass, Fail }

    private sealed class CoverageTracker(HistoricalEfficiencyDriverRuleV1 rule, int eligibleSessionCount)
    {
        internal HistoricalEfficiencyDriverRuleV1 Rule { get; } = rule;
        internal int EligibleSessionCount { get; } = eligibleSessionCount;
        internal int ObservedSampleCount { get; private set; }
        internal bool CompleteEvaluation { get; set; }
        internal bool Matched { get; set; }
        internal bool MissingMetric { get; set; }
        internal bool MixedDimension { get; set; }

        internal void Observe(int count) => ObservedSampleCount = Math.Max(ObservedSampleCount, count);

        internal HistoricalEfficiencyCategoryCoverageV1 Build()
        {
            HistoricalEfficiencyCoverageStateV1 state;
            IReadOnlyList<HistoricalEfficiencyCoverageReasonV1> reasons;
            if (Rule.UnsupportedReason is not null)
            {
                state = HistoricalEfficiencyCoverageStateV1.Unavailable;
                reasons = [Rule.UnsupportedReason.Value];
            }
            else if (Matched)
            {
                state = HistoricalEfficiencyCoverageStateV1.Matched;
                reasons = [];
            }
            else if (CompleteEvaluation)
            {
                state = HistoricalEfficiencyCoverageStateV1.NoMatch;
                reasons = [HistoricalEfficiencyCoverageReasonV1.NoThresholdMatch];
            }
            else if (EligibleSessionCount == 0)
            {
                state = HistoricalEfficiencyCoverageStateV1.Unavailable;
                reasons = [HistoricalEfficiencyCoverageReasonV1.MissingRequiredCapability];
            }
            else
            {
                state = HistoricalEfficiencyCoverageStateV1.Insufficient;
                var values = new List<HistoricalEfficiencyCoverageReasonV1>();
                if (MissingMetric) values.Add(HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric);
                if (MixedDimension) values.Add(HistoricalEfficiencyCoverageReasonV1.MixedEvaluationDimension);
                if (ObservedSampleCount < Rule.MinimumSample) values.Add(HistoricalEfficiencyCoverageReasonV1.MinimumSampleUnmet);
                reasons = values;
            }
            return new(Rule.Category, Rule.DriverVersion, Rule.RuleSource, Rule.RequiredCapabilities, Rule.Formula,
                Rule.Threshold, state, EligibleSessionCount, ObservedSampleCount, Rule.MinimumSample, reasons);
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<HistoricalEvidenceReferenceV1>
    {
        internal static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(HistoricalEvidenceReferenceV1? x, HistoricalEvidenceReferenceV1? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null
            && x.SessionId == y.SessionId && x.TraceId == y.TraceId && x.SpanId == y.SpanId
            && x.TurnIndex == y.TurnIndex && x.RelativePosition == y.RelativePosition;

        public int GetHashCode(HistoricalEvidenceReferenceV1 value) =>
            HashCode.Combine(value.SessionId, value.TraceId, value.SpanId, value.TurnIndex, value.RelativePosition);
    }
}

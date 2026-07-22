using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalEfficiencyJsonV1
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Serialize(HistoricalEfficiencyReceiptV1 receipt)
    {
        Validate(receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length > HistoricalEvidenceContractsV1.MaximumPayloadBytes)
            throw new HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
        return bytes;
    }

    internal static HistoricalEfficiencyReceiptV1 Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > HistoricalEvidenceContractsV1.MaximumPayloadBytes)
            throw new HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
        try
        {
            var receipt = JsonSerializer.Deserialize<HistoricalEfficiencyReceiptV1>(bytes, Options)
                ?? throw new HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
            Validate(receipt);
            if (!bytes.SequenceEqual(Serialize(receipt)))
                throw new HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
            return receipt;
        }
        catch (HistoricalEfficiencyValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException
            or NullReferenceException or InvalidOperationException or OverflowException)
        {
            throw new HistoricalEfficiencyValidationException(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
        }
    }

    private static void Validate(HistoricalEfficiencyReceiptV1 receipt)
    {
        var rules = HistoricalEfficiencyDriverRegistryV1.Rules;
        if (receipt.SchemaVersion != HistoricalEfficiencyContractsV1.ReceiptSchemaVersion
            || receipt.RegistryVersion != HistoricalEfficiencyContractsV1.RegistryVersion
            || !Token(receipt.ReceiptId, "historical-efficiency-receipt-")
            || !Token(receipt.ExtractionId, "historical-extraction-")
            || !Hash(receipt.ExtractionSha256)
            || receipt.ReceiptId != HistoricalEfficiencyAnalyzerV1.CalculateReceiptId(receipt.ExtractionId, receipt.ExtractionSha256)
            || receipt.CategoryCoverage.Count != rules.Count
            || !receipt.CategoryCoverage.Select(value => value.Category).SequenceEqual(rules.Select(value => value.Category))
            || receipt.Drivers.Count > 51_200
            || receipt.State != (receipt.Drivers.Count == 0
                ? HistoricalEfficiencyAnalysisStateV1.ZeroDrivers
                : HistoricalEfficiencyAnalysisStateV1.Succeeded)
            || !OrderedDistinct(receipt.ComparisonNotes)
            || !ValidQualityNote(receipt.QualityAvailability, receipt.ComparisonNotes)
            || receipt.Coverage.IncludedSessionCount is < 0 or > HistoricalEvidenceContractsV1.MaximumSessions
            || receipt.Coverage.ExcludedSessionCount is < 0 or > 401
            || receipt.Coverage.TruncatedSessionCount < 0
            || !ValidCounts(receipt.Coverage.Completeness, CompletenessKeys(), receipt.Coverage.IncludedSessionCount, exactTotal: true)
            || !ValidCounts(receipt.Coverage.SourceKinds, SourceKindKeys(), receipt.Coverage.IncludedSessionCount, exactTotal: true)
            || !ValidCounts(receipt.Coverage.Capabilities, CapabilityKeys, receipt.Coverage.IncludedSessionCount, exactTotal: false)
            || receipt.ComparisonNotes.Contains(HistoricalEfficiencyComparisonNoteV1.TruncatedWindow) != receipt.Coverage.TruncatedBefore
            || receipt.ComparisonNotes.Contains(HistoricalEfficiencyComparisonNoteV1.PartialCompleteness)
                != receipt.Coverage.Completeness.Any(value => value.Key == SessionWire.ToWire(SessionCompleteness.Partial))
            || receipt.ComparisonNotes.Contains(HistoricalEfficiencyComparisonNoteV1.HistoricalSummaryPresent)
                != receipt.Coverage.SourceKinds.Any(value => value.Key == "historical_summary"))
            throw Invalid();

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var coverage = receipt.CategoryCoverage[index];
            var hasDriver = receipt.Drivers.Any(value => value.Category == rule.Category);
            if (coverage.DriverVersion != rule.DriverVersion || coverage.RuleSource != rule.RuleSource
                || !coverage.RequiredCapabilities.SequenceEqual(rule.RequiredCapabilities)
                || coverage.Formula != rule.Formula || coverage.Threshold != rule.Threshold
                || coverage.MinimumSample != rule.MinimumSample
                || coverage.EligibleSessionCount is < 0 || coverage.EligibleSessionCount > receipt.Coverage.IncludedSessionCount
                || coverage.ObservedSampleCount is < 0 or > 819_200
                || !OrderedDistinct(coverage.Reasons)
                || hasDriver != (coverage.State == HistoricalEfficiencyCoverageStateV1.Matched)
                || !ValidCoverageState(rule, coverage))
                throw Invalid();
        }

        var orderedDrivers = receipt.Drivers.OrderBy(value => value.Category)
            .ThenBy(value => value.SubjectSessionId, StringComparer.Ordinal)
            .ThenBy(value => value.DriverId, StringComparer.Ordinal);
        if (!receipt.Drivers.SequenceEqual(orderedDrivers)) throw Invalid();
        foreach (var driver in receipt.Drivers)
            ValidateDriver(driver, rules.Single(value => value.Category == driver.Category),
                receipt.ExtractionId, receipt.ExtractionSha256);
    }

    private static void ValidateDriver(
        HistoricalEfficiencyDriverV1 driver,
        HistoricalEfficiencyDriverRuleV1 rule,
        string extractionId,
        string extractionSha256)
    {
        var modelMix = driver.Category == HistoricalEfficiencyDriverCategoryV1.ModelMixObservation;
        if (rule.UnsupportedReason is not null
            || !Token(driver.DriverId, "historical-efficiency-driver-")
            || driver.ExtractionId != extractionId
            || driver.ExtractionSha256 != extractionSha256
            || driver.DriverId != HistoricalEfficiencyAnalyzerV1.CalculateDriverId(driver)
            || driver.DriverVersion != rule.DriverVersion || driver.RuleSource != rule.RuleSource
            || driver.Formula != rule.Formula || driver.Threshold != rule.Threshold
            || modelMix != (driver.SubjectSessionId is null)
            || driver.SubjectSessionId is not null && !SessionToken(driver.SubjectSessionId)
            || driver.SourceSessions.Count is < 1 or > HistoricalEvidenceContractsV1.MaximumSessions
            || !driver.SourceSessions.SequenceEqual(driver.SourceSessions.Order(StringComparer.Ordinal))
            || driver.SourceSessions.Distinct(StringComparer.Ordinal).Count() != driver.SourceSessions.Count
            || driver.SubjectSessionId is not null && !driver.SourceSessions.Contains(driver.SubjectSessionId, StringComparer.Ordinal)
            || !ValidReferences(driver.EvidenceRefs, driver.SourceSessions, requireOne: true)
            || !ValidReferences(driver.QualityEvidenceRefs, driver.SourceSessions, requireOne: false)
            || !driver.Mitigation.EvidenceRefs.SequenceEqual(driver.EvidenceRefs)
            || driver.ObservedValues.Count is < 1 or > 16
            || driver.ObservedValues.Any(value => !Code(value.Name, 64) || value.Value < 0 || !Code(value.Unit, 32))
            || driver.ObservedValues.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != driver.ObservedValues.Count
            || !ValidObservedShape(driver)
            || driver.CohortMedian is not null && (!Code(driver.CohortMedian.Name, 64)
                || driver.CohortMedian.Value < 0 || !Code(driver.CohortMedian.Unit, 32))
            || driver.CohortPercentile is not null && (driver.CohortPercentile.Percentile != 75
                || !Code(driver.CohortPercentile.Name, 64) || driver.CohortPercentile.Value < 0
                || !Code(driver.CohortPercentile.Unit, 32))
            || !ValidCohortShape(driver)
            || !ValidCounts(driver.SourceDistribution.SourceSurfaces, SourceSurfaceKeys(), driver.SourceSessions.Count, exactTotal: true)
            || !ValidCounts(driver.SourceDistribution.SourceKinds, SourceKindKeys(), driver.SourceSessions.Count, exactTotal: true)
            || !ValidCounts(driver.CompletenessDistribution, CompletenessKeys(), driver.SourceSessions.Count, exactTotal: true)
            || !OrderedDistinct(driver.ComparisonNotes)
            || driver.Summary != HistoricalEfficiencyDriverRegistryV1.Summary(driver.Category)
            || driver.Mitigation.Code != rule.MitigationCode
            || driver.Mitigation.Summary != HistoricalEfficiencyDriverRegistryV1.MitigationSummary(driver.Category)
            || !ValidQualityNote(driver.QualityAvailability, driver.ComparisonNotes)
            || modelMix && driver.Verdict == HistoricalEfficiencyDriverVerdictV1.Supported)
            throw Invalid();
    }

    private static bool ValidObservedShape(HistoricalEfficiencyDriverV1 driver)
    {
        var expected = driver.Category switch
        {
            HistoricalEfficiencyDriverCategoryV1.TokenVolume => new[] { ("session_total", "token"), ("median_ratio", "ratio") },
            HistoricalEfficiencyDriverCategoryV1.ContextGrowth =>
                [("first_input_tokens", "input_token"), ("last_input_tokens", "input_token"), ("context_growth_ratio", "ratio")],
            HistoricalEfficiencyDriverCategoryV1.CacheInefficiency =>
                [("included_input_tokens", "input_token"), ("included_cache_read_tokens", "cache_read_token"), ("cache_read_ratio", "ratio")],
            HistoricalEfficiencyDriverCategoryV1.RetryOverhead => [("attempt_count", "attempt"), ("retry_overhead", "attempt")],
            HistoricalEfficiencyDriverCategoryV1.DurationOutlier => [("session_duration", "millisecond"), ("median_ratio", "ratio")],
            HistoricalEfficiencyDriverCategoryV1.ModelMixObservation => [("distinct_model_count", "model")],
            _ => [],
        };
        return driver.ObservedValues.Select(value => (value.Name, value.Unit)).SequenceEqual(expected);
    }

    private static bool ValidCoverageState(HistoricalEfficiencyDriverRuleV1 rule, HistoricalEfficiencyCategoryCoverageV1 coverage) =>
        coverage.State switch
        {
            HistoricalEfficiencyCoverageStateV1.Matched => coverage.Reasons.Count == 0 && rule.UnsupportedReason is null,
            HistoricalEfficiencyCoverageStateV1.NoMatch =>
                coverage.Reasons.SequenceEqual([HistoricalEfficiencyCoverageReasonV1.NoThresholdMatch]) && rule.UnsupportedReason is null,
            HistoricalEfficiencyCoverageStateV1.Unavailable when rule.UnsupportedReason is not null =>
                coverage.Reasons.SequenceEqual([rule.UnsupportedReason.Value]),
            HistoricalEfficiencyCoverageStateV1.Unavailable =>
                coverage.Reasons.SequenceEqual([HistoricalEfficiencyCoverageReasonV1.MissingRequiredCapability]),
            HistoricalEfficiencyCoverageStateV1.Insufficient => coverage.Reasons.Count > 0
                && coverage.Reasons.All(value => value is HistoricalEfficiencyCoverageReasonV1.MissingRequiredMetric
                    or HistoricalEfficiencyCoverageReasonV1.MixedEvaluationDimension
                    or HistoricalEfficiencyCoverageReasonV1.MinimumSampleUnmet),
            _ => false,
        };

    private static bool ValidCohortShape(HistoricalEfficiencyDriverV1 driver)
    {
        var comparative = driver.Category is HistoricalEfficiencyDriverCategoryV1.TokenVolume
            or HistoricalEfficiencyDriverCategoryV1.DurationOutlier;
        return comparative == (driver.CohortMedian is not null) && comparative == (driver.CohortPercentile is not null);
    }

    private static bool ValidQualityNote(
        HistoricalEfficiencyQualityAvailabilityV1 quality,
        IReadOnlyList<HistoricalEfficiencyComparisonNoteV1> notes)
    {
        var expected = quality switch
        {
            HistoricalEfficiencyQualityAvailabilityV1.Unavailable => HistoricalEfficiencyComparisonNoteV1.QualityUnavailable,
            HistoricalEfficiencyQualityAvailabilityV1.Partial => HistoricalEfficiencyComparisonNoteV1.QualityPartial,
            HistoricalEfficiencyQualityAvailabilityV1.RegressionObserved => HistoricalEfficiencyComparisonNoteV1.QualityRegressionObserved,
            _ => (HistoricalEfficiencyComparisonNoteV1?)(null),
        };
        var qualityNotes = notes.Where(value => value is HistoricalEfficiencyComparisonNoteV1.QualityUnavailable
            or HistoricalEfficiencyComparisonNoteV1.QualityPartial
            or HistoricalEfficiencyComparisonNoteV1.QualityRegressionObserved).ToArray();
        return expected is null ? qualityNotes.Length == 0 : qualityNotes.SequenceEqual([expected.Value]);
    }

    private static bool ValidReferences(
        IReadOnlyList<HistoricalEvidenceReferenceV1> references,
        IReadOnlyList<string> sourceSessions,
        bool requireOne)
    {
        if (requireOne && references.Count == 0 || references.Count > HistoricalEvidenceContractsV1.MaximumEventsPerSession * HistoricalEvidenceContractsV1.MaximumSessions)
            return false;
        if (!references.SequenceEqual(references.OrderBy(ReferenceSortKey, StringComparer.Ordinal))
            || references.Distinct().Count() != references.Count) return false;
        return references.All(value => sourceSessions.Contains(value.SessionId, StringComparer.Ordinal)
            && SessionToken(value.SessionId) && Token(value.TraceId, "trace-ref-")
            && (value.SpanId is null || Token(value.SpanId, "span-ref-"))
            && value.TurnIndex is null or > 0 && (value.SpanId is not null || value.TurnIndex is not null));
    }

    private static string ReferenceSortKey(HistoricalEvidenceReferenceV1 value) => string.Join('\u001f',
        value.SessionId, value.TraceId, value.SpanId ?? string.Empty,
        value.TurnIndex?.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        ((int)value.RelativePosition).ToString("D2", System.Globalization.CultureInfo.InvariantCulture));

    private static bool ValidCounts(
        IReadOnlyList<HistoricalDistributionCountV1> counts,
        IReadOnlySet<string> allowedKeys,
        int maximumTotal,
        bool exactTotal)
    {
        if (!counts.Select(value => value.Key).SequenceEqual(counts.Select(value => value.Key).Order(StringComparer.Ordinal))
            || counts.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != counts.Count
            || counts.Any(value => !allowedKeys.Contains(value.Key) || value.Count is < 1 or > HistoricalEvidenceContractsV1.MaximumSessions))
            return false;
        var total = counts.Sum(value => value.Count);
        return exactTotal ? total == maximumTotal : counts.All(value => value.Count <= maximumTotal);
    }

    private static bool OrderedDistinct<T>(IReadOnlyList<T> values) where T : struct, Enum =>
        values.SequenceEqual(values.Order()) && values.Distinct().Count() == values.Count;

    private static bool Code(string value, int maximumLength) => value.Length is > 0 && value.Length <= maximumLength
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool Hash(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Token(string value, string prefix) => value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).ToArray().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SessionToken(string value) => Token(value, "session-ref-");

    private static IReadOnlySet<string> CompletenessKeys() => Enum.GetValues<SessionCompleteness>()
        .Select(value => SessionWire.ToWire(value)).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> SourceSurfaceKeys() => Enum.GetValues<SessionSourceSurface>()
        .Select(value => SessionWire.ToWire(value)).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> SourceKindKeys() => Enum.GetValues<HistoricalEvidenceSourceKindV1>()
        .Select(value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString())).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> CapabilityKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "turn_rollup", "token_rollup", "cache_rollup", "error_span", "retry_chain", "repeated_tool_call",
        "permission_wait", "subagent_fan_out", "raw_local_descriptor", "quality_reference", "source_comparison",
        "instruction_finding_reference",
    };

    private static HistoricalEfficiencyValidationException Invalid() =>
        new(HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}

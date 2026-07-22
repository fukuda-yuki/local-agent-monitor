using System.Globalization;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class HistoricalEfficiencyTestData
{
    internal static HistoricalEvidenceExtractionV1 Extraction(params SyntheticSession[] sessions) =>
        Extraction(sessions, [], truncatedBefore: false, truncatedSessionCount: 0);

    internal static HistoricalEvidenceExtractionV1 Extraction(
        IReadOnlyList<SyntheticSession> sessions,
        IReadOnlyList<HistoricalExcludedSessionV1> excluded,
        bool truncatedBefore,
        long truncatedSessionCount)
    {
        var orderedSessions = sessions.OrderBy(value => value.Index).Select(value => value.Session).ToArray();
        var groups = sessions.OrderBy(value => value.Index).SelectMany(value => value.Groups)
            .OrderBy(value => Array.FindIndex(orderedSessions, session => session.SessionId == value.SessionId))
            .ThenBy(value => value.Kind)
            .ThenBy(value => value.GroupId, StringComparer.Ordinal)
            .ToArray();
        var dataset = new HistoricalEvidenceDatasetV1(
            HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion,
            "historical-extraction-00000000000000000000000000000001",
            "synthetic-snapshot-v1",
            HistoricalEvidenceRepresentationV1.RepositorySafe,
            new HistoricalEvidenceSelectionProjectionV1(
                Repository(1), null, null, null, [], [], null, null,
                HistoricalEvidenceContractsV1.DefaultMaximumSessions, true),
            truncatedBefore,
            truncatedSessionCount,
            orderedSessions,
            excluded.OrderBy(value => value.SessionId, StringComparer.Ordinal).ToArray(),
            groups,
            Distribution(orderedSessions));
        var safeBytes = HistoricalEvidenceJsonV1.Serialize(dataset);
        var rawSentinel = Encoding.UTF8.GetBytes("raw prompt person@example.com C:\\private\\workspace");
        return new(dataset, dataset, rawSentinel, safeBytes,
            HistoricalEvidenceExtractorV1.Sha256(rawSentinel), HistoricalEvidenceExtractorV1.Sha256(safeBytes));
    }

    internal static HistoricalExcludedSessionV1 MissingSession(int index) =>
        new(Session(index), HistoricalSessionExclusionReasonV1.MissingSessionReference, null);

    internal static string Session(int value) => Token("session-ref", value);
    internal static string Trace(int value) => Token("trace-ref", value);
    internal static string Span(int value) => Token("span-ref", value);
    internal static string Model(int value) => Token("model-ref", value);
    internal static string SourceVersion(int value) => Token("source-version-ref", value);
    internal static string AdapterVersion(int value) => Token("adapter-version-ref", value);
    internal static string Repository(int value) => Token("repository-ref", value);

    private static string Token(string prefix, int value) =>
        $"{prefix}-{value.ToString("x32", CultureInfo.InvariantCulture)}";

    private static HistoricalEvidenceDistributionV1 Distribution(IReadOnlyList<HistoricalEvidenceSessionV1> sessions)
    {
        var completeness = sessions.GroupBy(value => SessionWire.ToWire(value.Completeness), StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new HistoricalDistributionCountV1(value.Key, value.Count())).ToArray();
        var sourceKinds = sessions.GroupBy(value => Snake(value.SourceKind), StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new HistoricalDistributionCountV1(value.Key, value.Count())).ToArray();
        var capabilities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            foreach (var (key, available) in CapabilityValues(session.Capabilities))
                if (available) capabilities[key] = capabilities.GetValueOrDefault(key) + 1;
        }
        return new(completeness, sourceKinds,
            capabilities.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new HistoricalDistributionCountV1(value.Key, value.Value)).ToArray());
    }

    private static IEnumerable<(string Key, bool Available)> CapabilityValues(HistoricalSessionCapabilitiesV1 value)
    {
        yield return ("turn_rollup", value.TurnRollup);
        yield return ("token_rollup", value.TokenRollup);
        yield return ("cache_rollup", value.CacheRollup);
        yield return ("error_span", value.ErrorSpan);
        yield return ("retry_chain", value.RetryChain);
        yield return ("repeated_tool_call", value.RepeatedToolCall);
        yield return ("permission_wait", value.PermissionWait);
        yield return ("subagent_fan_out", value.SubagentFanOut);
        yield return ("raw_local_descriptor", value.RawLocalDescriptor);
        yield return ("quality_reference", value.QualityReference);
        yield return ("source_comparison", value.SourceComparison);
        yield return ("instruction_finding_reference", value.InstructionFindingReference);
    }

    private static string Snake<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

internal sealed class SyntheticSession
{
    private readonly List<HistoricalEvidenceGroupV1> groups = [];
    private readonly string sessionId;
    private readonly string traceId;
    private int groupSequence;

    internal SyntheticSession(
        int index,
        SessionSourceSurface sourceSurface = SessionSourceSurface.CopilotSdk,
        int sourceVersion = 1,
        int adapterVersion = 1,
        int? model = 1,
        SessionCompleteness completeness = SessionCompleteness.Full,
        HistoricalEvidenceSourceKindV1 sourceKind = HistoricalEvidenceSourceKindV1.LiveOtel)
    {
        Index = index;
        SourceSurface = sourceSurface;
        SourceVersion = sourceVersion;
        AdapterVersion = adapterVersion;
        ModelIndex = model;
        Completeness = completeness;
        SourceKind = sourceKind;
        sessionId = HistoricalEfficiencyTestData.Session(index);
        traceId = HistoricalEfficiencyTestData.Trace(index);
    }

    internal int Index { get; }
    internal SessionSourceSurface SourceSurface { get; }
    internal int SourceVersion { get; }
    internal int AdapterVersion { get; }
    internal int? ModelIndex { get; }
    internal SessionCompleteness Completeness { get; }
    internal HistoricalEvidenceSourceKindV1 SourceKind { get; }
    internal IReadOnlyList<HistoricalEvidenceGroupV1> Groups => groups;
    internal List<HistoricalDurationObservationV1> Durations { get; } = [];

    internal HistoricalEvidenceSessionV1 Session
    {
        get
        {
            var capabilities = Capabilities();
            var reasons = CompletenessReasons();
            var sourceVersion = HistoricalEfficiencyTestData.SourceVersion(SourceVersion);
            var adapterVersion = HistoricalEfficiencyTestData.AdapterVersion(AdapterVersion);
            var provenance = new[] { new HistoricalSourceProvenanceV1(SourceSurface, sourceVersion, adapterVersion) };
            var modelReference = DefaultReference();
            var models = ModelIndex is null
                ? []
                : new[] { new HistoricalModelObservationV1(HistoricalEfficiencyTestData.Model(ModelIndex.Value), modelReference) };
            var metadata = new HistoricalDecisionMetadataV1(
                null,
                null,
                new DateTimeOffset(2026, 7, 1, 0, Index, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 1, 0, Index, 30, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 1, 0, Index, 30, TimeSpan.Zero),
                [SourceSurface],
                provenance,
                models,
                Durations.ToArray(),
                Completeness,
                reasons,
                SourceKind,
                SessionContentState.Available,
                capabilities);
            return new HistoricalEvidenceSessionV1(
                sessionId,
                SourceSurface,
                sourceVersion,
                adapterVersion,
                Completeness,
                reasons,
                SourceKind,
                SessionContentState.Available,
                HistoricalDescriptorStateV1.Unavailable,
                null,
                capabilities,
                metadata);
        }
    }

    internal SyntheticSession AddTurn(
        int turnIndex,
        long? totalTokens = null,
        long? inputTokens = null,
        long? outputTokens = null,
        long? cacheReadTokens = null)
    {
        var reference = TurnReference(turnIndex);
        Add(HistoricalEvidenceGroupKindV1.TurnRollup, [reference], 1, "turn");
        if (totalTokens is not null) Add(HistoricalEvidenceGroupKindV1.TokenRollup, [reference], totalTokens, HistoricalEvidenceScalarUnitsV1.TotalToken);
        if (inputTokens is not null) Add(HistoricalEvidenceGroupKindV1.TokenRollup, [reference], inputTokens, HistoricalEvidenceScalarUnitsV1.InputToken);
        if (outputTokens is not null) Add(HistoricalEvidenceGroupKindV1.TokenRollup, [reference], outputTokens, HistoricalEvidenceScalarUnitsV1.OutputToken);
        if (cacheReadTokens is not null) Add(HistoricalEvidenceGroupKindV1.CacheRollup, [reference], cacheReadTokens, HistoricalEvidenceScalarUnitsV1.CacheReadToken);
        return this;
    }

    internal SyntheticSession AddQuality(string status)
    {
        Add(HistoricalEvidenceGroupKindV1.QualityReference, [DefaultReference()], null, null, status);
        return this;
    }

    internal SyntheticSession AddDuration(long milliseconds)
    {
        Durations.Add(new(milliseconds, SpanReference(600 + Durations.Count)));
        return this;
    }

    internal SyntheticSession AddRetry(int attempts, string status = "error")
    {
        var references = Enumerable.Range(1, attempts).Select(value => SpanReference(100 + value)).ToArray();
        Add(HistoricalEvidenceGroupKindV1.RetryChain, references, attempts, "attempt", status);
        return this;
    }

    internal SyntheticSession AddDuplicateRetry(int attempts, string status = "error") => AddRetry(attempts, status);

    internal SyntheticSession AddRepeatedToolCalls(int count)
    {
        var references = Enumerable.Range(1, count).Select(value => SpanReference(200 + value)).ToArray();
        Add(HistoricalEvidenceGroupKindV1.RepeatedToolCall, references, count, "call", null,
            canonicalCallHash: new string('a', 64));
        return this;
    }

    internal SyntheticSession AddPermissionWait(params long[] seconds)
    {
        for (var index = 0; index < seconds.Length; index++)
            Add(HistoricalEvidenceGroupKindV1.PermissionWait, [SpanReference(300 + index)], seconds[index], "seconds");
        return this;
    }

    internal SyntheticSession AddPermissionWaitWithUnit(long value, string unit)
    {
        Add(HistoricalEvidenceGroupKindV1.PermissionWait, [SpanReference(350)], value, unit);
        return this;
    }

    internal SyntheticSession AddSubagentFanout(int count)
    {
        for (var index = 0; index < count; index++)
            Add(HistoricalEvidenceGroupKindV1.SubagentFanOut, [SpanReference(400 + index)], 1, "agent");
        return this;
    }

    internal SyntheticSession AddErrorSpan()
    {
        Add(HistoricalEvidenceGroupKindV1.ErrorSpan, [SpanReference(500)], null, null, "error");
        return this;
    }

    private HistoricalSessionCapabilitiesV1 Capabilities() => new(
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.TurnRollup),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.TokenRollup),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.CacheRollup),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.ErrorSpan),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.RetryChain),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.RepeatedToolCall),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.PermissionWait),
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.SubagentFanOut),
        false,
        groups.Any(value => value.Kind == HistoricalEvidenceGroupKindV1.QualityReference),
        false,
        false);

    private IReadOnlyList<string> CompletenessReasons()
    {
        if (Completeness == SessionCompleteness.Full) return [];
        if (SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary) return ["historical_summary_only"];
        return ["ingest_gap"];
    }

    private HistoricalEvidenceReferenceV1 DefaultReference() =>
        groups.SelectMany(value => value.References).FirstOrDefault() ?? SpanReference(1);

    private HistoricalEvidenceReferenceV1 TurnReference(int turnIndex) =>
        new(sessionId, traceId, HistoricalEfficiencyTestData.Span(Index * 1000 + turnIndex), turnIndex, HistoricalEvidenceRelativePositionV1.Anchor);

    private HistoricalEvidenceReferenceV1 SpanReference(int offset) =>
        new(sessionId, traceId, HistoricalEfficiencyTestData.Span(Index * 1000 + offset), null, HistoricalEvidenceRelativePositionV1.Anchor);

    private void Add(
        HistoricalEvidenceGroupKindV1 kind,
        IReadOnlyList<HistoricalEvidenceReferenceV1> references,
        long? numericValue,
        string? unit,
        string? status = null,
        string? canonicalCallHash = null)
    {
        var id = $"historical-group-{(Index * 1000 + ++groupSequence).ToString("x32", CultureInfo.InvariantCulture)}";
        groups.Add(new HistoricalEvidenceGroupV1(
            id,
            kind,
            sessionId,
            references.OrderBy(value => value.SessionId, StringComparer.Ordinal)
                .ThenBy(value => value.TraceId, StringComparer.Ordinal)
                .ThenBy(value => value.SpanId, StringComparer.Ordinal)
                .ThenBy(value => value.TurnIndex)
                .ThenBy(value => value.RelativePosition)
                .ToArray(),
            numericValue,
            unit,
            status,
            null,
            canonicalCallHash,
            null,
            null,
            null,
            null));
    }
}

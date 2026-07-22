using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class HistoricalEvidenceApplicationServiceV1
{
    private readonly IHistoricalEvidenceSnapshotSourceV1 source;
    private readonly SqliteHistoricalEvidenceDatasetStoreV1 store;
    private readonly TimeProvider timeProvider;

    internal HistoricalEvidenceApplicationServiceV1(
        IHistoricalEvidenceSnapshotSourceV1 source,
        SqliteHistoricalEvidenceDatasetStoreV1 store,
        TimeProvider? timeProvider = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async ValueTask<HistoricalEvidenceExtractionV1> CreateAsync(
        HistoricalEvidenceSelectionV1 selection,
        CancellationToken cancellationToken)
    {
        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, cancellationToken).ConfigureAwait(false);
        store.Save(extraction, timeProvider.GetUtcNow());
        return extraction;
    }

    internal HistoricalEvidenceExtractionV1? Get(string extractionId) => store.Get(extractionId);
}

internal interface IHistoricalSessionContentReaderV1
{
    ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken);
}

internal sealed class HistoricalSessionContentReaderV1(ISessionStore store) : IHistoricalSessionContentReaderV1
{
    public ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken) =>
        store.ReadContentAsync(sessionId, eventId, cancellationToken);
}

internal sealed class SqliteHistoricalEvidenceSnapshotSourceV1 : IHistoricalEvidenceSnapshotSourceV1
{
    private readonly string databasePath;
    private readonly ISessionStore sessionStore;
    private readonly IHistoricalSessionContentReaderV1 contentReader;

    internal SqliteHistoricalEvidenceSnapshotSourceV1(
        string databasePath,
        ISessionStore sessionStore,
        IHistoricalSessionContentReaderV1? contentReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.contentReader = contentReader ?? new HistoricalSessionContentReaderV1(sessionStore);
    }

    public async ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
        HistoricalEvidenceSelectionV1 selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var matching = ReadMatchingRows(connection, transaction, selection, selection.MaximumSessionCount + 1);
        var matchingCount = CountMatching(connection, transaction, selection);
        var explicitRows = ReadExplicitRows(connection, transaction, selection.ExplicitSessionIds);
        var rows = matching.Concat(explicitRows)
            .GroupBy(row => row.SessionId)
            .Select(group => group.First())
            .OrderBy(row => row.StartedAt ?? row.LastSeenAt)
            .ThenBy(row => row.SessionId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var returnedMatchingCount = CountMatchingReturned(connection, transaction, selection, rows);
        var details = new Dictionary<Guid, SessionDetail>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            details.Add(row.SessionId, ReadBoundedDetail(connection, transaction, row));
        }
        var spans = details.ToDictionary(pair => pair.Key, pair => FilterExactSpans(pair.Value, ReadBoundedSpans(connection, transaction, pair.Key)));
        if (details.SelectMany(pair => ExactTraceIds(pair.Value).Select(traceId => (traceId, pair.Key)))
            .GroupBy(item => item.traceId, StringComparer.Ordinal).Any(group => group.Select(item => item.Key).Distinct().Count() > 1))
            throw ChildOverflow();
        var objectives = details.ToDictionary(pair => pair.Key, pair => ReadBoundedObjectives(connection, transaction, pair.Key));
        var handoffRows = ReadBoundedHandoffs(connection, transaction, rows);
        var associations = AssociateFindings(details, spans, handoffRows);
        transaction.Commit();

        var retention = rows.ToDictionary(row => row.SessionId, row => sessionStore.GetRawRetentionState(row.SessionId));
        var descriptorSessionIds = DescriptorSessionIds(selection, rows, details, spans, associations);
        var descriptors = await ReadDescriptorsAsync(selection, details, spans, objectives, associations, retention, descriptorSessionIds, cancellationToken).ConfigureAwait(false);
        var metadata = rows.Select(row => ProjectMetadata(details[row.SessionId], spans[row.SessionId], objectives[row.SessionId], retention[row.SessionId], descriptors[row.SessionId], associations.GetValueOrDefault(row.SessionId, []))).ToArray();
        var omitted = Math.Max(0, matchingCount - returnedMatchingCount);
        var snapshotId = SnapshotId(selection, rows, matchingCount, returnedMatchingCount, omitted, details, spans, objectives, retention, descriptors, handoffRows);
        return new Lease(snapshotId, metadata, omitted, details, spans, objectives, associations, descriptors);
    }

    private static HistoricalSessionMetadataV1 ProjectMetadata(SessionDetail detail, IReadOnlyList<MonitorSpanRow> spans,
        IReadOnlyList<SnapshotObjectiveRow> objectives,
        SessionRawRetentionState retentionState, IReadOnlyList<HistoricalEvidenceGroupDraftV1> descriptors,
        IReadOnlyList<InstructionFindingAssociation> findings)
    {
        var exactEvents = ExactLocations(detail, spans).Distinct().ToArray();
        var eventByRun = detail.Events.Where(value => value.RunId is not null)
            .GroupBy(value => value.RunId!.Value).ToDictionary(group => group.Key, group => group.Select(TryExactReference).FirstOrDefault(value => value is not null));
        var provenance = detail.Events.Where(value => value.SourceSurface is not null).Select(value => new HistoricalSourceProvenanceV1(
                value.SourceSurface!.Value,
                value.SourceApplicationVersion,
                value.AdapterVersion))
            .Distinct().OrderBy(value => value.SourceSurface).ThenBy(value => value.SourceApplicationVersion, StringComparer.Ordinal).ThenBy(value => value.AdapterVersion, StringComparer.Ordinal).ToArray();
        var models = detail.Runs.Where(run => run.Model is not null && eventByRun.GetValueOrDefault(run.RunId) is not null)
            .Select(run => new HistoricalRawModelObservationV1(run.Model!, eventByRun[run.RunId]!))
            .Concat(spans.Where(span => span.SpanId is not null && (span.ResponseModel is not null || span.RequestModel is not null)).Select(span => new HistoricalRawModelObservationV1(
                span.ResponseModel ?? span.RequestModel!, new(detail.Session.SessionId, span.TraceId, span.SpanId, null, HistoricalEvidenceRelativePositionV1.Anchor))))
            .Distinct().ToArray();
        var durations = detail.Runs.Select(run => (Run: run, Duration: run.StartedAt is not null && run.EndedAt >= run.StartedAt
                ? IntegralMilliseconds((run.EndedAt.Value - run.StartedAt.Value).TotalMilliseconds) : null))
            .Where(item => item.Duration is not null && eventByRun.GetValueOrDefault(item.Run.RunId) is not null)
            .Select(item => new HistoricalRawDurationObservationV1(item.Duration!.Value, eventByRun[item.Run.RunId]!))
            .Concat(spans.Where(span => span.SpanId is not null && IntegralMilliseconds(span.DurationMs) is not null).Select(span => new HistoricalRawDurationObservationV1(
                IntegralMilliseconds(span.DurationMs)!.Value, new(detail.Session.SessionId, span.TraceId, span.SpanId, null, HistoricalEvidenceRelativePositionV1.Anchor))))
            .Distinct().ToArray();
        var surfaces = detail.NativeIds.Select(value => value.SourceSurface)
            .Concat(detail.Runs.Select(value => value.SourceSurface).OfType<SessionSourceSurface>())
            .Concat(detail.Events.Select(value => value.SourceSurface).OfType<SessionSourceSurface>())
            .Distinct().Order().ToArray();
        provenance = provenance
            .Concat(surfaces.Where(surface => provenance.All(value => value.SourceSurface != surface))
                .Select(surface => new HistoricalSourceProvenanceV1(surface, null, null)))
            .OrderBy(value => value.SourceSurface)
            .ThenBy(value => value.SourceApplicationVersion, StringComparer.Ordinal)
            .ThenBy(value => value.AdapterVersion, StringComparer.Ordinal)
            .ToArray();
        var primary = surfaces.Length == 0 ? SessionSourceSurface.HookUnknown : surfaces[0];
        var accepted = spans.GroupBy(span => span.TraceId, StringComparer.Ordinal)
            .Select(group => InstructionEvidenceExtractor.Extract(group.Key, group.ToArray(), [], [])).ToArray();
        var capabilities = new HistoricalSessionCapabilitiesV1(
            TurnRollup: spans.Any(IsTurn),
            TokenRollup: spans.Any(span => IsTurn(span) && (span.TotalTokens is not null || span.InputTokens is not null || span.OutputTokens is not null))
                || detail.Runs.Any(run => run.TotalTokens is not null && eventByRun.GetValueOrDefault(run.RunId) is not null),
            CacheRollup: spans.Any(span => span.CacheReadTokens is not null || span.CacheCreationTokens is not null),
            ErrorSpan: spans.Any(span => span.Status == "error" && span.SpanId is not null),
            RetryChain: accepted.Any(evidence => evidence.RetryChains.Any(chain => chain.SpanIds.Count is > 1 and <= HistoricalEvidenceContractsV1.MaximumReferencesPerGroup && chain.SpanIds.All(id => id is not null))),
            RepeatedToolCall: false, PermissionWait: false, SubagentFanOut: false,
            RawLocalDescriptor: descriptors.Count > 0,
            QualityReference: objectives.Any(objective => exactEvents.Any(reference => reference.TraceId == objective.TraceId)),
            SourceComparison: false, InstructionFindingReference: findings.Count > 0);
        return new HistoricalSessionMetadataV1(
            detail.Session.SessionId, primary, provenance.FirstOrDefault()?.SourceApplicationVersion,
            provenance.FirstOrDefault()?.AdapterVersion, detail.Session.Completeness, CompletenessReasons(detail, spans),
            spans.Count > 0 ? HistoricalEvidenceSourceKindV1.LiveOtel : HistoricalEvidenceSourceKindV1.SavedRaw,
            CapturedContentState(detail, retentionState),
            detail.Session.Repository, detail.Session.Workspace, null, null, detail.Session.StartedAt, detail.Session.LastSeenAt,
            capabilities, exactEvents.Concat(findings.SelectMany(value => value.References)).Distinct()
                .Select(value => new HistoricalEvidenceLocationV1(value.SessionId, value.TraceId, value.SpanId, value.TurnIndex, value.RelativePosition)).ToArray(),
            findings.Select(value => value.Receipt.FindingId).Order(StringComparer.Ordinal).ToArray())
        {
            EndedAt = detail.Session.EndedAt,
            SourceSurfaces = surfaces,
            SourceProvenance = provenance,
            ModelObservations = models,
            DurationObservations = durations,
        };
    }

    private static IReadOnlyList<string> CompletenessReasons(SessionDetail detail, IReadOnlyList<MonitorSpanRow> spans)
    {
        if (detail.Session.Completeness == SessionCompleteness.Full) return [];
        var reasons = new List<string>();
        if (detail.Session.Completeness == SessionCompleteness.Unbound && detail.NativeIds.Count == 0) reasons.Add("missing_native_session_id");
        if (spans.Count == 0) reasons.Add("missing_trace_context");
        if (detail.Events.All(value => value.ContentState != SessionContentState.Available)) reasons.Add("content_capture_disabled");
        if (detail.Events.Any(value => value.Status == "gap_before_capture")) reasons.Add("ingest_gap");
        if (detail.Events.Any(value => value.ContentState == SessionContentState.Unsupported)) reasons.Add("unsupported_source_version");
        if (spans.Count == 0 && detail.Events.Count > 0) reasons.Add("hook_only");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static SessionContentState CapturedContentState(SessionDetail detail, SessionRawRetentionState retentionState)
    {
        if (detail.Events.Any(value => value.ContentState == SessionContentState.Redacted)) return SessionContentState.Redacted;
        if (detail.Events.Any(value => value.ContentState == SessionContentState.Available))
            return retentionState == SessionRawRetentionState.Expiring
                ? SessionContentState.Available
                : SessionContentState.ExpiredPendingDeletion;
        if (detail.Events.Any(value => value.ContentState == SessionContentState.Unsupported)) return SessionContentState.Unsupported;
        return SessionContentState.NotCaptured;
    }

    private static IEnumerable<ObservedSessionEvent> CorrectionEvents(SessionDetail detail) => detail.Events
        .Where(value => value.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted")
        .Where(value => TryExactReference(value) is not null)
        .OrderBy(value => value.OccurredAt).ThenBy(value => value.EventId)
        .Skip(1);

    private async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<HistoricalEvidenceGroupDraftV1>>> ReadDescriptorsAsync(
        HistoricalEvidenceSelectionV1 selection,
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyDictionary<Guid, IReadOnlyList<MonitorSpanRow>> spans,
        IReadOnlyDictionary<Guid, IReadOnlyList<SnapshotObjectiveRow>> objectives,
        IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> findings,
        IReadOnlyDictionary<Guid, SessionRawRetentionState> retention,
        IReadOnlySet<Guid> includedSessionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<HistoricalEvidenceGroupDraftV1>>();
        foreach (var (sessionId, detail) in details)
        {
            var groups = new List<HistoricalEvidenceGroupDraftV1>();
            var correctionEvents = CorrectionEvents(detail).Where(value => value.ContentState == SessionContentState.Available).ToArray();
            if (includedSessionIds.Contains(sessionId)
                && CountNonDescriptorGroups(detail, spans[sessionId], objectives[sessionId], findings.GetValueOrDefault(sessionId, [])) + correctionEvents.Length
                    > HistoricalEvidenceContractsV1.MaximumGroupsPerSession)
                throw ChildOverflow();
            if (includedSessionIds.Contains(sessionId) && !selection.SanitizedOnly
                && CapturedContentState(detail, retention[sessionId]) == SessionContentState.Available
                && retention[sessionId] == SessionRawRetentionState.Expiring)
            {
                foreach (var item in correctionEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (groups.Count == HistoricalEvidenceContractsV1.MaximumGroupsPerSession) throw ChildOverflow();
                    var read = await contentReader.ReadContentAsync(sessionId, item.EventId, cancellationToken).ConfigureAwait(false);
                    if (read.Disposition != SessionContentReadDisposition.Granted || read.Lease is null) continue;
                    await using var lease = read.Lease;
                    var reference = TryExactReference(item);
                    var descriptor = ReadDescriptor(lease.Content.ContentJson);
                    var projected = descriptor is null
                        ? (HistoricalDescriptorStateV1.Unavailable, (string?)null)
                        : HistoricalEvidenceExtractorV1.ProjectDescriptorCandidates(false, [descriptor]);
                    if (reference is not null)
                        groups.Add(new(HistoricalEvidenceGroupKindV1.UserCorrection, [reference], null, null, null, null, null, null, null,
                            projected.Item2, DescriptorCandidateState: projected.Item1));
                }
            }
            result.Add(sessionId, groups);
        }
        return result;
    }

    private static IReadOnlySet<Guid> DescriptorSessionIds(HistoricalEvidenceSelectionV1 selection, IReadOnlyList<SnapshotRow> rows,
        IReadOnlyDictionary<Guid, SessionDetail> details, IReadOnlyDictionary<Guid, IReadOnlyList<MonitorSpanRow>> spans,
        IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> findings)
    {
        var eligible = rows.Where(row => MatchesSourceSelection(selection, details[row.SessionId])
                && details[row.SessionId].Session.Completeness != SessionCompleteness.Unbound
                && (ExactLocations(details[row.SessionId], spans[row.SessionId]).Any() || findings.ContainsKey(row.SessionId)))
            .Select(row => row.SessionId).ToArray();
        return eligible.TakeLast(selection.MaximumSessionCount).ToHashSet();
    }

    private static bool MatchesSourceSelection(HistoricalEvidenceSelectionV1 selection, SessionDetail detail)
    {
        var time = detail.Session.StartedAt ?? detail.Session.LastSeenAt;
        var surfaces = detail.NativeIds.Select(item => item.SourceSurface)
            .Concat(detail.Runs.Select(item => item.SourceSurface).OfType<SessionSourceSurface>())
            .Concat(detail.Events.Select(item => item.SourceSurface).OfType<SessionSourceSurface>()).ToHashSet();
        return !(!HasNonIdScope(selection) && selection.ExplicitSessionIds.Count > 0 && !selection.ExplicitSessionIds.Contains(detail.Session.SessionId)
            || selection.Repository is not null && selection.Repository != detail.Session.Repository
            || selection.Workspace is not null && selection.Workspace != detail.Session.Workspace
            || selection.TaskLabel is not null || selection.ExperimentLabel is not null
            || selection.SourceSurfaces.Count > 0 && !selection.SourceSurfaces.Any(surfaces.Contains)
            || selection.From is { } from && time < from
            || selection.To is { } to && time >= to);
    }

    private static int CountNonDescriptorGroups(SessionDetail detail, IReadOnlyList<MonitorSpanRow> spans,
        IReadOnlyList<SnapshotObjectiveRow> objectives, IReadOnlyList<InstructionFindingAssociation> findings)
    {
        var count = findings.Count;
        foreach (var trace in spans.GroupBy(span => span.TraceId, StringComparer.Ordinal))
        {
            var ordered = trace.OrderBy(span => span.RawRecordId).ThenBy(span => span.SpanOrdinal).ToArray();
            var evidence = InstructionEvidenceExtractor.Extract(trace.Key, ordered, [], []);
            var turnSpans = ordered.Where(IsTurn).ToArray();
            for (var index = 0; index < evidence.TurnTokens.Count; index++)
            {
                var turn = evidence.TurnTokens[index];
                count++;
                var span = turnSpans[index];
                if (span.TotalTokens is not null) count++;
                if (span.InputTokens is not null) count++;
                if (span.OutputTokens is not null) count++;
                if (span.CacheReadTokens is not null) count++;
                if (span.CacheCreationTokens is not null) count++;
            }
            count += ordered.Where(span => !IsTurn(span) && span.SpanId is not null)
                .Sum(span => (span.CacheReadTokens is not null ? 1 : 0) + (span.CacheCreationTokens is not null ? 1 : 0));
            count += ordered.Count(span => span.Status == "error" && span.SpanId is not null);
            count += evidence.RetryChains.Count(chain => chain.SpanIds.Count is > 1 and <= HistoricalEvidenceContractsV1.MaximumReferencesPerGroup && chain.SpanIds.All(id => id is not null));
        }
        count += detail.Runs.Where(run => !spans.Any(span => span.TraceId == run.TraceId && IsTurn(span))
                && detail.Events.Where(value => value.RunId == run.RunId).Select(TryExactReference).Any(value => value is not null))
            .Sum(run => (run.TotalTokens is not null ? 1 : 0) + (run.InputTokens is not null ? 1 : 0) + (run.OutputTokens is not null ? 1 : 0));
        var exactTraceIds = ExactLocations(detail, spans).Select(location => location.TraceId).ToHashSet(StringComparer.Ordinal);
        count += objectives.Count(objective => exactTraceIds.Contains(objective.TraceId));
        return count;
    }

    private static string? ReadDescriptor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            foreach (var name in new[] { "text", "prompt", "message" })
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static HistoricalRawEvidenceReferenceV1? TryExactReference(ObservedSessionEvent value)
    {
        if (value.MatchKind is not (SessionMatchKind.ExactNative or SessionMatchKind.ExplicitLink)
            || value.SourceAdapter is not ("otel-exact" or "claude-code-otel") || value.TraceId is null
            || value.SourceEventId.Split('/') is not [var trace, var span]
            || !string.Equals(trace, value.TraceId, StringComparison.Ordinal) || span.Length == 0)
            return null;
        return new(value.SessionId, trace, span, null, HistoricalEvidenceRelativePositionV1.Anchor);
    }

    private static bool IsTurn(MonitorSpanRow span) => span.Operation == "chat" || span.Category == "llm_call";
    private static IReadOnlySet<string> ExactTraceIds(SessionDetail detail) => detail.Events.Select(TryExactReference)
        .OfType<HistoricalRawEvidenceReferenceV1>().Select(reference => reference.TraceId).ToHashSet(StringComparer.Ordinal);
    private static IReadOnlyList<MonitorSpanRow> FilterExactSpans(SessionDetail detail, IReadOnlyList<MonitorSpanRow> spans)
    {
        var exactTraceIds = ExactTraceIds(detail);
        return spans.Where(span => exactTraceIds.Contains(span.TraceId)).ToArray();
    }
    private static long? IntegralMilliseconds(double? value) => value is { } duration && double.IsFinite(duration)
        && duration >= 0 && duration <= long.MaxValue && duration == Math.Truncate(duration) ? checked((long)duration) : null;

    private static IEnumerable<HistoricalRawEvidenceReferenceV1> ExactLocations(SessionDetail detail, IReadOnlyList<MonitorSpanRow> spans)
    {
        var exactTraces = detail.Events.Select(TryExactReference).OfType<HistoricalRawEvidenceReferenceV1>()
            .Select(reference => reference.TraceId).ToHashSet(StringComparer.Ordinal);
        foreach (var trace in spans.Where(span => exactTraces.Contains(span.TraceId)).GroupBy(span => span.TraceId, StringComparer.Ordinal))
        {
            var ordered = trace.OrderBy(span => span.RawRecordId).ThenBy(span => span.SpanOrdinal).ToArray();
            var evidence = InstructionEvidenceExtractor.Extract(trace.Key, ordered, [], []);
            foreach (var span in ordered.Where(span => span.SpanId is not null))
                yield return new(detail.Session.SessionId, trace.Key, span.SpanId, null, HistoricalEvidenceRelativePositionV1.Anchor);
            foreach (var turn in evidence.TurnTokens)
            {
                yield return new(detail.Session.SessionId, trace.Key, turn.SpanId, turn.TurnIndex, HistoricalEvidenceRelativePositionV1.Anchor);
                yield return new(detail.Session.SessionId, trace.Key, null, turn.TurnIndex, HistoricalEvidenceRelativePositionV1.Anchor);
            }
        }
        foreach (var reference in detail.Events.Select(TryExactReference).OfType<HistoricalRawEvidenceReferenceV1>())
            yield return reference;
    }

    private static string SnapshotId(
        HistoricalEvidenceSelectionV1 selection,
        IReadOnlyList<SnapshotRow> rows,
        long matchingCount,
        int returnedMatchingCount,
        long omitted,
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyDictionary<Guid, IReadOnlyList<MonitorSpanRow>> spans,
        IReadOnlyDictionary<Guid, IReadOnlyList<SnapshotObjectiveRow>> objectives,
        IReadOnlyDictionary<Guid, SessionRawRetentionState> retention,
        IReadOnlyDictionary<Guid, IReadOnlyList<HistoricalEvidenceGroupDraftV1>> descriptors,
        IReadOnlyList<SnapshotHandoffRow> handoffs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSnapshotFact(hash, "domain", Encoding.UTF8.GetBytes("copilot-agent-observability/session-snapshot/v1"));
        AppendSnapshotFact(hash, "selection", HistoricalEvidenceJsonV1.SerializeSelection(HistoricalEvidenceExtractorV1.CanonicalInputSelection(selection)));
        AppendSnapshotFact(hash, "selection-counts", JsonSerializer.SerializeToUtf8Bytes(new { matchingCount, returnedMatchingCount, omitted }));
        foreach (var row in rows)
        {
            AppendSnapshotFact(hash, "session-detail", JsonSerializer.SerializeToUtf8Bytes(details[row.SessionId]));
            AppendSnapshotFact(hash, "exact-spans", JsonSerializer.SerializeToUtf8Bytes(spans[row.SessionId]));
            AppendSnapshotFact(hash, "objectives", JsonSerializer.SerializeToUtf8Bytes(objectives[row.SessionId]));
            AppendSnapshotFact(hash, "retention", JsonSerializer.SerializeToUtf8Bytes(retention[row.SessionId]));
            AppendSnapshotFact(hash, "descriptor-candidates", JsonSerializer.SerializeToUtf8Bytes(descriptors[row.SessionId]
                .Select(group => new { group.DescriptorCandidateState, group.RawDescriptor, group.References }).ToArray()));
            var descriptor = HistoricalEvidenceExtractorV1.ProjectDescriptorDrafts(selection.SanitizedOnly, descriptors[row.SessionId]);
            AppendSnapshotFact(hash, "descriptor", JsonSerializer.SerializeToUtf8Bytes(new { descriptor.State, descriptor.Value }));
        }
        foreach (var handoff in handoffs)
            AppendSnapshotFact(hash, "handoff", JsonSerializer.SerializeToUtf8Bytes(new { handoff.AnalysisRunId, handoff.TraceId, handoff.Checksum }));
        return "session-snapshot-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..32];
    }

    private static void AppendSnapshotFact(IncrementalHash hash, string label, ReadOnlySpan<byte> payload)
    {
        var labelBytes = Encoding.UTF8.GetBytes(label);
        Span<byte> lengths = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(lengths[..4], labelBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(lengths[4..], payload.Length);
        hash.AppendData(lengths);
        hash.AppendData(labelBytes);
        hash.AppendData(payload);
    }

    private static IReadOnlyList<SnapshotHandoffRow> ReadBoundedHandoffs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SnapshotRow> rows)
    {
        if (rows.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = rows.Select((_, index) => "$session" + index).ToArray();
        command.CommandText = $"""
            SELECT h.analysis_run_id,r.trace_id,length(CAST(h.payload_json AS BLOB)),h.payload_json,h.payload_sha256
            FROM instruction_finding_handoffs h
            JOIN monitor_analysis_runs r ON r.id=h.analysis_run_id
            WHERE EXISTS(SELECT 1 FROM session_events e
                WHERE e.trace_id=r.trace_id
                    AND e.session_id IN ({string.Join(',', parameters)})
                    AND e.match_kind IN ('exact_native','explicit_link')
                    AND e.source_adapter IN ('otel-exact','claude-code-otel'))
            ORDER BY h.analysis_run_id
            LIMIT $limit;
            """;
        for (var index = 0; index < parameters.Length; index++)
            command.Parameters.AddWithValue(parameters[index], rows[index].SessionId.ToString("D"));
        command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumInstructionFindingHandoffs + 1);
        using var reader = command.ExecuteReader();
        var result = new List<SnapshotHandoffRow>();
        long totalBytes = 0;
        while (reader.Read())
        {
            HistoricalEvidenceReadBoundsV1.Validate(result.Count + 1, totalBytes, 0, 0);
            var runId = reader.GetInt64(0);
            var traceId = reader.GetString(1);
            var payloadLength = reader.GetInt64(2);
            if (payloadLength is < 1 or > InstructionFindingHandoffConsumerV1.MaxPayloadBytes) throw ChildOverflow();
            try { totalBytes = checked(totalBytes + payloadLength); }
            catch (OverflowException) { throw ChildOverflow(); }
            HistoricalEvidenceReadBoundsV1.Validate(result.Count + 1, totalBytes, 0, 0);
            var payload = Encoding.UTF8.GetBytes(reader.GetString(3));
            if (payload.Length != payloadLength) throw ChildOverflow();
            var checksum = reader.GetString(4);
            if (!string.Equals(HistoricalEvidenceExtractorV1.Sha256(payload), checksum, StringComparison.Ordinal)) throw ChildOverflow();
            InstructionFindingHandoffV1 handoff;
            try
            {
                if (InstructionFindingHandoffConsumerV1.Validate(payload) != runId) throw ChildOverflow();
                handoff = InstructionFindingJsonV1.Deserialize(payload);
            }
            catch (InstructionFindingHandoffConsumerValidationException) { throw ChildOverflow(); }
            if (handoff.AnalysisRunId != runId) throw ChildOverflow();
            result.Add(new(runId, traceId, checksum, handoff));
        }
        return result;
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> AssociateFindings(
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyDictionary<Guid, IReadOnlyList<MonitorSpanRow>> spans,
        IReadOnlyList<SnapshotHandoffRow> handoffs)
    {
        var locationIndex = new Dictionary<InstructionEvidenceReferenceV1, List<(Guid SessionId, HistoricalRawEvidenceReferenceV1 Reference)>>();
        foreach (var (sessionId, detail) in details)
        {
            foreach (var location in ExactLocations(detail, spans[sessionId]).Distinct())
            {
                foreach (var relativePosition in Enum.GetValues<HistoricalEvidenceRelativePositionV1>())
                {
                    var positioned = location with { RelativePosition = relativePosition };
                    foreach (var raw in new[]
                    {
                        new InstructionRawEvidenceReferenceV1(null, positioned.TraceId, positioned.SpanId, positioned.TurnIndex, (InstructionEvidenceRelativePositionV1)(int)relativePosition),
                        new InstructionRawEvidenceReferenceV1(sessionId.ToString(), positioned.TraceId, positioned.SpanId, positioned.TurnIndex, (InstructionEvidenceRelativePositionV1)(int)relativePosition),
                    })
                    {
                        var key = InstructionFindingReferenceTokenizationV1.Tokenize(raw);
                        if (!locationIndex.TryGetValue(key, out var candidates)) locationIndex.Add(key, candidates = []);
                        if (!candidates.Contains((sessionId, positioned))) candidates.Add((sessionId, positioned));
                    }
                }
            }
        }

        var foundBySession = details.Keys.ToDictionary(id => id, _ => new List<InstructionFindingAssociation>());
        foreach (var row in handoffs)
        {
            if (row.Handoff.Findings.Any(receipt => receipt.AnchorTraceId != InstructionFindingReferenceTokenizationV1.TokenizeTrace(row.TraceId)))
                throw ChildOverflow();
            var resolved = new Dictionary<string, (Guid SessionId, IReadOnlyList<HistoricalRawEvidenceReferenceV1> References)>(StringComparer.Ordinal);
            foreach (var receipt in row.Handoff.Findings)
            {
                var locations = new List<(Guid SessionId, HistoricalRawEvidenceReferenceV1 Reference)>();
                foreach (var safeReference in receipt.EvidenceRefs)
                {
                    if (!locationIndex.TryGetValue(safeReference, out var matches)) throw ChildOverflow();
                    var unique = matches.Distinct().ToArray();
                    if (unique.Length != 1) throw ChildOverflow();
                    locations.Add(unique[0]);
                }
                if (locations.Select(item => item.SessionId).Distinct().Count() != 1) throw ChildOverflow();
                var sessionId = locations[0].SessionId;
                var references = locations.Select(item => item.Reference).Distinct()
                    .OrderBy(value => value.TraceId, StringComparer.Ordinal).ThenBy(value => value.SpanId, StringComparer.Ordinal)
                    .ThenBy(value => value.TurnIndex).ThenBy(value => value.RelativePosition).ToArray();
                if (references.Length != receipt.EvidenceRefs.Count) throw ChildOverflow();
                resolved.Add(receipt.FindingId, (sessionId, references));
            }
            var candidateByFindingId = new Dictionary<string, InstructionRuleCandidateV1>(StringComparer.Ordinal);
            foreach (var candidate in row.Handoff.Candidates)
                foreach (var findingId in candidate.SourceFindingIds)
                    if (!candidateByFindingId.TryAdd(findingId, candidate)) throw ChildOverflow();
            foreach (var receipt in row.Handoff.Findings)
            {
                var item = resolved[receipt.FindingId];
                candidateByFindingId.TryGetValue(receipt.FindingId, out var candidate);
                if (candidate is not null && candidate.SourceFindingIds.Any(id => !resolved.ContainsKey(id))) throw ChildOverflow();
                var found = foundBySession[item.SessionId];
                if (found.Count == HistoricalEvidenceContractsV1.MaximumGroupsPerSession) throw ChildOverflow();
                found.Add(new(receipt, candidate, item.References));
            }
        }
        return foundBySession.Where(pair => pair.Value.Count > 0).ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<InstructionFindingAssociation>)pair.Value.OrderBy(value => value.Receipt.FindingId, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<SnapshotRow> ReadMatchingRows(SqliteConnection connection, SqliteTransaction transaction, HistoricalEvidenceSelectionV1 selection, int limit)
    {
        using var command = BuildSelectionCommand(connection, transaction, selection, count: false);
        command.CommandText += " ORDER BY COALESCE(s.started_at,s.last_seen_at) DESC,s.session_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<SnapshotRow>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    private static long CountMatching(SqliteConnection connection, SqliteTransaction transaction, HistoricalEvidenceSelectionV1 selection)
    {
        using var command = BuildSelectionCommand(connection, transaction, selection, count: true);
        return (long)command.ExecuteScalar()!;
    }

    private static int CountMatchingReturned(SqliteConnection connection, SqliteTransaction transaction,
        HistoricalEvidenceSelectionV1 selection, IReadOnlyList<SnapshotRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var command = BuildSelectionCommand(connection, transaction, selection, count: true);
        var names = rows.Select((_, index) => "$returned" + index).ToArray();
        command.CommandText = command.CommandText.TrimEnd(';');
        command.CommandText += command.CommandText.Contains(" WHERE ", StringComparison.Ordinal)
            ? $" AND s.session_id IN ({string.Join(',', names)});"
            : $" WHERE s.session_id IN ({string.Join(',', names)});";
        for (var index = 0; index < names.Length; index++)
            command.Parameters.AddWithValue(names[index], rows[index].SessionId.ToString("D"));
        return checked((int)(long)command.ExecuteScalar()!);
    }

    private static SqliteCommand BuildSelectionCommand(SqliteConnection connection, SqliteTransaction transaction, HistoricalEvidenceSelectionV1 selection, bool count)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        var filters = new List<string>();
        if (selection.Repository is not null) { filters.Add("s.repository=$repository COLLATE BINARY"); command.Parameters.AddWithValue("$repository", selection.Repository); }
        if (selection.Workspace is not null) { filters.Add("s.workspace=$workspace COLLATE BINARY"); command.Parameters.AddWithValue("$workspace", selection.Workspace); }
        if (selection.From is not null) { filters.Add("COALESCE(s.started_at,s.last_seen_at)>=$from"); command.Parameters.AddWithValue("$from", selection.From.Value.ToUniversalTime().ToString("O")); }
        if (selection.To is not null) { filters.Add("COALESCE(s.started_at,s.last_seen_at)<$to"); command.Parameters.AddWithValue("$to", selection.To.Value.ToUniversalTime().ToString("O")); }
        if (selection.SourceSurfaces.Count > 0)
        {
            var names = selection.SourceSurfaces.Select((_, index) => "$surface" + index).ToArray();
            var values = string.Join(',', names);
            filters.Add($"(EXISTS(SELECT 1 FROM session_native_ids n WHERE n.session_id=s.session_id AND n.source_surface IN ({values}))"
                + $" OR EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=s.session_id AND r.source_surface IN ({values}))"
                + $" OR EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=s.session_id AND e.source_surface IN ({values})))");
            for (var index = 0; index < names.Length; index++) command.Parameters.AddWithValue(names[index], SessionWire.ToWire(selection.SourceSurfaces[index]));
        }
        if (selection.TaskLabel is not null || selection.ExperimentLabel is not null) filters.Add("1=0");
        if (!HasNonIdScope(selection) && selection.ExplicitSessionIds.Count > 0)
        {
            var names = selection.ExplicitSessionIds.Select((_, index) => "$id" + index).ToArray();
            filters.Add($"s.session_id IN ({string.Join(',', names)})");
            for (var index = 0; index < names.Length; index++) command.Parameters.AddWithValue(names[index], selection.ExplicitSessionIds[index].ToString("D"));
        }
        command.CommandText = count
            ? "SELECT COUNT(*) FROM sessions s" + Where(filters) + ";"
            : "SELECT s.session_id,s.started_at,s.last_seen_at,s.updated_at FROM sessions s" + Where(filters);
        return command;
    }

    private static IReadOnlyList<SnapshotRow> ReadExplicitRows(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return [];
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var names = ids.Select((_, index) => "$explicit" + index).ToArray();
        command.CommandText = $"SELECT session_id,started_at,last_seen_at,updated_at FROM sessions WHERE session_id IN ({string.Join(',', names)}) ORDER BY session_id;";
        for (var index = 0; index < names.Length; index++) command.Parameters.AddWithValue(names[index], ids[index].ToString("D"));
        using var reader = command.ExecuteReader(); var rows = new List<SnapshotRow>(); while (reader.Read()) rows.Add(ReadRow(reader)); return rows;
    }

    private static SnapshotRow ReadRow(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
    private static string Where(IReadOnlyList<string> filters) => filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    private static bool HasNonIdScope(HistoricalEvidenceSelectionV1 value) => value.Repository is not null || value.Workspace is not null || value.From is not null || value.To is not null || value.SourceSurfaces.Count > 0 || value.TaskLabel is not null || value.ExperimentLabel is not null;

    private static IReadOnlyList<MonitorSpanRow> ReadBoundedSpans(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT m.id,m.raw_record_id,m.trace_id,m.span_id,m.parent_span_id,m.span_ordinal,m.operation,m.category,
                m.tool_name,m.tool_type,m.mcp_tool_name,m.mcp_server_hash,m.agent_name,m.request_model,m.response_model,
                m.input_tokens,m.output_tokens,m.total_tokens,m.reasoning_tokens,m.cache_read_tokens,m.cache_creation_tokens,
                m.status,m.error_type,m.finish_reasons,m.conversation_id,m.duration_ms,m.start_time,m.end_time,m.projected_at
            FROM monitor_spans m
            JOIN session_events e ON e.trace_id=m.trace_id
            WHERE e.session_id=$session
                AND e.match_kind IN ('exact_native','explicit_link')
                AND e.source_adapter IN ('otel-exact','claude-code-otel')
            ORDER BY m.raw_record_id,m.span_ordinal,m.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumEventsPerSession + 1);
        using var reader = command.ExecuteReader();
        var result = new List<MonitorSpanRow>();
        while (reader.Read())
        {
            HistoricalEvidenceReadBoundsV1.Validate(0, 0, result.Count + 1, 0);
            result.Add(new(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), reader.GetInt32(5),
                NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11),
                NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14), NullableInt32(reader, 15), NullableInt32(reader, 16), NullableInt32(reader, 17),
                NullableInt32(reader, 18), NullableInt32(reader, 19), NullableInt32(reader, 20), NullableString(reader, 21), NullableString(reader, 22), NullableString(reader, 23),
                NullableString(reader, 24), NullableDouble(reader, 25), NullableString(reader, 26), NullableString(reader, 27), reader.GetString(28)));
        }
        return result;
    }

    private static IReadOnlyList<SnapshotObjectiveRow> ReadBoundedObjectives(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT objective_evaluation_id,trace_id,result,recorded_at FROM objective_evaluations WHERE session_id=$session ORDER BY recorded_at,objective_evaluation_id LIMIT $limit;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumGroupsPerSession + 1);
        using var reader = command.ExecuteReader();
        var result = new List<SnapshotObjectiveRow>();
        while (reader.Read())
        {
            HistoricalEvidenceReadBoundsV1.Validate(0, 0, 0, result.Count + 1);
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), Timestamp(reader.GetString(3))));
        }
        return result;
    }

    private static SessionDetail ReadBoundedDetail(SqliteConnection connection, SqliteTransaction transaction, SnapshotRow row)
    {
        ObservedSession session;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at FROM sessions WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", row.SessionId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
            session = new(
                Guid.Parse(reader.GetString(0)), SessionWire.ParseStatus(reader.GetString(1)), SessionWire.ParseCompleteness(reader.GetString(2)),
                NullableString(reader, 3), NullableString(reader, 4), NullableTimestamp(reader, 5), NullableTimestamp(reader, 6),
                Timestamp(reader.GetString(7)), SessionWire.ParseRawRetentionState(reader.GetString(8)), Timestamp(reader.GetString(9)), Timestamp(reader.GetString(10)));
            if (reader.Read() || session.UpdatedAt != row.UpdatedAt)
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
        }

        var nativeIds = new List<SessionNativeId>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids WHERE session_id=$id ORDER BY observed_at,source_surface,native_session_id LIMIT $limit;";
            command.Parameters.AddWithValue("$id", row.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumNativeIdsPerSession + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (nativeIds.Count == HistoricalEvidenceContractsV1.MaximumNativeIdsPerSession) throw ChildOverflow();
                nativeIds.Add(new(Guid.Parse(reader.GetString(0)), SessionWire.ParseSourceSurface(reader.GetString(1)), reader.GetString(2), SessionWire.ParseBindingKind(reader.GetString(3)), Timestamp(reader.GetString(4))));
            }
        }

        var runs = new List<ObservedSessionRun>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,status,started_at,ended_at,input_tokens,output_tokens,total_tokens FROM session_runs WHERE session_id=$id ORDER BY started_at,run_id LIMIT $limit;";
            command.Parameters.AddWithValue("$id", row.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumRunsPerSession + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (runs.Count == HistoricalEvidenceContractsV1.MaximumRunsPerSession) throw ChildOverflow();
                runs.Add(new(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableSurface(reader, 2), NullableString(reader, 3), NullableString(reader, 4), NullableGuid(reader, 5), NullableString(reader, 6),
                    SessionWire.ParseStatus(reader.GetString(7)), NullableTimestamp(reader, 8), NullableTimestamp(reader, 9), NullableInt64(reader, 10), NullableInt64(reader, 11), NullableInt64(reader, 12)));
            }
        }

        var events = new List<ObservedSessionEvent>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind FROM session_events WHERE session_id=$id ORDER BY occurred_at,event_id LIMIT $limit;";
            command.Parameters.AddWithValue("$id", row.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$limit", HistoricalEvidenceContractsV1.MaximumEventsPerSession + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (events.Count == HistoricalEvidenceContractsV1.MaximumEventsPerSession) throw ChildOverflow();
                events.Add(new(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableGuid(reader, 2), NullableSurface(reader, 3), NullableGuid(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), Timestamp(reader.GetString(10)), SessionWire.ParseContentState(reader.GetString(11)),
                    NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14), NullableString(reader, 15), NullableMatchKind(reader, 16)));
            }
        }
        return new(session, nativeIds, runs, events);
    }

    private static HistoricalEvidenceValidationException ChildOverflow() => new(HistoricalEvidenceValidationCodeV1.InvalidContract);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? NullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static int? NullableInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static DateTimeOffset Timestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Timestamp(reader.GetString(ordinal));
    private static SessionSourceSurface? NullableSurface(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : SessionWire.ParseSourceSurface(reader.GetString(ordinal));
    private static SessionMatchKind? NullableMatchKind(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return reader.GetString(ordinal) switch
        {
            "exact_native" => SessionMatchKind.ExactNative,
            "explicit_link" => SessionMatchKind.ExplicitLink,
            "trace_continuity" => SessionMatchKind.TraceContinuity,
            "conversation_id" => SessionMatchKind.ConversationId,
            "none" => SessionMatchKind.None,
            _ => throw ChildOverflow(),
        };
    }
    private SqliteConnection Open() { var value = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); value.Open(); return value; }
    private sealed record SnapshotRow(Guid SessionId, DateTimeOffset? StartedAt, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt);
    private sealed record SnapshotObjectiveRow(Guid ObjectiveEvaluationId, string TraceId, string Result, DateTimeOffset RecordedAt);
    private sealed record SnapshotHandoffRow(long AnalysisRunId, string TraceId, string Checksum, InstructionFindingHandoffV1 Handoff);
    private sealed record InstructionFindingAssociation(
        InstructionFindingReceiptV1 Receipt,
        InstructionRuleCandidateV1? Candidate,
        IReadOnlyList<HistoricalRawEvidenceReferenceV1> References);

    private sealed class Lease(
        string snapshotId,
        IReadOnlyList<HistoricalSessionMetadataV1> sessions,
        long omitted,
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyDictionary<Guid, IReadOnlyList<MonitorSpanRow>> spans,
        IReadOnlyDictionary<Guid, IReadOnlyList<SnapshotObjectiveRow>> objectives,
        IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> findings,
        IReadOnlyDictionary<Guid, IReadOnlyList<HistoricalEvidenceGroupDraftV1>> descriptors) : IHistoricalEvidenceSnapshotLeaseV1
    {
        public string SnapshotId => snapshotId;
        public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => sessions;
        public long OmittedEarlierMatchingSessionCount => omitted;

        public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(Guid sessionId, bool includeDescriptors, CancellationToken cancellationToken)
        {
            if (!details.TryGetValue(sessionId, out var detail)) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference);
            cancellationToken.ThrowIfCancellationRequested();
            var groups = new List<HistoricalEvidenceGroupDraftV1>();
            foreach (var trace in spans[sessionId].GroupBy(span => span.TraceId, StringComparer.Ordinal))
            {
                var ordered = trace.OrderBy(span => span.RawRecordId).ThenBy(span => span.SpanOrdinal).ToArray();
                var evidence = InstructionEvidenceExtractor.Extract(trace.Key, ordered, [], []);
                var turnSpans = ordered.Where(IsTurn).ToArray();
                for (var index = 0; index < evidence.TurnTokens.Count; index++)
                {
                    var turn = evidence.TurnTokens[index];
                    var reference = new HistoricalRawEvidenceReferenceV1(sessionId, trace.Key, turn.SpanId, turn.TurnIndex, HistoricalEvidenceRelativePositionV1.Anchor);
                    Add(groups, new(HistoricalEvidenceGroupKindV1.TurnRollup, [reference], 1, "turn", null, null, null, null, null, null));
                    var span = turnSpans[index];
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, span.TotalTokens, HistoricalEvidenceScalarUnitsV1.TotalToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, span.InputTokens, HistoricalEvidenceScalarUnitsV1.InputToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, span.OutputTokens, HistoricalEvidenceScalarUnitsV1.OutputToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.CacheRollup, reference, span.CacheReadTokens, HistoricalEvidenceScalarUnitsV1.CacheReadToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.CacheRollup, reference, span.CacheCreationTokens, HistoricalEvidenceScalarUnitsV1.CacheCreationToken);
                }
                foreach (var span in ordered.Where(span => !IsTurn(span) && span.SpanId is not null))
                {
                    var reference = new HistoricalRawEvidenceReferenceV1(sessionId, trace.Key, span.SpanId, null, HistoricalEvidenceRelativePositionV1.Anchor);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.CacheRollup, reference, span.CacheReadTokens, HistoricalEvidenceScalarUnitsV1.CacheReadToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.CacheRollup, reference, span.CacheCreationTokens, HistoricalEvidenceScalarUnitsV1.CacheCreationToken);
                }
                foreach (var error in ordered.Where(span => span.Status == "error" && span.SpanId is not null))
                    Add(groups, new(HistoricalEvidenceGroupKindV1.ErrorSpan,
                        [new(sessionId, trace.Key, error.SpanId, null, HistoricalEvidenceRelativePositionV1.Anchor)], null, null,
                        "error", null, null, null, null, null));
                foreach (var retry in evidence.RetryChains.Where(chain => chain.SpanIds.Count is > 1 and <= HistoricalEvidenceContractsV1.MaximumReferencesPerGroup && chain.SpanIds.All(id => id is not null)))
                    Add(groups, new(HistoricalEvidenceGroupKindV1.RetryChain,
                        retry.SpanIds.Select(id => new HistoricalRawEvidenceReferenceV1(sessionId, trace.Key, id, null, HistoricalEvidenceRelativePositionV1.Anchor)).ToArray(),
                        retry.SpanIds.Count, "attempt", retry.FinalOutcome, null, null, null, null, null));
            }
            foreach (var run in detail.Runs.Where(value => !spans[sessionId].Any(span => span.TraceId == value.TraceId && IsTurn(span))))
            {
                var reference = detail.Events.Where(value => value.RunId == run.RunId).Select(TryExactReference).FirstOrDefault(value => value is not null);
                if (reference is not null)
                {
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, run.TotalTokens, HistoricalEvidenceScalarUnitsV1.TotalToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, run.InputTokens, HistoricalEvidenceScalarUnitsV1.InputToken);
                    AddComponent(groups, HistoricalEvidenceGroupKindV1.TokenRollup, reference, run.OutputTokens, HistoricalEvidenceScalarUnitsV1.OutputToken);
                }
            }
            var exactLocations = ExactLocations(detail, spans[sessionId]).Distinct().ToArray();
            foreach (var objective in objectives[sessionId])
            {
                var reference = exactLocations.FirstOrDefault(item => item.TraceId == objective.TraceId);
                if (reference is not null)
                    Add(groups, new(HistoricalEvidenceGroupKindV1.QualityReference, [reference], null, null, objective.Result, null, null, null, null, null));
            }
            foreach (var finding in findings.GetValueOrDefault(sessionId, []))
                Add(groups, new(HistoricalEvidenceGroupKindV1.InstructionFinding, finding.References, null, null, null, null, null, null,
                    finding.Receipt.FindingId, null, finding.Receipt, finding.Candidate));
            if (includeDescriptors)
                foreach (var descriptor in descriptors[sessionId]) Add(groups, descriptor);
            return ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(groups);
        }

        private static void Add(List<HistoricalEvidenceGroupDraftV1> groups, HistoricalEvidenceGroupDraftV1 group)
        {
            if (groups.Count == HistoricalEvidenceContractsV1.MaximumGroupsPerSession) throw ChildOverflow();
            groups.Add(group);
        }

        private static void AddComponent(List<HistoricalEvidenceGroupDraftV1> groups, HistoricalEvidenceGroupKindV1 kind,
            HistoricalRawEvidenceReferenceV1 reference, long? value, string unit)
        {
            if (value is not null) Add(groups, new(kind, [reference], value, unit, null, null, null, null, null, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class HistoricalEvidenceReadBoundsV1
{
    internal static void Validate(int handoffCount, long aggregateHandoffBytes, int spanCount, int objectiveCount)
    {
        if (handoffCount < 0 || handoffCount > HistoricalEvidenceContractsV1.MaximumInstructionFindingHandoffs
            || aggregateHandoffBytes < 0 || aggregateHandoffBytes > HistoricalEvidenceContractsV1.MaximumInstructionFindingTotalBytes
            || spanCount < 0 || spanCount > HistoricalEvidenceContractsV1.MaximumEventsPerSession
            || objectiveCount < 0 || objectiveCount > HistoricalEvidenceContractsV1.MaximumGroupsPerSession)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
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
        var details = new Dictionary<Guid, SessionDetail>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            details.Add(row.SessionId, ReadBoundedDetail(connection, transaction, row));
        }
        var handoffRows = ReadBoundedHandoffs(connection, transaction, rows);
        var associations = AssociateFindings(details, handoffRows);
        transaction.Commit();

        var metadata = rows.Select(row => ProjectMetadata(details[row.SessionId], associations.GetValueOrDefault(row.SessionId, []))).ToArray();
        var omitted = Math.Max(0, matchingCount - matching.Count);
        var snapshotId = SnapshotId(selection, rows, matchingCount, matching.Count, omitted, handoffRows);
        return ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(snapshotId, metadata, omitted, details, associations, sessionStore, contentReader));
    }

    private static HistoricalSessionMetadataV1 ProjectMetadata(SessionDetail detail, IReadOnlyList<InstructionFindingAssociation> findings)
    {
        var exactEvents = detail.Events.Select(TryExactReference).Where(value => value is not null).Cast<HistoricalRawEvidenceReferenceV1>().ToArray();
        var eventByRun = detail.Events.Where(value => value.RunId is not null)
            .GroupBy(value => value.RunId!.Value).ToDictionary(group => group.Key, group => group.Select(TryExactReference).FirstOrDefault(value => value is not null));
        var provenance = detail.Events.Select(value => new HistoricalSourceProvenanceV1(
                value.SourceSurface ?? detail.NativeIds.Select(item => (SessionSourceSurface?)item.SourceSurface).FirstOrDefault() ?? SessionSourceSurface.HookUnknown,
                value.SourceApplicationVersion,
                value.AdapterVersion))
            .Distinct().OrderBy(value => value.SourceSurface).ThenBy(value => value.SourceApplicationVersion, StringComparer.Ordinal).ThenBy(value => value.AdapterVersion, StringComparer.Ordinal).ToArray();
        var models = detail.Runs.Where(run => run.Model is not null && eventByRun.GetValueOrDefault(run.RunId) is not null)
            .Select(run => new HistoricalRawModelObservationV1(run.Model!, eventByRun[run.RunId]!)).ToArray();
        var durations = detail.Runs.Where(run => run.StartedAt is not null && run.EndedAt is not null && run.EndedAt >= run.StartedAt && eventByRun.GetValueOrDefault(run.RunId) is not null)
            .Select(run => new HistoricalRawDurationObservationV1(checked((long)(run.EndedAt!.Value - run.StartedAt!.Value).TotalMilliseconds), eventByRun[run.RunId]!)).ToArray();
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
        var availableDescriptor = detail.Events.Any(value => value.ContentState == SessionContentState.Available
            && value.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted"
            && TryExactReference(value) is not null);
        var capabilities = new HistoricalSessionCapabilitiesV1(
            TurnRollup: exactEvents.Length > 0,
            TokenRollup: detail.Runs.Any(run => run.TotalTokens is not null && eventByRun.GetValueOrDefault(run.RunId) is not null),
            CacheRollup: false, ErrorSpan: false, RetryChain: false, RepeatedToolCall: false,
            PermissionWait: false, SubagentFanOut: false, RawLocalDescriptor: availableDescriptor,
            QualityReference: false, SourceComparison: provenance.Length > 1, InstructionFindingReference: findings.Count > 0);
        return new HistoricalSessionMetadataV1(
            detail.Session.SessionId, primary, provenance.FirstOrDefault()?.SourceApplicationVersion,
            provenance.FirstOrDefault()?.AdapterVersion, detail.Session.Completeness, [], HistoricalEvidenceSourceKindV1.LiveOtel,
            detail.Events.Any(value => value.ContentState == SessionContentState.Available) ? SessionContentState.Available : detail.Events.FirstOrDefault()?.ContentState ?? SessionContentState.NotCaptured,
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

    private static HistoricalRawEvidenceReferenceV1? TryExactReference(ObservedSessionEvent value)
    {
        if (value.SourceAdapter is not ("otel-exact" or "claude-code-otel") || value.TraceId is null
            || value.SourceEventId.Split('/') is not [var trace, var span]
            || !string.Equals(trace, value.TraceId, StringComparison.Ordinal) || span.Length == 0)
            return null;
        return new(value.SessionId, trace, span, null, HistoricalEvidenceRelativePositionV1.Anchor);
    }

    private static string SnapshotId(
        HistoricalEvidenceSelectionV1 selection,
        IReadOnlyList<SnapshotRow> rows,
        long matchingCount,
        int returnedMatchingCount,
        long omitted,
        IReadOnlyList<SnapshotHandoffRow> handoffs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("copilot-agent-observability/session-snapshot/v1\0"));
        hash.AppendData(HistoricalEvidenceJsonV1.SerializeSelection(HistoricalEvidenceExtractorV1.CanonicalInputSelection(selection)));
        hash.AppendData(Encoding.UTF8.GetBytes($"\nmatching={matchingCount}\nreturned={returnedMatchingCount}\nomitted={omitted}\n"));
        foreach (var row in rows)
            hash.AppendData(Encoding.UTF8.GetBytes($"{row.SessionId:D}|{row.UpdatedAt:O}\n"));
        foreach (var handoff in handoffs)
            hash.AppendData(Encoding.UTF8.GetBytes($"handoff={handoff.AnalysisRunId}|{handoff.Checksum}\n"));
        return "session-snapshot-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..32];
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
            SELECT DISTINCT h.analysis_run_id,length(CAST(h.payload_json AS BLOB)),h.payload_json,h.payload_sha256
            FROM instruction_finding_handoffs h
            JOIN monitor_analysis_runs r ON r.id=h.analysis_run_id
            JOIN session_events e ON e.trace_id=r.trace_id
            WHERE e.session_id IN ({string.Join(',', parameters)})
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
            if (result.Count == HistoricalEvidenceContractsV1.MaximumInstructionFindingHandoffs) throw ChildOverflow();
            var runId = reader.GetInt64(0);
            var payloadLength = reader.GetInt64(1);
            if (payloadLength is < 1 or > HistoricalEvidenceContractsV1.MaximumInstructionFindingPayloadBytes) throw ChildOverflow();
            try { totalBytes = checked(totalBytes + payloadLength); }
            catch (OverflowException) { throw ChildOverflow(); }
            if (totalBytes > HistoricalEvidenceContractsV1.MaximumInstructionFindingTotalBytes) throw ChildOverflow();
            var payload = Encoding.UTF8.GetBytes(reader.GetString(2));
            if (payload.Length != payloadLength) throw ChildOverflow();
            var checksum = reader.GetString(3);
            if (!string.Equals(HistoricalEvidenceExtractorV1.Sha256(payload), checksum, StringComparison.Ordinal)) throw ChildOverflow();
            InstructionFindingHandoffV1 handoff;
            try { handoff = InstructionFindingJsonV1.Deserialize(payload); }
            catch (InstructionFindingValidationException) { throw ChildOverflow(); }
            if (handoff.AnalysisRunId != runId) throw ChildOverflow();
            result.Add(new(runId, checksum, handoff));
        }
        return result;
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> AssociateFindings(
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyList<SnapshotHandoffRow> handoffs)
    {
        var result = new Dictionary<Guid, IReadOnlyList<InstructionFindingAssociation>>();
        foreach (var (sessionId, detail) in details)
        {
            var locations = detail.Events.Select(TryExactReference).OfType<HistoricalRawEvidenceReferenceV1>().ToArray();
            var found = new List<InstructionFindingAssociation>();
            foreach (var handoff in handoffs.Select(value => value.Handoff))
            {
                foreach (var receipt in handoff.Findings)
                {
                    var resolved = new List<HistoricalRawEvidenceReferenceV1>();
                    foreach (var safeReference in receipt.EvidenceRefs)
                    {
                        var reference = locations.Select(location => location with
                            {
                                TurnIndex = safeReference.TurnIndex,
                                RelativePosition = (HistoricalEvidenceRelativePositionV1)(int)safeReference.RelativePosition,
                            })
                            .FirstOrDefault(location => InstructionFindingReferenceTokenizationV1.Tokenize(new(
                                location.SessionId.ToString(), location.TraceId, location.SpanId, location.TurnIndex,
                                (InstructionEvidenceRelativePositionV1)(int)location.RelativePosition)) == safeReference);
                        if (reference is null) break;
                        resolved.Add(reference);
                    }
                    if (resolved.Count != receipt.EvidenceRefs.Count) continue;
                    var references = resolved.Distinct().OrderBy(value => value.TraceId, StringComparer.Ordinal)
                        .ThenBy(value => value.SpanId, StringComparer.Ordinal).ThenBy(value => value.TurnIndex).ToArray();
                    if (references.Length != receipt.EvidenceRefs.Count) continue;
                    var candidate = handoff.Candidates.SingleOrDefault(value => value.SourceFindingIds.Contains(receipt.FindingId, StringComparer.Ordinal));
                    found.Add(new(receipt, candidate, references));
                }
            }
            if (found.Count > HistoricalEvidenceContractsV1.MaximumGroupsPerSession) throw ChildOverflow();
            if (found.Count > 0)
                result.Add(sessionId, found.OrderBy(value => value.Receipt.FindingId, StringComparer.Ordinal).ToArray());
        }
        return result;
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
    private sealed record SnapshotHandoffRow(long AnalysisRunId, string Checksum, InstructionFindingHandoffV1 Handoff);
    private sealed record InstructionFindingAssociation(
        InstructionFindingReceiptV1 Receipt,
        InstructionRuleCandidateV1? Candidate,
        IReadOnlyList<HistoricalRawEvidenceReferenceV1> References);

    private sealed class Lease(
        string snapshotId,
        IReadOnlyList<HistoricalSessionMetadataV1> sessions,
        long omitted,
        IReadOnlyDictionary<Guid, SessionDetail> details,
        IReadOnlyDictionary<Guid, IReadOnlyList<InstructionFindingAssociation>> findings,
        ISessionStore sessionStore,
        IHistoricalSessionContentReaderV1 contentReader) : IHistoricalEvidenceSnapshotLeaseV1
    {
        public string SnapshotId => snapshotId;
        public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => sessions;
        public long OmittedEarlierMatchingSessionCount => omitted;

        public async ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(Guid sessionId, bool includeDescriptors, CancellationToken cancellationToken)
        {
            if (!details.TryGetValue(sessionId, out var detail)) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference);
            var metadata = sessions.Single(value => value.SessionId == sessionId);
            var references = metadata.EvidenceLocations.Select(value => new HistoricalRawEvidenceReferenceV1(value.SessionId, value.TraceId, value.SpanId, value.TurnIndex, value.RelativePosition)).ToArray();
            var groups = new List<HistoricalEvidenceGroupDraftV1>();
            foreach (var referencePage in references.Chunk(HistoricalEvidenceContractsV1.MaximumReferencesPerGroup))
                groups.Add(new(HistoricalEvidenceGroupKindV1.TurnRollup, referencePage, referencePage.Length, "event", null, null, null, null, null, null));
            foreach (var run in detail.Runs.Where(value => value.TotalTokens is not null))
            {
                var reference = detail.Events.Where(value => value.RunId == run.RunId).Select(TryExactReference).FirstOrDefault(value => value is not null);
                if (reference is not null) groups.Add(new(HistoricalEvidenceGroupKindV1.TokenRollup, [reference], run.TotalTokens, "token", null, null, null, null, null, null));
            }
            foreach (var finding in findings.GetValueOrDefault(sessionId, []))
                groups.Add(new(HistoricalEvidenceGroupKindV1.InstructionFinding, finding.References, null, null, null, null, null, null,
                    finding.Receipt.FindingId, null, finding.Receipt, finding.Candidate));
            var descriptorEvents = detail.Events
                .Where(value => value.ContentState == SessionContentState.Available && value.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted")
                .Where(value => TryExactReference(value) is not null)
                .ToArray();
            if (groups.Count + (includeDescriptors ? descriptorEvents.Length : 0) > HistoricalEvidenceContractsV1.MaximumGroupsPerSession)
                throw ChildOverflow();
            if (includeDescriptors && metadata.Capabilities.RawLocalDescriptor && metadata.ContentState == SessionContentState.Available
                && metadata.SourceKind != HistoricalEvidenceSourceKindV1.HistoricalSummary
                && sessionStore.GetRawRetentionState(sessionId) == SessionRawRetentionState.Expiring)
            {
                foreach (var item in descriptorEvents)
                {
                    var reference = TryExactReference(item); if (reference is null) continue;
                    var read = await contentReader.ReadContentAsync(sessionId, item.EventId, cancellationToken).ConfigureAwait(false);
                    if (read.Disposition != SessionContentReadDisposition.Granted || read.Lease is null) continue;
                    await using var contentLease = read.Lease;
                    var descriptor = Descriptor(contentLease.Content.ContentJson);
                    if (descriptor is not null) groups.Add(new(HistoricalEvidenceGroupKindV1.UserCorrection, [reference], null, null, null, null, null, null, null, descriptor));
                }
            }
            return groups;
        }

        private static string? Descriptor(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var name in new[] { "text", "prompt", "message" })
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString();
                return null;
            }
            catch (JsonException) { return null; }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

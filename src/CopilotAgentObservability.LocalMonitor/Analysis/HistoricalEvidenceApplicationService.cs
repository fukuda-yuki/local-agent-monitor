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
        transaction.Commit();

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
            var detail = sessionStore.GetDetail(row.SessionId);
            if (detail is null || detail.Session.UpdatedAt != row.UpdatedAt)
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
            details.Add(row.SessionId, detail);
        }
        EnsureRowsStable(rows);

        var metadata = rows.Select(row => ProjectMetadata(details[row.SessionId])).ToArray();
        var omitted = Math.Max(0, matchingCount - matching.Count);
        var snapshotId = SnapshotId(rows);
        return ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(snapshotId, metadata, omitted, details, contentReader));
    }

    private static HistoricalSessionMetadataV1 ProjectMetadata(SessionDetail detail)
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
            && TryExactReference(value) is not null)
            && detail.Session.RawRetentionState == SessionRawRetentionState.Expiring;
        var capabilities = new HistoricalSessionCapabilitiesV1(
            TurnRollup: exactEvents.Length > 0,
            TokenRollup: detail.Runs.Any(run => run.TotalTokens is not null && eventByRun.GetValueOrDefault(run.RunId) is not null),
            CacheRollup: false, ErrorSpan: false, RetryChain: false, RepeatedToolCall: false,
            PermissionWait: false, SubagentFanOut: false, RawLocalDescriptor: availableDescriptor,
            QualityReference: false, SourceComparison: provenance.Length > 1, InstructionFindingReference: false);
        return new HistoricalSessionMetadataV1(
            detail.Session.SessionId, primary, provenance.FirstOrDefault()?.SourceApplicationVersion,
            provenance.FirstOrDefault()?.AdapterVersion, detail.Session.Completeness, [], HistoricalEvidenceSourceKindV1.LiveOtel,
            detail.Events.Any(value => value.ContentState == SessionContentState.Available) ? SessionContentState.Available : detail.Events.FirstOrDefault()?.ContentState ?? SessionContentState.NotCaptured,
            detail.Session.Repository, detail.Session.Workspace, null, null, detail.Session.StartedAt, detail.Session.LastSeenAt,
            capabilities, exactEvents.Select(value => new HistoricalEvidenceLocationV1(value.SessionId, value.TraceId, value.SpanId, value.TurnIndex, value.RelativePosition)).ToArray(), [])
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

    private static string SnapshotId(IReadOnlyList<SnapshotRow> rows)
    {
        var input = string.Join("\n", rows.Select(row => $"{row.SessionId:D}|{row.UpdatedAt:O}"));
        return "session-snapshot-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..32];
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
    private void EnsureRowsStable(IReadOnlyList<SnapshotRow> rows)
    {
        if (rows.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = rows.Select((_, index) => "$stable" + index).ToArray();
        command.CommandText = $"SELECT session_id,updated_at FROM sessions WHERE session_id IN ({string.Join(',', names)}) ORDER BY session_id;";
        for (var index = 0; index < names.Length; index++) command.Parameters.AddWithValue(names[index], rows[index].SessionId.ToString("D"));
        var stable = new Dictionary<Guid, DateTimeOffset>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) stable.Add(Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        if (stable.Count != rows.Count || rows.Any(row => !stable.TryGetValue(row.SessionId, out var updatedAt) || updatedAt != row.UpdatedAt))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
        transaction.Commit();
    }
    private SqliteConnection Open() { var value = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); value.Open(); return value; }
    private sealed record SnapshotRow(Guid SessionId, DateTimeOffset? StartedAt, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt);

    private sealed class Lease(
        string snapshotId,
        IReadOnlyList<HistoricalSessionMetadataV1> sessions,
        long omitted,
        IReadOnlyDictionary<Guid, SessionDetail> details,
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
            if (references.Length > 0) groups.Add(new(HistoricalEvidenceGroupKindV1.TurnRollup, references.Take(HistoricalEvidenceContractsV1.MaximumReferencesPerGroup).ToArray(), references.Length, "event", null, null, null, null, null, null));
            foreach (var run in detail.Runs.Where(value => value.TotalTokens is not null))
            {
                var reference = detail.Events.Where(value => value.RunId == run.RunId).Select(TryExactReference).FirstOrDefault(value => value is not null);
                if (reference is not null) groups.Add(new(HistoricalEvidenceGroupKindV1.TokenRollup, [reference], run.TotalTokens, "token", null, null, null, null, null, null));
            }
            if (includeDescriptors && metadata.Capabilities.RawLocalDescriptor && metadata.ContentState == SessionContentState.Available
                && metadata.SourceKind != HistoricalEvidenceSourceKindV1.HistoricalSummary
                && detail.Session.RawRetentionState == SessionRawRetentionState.Expiring)
            {
                foreach (var item in detail.Events.Where(value => value.ContentState == SessionContentState.Available && value.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted"))
                {
                    var reference = TryExactReference(item); if (reference is null) continue;
                    var read = await contentReader.ReadContentAsync(sessionId, item.EventId, cancellationToken).ConfigureAwait(false);
                    if (read.Disposition != SessionContentReadDisposition.Granted || read.Lease is null) continue;
                    await using var contentLease = read.Lease;
                    var descriptor = Descriptor(contentLease.Content.ContentJson);
                    if (descriptor is not null) groups.Add(new(HistoricalEvidenceGroupKindV1.UserCorrection, [reference], null, null, null, null, null, null, null, descriptor));
                }
            }
            return groups.Take(HistoricalEvidenceContractsV1.MaximumGroupsPerSession).ToArray();
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

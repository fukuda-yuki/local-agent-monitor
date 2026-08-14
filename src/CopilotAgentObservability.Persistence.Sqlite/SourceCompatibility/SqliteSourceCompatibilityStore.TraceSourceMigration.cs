using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteSourceCompatibilityStore
{
    internal const string TraceSourceAttributionTableSql =
        """
        CREATE TABLE IF NOT EXISTS source_trace_attribution_observations (
            raw_record_id INTEGER NOT NULL,
            trace_id TEXT NOT NULL,
            cli_candidate_observed INTEGER NOT NULL CHECK (cli_candidate_observed IN (0, 1)),
            vscode_candidate_observed INTEGER NOT NULL CHECK (vscode_candidate_observed IN (0, 1)),
            unknown_candidate_observed INTEGER NOT NULL CHECK (unknown_candidate_observed IN (0, 1)),
            relevant_evidence_observed INTEGER NOT NULL CHECK (relevant_evidence_observed IN (0, 1)),
            PRIMARY KEY (raw_record_id, trace_id),
            CHECK (
                relevant_evidence_observed = 1 OR
                (cli_candidate_observed = 0 AND vscode_candidate_observed = 0 AND unknown_candidate_observed = 0)
            )
        );
        """;

    internal const string TraceSourceAttributionIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_source_trace_attribution_observations_trace_id ON source_trace_attribution_observations(trace_id, raw_record_id);";

    internal const string TraceSourceReconciliationQueueTableSql =
        """
        CREATE TABLE IF NOT EXISTS source_trace_attribution_reconciliation_queue (
            trace_id TEXT NOT NULL PRIMARY KEY
        );
        """;

    internal static void EnsureTraceSourceAttributionSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            TraceSourceAttributionTableSql);
        Execute(
            connection,
            transaction,
            TraceSourceAttributionIndexSql);
        Execute(
            connection,
            transaction,
            TraceSourceReconciliationQueueTableSql);
    }

    internal static void TransitionRetainedTraceSourceAttribution(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        TransitionRetainedTraceSourceAttribution(
            connection,
            transaction,
            TimeProvider.System);

    internal static void TransitionRetainedTraceSourceAttribution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeProvider timeProvider) =>
        TransitionRetainedTraceSourceAttribution(
            connection,
            transaction,
            timeProvider,
            rawPayloadMaterializationCheckpoint: null);

    internal static void TransitionRetainedTraceSourceAttribution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeProvider timeProvider,
        Action<long>? rawPayloadMaterializationCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var migrationNow = timeProvider.GetUtcNow().ToUniversalTime();
        var retainedRaw = ReadRetainedRawSourceEvidence(
            connection,
            transaction,
            migrationNow,
            rawPayloadMaterializationCheckpoint);
        var ingestions = ReadProjectedIngestions(connection, transaction);
        var traces = ReadProjectedTraces(connection, transaction);
        var spans = ReadProjectedSpanMembership(connection, transaction);
        var completeTraceIds = traces.Values
            .Where(trace => IsCompleteForTransition(trace, retainedRaw, ingestions, spans))
            .Select(trace => trace.TraceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var raw in retainedRaw.Values)
        {
            if (!raw.IsParseable)
            {
                continue;
            }

            foreach (var resolution in raw.Resolutions.Values)
            {
                if (!traces.ContainsKey(resolution.TraceId)
                    || completeTraceIds.Contains(resolution.TraceId))
                {
                    InsertMigratedTraceSourceResolution(
                        connection,
                        transaction,
                        raw.RawRecordId,
                        resolution);
                }
            }
        }

        var resolutions = AggregateTraceSourceResolutions(
            ReadAllTraceSourceEvidence(connection, transaction));
        foreach (var traceId in completeTraceIds)
        {
            UpdateClientKind(
                connection,
                transaction,
                "UPDATE monitor_traces SET client_kind=$client_kind WHERE trace_id=$identity;",
                traceId,
                resolutions.GetValueOrDefault(traceId));
        }

        foreach (var ingestion in ingestions.Values)
        {
            if (!retainedRaw.TryGetValue(ingestion.RawRecordId, out var raw)
                || !raw.IsParseable
                || raw.Resolutions.Count == 0
                || raw.Resolutions.Keys.Any(traceId => !completeTraceIds.Contains(traceId)))
            {
                continue;
            }

            var families = raw.Resolutions.Keys
                .Select(traceId => resolutions.GetValueOrDefault(traceId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            UpdateClientKind(
                connection,
                transaction,
                "UPDATE monitor_ingestions SET client_kind=$client_kind WHERE raw_record_id=$identity;",
                ingestion.RawRecordId,
                families.Length == 1 ? families[0] : null);
        }
    }

    private static bool IsCompleteForTransition(
        ProjectedTrace trace,
        IReadOnlyDictionary<long, RetainedRawSourceEvidence> retainedRaw,
        IReadOnlyDictionary<long, ProjectedIngestion> ingestions,
        ProjectedSpanMembership spans)
    {
        if (trace.SpanCount is null
            || trace.SpanCount <= 0
            || spans.CountByTrace.GetValueOrDefault(trace.TraceId) != trace.SpanCount)
        {
            return false;
        }

        var contributingRawIds = spans.RawIdsByTrace.GetValueOrDefault(trace.TraceId);
        if (contributingRawIds is null || contributingRawIds.Count == 0)
        {
            return false;
        }

        foreach (var rawRecordId in contributingRawIds)
        {
            if (!retainedRaw.TryGetValue(rawRecordId, out var raw)
                || !raw.IsParseable
                || !raw.Resolutions.ContainsKey(trace.TraceId)
                || !HasExactRawSpanMembership(raw, spans)
                || !ingestions.TryGetValue(rawRecordId, out var ingestion)
                || !ingestion.SpanProjectionComplete
                || ingestion.SpanCount is null
                || raw.SpanCount != ingestion.SpanCount)
            {
                return false;
            }
        }

        if (retainedRaw.Values.Any(raw =>
                raw.Resolutions.ContainsKey(trace.TraceId)
                && !contributingRawIds.Contains(raw.RawRecordId)))
        {
            return false;
        }

        if (retainedRaw.Values.Any(raw =>
                string.Equals(raw.EnvelopeTraceId, trace.TraceId, StringComparison.Ordinal)
                && (!raw.IsParseable || !raw.Resolutions.ContainsKey(trace.TraceId))))
        {
            return false;
        }

        return ingestions.Values
            .Where(ingestion =>
                string.Equals(ingestion.TraceId, trace.TraceId, StringComparison.Ordinal))
            .All(ingestion => contributingRawIds.Contains(ingestion.RawRecordId));
    }

    private static bool HasExactRawSpanMembership(
        RetainedRawSourceEvidence raw,
        ProjectedSpanMembership projected)
    {
        if (projected.CountByRawRecord.GetValueOrDefault(raw.RawRecordId) != raw.SpanCount
            || !projected.SpanIdentitiesByRawRecord.TryGetValue(
                raw.RawRecordId,
                out var projectedIdentities)
            || projectedIdentities.Count != raw.SpanIdentities.Count)
        {
            return false;
        }

        return raw.SpanIdentities.SequenceEqual(projectedIdentities);
    }

    private static IReadOnlyDictionary<long, RetainedRawSourceEvidence> ReadRetainedRawSourceEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset migrationNow,
        Action<long>? rawPayloadMaterializationCheckpoint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT raw.id, raw.trace_id
            FROM raw_records AS raw
            ORDER BY id;
            """;
        var rows = new List<(long RawRecordId, string? TraceId)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    NullableString(reader, 1)));
            }
        }
        var result = new Dictionary<long, RetainedRawSourceEvidence>();
        foreach (var row in rows)
        {
            var rawRecordId = row.RawRecordId;
            if (!Retention.RetentionCatalogStore.IsRawRecordReadAuthorizedForMigration(
                    connection,
                    transaction,
                    rawRecordId,
                    migrationNow))
            {
                continue;
            }
            var traceId = row.TraceId;
            rawPayloadMaterializationCheckpoint?.Invoke(rawRecordId);
            var payloadJson = ReadRawPayloadJson(
                connection,
                transaction,
                rawRecordId);
            if (payloadJson is null)
            {
                continue;
            }
            try
            {
                var resolutions = OtlpTraceSourceResolver.Resolve(payloadJson)
                    .ToDictionary(item => item.TraceId, StringComparer.Ordinal);
                var spanMembership = ReadRawSpanMembership(payloadJson);
                result.Add(
                    rawRecordId,
                    new RetainedRawSourceEvidence(
                        rawRecordId,
                        traceId,
                        IsParseable: true,
                        resolutions,
                        spanMembership.SpanCount,
                        spanMembership.SpanIdentities));
            }
            catch (JsonException)
            {
                result.Add(
                    rawRecordId,
                    new RetainedRawSourceEvidence(
                        rawRecordId,
                        traceId,
                        IsParseable: false,
                        new Dictionary<string, TraceSourceResolutionDraft>(StringComparer.Ordinal),
                        SpanCount: 0,
                        []));
            }
        }
        return result;
    }

    private static string? ReadRawPayloadJson(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT payload_json FROM raw_records WHERE id=$raw_record_id;";
        Add(command, "$raw_record_id", rawRecordId);
        return command.ExecuteScalar() as string;
    }

    private static RawSpanMembership ReadRawSpanMembership(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        long spanCount = 0;
        var spanIdentities = new List<SpanMembershipIdentity>();
        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(
                     document.RootElement,
                     "resourceSpans"))
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var traceId = OtlpSpanReader.ReadString(span, "traceId");
                    var spanId = OtlpSpanReader.ReadString(span, "spanId");
                    spanIdentities.Add(new((int)spanCount, traceId, spanId));
                    spanCount++;
                }
            }
        }
        return new RawSpanMembership(spanCount, spanIdentities);
    }

    private static IReadOnlyDictionary<long, ProjectedIngestion> ReadProjectedIngestions(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT raw_record_id, trace_id, span_count, span_projected_at
            FROM monitor_ingestions
            ORDER BY raw_record_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, ProjectedIngestion>();
        while (reader.Read())
        {
            var rawRecordId = reader.GetInt64(0);
            result.Add(
                rawRecordId,
                new ProjectedIngestion(
                    rawRecordId,
                    NullableString(reader, 1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    SpanProjectionComplete: !reader.IsDBNull(3)));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ProjectedTrace> ReadProjectedTraces(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT trace_id, span_count
            FROM monitor_traces
            ORDER BY trace_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, ProjectedTrace>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var traceId = reader.GetString(0);
            result.Add(
                traceId,
                new ProjectedTrace(
                    traceId,
                    reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }
        return result;
    }

    private static ProjectedSpanMembership ReadProjectedSpanMembership(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT raw_record_id, trace_id, span_id, span_ordinal
            FROM monitor_spans
            ORDER BY raw_record_id, span_ordinal;
            """;
        using var reader = command.ExecuteReader();
        var countByTrace = new Dictionary<string, long>(StringComparer.Ordinal);
        var countByRawRecord = new Dictionary<long, long>();
        var rawIdsByTrace = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var spanIdentitiesByRawRecord =
            new Dictionary<long, List<SpanMembershipIdentity>>();
        while (reader.Read())
        {
            var rawRecordId = reader.GetInt64(0);
            var traceId = reader.GetString(1);
            var spanId = NullableString(reader, 2);
            var spanOrdinal = reader.GetInt32(3);
            countByTrace[traceId] = countByTrace.GetValueOrDefault(traceId) + 1;
            countByRawRecord[rawRecordId] = countByRawRecord.GetValueOrDefault(rawRecordId) + 1;
            if (!rawIdsByTrace.TryGetValue(traceId, out var rawIds))
            {
                rawIds = [];
                rawIdsByTrace.Add(traceId, rawIds);
            }
            rawIds.Add(rawRecordId);
            if (!spanIdentitiesByRawRecord.TryGetValue(rawRecordId, out var identities))
            {
                identities = [];
                spanIdentitiesByRawRecord.Add(rawRecordId, identities);
            }
            identities.Add(new(spanOrdinal, traceId, spanId));
        }
        return new ProjectedSpanMembership(
            countByTrace,
            countByRawRecord,
            rawIdsByTrace,
            spanIdentitiesByRawRecord.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<SpanMembershipIdentity>)item.Value));
    }

    private static IReadOnlyDictionary<string, string?> AggregateTraceSourceResolutions(
        IReadOnlyList<StoredTraceSourceEvidence> evidence) =>
        evidence
            .GroupBy(item => item.TraceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var resolution = TraceSourceResolutionDraft.FromEvidence(
                        group.Key,
                        group.Any(item => item.CliCandidateObserved),
                        group.Any(item => item.VsCodeCandidateObserved),
                        group.Any(item => item.UnknownCandidateObserved),
                        group.Any(item => item.RelevantEvidenceObserved));
                    return resolution.State == TraceSourceResolutionState.Resolved
                        ? resolution.SourceFamily
                        : null;
                },
                StringComparer.Ordinal);

    private static void InsertMigratedTraceSourceResolution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        TraceSourceResolutionDraft resolution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO source_trace_attribution_observations (
                raw_record_id, trace_id, cli_candidate_observed, vscode_candidate_observed,
                unknown_candidate_observed, relevant_evidence_observed
            ) VALUES (
                $raw_record_id, $trace_id, $cli_candidate_observed, $vscode_candidate_observed,
                $unknown_candidate_observed, $relevant_evidence_observed
            );
            """;
        Add(command, "$raw_record_id", rawRecordId);
        Add(command, "$trace_id", resolution.TraceId);
        Add(command, "$cli_candidate_observed", resolution.CliCandidateObserved ? 1 : 0);
        Add(command, "$vscode_candidate_observed", resolution.VsCodeCandidateObserved ? 1 : 0);
        Add(command, "$unknown_candidate_observed", resolution.UnknownCandidateObserved ? 1 : 0);
        Add(command, "$relevant_evidence_observed", resolution.RelevantEvidenceObserved ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private sealed record RetainedRawSourceEvidence(
        long RawRecordId,
        string? EnvelopeTraceId,
        bool IsParseable,
        IReadOnlyDictionary<string, TraceSourceResolutionDraft> Resolutions,
        long SpanCount,
        IReadOnlyList<SpanMembershipIdentity> SpanIdentities);

    private sealed record RawSpanMembership(
        long SpanCount,
        IReadOnlyList<SpanMembershipIdentity> SpanIdentities);

    private sealed record SpanMembershipIdentity(
        int SpanOrdinal,
        string? TraceId,
        string? SpanId);

    private sealed record ProjectedIngestion(
        long RawRecordId,
        string? TraceId,
        long? SpanCount,
        bool SpanProjectionComplete);

    private sealed record ProjectedTrace(string TraceId, long? SpanCount);

    private sealed record ProjectedSpanMembership(
        IReadOnlyDictionary<string, long> CountByTrace,
        IReadOnlyDictionary<long, long> CountByRawRecord,
        IReadOnlyDictionary<string, HashSet<long>> RawIdsByTrace,
        IReadOnlyDictionary<long, IReadOnlyList<SpanMembershipIdentity>> SpanIdentitiesByRawRecord);
}

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed class SqliteRawReplaySnapshotProvider : IRawReplaySnapshotProvider
{
    private readonly RetentionCatalogContext context;
    private readonly TimeProvider timeProvider;

    public SqliteRawReplaySnapshotProvider(string databasePath, TimeProvider? timeProvider = null)
        : this(databasePath, RetentionCatalogContext.AdoptExistingCatalogV1(databasePath), timeProvider)
    {
    }

    public SqliteRawReplaySnapshotProvider(string databasePath, RetentionCatalogContext context, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(Path.GetFullPath(databasePath), context.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The retention catalog context belongs to a different database.", nameof(context));
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<RawReplaySnapshotCapture> CaptureAsync(
        RawReplaySelection selection,
        bool includeSessionContent,
        CancellationToken cancellationToken)
    {
        if (!ValidSelection(selection, includeSessionContent)) return Failure("invalid_selection");
        IReadOnlyList<Candidate>? candidates = null;
        try
        {
            var result = await new RetentionCatalogStore(context, timeProvider).ReadSelectedBatchAsync(
                (connection, transaction, _) =>
                {
                    candidates = SelectCandidates(connection, transaction, selection, includeSessionContent);
                    if (candidates.Count > RawReplayLimits.MaximumPayloadEntries) throw new SelectionLimitException();
                    ValidateCandidateSizes(candidates);
                    var now = timeProvider.GetUtcNow();
                    return ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(candidates.Select(candidate => new RetentionReadRequest(
                        new RetentionOwnershipKey(context.StoreInstanceId, candidate.Kind, candidate.SourceId),
                        RetentionReadKind.Operation,
                        now,
                        ExpectedRevision: null)).ToArray());
                },
                (connection, transaction, grants, _) => ValueTask.FromResult(Materialize(connection, transaction, candidates!, grants, includeSessionContent)),
                cancellationToken).ConfigureAwait(false);

            if (result.Disposition != RetentionReadDisposition.Granted || result.Lease is null)
                return Failure(result.Disposition switch
                {
                    RetentionReadDisposition.Busy => "snapshot_store_busy",
                    RetentionReadDisposition.NotFound => "snapshot_member_missing",
                    _ => "snapshot_read_denied",
                });

            var lease = result.Lease;
            return new(true, null, new RawReplaySnapshotLease(lease.Value, lease.DisposeAsync));
        }
        catch (SelectionLimitException)
        {
            return Failure("selection_limit_exceeded");
        }
        catch (SelectionMemberMissingException)
        {
            return Failure("snapshot_member_missing");
        }
        catch (SnapshotSizeException exception)
        {
            return Failure(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or OverflowException or CryptographicException)
        {
            return Failure("snapshot_store_unavailable");
        }
    }

    private IReadOnlyList<Candidate> SelectCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RawReplaySelection selection,
        bool includeSessionContent)
    {
        if (!TableExists(connection, transaction, "raw_records")
            || !TableExists(connection, transaction, "monitor_spans")) throw new InvalidOperationException();
        if (selection.SessionIds is { Count: > 0 }
            && !TableExists(connection, transaction, "session_runs")) throw new InvalidOperationException();
        if (includeSessionContent && (!TableExists(connection, transaction, "session_events")
            || !TableExists(connection, transaction, "session_event_content"))) throw new InvalidOperationException();

        EnsureResolvedExplicitRawMembers(connection, transaction, selection);

        var candidates = new List<Candidate>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT r.id,length(CAST(r.payload_json AS BLOB)) FROM raw_records r WHERE {BuildRawPredicate(selection, command)} ORDER BY r.id LIMIT {RawReplayLimits.MaximumPayloadEntries + 1};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                candidates.Add(new(RetentionStoreKind.RawRecord, id.ToString(CultureInfo.InvariantCulture), reader.GetInt64(1)));
            }
        }

        if (includeSessionContent)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = AddTextParameters(command, selection.SessionIds!, "session");
            command.CommandText = $"SELECT c.event_id,length(CAST(c.content_json AS BLOB)) FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE e.session_id COLLATE BINARY IN ({string.Join(',', names)}) ORDER BY c.event_id COLLATE BINARY LIMIT {RawReplayLimits.MaximumPayloadEntries + 1};";
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add(new(RetentionStoreKind.SessionEventContent, reader.GetString(0), reader.GetInt64(1)));
        }

        return candidates;
    }

    private static void ValidateCandidateSizes(IReadOnlyList<Candidate> candidates)
    {
        long aggregate = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ByteLength < 0) throw new InvalidOperationException();
            var maximum = candidate.Kind == RetentionStoreKind.RawRecord
                ? RawReplayLimits.MaximumRawRecordBytes
                : RawReplayLimits.MaximumSessionContentBytes;
            if (candidate.ByteLength > maximum) throw new SnapshotSizeException("entry_too_large");
            try { aggregate = checked(aggregate + candidate.ByteLength); }
            catch (OverflowException) { throw new SnapshotSizeException("archive_too_large"); }
            if (aggregate > RawReplayLimits.MaximumArchiveBytes) throw new SnapshotSizeException("archive_too_large");
        }
    }

    private static void EnsureResolvedExplicitRawMembers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RawReplaySelection selection)
    {
        if (selection.RawRecordIds is not { Count: > 0 } rawRecordIds) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = AddLongParameters(command, rawRecordIds, "required_raw");
        command.CommandText = $"SELECT id FROM raw_records WHERE id IN ({string.Join(',', names)});";
        using var reader = command.ExecuteReader();
        var resolved = new HashSet<long>();
        while (reader.Read()) resolved.Add(reader.GetInt64(0));
        if (resolved.Count != rawRecordIds.Count) throw new SelectionMemberMissingException();
    }

    private RawReplaySnapshot? Materialize(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<RetentionReadGrant> grants,
        bool includeSessionContent)
    {
        if (candidates.Count != grants.Count) return null;
        var records = new List<RawReplayRecord>();
        var contents = new List<RawReplaySessionContent>();
        var knownMissing = new SortedSet<string>(StringComparer.Ordinal);
        if (!includeSessionContent) knownMissing.Add("session_content_not_requested");
        else if (!candidates.Any(candidate => candidate.Kind == RetentionStoreKind.SessionEventContent))
            knownMissing.Add("session_content_unavailable");

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Kind == RetentionStoreKind.RawRecord)
            {
                var record = ReadRawRecord(connection, transaction, candidate.SourceId, grants[index], out var provenanceMissing);
                if (record is null) return null;
                records.Add(record);
                if (provenanceMissing) knownMissing.Add("source_observation_missing");
            }
            else
            {
                var content = ReadSessionContent(connection, transaction, candidate.SourceId, grants[index]);
                if (content is null) return null;
                contents.Add(content);
            }
        }

        var snapshotId = SnapshotId(records, contents);
        return new(snapshotId, timeProvider.GetUtcNow(), "local-monitor.v1", records, contents, knownMissing.ToArray());
    }

    private RawReplayRecord? ReadRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        RetentionReadGrant grant,
        out bool provenanceMissing)
    {
        var hasObservations = TableExists(connection, transaction, "source_schema_observations");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = hasObservations
            ? """
                SELECT r.id,r.source,r.trace_id,r.received_at,r.resource_attributes_json,r.payload_json,r.schema_version,
                       o.source_surface,o.source_application_version,o.source_adapter,o.adapter_version,o.schema_fingerprint,
                       o.inventory_hash,o.compatibility_state,o.capture_content_state
                FROM raw_records r
                LEFT JOIN source_schema_observations o ON o.raw_record_id=r.id
                WHERE r.id=$id AND r.retention_owner_token=$retention_read_source_token
                  AND EXISTS (SELECT 1 FROM retention_items WHERE item_id=$retention_read_item_id AND revision=$retention_read_revision)
                  AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND lease_kind='operation'
                    AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
                """
            : """
                SELECT r.id,r.source,r.trace_id,r.received_at,r.resource_attributes_json,r.payload_json,r.schema_version,
                       NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL
                FROM raw_records r
                WHERE r.id=$id AND r.retention_owner_token=$retention_read_source_token
                  AND EXISTS (SELECT 1 FROM retention_items WHERE item_id=$retention_read_item_id AND revision=$retention_read_revision)
                  AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND lease_kind='operation'
                    AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
                """;
        command.Parameters.AddWithValue("$id", long.Parse(sourceId, CultureInfo.InvariantCulture));
        grant.BindSelectorCapability(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { provenanceMissing = false; return null; }
        provenanceMissing = reader.IsDBNull(13);
        var provenance = new RawReplaySourceProvenance(
            Nullable(reader, 7), Nullable(reader, 8), Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12),
            Nullable(reader, 13) ?? "unknown", Nullable(reader, 14) ?? "unknown",
            "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner);
        return new(reader.GetInt64(0), reader.GetString(1), Nullable(reader, 2), Timestamp(reader.GetString(3)), Nullable(reader, 4),
            reader.GetString(5), reader.GetInt32(6), provenance);
    }

    private RawReplaySessionContent? ReadSessionContent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        RetentionReadGrant grant)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.event_id,e.session_id,e.run_id,e.trace_id,e.source_adapter,e.source_event_id,e.occurred_at,e.content_state,
                   e.source_application_version,e.adapter_version,e.schema_fingerprint,e.normalization_version,e.match_kind,
                   c.content_kind,c.content_json,c.captured_at,c.expires_at
            FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id
            WHERE c.event_id=$event_id AND c.retention_owner_token=$retention_read_source_token
              AND EXISTS (SELECT 1 FROM retention_items WHERE item_id=$retention_read_item_id AND revision=$retention_read_revision)
              AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND lease_kind='operation'
                AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
            """;
        command.Parameters.AddWithValue("$event_id", sourceId);
        grant.BindSelectorCapability(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetString(0), reader.GetString(1), Nullable(reader, 2), Nullable(reader, 3), reader.GetString(4), reader.GetString(5),
            Timestamp(reader.GetString(6)), reader.GetString(7), Nullable(reader, 8), Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12),
            reader.GetString(13), reader.GetString(14), Timestamp(reader.GetString(15)), Timestamp(reader.GetString(16)),
            "session_secret_filter_applied", RawReplayContractVersions.CredentialScanner);
    }

    private static string BuildRawPredicate(RawReplaySelection selection, SqliteCommand command)
    {
        var clauses = new List<string>();
        if (selection.RawRecordIds is { Count: > 0 })
        {
            var names = AddLongParameters(command, selection.RawRecordIds, "raw");
            clauses.Add($"r.id IN ({string.Join(',', names)})");
        }
        if (selection.TraceIds is { Count: > 0 })
        {
            var names = AddTextParameters(command, selection.TraceIds, "trace");
            clauses.Add($"EXISTS (SELECT 1 FROM monitor_spans ms WHERE ms.raw_record_id=r.id AND ms.trace_id COLLATE BINARY IN ({string.Join(',', names)}))");
        }
        if (selection.SessionIds is { Count: > 0 })
        {
            if (!TableExists(command.Connection!, command.Transaction!, "session_runs")) throw new InvalidOperationException();
            var names = AddTextParameters(command, selection.SessionIds, "sid");
            clauses.Add($"EXISTS (SELECT 1 FROM monitor_spans ms JOIN session_runs sr ON sr.trace_id=ms.trace_id WHERE ms.raw_record_id=r.id AND sr.session_id COLLATE BINARY IN ({string.Join(',', names)}))");
        }
        if (selection.Sources is { Count: > 0 })
        {
            var names = AddTextParameters(command, selection.Sources, "source");
            clauses.Add($"r.source COLLATE BINARY IN ({string.Join(',', names)})");
        }
        if (selection.StartInclusive is { } start)
        {
            command.Parameters.AddWithValue("$start", Wire(start));
            clauses.Add("r.received_at >= $start COLLATE BINARY");
        }
        if (selection.EndExclusive is { } end)
        {
            command.Parameters.AddWithValue("$end", Wire(end));
            clauses.Add("r.received_at < $end COLLATE BINARY");
        }
        return string.Join(" AND ", clauses);
    }

    private static bool ValidSelection(RawReplaySelection? selection, bool includeContent)
    {
        if (selection is null || includeContent && selection.SessionIds is not { Count: > 0 }
            || selection.StartInclusive is { Offset: var startOffset } && startOffset != TimeSpan.Zero
            || selection.EndExclusive is { Offset: var endOffset } && endOffset != TimeSpan.Zero
            || selection.StartInclusive is { } start && selection.EndExclusive is { } end && start >= end) return false;
        var any = selection.SessionIds is { Count: > 0 } || selection.TraceIds is { Count: > 0 }
            || selection.RawRecordIds is { Count: > 0 } || selection.Sources is { Count: > 0 }
            || selection.StartInclusive is not null || selection.EndExclusive is not null;
        return any && ValidStrings(selection.SessionIds) && ValidStrings(selection.TraceIds)
            && (selection.RawRecordIds is null || selection.RawRecordIds.Count <= RawReplayLimits.MaximumSelectionValues
                && selection.RawRecordIds.All(id => id > 0) && selection.RawRecordIds.Distinct().Count() == selection.RawRecordIds.Count)
            && ValidStrings(selection.Sources)
            && (selection.Sources is null || selection.Sources.All(RawTelemetrySources.IsAllowed));
    }

    private static bool ValidStrings(IReadOnlyList<string>? values) => values is null
        || values.Count <= RawReplayLimits.MaximumSelectionValues
        && values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= RawReplayLimits.MaximumIdentifierLength
            && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#'))
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static string[] AddTextParameters(SqliteCommand command, IReadOnlyList<string> values, string prefix)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"${prefix}{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return names;
    }

    private static string[] AddLongParameters(SqliteCommand command, IReadOnlyList<long> values, string prefix)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"${prefix}{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return names;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static string SnapshotId(IReadOnlyList<RawReplayRecord> records, IReadOnlyList<RawReplaySessionContent> contents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Frame(hash, "copilot-agent-observability/raw-local-replay-snapshot/v1");
        foreach (var record in records)
        {
            Frame(hash, record.RawRecordId.ToString(CultureInfo.InvariantCulture));
            Frame(hash, record.Source); Frame(hash, record.TraceId ?? string.Empty); Frame(hash, Wire(record.ReceivedAt));
            Frame(hash, record.ResourceAttributesJson ?? string.Empty); Frame(hash, record.PayloadJson); Frame(hash, record.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            Frame(hash, string.Join('|', record.Provenance.SourceSurface, record.Provenance.SourceApplicationVersion, record.Provenance.SourceAdapter,
                record.Provenance.AdapterVersion, record.Provenance.SchemaFingerprint, record.Provenance.InventoryHash,
                record.Provenance.CompatibilityState, record.Provenance.CaptureContentState));
        }
        foreach (var content in contents)
        {
            Frame(hash, content.EventId); Frame(hash, content.SessionId); Frame(hash, content.ContentJson);
            Frame(hash, Wire(content.CapturedAt)); Frame(hash, Wire(content.ExpiresAt));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Frame(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset Timestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string Wire(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static RawReplaySnapshotCapture Failure(string code) => new(false, code, null);

    private sealed record Candidate(RetentionStoreKind Kind, string SourceId, long ByteLength);
    private sealed class SelectionLimitException : Exception;
    private sealed class SelectionMemberMissingException : Exception;
    private sealed class SnapshotSizeException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}

using CopilotAgentObservability.Persistence.Sqlite.Ingestion;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteSourceCompatibilityStore : ISourceCompatibilityStore
{
    private const int MaximumListLimit = 200;
    public const int MonitorSchemaVersion = MonitorSchemaMigrator.BaseSchemaVersion;
    private readonly string databasePath;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;
    private readonly Action<SqliteConnection, SqliteTransaction>? migrationCheckpoint;

    public SqliteSourceCompatibilityStore(
        string databasePath,
        RawTelemetryStoreConnectionOptions? connectionOptions = null)
        : this(databasePath, connectionOptions, migrationCheckpoint: null)
    {
    }

    internal SqliteSourceCompatibilityStore(
        string databasePath,
        RawTelemetryStoreConnectionOptions? connectionOptions,
        Action<SqliteConnection, SqliteTransaction>? migrationCheckpoint)
    {
        this.databasePath = databasePath;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
        this.migrationCheckpoint = migrationCheckpoint;
    }

    public void CreateSchema()
    {
        EnsureParentDirectory();
        using var connection = OpenConnection();
        var existingVersion = MonitorSchemaMigrator.ValidateBeforeInitialization(connection);
        ApplyWriteAheadLog(connection);
        using var transaction = connection.BeginTransaction();
        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);

        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_schema_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observation_id TEXT NOT NULL UNIQUE,
                raw_record_id INTEGER NULL UNIQUE,
                ingest_batch_id TEXT NULL UNIQUE,
                source_surface TEXT NULL,
                source_application_version TEXT NULL,
                source_adapter TEXT NULL,
                adapter_version TEXT NULL,
                schema_fingerprint TEXT NULL,
                inventory_hash TEXT NULL,
                compatibility_state TEXT NOT NULL CHECK (compatibility_state IN ('supported', 'supported_with_unknown_fields', 'schema_drift_detected', 'unsupported_source_version', 'recognized_record_drop_detected', 'adapter_failure')),
                reason_code TEXT NULL CHECK (reason_code IS NULL OR reason_code IN ('unknown_fields_observed', 'unsupported_source_version', 'schema_drift_detected', 'recognized_record_drop_detected', 'adapter_parse_failure', 'adapter_exception')),
                next_action TEXT NOT NULL CHECK (next_action IN ('none', 'review_unknown_fields', 'use_compatible_source_or_update_adapter', 'capture_fixture_and_review_mapping', 'restore_mapping_or_update_versioned_golden', 'validate_payload_and_protocol', 'inspect_sanitized_adapter_failure')),
                capture_content_state TEXT NULL CHECK (capture_content_state IS NULL OR capture_content_state IN ('available', 'not_captured', 'redacted', 'unsupported')),
                unknown_span_count INTEGER NOT NULL CHECK (unknown_span_count >= 0),
                unknown_event_count INTEGER NOT NULL CHECK (unknown_event_count >= 0),
                unknown_attribute_count INTEGER NOT NULL CHECK (unknown_attribute_count >= 0),
                overflow_distinct_count INTEGER NOT NULL CHECK (overflow_distinct_count >= 0),
                overflow_occurrence_count INTEGER NOT NULL CHECK (overflow_occurrence_count >= 0),
                observed_at TEXT NOT NULL,
                CHECK (compatibility_state = 'adapter_failure' OR capture_content_state IS NOT NULL),
                CHECK (compatibility_state = 'supported' OR reason_code IS NOT NULL),
                CHECK (
                    (compatibility_state = 'supported' AND reason_code IS NULL AND next_action = 'none') OR
                    (compatibility_state = 'supported_with_unknown_fields' AND reason_code = 'unknown_fields_observed' AND next_action = 'review_unknown_fields') OR
                    (compatibility_state = 'unsupported_source_version' AND reason_code = 'unsupported_source_version' AND next_action = 'use_compatible_source_or_update_adapter') OR
                    (compatibility_state = 'schema_drift_detected' AND reason_code = 'schema_drift_detected' AND next_action = 'capture_fixture_and_review_mapping') OR
                    (compatibility_state = 'recognized_record_drop_detected' AND reason_code = 'recognized_record_drop_detected' AND next_action = 'restore_mapping_or_update_versioned_golden') OR
                    (compatibility_state = 'adapter_failure' AND reason_code = 'adapter_parse_failure' AND next_action = 'validate_payload_and_protocol') OR
                    (compatibility_state = 'adapter_failure' AND reason_code = 'adapter_exception' AND next_action = 'inspect_sanitized_adapter_failure')
                )
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_unknown_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_observation_id INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('span', 'event', 'attribute')),
                name TEXT NOT NULL,
                occurrence_count INTEGER NOT NULL CHECK (occurrence_count BETWEEN 1 AND 1000000),
                source_version_label TEXT NULL,
                first_observed_at TEXT NOT NULL,
                last_observed_at TEXT NOT NULL,
                opaque_sample_reference TEXT NOT NULL,
                UNIQUE(source_observation_id, kind, name),
                CHECK (first_observed_at <= last_observed_at)
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_trace_version_observations (
                source_observation_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                resolution_state TEXT NOT NULL CHECK (resolution_state IN ('resolved', 'missing', 'conflicting', 'unrecognised')),
                source_application_version TEXT NULL CHECK (source_application_version IS NULL OR (length(source_application_version) BETWEEN 1 AND 256)),
                PRIMARY KEY (source_observation_id, trace_id),
                FOREIGN KEY (source_observation_id) REFERENCES source_schema_observations(id) ON DELETE CASCADE,
                CHECK (
                    (resolution_state = 'resolved' AND source_application_version IS NOT NULL) OR
                    (resolution_state IN ('missing', 'conflicting') AND source_application_version IS NULL) OR
                    resolution_state = 'unrecognised'
                )
            );
            """);
        Execute(
            connection,
            transaction,
            "CREATE INDEX IF NOT EXISTS IX_source_schema_observations_cursor ON source_schema_observations(id);");
        Execute(
            connection,
            transaction,
            "CREATE INDEX IF NOT EXISTS IX_source_unknown_observations_cursor ON source_unknown_observations(source_observation_id, id);");
        Execute(
            connection,
            transaction,
            "CREATE INDEX IF NOT EXISTS IX_source_trace_version_observations_trace_id ON source_trace_version_observations(trace_id, source_observation_id);");
        MonitorSchemaMigrator.EnsureProjectionDispositionSchema(connection, transaction);
        MonitorSchemaMigrator.EnsureRuntimeStateSchema(connection, transaction);
        migrationCheckpoint?.Invoke(connection, transaction);
        if (existingVersion != MonitorSchemaVersion)
        {
            MonitorSchemaMigrator.SetMonitorSchemaVersion(
                connection,
                transaction,
                MonitorSchemaVersion);
        }
        transaction.Commit();
    }

    public long RecordAdapterFailure(SourceAdapterFailureDraft failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = FindObservationId(connection, transaction, failure.ObservationId);
            if (existing is not null)
            {
                transaction.Commit();
                return existing.Value;
            }

            var id = InsertParent(
                connection,
                transaction,
                failure.ObservationId,
                rawRecordId: null,
                failure.IngestBatchId,
                failure.SourceSurface,
                failure.SourceApplicationVersion,
                failure.SourceAdapter,
                failure.AdapterVersion,
                schemaFingerprint: null,
                inventoryHash: null,
                failure.CompatibilityState,
                failure.ReasonCodes,
                failure.NextAction,
                failure.CaptureContentState,
                unknownSpanCount: 0,
                unknownEventCount: 0,
                unknownAttributeCount: 0,
                overflowDistinctCount: 0,
                overflowOccurrenceCount: 0,
                failure.ObservedAt);
            transaction.Commit();
            return id;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new IngestionCommitBusyException();
        }
    }

    public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit)
    {
        try
        {
            return ListCore(after, limit);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new PersistenceBusyException();
        }
    }

    private IReadOnlyList<SourceCompatibilityRow> ListCore(long? after, int limit)
    {
        if (after < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(after));
        }
        if (limit is < 1 or > MaximumListLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                observation_id,
                raw_record_id,
                ingest_batch_id,
                source_surface,
                source_application_version,
                source_adapter,
                adapter_version,
                schema_fingerprint,
                inventory_hash,
                compatibility_state,
                reason_code,
                next_action,
                capture_content_state,
                unknown_span_count,
                unknown_event_count,
                unknown_attribute_count,
                overflow_distinct_count,
                overflow_occurrence_count,
                observed_at
            FROM source_schema_observations
            WHERE id > $after
            ORDER BY id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", after ?? 0);
        command.Parameters.AddWithValue("$limit", limit);
        var parents = new List<ParentRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                parents.Add(new ParentRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    NullableInt64(reader, 2),
                    NullableString(reader, 3),
                    NullableString(reader, 4),
                    NullableString(reader, 5),
                    NullableString(reader, 6),
                    NullableString(reader, 7),
                    NullableString(reader, 8),
                    NullableString(reader, 9),
                    ParseCompatibilityState(reader.GetString(10)),
                    NullableString(reader, 11) is { } reason ? new[] { reason } : Array.Empty<string>(),
                    reader.GetString(12),
                    NullableString(reader, 13) is { } capture ? ParseCaptureContentState(capture) : null,
                    reader.GetInt64(14),
                    reader.GetInt64(15),
                    reader.GetInt64(16),
                    reader.GetInt32(17),
                    reader.GetInt32(18),
                    ParseTimestamp(reader.GetString(19))));
            }
        }

        return parents.Select(parent => new SourceCompatibilityRow(
            parent.Id,
            parent.ObservationId,
            parent.RawRecordId,
            parent.IngestBatchId,
            parent.SourceSurface,
            parent.SourceApplicationVersion,
            parent.SourceAdapter,
            parent.AdapterVersion,
            parent.SchemaFingerprint,
            parent.InventoryHash,
            parent.CompatibilityState,
            parent.ReasonCodes,
            parent.NextAction,
            parent.CaptureContentState,
            parent.UnknownSpanCount,
            parent.UnknownEventCount,
            parent.UnknownAttributeCount,
            parent.OverflowDistinctCount,
            parent.OverflowOccurrenceCount,
            parent.ObservedAt,
            ListUnknowns(connection, parent.Id))).ToArray();
    }

    public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId)
    {
        if (rawRecordId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id FROM source_schema_observations WHERE raw_record_id = $raw_record_id;";
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        var result = command.ExecuteScalar();
        if (result is not long observationId)
        {
            return null;
        }

        return List(observationId - 1, 1).Single();
    }

    public TraceSourceVersionResolutionRow? GetTraceSourceVersionResolution(string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT resolution_state, source_application_version
            FROM source_trace_version_observations
            WHERE trace_id = $trace_id
            ORDER BY source_observation_id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        var observations = new List<(TraceSourceVersionResolutionState State, string? Version)>();
        while (reader.Read())
        {
            observations.Add((
                ParseTraceSourceVersionResolutionState(reader.GetString(0)),
                NullableString(reader, 1)));
        }
        if (observations.Count == 0)
        {
            return null;
        }
        if (observations.Any(item => item.State == TraceSourceVersionResolutionState.Conflicting))
        {
            return new TraceSourceVersionResolutionRow(
                traceId, TraceSourceVersionResolutionState.Conflicting, SourceApplicationVersion: null);
        }

        var versions = observations
            .Where(item => item.Version is not null)
            .Select(item => item.Version!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versions.Length > 1)
        {
            return new TraceSourceVersionResolutionRow(
                traceId, TraceSourceVersionResolutionState.Conflicting, SourceApplicationVersion: null);
        }
        var unrecognised = observations
            .Where(item => item.State == TraceSourceVersionResolutionState.Unrecognised)
            .ToArray();
        if (unrecognised.Length > 0)
        {
            var version = unrecognised.All(item => item.Version is not null) && versions.Length == 1
                ? versions[0]
                : null;
            return new TraceSourceVersionResolutionRow(
                traceId, TraceSourceVersionResolutionState.Unrecognised, version);
        }
        if (observations.All(item => item.State == TraceSourceVersionResolutionState.Resolved)
            && versions.Length == 1)
        {
            return new TraceSourceVersionResolutionRow(
                traceId, TraceSourceVersionResolutionState.Resolved, versions.Single());
        }
        return new TraceSourceVersionResolutionRow(
            traceId, TraceSourceVersionResolutionState.Missing, SourceApplicationVersion: null);
    }

    public TraceSourceResolutionRow? GetTraceSourceResolution(string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                MAX(cli_candidate_observed),
                MAX(vscode_candidate_observed),
                MAX(unknown_candidate_observed),
                MAX(relevant_evidence_observed)
            FROM source_trace_attribution_observations
            WHERE trace_id = $trace_id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }
        var resolution = TraceSourceResolutionDraft.FromEvidence(
            traceId,
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0);
        return new TraceSourceResolutionRow(
            resolution.TraceId,
            resolution.State,
            resolution.SourceFamily);
    }

    public bool ReconcileProjectedTraceSourceAttribution()
    {
        using var connection = OpenConnection();
        if (!HasPendingTraceSourceReconciliation(connection))
        {
            return false;
        }
        using var transaction = connection.BeginTransaction();
        var pendingTraceIds = ReadPendingTraceSourceReconciliationIds(
            connection,
            transaction);
        if (pendingTraceIds.Count == 0)
        {
            transaction.Commit();
            return false;
        }

        var changed = 0;
        var resolutions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var traceId in pendingTraceIds)
        {
            var sourceFamily = ResolveTraceSourceFamily(
                connection,
                transaction,
                traceId);
            resolutions.Add(traceId, sourceFamily);
            changed += UpdateClientKind(
                connection,
                transaction,
                """
                UPDATE monitor_traces
                SET client_kind=$client_kind
                WHERE trace_id=$identity AND client_kind IS NOT $client_kind;
                """,
                traceId,
                sourceFamily);
            changed += ReconcileExactOtelSessionSurface(
                connection,
                transaction,
                traceId,
                sourceFamily);
        }

        foreach (var rawRecordId in ReadAffectedRawRecordIds(
                     connection,
                     transaction))
        {
            var families = ReadTraceIdsForRawRecord(
                    connection,
                    transaction,
                    rawRecordId)
                .Select(traceId =>
                {
                    if (!resolutions.TryGetValue(traceId, out var family))
                    {
                        family = ResolveTraceSourceFamily(
                            connection,
                            transaction,
                            traceId);
                        resolutions.Add(traceId, family);
                    }
                    return family;
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var sourceFamily = families.Length == 1 ? families[0] : null;
            changed += UpdateClientKind(
                connection,
                transaction,
                """
                UPDATE monitor_ingestions
                SET client_kind=$client_kind
                WHERE raw_record_id=$identity AND client_kind IS NOT $client_kind;
                """,
                rawRecordId,
                sourceFamily);
        }

        DeletePendingTraceSourceReconciliations(
            connection,
            transaction,
            pendingTraceIds);
        transaction.Commit();
        return changed != 0;
    }

    internal static long InsertBatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        SourceObservationBatchDraft observation)
    {
        var id = InsertParent(
            connection,
            transaction,
            observation.IngestBatchId,
            rawRecordId,
            observation.IngestBatchId,
            observation.SourceSurface,
            observation.SourceApplicationVersion,
            observation.SourceAdapter,
            observation.AdapterVersion,
            observation.SchemaFingerprint,
            observation.InventoryHash,
            observation.CompatibilityState,
            observation.ReasonCodes,
            observation.NextAction,
            observation.CaptureContentState,
            observation.Inventory.UnknownSpanCount,
            observation.Inventory.UnknownEventCount,
            observation.Inventory.UnknownAttributeCount,
            observation.Inventory.OverflowDistinctCount,
            observation.Inventory.OverflowOccurrenceCount,
            observation.ObservedAt);

        foreach (var identity in observation.Inventory.RetainedUnknownIdentities)
        {
            InsertUnknown(connection, transaction, id, SourceUnknownObservationDraft.Create(observation, identity));
        }
        foreach (var resolution in observation.TraceSourceVersionResolutions)
        {
            InsertTraceSourceVersionResolution(connection, transaction, id, resolution);
        }
        InsertTraceSourceResolutions(
            connection,
            transaction,
            rawRecordId,
            observation.TraceSourceResolutions);
        return id;
    }

    internal static void InsertTraceSourceResolutions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        IReadOnlyList<TraceSourceResolutionDraft> resolutions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(resolutions);
        foreach (var resolution in resolutions)
        {
            InsertTraceSourceResolution(
                connection,
                transaction,
                rawRecordId,
                resolution);
        }
    }

    private static long InsertParent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string observationId,
        long? rawRecordId,
        string? ingestBatchId,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string? sourceAdapter,
        string? adapterVersion,
        string? schemaFingerprint,
        string? inventoryHash,
        SourceCompatibilityState compatibilityState,
        IReadOnlyList<string> reasonCodes,
        string nextAction,
        SourceCaptureContentState? captureContentState,
        long unknownSpanCount,
        long unknownEventCount,
        long unknownAttributeCount,
        int overflowDistinctCount,
        int overflowOccurrenceCount,
        DateTimeOffset observedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_schema_observations (
                observation_id, raw_record_id, ingest_batch_id, source_surface, source_application_version,
                source_adapter, adapter_version, schema_fingerprint, inventory_hash, compatibility_state,
                reason_code, next_action, capture_content_state, unknown_span_count, unknown_event_count,
                unknown_attribute_count, overflow_distinct_count, overflow_occurrence_count, observed_at
            ) VALUES (
                $observation_id, $raw_record_id, $ingest_batch_id, $source_surface, $source_application_version,
                $source_adapter, $adapter_version, $schema_fingerprint, $inventory_hash, $compatibility_state,
                $reason_code, $next_action, $capture_content_state, $unknown_span_count, $unknown_event_count,
                $unknown_attribute_count, $overflow_distinct_count, $overflow_occurrence_count, $observed_at
            );
            SELECT last_insert_rowid();
            """;
        Add(command, "$observation_id", observationId);
        Add(command, "$raw_record_id", rawRecordId);
        Add(command, "$ingest_batch_id", ingestBatchId);
        Add(command, "$source_surface", sourceSurface);
        Add(command, "$source_application_version", sourceApplicationVersion);
        Add(command, "$source_adapter", sourceAdapter);
        Add(command, "$adapter_version", adapterVersion);
        Add(command, "$schema_fingerprint", schemaFingerprint);
        Add(command, "$inventory_hash", inventoryHash);
        Add(command, "$compatibility_state", CompatibilityStateWire(compatibilityState));
        Add(command, "$reason_code", reasonCodes.Count == 0 ? null : reasonCodes.Single());
        Add(command, "$next_action", nextAction);
        Add(command, "$capture_content_state", captureContentState is null ? null : CaptureContentStateWire(captureContentState.Value));
        Add(command, "$unknown_span_count", unknownSpanCount);
        Add(command, "$unknown_event_count", unknownEventCount);
        Add(command, "$unknown_attribute_count", unknownAttributeCount);
        Add(command, "$overflow_distinct_count", overflowDistinctCount);
        Add(command, "$overflow_occurrence_count", overflowOccurrenceCount);
        Add(command, "$observed_at", Timestamp(observedAt));
        return (long)command.ExecuteScalar()!;
    }

    private static void InsertUnknown(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceObservationId,
        SourceUnknownObservationDraft unknown)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_unknown_observations (
                source_observation_id, kind, name, occurrence_count, source_version_label,
                first_observed_at, last_observed_at, opaque_sample_reference
            ) VALUES (
                $source_observation_id, $kind, $name, $occurrence_count, $source_version_label,
                $first_observed_at, $last_observed_at, $opaque_sample_reference
            );
            """;
        Add(command, "$source_observation_id", sourceObservationId);
        Add(command, "$kind", UnknownKindWire(unknown.Kind));
        Add(command, "$name", unknown.Name);
        Add(command, "$occurrence_count", unknown.Count);
        Add(command, "$source_version_label", unknown.SourceVersionLabel);
        Add(command, "$first_observed_at", Timestamp(unknown.FirstObservedAt));
        Add(command, "$last_observed_at", Timestamp(unknown.LastObservedAt));
        Add(command, "$opaque_sample_reference", unknown.OpaqueSampleReference);
        command.ExecuteNonQuery();
    }

    private static void InsertTraceSourceVersionResolution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceObservationId,
        TraceSourceVersionResolutionDraft resolution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_trace_version_observations (
                source_observation_id, trace_id, resolution_state, source_application_version
            ) VALUES (
                $source_observation_id, $trace_id, $resolution_state, $source_application_version
            );
            """;
        Add(command, "$source_observation_id", sourceObservationId);
        Add(command, "$trace_id", resolution.TraceId);
        Add(command, "$resolution_state", TraceSourceVersionResolutionStateWire(resolution.State));
        Add(command, "$source_application_version", resolution.SourceApplicationVersion);
        command.ExecuteNonQuery();
    }

    private static void InsertTraceSourceResolution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        TraceSourceResolutionDraft resolution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_trace_attribution_observations (
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
        using var queue = connection.CreateCommand();
        queue.Transaction = transaction;
        queue.CommandText =
            """
            INSERT OR IGNORE INTO source_trace_attribution_reconciliation_queue(trace_id)
            VALUES($trace_id);
            """;
        Add(queue, "$trace_id", resolution.TraceId);
        queue.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadPendingTraceSourceReconciliationIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT trace_id
            FROM source_trace_attribution_reconciliation_queue
            ORDER BY trace_id;
            """;
        using var reader = command.ExecuteReader();
        var traceIds = new List<string>();
        while (reader.Read())
        {
            traceIds.Add(reader.GetString(0));
        }
        return traceIds;
    }

    private static bool HasPendingTraceSourceReconciliation(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM source_trace_attribution_reconciliation_queue
                LIMIT 1
            );
            """;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0;
    }

    private static IReadOnlyList<long> ReadAffectedRawRecordIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DISTINCT evidence.raw_record_id
            FROM source_trace_attribution_observations AS evidence
            JOIN source_trace_attribution_reconciliation_queue AS pending
              ON pending.trace_id=evidence.trace_id
            ORDER BY evidence.raw_record_id;
            """;
        using var reader = command.ExecuteReader();
        var rawRecordIds = new List<long>();
        while (reader.Read())
        {
            rawRecordIds.Add(reader.GetInt64(0));
        }
        return rawRecordIds;
    }

    private static IReadOnlyList<string> ReadTraceIdsForRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT trace_id
            FROM source_trace_attribution_observations
            WHERE raw_record_id=$raw_record_id
            ORDER BY trace_id;
            """;
        Add(command, "$raw_record_id", rawRecordId);
        using var reader = command.ExecuteReader();
        var traceIds = new List<string>();
        while (reader.Read())
        {
            traceIds.Add(reader.GetString(0));
        }
        return traceIds;
    }

    private static string? ResolveTraceSourceFamily(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                MAX(cli_candidate_observed),
                MAX(vscode_candidate_observed),
                MAX(unknown_candidate_observed),
                MAX(relevant_evidence_observed)
            FROM source_trace_attribution_observations
            WHERE trace_id=$trace_id;
            """;
        Add(command, "$trace_id", traceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }
        var resolution = TraceSourceResolutionDraft.FromEvidence(
            traceId,
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0);
        return resolution.State == TraceSourceResolutionState.Resolved
            ? resolution.SourceFamily
            : null;
    }

    private static int ReconcileExactOtelSessionSurface(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId,
        string? sourceFamily)
    {
        if (!SchemaObjectExists(connection, transaction, "table", "session_events")
            || !SchemaObjectExists(connection, transaction, "table", "session_runs"))
        {
            return 0;
        }

        var sessionSurface = sourceFamily switch
        {
            "vscode-copilot-chat" => "vscode",
            "copilot-cli" => "copilot-cli",
            _ => null,
        };
        var changed = 0;
        using (var runs = connection.CreateCommand())
        {
            runs.Transaction = transaction;
            runs.CommandText =
                """
                UPDATE session_runs
                SET source_surface=$source_surface
                WHERE trace_id=$trace_id
                  AND source_surface IS NOT $source_surface
                  AND EXISTS(
                    SELECT 1
                    FROM session_events AS event
                    WHERE event.run_id=session_runs.run_id
                      AND event.trace_id=$trace_id
                      AND event.source_adapter='otel-exact'
                      AND event.type='otel.span'
                  );
                """;
            Add(runs, "$source_surface", sessionSurface);
            Add(runs, "$trace_id", traceId);
            changed += runs.ExecuteNonQuery();
        }
        using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText =
                """
                UPDATE session_events
                SET source_surface=$source_surface
                WHERE trace_id=$trace_id
                  AND source_adapter='otel-exact'
                  AND type='otel.span'
                  AND source_surface IS NOT $source_surface;
                """;
            Add(events, "$source_surface", sessionSurface);
            Add(events, "$trace_id", traceId);
            changed += events.ExecuteNonQuery();
        }
        return changed;
    }

    private static void DeletePendingTraceSourceReconciliations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> traceIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM source_trace_attribution_reconciliation_queue
            WHERE trace_id=$trace_id;
            """;
        var parameter = command.Parameters.Add("$trace_id", SqliteType.Text);
        foreach (var traceId in traceIds)
        {
            parameter.Value = traceId;
            command.ExecuteNonQuery();
        }
    }

    private static bool SchemaObjectExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string type,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name
            );
            """;
        Add(command, "$type", type);
        Add(command, "$name", name);
        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0;
    }

    private static IReadOnlyList<StoredTraceSourceEvidence> ReadAllTraceSourceEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT raw_record_id, trace_id, cli_candidate_observed, vscode_candidate_observed,
                   unknown_candidate_observed, relevant_evidence_observed
            FROM source_trace_attribution_observations
            ORDER BY raw_record_id, trace_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<StoredTraceSourceEvidence>();
        while (reader.Read())
        {
            rows.Add(new StoredTraceSourceEvidence(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3) != 0,
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0));
        }
        return rows;
    }

    private static int UpdateClientKind(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        object identity,
        string? clientKind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$client_kind", clientKind);
        Add(command, "$identity", identity);
        return command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SourceUnknownObservationRow> ListUnknowns(SqliteConnection connection, long sourceObservationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_observation_id, kind, name, occurrence_count, source_version_label,
                   first_observed_at, last_observed_at, opaque_sample_reference
            FROM source_unknown_observations
            WHERE source_observation_id = $source_observation_id
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$source_observation_id", sourceObservationId);
        using var reader = command.ExecuteReader();
        var rows = new List<SourceUnknownObservationRow>();
        while (reader.Read())
        {
            rows.Add(new SourceUnknownObservationRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                ParseUnknownKind(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                NullableString(reader, 5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7)),
                reader.GetString(8)));
        }
        return rows;
    }

    private static long? FindObservationId(SqliteConnection connection, SqliteTransaction transaction, string observationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM source_schema_observations WHERE observation_id = $observation_id;";
        command.Parameters.AddWithValue("$observation_id", observationId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        if (connectionOptions.BusyTimeoutMilliseconds is { } configuredTimeout)
        {
            connectionString.DefaultTimeout = Math.Max(1, checked((configuredTimeout + 999) / 1_000));
        }

        var connection = new SqliteConnection(connectionString.ToString());
        connection.Open();
        if (connectionOptions.BusyTimeoutMilliseconds is { } timeout)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {timeout.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private void ApplyWriteAheadLog(SqliteConnection connection)
    {
        if (connectionOptions.EnableWriteAheadLog)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string CompatibilityStateWire(SourceCompatibilityState state) => state switch
    {
        SourceCompatibilityState.Supported => "supported",
        SourceCompatibilityState.SupportedWithUnknownFields => "supported_with_unknown_fields",
        SourceCompatibilityState.SchemaDriftDetected => "schema_drift_detected",
        SourceCompatibilityState.UnsupportedSourceVersion => "unsupported_source_version",
        SourceCompatibilityState.RecognizedRecordDropDetected => "recognized_record_drop_detected",
        SourceCompatibilityState.AdapterFailure => "adapter_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static SourceCompatibilityState ParseCompatibilityState(string state) => state switch
    {
        "supported" => SourceCompatibilityState.Supported,
        "supported_with_unknown_fields" => SourceCompatibilityState.SupportedWithUnknownFields,
        "schema_drift_detected" => SourceCompatibilityState.SchemaDriftDetected,
        "unsupported_source_version" => SourceCompatibilityState.UnsupportedSourceVersion,
        "recognized_record_drop_detected" => SourceCompatibilityState.RecognizedRecordDropDetected,
        "adapter_failure" => SourceCompatibilityState.AdapterFailure,
        _ => throw new InvalidOperationException("Stored source compatibility state is invalid."),
    };

    private static string CaptureContentStateWire(SourceCaptureContentState state) => state switch
    {
        SourceCaptureContentState.Available => "available",
        SourceCaptureContentState.NotCaptured => "not_captured",
        SourceCaptureContentState.Redacted => "redacted",
        SourceCaptureContentState.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static SourceCaptureContentState ParseCaptureContentState(string state) => state switch
    {
        "available" => SourceCaptureContentState.Available,
        "not_captured" => SourceCaptureContentState.NotCaptured,
        "redacted" => SourceCaptureContentState.Redacted,
        "unsupported" => SourceCaptureContentState.Unsupported,
        _ => throw new InvalidOperationException("Stored source capture content state is invalid."),
    };

    private static string UnknownKindWire(SourceUnknownKind kind) => kind switch
    {
        SourceUnknownKind.Span => "span",
        SourceUnknownKind.Event => "event",
        SourceUnknownKind.Attribute => "attribute",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SourceUnknownKind ParseUnknownKind(string kind) => kind switch
    {
        "span" => SourceUnknownKind.Span,
        "event" => SourceUnknownKind.Event,
        "attribute" => SourceUnknownKind.Attribute,
        _ => throw new InvalidOperationException("Stored source unknown kind is invalid."),
    };

    private static string TraceSourceVersionResolutionStateWire(TraceSourceVersionResolutionState state) => state switch
    {
        TraceSourceVersionResolutionState.Resolved => "resolved",
        TraceSourceVersionResolutionState.Missing => "missing",
        TraceSourceVersionResolutionState.Conflicting => "conflicting",
        TraceSourceVersionResolutionState.Unrecognised => "unrecognised",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static TraceSourceVersionResolutionState ParseTraceSourceVersionResolutionState(string state) => state switch
    {
        "resolved" => TraceSourceVersionResolutionState.Resolved,
        "missing" => TraceSourceVersionResolutionState.Missing,
        "conflicting" => TraceSourceVersionResolutionState.Conflicting,
        "unrecognised" => TraceSourceVersionResolutionState.Unrecognised,
        _ => throw new InvalidOperationException("Stored trace source-version resolution state is invalid."),
    };

    private sealed record ParentRow(
        long Id,
        string ObservationId,
        long? RawRecordId,
        string? IngestBatchId,
        string? SourceSurface,
        string? SourceApplicationVersion,
        string? SourceAdapter,
        string? AdapterVersion,
        string? SchemaFingerprint,
        string? InventoryHash,
        SourceCompatibilityState CompatibilityState,
        IReadOnlyList<string> ReasonCodes,
        string NextAction,
        SourceCaptureContentState? CaptureContentState,
        long UnknownSpanCount,
        long UnknownEventCount,
        long UnknownAttributeCount,
        int OverflowDistinctCount,
        int OverflowOccurrenceCount,
        DateTimeOffset ObservedAt);

    private sealed record StoredTraceSourceEvidence(
        long RawRecordId,
        string TraceId,
        bool CliCandidateObserved,
        bool VsCodeCandidateObserved,
        bool UnknownCandidateObserved,
        bool RelevantEvidenceObserved);
}

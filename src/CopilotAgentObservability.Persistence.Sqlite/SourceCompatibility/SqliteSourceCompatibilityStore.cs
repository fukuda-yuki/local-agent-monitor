using CopilotAgentObservability.Persistence.Sqlite.Ingestion;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteSourceCompatibilityStore : ISourceCompatibilityStore
{
    private const int MaximumListLimit = 200;
    public const int MonitorSchemaVersion = 6;
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
        ApplyWriteAheadLog(connection);
        using var transaction = connection.BeginTransaction();
        var existingVersion = MonitorSchemaMigrator.ReadMonitorSchemaVersion(connection, transaction);
        if (existingVersion > MonitorSchemaVersion)
        {
            ThrowNewerVersion(existingVersion.Value);
        }
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
            "CREATE INDEX IF NOT EXISTS IX_source_schema_observations_cursor ON source_schema_observations(id);");
        Execute(
            connection,
            transaction,
            "CREATE INDEX IF NOT EXISTS IX_source_unknown_observations_cursor ON source_unknown_observations(source_observation_id, id);");
        MonitorSchemaMigrator.EnsureProjectionDispositionSchema(connection, transaction);
        migrationCheckpoint?.Invoke(connection, transaction);
        MonitorSchemaMigrator.SetMonitorSchemaVersion(connection, transaction, MonitorSchemaVersion);
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
        return id;
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

    private static void ThrowNewerVersion(long version) =>
        throw new InvalidOperationException(
            $"Monitor schema version {version.ToString(CultureInfo.InvariantCulture)} is newer than supported version {MonitorSchemaVersion.ToString(CultureInfo.InvariantCulture)}.");

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
}

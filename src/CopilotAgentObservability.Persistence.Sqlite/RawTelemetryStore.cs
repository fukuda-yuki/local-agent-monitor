namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class RawTelemetryStore
{
    private readonly string databasePath;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;

    public RawTelemetryStore(string databasePath, RawTelemetryStoreConnectionOptions? connectionOptions = null)
    {
        this.databasePath = databasePath;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
    }

    public const int MonitorSchemaVersion = 1;

    public void CreateSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        EnsureRawRecordsSchema(connection);
    }

    /// <summary>
    /// Idempotent additive migration for the Local Ingestion Monitor: ensures the
    /// raw_records store, then adds the schema_version table and the empty
    /// monitor_ingestions / monitor_traces projection tables defined in
    /// docs/specifications/layers/raw-store-normalization.md. Existing raw_records
    /// rows are preserved; the projection tables are not populated here (M4 owns
    /// projection population).
    /// </summary>
    public void CreateMonitorSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        EnsureRawRecordsSchema(connection);
        EnsureMonitorProjectionSchema(connection);
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static void EnsureRawRecordsSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS raw_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL CHECK (source IN ('raw-otlp', 'collector-output', 'langfuse-export')),
                trace_id TEXT NULL,
                received_at TEXT NOT NULL,
                resource_attributes_json TEXT NULL,
                payload_json TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version = 1)
            );
            """);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_trace_id ON raw_records(trace_id);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_received_at ON raw_records(received_at);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_source ON raw_records(source);");
    }

    private static void EnsureMonitorProjectionSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            $"""
            INSERT INTO schema_version (component, version)
            VALUES ('monitor', {MonitorSchemaVersion})
            ON CONFLICT (component) DO UPDATE SET version = excluded.version;
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_ingestions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL UNIQUE,
                received_at TEXT NOT NULL,
                source TEXT NOT NULL,
                trace_id TEXT NULL,
                client_kind TEXT NULL,
                span_count INTEGER NULL,
                projected_at TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL UNIQUE,
                client_kind TEXT NULL,
                experiment_id TEXT NULL,
                task_id TEXT NULL,
                task_category TEXT NULL,
                agent_variant TEXT NULL,
                prompt_version TEXT NULL,
                span_count INTEGER NULL,
                tool_call_count INTEGER NULL,
                error_count INTEGER NULL,
                first_seen_at TEXT NULL,
                last_seen_at TEXT NULL,
                projected_at TEXT NOT NULL
            );
            """);
    }

    public long Insert(RawTelemetryRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_records (
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            )
            VALUES (
                $source,
                $trace_id,
                $received_at,
                $resource_attributes_json,
                $payload_json,
                $schema_version
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$source", record.Source);
        AddParameter(command, "$trace_id", record.TraceId);
        AddParameter(command, "$received_at", record.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "$resource_attributes_json", record.ResourceAttributesJson);
        AddParameter(command, "$payload_json", record.PayloadJson);
        AddParameter(command, "$schema_version", record.SchemaVersion);
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<RawTelemetryRecord> ListRecords()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            FROM raw_records
            ORDER BY id;
            """;

        using var reader = command.ExecuteReader();
        var records = new List<RawTelemetryRecord>();
        while (reader.Read())
        {
            records.Add(ReadRawRecord(reader));
        }

        return records;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> raw records that have no
    /// <c>monitor_ingestions</c> row yet, in id order. The payload is included
    /// because it is the projection worker's in-process input; it is never written
    /// to a projection table or a list response.
    /// </summary>
    public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            FROM raw_records
            WHERE id NOT IN (SELECT raw_record_id FROM monitor_ingestions)
            ORDER BY id
            LIMIT $limit;
            """;
        AddParameter(command, "$limit", limit);

        using var reader = command.ExecuteReader();
        var records = new List<RawTelemetryRecord>();
        while (reader.Read())
        {
            records.Add(ReadRawRecord(reader));
        }

        return records;
    }

    /// <summary>
    /// Idempotently projects one raw record. Inserts a single
    /// <c>monitor_ingestions</c> row (keyed on <paramref name="rawRecordId"/>); only
    /// when that insert is new does it fan out each non-blank-<c>trace_id</c>
    /// contribution into <c>monitor_traces</c> (aggregating counts and seen-at).
    /// Returns <c>true</c> when newly projected, <c>false</c> when already present.
    /// </summary>
    public bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt)
    {
        var receivedAtText = FormatTimestamp(receivedAt);
        var projectedAtText = FormatTimestamp(projectedAt);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        int inserted;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO monitor_ingestions
                    (raw_record_id, received_at, source, trace_id, client_kind, span_count, projected_at)
                VALUES ($raw_record_id, $received_at, $source, $trace_id, $client_kind, $span_count, $projected_at);
                """;
            AddParameter(insert, "$raw_record_id", rawRecordId);
            AddParameter(insert, "$received_at", receivedAtText);
            AddParameter(insert, "$source", source);
            AddParameter(insert, "$trace_id", projection.TraceId);
            AddParameter(insert, "$client_kind", projection.ClientKind);
            AddParameter(insert, "$span_count", projection.SpanCount);
            AddParameter(insert, "$projected_at", projectedAtText);
            inserted = insert.ExecuteNonQuery();
        }

        if (inserted == 0)
        {
            transaction.Rollback();
            return false;
        }

        foreach (var contribution in projection.TraceContributions)
        {
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO monitor_traces
                    (trace_id, client_kind, experiment_id, task_id, task_category, agent_variant, prompt_version,
                     span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at)
                VALUES ($trace_id, $client_kind, $experiment_id, $task_id, $task_category, $agent_variant, $prompt_version,
                     $span_count, $tool_call_count, $error_count, $seen_at, $seen_at, $projected_at)
                ON CONFLICT(trace_id) DO UPDATE SET
                    span_count = COALESCE(span_count, 0) + excluded.span_count,
                    tool_call_count = COALESCE(tool_call_count, 0) + excluded.tool_call_count,
                    error_count = COALESCE(error_count, 0) + excluded.error_count,
                    first_seen_at = MIN(first_seen_at, excluded.first_seen_at),
                    last_seen_at = MAX(last_seen_at, excluded.last_seen_at),
                    client_kind = COALESCE(client_kind, excluded.client_kind),
                    experiment_id = COALESCE(experiment_id, excluded.experiment_id),
                    task_id = COALESCE(task_id, excluded.task_id),
                    task_category = COALESCE(task_category, excluded.task_category),
                    agent_variant = COALESCE(agent_variant, excluded.agent_variant),
                    prompt_version = COALESCE(prompt_version, excluded.prompt_version),
                    projected_at = excluded.projected_at;
                """;
            AddParameter(upsert, "$trace_id", contribution.TraceId);
            AddParameter(upsert, "$client_kind", contribution.ClientKind);
            AddParameter(upsert, "$experiment_id", contribution.ExperimentId);
            AddParameter(upsert, "$task_id", contribution.TaskId);
            AddParameter(upsert, "$task_category", contribution.TaskCategory);
            AddParameter(upsert, "$agent_variant", contribution.AgentVariant);
            AddParameter(upsert, "$prompt_version", contribution.PromptVersion);
            AddParameter(upsert, "$span_count", contribution.SpanCount);
            AddParameter(upsert, "$tool_call_count", contribution.ToolCallCount);
            AddParameter(upsert, "$error_count", contribution.ErrorCount);
            AddParameter(upsert, "$seen_at", receivedAtText);
            AddParameter(upsert, "$projected_at", projectedAtText);
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>Backlog count and oldest unprocessed ingestion time (for projection-lag readiness).</summary>
    public MonitorProjectionStatus GetProjectionStatus()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*), MIN(received_at)
            FROM raw_records
            WHERE id NOT IN (SELECT raw_record_id FROM monitor_ingestions);
            """;

        using var reader = command.ExecuteReader();
        reader.Read();
        var backlog = reader.GetInt32(0);
        var oldest = reader.IsDBNull(1) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(1));
        return new MonitorProjectionStatus(backlog, oldest);
    }

    /// <summary>
    /// Cursor page of sanitized <c>monitor_ingestions</c> rows after
    /// <paramref name="afterRawRecordId"/>, ordered by <c>raw_record_id</c>. Reads
    /// up to <c>limit + 1</c> rows to detect a further page; returns at most
    /// <paramref name="limit"/>. The cursor key, filter, and ordering are all
    /// <c>raw_record_id</c>, so they cannot diverge.
    /// </summary>
    public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT raw_record_id, received_at, source, trace_id, client_kind, span_count, projected_at
            FROM monitor_ingestions
            WHERE raw_record_id > $after
            ORDER BY raw_record_id
            LIMIT $limit;
            """;
        AddParameter(command, "$after", afterRawRecordId);
        AddParameter(command, "$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var items = new List<MonitorIngestionRow>();
        while (reader.Read())
        {
            items.Add(new MonitorIngestionRow(
                RawRecordId: reader.GetInt64(0),
                ReceivedAt: reader.GetString(1),
                Source: reader.GetString(2),
                TraceId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ClientKind: reader.IsDBNull(4) ? null : reader.GetString(4),
                SpanCount: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ProjectedAt: reader.GetString(6)));
        }

        return BuildPage(items, limit);
    }

    /// <summary>
    /// Cursor page of sanitized <c>monitor_traces</c> rows after
    /// <paramref name="afterId"/>, ordered by projection-row id, using the same
    /// <c>limit + 1</c> probe.
    /// </summary>
    public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trace_id, client_kind, experiment_id, task_id, task_category, agent_variant, prompt_version,
                   span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at
            FROM monitor_traces
            WHERE id > $after
            ORDER BY id
            LIMIT $limit;
            """;
        AddParameter(command, "$after", afterId);
        AddParameter(command, "$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var items = new List<MonitorTraceRow>();
        while (reader.Read())
        {
            items.Add(new MonitorTraceRow(
                Id: reader.GetInt64(0),
                TraceId: reader.GetString(1),
                ClientKind: reader.IsDBNull(2) ? null : reader.GetString(2),
                ExperimentId: reader.IsDBNull(3) ? null : reader.GetString(3),
                TaskId: reader.IsDBNull(4) ? null : reader.GetString(4),
                TaskCategory: reader.IsDBNull(5) ? null : reader.GetString(5),
                AgentVariant: reader.IsDBNull(6) ? null : reader.GetString(6),
                PromptVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
                SpanCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                ToolCallCount: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                ErrorCount: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                FirstSeenAt: reader.IsDBNull(11) ? null : reader.GetString(11),
                LastSeenAt: reader.IsDBNull(12) ? null : reader.GetString(12),
                ProjectedAt: reader.GetString(13)));
        }

        return BuildPage(items, limit);
    }

    /// <summary>Fetches one raw record by id for the opt-in raw-detail route; null if not found.</summary>
    public RawTelemetryRecord? GetRawRecordById(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source, trace_id, received_at, resource_attributes_json, payload_json, schema_version
            FROM raw_records
            WHERE id = $id;
            """;
        AddParameter(command, "$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRawRecord(reader) : null;
    }

    private static MonitorProjectionPage<T> BuildPage<T>(List<T> rows, int limit)
    {
        if (rows.Count > limit)
        {
            return new MonitorProjectionPage<T>(rows.GetRange(0, limit), HasMore: true);
        }

        return new MonitorProjectionPage<T>(rows, HasMore: false);
    }

    private static RawTelemetryRecord ReadRawRecord(SqliteDataReader reader)
    {
        return new RawTelemetryRecord(
            Id: reader.GetInt64(0),
            Source: reader.GetString(1),
            TraceId: reader.IsDBNull(2) ? null : reader.GetString(2),
            ReceivedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ResourceAttributesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
            PayloadJson: reader.GetString(5),
            SchemaVersion: reader.GetInt32(6));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyBusyTimeout(connection);
        return connection;
    }

    private void ApplyBusyTimeout(SqliteConnection connection)
    {
        if (connectionOptions.BusyTimeoutMilliseconds is { } busyTimeoutMilliseconds)
        {
            ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
        }
    }

    private void ApplyWriteAheadLog(SqliteConnection connection)
    {
        if (connectionOptions.EnableWriteAheadLog)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

internal sealed record RawTelemetryStoreConnectionOptions(
    bool EnableWriteAheadLog,
    int? BusyTimeoutMilliseconds)
{
    public static RawTelemetryStoreConnectionOptions Default { get; } = new(
        EnableWriteAheadLog: false,
        BusyTimeoutMilliseconds: null);

    public static RawTelemetryStoreConnectionOptions MonitorWriter { get; } = new(
        EnableWriteAheadLog: true,
        BusyTimeoutMilliseconds: 5_000);
}

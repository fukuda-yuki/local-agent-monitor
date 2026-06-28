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

    public const int MonitorSchemaVersion = 2;

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
            """
            CREATE TABLE IF NOT EXISTS monitor_ingestions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL UNIQUE,
                received_at TEXT NOT NULL,
                source TEXT NOT NULL,
                trace_id TEXT NULL,
                client_kind TEXT NULL,
                span_count INTEGER NULL,
                projected_at TEXT NOT NULL,
                span_projected_at TEXT NULL
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
                projected_at TEXT NOT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                total_tokens INTEGER NULL,
                turn_count INTEGER NULL,
                agent_invocation_count INTEGER NULL,
                duration_ms REAL NULL,
                primary_model TEXT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_spans (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                span_id TEXT NULL,
                parent_span_id TEXT NULL,
                span_ordinal INTEGER NOT NULL,
                operation TEXT NULL,
                category TEXT NULL,
                tool_name TEXT NULL,
                tool_type TEXT NULL,
                mcp_tool_name TEXT NULL,
                mcp_server_hash TEXT NULL,
                agent_name TEXT NULL,
                request_model TEXT NULL,
                response_model TEXT NULL,
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                total_tokens INTEGER NULL,
                reasoning_tokens INTEGER NULL,
                cache_read_tokens INTEGER NULL,
                cache_creation_tokens INTEGER NULL,
                status TEXT NULL,
                error_type TEXT NULL,
                finish_reasons TEXT NULL,
                conversation_id TEXT NULL,
                duration_ms REAL NULL,
                start_time TEXT NULL,
                end_time TEXT NULL,
                projected_at TEXT NOT NULL,
                UNIQUE(raw_record_id, span_ordinal)
            );
            """);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_monitor_spans_trace_id ON monitor_spans(trace_id);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_monitor_spans_raw_record_id ON monitor_spans(raw_record_id);");

        // Upgrade existing v1 DBs: add columns that were absent in the original DDL.
        // CREATE TABLE IF NOT EXISTS above is a no-op on existing tables, so we
        // use PRAGMA table_info to add missing columns idempotently.
        AddColumnIfMissing(connection, "monitor_ingestions", "span_projected_at", "TEXT NULL");
        AddColumnIfMissing(connection, "monitor_traces", "input_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, "monitor_traces", "output_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, "monitor_traces", "total_tokens", "INTEGER NULL");
        AddColumnIfMissing(connection, "monitor_traces", "turn_count", "INTEGER NULL");
        AddColumnIfMissing(connection, "monitor_traces", "agent_invocation_count", "INTEGER NULL");
        AddColumnIfMissing(connection, "monitor_traces", "duration_ms", "REAL NULL");
        AddColumnIfMissing(connection, "monitor_traces", "primary_model", "TEXT NULL");

        ExecuteNonQuery(
            connection,
            $"""
            INSERT INTO schema_version (component, version)
            VALUES ('monitor', {MonitorSchemaVersion})
            ON CONFLICT (component) DO UPDATE SET version = excluded.version;
            """);
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string columnDdl)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table});";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            // Column 1 in PRAGMA table_info is the column name.
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return; // Column already exists.
            }
        }

        ExecuteNonQuery(connection, $"ALTER TABLE {table} ADD COLUMN {column} {columnDdl};");
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
                   span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at,
                   input_tokens, output_tokens, total_tokens, turn_count, agent_invocation_count, duration_ms, primary_model
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
                ProjectedAt: reader.GetString(13),
                InputTokens: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                OutputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                TotalTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                TurnCount: reader.IsDBNull(17) ? null : reader.GetInt32(17),
                AgentInvocationCount: reader.IsDBNull(18) ? null : reader.GetInt32(18),
                DurationMs: reader.IsDBNull(19) ? null : reader.GetDouble(19),
                PrimaryModel: reader.IsDBNull(20) ? null : reader.GetString(20)));
        }

        return BuildPage(items, limit);
    }

    /// <summary>
    /// Fetches one sanitized <c>monitor_traces</c> row by <c>trace_id</c> for the
    /// trace-detail page summary; null if the trace has not been projected.
    /// </summary>
    public MonitorTraceRow? GetMonitorTrace(string traceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trace_id, client_kind, experiment_id, task_id, task_category, agent_variant, prompt_version,
                   span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at,
                   input_tokens, output_tokens, total_tokens, turn_count, agent_invocation_count, duration_ms, primary_model
            FROM monitor_traces
            WHERE trace_id = $trace_id;
            """;
        AddParameter(command, "$trace_id", traceId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MonitorTraceRow(
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
            ProjectedAt: reader.GetString(13),
            InputTokens: reader.IsDBNull(14) ? null : reader.GetInt32(14),
            OutputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
            TotalTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
            TurnCount: reader.IsDBNull(17) ? null : reader.GetInt32(17),
            AgentInvocationCount: reader.IsDBNull(18) ? null : reader.GetInt32(18),
            DurationMs: reader.IsDBNull(19) ? null : reader.GetDouble(19),
            PrimaryModel: reader.IsDBNull(20) ? null : reader.GetString(20));
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

    /// <summary>
    /// Raw records for a trace (ordered by id) for the raw-bearing trace-detail
    /// page's bounded inline preview. Uses the span projection's raw_record_id
    /// mapping so secondary traces inside a multi-trace OTLP request resolve to
    /// their containing raw payload.
    /// </summary>
    public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source, trace_id, received_at, resource_attributes_json, payload_json, schema_version
            FROM raw_records
            WHERE id IN (
                SELECT DISTINCT raw_record_id
                FROM monitor_spans
                WHERE trace_id = $trace_id
            )
            ORDER BY id
            LIMIT $limit;
            """;
        AddParameter(command, "$trace_id", traceId);
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
    /// Returns up to <paramref name="limit"/> raw records whose
    /// <c>monitor_ingestions.span_projected_at</c> is NULL (span projection not yet
    /// applied), ordered by id. A record must already have a <c>monitor_ingestions</c>
    /// row (i.e. trace-projection has completed) to be eligible.
    /// </summary>
    public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit)
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
            WHERE id IN (SELECT raw_record_id FROM monitor_ingestions WHERE span_projected_at IS NULL)
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
    /// Idempotently projects span rows for one raw record. Inserts <c>monitor_spans</c>
    /// rows and updates the <c>monitor_traces</c> rollup columns. Returns <c>true</c>
    /// when newly projected, <c>false</c> when already projected or not yet ingested.
    /// </summary>
    public bool ApplySpanProjection(
        long rawRecordId,
        IReadOnlyList<MonitorSpanProjection> spans,
        DateTimeOffset projectedAt)
    {
        var projectedAtText = FormatTimestamp(projectedAt);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Idempotency check: only proceed if the ingestion row exists and span_projected_at is null.
        string? spanProjectedAt;
        bool ingestionExists;
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText =
                "SELECT span_projected_at FROM monitor_ingestions WHERE raw_record_id = $id;";
            AddParameter(check, "$id", rawRecordId);
            using var r = check.ExecuteReader();
            ingestionExists = r.Read();
            spanProjectedAt = ingestionExists && !r.IsDBNull(0) ? r.GetString(0) : null;
        }

        if (!ingestionExists || spanProjectedAt is not null)
        {
            transaction.Rollback();
            return false;
        }

        var validSpans = spans
            .Where(span => !string.IsNullOrWhiteSpace(span.TraceId))
            .ToList();

        // Insert spans — idempotent via UNIQUE(raw_record_id, span_ordinal).
        foreach (var span in validSpans)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO monitor_spans (
                    raw_record_id, trace_id, span_id, parent_span_id, span_ordinal,
                    operation, category, tool_name, tool_type, mcp_tool_name,
                    mcp_server_hash, agent_name, request_model, response_model,
                    input_tokens, output_tokens, total_tokens, reasoning_tokens,
                    cache_read_tokens, cache_creation_tokens, status, error_type,
                    finish_reasons, conversation_id, duration_ms, start_time, end_time,
                    projected_at
                ) VALUES (
                    $raw_record_id, $trace_id, $span_id, $parent_span_id, $span_ordinal,
                    $operation, $category, $tool_name, $tool_type, $mcp_tool_name,
                    $mcp_server_hash, $agent_name, $request_model, $response_model,
                    $input_tokens, $output_tokens, $total_tokens, $reasoning_tokens,
                    $cache_read_tokens, $cache_creation_tokens, $status, $error_type,
                    $finish_reasons, $conversation_id, $duration_ms, $start_time, $end_time,
                    $projected_at
                );
                """;
            AddParameter(insert, "$raw_record_id", rawRecordId);
            AddParameter(insert, "$trace_id", span.TraceId);
            AddParameter(insert, "$span_id", span.SpanId);
            AddParameter(insert, "$parent_span_id", span.ParentSpanId);
            AddParameter(insert, "$span_ordinal", span.SpanOrdinal);
            AddParameter(insert, "$operation", span.Operation);
            AddParameter(insert, "$category", span.Category);
            AddParameter(insert, "$tool_name", span.ToolName);
            AddParameter(insert, "$tool_type", span.ToolType);
            AddParameter(insert, "$mcp_tool_name", span.McpToolName);
            AddParameter(insert, "$mcp_server_hash", span.McpServerHash);
            AddParameter(insert, "$agent_name", span.AgentName);
            AddParameter(insert, "$request_model", span.RequestModel);
            AddParameter(insert, "$response_model", span.ResponseModel);
            AddParameter(insert, "$input_tokens", span.InputTokens);
            AddParameter(insert, "$output_tokens", span.OutputTokens);
            AddParameter(insert, "$total_tokens", span.TotalTokens);
            AddParameter(insert, "$reasoning_tokens", span.ReasoningTokens);
            AddParameter(insert, "$cache_read_tokens", span.CacheReadTokens);
            AddParameter(insert, "$cache_creation_tokens", span.CacheCreationTokens);
            AddParameter(insert, "$status", span.Status);
            AddParameter(insert, "$error_type", span.ErrorType);
            AddParameter(insert, "$finish_reasons", span.FinishReasons);
            AddParameter(insert, "$conversation_id", span.ConversationId);
            AddParameter(insert, "$duration_ms", span.DurationMs);
            AddParameter(insert, "$start_time", span.StartTime);
            AddParameter(insert, "$end_time", span.EndTime);
            AddParameter(insert, "$projected_at", projectedAtText);
            insert.ExecuteNonQuery();
        }

        // Update rollup columns on monitor_traces for each affected trace_id.
        var affectedTraceIds = validSpans
            .Select(s => s.TraceId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var traceId in affectedTraceIds)
        {
            // Read back all spans for this trace_id within the transaction.
            var traceSpans = new List<MonitorSpanProjection>();
            using (var readSpans = connection.CreateCommand())
            {
                readSpans.Transaction = transaction;
                readSpans.CommandText =
                    """
                    SELECT trace_id, span_id, parent_span_id, span_ordinal,
                           operation, category, input_tokens, output_tokens, total_tokens,
                           request_model, response_model, start_time, end_time
                    FROM monitor_spans
                    WHERE trace_id = $trace_id
                    ORDER BY raw_record_id, span_ordinal;
                    """;
                AddParameter(readSpans, "$trace_id", traceId!);
                using var sr = readSpans.ExecuteReader();
                while (sr.Read())
                {
                    traceSpans.Add(new MonitorSpanProjection(
                        TraceId: sr.IsDBNull(0) ? null : sr.GetString(0),
                        SpanId: sr.IsDBNull(1) ? null : sr.GetString(1),
                        ParentSpanId: sr.IsDBNull(2) ? null : sr.GetString(2),
                        SpanOrdinal: sr.GetInt32(3),
                        Operation: sr.IsDBNull(4) ? null : sr.GetString(4),
                        Category: sr.IsDBNull(5) ? null : sr.GetString(5),
                        ToolName: null,
                        ToolType: null,
                        McpToolName: null,
                        McpServerHash: null,
                        AgentName: null,
                        RequestModel: sr.IsDBNull(9) ? null : sr.GetString(9),
                        ResponseModel: sr.IsDBNull(10) ? null : sr.GetString(10),
                        InputTokens: sr.IsDBNull(6) ? null : sr.GetInt32(6),
                        OutputTokens: sr.IsDBNull(7) ? null : sr.GetInt32(7),
                        TotalTokens: sr.IsDBNull(8) ? null : sr.GetInt32(8),
                        ReasoningTokens: null,
                        CacheReadTokens: null,
                        CacheCreationTokens: null,
                        Status: null,
                        ErrorType: null,
                        FinishReasons: null,
                        ConversationId: null,
                        DurationMs: null,
                        StartTime: sr.IsDBNull(11) ? null : sr.GetString(11),
                        EndTime: sr.IsDBNull(12) ? null : sr.GetString(12)));
                }
            }

            var rollup = MonitorTraceRollupBuilder.ComputeRollup(traceSpans);

            using var updateTrace = connection.CreateCommand();
            updateTrace.Transaction = transaction;
            updateTrace.CommandText =
                """
                UPDATE monitor_traces
                SET input_tokens = $it, output_tokens = $ot, total_tokens = $tt,
                    turn_count = $turn, agent_invocation_count = $aic,
                    duration_ms = $dur, primary_model = $pm
                WHERE trace_id = $trace_id;
                """;
            AddParameter(updateTrace, "$it", rollup.InputTokens);
            AddParameter(updateTrace, "$ot", rollup.OutputTokens);
            AddParameter(updateTrace, "$tt", rollup.TotalTokens);
            AddParameter(updateTrace, "$turn", rollup.TurnCount);
            AddParameter(updateTrace, "$aic", rollup.AgentInvocationCount);
            AddParameter(updateTrace, "$dur", rollup.DurationMs);
            AddParameter(updateTrace, "$pm", rollup.PrimaryModel);
            AddParameter(updateTrace, "$trace_id", traceId!);
            updateTrace.ExecuteNonQuery();
        }

        // Stamp the ingestion row as span-projected.
        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText =
                "UPDATE monitor_ingestions SET span_projected_at = $p WHERE raw_record_id = $id;";
            AddParameter(stamp, "$p", projectedAtText);
            AddParameter(stamp, "$id", rawRecordId);
            stamp.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>Backlog count for span projection (records with ingestion row but no span_projected_at).</summary>
    public MonitorProjectionStatus GetSpanProjectionStatus()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*), MIN(received_at)
            FROM monitor_ingestions
            WHERE span_projected_at IS NULL;
            """;

        using var reader = command.ExecuteReader();
        reader.Read();
        var backlog = reader.GetInt32(0);
        var oldest = reader.IsDBNull(1) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(1));
        return new MonitorProjectionStatus(backlog, oldest);
    }

    /// <summary>
    /// Cursor page of sanitized <c>monitor_spans</c> rows for a trace after
    /// <paramref name="afterId"/>, ordered by projection-row id, using the same
    /// <c>limit + 1</c> probe as the other cursor reads.
    /// </summary>
    public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, raw_record_id, trace_id, span_id, parent_span_id, span_ordinal,
                   operation, category, tool_name, tool_type, mcp_tool_name, mcp_server_hash,
                   agent_name, request_model, response_model, input_tokens, output_tokens,
                   total_tokens, reasoning_tokens, cache_read_tokens, cache_creation_tokens,
                   status, error_type, finish_reasons, conversation_id, duration_ms,
                   start_time, end_time, projected_at
            FROM monitor_spans
            WHERE trace_id = $trace_id AND id > $after
            ORDER BY id
            LIMIT $limit;
            """;
        AddParameter(command, "$trace_id", traceId);
        AddParameter(command, "$after", afterId);
        AddParameter(command, "$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var items = new List<MonitorSpanRow>();
        while (reader.Read())
        {
            items.Add(new MonitorSpanRow(
                Id: reader.GetInt64(0),
                RawRecordId: reader.GetInt64(1),
                TraceId: reader.GetString(2),
                SpanId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ParentSpanId: reader.IsDBNull(4) ? null : reader.GetString(4),
                SpanOrdinal: reader.GetInt32(5),
                Operation: reader.IsDBNull(6) ? null : reader.GetString(6),
                Category: reader.IsDBNull(7) ? null : reader.GetString(7),
                ToolName: reader.IsDBNull(8) ? null : reader.GetString(8),
                ToolType: reader.IsDBNull(9) ? null : reader.GetString(9),
                McpToolName: reader.IsDBNull(10) ? null : reader.GetString(10),
                McpServerHash: reader.IsDBNull(11) ? null : reader.GetString(11),
                AgentName: reader.IsDBNull(12) ? null : reader.GetString(12),
                RequestModel: reader.IsDBNull(13) ? null : reader.GetString(13),
                ResponseModel: reader.IsDBNull(14) ? null : reader.GetString(14),
                InputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                OutputTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                TotalTokens: reader.IsDBNull(17) ? null : reader.GetInt32(17),
                ReasoningTokens: reader.IsDBNull(18) ? null : reader.GetInt32(18),
                CacheReadTokens: reader.IsDBNull(19) ? null : reader.GetInt32(19),
                CacheCreationTokens: reader.IsDBNull(20) ? null : reader.GetInt32(20),
                Status: reader.IsDBNull(21) ? null : reader.GetString(21),
                ErrorType: reader.IsDBNull(22) ? null : reader.GetString(22),
                FinishReasons: reader.IsDBNull(23) ? null : reader.GetString(23),
                ConversationId: reader.IsDBNull(24) ? null : reader.GetString(24),
                DurationMs: reader.IsDBNull(25) ? null : reader.GetDouble(25),
                StartTime: reader.IsDBNull(26) ? null : reader.GetString(26),
                EndTime: reader.IsDBNull(27) ? null : reader.GetString(27),
                ProjectedAt: reader.GetString(28)));
        }

        return BuildPage(items, limit);
    }

    /// <summary>All <c>monitor_spans</c> rows for a trace, ordered for deterministic reads in tests.</summary>
    public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, raw_record_id, trace_id, span_id, parent_span_id, span_ordinal,
                   operation, category, tool_name, tool_type, mcp_tool_name, mcp_server_hash,
                   agent_name, request_model, response_model, input_tokens, output_tokens,
                   total_tokens, reasoning_tokens, cache_read_tokens, cache_creation_tokens,
                   status, error_type, finish_reasons, conversation_id, duration_ms,
                   start_time, end_time, projected_at
            FROM monitor_spans
            WHERE trace_id = $trace_id
            ORDER BY raw_record_id, span_ordinal;
            """;
        AddParameter(command, "$trace_id", traceId);

        using var reader = command.ExecuteReader();
        var rows = new List<MonitorSpanRow>();
        while (reader.Read())
        {
            rows.Add(new MonitorSpanRow(
                Id: reader.GetInt64(0),
                RawRecordId: reader.GetInt64(1),
                TraceId: reader.GetString(2),
                SpanId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ParentSpanId: reader.IsDBNull(4) ? null : reader.GetString(4),
                SpanOrdinal: reader.GetInt32(5),
                Operation: reader.IsDBNull(6) ? null : reader.GetString(6),
                Category: reader.IsDBNull(7) ? null : reader.GetString(7),
                ToolName: reader.IsDBNull(8) ? null : reader.GetString(8),
                ToolType: reader.IsDBNull(9) ? null : reader.GetString(9),
                McpToolName: reader.IsDBNull(10) ? null : reader.GetString(10),
                McpServerHash: reader.IsDBNull(11) ? null : reader.GetString(11),
                AgentName: reader.IsDBNull(12) ? null : reader.GetString(12),
                RequestModel: reader.IsDBNull(13) ? null : reader.GetString(13),
                ResponseModel: reader.IsDBNull(14) ? null : reader.GetString(14),
                InputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                OutputTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                TotalTokens: reader.IsDBNull(17) ? null : reader.GetInt32(17),
                ReasoningTokens: reader.IsDBNull(18) ? null : reader.GetInt32(18),
                CacheReadTokens: reader.IsDBNull(19) ? null : reader.GetInt32(19),
                CacheCreationTokens: reader.IsDBNull(20) ? null : reader.GetInt32(20),
                Status: reader.IsDBNull(21) ? null : reader.GetString(21),
                ErrorType: reader.IsDBNull(22) ? null : reader.GetString(22),
                FinishReasons: reader.IsDBNull(23) ? null : reader.GetString(23),
                ConversationId: reader.IsDBNull(24) ? null : reader.GetString(24),
                DurationMs: reader.IsDBNull(25) ? null : reader.GetDouble(25),
                StartTime: reader.IsDBNull(26) ? null : reader.GetString(26),
                EndTime: reader.IsDBNull(27) ? null : reader.GetString(27),
                ProjectedAt: reader.GetString(28)));
        }

        return rows;
    }

    /// <summary>Rollup columns for a single trace_id; null if trace not found.</summary>
    public MonitorTraceRollupRow? GetTraceRollup(string traceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT input_tokens, output_tokens, total_tokens, turn_count,
                   agent_invocation_count, duration_ms, primary_model
            FROM monitor_traces
            WHERE trace_id = $trace_id;
            """;
        AddParameter(command, "$trace_id", traceId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MonitorTraceRollupRow(
            InputTokens: reader.IsDBNull(0) ? null : reader.GetInt32(0),
            OutputTokens: reader.IsDBNull(1) ? null : reader.GetInt32(1),
            TotalTokens: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            TurnCount: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            AgentInvocationCount: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            DurationMs: reader.IsDBNull(5) ? null : reader.GetDouble(5),
            PrimaryModel: reader.IsDBNull(6) ? null : reader.GetString(6));
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

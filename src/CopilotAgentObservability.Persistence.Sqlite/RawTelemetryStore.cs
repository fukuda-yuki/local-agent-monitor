namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum RawTelemetryStoreWritePhase { AfterSourceInsert, AfterCatalogRegistration }

internal sealed partial class RawTelemetryStore
{
    private readonly string databasePath;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;
    private readonly Action<RawTelemetryStoreWritePhase>? writeFailureInjector;
    private readonly Retention.RetentionCatalogContext? retentionContext;

    internal string DatabasePath => databasePath;
    private readonly TimeProvider timeProvider;

    public RawTelemetryStore(string databasePath, RawTelemetryStoreConnectionOptions? connectionOptions = null, Action<RawTelemetryStoreWritePhase>? writeFailureInjector = null)
    {
        this.databasePath = databasePath;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
        this.writeFailureInjector = writeFailureInjector;
        timeProvider = TimeProvider.System;
    }

    public RawTelemetryStore(
        string databasePath,
        Retention.RetentionCatalogContext retentionContext,
        TimeProvider? timeProvider = null,
        RawTelemetryStoreConnectionOptions? connectionOptions = null,
        Action<RawTelemetryStoreWritePhase>? writeFailureInjector = null)
    {
        ArgumentNullException.ThrowIfNull(retentionContext);
        if (!string.Equals(Path.GetFullPath(databasePath), retentionContext.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The retention catalog context belongs to a different database.", nameof(retentionContext));

        this.databasePath = databasePath;
        this.retentionContext = retentionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
        this.writeFailureInjector = writeFailureInjector;
    }

    public const int MonitorSchemaVersion = MonitorSchemaMigrator.BaseSchemaVersion;

    public void CreateSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        using var transaction = connection.BeginTransaction();
        MonitorSchemaMigrator.EnsureRawRecordsSchema(connection, transaction);
        new Retention.RetentionCatalogStore(databasePath, timeProvider).InitializeForWrite(connection, transaction);
        transaction.Commit();
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
        using var transaction = connection.BeginTransaction();
        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
        new Retention.RetentionCatalogStore(databasePath, timeProvider).InitializeForWrite(connection, transaction);
        transaction.Commit();
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    public long Insert(RawTelemetryRecord record)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var catalog = new Retention.RetentionCatalogStore(databasePath, timeProvider);
        try { catalog.InitializeForWrite(connection, transaction); }
        catch (SqliteException) { throw new Retention.RetentionMigrationBlockedException(); }
        var ownerToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var rawRecordId = RawTelemetryRecordSql.Insert(connection, transaction, record, ownerToken);
        writeFailureInjector?.Invoke(RawTelemetryStoreWritePhase.AfterSourceInsert);
        catalog.RegisterRawRecord(connection, transaction, rawRecordId, record.ReceivedAt, record.SchemaVersion, ownerToken);
        writeFailureInjector?.Invoke(RawTelemetryStoreWritePhase.AfterCatalogRegistration);
        transaction.Commit();
        return rawRecordId;
    }

    public ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRecordsAsync(
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken) =>
        ReadSelectedRawRecordsAsync(
            static (connection, transaction) => SelectRawRecordIds(connection, transaction, "SELECT id FROM raw_records ORDER BY id;"),
            leaseKind,
            cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="limit"/> raw records that have no
    /// <c>monitor_ingestions</c> row yet, in id order. The payload is included
    /// because it is the projection worker's in-process input; it is never written
    /// to a projection table or a list response.
    /// </summary>
    public ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(
        int limit,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken) =>
        ReadSelectedRawRecordsAsync(
            (connection, transaction) => SelectRawRecordIds(connection, transaction, "SELECT id FROM raw_records WHERE id NOT IN (SELECT raw_record_id FROM monitor_ingestions) ORDER BY id LIMIT $limit;", limit),
            leaseKind,
            cancellationToken);

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
        DateTimeOffset projectedAt) =>
        ApplyProjectionCore(rawRecordId, source, receivedAt, projection, projectedAt, expectedDispositionRevision: null);

    public bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt,
        int expectedDispositionRevision) =>
        ApplyProjectionCore(rawRecordId, source, receivedAt, projection, projectedAt, expectedDispositionRevision);

    private bool ApplyProjectionCore(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt,
        int? expectedDispositionRevision)
    {
        var receivedAtText = FormatTimestamp(receivedAt);
        var projectedAtText = FormatTimestamp(projectedAt);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (expectedDispositionRevision is { } expectedRevision &&
            !HasPendingDisposition(connection, transaction, rawRecordId, expectedRevision))
        {
            transaction.Rollback();
            return false;
        }

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
                     span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at,
                     repository_name, workspace_label, repo_snapshot)
                VALUES ($trace_id, $client_kind, $experiment_id, $task_id, $task_category, $agent_variant, $prompt_version,
                     $span_count, $tool_call_count, $error_count, $seen_at, $seen_at, $projected_at,
                     $repository_name, $workspace_label, $repo_snapshot)
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
                    repository_name = COALESCE(repository_name, excluded.repository_name),
                    workspace_label = COALESCE(workspace_label, excluded.workspace_label),
                    repo_snapshot = COALESCE(repo_snapshot, excluded.repo_snapshot),
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
            AddParameter(upsert, "$repository_name", contribution.RepositoryName);
            AddParameter(upsert, "$workspace_label", contribution.WorkspaceLabel);
            AddParameter(upsert, "$repo_snapshot", contribution.RepoSnapshot);
            upsert.ExecuteNonQuery();
        }

        if (expectedDispositionRevision is { } dispositionRevision)
        {
            using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText =
                """
                UPDATE monitor_projection_dispositions
                SET state = 'completed', revision = revision + 1, updated_at = $updated_at
                WHERE raw_record_id = $raw_record_id AND state = 'pending' AND revision = $expected_revision;
                """;
            AddParameter(complete, "$updated_at", projectedAtText);
            AddParameter(complete, "$raw_record_id", rawRecordId);
            AddParameter(complete, "$expected_revision", dispositionRevision);
            if (complete.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        return true;
    }

    public ProjectionDisposition? GetProjectionDisposition(long rawRecordId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT raw_record_id, state, revision, updated_at
            FROM monitor_projection_dispositions
            WHERE raw_record_id = $raw_record_id;
            """;
        AddParameter(command, "$raw_record_id", rawRecordId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ProjectionDisposition(
                reader.GetInt64(0),
                ParseProjectionDispositionState(reader.GetString(1)),
                reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3))!.Value)
            : null;
    }

    public bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
        TransitionProjectionDisposition(
            rawRecordId,
            expectedRevision,
            updatedAt,
            "state IN ('not_started', 'pending', 'failed')",
            "pending");

    public bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
        TransitionProjectionDisposition(rawRecordId, expectedRevision, updatedAt, "state = 'pending'", "failed");

    private bool TransitionProjectionDisposition(
        long rawRecordId,
        int expectedRevision,
        DateTimeOffset updatedAt,
        string expectedStateSql,
        string nextState)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE monitor_projection_dispositions
            SET state = $next_state, revision = revision + 1, updated_at = $updated_at
            WHERE raw_record_id = $raw_record_id AND revision = $expected_revision AND {expectedStateSql};
            """;
        AddParameter(command, "$next_state", nextState);
        AddParameter(command, "$updated_at", FormatTimestamp(updatedAt));
        AddParameter(command, "$raw_record_id", rawRecordId);
        AddParameter(command, "$expected_revision", expectedRevision);
        return command.ExecuteNonQuery() == 1;
    }

    private static bool HasPendingDisposition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        int expectedRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM monitor_projection_dispositions
            WHERE raw_record_id = $raw_record_id AND state = 'pending' AND revision = $expected_revision;
            """;
        AddParameter(command, "$raw_record_id", rawRecordId);
        AddParameter(command, "$expected_revision", expectedRevision);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static ProjectionDispositionState ParseProjectionDispositionState(string state) => state switch
    {
        "not_started" => ProjectionDispositionState.NotStarted,
        "pending" => ProjectionDispositionState.Pending,
        "completed" => ProjectionDispositionState.Completed,
        "failed" => ProjectionDispositionState.Failed,
        _ => throw new InvalidOperationException("The stored projection disposition state is invalid."),
    };

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
                   input_tokens, output_tokens, total_tokens, turn_count, agent_invocation_count, duration_ms, primary_model,
                   repository_name, workspace_label, repo_snapshot, cache_read_tokens, cache_creation_tokens, trace_status
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
                PrimaryModel: reader.IsDBNull(20) ? null : reader.GetString(20),
                RepositoryName: reader.IsDBNull(21) ? null : reader.GetString(21),
                WorkspaceLabel: reader.IsDBNull(22) ? null : reader.GetString(22),
                RepoSnapshot: reader.IsDBNull(23) ? null : reader.GetString(23),
                CacheReadTokens: reader.IsDBNull(24) ? null : reader.GetInt32(24),
                CacheCreationTokens: reader.IsDBNull(25) ? null : reader.GetInt32(25),
                TraceStatus: reader.IsDBNull(26) ? null : reader.GetString(26)));
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
                   input_tokens, output_tokens, total_tokens, turn_count, agent_invocation_count, duration_ms, primary_model,
                   repository_name, workspace_label, repo_snapshot, cache_read_tokens, cache_creation_tokens, trace_status
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
            PrimaryModel: reader.IsDBNull(20) ? null : reader.GetString(20),
            RepositoryName: reader.IsDBNull(21) ? null : reader.GetString(21),
            WorkspaceLabel: reader.IsDBNull(22) ? null : reader.GetString(22),
            RepoSnapshot: reader.IsDBNull(23) ? null : reader.GetString(23),
            CacheReadTokens: reader.IsDBNull(24) ? null : reader.GetInt32(24),
            CacheCreationTokens: reader.IsDBNull(25) ? null : reader.GetInt32(25),
            TraceStatus: reader.IsDBNull(26) ? null : reader.GetString(26));
    }

    public ValueTask<Retention.RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(
        long id,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken)
    {
        var context = retentionContext ?? throw new Retention.RetentionCatalogUnavailableException();
        var key = new Retention.RetentionOwnershipKey(
            context.StoreInstanceId,
            Retention.RetentionStoreKind.RawRecord,
            id.ToString(CultureInfo.InvariantCulture));

        return new Retention.RetentionCatalogStore(context, timeProvider).ReadAsync(
            new Retention.RetentionReadRequest(key, leaseKind, timeProvider.GetUtcNow(), ExpectedRevision: null),
            (connection, transaction, grant, _) => ValueTask.FromResult(SelectExactRawRecord(connection, transaction, id, grant)),
            cancellationToken);
    }

    public ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(
        IReadOnlyList<long> ids,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var context = retentionContext ?? throw new Retention.RetentionCatalogUnavailableException();
        var requestedIds = ids.Distinct().ToArray();
        if (requestedIds.Length == 0)
            return ValueTask.FromResult(new Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>(
                Retention.RetentionReadDisposition.Granted,
                new Retention.RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>([], Retention.RetentionRevisionFence.Create(), static () => ValueTask.CompletedTask)));
        var now = timeProvider.GetUtcNow();
        var requests = requestedIds
            .Select(id => new Retention.RetentionReadRequest(
                new Retention.RetentionOwnershipKey(context.StoreInstanceId, Retention.RetentionStoreKind.RawRecord, id.ToString(CultureInfo.InvariantCulture)),
                leaseKind,
                now,
                ExpectedRevision: null))
            .ToArray();

        return new Retention.RetentionCatalogStore(context, timeProvider).ReadBatchAsync(
            requests,
            (connection, transaction, grants, _) => ValueTask.FromResult(SelectExactRawRecords(connection, transaction, requestedIds, grants)),
            cancellationToken);
    }

    /// <summary>
    /// Raw records for a trace (ordered by id) for the raw-bearing trace-detail
    /// page's bounded inline preview. Uses the span projection's raw_record_id
    /// mapping so secondary traces inside a multi-trace OTLP request resolve to
    /// their containing raw payload.
    /// </summary>
    public ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(
        string traceId,
        int limit,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken) =>
        ReadSelectedRawRecordsAsync(
            (connection, transaction) => SelectRawRecordIdsForTrace(connection, transaction, traceId, limit),
            leaseKind,
            cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="limit"/> raw records whose
    /// <c>monitor_ingestions.span_projected_at</c> is NULL (span projection not yet
    /// applied), ordered by id. A record must already have a <c>monitor_ingestions</c>
    /// row (i.e. trace-projection has completed) to be eligible.
    /// </summary>
    public ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(
        int limit,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken) =>
        ReadSelectedRawRecordsAsync(
            (connection, transaction) => SelectRawRecordIds(connection, transaction, "SELECT id FROM raw_records WHERE id IN (SELECT raw_record_id FROM monitor_ingestions WHERE span_projected_at IS NULL) ORDER BY id LIMIT $limit;", limit),
            leaseKind,
            cancellationToken);

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
                           request_model, response_model, start_time, end_time,
                           cache_read_tokens, cache_creation_tokens, status
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
                        CacheReadTokens: sr.IsDBNull(13) ? null : sr.GetInt32(13),
                        CacheCreationTokens: sr.IsDBNull(14) ? null : sr.GetInt32(14),
                        Status: sr.IsDBNull(15) ? null : sr.GetString(15),
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
                    duration_ms = $dur, primary_model = $pm,
                    cache_read_tokens = $crt, cache_creation_tokens = $cct,
                    trace_status = $ts
                WHERE trace_id = $trace_id;
                """;
            AddParameter(updateTrace, "$it", rollup.InputTokens);
            AddParameter(updateTrace, "$ot", rollup.OutputTokens);
            AddParameter(updateTrace, "$tt", rollup.TotalTokens);
            AddParameter(updateTrace, "$turn", rollup.TurnCount);
            AddParameter(updateTrace, "$aic", rollup.AgentInvocationCount);
            AddParameter(updateTrace, "$dur", rollup.DurationMs);
            AddParameter(updateTrace, "$pm", rollup.PrimaryModel);
            AddParameter(updateTrace, "$crt", rollup.CacheReadTokens);
            AddParameter(updateTrace, "$cct", rollup.CacheCreationTokens);
            AddParameter(updateTrace, "$ts", rollup.TraceStatus);
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

    /// <summary>
    /// Sibling traces sharing one <c>conversation_id</c>, ordered by earliest span
    /// start time then trace id (deterministic; Sprint20, D047). Metadata only.
    /// </summary>
    public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trace_id, MIN(start_time) AS first_start_time
            FROM monitor_spans
            WHERE conversation_id = $conversation_id
            GROUP BY trace_id
            ORDER BY MIN(start_time), trace_id;
            """;
        AddParameter(command, "$conversation_id", conversationId);

        using var reader = command.ExecuteReader();
        var rows = new List<MonitorConversationTraceRow>();
        while (reader.Read())
        {
            rows.Add(new MonitorConversationTraceRow(
                TraceId: reader.GetString(0),
                FirstStartTime: reader.IsDBNull(1) ? null : reader.GetString(1)));
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

    private ValueTask<Retention.RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadSelectedRawRecordsAsync(
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<long>> candidateSelector,
        Retention.RetentionReadKind leaseKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateSelector);
        var context = retentionContext ?? throw new Retention.RetentionCatalogUnavailableException();
        IReadOnlyList<long>? selectedIds = null;
        return new Retention.RetentionCatalogStore(context, timeProvider).ReadSelectedBatchAsync(
            (connection, transaction, _) =>
            {
                var now = timeProvider.GetUtcNow();
                selectedIds = candidateSelector(connection, transaction)
                    .Distinct()
                    .ToArray();
                var requests = selectedIds
                    .Select(id => new Retention.RetentionReadRequest(
                        new Retention.RetentionOwnershipKey(context.StoreInstanceId, Retention.RetentionStoreKind.RawRecord, id.ToString(CultureInfo.InvariantCulture)),
                        leaseKind,
                        now,
                        ExpectedRevision: null))
                    .ToArray();
                return ValueTask.FromResult<IReadOnlyList<Retention.RetentionReadRequest>>(requests);
            },
            (connection, transaction, grants, _) =>
            {
                return ValueTask.FromResult(SelectExactRawRecords(connection, transaction, selectedIds!, grants));
            },
            cancellationToken);
    }

    private static IReadOnlyList<long> SelectRawRecordIds(SqliteConnection connection, SqliteTransaction transaction, string commandText, int? limit = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        if (limit is not null) AddParameter(command, "$limit", limit.Value);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static IReadOnlyList<long> SelectRawRecordIdsForTrace(SqliteConnection connection, SqliteTransaction transaction, string traceId, int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id
            FROM raw_records
            WHERE id IN (SELECT DISTINCT raw_record_id FROM monitor_spans WHERE trace_id = $trace_id)
            ORDER BY id
            LIMIT $limit;
            """;
        AddParameter(command, "$trace_id", traceId);
        AddParameter(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static RawTelemetryRecord? SelectExactRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        Retention.RetentionReadGrant grant)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, source, trace_id, received_at, resource_attributes_json, payload_json, schema_version
            FROM raw_records
            WHERE id=$id AND retention_owner_token=$retention_read_source_token
              AND EXISTS (SELECT 1 FROM retention_items WHERE item_id=$retention_read_item_id AND revision=$retention_read_revision)
              AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
            """;
        AddParameter(command, "$id", id);
        grant.BindSelectorCapability(command);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRawRecord(reader) : null;
    }

    private static IReadOnlyList<RawTelemetryRecord>? SelectExactRawRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> ids,
        IReadOnlyList<Retention.RetentionReadGrant> grants)
    {
        var records = new List<RawTelemetryRecord>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var record = SelectExactRawRecord(connection, transaction, ids[index], grants[index]);
            if (record is null) return null;
            records.Add(record);
        }
        return records;
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

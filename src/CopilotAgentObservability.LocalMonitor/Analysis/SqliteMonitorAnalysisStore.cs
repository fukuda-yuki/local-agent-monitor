using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal enum MonitorAnalysisStoreWritePhase { AfterSourceWrite, AfterCatalogRegistration, BeforeCommit }

internal interface IMonitorAnalysisStore
{
    void CreateSchema();

    MonitorAnalysisStartResult StartRun(
        string traceId,
        long? rawRecordId,
        string? spanId,
        MonitorAnalysisFocus focus,
        DateTimeOffset requestedAt);

    MonitorAnalysisRun? GetRun(long runId);

    IReadOnlyList<MonitorAnalysisRun> ListRunsForTrace(string traceId, int limit);

    ValueTask<RetentionReadResult<AnalysisRunRawSnapshot>> ReadRawSnapshotAsync(long runId, CancellationToken cancellationToken);

    void MarkRunning(long runId, DateTimeOffset startedAt);

    RetentionRevisionFence AppendEvent(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string eventType, string message, DateTimeOffset occurredAt);

    RetentionRevisionFence CompleteRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, DateTimeOffset completedAt);

    RetentionRevisionFence? FinishRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt);

    MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt);
}

internal sealed class SqliteMonitorAnalysisStore : IMonitorAnalysisStore
{
    private static readonly byte[] OperationTokenDomain = "copilot-agent-observability/monitor-analysis-operation-token/v1"u8.ToArray();
    private readonly string databasePath;
    private readonly RetentionCatalogContext retentionContext;
    private readonly TimeProvider timeProvider;
    private readonly Action<MonitorAnalysisStoreWritePhase>? writeFailureInjector;
    private readonly Func<CancellationToken, ValueTask>? rawSnapshotSelectorBarrier;
    private readonly Action<SqliteConnection>? beforeRawWriterBegin;

    public SqliteMonitorAnalysisStore(string databasePath, RetentionCatalogContext retentionContext, TimeProvider timeProvider, Action<MonitorAnalysisStoreWritePhase>? writeFailureInjector = null, Func<CancellationToken, ValueTask>? rawSnapshotSelectorBarrier = null, Action<SqliteConnection>? beforeRawWriterBegin = null)
    {
        this.databasePath = databasePath;
        this.retentionContext = retentionContext;
        this.timeProvider = timeProvider;
        this.writeFailureInjector = writeFailureInjector;
        this.rawSnapshotSelectorBarrier = rawSnapshotSelectorBarrier;
        this.beforeRawWriterBegin = beforeRawWriterBegin;
    }

    public void CreateSchema()
    {
        var adopted = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        if (!string.Equals(adopted.StoreInstanceId, retentionContext.StoreInstanceId, StringComparison.Ordinal))
            throw new RetentionCatalogUnavailableException();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_analysis_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL,
                raw_record_id INTEGER NULL,
                span_id TEXT NULL,
                focus TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                started_at TEXT NULL,
                completed_at TEXT NULL,
                result_markdown TEXT NULL,
                error_message TEXT NULL,
                retention_owner_token BLOB NOT NULL CHECK(typeof(retention_owner_token) = 'blob' AND length(retention_owner_token) = 32)
            );
            """);
        ExecuteNonQuery(connection, transaction,
            "CREATE INDEX IF NOT EXISTS IX_monitor_analysis_runs_trace_id ON monitor_analysis_runs(trace_id);");
        ExecuteNonQuery(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_analysis_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES monitor_analysis_runs(id) ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS monitor_analysis_safe_summaries (
                run_id INTEGER PRIMARY KEY,
                markdown TEXT NOT NULL,
                generated_at TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, transaction,
            "CREATE TRIGGER IF NOT EXISTS retention_monitor_analysis_runs_token_immutable BEFORE UPDATE OF retention_owner_token ON monitor_analysis_runs WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");
        transaction.Commit();
    }

    public MonitorAnalysisStartResult StartRun(
        string traceId,
        long? rawRecordId,
        string? spanId,
        MonitorAnalysisFocus focus,
        DateTimeOffset requestedAt)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO monitor_analysis_runs (
                trace_id, raw_record_id, span_id, focus, status, requested_at, retention_owner_token
            ) VALUES (
                $trace_id, $raw_record_id, $span_id, $focus, $status, $requested_at, $retention_owner_token
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$trace_id", traceId);
        AddParameter(command, "$raw_record_id", rawRecordId);
        AddParameter(command, "$span_id", string.IsNullOrWhiteSpace(spanId) ? null : spanId);
        AddParameter(command, "$focus", focus.ToWireValue());
        AddParameter(command, "$status", MonitorAnalysisStatus.Queued.ToWireValue());
        AddParameter(command, "$requested_at", FormatTimestamp(requestedAt));
        var ownerToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        AddParameter(command, "$retention_owner_token", ownerToken);
        var runId = (long)(long)command.ExecuteScalar()!;
        transaction.Commit();
        return new MonitorAnalysisStartResult(runId, new MonitorAnalysisOperationToken(DeriveOperationToken(ownerToken, runId, requestedAt, rawRecordId, string.IsNullOrWhiteSpace(spanId) ? null : spanId)));
    }

    public MonitorAnalysisRun? GetRun(long runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trace_id, raw_record_id, span_id, focus, status, requested_at,
                   started_at, completed_at
            FROM monitor_analysis_runs
            WHERE id = $id;
            """;
        AddParameter(command, "$id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public IReadOnlyList<MonitorAnalysisRun> ListRunsForTrace(string traceId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trace_id, raw_record_id, span_id, focus, status, requested_at,
                   started_at, completed_at
            FROM monitor_analysis_runs
            WHERE trace_id = $trace_id
            ORDER BY id DESC
            LIMIT $limit;
            """;
        AddParameter(command, "$trace_id", traceId);
        AddParameter(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<MonitorAnalysisRun>();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public ValueTask<RetentionReadResult<AnalysisRunRawSnapshot>> ReadRawSnapshotAsync(long runId, CancellationToken cancellationToken)
    {
        var request = new RetentionReadRequest(
            new RetentionOwnershipKey(retentionContext.StoreInstanceId, RetentionStoreKind.AnalysisRunRaw, runId.ToString(CultureInfo.InvariantCulture)),
            RetentionReadKind.Access,
            timeProvider.GetUtcNow(),
            ExpectedRevision: null);
        return new RetentionCatalogStore(retentionContext, timeProvider).ReadAsync(
            request,
            async (connection, transaction, grant, cancellationToken) =>
            {
                if (rawSnapshotSelectorBarrier is not null) await rawSnapshotSelectorBarrier(cancellationToken).ConfigureAwait(false);
                return ReadRawSnapshot(connection, transaction, grant, runId);
            },
            cancellationToken);
    }

    public void MarkRunning(long runId, DateTimeOffset startedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE monitor_analysis_runs
            SET status = $status, started_at = COALESCE(started_at, $started_at)
            WHERE id = $id;
            """;
        AddParameter(command, "$status", MonitorAnalysisStatus.Running.ToWireValue());
        AddParameter(command, "$started_at", FormatTimestamp(startedAt));
        AddParameter(command, "$id", runId);
        command.ExecuteNonQuery();
    }

    public RetentionRevisionFence AppendEvent(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string eventType, string message, DateTimeOffset occurredAt)
    {
        using var connection = OpenConnection();
        using var transaction = BeginRawWriterTransaction(connection);
        var run = ReadRetentionRun(connection, transaction, runId);
        var createsRaw = ValidateRawWrite(connection, transaction, run, operationToken, expectedFence);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO monitor_analysis_events (run_id, event_type, message, occurred_at)
            VALUES ($run_id, $event_type, $message, $occurred_at);
            """;
        AddParameter(command, "$run_id", runId);
        AddParameter(command, "$event_type", eventType);
        AddParameter(command, "$message", message);
        AddParameter(command, "$occurred_at", FormatTimestamp(occurredAt));
        RequireSingleSourceRow(command);
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (createsRaw)
        {
            new RetentionCatalogStore(retentionContext, timeProvider).RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        var fence = IssueFence(connection, transaction, run, operationToken);
        transaction.Commit();
        return fence;
    }

    public RetentionRevisionFence CompleteRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, DateTimeOffset completedAt)
    {
        using var connection = OpenConnection();
        using var transaction = BeginRawWriterTransaction(connection);
        var run = ReadRetentionRun(connection, transaction, runId);
        var createsRaw = ValidateRawWrite(connection, transaction, run, operationToken, expectedFence);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE monitor_analysis_runs
            SET status = $status, completed_at = $completed_at, result_markdown = $result_markdown, error_message = NULL
            WHERE id = $id;
            """;
        AddParameter(command, "$status", MonitorAnalysisStatus.Succeeded.ToWireValue());
        AddParameter(command, "$completed_at", FormatTimestamp(completedAt));
        AddParameter(command, "$result_markdown", resultMarkdown);
        AddParameter(command, "$id", runId);
        RequireSingleSourceRow(command);
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (createsRaw)
        {
            new RetentionCatalogStore(retentionContext, timeProvider).RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        var fence = IssueFence(connection, transaction, run, operationToken);
        transaction.Commit();
        return fence;
    }

    public RetentionRevisionFence? FinishRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt)
    {
        if (status == MonitorAnalysisStatus.Succeeded)
        {
            return CompleteRun(runId, operationToken, expectedFence, message ?? string.Empty, completedAt);
        }

        using var connection = OpenConnection();
        using var transaction = BeginRawWriterTransaction(connection);
        var run = ReadRetentionRun(connection, transaction, runId);
        var writesRawMessage = message is not null;
        var createsRaw = writesRawMessage && ValidateRawWrite(connection, transaction, run, operationToken, expectedFence);
        if (!writesRawMessage && HasCatalogItem(connection, transaction, run.Id))
            _ = ValidateRawWrite(connection, transaction, run, operationToken, expectedFence);
        if (!writesRawMessage && !HasCatalogItem(connection, transaction, run.Id) && expectedFence is not null)
            throw new RetentionRevisionFenceRejectedException();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE monitor_analysis_runs
            SET status = $status, completed_at = $completed_at, error_message = $message
            WHERE id = $id;
            """;
        AddParameter(command, "$status", status.ToWireValue());
        AddParameter(command, "$completed_at", FormatTimestamp(completedAt));
        AddParameter(command, "$message", message);
        AddParameter(command, "$id", runId);
        RequireSingleSourceRow(command);
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (createsRaw)
        {
            new RetentionCatalogStore(retentionContext, timeProvider).RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        var fence = writesRawMessage || HasCatalogItem(connection, transaction, run.Id)
            ? IssueFence(connection, transaction, run, operationToken)
            : null;
        transaction.Commit();
        return fence;
    }

    public MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt)
    {
        var run = GetRun(runId) ?? throw new InvalidOperationException("analysis run not found.");
        var markdown = $"""
            # Repository-safe Copilot analysis summary

            repository_safe: true
            analysis_run_id: {run.Id.ToString(CultureInfo.InvariantCulture)}
            trace_id: {run.TraceId}
            raw_record_ref: {(run.RawRecordId is { } id ? $"raw record {id.ToString(CultureInfo.InvariantCulture)}" : "none")}
            span_ref: {run.SpanId ?? "none"}
            focus: {run.Focus.ToWireValue()}
            status: {run.Status.ToWireValue()}
            evidence: metrics, ids, statuses, and local raw references only; raw bodies are excluded.
            """;

        var summary = new MonitorAnalysisSafeSummary(run.Id, markdown, FormatTimestamp(generatedAt));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO monitor_analysis_safe_summaries (run_id, markdown, generated_at)
            VALUES ($run_id, $markdown, $generated_at)
            ON CONFLICT(run_id) DO UPDATE SET markdown = excluded.markdown, generated_at = excluded.generated_at;
            """;
        AddParameter(command, "$run_id", summary.RunId);
        AddParameter(command, "$markdown", summary.Markdown);
        AddParameter(command, "$generated_at", summary.GeneratedAt);
        command.ExecuteNonQuery();
        return summary;
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private SqliteTransaction BeginRawWriterTransaction(SqliteConnection connection)
    {
        beforeRawWriterBegin?.Invoke(connection);
        return connection.BeginTransaction(deferred: false);
    }

    private static RetentionRun ReadRetentionRun(SqliteConnection connection, SqliteTransaction transaction, long runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, requested_at, raw_record_id, span_id, retention_owner_token FROM monitor_analysis_runs WHERE id=$id;";
        AddParameter(command, "$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(4) || reader.GetFieldValue<byte[]>(4) is not { Length: 32 } ownerToken)
        {
            throw new InvalidOperationException("analysis run is not writable.");
        }

        if (!DateTimeOffset.TryParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requestedAt))
        {
            throw new InvalidOperationException("analysis run is not writable.");
        }

        return new RetentionRun(reader.GetInt64(0), requestedAt, reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), ownerToken);
    }

    private static bool HasRawAggregate(SqliteConnection connection, SqliteTransaction transaction, long runId, bool excludeEvents = false, bool excludeResult = false, bool excludeError = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT EXISTS(
                SELECT 1 FROM monitor_analysis_runs r
                WHERE r.id=$id
                  AND ({(excludeResult ? "0" : "r.result_markdown IS NOT NULL")}
                       OR {(excludeError ? "0" : "r.error_message IS NOT NULL")}
                       OR {(excludeEvents ? "0" : "EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=r.id)")})
            );
            """;
        AddParameter(command, "$id", runId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private bool ValidateRawWrite(SqliteConnection connection, SqliteTransaction transaction, RetentionRun run, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence)
    {
        if (!operationToken.Matches(DeriveOperationToken(run.OwnerToken, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId))) throw new RetentionRevisionFenceRejectedException();
        var item = ReadCatalogItem(connection, transaction, run.Id);
        var hasRaw = HasRawAggregate(connection, transaction, run.Id);
        if (expectedFence is null)
        {
            if (item is not null || hasRaw) throw new RetentionRevisionFenceRejectedException();
            return true;
        }

        if (item is null || !expectedFence.MatchesAnalysisRunRaw(item.ItemId, retentionContext.StoreInstanceId, run.Id, item.Revision, run.OwnerToken, operationToken.Copy()))
            throw new RetentionRevisionFenceRejectedException();
        try
        {
            new RetentionCatalogStore(retentionContext, timeProvider).AssertAnalysisRunRawWritable(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
        }
        catch (RetentionMigrationBlockedException)
        {
            throw new RetentionRevisionFenceRejectedException();
        }

        return false;
    }

    private RetentionRevisionFence IssueFence(SqliteConnection connection, SqliteTransaction transaction, RetentionRun run, MonitorAnalysisOperationToken operationToken)
    {
        var item = ReadCatalogItem(connection, transaction, run.Id) ?? throw new RetentionRevisionFenceRejectedException();
        return RetentionRevisionFence.CreateAnalysisRunRaw(item.ItemId, retentionContext.StoreInstanceId, run.Id, item.Revision, run.OwnerToken, operationToken.Copy());
    }

    private byte[] DeriveOperationToken(byte[] ownerToken, long runId, DateTimeOffset requestedAt, long? rawRecordId, string? spanId)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, OperationTokenDomain);
        WriteFrame(stream, System.Text.Encoding.UTF8.GetBytes(retentionContext.StoreInstanceId));
        WriteFrame(stream, ownerToken);
        WriteInt64(stream, runId);
        WriteFrame(stream, System.Text.Encoding.UTF8.GetBytes(FormatTimestamp(requestedAt)));
        stream.WriteByte(rawRecordId.HasValue ? (byte)1 : (byte)0);
        if (rawRecordId.HasValue) WriteInt64(stream, rawRecordId.Value);
        stream.WriteByte(spanId is null ? (byte)0 : (byte)1);
        if (spanId is not null) WriteFrame(stream, System.Text.Encoding.UTF8.GetBytes(spanId));
        return System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private bool HasCatalogItem(SqliteConnection connection, SqliteTransaction transaction, long runId) =>
        ReadCatalogItem(connection, transaction, runId) is not null;

    private CatalogItem? ReadCatalogItem(SqliteConnection connection, SqliteTransaction transaction, long runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT item_id, revision FROM retention_items WHERE store_instance_id=$store AND store_kind='analysis_run_raw' AND source_item_id=$source;";
        AddParameter(command, "$store", retentionContext.StoreInstanceId);
        AddParameter(command, "$source", runId.ToString(CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new CatalogItem(reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static void RequireSingleSourceRow(SqliteCommand command)
    {
        if (command.ExecuteNonQuery() != 1) throw new RetentionRevisionFenceRejectedException();
    }

    private static MonitorAnalysisRun ReadRun(SqliteDataReader reader)
    {
        var focus = reader.GetString(4);
        var status = reader.GetString(5);
        if (!MonitorAnalysisWire.TryParseFocus(focus, out var parsedFocus))
        {
            parsedFocus = MonitorAnalysisFocus.Latency;
        }

        var parsedStatus = status switch
        {
            "queued" => MonitorAnalysisStatus.Queued,
            "running" => MonitorAnalysisStatus.Running,
            "succeeded" => MonitorAnalysisStatus.Succeeded,
            "failed" => MonitorAnalysisStatus.Failed,
            "canceled" => MonitorAnalysisStatus.Canceled,
            "timed_out" => MonitorAnalysisStatus.TimedOut,
            _ => MonitorAnalysisStatus.Failed,
        };

        return new MonitorAnalysisRun(
            Id: reader.GetInt64(0),
            TraceId: reader.GetString(1),
            RawRecordId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
            SpanId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Focus: parsedFocus,
            Status: parsedStatus,
            RequestedAt: reader.GetString(6),
            StartedAt: reader.IsDBNull(7) ? null : reader.GetString(7),
            CompletedAt: reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static AnalysisRunRawSnapshot? ReadRawSnapshot(SqliteConnection connection, SqliteTransaction transaction, RetentionReadGrant grant, long runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH authorized_run AS (
                SELECT r.id
                FROM monitor_analysis_runs r
                JOIN retention_items i ON i.item_id = $retention_read_item_id
                    AND i.revision = $retention_read_revision
                    AND i.store_kind = 'analysis_run_raw'
                    AND i.source_item_id = CAST($run_id AS TEXT)
                JOIN retention_leases l ON l.item_id = i.item_id
                    AND l.lease_kind = 'access'
                    AND l.owner = $retention_read_lease_owner
                    AND l.generation = $retention_read_lease_generation
                    AND l.expires_at = $retention_read_lease_expires_at
                WHERE r.id = $run_id
                  AND r.retention_owner_token = $retention_read_source_token
            )
            SELECT r.result_markdown, r.error_message
            FROM monitor_analysis_runs r JOIN authorized_run a ON a.id = r.id;

            WITH authorized_run AS (
                SELECT r.id
                FROM monitor_analysis_runs r
                JOIN retention_items i ON i.item_id = $retention_read_item_id
                    AND i.revision = $retention_read_revision
                    AND i.store_kind = 'analysis_run_raw'
                    AND i.source_item_id = CAST($run_id AS TEXT)
                JOIN retention_leases l ON l.item_id = i.item_id
                    AND l.lease_kind = 'access'
                    AND l.owner = $retention_read_lease_owner
                    AND l.generation = $retention_read_lease_generation
                    AND l.expires_at = $retention_read_lease_expires_at
                WHERE r.id = $run_id
                  AND r.retention_owner_token = $retention_read_source_token
            )
            SELECT e.event_type, e.message, e.occurred_at
            FROM monitor_analysis_events e JOIN authorized_run a ON a.id = e.run_id
            ORDER BY e.occurred_at, e.id;
            """;
        AddParameter(command, "$run_id", runId);
        grant.BindSelectorCapability(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = reader.IsDBNull(0) ? null : reader.GetString(0);
        var error = reader.IsDBNull(1) ? null : reader.GetString(1);
        var events = new List<AnalysisRunRawEvent>();
        if (reader.NextResult())
        {
            while (reader.Read()) events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return new AnalysisRunRawSnapshot(result, error, events);
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private sealed record RetentionRun(long Id, DateTimeOffset RequestedAt, long? RawRecordId, string? SpanId, byte[] OwnerToken);
    private sealed record CatalogItem(string ItemId, long Revision);
}

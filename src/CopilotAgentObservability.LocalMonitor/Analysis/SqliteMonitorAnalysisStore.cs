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

    void MarkRunning(long runId, DateTimeOffset startedAt);

    void AppendEvent(long runId, string eventType, string message, DateTimeOffset occurredAt);

    void CompleteRun(long runId, string resultMarkdown, DateTimeOffset completedAt);

    void FinishRun(long runId, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt);

    MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt);
}

internal sealed class SqliteMonitorAnalysisStore : IMonitorAnalysisStore
{
    private readonly string databasePath;
    private readonly Action<MonitorAnalysisStoreWritePhase>? writeFailureInjector;

    public SqliteMonitorAnalysisStore(string databasePath, Action<MonitorAnalysisStoreWritePhase>? writeFailureInjector = null)
    {
        this.databasePath = databasePath;
        this.writeFailureInjector = writeFailureInjector;
    }

    public void CreateSchema()
    {
        EnsureParentDirectory();
        new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema();
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
        new RetentionCatalogStore(databasePath).InitializeForWrite(connection, transaction);
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
        AddParameter(command, "$retention_owner_token", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var runId = (long)(long)command.ExecuteScalar()!;
        transaction.Commit();
        return new MonitorAnalysisStartResult(runId);
    }

    public MonitorAnalysisRun? GetRun(long runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trace_id, raw_record_id, span_id, focus, status, requested_at,
                   started_at, completed_at, result_markdown, error_message
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
                   started_at, completed_at, result_markdown, error_message
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

    public void AppendEvent(long runId, string eventType, string message, DateTimeOffset occurredAt)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var run = ReadRetentionRun(connection, transaction, runId);
        var catalog = new RetentionCatalogStore(databasePath);
        if (HasRawAggregate(connection, transaction, runId))
        {
            catalog.AssertAnalysisRunRawWritable(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
        }
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
        command.ExecuteNonQuery();
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (!HasRawAggregate(connection, transaction, runId, excludeEvents: true))
        {
            catalog.RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        transaction.Commit();
    }

    public void CompleteRun(long runId, string resultMarkdown, DateTimeOffset completedAt)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var run = ReadRetentionRun(connection, transaction, runId);
        var catalog = new RetentionCatalogStore(databasePath);
        if (HasRawAggregate(connection, transaction, runId))
        {
            catalog.AssertAnalysisRunRawWritable(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
        }
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
        command.ExecuteNonQuery();
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (!HasRawAggregate(connection, transaction, runId, excludeResult: true))
        {
            catalog.RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        transaction.Commit();
    }

    public void FinishRun(long runId, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt)
    {
        if (status == MonitorAnalysisStatus.Succeeded)
        {
            CompleteRun(runId, message ?? string.Empty, completedAt);
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var run = ReadRetentionRun(connection, transaction, runId);
        var catalog = new RetentionCatalogStore(databasePath);
        var writesRawMessage = message is not null;
        if (writesRawMessage && HasRawAggregate(connection, transaction, runId))
        {
            catalog.AssertAnalysisRunRawWritable(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
        }
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
        command.ExecuteNonQuery();
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterSourceWrite);
        if (writesRawMessage && !HasRawAggregate(connection, transaction, runId, excludeError: true))
        {
            catalog.RegisterAnalysisRunRaw(connection, transaction, run.Id, run.RequestedAt, run.RawRecordId, run.SpanId, run.OwnerToken);
            writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.AfterCatalogRegistration);
        }
        writeFailureInjector?.Invoke(MonitorAnalysisStoreWritePhase.BeforeCommit);
        transaction.Commit();
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
            CompletedAt: reader.IsDBNull(8) ? null : reader.GetString(8),
            ResultMarkdown: reader.IsDBNull(9) ? null : reader.GetString(9),
            ErrorMessage: reader.IsDBNull(10) ? null : reader.GetString(10));
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
}

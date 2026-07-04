using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

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

    public SqliteMonitorAnalysisStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public void CreateSchema()
    {
        EnsureParentDirectory();
        new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema();
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
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
                error_message TEXT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_monitor_analysis_runs_trace_id ON monitor_analysis_runs(trace_id);");
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_analysis_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_analysis_safe_summaries (
                run_id INTEGER PRIMARY KEY,
                markdown TEXT NOT NULL,
                generated_at TEXT NOT NULL
            );
            """);
    }

    public MonitorAnalysisStartResult StartRun(
        string traceId,
        long? rawRecordId,
        string? spanId,
        MonitorAnalysisFocus focus,
        DateTimeOffset requestedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO monitor_analysis_runs (
                trace_id, raw_record_id, span_id, focus, status, requested_at
            ) VALUES (
                $trace_id, $raw_record_id, $span_id, $focus, $status, $requested_at
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$trace_id", traceId);
        AddParameter(command, "$raw_record_id", rawRecordId);
        AddParameter(command, "$span_id", string.IsNullOrWhiteSpace(spanId) ? null : spanId);
        AddParameter(command, "$focus", focus.ToWireValue());
        AddParameter(command, "$status", MonitorAnalysisStatus.Queued.ToWireValue());
        AddParameter(command, "$requested_at", FormatTimestamp(requestedAt));
        var runId = (long)(long)command.ExecuteScalar()!;
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
        using var command = connection.CreateCommand();
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
    }

    public void CompleteRun(long runId, string resultMarkdown, DateTimeOffset completedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
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
    }

    public void FinishRun(long runId, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt)
    {
        if (status == MonitorAnalysisStatus.Succeeded)
        {
            CompleteRun(runId, message ?? string.Empty, completedAt);
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
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
        return connection;
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

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

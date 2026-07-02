using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorProjectionStoreTests
{
    private const string Source = "raw-otlp";

    [Fact]
    public void ListUnprocessed_ReturnsUnprojectedRowsInIdOrderBoundedByLimit()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id1 = store.Insert(Raw("t1", T(1)));
        var id2 = store.Insert(Raw("t2", T(2)));
        var id3 = store.Insert(Raw("t3", T(3)));

        var firstTwo = store.ListUnprocessedForProjection(2);
        Assert.Equal(new[] { id1, id2 }, firstTwo.Select(r => r.Id!.Value));

        store.ApplyProjection(id1, Source, T(1), Projection("t1", 1, Trace("t1")), T(10));

        var remaining = store.ListUnprocessedForProjection(10);
        Assert.Equal(new[] { id2, id3 }, remaining.Select(r => r.Id!.Value));
    }

    [Fact]
    public void ApplyProjection_IsIdempotentPerRawRecord()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id = store.Insert(Raw("t1", T(1)));

        Assert.True(store.ApplyProjection(id, Source, T(1), Projection("t1", 2, Trace("t1", span: 2, tool: 1, error: 1)), T(10)));
        Assert.False(store.ApplyProjection(id, Source, T(1), Projection("t1", 2, Trace("t1", span: 2, tool: 1, error: 1)), T(11)));

        Assert.Single(store.ListMonitorIngestions(0, 100).Items);
        var trace = Assert.Single(store.ListMonitorTraces(0, 100).Items);
        Assert.Equal(2, trace.SpanCount);
        Assert.Equal(1, trace.ToolCallCount);
        Assert.Equal(1, trace.ErrorCount);
    }

    [Fact]
    public void ApplyProjection_SingleRecordFansOutToMultipleTraceRows()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id = store.Insert(Raw("t1", T(1)));

        store.ApplyProjection(id, Source, T(1), Projection("t1", 3, Trace("t1", span: 2), Trace("t2", span: 1)), T(10));

        var traces = store.ListMonitorTraces(0, 100).Items;
        Assert.Equal(2, traces.Count);

        // Re-applying the same multi-trace record must not double-count either trace.
        Assert.False(store.ApplyProjection(id, Source, T(1), Projection("t1", 3, Trace("t1", span: 2), Trace("t2", span: 1)), T(11)));
        Assert.Equal(2, store.ListMonitorTraces(0, 100).Items.Single(t => t.TraceId == "t1").SpanCount);
        Assert.Equal(1, store.ListMonitorTraces(0, 100).Items.Single(t => t.TraceId == "t2").SpanCount);
    }

    [Fact]
    public void ApplyProjection_AggregatesTraceAcrossRecords()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id1 = store.Insert(Raw("shared", T(1)));
        var id2 = store.Insert(Raw("shared", T(5)));

        store.ApplyProjection(id1, Source, T(1), Projection("shared", 2, Trace("shared", span: 2, tool: 1, error: 0)), T(10));
        store.ApplyProjection(id2, Source, T(5), Projection("shared", 3, Trace("shared", span: 3, tool: 2, error: 1)), T(11));

        var trace = Assert.Single(store.ListMonitorTraces(0, 100).Items);
        Assert.Equal(5, trace.SpanCount);
        Assert.Equal(3, trace.ToolCallCount);
        Assert.Equal(1, trace.ErrorCount);
        Assert.Equal(T(1), DateTimeOffset.Parse(trace.FirstSeenAt!));
        Assert.Equal(T(5), DateTimeOffset.Parse(trace.LastSeenAt!));
    }

    [Fact]
    public void ApplyProjection_FillsNullRepositoryMetadataAndPreservesExistingNonNullValues()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id1 = store.Insert(Raw("shared", T(1)));
        var id2 = store.Insert(Raw("shared", T(2)));
        var id3 = store.Insert(Raw("shared", T(3)));

        store.ApplyProjection(id1, Source, T(1), Projection("shared", 1, Trace("shared", repository: null, workspace: null, snapshot: null)), T(10));
        store.ApplyProjection(id2, Source, T(2), Projection("shared", 1, Trace("shared", repository: "repo-a", workspace: "workspace-a", snapshot: "snapshot-a")), T(11));
        store.ApplyProjection(id3, Source, T(3), Projection("shared", 1, Trace("shared", repository: "repo-b", workspace: "workspace-b", snapshot: "snapshot-b")), T(12));

        var trace = Assert.Single(store.ListMonitorTraces(0, 100).Items);
        Assert.Equal("repo-a", trace.RepositoryName);
        Assert.Equal("workspace-a", trace.WorkspaceLabel);
        Assert.Equal("snapshot-a", trace.RepoSnapshot);
    }

    [Fact]
    public void CreateMonitorSchema_UpgradesExistingV2RowsWithoutBackfillingRepositoryMetadata()
    {
        using var temp = new MonitorTempDirectory();
        BuildV2DatabaseWithoutRepositoryMetadata(temp.DatabasePath);

        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();

        Assert.Equal(3, RawTelemetryStore.MonitorSchemaVersion);
        var trace = Assert.Single(store.ListMonitorTraces(0, 100).Items);
        Assert.Equal("legacy-trace", trace.TraceId);
        Assert.Null(trace.RepositoryName);
        Assert.Null(trace.WorkspaceLabel);
        Assert.Null(trace.RepoSnapshot);

        using var connection = OpenConnection(temp.DatabasePath);
        var columns = ReadColumns(connection, "monitor_traces");
        Assert.Contains("repository_name", columns);
        Assert.Contains("workspace_label", columns);
        Assert.Contains("repo_snapshot", columns);
    }

    [Fact]
    public void ApplyProjection_NoTraceId_ProjectsIngestionOnlyAndDoesNotStall()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id = store.Insert(Raw(traceId: null, T(1)));

        Assert.True(store.ApplyProjection(id, Source, T(1), Projection(traceId: null, spanCount: 1), T(10)));

        var ingestion = Assert.Single(store.ListMonitorIngestions(0, 100).Items);
        Assert.Null(ingestion.TraceId);
        Assert.Empty(store.ListMonitorTraces(0, 100).Items);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
    }

    [Fact]
    public void GetProjectionStatus_ReportsBacklogAndOldestUnprocessed()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id1 = store.Insert(Raw("t1", T(1)));
        store.Insert(Raw("t2", T(2)));
        store.Insert(Raw("t3", T(3)));

        store.ApplyProjection(id1, Source, T(1), Projection("t1", 1, Trace("t1")), T(10));

        var status = store.GetProjectionStatus();
        Assert.Equal(2, status.Backlog);
        Assert.Equal(T(2), status.OldestUnprocessedReceivedAt);

        foreach (var record in store.ListUnprocessedForProjection(100))
        {
            store.ApplyProjection(record.Id!.Value, record.Source, record.ReceivedAt, Projection(record.TraceId, 1, Trace(record.TraceId!)), T(20));
        }

        var caughtUp = store.GetProjectionStatus();
        Assert.Equal(0, caughtUp.Backlog);
        Assert.Null(caughtUp.OldestUnprocessedReceivedAt);
    }

    [Fact]
    public void ListMonitorIngestions_PaginatesByRawRecordIdWithTerminalCursor()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var ids = new[]
        {
            store.Insert(Raw("t1", T(1))),
            store.Insert(Raw("t2", T(2))),
            store.Insert(Raw("t3", T(3))),
        };

        // Project in reverse raw order so monitor_ingestions.id diverges from raw_record_id.
        store.ApplyProjection(ids[2], Source, T(3), Projection("t3", 1, Trace("t3")), T(10));
        store.ApplyProjection(ids[0], Source, T(1), Projection("t1", 1, Trace("t1")), T(11));
        store.ApplyProjection(ids[1], Source, T(2), Projection("t2", 1, Trace("t2")), T(12));

        var page1 = store.ListMonitorIngestions(0, 2);
        Assert.True(page1.HasMore);
        Assert.Equal(new[] { ids[0], ids[1] }, page1.Items.Select(i => i.RawRecordId));

        var page2 = store.ListMonitorIngestions(page1.Items[^1].RawRecordId, 2);
        Assert.False(page2.HasMore);
        Assert.Equal(new[] { ids[2] }, page2.Items.Select(i => i.RawRecordId));

        // Exact-multiple terminal: exactly limit rows ⇒ HasMore false (no extra empty fetch).
        var exact = store.ListMonitorIngestions(0, 3);
        Assert.False(exact.HasMore);
        Assert.Equal(3, exact.Items.Count);
    }

    [Fact]
    public void GetRawRecordById_ReturnsRecordOrNull()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);
        var id = store.Insert(Raw("t1", T(1)));

        var found = store.GetRawRecordById(id);
        Assert.NotNull(found);
        Assert.Equal("t1", found!.TraceId);

        Assert.Null(store.GetRawRecordById(999_999));
    }

    [Fact]
    public void ProjectionRowDtos_ExposeOnlyAllowlistMembers()
    {
        var ingestion = typeof(MonitorIngestionRow).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "RawRecordId", "ReceivedAt", "Source", "TraceId", "ClientKind", "SpanCount", "ProjectedAt" },
            ingestion);

        var trace = typeof(MonitorTraceRow).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "Id", "TraceId", "ClientKind", "ExperimentId", "TaskId", "TaskCategory", "AgentVariant",
                "PromptVersion", "SpanCount", "ToolCallCount", "ErrorCount", "FirstSeenAt", "LastSeenAt", "ProjectedAt",
                "InputTokens", "OutputTokens", "TotalTokens", "TurnCount", "AgentInvocationCount", "DurationMs", "PrimaryModel",
                "RepositoryName", "WorkspaceLabel", "RepoSnapshot",
            },
            trace);

        // Defensive: no payload / resource / PII member ever leaks into a read DTO.
        var all = ingestion.Concat(trace);
        Assert.DoesNotContain(all, n =>
            n.Contains("payload", StringComparison.OrdinalIgnoreCase)
            || n.Contains("resource", StringComparison.OrdinalIgnoreCase)
            || n.Contains("user", StringComparison.OrdinalIgnoreCase)
            || n.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    private static RawTelemetryStore NewStore(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        return store;
    }

    private static DateTimeOffset T(int minutes) => DateTimeOffset.UnixEpoch.AddMinutes(minutes);

    private static RawTelemetryRecord Raw(string? traceId, DateTimeOffset receivedAt) =>
        new(
            Id: null,
            Source: Source,
            TraceId: traceId,
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: "{}");

    private static MonitorRecordProjection Projection(string? traceId, int spanCount, params MonitorTraceContribution[] traces) =>
        new(TraceId: traceId, ClientKind: "vscode-copilot-chat", SpanCount: spanCount, TraceContributions: traces);

    private static MonitorTraceContribution Trace(
        string traceId,
        int span = 1,
        int tool = 0,
        int error = 0,
        string? repository = "repo",
        string? workspace = "workspace",
        string? snapshot = "snapshot") =>
        new(
            TraceId: traceId,
            ClientKind: "vscode-copilot-chat",
            ExperimentId: "exp",
            TaskId: "task",
            TaskCategory: "cat",
            AgentVariant: "v",
            PromptVersion: "p",
            SpanCount: span,
            ToolCallCount: tool,
            ErrorCount: error,
            RepositoryName: repository,
            WorkspaceLabel: workspace,
            RepoSnapshot: snapshot);

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", tableName);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static void BuildV2DatabaseWithoutRepositoryMetadata(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenConnection(path);
        Execute(connection, """
            CREATE TABLE raw_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                trace_id TEXT NULL,
                received_at TEXT NOT NULL,
                resource_attributes_json TEXT NULL,
                payload_json TEXT NOT NULL,
                schema_version INTEGER NOT NULL
            );
            """);
        Execute(connection, """
            CREATE TABLE schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """);
        Execute(connection, "INSERT INTO schema_version (component, version) VALUES ('monitor', 2);");
        Execute(connection, """
            CREATE TABLE monitor_ingestions (
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
        Execute(connection, """
            CREATE TABLE monitor_traces (
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
        Execute(connection, """
            INSERT INTO monitor_traces
                (trace_id, client_kind, span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at)
            VALUES ('legacy-trace', 'vscode-copilot-chat', 1, 0, 0,
                '1970-01-01T00:01:00.0000000+00:00',
                '1970-01-01T00:01:00.0000000+00:00',
                '1970-01-01T00:05:00.0000000+00:00');
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

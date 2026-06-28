using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Store-level tests for span projection (M3): schema migration, idempotency,
/// backfill of v1 DBs, rollup column updates, and backlog tracking.
/// </summary>
public class MonitorSpanProjectionStoreTests
{
    private const string Source = "raw-otlp";

    // ---- Schema Upgrade (v1 -> v2) ----------------------------------------

    [Fact]
    public void UpgradeFromV1_BackfillsSpansAndRollup()
    {
        using var temp = new MonitorTempDirectory();

        // Build a v1 DB manually (no span_projected_at, no rollup cols, no monitor_spans).
        BuildV1Database(temp.DatabasePath, out var rawRecordId, out var traceId, MultiSpanPayload(traceId: "upg-1"));

        // Open a v2 store on the same DB — this runs the migration.
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();

        // Verify schema_version = 2.
        Assert.Equal(2, RawTelemetryStore.MonitorSchemaVersion);

        // Now run span projection directly (simulate worker phase 2).
        var records = store.ListUnprocessedForSpanProjection(100);
        Assert.Single(records);
        Assert.Equal(rawRecordId, records[0].Id!.Value);

        var spans = MonitorSpanProjectionBuilder.Build(records[0]);
        Assert.True(spans.Count > 0);

        var result = store.ApplySpanProjection(rawRecordId, spans, DateTimeOffset.UtcNow);
        Assert.True(result);

        // Spans are in the DB.
        var storedSpans = store.GetSpansForTrace(traceId);
        Assert.Equal(spans.Count, storedSpans.Count);

        // Rollup columns match ComputeRollup.
        var rollup = store.GetTraceRollup(traceId);
        Assert.NotNull(rollup);
        var expected = MonitorTraceRollupBuilder.ComputeRollup(spans);
        Assert.Equal(expected.InputTokens, rollup!.InputTokens);
        Assert.Equal(expected.OutputTokens, rollup.OutputTokens);
        Assert.Equal(expected.TotalTokens, rollup.TotalTokens);
        Assert.Equal(expected.TurnCount, rollup.TurnCount);
        Assert.Equal(expected.AgentInvocationCount, rollup.AgentInvocationCount);
        Assert.Equal(expected.DurationMs, rollup.DurationMs);
        Assert.Equal(expected.PrimaryModel, rollup.PrimaryModel);

        // Backlog is 0 after projection.
        var status = store.GetSpanProjectionStatus();
        Assert.Equal(0, status.Backlog);
        Assert.Null(status.OldestUnprocessedReceivedAt);
    }

    // ---- Independent progress -----------------------------------------------

    [Fact]
    public void GetSpanProjectionStatus_ReflectsBacklogBeforeProjection()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var id1 = store.Insert(Raw("t1", T(1)));
        // Ingestion row must exist for span projection to be eligible.
        store.ApplyProjection(id1, Source, T(1), MakeProjection("t1"), T(5));

        var status = store.GetSpanProjectionStatus();
        Assert.Equal(1, status.Backlog);
        Assert.Equal(T(1), status.OldestUnprocessedReceivedAt);

        var records = store.ListUnprocessedForSpanProjection(100);
        Assert.Equal(new[] { id1 }, records.Select(r => r.Id!.Value));
    }

    [Fact]
    public void ApplySpanProjection_EmptySpanList_StillDrainsBacklog()
    {
        // A record with a payload that yields 0 spans (e.g. empty resourceSpans)
        // must still get span_projected_at stamped so backlog drains to 0.
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var id = store.Insert(Raw("t1", T(1)));
        store.ApplyProjection(id, Source, T(1), MakeProjection("t1"), T(5));

        Assert.Equal(1, store.GetSpanProjectionStatus().Backlog);

        var result = store.ApplySpanProjection(id, [], DateTimeOffset.UtcNow);
        Assert.True(result);

        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
    }

    [Fact]
    public void ApplySpanProjection_TraceLessSpan_DropsSpanAndDrainsBacklog()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"spanId":"missing-trace","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"","spanId":"blank-trace","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000500000000","endTimeUnixNano":"1710000001500000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"valid-trace","spanId":"valid","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]}
        ]}]}]}
        """;
        var record = new RawTelemetryRecord(
            Id: null,
            Source: Source,
            TraceId: "valid-trace",
            ReceivedAt: T(1),
            ResourceAttributesJson: null,
            PayloadJson: payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, Source, T(1), MakeProjection("valid-trace", spanCount: 2), T(5));

        var spans = MonitorSpanProjectionBuilder.Build(record with { Id = id });
        Assert.Contains(spans, span => span.TraceId is null);

        var result = store.ApplySpanProjection(id, spans, T(10));

        Assert.True(result);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
        var stored = store.GetSpansForTrace("valid-trace");
        var span = Assert.Single(stored);
        Assert.Equal("valid", span.SpanId);
        Assert.Empty(store.GetSpansForTrace(string.Empty));
    }

    [Fact]
    public void ListUnprocessedForSpanProjection_DoesNotReturnRecordsWithoutIngestionRow()
    {
        // raw_records rows that have not been trace-projected (no ingestion row) must
        // not appear in the span projection queue.
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        store.Insert(Raw("t1", T(1))); // No ingestion row.

        var records = store.ListUnprocessedForSpanProjection(100);
        Assert.Empty(records);
    }

    // ---- Idempotency --------------------------------------------------------

    [Fact]
    public void ApplySpanProjection_IsIdempotent_SecondCallReturnsFalse()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var id = store.Insert(Raw("t1", T(1)));
        store.ApplyProjection(id, Source, T(1), MakeProjection("t1"), T(5));

        var spans = MonitorSpanProjectionBuilder.Build(Raw("t1", T(1)));

        Assert.True(store.ApplySpanProjection(id, spans, T(10)));
        Assert.False(store.ApplySpanProjection(id, spans, T(11)));

        // Span count must not change on the second call.
        var stored = store.GetSpansForTrace("t1");
        Assert.Equal(spans.Count, stored.Count);
    }

    [Fact]
    public void ApplySpanProjection_ReturnsFalse_WhenNoIngestionRow()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var id = store.Insert(Raw("t1", T(1)));
        // Intentionally do NOT call ApplyProjection — no ingestion row.

        var result = store.ApplySpanProjection(id, [], T(10));
        Assert.False(result);
    }

    [Fact]
    public void ApplySpanProjection_DuplicateOrMissingSpanId_DoesNotThrow_OrdinalDistinguishes()
    {
        // Payload with two spans that have the same (or null) span_id — ordinal is the key.
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var id = store.Insert(Raw("t1", T(1)));
        store.ApplyProjection(id, Source, T(1), MakeProjection("t1"), T(5));

        var spans = new List<MonitorSpanProjection>
        {
            new(TraceId: "t1", SpanId: null, ParentSpanId: null, SpanOrdinal: 0,
                Operation: "chat", Category: "llm_call",
                ToolName: null, ToolType: null, McpToolName: null, McpServerHash: null,
                AgentName: null, RequestModel: "gpt-4o", ResponseModel: "gpt-4o",
                InputTokens: 100, OutputTokens: 50, TotalTokens: 150,
                ReasoningTokens: null, CacheReadTokens: null, CacheCreationTokens: null,
                Status: "ok", ErrorType: null, FinishReasons: "stop",
                ConversationId: null, DurationMs: 1000.0,
                StartTime: "2024-03-10T00:00:00.000+00:00",
                EndTime: "2024-03-10T00:00:01.000+00:00"),
            new(TraceId: "t1", SpanId: null, ParentSpanId: null, SpanOrdinal: 1,
                Operation: "chat", Category: "llm_call",
                ToolName: null, ToolType: null, McpToolName: null, McpServerHash: null,
                AgentName: null, RequestModel: "gpt-4o", ResponseModel: "gpt-4o",
                InputTokens: 200, OutputTokens: 80, TotalTokens: 280,
                ReasoningTokens: null, CacheReadTokens: null, CacheCreationTokens: null,
                Status: "ok", ErrorType: null, FinishReasons: "stop",
                ConversationId: null, DurationMs: 500.0,
                StartTime: "2024-03-10T00:00:01.000+00:00",
                EndTime: "2024-03-10T00:00:01.500+00:00"),
        };

        var result = store.ApplySpanProjection(id, spans, T(10));
        Assert.True(result);

        var stored = store.GetSpansForTrace("t1");
        Assert.Equal(2, stored.Count);
    }

    // ---- Rollup correctness -------------------------------------------------

    [Fact]
    public void ApplySpanProjection_UpdatesTraceRollupColumns()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var payload = ChatAndToolPayload(traceId: "roll-t1");
        var id = store.Insert(new RawTelemetryRecord(
            Id: null, Source: Source, TraceId: "roll-t1",
            ReceivedAt: T(1), ResourceAttributesJson: null, PayloadJson: payload));
        store.ApplyProjection(id, Source, T(1), MakeProjection("roll-t1"), T(5));

        var spans = MonitorSpanProjectionBuilder.Build(
            new RawTelemetryRecord(Id: id, Source: Source, TraceId: "roll-t1",
                ReceivedAt: T(1), ResourceAttributesJson: null, PayloadJson: payload));

        store.ApplySpanProjection(id, spans, T(10));

        var rollup = store.GetTraceRollup("roll-t1");
        Assert.NotNull(rollup);
        var expected = MonitorTraceRollupBuilder.ComputeRollup(spans);
        Assert.Equal(expected.InputTokens, rollup!.InputTokens);
        Assert.Equal(expected.OutputTokens, rollup.OutputTokens);
        Assert.Equal(expected.TurnCount, rollup.TurnCount);
        Assert.Equal(expected.PrimaryModel, rollup.PrimaryModel);
    }

    [Fact]
    public void ApplySpanProjection_ChildBeforeRoot_StoresRootRollup()
    {
        using var temp = new MonitorTempDirectory();
        var store = NewStore(temp);

        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-store","spanId":"child","parentSpanId":"root","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"roll-store","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;
        var record = new RawTelemetryRecord(
            Id: null,
            Source: Source,
            TraceId: "roll-store",
            ReceivedAt: T(1),
            ResourceAttributesJson: null,
            PayloadJson: payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, Source, T(1), MakeProjection("roll-store", spanCount: 2), T(5));
        var spans = MonitorSpanProjectionBuilder.Build(record with { Id = id });

        store.ApplySpanProjection(id, spans, T(10));

        var rollup = store.GetTraceRollup("roll-store");
        Assert.NotNull(rollup);
        Assert.Equal(100, rollup!.InputTokens);
        Assert.Equal(50, rollup.OutputTokens);
        Assert.Equal(150, rollup.TotalTokens);
    }

    // ---- Helpers ------------------------------------------------------------

    private static RawTelemetryStore NewStore(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        return store;
    }

    private static DateTimeOffset T(int minutes) => DateTimeOffset.UnixEpoch.AddMinutes(minutes);

    private static RawTelemetryRecord Raw(string? traceId, DateTimeOffset receivedAt) =>
        new(Id: null, Source: Source, TraceId: traceId,
            ReceivedAt: receivedAt, ResourceAttributesJson: null, PayloadJson: "{}");

    private static MonitorRecordProjection MakeProjection(string? traceId, int spanCount = 1) =>
        new(TraceId: traceId, ClientKind: "vscode-copilot-chat", SpanCount: spanCount,
            TraceContributions:
            [
                new MonitorTraceContribution(
                    TraceId: traceId!,
                    ClientKind: "vscode-copilot-chat",
                    ExperimentId: null, TaskId: null, TaskCategory: null,
                    AgentVariant: null, PromptVersion: null,
                    SpanCount: spanCount, ToolCallCount: 0, ErrorCount: 0),
            ]);

    /// <summary>
    /// A synthetic multi-span payload: invoke_agent + chat + execute_tool + error chat +
    /// invoke_agent sub-agent + MCP tool call. Used for the upgrade test.
    /// </summary>
    private static string MultiSpanPayload(string traceId) =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"4000","parentSpanId":"1000","name":"execute_tool mcp",
           "startTimeUnixNano":"1710000003000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"mcp_list_files"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"extension"}},
             {"key":"github.copilot.tool.parameters.mcp_tool_name","value":{"stringValue":"list_files"}},
             {"key":"github.copilot.tool.parameters.mcp_server_name_hash","value":{"stringValue":"abcdef123456"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"5000","parentSpanId":"1000","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000004000000000","endTimeUnixNano":"1710000007000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"600"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"300"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"6000","parentSpanId":"5000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000005000000000","endTimeUnixNano":"1710000006000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"timeout"}}
           ]}
        ]}]}]}
        """.Replace("TRACE_ID", traceId);

    /// <summary>A simple chat + tool span payload for rollup tests.</summary>
    private static string ChatAndToolPayload(string traceId) =>
        """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
           ]},
          {"traceId":"TRACE_ID","spanId":"2000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"write_file"}}
           ]}
        ]}]}]}
        """.Replace("TRACE_ID", traceId);

    /// <summary>
    /// Builds a v1 (Sprint8) SQLite database without <c>span_projected_at</c>,
    /// without rollup columns on <c>monitor_traces</c>, and without
    /// <c>monitor_spans</c>. Inserts a raw record and a matching ingestion row.
    /// </summary>
    private static void BuildV1Database(string path, out long rawRecordId, out string traceId, string payload)
    {
        traceId = "upg-1";

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        // raw_records table (same as v1/v2).
        Execute(conn, """
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

        // monitor_ingestions WITHOUT span_projected_at (v1).
        Execute(conn, """
            CREATE TABLE monitor_ingestions (
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

        // monitor_traces WITHOUT rollup columns (v1).
        Execute(conn, """
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
                projected_at TEXT NOT NULL
            );
            """);

        // schema_version (monitor = 1).
        Execute(conn, """
            CREATE TABLE schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """);
        Execute(conn, "INSERT INTO schema_version (component, version) VALUES ('monitor', 1);");

        // Insert a raw record.
        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO raw_records (source, trace_id, received_at, payload_json, schema_version)
            VALUES ('raw-otlp', $tid, '1970-01-01T00:01:00.0000000+00:00', $payload, 1);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$tid", traceId);
        insert.Parameters.AddWithValue("$payload", payload);
        rawRecordId = (long)insert.ExecuteScalar()!;

        // Insert a corresponding monitor_ingestions row (already trace-projected).
        using var ingestion = conn.CreateCommand();
        ingestion.CommandText = """
            INSERT INTO monitor_ingestions
                (raw_record_id, received_at, source, trace_id, client_kind, span_count, projected_at)
            VALUES ($rid, '1970-01-01T00:01:00.0000000+00:00', 'raw-otlp', $tid, 'vscode-copilot-chat', 6, '1970-01-01T00:05:00.0000000+00:00');
            """;
        ingestion.Parameters.AddWithValue("$rid", rawRecordId);
        ingestion.Parameters.AddWithValue("$tid", traceId);
        ingestion.ExecuteNonQuery();

        // Insert a monitor_traces row for the trace (no rollup columns yet in v1).
        using var traceRow = conn.CreateCommand();
        traceRow.CommandText = """
            INSERT INTO monitor_traces
                (trace_id, client_kind, span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at)
            VALUES ($tid, 'vscode-copilot-chat', 6, 2, 1,
                '1970-01-01T00:01:00.0000000+00:00',
                '1970-01-01T00:01:00.0000000+00:00',
                '1970-01-01T00:05:00.0000000+00:00');
            """;
        traceRow.Parameters.AddWithValue("$tid", traceId);
        traceRow.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

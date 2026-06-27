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

    private static MonitorTraceContribution Trace(string traceId, int span = 1, int tool = 0, int error = 0) =>
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
            ErrorCount: error);
}

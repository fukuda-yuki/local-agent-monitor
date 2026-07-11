using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionOtelEnrichmentTests
{
    [Fact]
    public void ProcessNextBatch_UsesOnlyExactLinksAndCreatesUnboundForUnmatchedRows()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var events = new[]
        {
            Event(sessionId, "start", "SessionStart", now),
            Event(sessionId, "prompt", "UserPromptSubmit", now.AddSeconds(1)),
            Event(sessionId, "end", "SessionEnd", now.AddSeconds(2)),
            Event(sessionId, "trace-link", "tool.execution_complete", now.AddSeconds(2)) with { TraceId = "trace-by-context" },
        };
        store.Write(new(
            new(
                new(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich, "same-repo", null, now, now.AddSeconds(2), now.AddSeconds(2), SessionRawRetentionState.NotCaptured, now, now),
                [new(sessionId, SessionSourceSurface.HookUnknown, "native-exact", SessionBindingKind.Native, now)],
                [],
                events),
            []));
        InsertProjectedSpan(temp.DatabasePath, "trace-exact", "span-1", "native-exact", "vscode-copilot-chat", "same-repo", now.AddSeconds(3));
        InsertProjectedSpan(temp.DatabasePath, "trace-unmatched", "span-2", null, "vscode-copilot-chat", "same-repo", now.AddSeconds(4));
        InsertProjectedSpan(temp.DatabasePath, "trace-by-context", "span-3", null, "unrecognized-client", "same-repo", now.AddSeconds(5));

        var processor = new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System);
        var processed = processor.ProcessNextBatch(100);

        Assert.Equal(3, processed);
        var confirmed = store.Resolve(SessionSourceSurface.VisualStudioCode, "native-exact");
        Assert.NotNull(confirmed);
        Assert.Equal(sessionId, confirmed.SessionId);
        Assert.Equal(SessionCompleteness.Full, confirmed.Completeness);
        var detail = store.GetDetail(sessionId)!;
        Assert.Contains(detail.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "trace-exact/span-1");
        Assert.Contains(detail.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "trace-by-context/span-3");

        var sessions = store.ListMostRecent(10);
        var unbound = Assert.Single(sessions, item => item.SessionId != sessionId);
        Assert.Equal(SessionCompleteness.Unbound, unbound.Completeness);
        Assert.Equal("same-repo", unbound.Repository);
        Assert.Null(store.Resolve(SessionSourceSurface.VisualStudioCode, "trace-unmatched"));
        Assert.Equal(3, store.GetProjectionState("session-otel-enrichment")!.ProjectionCursor);
    }

    private static ObservedSessionEvent Event(Guid sessionId, string sourceId, string type, DateTimeOffset occurredAt) =>
        new(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.HookUnknown, null, null, null, "copilot-compatible-hook", sourceId, type, occurredAt, SessionContentState.NotCaptured);

    private static void InsertProjectedSpan(string databasePath, string traceId, string spanId, string? conversationId, string clientKind, string repository, DateTimeOffset time)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var trace = connection.CreateCommand();
        trace.Transaction = transaction;
        trace.CommandText = """
            INSERT INTO monitor_traces(trace_id,client_kind,first_seen_at,last_seen_at,span_count,projected_at,repository_name)
            VALUES($trace_id,$client_kind,$time,$time,1,$time,$repository);
            """;
        trace.Parameters.AddWithValue("$trace_id", traceId);
        trace.Parameters.AddWithValue("$client_kind", clientKind);
        trace.Parameters.AddWithValue("$time", time.ToString("O"));
        trace.Parameters.AddWithValue("$repository", repository);
        trace.ExecuteNonQuery();
        using var span = connection.CreateCommand();
        span.Transaction = transaction;
        span.CommandText = """
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,conversation_id,start_time,projected_at)
            VALUES($raw_record_id,$trace_id,$span_id,0,$conversation_id,$time,$time);
            """;
        span.Parameters.AddWithValue("$trace_id", traceId);
        span.Parameters.AddWithValue("$span_id", spanId);
        span.Parameters.AddWithValue("$raw_record_id", int.Parse(spanId.AsSpan(spanId.Length - 1), System.Globalization.CultureInfo.InvariantCulture));
        span.Parameters.AddWithValue("$conversation_id", (object?)conversationId ?? DBNull.Value);
        span.Parameters.AddWithValue("$time", time.ToString("O"));
        span.ExecuteNonQuery();
        transaction.Commit();
    }
}

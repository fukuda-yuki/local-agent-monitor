using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SqliteSessionOtelEnricher
{
    public const string ProjectorKey = "session-otel-enrichment";
    private readonly string databasePath;
    private readonly ISessionStore store;
    private readonly TimeProvider timeProvider;

    public SqliteSessionOtelEnricher(string databasePath, ISessionStore store, TimeProvider? timeProvider = null)
    {
        this.databasePath = databasePath;
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ProcessNextBatch(int limit = 100)
    {
        var state = store.GetProjectionState(ProjectorKey);
        var rows = ReadRows(state?.ProjectionCursor ?? 0, limit);
        foreach (var row in rows)
        {
            Process(row);
            store.UpsertProjectionState(new(ProjectorKey, row.Id, state?.UnsupportedEventVersionCount ?? 0, timeProvider.GetUtcNow()));
        }
        return rows.Count;
    }

    public long CountBacklog()
    {
        var cursor = store.GetProjectionState(ProjectorKey)?.ProjectionCursor ?? 0;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_spans WHERE id > $cursor;";
        command.Parameters.AddWithValue("$cursor", cursor);
        return (long)command.ExecuteScalar()!;
    }

    private void Process(ProjectedSpan row)
    {
        var traceSessionId = FindSessionByTraceId(row.TraceId);
        var nativeSessionId = string.IsNullOrEmpty(row.ConversationId) ? null : FindUnambiguousSessionByNativeId(row.ConversationId);
        var sessionId = traceSessionId ?? nativeSessionId ?? Guid.CreateVersion7();
        var existing = store.GetDetail(sessionId);
        var confirmedSurface = ConfirmSurface(row.ClientKind);
        var eventId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var occurredAt = row.StartTime ?? row.ProjectedAt;

        var nativeIds = new List<SessionNativeId>();
        if (nativeSessionId == sessionId && confirmedSurface is not null && row.ConversationId is not null
            && existing!.NativeIds.All(item => item.SourceSurface != confirmedSurface.Value || !string.Equals(item.NativeSessionId, row.ConversationId, StringComparison.Ordinal)))
        {
            nativeIds.Add(new(sessionId, confirmedSurface.Value, row.ConversationId, SessionBindingKind.Native, occurredAt));
        }

        var existingTypes = existing?.Events ?? [];
        var hasNative = existing?.NativeIds.Count > 0 || nativeIds.Count > 0;
        var hasStart = existingTypes.Any(item => item.Type is "session.start" or "SessionStart");
        var hasInstruction = existingTypes.Any(item => item.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted");
        var hasTerminal = existingTypes.Any(item => item.Type is "session.shutdown" or "session.task_complete" or "SessionEnd" or "Stop");
        var hasGap = existingTypes.Any(item => item.Type == "capture.started" && item.Status == "gap_before_capture");
        var unsupported = existingTypes.Any(item => item.ContentState == SessionContentState.Unsupported);
        var completeness = SessionCompletenessCalculator.Calculate(new(
            hasNative, hasStart, hasInstruction, true, hasTerminal, true,
            hasStart && hasInstruction && hasTerminal, unsupported, hasGap));
        var now = timeProvider.GetUtcNow();
        var session = existing?.Session is { } current
            ? current with
            {
                Completeness = completeness,
                Repository = current.Repository ?? row.Repository,
                Workspace = current.Workspace ?? row.Workspace,
                LastSeenAt = current.LastSeenAt > occurredAt ? current.LastSeenAt : occurredAt,
                UpdatedAt = now,
            }
            : new ObservedSession(
                sessionId, ObservedSessionStatus.Unknown, SessionCompleteness.Unbound,
                row.Repository, row.Workspace, null, null, occurredAt,
                SessionRawRetentionState.NotCaptured, now, now);
        var run = new ObservedSessionRun(
            runId, sessionId, confirmedSurface, null, row.TraceId, null, null,
            ObservedSessionStatus.Unknown, occurredAt, null, null, null, null);
        var @event = new ObservedSessionEvent(
            eventId, sessionId, runId, confirmedSurface, null, row.TraceId, null,
            "otel-exact", $"{row.TraceId}/{row.SpanId}", "otel.span", occurredAt, SessionContentState.NotCaptured);
        store.Write(new(new(session, nativeIds, [run], [@event]), []));
    }

    private IReadOnlyList<ProjectedSpan> ReadRows(long after, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.trace_id,COALESCE(s.span_id,''),s.conversation_id,t.client_kind,
                   t.repository_name,t.workspace_label,s.start_time,s.projected_at
            FROM monitor_spans s JOIN monitor_traces t ON t.trace_id=s.trace_id
            WHERE s.id > $after ORDER BY s.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<ProjectedSpan>();
        while (reader.Read())
        {
            rows.Add(new(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), Nullable(reader, 3), Nullable(reader, 4),
                Nullable(reader, 5), Nullable(reader, 6), Timestamp(reader, 7), DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return rows;
    }

    private Guid? FindSessionByTraceId(string traceId) => FindUnambiguous("SELECT DISTINCT session_id FROM session_events WHERE trace_id=$value COLLATE BINARY;", traceId);
    private Guid? FindUnambiguousSessionByNativeId(string nativeId) => FindUnambiguous("SELECT DISTINCT session_id FROM session_native_ids WHERE native_session_id=$value COLLATE BINARY;", nativeId);

    private Guid? FindUnambiguous(string sql, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        Guid? result = null;
        while (reader.Read())
        {
            var current = Guid.Parse(reader.GetString(0));
            if (result is not null && result != current) return null;
            result = current;
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SessionSourceSurface? ConfirmSurface(string? clientKind) => clientKind switch
    {
        "vscode-copilot-chat" => SessionSourceSurface.VisualStudioCode,
        "copilot-cli" => SessionSourceSurface.CopilotCli,
        _ => null,
    };

    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? Timestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), null, System.Globalization.DateTimeStyles.RoundtripKind);
    private sealed record ProjectedSpan(long Id, string TraceId, string SpanId, string? ConversationId, string? ClientKind, string? Repository, string? Workspace, DateTimeOffset? StartTime, DateTimeOffset ProjectedAt);
}

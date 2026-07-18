using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SqliteSessionOtelEnricher
{
    public const string ProjectorKey = "session-otel-enrichment";
    private readonly string databasePath;
    private readonly ISessionStore store;
    private readonly ClaudeExactBindingRule claudeExactBindingRule;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? checkpoint;

    public SqliteSessionOtelEnricher(string databasePath, ISessionStore store, TimeProvider? timeProvider = null)
    {
        this.databasePath = databasePath;
        this.store = store;
        claudeExactBindingRule = new(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal SqliteSessionOtelEnricher(
        string databasePath,
        ISessionStore store,
        TimeProvider timeProvider,
        Action<string> checkpoint)
        : this(databasePath, store, timeProvider)
    {
        this.checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public int ProcessNextBatch(int limit = 100)
    {
        var state = store.GetProjectionState(ProjectorKey);
        var rows = ReadRows(state?.ProjectionCursor ?? 0, limit);
        foreach (var row in rows)
        {
            if (row.IsClaudeCode)
            {
                ProcessClaude(row);
            }
            else
            {
                Process(row);
            }
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
        var claudeBinding = row.PayloadJson is null
            ? null
            : claudeExactBindingRule.Resolve(row.PayloadJson, row.TraceId, row.SpanId);
        var traceSessionId = FindSessionByTraceId(row.TraceId);
        var conversationSessionId = string.IsNullOrEmpty(row.ConversationId) ? null : FindUnambiguousSessionByNativeId(row.ConversationId);
        var sessionId = claudeBinding?.SessionId ?? traceSessionId ?? conversationSessionId ?? Guid.CreateVersion7();
        var matchKind = claudeBinding is not null
            ? MatchKind(claudeBinding.BindingKind)
            : traceSessionId == sessionId
                ? SessionMatchKind.TraceContinuity
                : conversationSessionId == sessionId
                    ? SessionMatchKind.ConversationId
                    : SessionMatchKind.None;
        var existing = store.GetDetail(sessionId);
        var confirmedSurface = ConfirmSurface(row.ClientKind);
        var eventId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var occurredAt = row.StartTime ?? row.ProjectedAt;

        var nativeIds = new List<SessionNativeId>();
        if (conversationSessionId == sessionId && confirmedSurface is not null && row.ConversationId is not null
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
            "otel-exact", $"{row.TraceId}/{row.SpanId}", "otel.span", occurredAt, SessionContentState.NotCaptured,
            MatchKind: matchKind);
        store.Write(new(new(session, nativeIds, [run], [@event]), []));
    }

    // Issue #108 / D058: the exact native-session-ID resolver binds on its own
    // session.id evidence alone. It must not require claude-code-otel adapter
    // promotion (gated only by ProjectedSpan.IsClaudeCode for ProcessClaude);
    // a span still labeled raw-otlp (or without an observation row at all)
    // binds here on byte-identical session.id evidence.
    private void ProcessClaude(ProjectedSpan row)
    {
        const string sourceAdapter = "claude-code-otel";
        var sourceEventId = $"{row.TraceId}/{row.SpanId}";
        if (FindSessionBySourceIdentity(sourceAdapter, sourceEventId) is not null)
        {
            return;
        }

        var binding = row.PayloadJson is null
            ? null
            : claudeExactBindingRule.Resolve(row.PayloadJson, row.TraceId, row.SpanId);
        var traceSessionId = FindUnboundClaudeSessionByTraceId(row.TraceId);
        var sessionId = binding?.SessionId ?? traceSessionId ?? Guid.CreateVersion7();
        var matchKind = binding is not null
            ? MatchKind(binding.BindingKind)
            : traceSessionId == sessionId
                ? SessionMatchKind.TraceContinuity
                : SessionMatchKind.None;
        var existing = store.GetDetail(sessionId);
        var occurredAt = row.StartTime ?? row.ProjectedAt;
        var lastSeenAt = row.EndTime ?? occurredAt;
        var now = timeProvider.GetUtcNow();
        var completeness = binding is null
            ? SessionCompleteness.Unbound
            : CalculateExactCompleteness(existing);
        var session = existing?.Session is { } current
            ? current with
            {
                Completeness = completeness,
                LastSeenAt = current.LastSeenAt > lastSeenAt ? current.LastSeenAt : lastSeenAt,
                UpdatedAt = now,
            }
            : new ObservedSession(
                sessionId,
                ObservedSessionStatus.Unknown,
                SessionCompleteness.Unbound,
                Repository: null,
                Workspace: null,
                StartedAt: null,
                EndedAt: null,
                LastSeenAt: lastSeenAt,
                SessionRawRetentionState.NotCaptured,
                CreatedAt: now,
                UpdatedAt: now);
        var runId = Guid.CreateVersion7();
        var run = new ObservedSessionRun(
            runId,
            sessionId,
            SessionSourceSurface.ClaudeCode,
            NativeRunId: null,
            row.TraceId,
            ParentRunId: null,
            row.RequestModel,
            ParseRunStatus(row.Status),
            row.StartTime,
            row.EndTime,
            row.InputTokens,
            row.OutputTokens,
            row.TotalTokens);
        var @event = new ObservedSessionEvent(
            Guid.CreateVersion7(),
            sessionId,
            runId,
            SessionSourceSurface.ClaudeCode,
            ParentEventId: null,
            row.TraceId,
            row.Status,
            sourceAdapter,
            sourceEventId,
            "otel.span",
            occurredAt,
            SessionContentState.NotCaptured,
            row.SourceApplicationVersion,
            row.AdapterVersion,
            row.SchemaFingerprint,
            NormalizationVersion: null,
            MatchKind: matchKind);
        checkpoint?.Invoke("before-claude-write");
        store.Write(new(new(session, [], [run], [@event]), []));
    }

    private static SessionCompleteness CalculateExactCompleteness(SessionDetail? existing)
    {
        var events = existing?.Events ?? [];
        var hasStart = events.Any(item => item.Type is "session.start" or "SessionStart");
        var hasInstruction = events.Any(item => item.Type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted");
        var hasTerminal = events.Any(item => item.Type is "session.shutdown" or "session.task_complete" or "SessionEnd" or "Stop");
        var hasGap = events.Any(item => item.Type == "capture.started" && item.Status == "gap_before_capture");
        var unsupported = events.Any(item => item.ContentState == SessionContentState.Unsupported);
        return SessionCompletenessCalculator.Calculate(new(
            HasNativeId: existing?.NativeIds.Count > 0,
            HasLifecycleStart: hasStart,
            HasUserInstruction: hasInstruction,
            HasSdkHookOrOtelEvidence: true,
            HasTerminalEvidence: hasTerminal,
            HasExactLinkedOtelEnrichment: true,
            HasAllSurfaceRequiredEvidence: hasStart && hasInstruction && hasTerminal,
            HasUnsupportedVersion: unsupported,
            HasIngestGap: hasGap));
    }

    private static SessionMatchKind MatchKind(SessionBindingKind bindingKind) => bindingKind switch
    {
        SessionBindingKind.Native => SessionMatchKind.ExactNative,
        SessionBindingKind.ExplicitResume or SessionBindingKind.ExplicitHandoff => SessionMatchKind.ExplicitLink,
        _ => throw new InvalidOperationException("Unsupported exact Claude binding kind."),
    };

    private Guid? FindSessionBySourceIdentity(string sourceAdapter, string sourceEventId) =>
        FindUnambiguous(
            "SELECT DISTINCT session_id FROM session_events WHERE source_adapter=$first AND source_event_id=$second COLLATE BINARY;",
            sourceAdapter,
            sourceEventId);

    private Guid? FindUnboundClaudeSessionByTraceId(string traceId) =>
        FindUnambiguous(
            """
            SELECT DISTINCT e.session_id
            FROM session_events e JOIN sessions s ON s.session_id=e.session_id
            WHERE e.source_adapter=$first AND e.trace_id=$second COLLATE BINARY AND s.completeness='unbound';
            """,
            "claude-code-otel",
            traceId);

    private Guid? FindUnambiguous(string sql, string first, string second)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        using var reader = command.ExecuteReader();
        Guid? result = null;
        while (reader.Read())
        {
            var current = Guid.Parse(reader.GetString(0));
            if (result is not null && result != current)
            {
                return null;
            }
            result = current;
        }
        return result;
    }

    private static ObservedSessionStatus ParseRunStatus(string? status) => status switch
    {
        "ok" => ObservedSessionStatus.Completed,
        "error" => ObservedSessionStatus.Failed,
        _ => ObservedSessionStatus.Unknown,
    };

    private IReadOnlyList<ProjectedSpan> ReadRows(long after, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var hasSourceObservations = TableExists(connection, "source_schema_observations");
        var observationColumns = hasSourceObservations
            ? "o.source_surface,o.source_application_version,o.source_adapter,o.adapter_version,o.schema_fingerprint"
            : "NULL,NULL,NULL,NULL,NULL";
        var observationJoin = hasSourceObservations
            ? "LEFT JOIN source_schema_observations o ON o.raw_record_id=s.raw_record_id"
            : string.Empty;
        command.CommandText = $"""
            SELECT s.id,s.raw_record_id,s.trace_id,COALESCE(s.span_id,''),s.conversation_id,t.client_kind,
                   t.repository_name,t.workspace_label,s.start_time,s.end_time,s.projected_at,
                   s.request_model,s.input_tokens,s.output_tokens,s.total_tokens,s.status,r.payload_json,
                   {observationColumns}
            FROM monitor_spans s
            JOIN monitor_traces t ON t.trace_id=s.trace_id
            LEFT JOIN raw_records r ON r.id=s.raw_record_id
            {observationJoin}
            WHERE s.id > $after ORDER BY s.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<ProjectedSpan>();
        while (reader.Read())
        {
            rows.Add(new(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), Nullable(reader, 4), Nullable(reader, 5),
                Nullable(reader, 6), Nullable(reader, 7), Timestamp(reader, 8), Timestamp(reader, 9),
                DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Nullable(reader, 11), NullableInt64(reader, 12), NullableInt64(reader, 13), NullableInt64(reader, 14), Nullable(reader, 15),
                Nullable(reader, 16), Nullable(reader, 17), Nullable(reader, 18), Nullable(reader, 19), Nullable(reader, 20), Nullable(reader, 21)));
        }
        return rows;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
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
    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTimeOffset? Timestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), null, System.Globalization.DateTimeStyles.RoundtripKind);
    private sealed record ProjectedSpan(
        long Id,
        long RawRecordId,
        string TraceId,
        string SpanId,
        string? ConversationId,
        string? ClientKind,
        string? Repository,
        string? Workspace,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        DateTimeOffset ProjectedAt,
        string? RequestModel,
        long? InputTokens,
        long? OutputTokens,
        long? TotalTokens,
        string? Status,
        string? PayloadJson,
        string? SourceSurface,
        string? SourceApplicationVersion,
        string? SourceAdapter,
        string? AdapterVersion,
        string? SchemaFingerprint)
    {
        public bool IsClaudeCode => string.Equals(SourceSurface, "claude-code", StringComparison.Ordinal)
            && string.Equals(SourceAdapter, "claude-code-otel", StringComparison.Ordinal);
    }
}

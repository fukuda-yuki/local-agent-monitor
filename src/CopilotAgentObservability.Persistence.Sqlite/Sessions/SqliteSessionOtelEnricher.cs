using CopilotAgentObservability.Telemetry.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SqliteSessionOtelEnricher
{
    public const string ProjectorKey = "session-otel-enrichment";
    internal const string ContentProjectorKey = "session-copilot-otel-content-v2";
    private readonly string databasePath;
    private readonly ISessionStore store;
    private readonly RawTelemetryStore rawStore;
    private readonly ClaudeExactBindingRule claudeExactBindingRule;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? checkpoint;

    public SqliteSessionOtelEnricher(string databasePath, ISessionStore store, RetentionCatalogContext retentionContext, TimeProvider? timeProvider = null)
    {
        this.databasePath = databasePath;
        this.store = store;
        rawStore = new RawTelemetryStore(databasePath, retentionContext, timeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        claudeExactBindingRule = new(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal SqliteSessionOtelEnricher(
        string databasePath,
        ISessionStore store,
        RetentionCatalogContext retentionContext,
        TimeProvider timeProvider,
        Action<string> checkpoint)
        : this(databasePath, store, retentionContext, timeProvider)
    {
        this.checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public int ProcessNextBatch(int limit = 100)
    {
        var state = store.GetProjectionState(ProjectorKey);
        var rows = ReadRows(state?.ProjectionCursor ?? 0, limit);
        var rawResult = rawStore.ReadRawRecordsAsync(rows.Select(row => row.RawRecordId).ToArray(), RetentionReadKind.Operation, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (rawResult.Lease is null)
        {
            ProcessContentBatch(limit);
            return 0;
        }
        try
        {
            if (rawResult.Disposition is not null)
            {
                _ = rawResult.CompletePostGrantFailure();
                return 0;
            }
            PreparedProjectedSpan[] retainedRows;
            using (var reference = rawResult.Lease.AcquireValueReference())
            {
                var payloadByRawRecordId = reference.Value.ToDictionary(record => record.Id!.Value, record => record.PayloadJson);
                retainedRows = rows
                    .Select(sourceRow =>
                    {
                        var payloadJson = payloadByRawRecordId.GetValueOrDefault(sourceRow.RawRecordId);
                        var binding = payloadJson is null
                            ? null
                            : claudeExactBindingRule.Resolve(payloadJson, sourceRow.TraceId, sourceRow.SpanId);
                        var source = payloadJson is null ? null : OtlpTraceSourceResolver.Resolve(payloadJson).SingleOrDefault(item => item.TraceId == sourceRow.TraceId);
                        var hasCopilotEvidence = source is { CliCandidateObserved: true } or { VsCodeCandidateObserved: true };
                        var copilotSurface = source?.State == TraceSourceResolutionState.Resolved
                            && string.Equals(source.SourceFamily, sourceRow.ClientKind, StringComparison.Ordinal)
                            ? ConfirmSurface(sourceRow.ClientKind) : null;
                        var rejectedCopilotSource = hasCopilotEvidence && copilotSurface is null;
                        var identity = payloadJson is null ? null : CopilotOtelMessages.ReadIdentity(payloadJson, sourceRow.TraceId, sourceRow.SpanId);
                        return new PreparedProjectedSpan(sourceRow with
                        {
                            ConversationId = hasCopilotEvidence ? copilotSurface is null ? null : identity?.NativeId : sourceRow.ConversationId,
                            ClientKind = rejectedCopilotSource ? null : sourceRow.ClientKind,
                            HasCopilotNativeIdentity = copilotSurface is not null && identity is not null,
                            RejectedCopilotSource = rejectedCopilotSource,
                            HasCopilotSource = copilotSurface is not null,
                        }, rejectedCopilotSource ? null : binding);
                    })
                    .ToArray();
            }
            checkpoint?.Invoke("before_raw_terminal");
            if (rawResult.Lease.TryCompleteWithoutRaw() != RetentionRawTerminalResult.CompletedWithoutRaw)
                return 0;
            foreach (var row in retainedRows)
            {
                if (row.Row.IsClaudeCode)
                {
                    ProcessClaude(row.Row, row.Binding);
                }
                else
                {
                    Process(row.Row, row.Binding);
                }
                store.UpsertProjectionState(new(ProjectorKey, row.Row.Id, state?.UnsupportedEventVersionCount ?? 0, timeProvider.GetUtcNow()));
            }
        }
        finally { rawResult.Lease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        ProcessContentBatch(limit);
        return rows.Count;
    }

    private void ProcessContentBatch(int limit)
    {
        var cursor = store.GetProjectionState(ContentProjectorKey)?.ProjectionCursor ?? 0;
        var metadataCursor = store.GetProjectionState(ProjectorKey)?.ProjectionCursor ?? 0;
        foreach (var row in ReadRows(cursor, limit).TakeWhile(row => row.Id <= metadataCursor))
        {
            var owner = FindEventBySourceIdentity("otel-exact", $"{row.TraceId}/{row.SpanId}");
            if (owner is not null && ConfirmSurface(row.ClientKind) is { } surface)
            {
                var result = rawStore.ReadRawRecordsAsync([row.RawRecordId], RetentionReadKind.Operation, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                try
                {
                    if (result.Lease is null)
                    {
                        if (result.Disposition is not (RetentionReadDisposition.LifecycleDenied or RetentionReadDisposition.SelectorUnavailable)) return;
                    }
                    else
                    {
                        if (result.Disposition is not null) { _ = result.CompletePostGrantFailure(); return; }
                        using (var reference = result.Lease.AcquireValueReference())
                        {
                            var raw = reference.Value.Single();
                            var messages = CopilotOtelMessages.ReadSurface(raw.PayloadJson, row.TraceId) == surface
                                ? CopilotOtelMessages.Read(raw.PayloadJson, row.TraceId, row.SpanId) : [];
                            var detail = store.GetDetail(owner.SessionId)!;
                            var events = new List<ObservedSessionEvent>();
                            var content = new List<SessionEventContent>();
                            foreach (var message in messages)
                            {
                                var sourceId = $"{row.TraceId}/{row.SpanId}/{message.Direction}/{message.Ordinal}";
                                if (FindEventBySourceIdentity("copilot-otel", sourceId) is not null) continue;
                                var eventId = Guid.CreateVersion7();
                                events.Add(new(eventId, owner.SessionId, owner.RunId, surface,
                                    message.Direction is "tool_input" or "tool_result" ? owner.EventId : null, row.TraceId, null,
                                    "copilot-otel", sourceId, message.Type,
                                    message.Direction == "output" ? row.EndTime ?? row.StartTime ?? row.ProjectedAt : row.StartTime ?? row.ProjectedAt,
                                    message.State, MatchKind: SessionMatchKind.TraceContinuity));
                                if (message.Json is not null)
                                    content.Add(new(eventId, "application/json", message.Json, raw.ReceivedAt, raw.ReceivedAt.AddDays(90)));
                            }
                            if (events.Count != 0)
                            {
                                checkpoint?.Invoke("before-copilot-content-write");
                                var session = content.Count == 0 ? detail.Session : detail.Session with
                                {
                                    RawRetentionState = SessionRawRetentionState.Expiring,
                                    UpdatedAt = timeProvider.GetUtcNow(),
                                };
                                ((SqliteSessionStore)store).WriteFromOtel(new(new(session, [], [], events), content), result.Lease.Grants);
                            }
                        }
                        if (result.Lease.TryCompleteWithoutRaw() != RetentionRawTerminalResult.CompletedWithoutRaw) return;
                    }
                }
                catch (SessionOtelLeaseLostException) { return; }
                finally { result.Lease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            store.UpsertProjectionState(new(ContentProjectorKey, row.Id, 0, timeProvider.GetUtcNow()));
        }
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

    private void Process(ProjectedSpan row, ClaudeExactBindingMatch? claudeBinding)
    {
        var confirmedSurface = ConfirmSurface(row.ClientKind);
        var traceSessionId = row.RejectedCopilotSource ? null : row.HasCopilotSource
            ? FindCopilotSession(row.TraceId, confirmedSurface!.Value, byTrace: true)
            : FindSessionByTraceId(row.TraceId);
        var conversationSessionId = string.IsNullOrEmpty(row.ConversationId) ? null
            : row.HasCopilotSource ? FindCopilotSession(row.ConversationId, confirmedSurface!.Value, byTrace: false)
            : FindUnambiguousSessionByNativeId(row.ConversationId);
        if (row.HasCopilotNativeIdentity && traceSessionId is not null)
        {
            var traceNativeIds = store.GetDetail(traceSessionId.Value)!.NativeIds
                .Where(item => item.SourceSurface == confirmedSurface).ToArray();
            if (traceNativeIds.Length != 0 && traceNativeIds.All(item => !string.Equals(item.NativeSessionId, row.ConversationId, StringComparison.Ordinal)))
                traceSessionId = null;
        }
        var identityConflict = traceSessionId is not null && conversationSessionId is not null && traceSessionId != conversationSessionId;
        var sessionId = claudeBinding?.SessionId ?? traceSessionId ?? conversationSessionId ?? Guid.CreateVersion7();
        var matchKind = claudeBinding is not null
            ? MatchKind(claudeBinding.BindingKind)
            : traceSessionId == sessionId
                ? SessionMatchKind.TraceContinuity
                : conversationSessionId == sessionId
                    ? SessionMatchKind.ConversationId
                    : SessionMatchKind.None;
        var existing = store.GetDetail(sessionId);
        var eventId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var occurredAt = row.StartTime ?? row.ProjectedAt;

        var nativeIds = new List<SessionNativeId>();
        if (!identityConflict && confirmedSurface is not null && row.ConversationId is not null
            && (conversationSessionId == sessionId || row.HasCopilotNativeIdentity && conversationSessionId is null
                && (existing is null || existing.NativeIds.Count == 0))
            && (existing?.NativeIds ?? []).All(item => item.SourceSurface != confirmedSurface.Value || !string.Equals(item.NativeSessionId, row.ConversationId, StringComparison.Ordinal)))
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
                sessionId, ObservedSessionStatus.Unknown, completeness,
                row.Repository, row.Workspace, null, null, occurredAt,
                SessionRawRetentionState.NotCaptured, now, now);
        var hasLlmTokenAuthority = string.Equals(row.Category, "llm_call", StringComparison.Ordinal);
        var run = new ObservedSessionRun(
            runId, sessionId, confirmedSurface, null, row.TraceId, null, row.ResponseModel ?? row.RequestModel,
            ParseRunStatus(row.Status), row.StartTime, row.EndTime,
            hasLlmTokenAuthority ? row.InputTokens : null,
            hasLlmTokenAuthority ? row.OutputTokens : null,
            hasLlmTokenAuthority ? row.ProducerTotalTokens : null);
        var @event = new ObservedSessionEvent(
            eventId, sessionId, runId, confirmedSurface, null, row.TraceId, null,
            "otel-exact", $"{row.TraceId}/{row.SpanId}", "otel.span", occurredAt, row.RejectedCopilotSource ? SessionContentState.Unsupported : SessionContentState.NotCaptured,
            MatchKind: matchKind);
        store.Write(new(new(session, nativeIds, [run], [@event]), []));
    }

    // Issue #108 / D058: the exact native-session-ID resolver binds on its own
    // session.id evidence alone. It must not require claude-code-otel adapter
    // promotion (gated only by ProjectedSpan.IsClaudeCode for ProcessClaude);
    // a span still labeled raw-otlp (or without an observation row at all)
    // binds here on byte-identical session.id evidence.
    private void ProcessClaude(ProjectedSpan row, ClaudeExactBindingMatch? binding)
    {
        const string sourceAdapter = "claude-code-otel";
        var sourceEventId = $"{row.TraceId}/{row.SpanId}";
        var replay = FindEventBySourceIdentity(sourceAdapter, sourceEventId);

        var traceSessionId = FindUnboundClaudeSessionByTraceId(row.TraceId, sourceEventId);
        var sessionId = binding?.SessionId ?? replay?.SessionId ?? traceSessionId ?? Guid.CreateVersion7();
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
        var session = replay is not null
            ? existing?.Session ?? throw new InvalidOperationException("Session event replay owner is missing.")
            : existing?.Session is { } current
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
        var runId = replay?.RunId ?? Guid.CreateVersion7();
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
        if (replay?.RunId is not null
            && replay.SessionId == sessionId
            && (existing is null || !existing.Runs.Contains(run)))
        {
            throw new InvalidOperationException("Session run replay conflict.");
        }
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

    private ExistingSourceEvent? FindEventBySourceIdentity(string sourceAdapter, string sourceEventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id,run_id,event_id FROM session_events WHERE source_adapter=$adapter AND source_event_id=$source_event_id COLLATE BINARY;";
        command.Parameters.AddWithValue("$adapter", sourceAdapter);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = new ExistingSourceEvent(
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)));
        if (reader.Read()) throw new InvalidOperationException("Ambiguous Session event source identity.");
        return result;
    }

    private Guid? FindUnboundClaudeSessionByTraceId(string traceId, string sourceEventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT e.session_id
            FROM session_events e JOIN sessions s ON s.session_id=e.session_id
            WHERE e.source_adapter='claude-code-otel'
              AND e.trace_id=$trace_id COLLATE BINARY
              AND e.source_event_id<>$source_event_id COLLATE BINARY
              AND s.completeness='unbound';
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId);
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
        var hasSpanFacts = TableExists(connection, "local_workspace_span_facts");
        var producerTotalTokensColumn = hasSpanFacts ? "f.producer_total_tokens" : "NULL";
        var spanFactsJoin = hasSpanFacts
            ? "LEFT JOIN local_workspace_span_facts f ON f.raw_record_id=s.raw_record_id AND f.span_ordinal=s.span_ordinal"
            : string.Empty;
        command.CommandText = $"""
            SELECT s.id,s.raw_record_id,s.trace_id,COALESCE(s.span_id,''),s.conversation_id,t.client_kind,
                   t.repository_name,t.workspace_label,s.start_time,s.end_time,s.projected_at,
                   s.category,s.request_model,s.response_model,s.input_tokens,s.output_tokens,
                   s.total_tokens,{producerTotalTokensColumn},s.status,
                   {observationColumns}
            FROM monitor_spans s
            JOIN monitor_traces t ON t.trace_id=s.trace_id
            {spanFactsJoin}
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
                Nullable(reader, 11), Nullable(reader, 12), Nullable(reader, 13),
                NullableInt64(reader, 14), NullableInt64(reader, 15), NullableInt64(reader, 16), NullableInt64(reader, 17), Nullable(reader, 18),
                PayloadJson: null, Nullable(reader, 19), Nullable(reader, 20), Nullable(reader, 21), Nullable(reader, 22), Nullable(reader, 23)));
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

    private Guid? FindCopilotSession(string identity, SessionSourceSurface surface, bool byTrace) => FindUnambiguous($"""
        SELECT DISTINCT candidate.session_id FROM {(byTrace ? "session_events" : "session_native_ids")} candidate
        WHERE candidate.{(byTrace ? "trace_id" : "native_session_id")}=$first COLLATE BINARY
          AND (candidate.source_surface=$second OR (candidate.source_surface='hook-unknown'
          AND NOT EXISTS(SELECT 1 FROM {(byTrace ? "session_events" : "session_native_ids")} exact
            WHERE exact.{(byTrace ? "trace_id" : "native_session_id")}=$first COLLATE BINARY AND exact.source_surface=$second)
          AND NOT EXISTS(SELECT 1 FROM session_native_ids known WHERE known.session_id=candidate.session_id
            AND known.source_surface IN ('copilot-cli','vscode') AND known.source_surface<>$second)
          AND NOT EXISTS(SELECT 1 FROM session_events known WHERE known.session_id=candidate.session_id
            AND known.source_surface IN ('copilot-cli','vscode') AND known.source_surface<>$second)));
        """, identity, SessionWire.ToWire(surface));

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
        string? Category,
        string? RequestModel,
        string? ResponseModel,
        long? InputTokens,
        long? OutputTokens,
        long? TotalTokens,
        long? ProducerTotalTokens,
        string? Status,
        string? PayloadJson,
        string? SourceSurface,
        string? SourceApplicationVersion,
        string? SourceAdapter,
        string? AdapterVersion,
        string? SchemaFingerprint)
    {
        public bool RejectedCopilotSource { get; init; }
        public bool HasCopilotSource { get; init; }
        public bool HasCopilotNativeIdentity { get; init; }
        public bool IsClaudeCode => string.Equals(SourceSurface, "claude-code", StringComparison.Ordinal)
            && string.Equals(SourceAdapter, "claude-code-otel", StringComparison.Ordinal);
    }

    private sealed record PreparedProjectedSpan(
        ProjectedSpan Row,
        ClaudeExactBindingMatch? Binding);

    private sealed record ExistingSourceEvent(Guid SessionId, Guid? RunId, Guid EventId);
}

using Microsoft.Data.Sqlite;
using System.Numerics;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionSnapshotContributor : ILocalRepositorySessionSnapshotContributor
{
    private const int MaximumSessions = 10_000;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? statementObserver;
    private readonly ISkillRegistryGenerationAuthority? registryAuthority;

    internal LocalWorkspaceSessionSnapshotContributor(
        TimeProvider? timeProvider = null,
        Action<string>? statementObserver = null,
        ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.statementObserver = statementObserver;
        this.registryAuthority = registryAuthority;
    }

    internal TimeProvider TimeProvider => timeProvider;
    internal ISkillRegistryGenerationAuthority? RegistryAuthority => registryAuthority;

    public ValueTask<LocalRepositorySessionContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken)
    {
        var acceptedAt = timeProvider.GetUtcNow();
        var now = acceptedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return transaction.ReadAsync((connection, sqliteTransaction, token) =>
            request.ExactTargetSessionIds is null
                ? ReadRowsAsync(connection, sqliteTransaction, acceptedAt, now, request.TargetSessionId, statementObserver, registryAuthority, token)
                : ReadExactRowsAsync(connection, sqliteTransaction, acceptedAt, now, request.ExactTargetSessionIds, statementObserver, registryAuthority, token), cancellationToken);
    }

    internal ValueTask<LocalRepositorySessionContribution> ReadPinnedAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        DateTimeOffset acceptedAt,
        ISkillRegistryGenerationAuthority registryGeneration,
        CancellationToken cancellationToken)
    {
        var now = acceptedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return transaction.ReadAsync((connection, sqliteTransaction, token) =>
            request.ExactTargetSessionIds is null
                ? ReadRowsAsync(connection, sqliteTransaction, acceptedAt, now, request.TargetSessionId, statementObserver, registryGeneration, token)
                : ReadExactRowsAsync(connection, sqliteTransaction, acceptedAt, now, request.ExactTargetSessionIds, statementObserver, registryGeneration, token), cancellationToken);
    }

    private static async ValueTask<LocalRepositorySessionContribution> ReadExactRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset acceptedAt,
        string now,
        IReadOnlyList<string> exactTargetSessionIds,
        Action<string>? statementObserver,
        ISkillRegistryGenerationAuthority? registryAuthority,
        CancellationToken cancellationToken)
    {
        var rows = new List<ILocalRepositorySessionSnapshotRow>(exactTargetSessionIds.Count);
        var projectionErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sessionId in exactTargetSessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contribution = await ReadRowsAsync(
                    connection,
                    transaction,
                    acceptedAt,
                    now,
                    sessionId,
                    statementObserver,
                    registryAuthority,
                    cancellationToken).ConfigureAwait(false);
                rows.AddRange(contribution.Sessions);
            }
            catch (LocalWorkspaceSessionDetailException exception) when (exception.Error != "workspace_too_large")
            {
                rows.Add(new LocalUnavailableRepositorySessionSnapshotRow(sessionId));
                projectionErrors.Add(sessionId, exception.Error);
            }
        }
        return new(Array.AsReadOnly(rows.ToArray()), projectionErrors);
    }

    private static async ValueTask<LocalRepositorySessionContribution> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset acceptedAt,
        string now,
        string? targetSessionId,
        Action<string>? statementObserver,
        ISkillRegistryGenerationAuthority? registryAuthority,
        CancellationToken cancellationToken)
    {
        var rows = new List<MutableRow>();
        var retentionInstalled = TableExists(connection, transaction, "retention_items");
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("sessions");
            command.Transaction = transaction;
            command.CommandText = retentionInstalled ? (targetSessionId is null ? """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND CASE WHEN c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN 1 ELSE 0 END=0 THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed,
                       CASE WHEN p.label_state IN ('recorded','not_observed','not_captured','expired') AND (
                         p.label_state='recorded' AND p.label_text IS NOT NULL AND trim(p.label_text)<>''
                           AND length(p.label_text)<=160 AND instr(p.label_text,char(10))=0 AND instr(p.label_text,char(13))=0
                           AND instr(p.label_text,char(8232))=0 AND instr(p.label_text,char(8233))=0
                           AND p.label_source_identity IS NOT NULL AND p.label_expires_at IS NOT NULL
                         OR p.label_state<>'recorded' AND p.label_text IS NULL
                           AND p.label_source_identity IS NULL AND p.label_expires_at IS NULL) THEN 1 ELSE 0 END
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id
                LEFT JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                WHERE 1=1
                ORDER BY p.session_id COLLATE BINARY LIMIT 10001;
                """ : """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND CASE WHEN c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN 1 ELSE 0 END=0 THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed,
                       CASE WHEN p.label_state IN ('recorded','not_observed','not_captured','expired') AND (
                         p.label_state='recorded' AND p.label_text IS NOT NULL AND trim(p.label_text)<>''
                           AND length(p.label_text)<=160 AND instr(p.label_text,char(10))=0 AND instr(p.label_text,char(13))=0
                           AND instr(p.label_text,char(8232))=0 AND instr(p.label_text,char(8233))=0
                           AND p.label_source_identity IS NOT NULL AND p.label_expires_at IS NOT NULL
                         OR p.label_state<>'recorded' AND p.label_text IS NULL
                           AND p.label_source_identity IS NULL AND p.label_expires_at IS NULL) THEN 1 ELSE 0 END
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id
                LEFT JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                WHERE p.session_id=$target_session_id
                ORDER BY p.session_id COLLATE BINARY LIMIT 2;
                """) : (targetSessionId is null ? """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND (c.event_id IS NULL OR c.expires_at COLLATE BINARY <= $now COLLATE BINARY) THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL AND c.expires_at COLLATE BINARY > $now COLLATE BINARY THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed,
                       CASE WHEN p.label_state IN ('recorded','not_observed','not_captured','expired') AND (
                         p.label_state='recorded' AND p.label_text IS NOT NULL AND trim(p.label_text)<>''
                           AND length(p.label_text)<=160 AND instr(p.label_text,char(10))=0 AND instr(p.label_text,char(13))=0
                           AND instr(p.label_text,char(8232))=0 AND instr(p.label_text,char(8233))=0
                           AND p.label_source_identity IS NOT NULL AND p.label_expires_at IS NOT NULL
                         OR p.label_state<>'recorded' AND p.label_text IS NULL
                           AND p.label_source_identity IS NULL AND p.label_expires_at IS NULL) THEN 1 ELSE 0 END
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id AND c.expires_at=p.label_expires_at
                WHERE 1=1
                ORDER BY p.session_id COLLATE BINARY LIMIT 10001;
                """ : """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND (c.event_id IS NULL OR c.expires_at COLLATE BINARY <= $now COLLATE BINARY) THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL AND c.expires_at COLLATE BINARY > $now COLLATE BINARY THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed,
                       CASE WHEN p.label_state IN ('recorded','not_observed','not_captured','expired') AND (
                         p.label_state='recorded' AND p.label_text IS NOT NULL AND trim(p.label_text)<>''
                           AND length(p.label_text)<=160 AND instr(p.label_text,char(10))=0 AND instr(p.label_text,char(13))=0
                           AND instr(p.label_text,char(8232))=0 AND instr(p.label_text,char(8233))=0
                           AND p.label_source_identity IS NOT NULL AND p.label_expires_at IS NOT NULL
                         OR p.label_state<>'recorded' AND p.label_text IS NULL
                           AND p.label_source_identity IS NULL AND p.label_expires_at IS NULL) THEN 1 ELSE 0 END
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id AND c.expires_at=p.label_expires_at
                WHERE p.session_id=$target_session_id
                ORDER BY p.session_id COLLATE BINARY LIMIT 2;
                """);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$target_session_id", (object?)targetSessionId ?? DBNull.Value);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == MaximumSessions)
                    throw new InvalidOperationException("local_repository_session_limit_exceeded");
                if (reader.GetInt64(17) != 1)
                    throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
                rows.Add(new(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt64(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.GetString(15), reader.GetString(16)));
            }
        }
        var byId = rows.ToDictionary(row => row.SessionId, StringComparer.Ordinal);
        var ids = System.Text.Json.JsonSerializer.Serialize(byId.Keys.Order(StringComparer.Ordinal));
        await ValidateClosedOwnerFacts(connection, transaction, ids, now, cancellationToken).ConfigureAwait(false);
        await ReadPairs(connection, transaction, "sources", "SELECT session_id,source FROM local_workspace_session_sources WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,source;", ids, byId, static (row, value) => row.Sources.Add(value), statementObserver, cancellationToken);
        await ReadPairs(connection, transaction, "models", "SELECT session_id,model FROM local_workspace_session_models WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,model;", ids, byId, static (row, value) => row.Models.Add(value), statementObserver, cancellationToken);
        var labelAuthority = retentionInstalled
            ? """
              SELECT 1 FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id AND i.expires_at=c.expires_at
              WHERE e.session_id=f.session_id AND e.event_id=f.source_identity AND e.content_state='available'
                AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY))
              """
            : """
              SELECT 1 FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.session_id=f.session_id AND e.event_id=f.source_identity AND e.content_state='available'
                AND c.expires_at=f.expires_at COLLATE BINARY AND c.expires_at COLLATE BINARY > $now COLLATE BINARY
              """;
        await ReadPairs(connection, transaction, "search", $"""
            SELECT f.session_id,f.normalized_text FROM local_workspace_session_search_facts f
            WHERE f.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND (f.expires_at IS NULL OR f.expires_at COLLATE BINARY > $now COLLATE BINARY)
              AND ((f.kind='label' AND EXISTS(
                {labelAuthority}))
                OR f.kind='tool')
            GROUP BY f.session_id,f.normalized_text ORDER BY f.session_id,f.normalized_text COLLATE BINARY;
            """, ids, byId, static (row, value) => row.SearchTexts.Add(value), statementObserver, cancellationToken, now);
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("activity");
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,kind,state,count FROM local_workspace_session_activity WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,kind;";
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (byId.TryGetValue(reader.GetString(0), out var row)) row.Activity[reader.GetString(1)] = new(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }
        if (rows.Any(static row => !row.HasClosedActivityFamilies))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var currentSkills = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, byId.Keys.ToArray(), acceptedAt, registryAuthority);
        foreach (var row in rows)
        {
            if (currentSkills.TryGetValue(row.SessionId, out var projection))
            {
                row.Activity["skill"] = projection.State == "current"
                    ? new("recorded", projection.InvocationCount)
                    : new(projection.State, null);
                var currentInvocationCount = projection.Invocations.LongCount(
                    static invocation => invocation.CurrentValidState == "current");
                row.CurrentSkillFilter = currentInvocationCount > 0
                    ? new("recorded", currentInvocationCount)
                    : row.Activity["skill"];
                row.SearchTexts.AddRange(projection.SearchFacts
                    .Select(static fact => fact.SkillName.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()));
            }
            else
            {
                row.Activity["skill"] = new("not_observed", null);
                row.CurrentSkillFilter = row.Activity["skill"];
            }
            row.SearchTexts.Sort(StringComparer.Ordinal);
        }
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("tokens");
            command.Transaction = transaction;
            command.CommandText = """
                SELECT session_id,execution_id,authority,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens
                FROM local_workspace_token_observations WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                ORDER BY session_id,execution_id,authority_rank,source_identity COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var observations = new Dictionary<string, List<(string ExecutionId, TokenObservation Tokens)>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                if (!observations.TryGetValue(sessionId, out var values)) observations[sessionId] = values = [];
                values.Add((reader.GetString(1), new(reader.GetString(2), Nullable(reader, 3), Nullable(reader, 4), Nullable(reader, 5), Nullable(reader, 6), Nullable(reader, 7), Nullable(reader, 8))));
            }
            foreach (var pair in observations)
            {
                if (!byId.TryGetValue(pair.Key, out var row)) continue;
                var calls = pair.Value.GroupBy(value => value.ExecutionId, StringComparer.Ordinal)
                    .Where(call => call.Any(value => value.Tokens.Authority == "llm_span" || value.Tokens.Values.Any(token => token is not null)))
                    .Select(call => SelectCallTokens(call.Select(value => value.Tokens).ToArray())).ToArray();
                if (calls.Length > 0) row.TokenAggregate = ReadTokens(calls);
            }
        }
        await ReadPairs(connection, transaction, "capture", "SELECT DISTINCT session_id,'source_unsupported' FROM session_events WHERE content_state='unsupported' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id;", ids, byId,
            static (row, note) => row.AdditionalCaptureNotes.Add(note), statementObserver, cancellationToken);
        var activityRanges = new Dictionary<string, LocalWorkspaceObservedActivity>(StringComparer.Ordinal);
        using (var range = connection.CreateCommand())
        {
            range.Transaction = transaction;
            range.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('monitor_spans') WHERE name='start_time');";
            if (Convert.ToInt64(await range.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                range.CommandText = """
                    SELECT e.session_id,MIN(local_workspace_ticks(m.start_time)),
                      MAX(COALESCE(local_workspace_ticks(m.end_time),local_workspace_ticks(m.start_time)))
                    FROM session_events e JOIN monitor_spans m
                      ON e.trace_id=m.trace_id COLLATE BINARY AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
                    WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                      AND e.source_adapter='otel-exact' AND e.type='otel.span'
                      AND local_workspace_ticks(m.start_time) IS NOT NULL
                      AND (m.end_time IS NULL OR local_workspace_ticks(m.end_time)>=local_workspace_ticks(m.start_time))
                      AND (SELECT COUNT(*) FROM monitor_spans other WHERE lower(other.trace_id)=m.trace_id AND lower(other.span_id)=m.span_id)=1
                    GROUP BY e.session_id;
                    """;
                range.Parameters.AddWithValue("$ids", ids);
                using var reader = await range.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var start = reader.GetInt64(1); var end = reader.GetInt64(2);
                    activityRanges[reader.GetString(0)] = new(new DateTimeOffset(start, TimeSpan.Zero).ToString("O"),
                        new DateTimeOffset(end, TimeSpan.Zero).ToString("O"), (end - start) / 10_000);
                }
            }
        }
        var frozen = rows.Select(row => row.Freeze() with { ObservedActivity = activityRanges.GetValueOrDefault(row.SessionId) }).ToArray();
        if (frozen.Any(static row => !ValidClosedRow(row)))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        return new(Array.AsReadOnly(frozen.Cast<ILocalRepositorySessionSnapshotRow>().ToArray()));
    }

    private static async Task ValidateClosedOwnerFacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ids,
        string now,
        CancellationToken cancellationToken)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        await ValidateClosedOwnerBounds(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
        var llmSpanRows = HasOtelTokenOwnerSchema(connection, transaction)
            ? """
              UNION ALL
              SELECT event.session_id,event.run_id,'llm_span',1,
                     CAST(span.raw_record_id AS TEXT)||':'||CAST(span.span_ordinal AS TEXT),
                     span.input_tokens,span.output_tokens,fact.producer_total_tokens,span.reasoning_tokens,
                     span.cache_read_tokens,span.cache_creation_tokens
              FROM session_events event
              JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                AND event.type='otel.span' COLLATE BINARY
                AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                AND event.trace_id=span.trace_id COLLATE BINARY
              LEFT JOIN local_workspace_span_facts fact
                ON fact.raw_record_id=span.raw_record_id AND fact.span_ordinal=span.span_ordinal
              WHERE event.run_id IS NOT NULL AND span.category='llm_call' COLLATE BINARY
                AND (SELECT COUNT(*) FROM monitor_spans owner
                  WHERE lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                    AND lower(owner.span_id)=lower(span.span_id) COLLATE BINARY)=1
                AND (SELECT COUNT(*) FROM session_events owner
                  WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                    AND owner.type='otel.span' COLLATE BINARY
                    AND lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                    AND lower(owner.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)=1
                AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : string.Empty;
        var otelActivityCtes = HasOtelActivityOwnerSchema(connection, transaction)
            ? """
              retry_incomplete AS (
                SELECT event.session_id,
                       CASE WHEN MAX(event.status='gap_before_capture')=1 THEN 'capture_gap'
                            WHEN MAX(event.content_state='unsupported')=1 THEN 'source_unsupported' END state
                FROM session_events event
                JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                  AND event.type='otel.span' COLLATE BINARY
                  AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                  AND event.trace_id=span.trace_id COLLATE BINARY
                JOIN local_workspace_span_facts fact
                  ON fact.raw_record_id=span.raw_record_id AND fact.span_ordinal=span.span_ordinal
                WHERE span.operation='chat' COLLATE BINARY AND fact.retry_count IS NOT NULL
                  AND (SELECT COUNT(*) FROM monitor_spans owner
                    WHERE lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                      AND lower(owner.span_id)=lower(span.span_id) COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner
                    WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                      AND owner.type='otel.span' COLLATE BINARY
                      AND lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                      AND lower(owner.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)=1
                  AND (event.status='gap_before_capture' OR event.content_state='unsupported')
                  AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                GROUP BY event.session_id),
              retry_totals AS (
                SELECT exact.session_id,SUM(exact.retry_count) count
                FROM (
                  SELECT DISTINCT event.session_id,event.run_id,span.raw_record_id,span.span_ordinal,fact.retry_count
                  FROM session_events event
                  JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                    AND event.type='otel.span' COLLATE BINARY
                    AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                    AND event.trace_id=span.trace_id COLLATE BINARY
                  JOIN local_workspace_span_facts fact
                    ON fact.raw_record_id=span.raw_record_id AND fact.span_ordinal=span.span_ordinal
                  WHERE event.run_id IS NOT NULL AND span.operation='chat' COLLATE BINARY AND fact.retry_count IS NOT NULL
                    AND (SELECT COUNT(*) FROM monitor_spans owner
                      WHERE lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                        AND lower(owner.span_id)=lower(span.span_id) COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner
                      WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                        AND owner.type='otel.span' COLLATE BINARY
                        AND lower(owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                        AND lower(owner.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)=1
                    AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))) exact
                GROUP BY exact.session_id),
              otel_tool_totals AS (
                SELECT exact.session_id,COUNT(*) count
                FROM (
                  SELECT event.session_id,span.trace_id,span.span_id
                  FROM session_events event
                  JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                    AND event.type='otel.span' COLLATE BINARY
                    AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                    AND event.trace_id=span.trace_id COLLATE BINARY
                  WHERE span.operation='execute_tool' COLLATE BINARY AND span.category IN ('tool_call','error')
                    AND length(span.trace_id)=32 AND span.trace_id=lower(span.trace_id) AND span.trace_id NOT GLOB '*[^0-9a-f]*'
                    AND length(span.span_id)=16 AND span.span_id=lower(span.span_id) AND span.span_id NOT GLOB '*[^0-9a-f]*'
                    AND (SELECT COUNT(*) FROM monitor_spans owner
                      WHERE lower(owner.trace_id)=span.trace_id COLLATE BINARY
                        AND lower(owner.span_id)=span.span_id COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner
                      WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                        AND owner.type='otel.span' COLLATE BINARY
                        AND lower(owner.trace_id)=span.trace_id COLLATE BINARY
                        AND lower(owner.source_event_id)=span.trace_id||'/'||span.span_id COLLATE BINARY)=1
                    AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))) exact
                GROUP BY exact.session_id)
              """
            : """
              retry_incomplete(session_id,state) AS (SELECT NULL,NULL WHERE 0),
              retry_totals(session_id,count) AS (SELECT NULL,NULL WHERE 0),
              otel_tool_totals(session_id,count) AS (SELECT NULL,NULL WHERE 0)
              """;
        var retentionInstalled = TableExists(connection, transaction, "retention_items");
        var labelCarriers = retentionInstalled
            ? """
              SELECT event.session_id,event.event_id,event.type,event.occurred_at,content.captured_at,
                     content.expires_at source_expires_at,
                     CASE WHEN item.state='retained_by_policy' THEN NULL ELSE item.expires_at END effective_expires_at,
                     projected.instruction_count
              FROM session_events event
              JOIN session_event_content content ON content.event_id=event.event_id
              JOIN local_workspace_sessions projected ON projected.session_id=event.session_id
                AND projected.label_state='recorded' AND projected.label_source_identity=event.event_id
              JOIN retention_items item ON item.store_kind='session_event_content'
                AND item.source_item_id=event.event_id AND item.expires_at=content.expires_at COLLATE BINARY
              CROSS JOIN projection_clock clock
              WHERE event.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                AND event.content_state='available'
                AND item.state IN ('expiring','retained_by_policy')
                AND item.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                AND item.captured_at=content.captured_at COLLATE BINARY AND item.expires_at=content.expires_at COLLATE BINARY
                AND local_workspace_retention_receipt_matches(item.store_instance_id,event.event_id,content.content_kind,
                  content.captured_at,content.expires_at,event.session_id,event.run_id,event.source_adapter,event.source_event_id,
                  content.retention_owner_token,item.ownership_receipt)=1
                AND item.read_denied_at IS NULL AND item.deleted_at IS NULL AND item.error_code IS NULL
                AND (item.state='retained_by_policy' OR item.expires_at COLLATE BINARY>clock.refreshed_at COLLATE BINARY)
                AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : """
              SELECT event.session_id,event.event_id,event.type,event.occurred_at,content.captured_at,
                     content.expires_at source_expires_at,
                     content.expires_at effective_expires_at,
                     projected.instruction_count
              FROM session_events event
              JOIN session_event_content content ON content.event_id=event.event_id
              JOIN local_workspace_sessions projected ON projected.session_id=event.session_id
                AND projected.label_state='recorded' AND projected.label_source_identity=event.event_id
              CROSS JOIN projection_clock clock
              WHERE event.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                AND event.content_state='available'
                AND content.expires_at COLLATE BINARY>clock.refreshed_at COLLATE BINARY
                AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """;
        var toolSearchRows = retentionInstalled
            && TableExists(connection, transaction, "raw_records")
            && HasColumns(connection, transaction, "monitor_spans", "raw_record_id", "span_ordinal", "trace_id", "span_id", "operation", "category", "tool_name")
            ? """
              SELECT event.session_id,'tool' kind,
                     CAST(span.raw_record_id AS TEXT)||':'||CAST(span.span_ordinal AS TEXT) source_identity,
                     local_workspace_search(span.tool_name) normalized_text,
                     CASE WHEN item.state='retained_by_policy' THEN NULL ELSE item.expires_at END expires_at
              FROM session_events event
              JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                AND event.type='otel.span' COLLATE BINARY
                AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                AND event.trace_id=span.trace_id COLLATE BINARY
              JOIN raw_records raw ON raw.id=span.raw_record_id
              JOIN retention_items item ON item.store_kind='raw_record'
                AND item.source_item_id=CAST(span.raw_record_id AS TEXT)
              CROSS JOIN projection_clock clock
              WHERE span.operation='execute_tool' COLLATE BINARY AND span.category='tool_call' COLLATE BINARY
                AND length(span.trace_id)=32 AND span.trace_id=lower(span.trace_id) AND span.trace_id NOT GLOB '*[^0-9a-f]*'
                AND length(span.span_id)=16 AND span.span_id=lower(span.span_id) AND span.span_id NOT GLOB '*[^0-9a-f]*'
                AND (SELECT COUNT(*) FROM monitor_spans owner
                  WHERE lower(owner.trace_id)=span.trace_id COLLATE BINARY
                    AND lower(owner.span_id)=span.span_id COLLATE BINARY)=1
                AND (SELECT COUNT(*) FROM session_events owner
                  WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                    AND owner.type='otel.span' COLLATE BINARY
                    AND lower(owner.trace_id)=span.trace_id COLLATE BINARY
                    AND lower(owner.source_event_id)=span.trace_id||'/'||span.span_id COLLATE BINARY)=1
                AND span.tool_name IS NOT NULL AND length(span.tool_name)>0
                AND item.state IN ('expiring','retained_by_policy')
                AND item.read_denied_at IS NULL AND item.deleted_at IS NULL AND item.error_code IS NULL
                AND (item.state='retained_by_policy' OR item.expires_at COLLATE BINARY>clock.refreshed_at COLLATE BINARY)
                AND event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : """
              SELECT NULL session_id,NULL kind,NULL source_identity,NULL normalized_text,NULL expires_at WHERE 0
              """;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH projection_clock AS (
              SELECT refreshed_at FROM local_workspace_projection_state
              WHERE projector_key='local-workspace-projection-v1'),
            label_carriers AS (
              {labelCarriers}),
            expected_labels AS (
              SELECT carrier.session_id,carrier.event_id,projected.label_text,carrier.source_expires_at,
                     carrier.effective_expires_at,carrier.instruction_count,
                     local_workspace_semantic_digest('session_label_owner',
                       local_workspace_semantic_digest('session_label_source',carrier.event_id,carrier.type),
                       local_workspace_semantic_digest('session_label_value',projected.label_text,
                         local_workspace_semantic_digest('session_label_time',carrier.occurred_at,carrier.captured_at)||
                         local_workspace_semantic_digest('session_label_expiry',carrier.source_expires_at,CAST(carrier.instruction_count AS TEXT)))) owner_revision
              FROM label_carriers carrier
              JOIN local_workspace_sessions projected ON projected.session_id=carrier.session_id
              WHERE projected.label_state='recorded' AND projected.label_source_identity=carrier.event_id COLLATE BINARY
                AND projected.label_owner_revision=local_workspace_semantic_digest('session_label_owner',
                  local_workspace_semantic_digest('session_label_source',carrier.event_id,carrier.type),
                  local_workspace_semantic_digest('session_label_value',projected.label_text,
                    local_workspace_semantic_digest('session_label_time',carrier.occurred_at,carrier.captured_at)||
                    local_workspace_semantic_digest('session_label_expiry',carrier.source_expires_at,CAST(carrier.instruction_count AS TEXT)))) COLLATE BINARY),
            expected_search_unbounded AS (
              SELECT session_id,'label' kind,event_id source_identity,
                     local_workspace_search(label_text) normalized_text,effective_expires_at expires_at
              FROM expected_labels
              UNION
              {toolSearchRows}),
            expected_search AS (
              SELECT session_id,kind,source_identity,normalized_text,expires_at
              FROM expected_search_unbounded
              WHERE expires_at IS NULL OR expires_at COLLATE BINARY>$now COLLATE BINARY),
            actual_search AS (
              SELECT session_id,kind,source_identity,normalized_text,expires_at
              FROM local_workspace_session_search_facts
              WHERE kind IN ('label','tool')
                AND (expires_at IS NULL OR expires_at COLLATE BINARY>$now COLLATE BINARY)
                AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            search_drift AS (
              SELECT 1 FROM (SELECT session_id,kind,source_identity,normalized_text,expires_at FROM actual_search EXCEPT SELECT session_id,kind,source_identity,normalized_text,expires_at FROM expected_search)
              UNION ALL SELECT 1 FROM (SELECT session_id,kind,source_identity,normalized_text,expires_at FROM expected_search EXCEPT SELECT session_id,kind,source_identity,normalized_text,expires_at FROM actual_search)),
            raw_sources AS (
              SELECT session_id,source_surface source FROM session_native_ids WHERE source_surface IS NOT NULL
              UNION SELECT session_id,source_surface FROM session_runs WHERE source_surface IS NOT NULL
              UNION SELECT session_id,source_surface FROM session_events WHERE source_surface IS NOT NULL),
            source_counts AS (
              SELECT session_id,COUNT(*) count FROM raw_sources GROUP BY session_id),
            expected_sources AS (
              SELECT source.session_id,source.source
              FROM raw_sources source
              LEFT JOIN source_counts count ON count.session_id=source.session_id
              WHERE trim(source.source)<>'' AND COALESCE(count.count,0)<=5
                AND source.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            raw_models AS (
              SELECT DISTINCT session_id,model FROM session_runs
              WHERE model IS NOT NULL AND trim(model)<>''),
            model_counts AS (
              SELECT session_id,COUNT(*) count FROM raw_models GROUP BY session_id),
            expected_models AS (
              SELECT model.session_id,model.model
              FROM raw_models model
              LEFT JOIN model_counts count ON count.session_id=model.session_id
              WHERE COALESCE(count.count,0)<=16
                AND model.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            expected_sessions AS (
              SELECT owner.session_id,
                     CASE WHEN COALESCE(local_workspace_epoch(owner.started_at),local_workspace_epoch(owner.created_at),local_workspace_epoch(owner.last_seen_at)) IS NULL THEN 1 ELSE 0 END sort_group,
                     COALESCE(local_workspace_epoch(owner.started_at),local_workspace_epoch(owner.created_at),local_workspace_epoch(owner.last_seen_at),0) sort_epoch_ms,
                     owner.status,owner.completeness,
                     CASE WHEN COALESCE(source.count,0)>5 THEN 'projection_invalid'
                          WHEN EXISTS(SELECT 1 FROM expected_sources value WHERE value.session_id=owner.session_id) THEN 'recorded'
                          ELSE 'not_observed' END source_state,
                     CASE WHEN COALESCE(model.count,0)>16 THEN 'projection_invalid'
                          WHEN EXISTS(SELECT 1 FROM expected_models value WHERE value.session_id=owner.session_id) THEN 'recorded'
                          ELSE 'not_observed' END model_state,
                     CASE WHEN local_workspace_epoch(owner.started_at) IS NOT NULL
                               AND local_workspace_epoch(owner.last_seen_at) IS NOT NULL
                               AND ((owner.status='active' AND owner.ended_at IS NULL)
                                 OR (owner.status IN ('completed','failed') AND local_workspace_epoch(owner.ended_at)>=local_workspace_epoch(owner.started_at))
                                 OR (owner.status='unknown' AND (owner.ended_at IS NULL OR local_workspace_epoch(owner.ended_at)>=local_workspace_epoch(owner.started_at)))) THEN 'recorded'
                          WHEN local_workspace_epoch(owner.started_at) IS NULL AND local_workspace_epoch(owner.ended_at) IS NULL THEN 'not_observed'
                          ELSE 'inconsistent' END timing_state,
                     local_workspace_canonical(owner.started_at) started_at,
                     local_workspace_canonical(owner.ended_at) ended_at,
                     local_workspace_canonical(owner.last_seen_at) last_seen_at,
                     local_workspace_epoch(owner.last_seen_at) last_seen_epoch_ms,
                     CASE WHEN owner.status<>'active' AND local_workspace_epoch(owner.started_at) IS NOT NULL
                               AND local_workspace_epoch(owner.ended_at)>=local_workspace_epoch(owner.started_at)
                          THEN local_workspace_epoch(owner.ended_at)-local_workspace_epoch(owner.started_at) END duration_ms,
                     CASE WHEN COALESCE(source.count,0)>5 OR COALESCE(model.count,0)>16
                          THEN CASE owner.raw_retention_state
                            WHEN 'expired_pending_deletion' THEN 'projection_invalid,raw_content_expired'
                            WHEN 'not_captured' THEN 'projection_invalid,raw_content_not_captured'
                            ELSE 'projection_invalid' END
                          ELSE CASE owner.raw_retention_state
                            WHEN 'expired_pending_deletion' THEN 'raw_content_expired'
                            WHEN 'not_captured' THEN 'raw_content_not_captured'
                            ELSE '' END END capture_notes,
                     CASE owner.raw_retention_state
                       WHEN 'expired_pending_deletion' THEN 'expired'
                       WHEN 'not_captured' THEN 'not_captured'
                       ELSE 'not_observed' END base_label_state,
                     owner.status||'|'||owner.completeness||'|'||
                       COALESCE(local_workspace_canonical(owner.started_at),'')||'|'||
                       COALESCE(local_workspace_canonical(owner.ended_at),'')||'|'||
                       COALESCE(local_workspace_canonical(owner.last_seen_at),'') revision_base
              FROM sessions owner
              LEFT JOIN source_counts source ON source.session_id=owner.session_id
              LEFT JOIN model_counts model ON model.session_id=owner.session_id
              WHERE owner.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            {otelActivityCtes},
            activity_kinds(kind) AS (
              VALUES('tool'),('subagent'),('error'),('retry')),
            activity_states AS (
              SELECT owner.session_id,kind.kind,
                     CASE
                       WHEN EXISTS(SELECT 1 FROM session_events event
                         WHERE event.session_id=owner.session_id AND event.status='gap_before_capture' AND CASE kind.kind
                           WHEN 'tool' THEN event.type IN ('tool.execution_start','PreToolUse')
                           WHEN 'subagent' THEN event.type IN ('subagent.started','SubagentStart')
                           WHEN 'error' THEN event.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR event.terminal_outcome='failed' OR {LocalWorkspaceProjectionStore.RecordedOtelFailurePredicate(connection, transaction, "event")}
                           ELSE 0 END) THEN 'capture_gap'
                       WHEN EXISTS(SELECT 1 FROM session_events event
                         WHERE event.session_id=owner.session_id AND event.content_state='unsupported' AND CASE kind.kind
                           WHEN 'tool' THEN event.type IN ('tool.execution_start','PreToolUse')
                           WHEN 'subagent' THEN event.type IN ('subagent.started','SubagentStart')
                           WHEN 'error' THEN event.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR event.terminal_outcome='failed' OR {LocalWorkspaceProjectionStore.RecordedOtelFailurePredicate(connection, transaction, "event")}
                           ELSE 0 END) THEN 'source_unsupported'
                       WHEN kind.kind<>'retry' AND EXISTS(SELECT 1 FROM session_events event
                         WHERE event.session_id=owner.session_id AND CASE kind.kind
                           WHEN 'tool' THEN
                             event.type='tool.execution_start' AND event.source_adapter='copilot-sdk-stream'
                               AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                             OR event.type='PreToolUse' AND event.source_surface='claude-code'
                               AND event.source_adapter='claude-code-hook'
                               AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                               AND event.adapter_version IS NOT NULL AND trim(event.adapter_version)<>''
                               AND event.normalization_version IS NOT NULL AND trim(event.normalization_version)<>''
                               AND (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
                                 OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                                   AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')
                           WHEN 'subagent' THEN
                             event.type='subagent.started' AND event.source_adapter='copilot-sdk-stream'
                               AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                             OR event.type='SubagentStart' AND event.run_id IS NOT NULL
                               AND event.source_surface='claude-code' AND event.source_adapter='claude-code-hook'
                               AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                               AND event.adapter_version IS NOT NULL AND trim(event.adapter_version)<>''
                               AND event.normalization_version IS NOT NULL AND trim(event.normalization_version)<>''
                               AND (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
                                 OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                                   AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')
                           WHEN 'error' THEN event.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR event.terminal_outcome='failed' OR {LocalWorkspaceProjectionStore.RecordedOtelFailurePredicate(connection, transaction, "event")}
                           ELSE 0 END) THEN 'recorded'
                       ELSE 'not_observed' END state
              FROM sessions owner CROSS JOIN activity_kinds kind
              WHERE owner.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            base_activity AS (
              SELECT state.session_id,state.kind,state.state,
                     CASE WHEN state.state='recorded' THEN (
                       SELECT COUNT(DISTINCT event.event_id) FROM session_events event
                       WHERE event.session_id=state.session_id AND CASE state.kind
                         WHEN 'tool' THEN
                           event.type='tool.execution_start' AND event.source_adapter='copilot-sdk-stream'
                             AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                           OR event.type='PreToolUse' AND event.source_surface='claude-code'
                             AND event.source_adapter='claude-code-hook'
                             AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                             AND event.adapter_version IS NOT NULL AND trim(event.adapter_version)<>''
                             AND event.normalization_version IS NOT NULL AND trim(event.normalization_version)<>''
                             AND (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
                               OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                                 AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')
                         WHEN 'subagent' THEN
                           event.type='subagent.started' AND event.source_adapter='copilot-sdk-stream'
                             AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                           OR event.type='SubagentStart' AND event.run_id IS NOT NULL
                             AND event.source_surface='claude-code' AND event.source_adapter='claude-code-hook'
                             AND event.source_event_id IS NOT NULL AND trim(event.source_event_id)<>''
                             AND event.adapter_version IS NOT NULL AND trim(event.adapter_version)<>''
                             AND event.normalization_version IS NOT NULL AND trim(event.normalization_version)<>''
                             AND (event.source_application_version IS NOT NULL AND trim(event.source_application_version)<>''
                               OR length(event.schema_fingerprint)=64 AND event.schema_fingerprint=lower(event.schema_fingerprint)
                                 AND event.schema_fingerprint NOT GLOB '*[^0-9a-f]*')
                         WHEN 'error' THEN event.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR event.terminal_outcome='failed' OR {LocalWorkspaceProjectionStore.RecordedOtelFailurePredicate(connection, transaction, "event")}
                         ELSE 0 END) END count
              FROM activity_states state),
            expected_activity AS (
              SELECT base.session_id,base.kind,
                     CASE WHEN base.kind='retry' AND incomplete.session_id IS NOT NULL THEN incomplete.state
                          WHEN base.kind='retry' AND retry.session_id IS NOT NULL THEN 'recorded'
                          WHEN base.kind='tool' AND base.state='not_observed' AND tool.session_id IS NOT NULL THEN 'recorded'
                          ELSE base.state END state,
                     CASE WHEN base.kind='retry' AND incomplete.session_id IS NOT NULL THEN NULL
                          WHEN base.kind='retry' AND retry.session_id IS NOT NULL THEN retry.count
                          WHEN base.kind='tool' AND base.state='not_observed' AND tool.session_id IS NOT NULL THEN tool.count
                          ELSE base.count END count
              FROM base_activity base
              LEFT JOIN retry_incomplete incomplete ON incomplete.session_id=base.session_id AND base.kind='retry'
              LEFT JOIN retry_totals retry ON retry.session_id=base.session_id AND base.kind='retry'
              LEFT JOIN otel_tool_totals tool ON tool.session_id=base.session_id AND base.kind='tool'),
            actual_activity AS (
              SELECT session_id,kind,state,count FROM local_workspace_session_activity
              WHERE kind<>'skill' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            activity_drift AS (
              SELECT 1 FROM (SELECT session_id,kind,state,count FROM actual_activity EXCEPT SELECT session_id,kind,state,count FROM expected_activity)
              UNION ALL SELECT 1 FROM (SELECT session_id,kind,state,count FROM expected_activity EXCEPT SELECT session_id,kind,state,count FROM actual_activity)),
            session_drift AS (
              SELECT 1 FROM local_workspace_sessions projected
              LEFT JOIN expected_sessions expected ON expected.session_id=projected.session_id
              LEFT JOIN expected_labels label ON label.session_id=projected.session_id
              WHERE projected.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND (expected.session_id IS NULL
                  OR projected.sort_group IS NOT expected.sort_group
                  OR projected.sort_epoch_ms IS NOT expected.sort_epoch_ms
                  OR projected.status IS NOT expected.status
                  OR projected.completeness IS NOT expected.completeness
                  OR projected.source_state IS NOT expected.source_state
                  OR projected.model_state IS NOT expected.model_state
                  OR projected.timing_state IS NOT expected.timing_state
                  OR projected.started_at IS NOT expected.started_at
                  OR projected.ended_at IS NOT expected.ended_at
                  OR projected.last_seen_at IS NOT expected.last_seen_at
                  OR projected.last_seen_epoch_ms IS NOT expected.last_seen_epoch_ms
                  OR projected.duration_ms IS NOT expected.duration_ms
                  OR projected.capture_notes IS NOT expected.capture_notes
                  OR label.session_id IS NOT NULL AND (
                    projected.label_state IS NOT 'recorded'
                    OR projected.label_text IS NOT label.label_text
                    OR projected.label_source_identity IS NOT label.event_id
                    OR projected.label_expires_at IS NOT label.source_expires_at
                    OR projected.label_owner_revision IS NOT label.owner_revision
                    OR projected.instruction_count IS NOT label.instruction_count)
                  OR label.session_id IS NULL AND (
                    projected.label_state IS NOT expected.base_label_state
                    OR projected.label_text IS NOT NULL
                    OR projected.label_source_identity IS NOT NULL
                    OR projected.label_expires_at IS NOT NULL
                    OR projected.label_owner_revision IS NOT NULL
                    OR projected.instruction_count IS NOT NULL)
                  OR projected.revision_seed IS NOT (
                    expected.revision_base||CASE WHEN projected.label_state='recorded'
                      THEN '|'||projected.label_owner_revision ELSE '' END
                  ) COLLATE BINARY)),
            actual_sources AS (
              SELECT session_id,source FROM local_workspace_session_sources
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            source_drift AS (
              SELECT 1 FROM (SELECT session_id,source FROM actual_sources EXCEPT SELECT session_id,source FROM expected_sources)
              UNION ALL SELECT 1 FROM (SELECT session_id,source FROM expected_sources EXCEPT SELECT session_id,source FROM actual_sources)),
            actual_models AS (
              SELECT session_id,model FROM local_workspace_session_models
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            model_drift AS (
              SELECT 1 FROM (SELECT session_id,model FROM actual_models EXCEPT SELECT session_id,model FROM expected_models)
              UNION ALL SELECT 1 FROM (SELECT session_id,model FROM expected_models EXCEPT SELECT session_id,model FROM actual_models)),
            expected_tokens(
              session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,
              total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens) AS (
              SELECT run.session_id,run.run_id,'session_run',0,run.run_id,run.input_tokens,run.output_tokens,
                     run.total_tokens,NULL,NULL,NULL
              FROM session_runs run
              WHERE run.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              {llmSpanRows}),
            actual_tokens AS (
              SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,
                     total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens
              FROM local_workspace_token_observations
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            token_overflow AS (
              SELECT session_id FROM actual_tokens GROUP BY session_id HAVING COUNT(*)>4096
              UNION
              SELECT session_id FROM expected_tokens GROUP BY session_id HAVING COUNT(*)>4096),
            bounded_actual_tokens AS (
              SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM actual_tokens WHERE session_id NOT IN (SELECT session_id FROM token_overflow)),
            bounded_expected_tokens AS (
              SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM expected_tokens WHERE session_id NOT IN (SELECT session_id FROM token_overflow)),
            token_drift AS (
              SELECT 1 FROM (SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM bounded_actual_tokens EXCEPT SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM bounded_expected_tokens)
              UNION ALL
              SELECT 1 FROM (SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM bounded_expected_tokens EXCEPT SELECT session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens FROM bounded_actual_tokens))
            SELECT EXISTS(
              SELECT 1 FROM session_drift
              UNION ALL SELECT 1 FROM source_drift
              UNION ALL SELECT 1 FROM model_drift
              UNION ALL SELECT 1 FROM activity_drift
              UNION ALL SELECT 1 FROM search_drift
              UNION ALL SELECT 1 FROM token_overflow
              UNION ALL SELECT 1 FROM token_drift);
            """;
        command.Parameters.AddWithValue("$ids", ids);
        command.Parameters.AddWithValue("$now", now);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
    }

    private static async Task ValidateClosedOwnerBounds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ids,
        CancellationToken cancellationToken)
    {
        var skillOwnerQueries = new List<string>();
        if (TableExists(connection, transaction, "skill_projection_invocations"))
            skillOwnerQueries.Add("SELECT invocation_id source_identity FROM skill_projection_invocations WHERE session_id=owner.session_id");
        if (TableExists(connection, transaction, "skill_invocation_snapshots"))
            skillOwnerQueries.Add("SELECT snapshot_id FROM skill_invocation_snapshots WHERE session_id=owner.session_id");
        if (TableExists(connection, transaction, "skill_projection_sdk_claims"))
            skillOwnerQueries.Add("SELECT claim_id FROM skill_projection_sdk_claims WHERE session_id=owner.session_id");
        var skillOwnerOverflow = skillOwnerQueries.Count == 0
            ? "0"
            : $"EXISTS(SELECT 1 FROM ({string.Join(" UNION ALL ", skillOwnerQueries)}) LIMIT 1 OFFSET 4096)";
        var monitorSpanOverflow = HasOtelActivityOwnerSchema(connection, transaction)
            ? """
              EXISTS(SELECT 1 FROM monitor_spans span WHERE EXISTS(
                SELECT 1 FROM session_events event WHERE event.session_id=owner.session_id
                  AND event.source_adapter='otel-exact' COLLATE BINARY
                  AND event.type='otel.span' COLLATE BINARY
                  AND lower(event.trace_id)=lower(span.trace_id) COLLATE BINARY
                  AND lower(event.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)
                LIMIT 1 OFFSET 4096)
              """
            : "0";
        var tokenOwnerLlmRows = HasOtelTokenOwnerSchema(connection, transaction)
            ? """
              UNION ALL
              SELECT CAST(span.raw_record_id AS TEXT)||':'||CAST(span.span_ordinal AS TEXT)
              FROM session_events event
              JOIN monitor_spans span ON event.source_adapter='otel-exact' COLLATE BINARY
                AND event.type='otel.span' COLLATE BINARY
                AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                AND event.trace_id=span.trace_id COLLATE BINARY
              WHERE event.session_id=owner.session_id AND event.run_id IS NOT NULL
                AND span.category='llm_call' COLLATE BINARY
                AND (SELECT COUNT(*) FROM monitor_spans exact_owner
                  WHERE lower(exact_owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                    AND lower(exact_owner.span_id)=lower(span.span_id) COLLATE BINARY)=1
                AND (SELECT COUNT(*) FROM session_events exact_owner
                  WHERE exact_owner.source_adapter='otel-exact' COLLATE BINARY
                    AND exact_owner.type='otel.span' COLLATE BINARY
                    AND lower(exact_owner.trace_id)=lower(span.trace_id) COLLATE BINARY
                    AND lower(exact_owner.source_event_id)=lower(span.trace_id||'/'||span.span_id) COLLATE BINARY)=1
              """
            : string.Empty;
        var tokenOwnerOverflow = $"""
            EXISTS(SELECT 1 FROM (
              SELECT run_id source_identity FROM session_runs WHERE session_id=owner.session_id
              {tokenOwnerLlmRows}) LIMIT 1 OFFSET 4096)
            """;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH selected(session_id) AS (
              SELECT CAST(value AS TEXT) FROM json_each($ids))
            SELECT EXISTS(
              SELECT 1 FROM selected owner WHERE
                EXISTS(SELECT 1 FROM session_native_ids value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 4096)
                OR EXISTS(SELECT 1 FROM session_runs value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 4096)
                OR EXISTS(SELECT 1 FROM session_events value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 4096)
                OR EXISTS(SELECT 1 FROM local_workspace_session_sources value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 5)
                OR EXISTS(SELECT 1 FROM local_workspace_session_models value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 16)
                OR EXISTS(SELECT 1 FROM local_workspace_session_activity value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 5)
                OR EXISTS(SELECT 1 FROM local_workspace_token_observations value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 4096)
                OR EXISTS(SELECT 1 FROM local_workspace_session_search_facts value WHERE value.session_id=owner.session_id LIMIT 1 OFFSET 4096)
                OR {skillOwnerOverflow}
                OR {monitorSpanOverflow}
                OR {tokenOwnerOverflow});
            """;
        command.Parameters.AddWithValue("$ids", ids);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new LocalWorkspaceSessionDetailException("workspace_too_large");
    }

    private static bool HasOtelTokenOwnerSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)=10 FROM pragma_table_info('monitor_spans')
            WHERE name IN ('raw_record_id','span_ordinal','trace_id','span_id','category','input_tokens',
                           'output_tokens','reasoning_tokens','cache_read_tokens','cache_creation_tokens');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasOtelActivityOwnerSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)=6 FROM pragma_table_info('monitor_spans')
            WHERE name IN ('raw_record_id','span_ordinal','trace_id','span_id','operation','category');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        params string[] columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var present = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) present.Add(reader.GetString(0));
        return columns.All(present.Contains);
    }

    private static bool ValidClosedRow(LocalWorkspaceProjectionRow row)
    {
        if (row.SortGroup is not (0 or 1)
            || row.SortGroup == 1 && row.SortEpochMilliseconds != 0
            || row.LabelState is not ("recorded" or "not_observed" or "not_captured" or "expired")
            || (row.LabelState == "recorded") != (row.LabelText is not null)
            || row.LabelText is not null && !ValidLabel(row.LabelText)
            || row.Status is not ("active" or "completed" or "failed" or "unknown")
            || row.Completeness is not ("unbound" or "partial" or "rich" or "full")
            || !ValidSet(row.Sources, 5) || !ValidSet(row.Models, 16)
            || !ValidCount(row.Activity.Skill) || !ValidCount(row.Activity.Tool)
            || !ValidCount(row.Activity.Subagent) || !ValidCount(row.Activity.Error)
            || !ValidCount(row.Activity.Retry) || !ValidTokens(row.Tokens)
            || !ValidTiming(row) || !ValidCaptureNotes(row.CaptureNotes)
            || string.IsNullOrEmpty(row.RevisionSeed))
            return false;
        return true;

        static bool ValidLabel(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && !value.EnumerateRunes().Any(static rune => rune.Value is '\r' or '\n' or 0x2028 or 0x2029)
            && value.EnumerateRunes().Take(161).Count() <= 160;

        static bool ValidSet(LocalWorkspaceSetFact? fact, int maximum)
        {
            if (fact?.Values is null
                || fact.State is not ("recorded" or "not_observed" or "projection_invalid")
                || (fact.State == "recorded") != (fact.Values.Count > 0)
                || fact.Values.Count > maximum)
                return false;
            string? previous = null;
            foreach (var value in fact.Values)
            {
                if (string.IsNullOrWhiteSpace(value)
                    || previous is not null && StringComparer.Ordinal.Compare(previous, value) >= 0)
                    return false;
                previous = value;
            }
            return true;
        }

        static bool ValidCount(LocalWorkspaceFact<long>? fact) =>
            fact is not null && ValidFactState(fact.State)
            && (fact.State == "recorded") == fact.Value.HasValue
            && fact.Value is null or >= 0;

        static bool ValidValue(LocalWorkspaceFact<long>? fact, long maximum = long.MaxValue) =>
            ValidCount(fact) && (fact!.Value is null || fact.Value <= maximum);

        static bool ValidFactState(string state) => state is
            "recorded" or "not_observed" or "source_unsupported" or "capture_gap" or
            "certification_pending" or "not_captured" or "expired" or "redacted" or
            "malformed" or "oversized" or "inconsistent" or "projection_invalid";

        static bool ValidCaptureNotes(IReadOnlyList<string> notes)
        {
            if (notes.Count > 16) return false;
            string? previous = null;
            foreach (var note in notes)
            {
                if (note is not ("raw_content_not_captured" or "raw_content_expired" or "source_unsupported" or
                    "capture_gap" or "certification_pending" or "projection_invalid" or "token_inconsistent" or
                    "cache_inconsistent")
                    || previous is not null && StringComparer.Ordinal.Compare(previous, note) >= 0)
                    return false;
                previous = note;
            }
            return true;
        }

        static bool ValidTokens(LocalWorkspaceTokenFacts? tokens)
        {
            if (tokens is null
                || tokens.Authority is not ("session_run" or "llm_span" or "mixed" or "none")
                || !ValidFactState(tokens.State) || !tokens.HasValidObservations()
                || tokens.AvailableExecutionCount < 0 || tokens.TotalExecutionCount < 0
                || tokens.AvailableExecutionCount > tokens.TotalExecutionCount
                || !ValidValue(tokens.Input) || !ValidValue(tokens.Output) || !ValidValue(tokens.Total)
                || !ValidValue(tokens.Reasoning) || !ValidValue(tokens.CacheRead)
                || !ValidValue(tokens.CacheCreation) || !ValidValue(tokens.NewInput)
                || !ValidValue(tokens.CacheReadRatioBasisPoints, 10_000))
                return false;
            if (tokens.Input is { State: "recorded", Value: not null } input
                && tokens.CacheRead is { State: "recorded", Value: not null } cache)
            {
                if (cache.Value > input.Value)
                    return tokens.State == "inconsistent"
                        && tokens.NewInput is { State: "inconsistent", Value: null }
                        && tokens.CacheReadRatioBasisPoints is { State: "inconsistent", Value: null };
                if (tokens.NewInput.State == "recorded" && tokens.NewInput.Value != input.Value - cache.Value)
                    return false;
                if (tokens.CacheReadRatioBasisPoints.State == "recorded"
                    && (input.Value == 0
                        || tokens.CacheReadRatioBasisPoints.Value != (long)((BigInteger)cache.Value * 10_000 / input.Value)))
                    return false;
            }
            return true;
        }

        static bool ValidTiming(LocalWorkspaceProjectionRow value)
        {
            if (value.TimingState is not ("recorded" or "not_observed" or "inconsistent")
                || !TryCanonical(value.StartedAt, out var started)
                || !TryCanonical(value.EndedAt, out var ended)
                || !TryCanonical(value.LastSeenAt, out var lastSeen)
                || (lastSeen is null) != (value.LastSeenEpochMilliseconds is null)
                || lastSeen is not null && value.LastSeenEpochMilliseconds != lastSeen.Value.ToUnixTimeMilliseconds()
                || value.DurationMilliseconds is < 0)
                return false;
            if (value.TimingState == "not_observed")
                return started is null && ended is null && value.DurationMilliseconds is null;
            if (value.TimingState == "inconsistent")
                return value.DurationMilliseconds is null;
            if (started is null || lastSeen is null)
                return false;
            if ((ended is null) != (value.DurationMilliseconds is null)
                || value.Status == "active" && ended is not null
                || value.Status == "completed" && ended is null)
                return false;
            if (ended is null)
                return true;
            return ended >= started
                && value.DurationMilliseconds == ended.Value.ToUnixTimeMilliseconds() - started.Value.ToUnixTimeMilliseconds();
        }

        static bool TryCanonical(string? text, out DateTimeOffset? value)
        {
            value = null;
            if (text is null) return true;
            if (!DateTimeOffset.TryParseExact(text, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed)
                || parsed.Offset != TimeSpan.Zero
                || parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture) != text)
                return false;
            value = parsed;
            return true;
        }
    }

    private static TokenObservation SelectCallTokens(IReadOnlyList<TokenObservation> observations)
    {
        var authorities = new HashSet<string>(StringComparer.Ordinal);
        long? Select(Func<TokenObservation, long?> component)
        {
            foreach (var observation in observations)
            {
                if (component(observation) is not { } value) continue;
                authorities.Add(observation.Authority);
                return value;
            }
            return null;
        }
        var input = Select(row => row.Input); var output = Select(row => row.Output);
        var total = Select(row => row.Total); var reasoning = Select(row => row.Reasoning);
        var cache = Select(row => row.CacheRead); var creation = Select(row => row.CacheCreation);
        var authority = authorities.Count == 0 ? "none" : authorities.Count == 1 ? authorities.Single() : "mixed";
        return new(authority, input, output, total, reasoning, cache, creation);
    }

    internal static LocalWorkspaceTokenFacts AggregateCalls(IEnumerable<LocalWorkspaceTokenFacts> calls) =>
        ReadTokens(calls.Select(value => new TokenObservation(value.Authority, value.Input.Value, value.Output.Value,
            value.Total.Value, value.Reasoning.Value, value.CacheRead.Value, value.CacheCreation.Value)).ToArray());

    internal static LocalWorkspaceTokenFacts MergeCallTokens(params LocalWorkspaceTokenFacts[] sources) =>
        ReadTokens([SelectCallTokens(sources.Select(value => new TokenObservation(value.Authority,
            value.Input.Value, value.Output.Value, value.Total.Value, value.Reasoning.Value, value.CacheRead.Value, value.CacheCreation.Value)).ToArray())]);

    private static LocalWorkspaceTokenFacts ReadTokens(IReadOnlyList<TokenObservation> rows)
    {
        var total = rows.Count; var available = rows.Count(r => r.Values.Any(v => v is not null));
        var authorities = rows.Select(r => r.Authority).Where(authority => authority != "none").Distinct(StringComparer.Ordinal).ToArray();
        var authority = authorities.Length == 0 ? "none" : authorities.Length == 1 ? authorities[0] : "mixed";
        var overflow = false;
        LocalWorkspaceFact<long> Fact(Func<TokenObservation, long?> select)
        {
            var values = rows.Select(select).ToArray(); var count = values.Count(v => v is not null);
            if (count == 0) return new("not_observed", null); if (count != total) return new("capture_gap", null);
            try { long sum = 0; foreach (var value in values) sum = checked(sum + value!.Value); return new("recorded", sum); }
            catch (OverflowException) { overflow = true; return new("oversized", null); }
        }
        var input = Fact(r => r.Input); var output = Fact(r => r.Output); var producerTotal = Fact(r => r.Total); var reasoning = Fact(r => r.Reasoning); var cache = Fact(r => r.CacheRead); var creation = Fact(r => r.CacheCreation);
        var inconsistent = input.State == "recorded" && cache.State == "recorded" && cache.Value > input.Value;
        var componentGap = new[] { input, output, producerTotal, reasoning, cache, creation }.Any(f => f.State == "capture_gap");
        var overall = overflow ? "oversized" : inconsistent ? "inconsistent" : available == 0 ? "not_observed" : available < total || componentGap ? "capture_gap" : "recorded";
        LocalWorkspaceFact<long> derived;
        if (inconsistent) derived = new("inconsistent", null); else if (input.State != "recorded" || cache.State != "recorded") derived = new(input.State == "capture_gap" || cache.State == "capture_gap" ? "capture_gap" : "not_observed", null); else derived = new("recorded", input.Value!.Value - cache.Value!.Value);
        LocalWorkspaceFact<long> ratio;
        if (inconsistent) ratio = new("inconsistent", null); else if (input.State != "recorded" || cache.State != "recorded" || input.Value == 0) ratio = new(input.State == "capture_gap" || cache.State == "capture_gap" ? "capture_gap" : "not_observed", null); else { var value = (BigInteger)cache.Value!.Value * 10_000 / input.Value!.Value; ratio = value > long.MaxValue ? new("oversized", null) : new("recorded", (long)value); }
        LocalWorkspaceTokenObservationFact Observed(Func<TokenObservation, long?> select)
        {
            var values = rows.Select(select).Where(value => value.HasValue).ToArray();
            LocalWorkspaceFact<long> subtotal;
            try { subtotal = values.Length == 0 ? new("not_observed", null) : new("recorded", values.Aggregate(0L, (sum, value) => checked(sum + value!.Value))); }
            catch (OverflowException) { subtotal = new("oversized", null); }
            return new(subtotal, values.Length, total, null);
        }
        var observations = new Dictionary<string, LocalWorkspaceTokenObservationFact>(StringComparer.Ordinal)
        {
            ["input"] = Observed(row => row.Input), ["output"] = Observed(row => row.Output),
            ["total"] = Observed(row => row.Total), ["reasoning"] = Observed(row => row.Reasoning),
            ["cache_read"] = Observed(row => row.CacheRead), ["cache_creation"] = Observed(row => row.CacheCreation),
        };
        var pairs = rows.Where(row => row.Input.HasValue && row.CacheRead.HasValue).ToArray();
        LocalWorkspaceFact<long> observedRatio;
        long? pairedInput = null;
        try
        {
            pairedInput = pairs.Aggregate(0L, (sum, row) => checked(sum + row.Input!.Value));
            var pairedCache = pairs.Aggregate(0L, (sum, row) => checked(sum + row.CacheRead!.Value));
            observedRatio = pairs.Any(row => row.CacheRead > row.Input) ? new("inconsistent", null)
                : pairedInput == 0 ? new("not_observed", null)
                : new("recorded", (long)(((BigInteger)pairedCache * 10_000 + pairedInput.Value / 2) / pairedInput.Value));
        }
        catch (OverflowException) { observedRatio = new("oversized", null); pairedInput = null; }
        observations["cache_read_ratio_basis_points"] = new(observedRatio, pairs.Length, total, pairs.Length == 0 ? null : pairedInput);
        return new(authority, overall, available, total, input, output, producerTotal, reasoning, cache, creation, derived, ratio) { Observations = observations };
    }

    private sealed record TokenObservation(string Authority, long? Input, long? Output, long? Total, long? Reasoning, long? CacheRead, long? CacheCreation)
    { internal IEnumerable<long?> Values => [Input, Output, Total, Reasoning, CacheRead, CacheCreation]; }

    private static long? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ReadPairs(SqliteConnection connection, SqliteTransaction transaction, string name, string sql, string ids, Dictionary<string, MutableRow> rows, Action<MutableRow, string> add, Action<string>? statementObserver, CancellationToken token, string? now = null)
    {
        statementObserver?.Invoke(name);
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) if (rows.TryGetValue(reader.GetString(0), out var row)) add(row, reader.GetString(1));
    }

    private sealed class MutableRow
    {
        private readonly long sortGroup;
        private readonly long sortEpoch;
        private readonly string labelState;
        private readonly string? label;
        private readonly string status;
        private readonly string completeness;
        private readonly string sourceState;
        private readonly string modelState;
        private readonly string timingState;
        private readonly string? startedAt;
        private readonly string? endedAt;
        private readonly string? lastSeenAt;
        private readonly long? lastSeenEpoch;
        private readonly long? duration;
        private readonly string notes;
        private readonly string revision;

        internal MutableRow(string sessionId, long sortGroup, long sortEpoch, string labelState, string? label, string status, string completeness, string sourceState, string modelState, string timingState, string? startedAt, string? endedAt, string? lastSeenAt, long? lastSeenEpoch, long? duration, string notes, string revision)
        {
            SessionId = sessionId; this.sortGroup = sortGroup; this.sortEpoch = sortEpoch;
            this.labelState = labelState; this.label = label; this.status = status; this.completeness = completeness; this.sourceState = sourceState; this.modelState = modelState;
            this.timingState = timingState; this.startedAt = startedAt; this.endedAt = endedAt; this.lastSeenAt = lastSeenAt; this.lastSeenEpoch = lastSeenEpoch;
            this.duration = duration; this.notes = notes; this.revision = revision;
        }

        internal string SessionId { get; }
        internal List<string> Sources { get; } = [];
        internal List<string> Models { get; } = [];
        internal List<string> SearchTexts { get; } = [];
        internal List<string> AdditionalCaptureNotes { get; } = [];
        internal Dictionary<string, LocalWorkspaceFact<long>> Activity { get; } = new(StringComparer.Ordinal);
        internal bool HasClosedActivityFamilies =>
            Activity.Count == 5
            && Activity.ContainsKey("skill")
            && Activity.ContainsKey("tool")
            && Activity.ContainsKey("subagent")
            && Activity.ContainsKey("error")
            && Activity.ContainsKey("retry");
        internal LocalWorkspaceFact<long>? CurrentSkillFilter { get; set; }
        internal LocalWorkspaceTokenFacts? TokenAggregate { get; set; }
        internal LocalWorkspaceProjectionRow Freeze()
        {
            LocalWorkspaceFact<long> A(string kind) => Activity.TryGetValue(kind, out var fact) ? fact : new("not_observed", null);
            var tokens = TokenAggregate ?? new("none", "not_observed", 0, 0, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null));
            return new LocalWorkspaceProjectionRow(SessionId, sortGroup, sortEpoch, labelState, label, status, completeness,
                new(sourceState, Array.AsReadOnly(Sources.ToArray())), new(modelState, Array.AsReadOnly(Models.ToArray())), new(A("skill"), A("tool"), A("subagent"), A("error"), A("retry")), tokens,
                timingState, startedAt, endedAt, lastSeenAt, lastSeenEpoch, duration, Array.AsReadOnly((notes.Length == 0 ? [] : notes.Split(',')).Concat(AdditionalCaptureNotes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(SearchTexts.ToArray()), revision)
            {
                CurrentSkillFilter = CurrentSkillFilter,
            };
        }
    }
}

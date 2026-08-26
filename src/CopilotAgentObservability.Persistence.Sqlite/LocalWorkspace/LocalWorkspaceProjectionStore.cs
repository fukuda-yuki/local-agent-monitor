using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionStore
{
    internal static void Refresh(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        var ids = ReadSessionIds(connection, transaction);
        RefreshSessions(connection, transaction, ids, now, skillRegistryAuthority);
        Execute(connection, transaction, "DELETE FROM local_workspace_sessions WHERE session_id NOT IN (SELECT session_id FROM sessions);");
    }

    internal static void RefreshSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now, ISkillRegistryGenerationAuthority skillRegistryAuthority)
        => RefreshSessionsCore(connection, transaction, sessionIds, now, skillRegistryAuthority);

    internal static void RefreshStructural(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        var ids = ReadSessionIds(connection, transaction);
        RefreshSessionsStructural(connection, transaction, ids, now);
        Execute(connection, transaction, "DELETE FROM local_workspace_sessions WHERE session_id NOT IN (SELECT session_id FROM sessions);");
    }

    internal static void RefreshSessionsStructural(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now)
        => RefreshSessionsCore(connection, transaction, sessionIds, now, null);

    private static void RefreshSessionsCore(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now, ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        RegisterProjectionFunctions(connection);
        var idsJson = JsonSerializer.Serialize(sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        ExecuteWithIds(connection, transaction, """
            DELETE FROM local_workspace_token_observations WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_activity WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_models WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_sources WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_search_facts WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_sessions WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_sessions(session_id,sort_group,sort_epoch_ms,label_state,label_text,label_source_identity,label_expires_at,status,completeness,source_state,model_state,timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed)
            SELECT s.session_id,CASE WHEN COALESCE(local_workspace_epoch(s.started_at),local_workspace_epoch(s.created_at),local_workspace_epoch(s.last_seen_at)) IS NULL THEN 1 ELSE 0 END,
                   COALESCE(local_workspace_epoch(s.started_at),local_workspace_epoch(s.created_at),local_workspace_epoch(s.last_seen_at),0),
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'expired' WHEN 'not_captured' THEN 'not_captured' ELSE 'not_observed' END,
                   NULL,NULL,NULL,s.status,s.completeness,'not_observed','not_observed',
                   CASE WHEN local_workspace_epoch(s.started_at) IS NOT NULL AND local_workspace_epoch(s.ended_at)>=local_workspace_epoch(s.started_at) THEN 'recorded' WHEN local_workspace_epoch(s.started_at) IS NULL AND local_workspace_epoch(s.ended_at) IS NULL THEN 'not_observed' ELSE 'inconsistent' END,
                   local_workspace_canonical(s.started_at),local_workspace_canonical(s.ended_at),local_workspace_canonical(s.last_seen_at),local_workspace_epoch(s.last_seen_at),CASE WHEN local_workspace_epoch(s.started_at) IS NOT NULL AND local_workspace_epoch(s.ended_at)>=local_workspace_epoch(s.started_at) THEN local_workspace_epoch(s.ended_at)-local_workspace_epoch(s.started_at) END,
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'raw_content_expired' WHEN 'not_captured' THEN 'raw_content_not_captured' ELSE '' END,
                   s.status||'|'||s.completeness||'|'||COALESCE(local_workspace_canonical(s.started_at),'')||'|'||COALESCE(local_workspace_canonical(s.ended_at),'')||'|'||COALESCE(local_workspace_canonical(s.last_seen_at),'')
            FROM sessions s WHERE s.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_session_sources
              WITH values_by_session AS (
                SELECT session_id,source_surface source FROM session_native_ids WHERE source_surface IS NOT NULL AND trim(source_surface)<>''
                UNION SELECT session_id,source_surface FROM session_runs WHERE source_surface IS NOT NULL AND trim(source_surface)<>''
                UNION SELECT session_id,source_surface FROM session_events WHERE source_surface IS NOT NULL AND trim(source_surface)<>''),
              ranked AS (SELECT session_id,source,row_number() OVER(PARTITION BY session_id ORDER BY source COLLATE BINARY) ordinal FROM values_by_session)
              SELECT session_id,source FROM ranked WHERE ordinal<=5 AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_session_models
              WITH distinct_models AS (SELECT DISTINCT session_id,model FROM session_runs WHERE model IS NOT NULL AND trim(model)<>''),
              ranked AS (SELECT session_id,model,row_number() OVER(PARTITION BY session_id ORDER BY model COLLATE BINARY) ordinal FROM distinct_models)
              SELECT session_id,model FROM ranked WHERE ordinal<=16 AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            UPDATE local_workspace_sessions SET
              source_state=CASE WHEN (SELECT COUNT(DISTINCT source) FROM (SELECT session_id,source_surface source FROM session_native_ids UNION ALL SELECT session_id,source_surface FROM session_runs UNION ALL SELECT session_id,source_surface FROM session_events) x WHERE x.session_id=local_workspace_sessions.session_id AND source IS NOT NULL)>5 THEN 'projection_invalid' WHEN EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=local_workspace_sessions.session_id) THEN 'recorded' ELSE 'not_observed' END,
              model_state=CASE WHEN (SELECT COUNT(DISTINCT model) FROM session_runs x WHERE x.session_id=local_workspace_sessions.session_id AND model IS NOT NULL AND trim(model)<>'')>16 THEN 'projection_invalid' WHEN EXISTS(SELECT 1 FROM local_workspace_session_models x WHERE x.session_id=local_workspace_sessions.session_id) THEN 'recorded' ELSE 'not_observed' END
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_sources WHERE session_id IN (SELECT session_id FROM local_workspace_sessions WHERE source_state='projection_invalid');
            DELETE FROM local_workspace_session_models WHERE session_id IN (SELECT session_id FROM local_workspace_sessions WHERE model_state='projection_invalid');
            UPDATE local_workspace_sessions SET capture_notes=CASE WHEN source_state='projection_invalid' OR model_state='projection_invalid' THEN CASE WHEN capture_notes='' THEN 'projection_invalid' ELSE 'projection_invalid,'||capture_notes END ELSE capture_notes END
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_session_activity SELECT s.session_id,k.kind,'not_observed',NULL FROM sessions s
              CROSS JOIN (SELECT 'skill' kind UNION ALL SELECT 'tool' UNION ALL SELECT 'subagent' UNION ALL SELECT 'error' UNION ALL SELECT 'retry') k
              WHERE s.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            UPDATE local_workspace_session_activity AS a SET
              state=CASE WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND (e.content_state='unsupported' OR e.status='gap_before_capture')) THEN CASE WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND e.status='gap_before_capture') THEN 'capture_gap' ELSE 'source_unsupported' END WHEN a.kind<>'retry' AND (EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND CASE a.kind WHEN 'tool' THEN e.type IN ('tool.execution_start','PreToolUse') WHEN 'subagent' THEN e.type IN ('subagent.started','SubagentStart') WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) OR (SELECT completeness FROM sessions WHERE session_id=a.session_id)='full') THEN 'recorded' ELSE 'not_observed' END,
              count=CASE WHEN NOT EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND (e.content_state='unsupported' OR e.status='gap_before_capture')) AND (a.kind<>'retry') AND (EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND CASE a.kind WHEN 'tool' THEN e.type IN ('tool.execution_start','PreToolUse') WHEN 'subagent' THEN e.type IN ('subagent.started','SubagentStart') WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) OR (SELECT completeness FROM sessions WHERE session_id=a.session_id)='full') THEN (SELECT COUNT(DISTINCT e.event_id) FROM session_events e WHERE e.session_id=a.session_id AND CASE a.kind WHEN 'tool' THEN e.type IN ('tool.execution_start','PreToolUse') WHEN 'subagent' THEN e.type IN ('subagent.started','SubagentStart') WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) END
              WHERE a.kind IN ('tool','subagent','error','retry');
            INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
              SELECT session_id,run_id,'session_run',0,run_id,input_tokens,output_tokens,total_tokens,NULL,NULL,NULL FROM session_runs
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);
        if (TableExists(connection, transaction, "monitor_spans"))
        {
            ExecuteWithIds(connection, transaction, $"""
                INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
                SELECT e.session_id,e.run_id,'llm_span',1,CAST(ms.raw_record_id AS TEXT)||':'||CAST(ms.span_ordinal AS TEXT),ms.input_tokens,ms.output_tokens,f.producer_total_tokens,ms.reasoning_tokens,ms.cache_read_tokens,ms.cache_creation_tokens
                FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY LEFT JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                WHERE e.run_id IS NOT NULL AND ms.category='llm_call' AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson);
            ExecuteWithIds(connection, transaction, """
                WITH exact_spans AS (
                  SELECT DISTINCT e.session_id,e.run_id,ms.raw_record_id,ms.span_ordinal,f.retry_count
                  FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                  WHERE e.run_id IS NOT NULL AND ms.operation='chat' COLLATE BINARY AND f.retry_count IS NOT NULL AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
                totals AS (SELECT session_id,SUM(retry_count) retry_count FROM exact_spans GROUP BY session_id)
                UPDATE local_workspace_session_activity AS a SET state='recorded',count=t.retry_count FROM totals t WHERE a.session_id=t.session_id AND a.kind='retry' AND a.state='not_observed';
                """, idsJson);
        }
        ApplyLabels(connection, transaction, idsJson, now);
        ApplySearchFacts(connection, transaction, idsJson, now, skillRegistryAuthority);
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO local_workspace_projection_state(projector_key,session_frontier,refreshed_at) VALUES('local-workspace-projection-v1',(SELECT MAX(updated_at) FROM sessions),$now) ON CONFLICT(projector_key) DO UPDATE SET session_frontier=excluded.session_frontier,refreshed_at=excluded.refreshed_at;";
        state.Parameters.AddWithValue("$now", Canonical(now)); state.ExecuteNonQuery();
    }

    private static void ApplyLabels(SqliteConnection connection, SqliteTransaction transaction, string idsJson, DateTimeOffset now)
    {
        connection.CreateFunction<string, string, string?>("local_workspace_label", static (type, json) => TryReadInstruction(type, json, out var text) ? text : null, isDeterministic: true);
        connection.CreateFunction<string, string?>("local_workspace_search", static text => text is null ? null : Search(text), isDeterministic: true);
        connection.CreateFunction<string, string, long>("local_workspace_future", static (expiry, instant) =>
            TryCanonicalFuture(expiry, DateTimeOffset.ParseExact(instant, "O", CultureInfo.InvariantCulture)) ? 1 : 0, isDeterministic: true);
        ExecuteWithIds(connection, transaction, """
            WITH candidates AS (
              SELECT e.session_id,e.event_id,c.expires_at,local_workspace_label(e.type,c.content_json) label_text,e.occurred_at
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted') AND e.content_state='available'
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND local_workspace_future(c.expires_at,$now)=1),
            ranked AS (SELECT *,row_number() OVER(PARTITION BY session_id ORDER BY occurred_at COLLATE BINARY,event_id COLLATE BINARY) ordinal FROM candidates WHERE label_text IS NOT NULL)
            UPDATE local_workspace_sessions AS p SET
              label_state='recorded',label_text=r.label_text,
              label_source_identity=r.event_id,label_expires_at=r.expires_at,
              revision_seed=p.revision_seed||'|'||r.event_id||'|'||r.expires_at
            FROM ranked r WHERE r.ordinal=1 AND r.session_id=p.session_id;
            """, idsJson, Canonical(now));
    }

    private static void ApplySearchFacts(SqliteConnection connection, SqliteTransaction transaction, string idsJson, DateTimeOffset now, ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        ExecuteWithIds(connection, transaction, """
            INSERT INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
            SELECT session_id,'label',label_source_identity,local_workspace_search(label_text),label_expires_at
            FROM local_workspace_sessions WHERE label_state='recorded' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);
        var sessionIds = JsonSerializer.Deserialize<string[]>(idsJson) ?? [];
        foreach (var fact in SkillProjectionReadService.ReadCurrentOtelSearchFacts(connection, transaction, sessionIds, now))
            InsertSkillSearchFact(connection, transaction, fact.SessionId, "otel:" + fact.SourceIdentity, fact.SkillName, fact.ExpiresAt);
        if (skillRegistryAuthority is not null)
            foreach (var fact in SkillProjectionReadService.ReadCurrentSdkSearchFacts(connection, transaction, sessionIds, skillRegistryAuthority, new ProjectionTimeProvider(now)))
                InsertSkillSearchFact(connection, transaction, fact.SessionId, "sdk:" + fact.SourceIdentity, fact.SkillName, fact.ExpiresAt);
        if (TableExists(connection, transaction, "raw_records") && TableExists(connection, transaction, "monitor_spans") && TableExists(connection, transaction, "retention_items") && ColumnExists(connection, transaction, "monitor_spans", "tool_name"))
            ExecuteWithIds(connection, transaction, """
                INSERT OR IGNORE INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
                SELECT e.session_id,'tool',CAST(m.raw_record_id AS TEXT)||':'||CAST(m.span_ordinal AS TEXT),local_workspace_search(m.tool_name),i.expires_at
                FROM session_events e JOIN monitor_spans m ON e.source_adapter='otel-exact' AND e.source_event_id=m.trace_id||'/'||m.span_id
                JOIN raw_records r ON r.id=m.raw_record_id
                JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(m.raw_record_id AS TEXT)
                WHERE m.operation='execute_tool' COLLATE BINARY AND m.category='tool_call' COLLATE BINARY
                  AND m.tool_name IS NOT NULL AND length(m.tool_name)>0 AND i.state IN ('expiring','retained_by_policy')
                  AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                  AND (i.state='retained_by_policy' OR i.expires_at COLLATE BINARY > $now COLLATE BINARY)
                  AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson, Canonical(now));
    }

    private static void InsertSkillSearchFact(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string sourceIdentity,
        string skillName,
        string? expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at) VALUES($session,'skill',$source,local_workspace_search($name),$expires_at);";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$source", sourceIdentity);
        command.Parameters.AddWithValue("$name", skillName);
        command.Parameters.AddWithValue("$expires_at", (object?)expiresAt ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void RegisterProjectionFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, long?>("local_workspace_epoch", static value => TryInstant(value, out var instant) ? instant.ToUnixTimeMilliseconds() : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_canonical", static value => TryInstant(value, out var instant) ? Canonical(instant) : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_search", static value => value is null ? null : Search(value), isDeterministic: true);
    }

    private static bool TryInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out instant);

    private static bool TryReadInstruction(string type, string json, out string text)
    {
        text = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var property = type == "user.message" ? "value" : "prompt";
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return false;
            var builder = new StringBuilder(); var previousSpace = false;
            foreach (var rune in value.GetString()!.EnumerateRunes())
            {
                if (rune.Value is '\r' or '\n' or 0x0085 or 0x2028 or 0x2029 || Rune.IsWhiteSpace(rune)) { if (builder.Length != 0 && !previousSpace) builder.Append(' '); previousSpace = true; continue; }
                builder.Append(rune); previousSpace = false;
            }
            var normalized = builder.ToString().Trim(); if (normalized.Length == 0) return false;
            text = string.Concat(normalized.EnumerateRunes().Take(160).Select(static rune => rune.ToString())); return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Search(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private sealed class ProjectionTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    internal static string CanonicalizeCaptureNotes(IEnumerable<string> values)
    {
        var allowed = new HashSet<string>(["raw_content_not_captured", "raw_content_expired", "source_unsupported", "capture_gap", "certification_pending", "projection_invalid", "token_inconsistent", "cache_inconsistent"], StringComparer.Ordinal);
        var notes = values.ToArray();
        if (notes.Length > 16 || notes.Any(note => !allowed.Contains(note))) throw new InvalidOperationException("local_workspace_capture_notes_invalid");
        return string.Join(',', notes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }
    private static bool TryCanonicalFuture(string value, DateTimeOffset now) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero && parsed > now;
    private static string Canonical(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string[] ReadSessionIds(SqliteConnection connection, SqliteTransaction transaction) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;"; using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray(); }
    private static void ExecuteWithIds(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids, string? now = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name", name); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0; }
    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string name) { using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=$"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name=$name);"; command.Parameters.AddWithValue("$name",name); return Convert.ToInt64(command.ExecuteScalar(),CultureInfo.InvariantCulture)!=0; }
}

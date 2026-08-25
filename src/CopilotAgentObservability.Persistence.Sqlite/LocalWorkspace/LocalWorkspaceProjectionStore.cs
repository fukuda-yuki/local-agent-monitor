using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionStore
{
    internal static void Refresh(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        var ids = ReadSessionIds(connection, transaction);
        RefreshSessions(connection, transaction, ids, now);
        Execute(connection, transaction, "DELETE FROM local_workspace_sessions WHERE session_id NOT IN (SELECT session_id FROM sessions);");
    }

    internal static void RefreshSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now)
    {
        if (sessionIds.Count == 0) return;
        var idsJson = JsonSerializer.Serialize(sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        ExecuteWithIds(connection, transaction, """
            DELETE FROM local_workspace_token_observations WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_activity WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_models WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_sources WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_sessions WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_sessions(session_id,sort_group,sort_epoch_ms,label_state,label_text,label_search_text,label_source_identity,label_expires_at,status,completeness,source_state,model_state,timing_state,started_at,ended_at,duration_ms,capture_notes,revision_seed)
            SELECT s.session_id,CASE WHEN s.started_at IS NULL THEN 2 WHEN s.ended_at IS NULL THEN 0 ELSE 1 END,
                   MAX(0,CAST((julianday(COALESCE(s.started_at,s.last_seen_at))-2440587.5)*86400000 AS INTEGER)),
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'expired' WHEN 'not_captured' THEN 'not_captured' ELSE 'not_observed' END,
                   NULL,NULL,NULL,NULL,s.status,s.completeness,'not_observed','not_observed',
                   CASE WHEN s.started_at IS NOT NULL AND s.ended_at IS NOT NULL AND julianday(s.ended_at)>=julianday(s.started_at) THEN 'recorded' WHEN s.started_at IS NULL AND s.ended_at IS NULL THEN 'not_observed' ELSE 'inconsistent' END,
                   s.started_at,s.ended_at,CASE WHEN s.started_at IS NOT NULL AND s.ended_at IS NOT NULL AND julianday(s.ended_at)>=julianday(s.started_at) THEN MAX(0,CAST((julianday(s.ended_at)-julianday(s.started_at))*86400000 AS INTEGER)) END,
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'raw_content_expired' WHEN 'not_captured' THEN 'raw_content_not_captured' ELSE '' END,
                   s.status||'|'||s.completeness||'|'||COALESCE(s.started_at,'')||'|'||COALESCE(s.ended_at,'')||'|'||s.last_seen_at
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
            UPDATE local_workspace_sessions SET capture_notes=CASE WHEN source_state='projection_invalid' OR model_state='projection_invalid' THEN CASE WHEN capture_notes='' THEN 'projection_invalid' ELSE capture_notes||',projection_invalid' END ELSE capture_notes END
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_session_activity SELECT s.session_id,k.kind,'not_observed',NULL FROM sessions s
              CROSS JOIN (SELECT 'skill' kind UNION ALL SELECT 'tool' UNION ALL SELECT 'subagent' UNION ALL SELECT 'error' UNION ALL SELECT 'retry') k
              WHERE s.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
              SELECT session_id,run_id,'session_run',0,run_id,input_tokens,output_tokens,total_tokens,NULL,NULL,NULL FROM session_runs
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);
        if (TableExists(connection, transaction, "monitor_spans"))
            ExecuteWithIds(connection, transaction, """
                INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
                SELECT e.session_id,e.run_id,'llm_span',1,CAST(ms.raw_record_id AS TEXT)||':'||CAST(ms.span_ordinal AS TEXT),ms.input_tokens,ms.output_tokens,ms.total_tokens,ms.reasoning_tokens,ms.cache_read_tokens,ms.cache_creation_tokens
                FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY
                WHERE e.run_id IS NOT NULL AND ms.category='llm_call' AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson);
        ApplyLabels(connection, transaction, idsJson, now);
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO local_workspace_projection_state(projector_key,session_frontier,refreshed_at) VALUES('local-workspace-projection-v1',(SELECT MAX(updated_at) FROM sessions),$now) ON CONFLICT(projector_key) DO UPDATE SET session_frontier=excluded.session_frontier,refreshed_at=excluded.refreshed_at;";
        state.Parameters.AddWithValue("$now", Canonical(now)); state.ExecuteNonQuery();
    }

    private static void ApplyLabels(SqliteConnection connection, SqliteTransaction transaction, string idsJson, DateTimeOffset now)
    {
        connection.CreateFunction<string, string?>("local_workspace_label", static json => TryReadInstruction(json, out var text) ? text : null, isDeterministic: true);
        connection.CreateFunction<string, string?>("local_workspace_search", static text => text is null ? null : Search(text), isDeterministic: true);
        connection.CreateFunction<string, string, long>("local_workspace_future", static (expiry, instant) =>
            TryCanonicalFuture(expiry, DateTimeOffset.ParseExact(instant, "O", CultureInfo.InvariantCulture)) ? 1 : 0, isDeterministic: true);
        ExecuteWithIds(connection, transaction, """
            WITH candidates AS (
              SELECT e.session_id,e.event_id,c.expires_at,local_workspace_label(c.content_json) label_text,e.occurred_at
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.type='user_prompt' COLLATE BINARY AND e.content_state='available'
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND local_workspace_future(c.expires_at,$now)=1),
            ranked AS (SELECT *,row_number() OVER(PARTITION BY session_id ORDER BY occurred_at COLLATE BINARY,event_id COLLATE BINARY) ordinal FROM candidates WHERE label_text IS NOT NULL)
            UPDATE local_workspace_sessions AS p SET
              label_state='recorded',label_text=r.label_text,label_search_text=local_workspace_search(r.label_text),
              label_source_identity=r.event_id,label_expires_at=r.expires_at,
              revision_seed=p.revision_seed||'|'||r.event_id||'|'||r.expires_at
            FROM ranked r WHERE r.ordinal=1 AND r.session_id=p.session_id;
            """, idsJson, Canonical(now));
    }

    private static bool TryReadInstruction(string json, out string text)
    {
        text = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("message", out var value) || value.ValueKind != JsonValueKind.String) return false;
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
    private static bool TryCanonicalFuture(string value, DateTimeOffset now) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero && parsed > now;
    private static string Canonical(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string[] ReadSessionIds(SqliteConnection connection, SqliteTransaction transaction) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;"; using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray(); }
    private static void ExecuteWithIds(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids, string? now = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name", name); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0; }
}

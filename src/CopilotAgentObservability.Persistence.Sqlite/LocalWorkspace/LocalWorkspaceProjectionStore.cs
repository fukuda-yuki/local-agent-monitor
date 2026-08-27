using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionStore
{
    internal static void CompleteSessionEventContentDeletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceItemId,
        DateTimeOffset completedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_workspace_content_tombstones(source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision)
            SELECT c.source_item_id,c.part,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,$at,i.item_id,i.revision
            FROM local_workspace_node_content_refs c
            JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=c.source_item_id
            WHERE c.source_item_id=$source
              AND c.availability_state IN ('available','expired','read_denied','oversized')
            ON CONFLICT(source_item_id) DO UPDATE SET
              deleted_at=excluded.deleted_at,retention_item_id=excluded.retention_item_id,retention_revision=excluded.retention_revision;
            UPDATE local_workspace_node_content_refs SET
              revision_input=revision_input||'|deleted|'||(SELECT CAST(revision AS TEXT) FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source),
              retention_item_id=(SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source),
              retention_store_instance_id=NULL,source_captured_at=NULL,source_expires_at=NULL,
              retention_revision=(SELECT revision FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source),
              retention_ownership_receipt=NULL,retention_owner_token=NULL,availability_state='deleted'
            WHERE source_item_id=$source
              AND EXISTS(SELECT 1 FROM local_workspace_content_tombstones t WHERE t.source_item_id=$source);
            """;
        command.Parameters.AddWithValue("$source", sourceItemId);
        command.Parameters.AddWithValue("$at", Canonical(completedAt));
        command.ExecuteNonQuery();
    }
    private static readonly Regex ExplicitOffsetInstant = new(
        "^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\\.(?<fraction>[0-9]{1,7}))?(?<offset>Z|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant);

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
        var skillProjection = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, sessionIds, now, skillRegistryAuthority);
        ApplySearchFacts(connection, transaction, idsJson, now, skillProjection);
        if (TableExists(connection, transaction, "local_workspace_execution_headers"))
            RefreshDetailProjection(connection, transaction, sessionIds, skillProjection, now);
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO local_workspace_projection_state(projector_key,session_frontier,refreshed_at) VALUES('local-workspace-projection-v1',(SELECT MAX(updated_at) FROM sessions),$now) ON CONFLICT(projector_key) DO UPDATE SET session_frontier=excluded.session_frontier,refreshed_at=excluded.refreshed_at;";
        state.Parameters.AddWithValue("$now", Canonical(now)); SqliteCommandExecutionObserver.Executing(); state.ExecuteNonQuery();
    }

    private static void RefreshDetailProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> skillProjection,
        DateTimeOffset now)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var idsJson = JsonSerializer.Serialize(ids);
        ExecuteWithIds(connection, transaction, """
            DELETE FROM local_workspace_node_content_refs WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
            DELETE FROM local_workspace_node_edges WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))) OR related_node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
            DELETE FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_execution_headers WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);

        using (var limits = connection.CreateCommand())
        {
            limits.Transaction = transaction;
            limits.CommandText = "SELECT EXISTS(SELECT 1 FROM session_runs WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) GROUP BY session_id HAVING COUNT(*)>256);";
            limits.Parameters.AddWithValue("$ids", idsJson);
            SqliteCommandExecutionObserver.Executing();
            if (Convert.ToInt64(limits.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("local_workspace_projection_workspace_too_large");
        }
        Execute(connection, transaction, """
            INSERT INTO local_workspace_execution_headers(execution_id,session_id,source_kind,source_identity,source_ordinal,lifecycle,status,model,trace_id,time_authority,start_utc_ticks,end_utc_ticks,duration_ms)
            SELECT local_workspace_execution_id('session_run',r.run_id),r.session_id,'session_run',r.run_id,
                   row_number() OVER(PARTITION BY r.session_id ORDER BY r.run_id COLLATE BINARY)-1,
                   CASE r.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE r.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,r.model,
                   CASE WHEN length(r.trace_id)=32 AND r.trace_id NOT GLOB '*[^0-9a-f]*' THEN r.trace_id END,
                   CASE WHEN local_workspace_ticks(r.started_at) IS NOT NULL THEN 'recorded' WHEN r.started_at IS NULL THEN 'missing' ELSE 'invalid' END,
                   local_workspace_ticks(r.started_at),
                   CASE WHEN local_workspace_ticks(r.started_at) IS NOT NULL AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at) THEN local_workspace_ticks(r.ended_at) END,
                   CASE WHEN local_workspace_ticks(r.started_at) IS NOT NULL AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at) THEN (local_workspace_ticks(r.ended_at)-local_workspace_ticks(r.started_at))/10000 END
            FROM session_runs r WHERE r.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,token_state)
            SELECT local_workspace_node_id('execution_root',h.source_identity),h.session_id,h.execution_id,'execution_root',h.source_identity,0,NULL,'exact','execution','not_observed',NULL,
                   CASE h.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE h.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   h.time_authority,h.start_utc_ticks,h.end_utc_ticks,h.duration_ms,'not_observed'
            FROM local_workspace_execution_headers h WHERE h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,token_state,trace_id,span_id,event_id)
            SELECT local_workspace_node_id('session_event',e.event_id),e.session_id,h.execution_id,'session_event',e.event_id,
                   row_number() OVER(PARTITION BY e.run_id ORDER BY e.event_id COLLATE BINARY),NULL,'unknown',local_workspace_node_kind(e.type),'recorded',e.type,
                   CASE e.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE e.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE WHEN local_workspace_ticks(e.occurred_at) IS NOT NULL THEN 'recorded' WHEN e.occurred_at IS NULL THEN 'missing' ELSE 'invalid' END,
                   local_workspace_ticks(e.occurred_at),local_workspace_ticks(e.occurred_at),CASE WHEN local_workspace_ticks(e.occurred_at) IS NULL THEN NULL ELSE 0 END,'not_observed',
                   CASE WHEN e.source_adapter='otel-exact' AND e.trace_id IS NOT NULL AND e.source_event_id LIKE e.trace_id||'/%' THEN e.trace_id END,
                   CASE WHEN e.source_adapter='otel-exact' AND e.trace_id IS NOT NULL AND e.source_event_id LIKE e.trace_id||'/%' THEN substr(e.source_event_id,length(e.trace_id)+2) END,e.event_id
            FROM session_events e JOIN local_workspace_execution_headers h ON h.session_id=e.session_id AND h.source_identity=e.run_id
            WHERE e.run_id IS NOT NULL AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
            SELECT local_workspace_node_id('unknown_relation_group',h.source_identity),h.session_id,h.execution_id,'unknown_relation_group',h.source_identity,
                   (SELECT COUNT(*)+1 FROM session_events x WHERE x.run_id=h.source_identity),NULL,'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
            FROM local_workspace_execution_headers h WHERE h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND EXISTS(SELECT 1 FROM session_events e LEFT JOIN session_events p ON p.event_id=e.parent_event_id AND p.run_id=e.run_id
                         WHERE e.run_id=h.source_identity AND e.parent_event_id IS NOT NULL AND p.event_id IS NULL);
            UPDATE local_workspace_nodes AS n SET
              parent_node_id=CASE WHEN e.parent_event_id IS NULL THEN local_workspace_node_id('execution_root',e.run_id)
                                  WHEN p.event_id IS NOT NULL THEN local_workspace_node_id('session_event',p.event_id)
                                  ELSE local_workspace_node_id('unknown_relation_group',e.run_id) END,
              relationship_authority=CASE WHEN e.parent_event_id IS NULL OR p.event_id IS NOT NULL THEN 'exact' ELSE 'unknown' END
            FROM session_events e LEFT JOIN session_events p ON p.event_id=e.parent_event_id AND p.run_id=e.run_id
            WHERE n.source_kind='session_event' AND n.source_identity=e.event_id AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
            SELECT node_id,parent_node_id,'parent',relationship_authority,source_ordinal FROM local_workspace_nodes
            WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) AND parent_node_id IS NOT NULL AND relationship_authority IN ('exact','explicit');
            """, ("$ids", idsJson));
        Execute(connection, transaction, """
            WITH ranked AS (
              SELECT o.*,row_number() OVER(PARTITION BY o.session_id,o.execution_id ORDER BY
                CASE WHEN o.input_tokens IS NULL AND o.output_tokens IS NULL AND o.total_tokens IS NULL AND o.reasoning_tokens IS NULL AND o.cache_read_tokens IS NULL AND o.cache_creation_tokens IS NULL THEN 1 ELSE 0 END,
                o.authority_rank,o.source_identity COLLATE BINARY) ordinal
              FROM local_workspace_token_observations o WHERE o.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            chosen AS (SELECT * FROM ranked WHERE ordinal=1)
            UPDATE local_workspace_execution_headers AS h SET
              skill_activity_state=COALESCE((SELECT CASE state WHEN 'unavailable' THEN 'projection_invalid' ELSE state END FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='skill'),'not_observed'),
              skill_activity_count=CASE WHEN (SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='skill')='recorded' THEN 0 END,
              tool_activity_state=COALESCE((SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='tool'),'not_observed'),
              tool_activity_count=CASE WHEN (SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='tool')='recorded' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND e.type IN ('tool.execution_start','PreToolUse')) END,
              subagent_activity_state=COALESCE((SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='subagent'),'not_observed'),
              subagent_activity_count=CASE WHEN (SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='subagent')='recorded' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND e.type IN ('subagent.started','SubagentStart')) END,
              error_activity_state=COALESCE((SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='error'),'not_observed'),
              error_activity_count=CASE WHEN (SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='error')='recorded' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=h.session_id AND e.run_id=h.source_identity AND (e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed')) END,
              retry_activity_state='not_observed',
              retry_activity_count=NULL,
              token_authority=CASE WHEN c.input_tokens IS NULL AND c.output_tokens IS NULL AND c.total_tokens IS NULL AND c.reasoning_tokens IS NULL AND c.cache_read_tokens IS NULL AND c.cache_creation_tokens IS NULL THEN 'none'
                WHEN (c.total_tokens IS NOT NULL AND c.input_tokens IS NOT NULL AND c.output_tokens IS NOT NULL AND c.total_tokens<>c.input_tokens+c.output_tokens) OR (c.cache_read_tokens IS NOT NULL AND (c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens)) THEN 'none' ELSE c.authority END,
              token_state=CASE WHEN c.input_tokens IS NULL AND c.output_tokens IS NULL AND c.total_tokens IS NULL AND c.reasoning_tokens IS NULL AND c.cache_read_tokens IS NULL AND c.cache_creation_tokens IS NULL THEN 'not_observed'
                WHEN (c.total_tokens IS NOT NULL AND c.input_tokens IS NOT NULL AND c.output_tokens IS NOT NULL AND c.total_tokens<>c.input_tokens+c.output_tokens) OR (c.cache_read_tokens IS NOT NULL AND (c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens)) THEN 'inconsistent' ELSE 'recorded' END,
              available_execution_count=CASE WHEN c.input_tokens IS NULL AND c.output_tokens IS NULL AND c.total_tokens IS NULL AND c.reasoning_tokens IS NULL AND c.cache_read_tokens IS NULL AND c.cache_creation_tokens IS NULL THEN 0
                WHEN (c.total_tokens IS NOT NULL AND c.input_tokens IS NOT NULL AND c.output_tokens IS NOT NULL AND c.total_tokens<>c.input_tokens+c.output_tokens) OR (c.cache_read_tokens IS NOT NULL AND (c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens)) THEN 0 ELSE 1 END,
              input_token_state=CASE WHEN c.input_tokens IS NULL THEN 'not_observed' WHEN c.cache_read_tokens IS NOT NULL AND c.cache_read_tokens>c.input_tokens THEN 'inconsistent' ELSE 'recorded' END,
              input_tokens=CASE WHEN c.input_tokens IS NOT NULL AND NOT (c.cache_read_tokens IS NOT NULL AND c.cache_read_tokens>c.input_tokens) THEN c.input_tokens END,
              output_tokens=CASE WHEN c.output_tokens IS NOT NULL THEN c.output_tokens END,
              total_tokens=CASE WHEN c.total_tokens IS NOT NULL AND NOT (c.input_tokens IS NOT NULL AND c.output_tokens IS NOT NULL AND c.total_tokens<>c.input_tokens+c.output_tokens) THEN c.total_tokens END,
              reasoning_tokens=c.reasoning_tokens,
              output_token_state=CASE WHEN c.output_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END,
              total_token_state=CASE WHEN c.total_tokens IS NULL THEN 'not_observed' WHEN c.input_tokens IS NOT NULL AND c.output_tokens IS NOT NULL AND c.total_tokens<>c.input_tokens+c.output_tokens THEN 'inconsistent' ELSE 'recorded' END,
              reasoning_token_state=CASE WHEN c.reasoning_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END,
              cache_read_token_state=CASE WHEN c.cache_read_tokens IS NULL THEN 'not_observed' WHEN c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens THEN 'inconsistent' ELSE 'recorded' END,
              cache_creation_token_state=CASE WHEN c.cache_creation_tokens IS NULL THEN 'not_observed' ELSE 'recorded' END,
              cache_read_tokens=CASE WHEN c.cache_read_tokens IS NOT NULL AND c.input_tokens IS NOT NULL AND c.cache_read_tokens<=c.input_tokens THEN c.cache_read_tokens END,cache_creation_tokens=c.cache_creation_tokens,
              new_input_token_state=CASE WHEN c.cache_read_tokens IS NOT NULL AND (c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens) THEN 'inconsistent' WHEN c.input_tokens IS NOT NULL AND c.cache_read_tokens IS NOT NULL THEN 'recorded' ELSE 'not_observed' END,
              new_input_tokens=CASE WHEN c.input_tokens IS NOT NULL AND c.cache_read_tokens IS NOT NULL AND c.cache_read_tokens<=c.input_tokens THEN c.input_tokens-c.cache_read_tokens END,
              cache_read_ratio_state=CASE WHEN c.cache_read_tokens IS NOT NULL AND (c.input_tokens IS NULL OR c.cache_read_tokens>c.input_tokens) THEN 'inconsistent' WHEN c.input_tokens>0 AND c.cache_read_tokens IS NOT NULL THEN 'recorded' ELSE 'not_observed' END,
              cache_read_ratio_basis_points=CASE WHEN c.input_tokens>0 AND c.cache_read_tokens IS NOT NULL AND c.cache_read_tokens<=c.input_tokens THEN (c.cache_read_tokens*10000)/c.input_tokens END
            FROM (SELECT h2.execution_id local_execution_id,c.* FROM local_workspace_execution_headers h2 LEFT JOIN chosen c ON c.session_id=h2.session_id AND c.execution_id=h2.source_identity) c
            WHERE h.execution_id=c.local_execution_id AND h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            UPDATE local_workspace_nodes AS n SET
              skill_activity_state=h.skill_activity_state,skill_activity_count=h.skill_activity_count,tool_activity_state=h.tool_activity_state,tool_activity_count=h.tool_activity_count,
              subagent_activity_state=h.subagent_activity_state,subagent_activity_count=h.subagent_activity_count,error_activity_state=h.error_activity_state,error_activity_count=h.error_activity_count,retry_activity_state=h.retry_activity_state,retry_activity_count=h.retry_activity_count,
              token_authority=h.token_authority,token_state=h.token_state,available_execution_count=h.available_execution_count,
              input_token_state=h.input_token_state,output_token_state=h.output_token_state,total_token_state=h.total_token_state,reasoning_token_state=h.reasoning_token_state,cache_read_token_state=h.cache_read_token_state,cache_creation_token_state=h.cache_creation_token_state,
              input_tokens=h.input_tokens,output_tokens=h.output_tokens,total_tokens=h.total_tokens,reasoning_tokens=h.reasoning_tokens,
              cache_read_tokens=h.cache_read_tokens,cache_creation_tokens=h.cache_creation_tokens,new_input_token_state=h.new_input_token_state,new_input_tokens=h.new_input_tokens,cache_read_ratio_state=h.cache_read_ratio_state,cache_read_ratio_basis_points=h.cache_read_ratio_basis_points
            FROM local_workspace_execution_headers h WHERE n.source_kind='execution_root' AND n.execution_id=h.execution_id
              AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            UPDATE local_workspace_nodes SET
              tool_activity_state=CASE WHEN kind='tool' THEN 'recorded' ELSE 'not_observed' END,tool_activity_count=CASE WHEN kind='tool' THEN 1 END,
              subagent_activity_state=CASE WHEN kind='subagent' THEN 'recorded' ELSE 'not_observed' END,subagent_activity_count=CASE WHEN kind='subagent' THEN 1 END,
              error_activity_state=CASE WHEN kind='error' THEN 'recorded' ELSE 'not_observed' END,error_activity_count=CASE WHEN kind='error' THEN 1 END,
              retry_activity_state=CASE WHEN kind='retry' THEN 'recorded' ELSE 'not_observed' END,retry_activity_count=CASE WHEN kind='retry' THEN 1 END
            WHERE source_kind='session_event' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, ("$ids", idsJson));

        if (TableExists(connection, transaction, "monitor_spans"))
        {
            Execute(connection, transaction, """
                WITH exact_spans AS (
                  SELECT DISTINCT e.session_id,e.run_id,ms.raw_record_id,ms.span_ordinal,f.retry_count
                  FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY
                  JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                  WHERE e.run_id IS NOT NULL AND ms.operation='chat' COLLATE BINARY AND f.retry_count IS NOT NULL AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
                totals AS (SELECT session_id,run_id,SUM(retry_count) retry_count FROM exact_spans GROUP BY session_id,run_id)
                UPDATE local_workspace_execution_headers AS h SET retry_activity_state='recorded',retry_activity_count=t.retry_count
                FROM totals t WHERE h.session_id=t.session_id AND h.source_identity=t.run_id;
                UPDATE local_workspace_nodes AS n SET retry_activity_state=h.retry_activity_state,retry_activity_count=h.retry_activity_count
                FROM local_workspace_execution_headers h WHERE n.source_kind='execution_root' AND n.execution_id=h.execution_id AND h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, ("$ids", idsJson));
        }

        var canonicalSkillsJson = JsonSerializer.Serialize(skillProjection
            .Where(static pair => pair.Value.State == "current")
            .SelectMany(static pair => pair.Value.Invocations)
            .Select(static invocation => new
            {
                identity = invocation.CanonicalIdentity,
                session = invocation.SessionId,
                trace = invocation.ProducerTraceId,
                span = invocation.ProducerSpanId,
                otel = invocation.OtelSourceIdentity,
                sdk = invocation.SdkSourceIdentity,
                name = invocation.SdkSkillName ?? invocation.OtelSkillName,
                executionKind = invocation.ExecutionSourceKind,
                execution = invocation.ExecutionSourceIdentity,
                otelEvent = invocation.OtelCarrierEventId,
                sdkEvent = invocation.SdkCarrierEventId,
                sdkParent = invocation.SdkSourceParentEventId,
                sdkAdapter = invocation.SdkSourceAdapter
            }));
        Execute(connection, transaction, $"""
            WITH canonical AS (
              SELECT value->>'identity' canonical_identity,value->>'session' session_id,
                     value->>'trace' trace_id,value->>'span' span_id,value->>'otel' otel_source_identity,
                     value->>'sdk' sdk_source_identity,value->>'name' skill_name,value->>'executionKind' execution_source_kind,
                     value->>'execution' execution_source_identity,value->>'otelEvent' otel_event_id,value->>'sdkEvent' sdk_event_id,
                     value->>'sdkParent' sdk_parent_source_event_id,value->>'sdkAdapter' sdk_source_adapter
              FROM json_each($skills)),
            unresolved AS (
              SELECT c.*,h.execution_id FROM canonical c JOIN local_workspace_execution_headers h
                ON h.session_id=c.session_id AND h.source_kind=c.execution_source_kind AND h.source_identity=c.execution_source_identity
              WHERE c.sdk_parent_source_event_id IS NOT NULL AND (
                (SELECT COUNT(*) FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_event_id=c.sdk_parent_source_event_id)<>1
                OR NOT EXISTS(SELECT 1 FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_adapter=c.sdk_source_adapter AND p.source_event_id=c.sdk_parent_source_event_id)))
            INSERT OR IGNORE INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
            SELECT local_workspace_node_id('unknown_relation_group',u.execution_source_identity),u.session_id,u.execution_id,'unknown_relation_group',u.execution_source_identity,
                   (SELECT COUNT(*)+1 FROM local_workspace_nodes n WHERE n.execution_id=u.execution_id),NULL,'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
            FROM unresolved u;
            WITH canonical AS (
              SELECT value->>'identity' canonical_identity,value->>'session' session_id,
                     value->>'trace' trace_id,value->>'span' span_id,value->>'otel' otel_source_identity,
                     value->>'sdk' sdk_source_identity,value->>'name' skill_name,value->>'executionKind' execution_source_kind,
                     value->>'execution' execution_source_identity,value->>'otelEvent' otel_event_id,value->>'sdkEvent' sdk_event_id,
                     value->>'sdkParent' sdk_parent_source_event_id,value->>'sdkAdapter' sdk_source_adapter
              FROM json_each($skills)),
            rows AS (
              SELECT c.*,h.execution_id,local_workspace_node_id('skill_invocation',c.canonical_identity) node_id,
                     CASE WHEN c.sdk_parent_source_event_id IS NULL THEN local_workspace_node_id('execution_root',h.source_identity)
                          WHEN (SELECT COUNT(*) FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_event_id=c.sdk_parent_source_event_id)=1 AND EXISTS(SELECT 1 FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_adapter=c.sdk_source_adapter AND p.source_event_id=c.sdk_parent_source_event_id)
                            THEN local_workspace_node_id('session_event',(SELECT p.event_id FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_adapter=c.sdk_source_adapter AND p.source_event_id=c.sdk_parent_source_event_id))
                          ELSE local_workspace_node_id('unknown_relation_group',h.source_identity) END parent_node_id,
                     CASE WHEN c.sdk_parent_source_event_id IS NULL THEN 'exact'
                          WHEN (SELECT COUNT(*) FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_event_id=c.sdk_parent_source_event_id)=1 AND EXISTS(SELECT 1 FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_adapter=c.sdk_source_adapter AND p.source_event_id=c.sdk_parent_source_event_id) THEN 'explicit'
                          ELSE 'unknown' END relation_authority,
                     (SELECT COUNT(*) FROM local_workspace_nodes n WHERE n.execution_id=h.execution_id)+
                       row_number() OVER(PARTITION BY h.execution_id ORDER BY c.canonical_identity COLLATE BINARY) source_ordinal,
                     COALESCE((SELECT occurred_at FROM session_events WHERE event_id=c.sdk_event_id AND session_id=c.session_id),
                              (SELECT occurred_at FROM session_events WHERE event_id=c.otel_event_id AND session_id=c.session_id)) occurred_at,
                     COALESCE(c.sdk_event_id,c.otel_event_id) event_id
              FROM canonical c JOIN local_workspace_execution_headers h ON h.session_id=c.session_id AND h.source_kind=c.execution_source_kind AND h.source_identity=c.execution_source_identity)
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,skill_activity_state,skill_activity_count,token_state,trace_id,span_id,event_id,otel_source_identity,sdk_source_identity)
            SELECT node_id,session_id,execution_id,'skill_invocation',canonical_identity,source_ordinal,parent_node_id,relation_authority,'skill',
                   CASE WHEN skill_name IS NULL OR trim(skill_name)='' THEN 'invalid' ELSE 'recorded' END,
                   CASE WHEN skill_name IS NULL OR trim(skill_name)='' THEN NULL ELSE skill_name END,
                   'completed','completed',CASE WHEN local_workspace_ticks(occurred_at) IS NULL THEN CASE WHEN occurred_at IS NULL THEN 'missing' ELSE 'invalid' END ELSE 'recorded' END,
                   local_workspace_ticks(occurred_at),local_workspace_ticks(occurred_at),CASE WHEN local_workspace_ticks(occurred_at) IS NULL THEN NULL ELSE 0 END,'recorded',1,'not_observed',trace_id,span_id,event_id,otel_source_identity,sdk_source_identity
            FROM rows;
            INSERT INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
            SELECT node_id,parent_node_id,'parent',relationship_authority,source_ordinal FROM local_workspace_nodes
            WHERE source_kind='skill_invocation' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) AND relationship_authority IN ('exact','explicit');
            UPDATE local_workspace_execution_headers AS h SET skill_activity_count=(SELECT COUNT(*) FROM local_workspace_nodes n WHERE n.execution_id=h.execution_id AND n.source_kind='skill_invocation')
            WHERE h.skill_activity_state='recorded' AND h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            UPDATE local_workspace_nodes AS n SET skill_activity_count=h.skill_activity_count
            FROM local_workspace_execution_headers h WHERE n.source_kind='execution_root' AND n.execution_id=h.execution_id AND h.skill_activity_state='recorded'
              AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, ("$ids", idsJson), ("$skills", canonicalSkillsJson));

        if (TableExists(connection, transaction, "session_event_content"))
        {
            var hasRetentionAuthority = TableExists(connection, transaction, "retention_items")
                && TableExists(connection, transaction, "retention_store_instances")
                && TableExists(connection, transaction, "retention_tombstones")
                && ColumnExists(connection, transaction, "retention_items", "item_id")
                && ColumnExists(connection, transaction, "retention_items", "ownership_receipt");
            var contentSql = hasRetentionAuthority
                ? """
                  INSERT INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,retention_ownership_receipt,retention_owner_token,availability_state)
                  SELECT n.node_id,COALESCE(t.part,local_workspace_content_part(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json))),'session_event_content',e.event_id,
                    COALESCE(t.locator_kind,CASE WHEN local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json) IS NULL THEN 'whole_event' ELSE 'json_pointer' END),
                    COALESCE(t.json_pointer,local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json)),
                    COALESCE(t.selected_utf8_bytes,local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json)),
                    e.content_state||'|'||COALESCE(c.captured_at,'')||'|'||COALESCE(c.expires_at,'')||'|'||COALESCE(i.item_id,'')||'|'||COALESCE(i.store_instance_id,'')||'|'||COALESCE(CAST(i.revision AS TEXT),'')||'|'||COALESCE(i.state,'')||'|'||COALESCE(t.deleted_at,''),
                    i.item_id,CASE WHEN t.source_item_id IS NULL THEN i.store_instance_id END,CASE WHEN t.source_item_id IS NULL THEN COALESCE(c.captured_at,i.captured_at) END,CASE WHEN t.source_item_id IS NULL THEN COALESCE(c.expires_at,i.expires_at) END,i.revision,CASE WHEN t.source_item_id IS NULL THEN i.ownership_receipt END,
                    CASE WHEN e.content_state='available' AND c.event_id IS NOT NULL AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                      AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at AND typeof(c.retention_owner_token)='blob' AND length(c.retention_owner_token)=32
                      AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1
                      AND NOT EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)
                      AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now)) AND local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json)<=1048576 THEN c.retention_owner_token END,
                    CASE WHEN t.source_item_id IS NOT NULL AND (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones rt WHERE rt.item_id=i.item_id)) THEN 'deleted' WHEN c.event_id IS NOT NULL AND i.read_denied_at IS NOT NULL THEN 'read_denied'
                         WHEN e.content_state='expired_pending_deletion' OR i.state IN ('expired_pending_deletion','deletion_queued','deleting','deletion_failed') OR (i.state='expiring' AND i.expires_at<=$now) THEN 'expired'
                         WHEN e.content_state='available' AND c.event_id IS NOT NULL AND local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json)>1048576 THEN 'oversized'
                         WHEN e.content_state='not_captured' OR c.event_id IS NULL THEN 'not_captured'
                         WHEN e.content_state='available' AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1) AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at
                           AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1
                           AND NOT EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id) AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now)) THEN 'available'
                         ELSE 'invalid' END
                  FROM local_workspace_nodes n JOIN session_events e ON n.source_kind='session_event' AND n.source_identity=e.event_id
                  LEFT JOIN session_event_content c ON c.event_id=e.event_id
                  LEFT JOIN local_workspace_content_tombstones t ON t.source_item_id=e.event_id
                  LEFT JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                    AND ((c.event_id IS NOT NULL AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at)
                      OR i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id))
                  WHERE n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                    AND e.type<>'skill.invoked';
                  """
                : """
                  INSERT INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,retention_owner_token,availability_state)
                  SELECT n.node_id,local_workspace_content_part(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json)),'session_event_content',e.event_id,
                    CASE WHEN local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json) IS NULL THEN 'whole_event' ELSE 'json_pointer' END,
                    local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),
                    local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json),
                    e.content_state||'|'||COALESCE(c.expires_at,''),NULL,
                    CASE WHEN e.content_state='not_captured' OR c.event_id IS NULL THEN 'not_captured' WHEN e.content_state='expired_pending_deletion' THEN 'expired' ELSE 'invalid' END
                  FROM local_workspace_nodes n JOIN session_events e ON n.source_kind='session_event' AND n.source_identity=e.event_id
                  LEFT JOIN session_event_content c ON c.event_id=e.event_id
                  WHERE n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                    AND e.type<>'skill.invoked';
                  """;
            Execute(connection, transaction, contentSql, ("$ids", idsJson), ("$now", Canonical(now)));
        }

        using var bound = connection.CreateCommand();
        bound.Transaction = transaction;
        bound.CommandText = "SELECT EXISTS(SELECT 1 FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) GROUP BY session_id HAVING COUNT(*)>4096);";
        bound.Parameters.AddWithValue("$ids", idsJson);
        SqliteCommandExecutionObserver.Executing();
        if (Convert.ToInt64(bound.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("local_workspace_projection_workspace_too_large");
    }

    internal static string StableExecutionId(string sessionId, string sourceKind, string sourceIdentity)
    {
        _ = sessionId;
        var bytes = Hash("local-workspace-execution-id\0v1\0", sourceKind, sourceIdentity);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true).ToString("D", CultureInfo.InvariantCulture);
    }

    internal static string StableNodeId(string sourceKind, string sourceIdentity) =>
        "node-" + Convert.ToHexString(Hash("local-workspace-node-id\0v1\0" + sourceKind + "\0", sourceKind, sourceIdentity).AsSpan(0, 16)).ToLowerInvariant();

    private static byte[] Hash(string domain, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(domain));
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }

    private static (string Authority, long? Ticks) Time(string? value)
    {
        if (value is null) return ("missing", null);
        return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? ("recorded", instant.UtcTicks)
            : ("invalid", null);
    }

    private static string NodeKind(string type) => type switch
    {
        "tool.execution_start" or "PreToolUse" => "tool",
        "subagent.started" or "SubagentStart" => "subagent",
        "PostToolUseFailure" or "StopFailure" or "subagent.failed" => "error",
        _ => "event",
    };

    private static string ContentPart(string? pointer) => pointer switch
    {
        "/prompt" => "instruction",
        "/tool_input" => "tool_input",
        "/tool_response" => "tool_result",
        "/error" => "error_message",
        "/agent_id" => "subagent_input",
        _ => "event_content",
    };

    private static string? ContentPointer(string? adapter, string? fingerprint, string type, string? json)
    {
        if (!string.Equals(adapter, "claude-code-hook", StringComparison.Ordinal) || fingerprint is null || fingerprint.Length != 64 || json is null)
            return null;
        var candidate = type switch
        {
            "UserPromptSubmit" => ("/prompt", "prompt", JsonValueKind.String),
            "PreToolUse" => ("/tool_input", "tool_input", JsonValueKind.Object),
            "PostToolUse" => ("/tool_response", "tool_response", JsonValueKind.Undefined),
            "PostToolUseFailure" or "StopFailure" => ("/error", "error", JsonValueKind.String),
            "SubagentStart" => ("/agent_id", "agent_id", JsonValueKind.String),
            _ => default,
        };
        if (candidate.Item1 is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(candidate.Item2, out var value)
                && (candidate.Item3 == JsonValueKind.Undefined || value.ValueKind == candidate.Item3)
                ? candidate.Item1
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static long? ContentBytes(string? pointer, string? json)
    {
        if (json is null) return null;
        if (pointer is null) return Encoding.UTF8.GetByteCount(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var property = pointer[1..];
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(property, out var value)) return null;
            return Encoding.UTF8.GetByteCount(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }
        catch (JsonException) { return null; }
    }

    private static void ApplyLabels(SqliteConnection connection, SqliteTransaction transaction, string idsJson, DateTimeOffset now)
    {
        connection.CreateFunction<string, string, string?>("local_workspace_label", static (type, json) => TryReadInstruction(type, json, out var text) ? text : null, isDeterministic: true);
        connection.CreateFunction<string, string?>("local_workspace_search", static text => text is null ? null : Search(text), isDeterministic: true);
        connection.CreateFunction<string, string, long>("local_workspace_future", static (expiry, instant) =>
            TryCanonicalFuture(expiry, DateTimeOffset.ParseExact(instant, "O", CultureInfo.InvariantCulture)) ? 1 : 0, isDeterministic: true);
        var candidates = TableExists(connection, transaction, "retention_items")
            ? """
              SELECT e.session_id,e.event_id,c.expires_at source_expires_at,
                     CASE WHEN i.state='retained_by_policy' THEN NULL ELSE i.expires_at END effective_expires_at,
                     local_workspace_label(e.type,c.content_json) label_text,e.occurred_at
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                AND i.expires_at=c.expires_at
              WHERE e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted') AND e.content_state='available'
                AND i.state IN ('expiring','retained_by_policy') AND i.read_denied_at IS NULL
                AND i.deleted_at IS NULL AND i.error_code IS NULL
                AND (i.state='retained_by_policy' OR local_workspace_future(i.expires_at,$now)=1)
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : """
              SELECT e.session_id,e.event_id,c.expires_at source_expires_at,c.expires_at effective_expires_at,local_workspace_label(e.type,c.content_json) label_text,e.occurred_at
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted') AND e.content_state='available'
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND local_workspace_future(c.expires_at,$now)=1
              """;
        ExecuteWithIds(connection, transaction, $"""
            WITH candidates AS (
              {candidates}),
            ranked AS (SELECT *,row_number() OVER(PARTITION BY session_id ORDER BY occurred_at COLLATE BINARY,event_id COLLATE BINARY) ordinal FROM candidates WHERE label_text IS NOT NULL)
            UPDATE local_workspace_sessions AS p SET
              label_state='recorded',label_text=r.label_text,
              label_source_identity=r.event_id,label_expires_at=r.source_expires_at,
              revision_seed=p.revision_seed||'|'||r.event_id||'|'||r.source_expires_at
            FROM ranked r WHERE r.ordinal=1 AND r.session_id=p.session_id;
            """, idsJson, Canonical(now));
    }

    private static void ApplySearchFacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idsJson,
        DateTimeOffset now,
        IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> skillProjection)
    {
        if (TableExists(connection, transaction, "retention_items"))
            ExecuteWithIds(connection, transaction, """
                INSERT INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
                SELECT p.session_id,'label',p.label_source_identity,local_workspace_search(p.label_text),
                       CASE WHEN i.state='retained_by_policy' THEN NULL ELSE i.expires_at END
                FROM local_workspace_sessions p JOIN retention_items i
                  ON i.store_kind='session_event_content' AND i.source_item_id=p.label_source_identity AND i.expires_at=p.label_expires_at
                WHERE p.label_state='recorded' AND p.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson);
        else
            ExecuteWithIds(connection, transaction, """
                INSERT INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
                SELECT session_id,'label',label_source_identity,local_workspace_search(label_text),label_expires_at
                FROM local_workspace_sessions WHERE label_state='recorded' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson);
        var skillJson = JsonSerializer.Serialize(skillProjection.Select(pair => new
        {
            session = pair.Key,
            state = pair.Value.State == "current" ? "recorded" : pair.Value.State,
            count = pair.Value.InvocationCount,
            facts = pair.Value.SearchFacts.Select(fact => new { source = fact.SourceIdentity, name = fact.SkillName, expires = fact.ExpiresAt })
        }));
        using (var skills = connection.CreateCommand())
        {
            skills.Transaction = transaction;
            skills.CommandText = """
                WITH projection AS (
                  SELECT value->>'session' session_id,value->>'state' state,
                         CASE WHEN json_type(value,'$.count')='null' THEN NULL ELSE CAST(value->>'count' AS INTEGER) END count,
                         value->'facts' facts
                  FROM json_each($projection))
                UPDATE local_workspace_session_activity AS a SET state=p.state,count=p.count
                FROM projection p WHERE a.session_id=p.session_id AND a.kind='skill';
                INSERT OR IGNORE INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
                SELECT p.value->>'session','skill',f.value->>'source',local_workspace_search(f.value->>'name'),f.value->>'expires'
                FROM json_each($projection) p JOIN json_each(p.value->'facts') f;
                """;
            skills.Parameters.AddWithValue("$projection", skillJson);
            SqliteCommandExecutionObserver.Executing();
            skills.ExecuteNonQuery();
        }
        if (TableExists(connection, transaction, "raw_records") && TableExists(connection, transaction, "monitor_spans") && TableExists(connection, transaction, "retention_items") && ColumnExists(connection, transaction, "monitor_spans", "tool_name"))
            ExecuteWithIds(connection, transaction, """
                INSERT OR IGNORE INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at)
                SELECT e.session_id,'tool',CAST(m.raw_record_id AS TEXT)||':'||CAST(m.span_ordinal AS TEXT),local_workspace_search(m.tool_name),
                       CASE WHEN i.state='retained_by_policy' THEN NULL ELSE i.expires_at END
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

    private static void RegisterProjectionFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, long?>("local_workspace_epoch", static value => TryInstant(value, out var instant) ? instant.ToUnixTimeMilliseconds() : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_canonical", static value => TryInstant(value, out var instant) ? Canonical(instant) : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_search", static value => value is null ? null : Search(value), isDeterministic: true);
        connection.CreateFunction<string, string, string>("local_workspace_node_id", StableNodeId, isDeterministic: true);
        connection.CreateFunction<string, string, string>("local_workspace_execution_id", static (kind, identity) => StableExecutionId(string.Empty, kind, identity), isDeterministic: true);
        connection.CreateFunction<string, string>("local_workspace_node_kind", NodeKind, isDeterministic: true);
        connection.CreateFunction<string?, string>("local_workspace_content_part", ContentPart, isDeterministic: true);
        connection.CreateFunction<string?, string?, string, string?, string?>("local_workspace_content_pointer", ContentPointer, isDeterministic: true);
        connection.CreateFunction<string?, string?, long?>("local_workspace_content_bytes", ContentBytes, isDeterministic: true);
        connection.CreateFunction<string?, string?, string?, string?, string?, string?, string?, string?, string?, byte[]?, byte[]?, long>(
            "local_workspace_retention_receipt_matches", RetentionReceiptMatches, isDeterministic: true);
        connection.CreateFunction<string?, long?>("local_workspace_ticks", static value => Time(value).Ticks, isDeterministic: true);
    }

    private static bool TryInstant(string? value, out DateTimeOffset instant)
    {
        instant = default;
        if (value is null) return false;
        var match = ExplicitOffsetInstant.Match(value);
        if (!match.Success) return false;
        var fraction = match.Groups["fraction"].Value;
        var utc = match.Groups["offset"].Value == "Z";
        var format = "yyyy-MM-dd'T'HH:mm:ss"
            + (fraction.Length == 0 ? string.Empty : "." + new string('f', fraction.Length))
            + (utc ? "'Z'" : "zzz");
        return DateTimeOffset.TryParseExact(
            value,
            format,
            CultureInfo.InvariantCulture,
            utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None,
            out instant);
    }

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
    private static string[] ReadSessionIds(SqliteConnection connection, SqliteTransaction transaction) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;"; SqliteCommandExecutionObserver.Executing(); using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray(); }
    private static void ExecuteWithIds(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids, string? now = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now); SqliteCommandExecutionObserver.Executing(); command.ExecuteNonQuery(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; SqliteCommandExecutionObserver.Executing(); command.ExecuteNonQuery(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var (name,value) in parameters) command.Parameters.AddWithValue(name,value ?? DBNull.Value); SqliteCommandExecutionObserver.Executing(); command.ExecuteNonQuery(); }
    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name", name); SqliteCommandExecutionObserver.Executing(); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0; }
    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string name) { using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=$"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name=$name);"; command.Parameters.AddWithValue("$name",name); SqliteCommandExecutionObserver.Executing(); return Convert.ToInt64(command.ExecuteScalar(),CultureInfo.InvariantCulture)!=0; }

    private static long RetentionReceiptMatches(string? storeInstanceId, string? eventId, string? contentKind,
        string? capturedAt, string? expiresAt, string? sessionId, string? runId, string? sourceAdapter,
        string? sourceEventId, byte[]? ownerToken, byte[]? ownershipReceipt)
    {
        try
        {
            if (storeInstanceId is null || eventId is null || contentKind is null || capturedAt is null || expiresAt is null
                || sessionId is null || sourceAdapter is null || sourceEventId is null || ownerToken is null || ownershipReceipt is null
                || !DateTimeOffset.TryParseExact(capturedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured)
                || !DateTimeOffset.TryParseExact(expiresAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) return 0;
            var expected = RetentionOwnershipReceipt.CreateSession(new(storeInstanceId, eventId, contentKind, capturedAt,
                captured.UtcDateTime.Ticks, expiresAt, expiry.UtcDateTime.Ticks, sessionId, runId, sourceAdapter, sourceEventId, ownerToken));
            return RetentionOwnershipReceipt.Matches(expected, ownershipReceipt) ? 1 : 0;
        }
        catch (ArgumentException) { return 0; }
    }
}

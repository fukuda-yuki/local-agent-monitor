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
            INSERT INTO local_workspace_content_tombstones(store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,deleted_at,retention_item_id,retention_revision)
            SELECT c.store_kind,c.source_item_id,c.part,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,$at,i.item_id,i.revision
            FROM local_workspace_node_content_refs c
            JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=c.source_item_id
            WHERE c.source_item_id=$source
              AND c.availability_state IN ('available','expired','read_denied','oversized')
            ON CONFLICT(store_kind,source_item_id,part) DO UPDATE SET
              deleted_at=excluded.deleted_at,retention_item_id=excluded.retention_item_id,retention_revision=excluded.retention_revision;
            UPDATE local_workspace_node_content_refs SET
              revision_input=(SELECT e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||i.item_id||'|'||
                i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|deleted|'||i.deleted_at
                FROM session_events e JOIN retention_items i
                  ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                WHERE e.event_id=$source),
              retention_item_id=(SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source),
              retention_store_instance_id=NULL,source_captured_at=NULL,source_expires_at=NULL,
              retention_revision=(SELECT revision FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$source),
              retention_ownership_receipt=NULL,retention_owner_token=NULL,availability_state='deleted'
            WHERE source_item_id=$source
              AND EXISTS(SELECT 1 FROM local_workspace_content_tombstones t WHERE t.store_kind=local_workspace_node_content_refs.store_kind AND t.source_item_id=$source AND t.part=local_workspace_node_content_refs.part);
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
        using var pinnedAuthority = PinnedSkillRegistryGenerationAuthority.Create(skillRegistryAuthority);
        RefreshAllSessionBatches(connection, transaction, now, pinnedAuthority);
        Execute(connection, transaction, "DELETE FROM local_workspace_sessions WHERE session_id NOT IN (SELECT session_id FROM sessions);");
    }

    internal static void RefreshSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now, ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        using var pinnedAuthority = PinnedSkillRegistryGenerationAuthority.Create(skillRegistryAuthority);
        RefreshStagedSessionBatches(connection, transaction, sessionIds, now, pinnedAuthority);
    }

    internal static void RefreshSessionBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<IReadOnlyCollection<string>> sessionIdBatches,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        using var pinnedAuthority = PinnedSkillRegistryGenerationAuthority.Create(skillRegistryAuthority);
        RefreshStagedSessionBatches(connection, transaction, sessionIdBatches.SelectMany(static batch => batch), now, pinnedAuthority);
    }

    internal static void RefreshStructural(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        RefreshAllSessionBatches(connection, transaction, now, null);
        Execute(connection, transaction, "DELETE FROM local_workspace_sessions WHERE session_id NOT IN (SELECT session_id FROM sessions);");
    }

    internal static void RefreshSessionsStructural(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now)
        => RefreshStagedSessionBatches(connection, transaction, sessionIds, now, null);

    private static void RefreshStagedSessionBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> sessionIds,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        Execute(connection, transaction, """
            DROP TABLE IF EXISTS temp.local_workspace_target_session_ids;
            CREATE TEMP TABLE local_workspace_target_session_ids(session_id TEXT PRIMARY KEY) WITHOUT ROWID;
            """);
        try
        {
            using (var stage = connection.CreateCommand())
            {
                stage.Transaction = transaction;
                stage.CommandText = "INSERT OR IGNORE INTO local_workspace_target_session_ids(session_id) VALUES($session_id);";
                var sessionId = stage.Parameters.Add("$session_id", SqliteType.Text);
                foreach (var value in sessionIds)
                {
                    sessionId.Value = value;
                    SqliteCommandExecutionObserver.Executing();
                    stage.ExecuteNonQuery();
                }
            }
            string? after = null;
            var refreshed = false;
            while (true)
            {
                var batch = ReadStagedSessionIdBatch(connection, transaction, after);
                if (batch.Length == 0) break;
                RefreshSessionsCore(connection, transaction, batch, now, skillRegistryAuthority);
                refreshed = true;
                after = batch[^1];
            }
            if (!refreshed)
                RefreshSessionsCore(connection, transaction, [], now, skillRegistryAuthority);
        }
        finally
        {
            Execute(connection, transaction, "DROP TABLE IF EXISTS temp.local_workspace_target_session_ids;");
        }
    }

    private static void RefreshAllSessionBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        string? after = null;
        var refreshed = false;
        while (true)
        {
            var ids = ReadSessionIdBatch(connection, transaction, after);
            if (ids.Length == 0) break;
            RefreshSessionsCore(connection, transaction, ids, now, skillRegistryAuthority);
            refreshed = true;
            after = ids[^1];
        }
        if (!refreshed)
            RefreshSessionsCore(connection, transaction, [], now, skillRegistryAuthority);
    }

    private static void RefreshSessionsCore(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now, ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        RegisterProjectionFunctions(connection);
        var idsJson = JsonSerializer.Serialize(sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (TableExists(connection, transaction, "local_workspace_execution_headers")
            && RetentionContentAuthorityInstalled(connection, transaction))
            PreserveAuthenticatedReadDeniedContentReferences(connection, transaction, idsJson);
        if (TableExists(connection, transaction, "local_workspace_semantic_receipts"))
            PreserveSemanticProjection(connection, transaction, idsJson);
        var labelProofInstalled = ColumnExists(connection, transaction, "local_workspace_sessions", "label_owner_revision");
        var labelProofColumns = labelProofInstalled ? ",label_owner_revision,instruction_count" : string.Empty;
        var labelProofValues = labelProofInstalled ? ",NULL,NULL" : string.Empty;
        ExecuteWithIds(connection, transaction, $"""
            DELETE FROM local_workspace_token_observations WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_activity WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_models WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_sources WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_session_search_facts WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_sessions WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            INSERT INTO local_workspace_sessions(session_id,sort_group,sort_epoch_ms,label_state,label_text,label_source_identity,label_expires_at{labelProofColumns},status,completeness,source_state,model_state,timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed,node_overflow)
            SELECT s.session_id,CASE WHEN COALESCE(local_workspace_epoch(s.started_at),local_workspace_epoch(s.created_at),local_workspace_epoch(s.last_seen_at)) IS NULL THEN 1 ELSE 0 END,
                   COALESCE(local_workspace_epoch(s.started_at),local_workspace_epoch(s.created_at),local_workspace_epoch(s.last_seen_at),0),
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'expired' WHEN 'not_captured' THEN 'not_captured' ELSE 'not_observed' END,
                   NULL,NULL,NULL{labelProofValues},s.status,s.completeness,'not_observed','not_observed',
                   CASE WHEN local_workspace_epoch(s.started_at) IS NOT NULL
                               AND local_workspace_epoch(s.last_seen_at) IS NOT NULL
                               AND ((s.status='active' AND s.ended_at IS NULL)
                                 OR (s.status IN ('completed','failed') AND local_workspace_epoch(s.ended_at)>=local_workspace_epoch(s.started_at))
                                 OR (s.status='unknown' AND (s.ended_at IS NULL OR local_workspace_epoch(s.ended_at)>=local_workspace_epoch(s.started_at))))
                          THEN 'recorded'
                        WHEN local_workspace_epoch(s.started_at) IS NULL AND local_workspace_epoch(s.ended_at) IS NULL THEN 'not_observed'
                        ELSE 'inconsistent' END,
                   local_workspace_canonical(s.started_at),local_workspace_canonical(s.ended_at),local_workspace_canonical(s.last_seen_at),local_workspace_epoch(s.last_seen_at),CASE WHEN s.status<>'active' AND local_workspace_epoch(s.started_at) IS NOT NULL AND local_workspace_epoch(s.ended_at)>=local_workspace_epoch(s.started_at) THEN local_workspace_epoch(s.ended_at)-local_workspace_epoch(s.started_at) END,
                   CASE s.raw_retention_state WHEN 'expired_pending_deletion' THEN 'raw_content_expired' WHEN 'not_captured' THEN 'raw_content_not_captured' ELSE '' END,
                   s.status||'|'||s.completeness||'|'||COALESCE(local_workspace_canonical(s.started_at),'')||'|'||COALESCE(local_workspace_canonical(s.ended_at),'')||'|'||COALESCE(local_workspace_canonical(s.last_seen_at),''),0
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
              state=CASE
                WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND e.status='gap_before_capture' AND CASE a.kind
                  WHEN 'tool' THEN e.type IN ('tool.execution_start','PreToolUse')
                  WHEN 'subagent' THEN e.type IN ('subagent.started','SubagentStart')
                  WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) THEN 'capture_gap'
                WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND e.content_state='unsupported' AND CASE a.kind
                  WHEN 'tool' THEN e.type IN ('tool.execution_start','PreToolUse')
                  WHEN 'subagent' THEN e.type IN ('subagent.started','SubagentStart')
                  WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) THEN 'source_unsupported'
                WHEN a.kind<>'retry' AND EXISTS(SELECT 1 FROM session_events e WHERE e.session_id=a.session_id AND CASE a.kind
                  WHEN 'tool' THEN
                    (e.type='tool.execution_start' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'')
                    OR (e.type='PreToolUse' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook'
                      AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'' AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>''
                      AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                      AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'')
                        OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                  WHEN 'subagent' THEN
                    (e.type='subagent.started' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'')
                    OR (e.type='SubagentStart' AND e.run_id IS NOT NULL AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook'
                      AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'' AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>''
                      AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                      AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'')
                        OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                  WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) THEN 'recorded'
                ELSE 'not_observed' END,
              count=CASE WHEN a.kind<>'retry' THEN (SELECT CASE WHEN COUNT(DISTINCT e.event_id)=0 THEN NULL ELSE COUNT(DISTINCT e.event_id) END
                FROM session_events e WHERE e.session_id=a.session_id AND CASE a.kind
                  WHEN 'tool' THEN
                    (e.type='tool.execution_start' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'')
                    OR (e.type='PreToolUse' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook'
                      AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'' AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>''
                      AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                      AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'')
                        OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                  WHEN 'subagent' THEN
                    (e.type='subagent.started' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'')
                    OR (e.type='SubagentStart' AND e.run_id IS NOT NULL AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook'
                      AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'' AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>''
                      AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                      AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'')
                        OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*')))
                  WHEN 'error' THEN e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed' ELSE 0 END) END
              WHERE a.kind IN ('tool','subagent','error','retry');
            UPDATE local_workspace_session_activity SET count=NULL
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) AND state<>'recorded';
            INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
              SELECT session_id,run_id,'session_run',0,run_id,input_tokens,output_tokens,total_tokens,NULL,NULL,NULL FROM session_runs
              WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);
        if (TableExists(connection, transaction, "monitor_spans"))
        {
            ExecuteWithIds(connection, transaction, """
                WITH incomplete_retry AS (
                  SELECT e.session_id,
                         CASE WHEN MAX(e.status='gap_before_capture')=1 THEN 'capture_gap'
                              WHEN MAX(e.content_state='unsupported')=1 THEN 'source_unsupported' END state
                  FROM session_events e
                  JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY
                    AND e.type='otel.span' COLLATE BINARY
                    AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY
                    AND e.trace_id=ms.trace_id COLLATE BINARY
                  JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                  WHERE ms.operation='chat' COLLATE BINARY AND f.retry_count IS NOT NULL
                    AND (SELECT COUNT(*) FROM monitor_spans owner
                      WHERE lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.span_id)=lower(ms.span_id) COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner
                      WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                        AND owner.type='otel.span' COLLATE BINARY
                        AND lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.source_event_id)=lower(ms.trace_id||'/'||ms.span_id) COLLATE BINARY)=1
                    AND (e.status='gap_before_capture' OR e.content_state='unsupported')
                    AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                  GROUP BY e.session_id)
                UPDATE local_workspace_session_activity AS a SET state=i.state,count=NULL
                FROM incomplete_retry i WHERE a.session_id=i.session_id AND a.kind='retry' AND i.state IS NOT NULL;
                """, idsJson);
            ExecuteWithIds(connection, transaction, $"""
                INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
                SELECT e.session_id,e.run_id,'llm_span',1,CAST(ms.raw_record_id AS TEXT)||':'||CAST(ms.span_ordinal AS TEXT),ms.input_tokens,ms.output_tokens,f.producer_total_tokens,ms.reasoning_tokens,ms.cache_read_tokens,ms.cache_creation_tokens
                FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY
                  AND e.type='otel.span' COLLATE BINARY
                  AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY
                LEFT JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                WHERE e.run_id IS NOT NULL AND ms.category='llm_call'
                  AND (SELECT COUNT(*) FROM monitor_spans owner
                    WHERE lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                      AND lower(owner.span_id)=lower(ms.span_id) COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner
                    WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                      AND owner.type='otel.span' COLLATE BINARY
                      AND lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                      AND lower(owner.source_event_id)=lower(ms.trace_id||'/'||ms.span_id) COLLATE BINARY)=1
                  AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson);
            ExecuteWithIds(connection, transaction, """
                WITH exact_spans AS (
                  SELECT DISTINCT e.session_id,e.run_id,ms.raw_record_id,ms.span_ordinal,f.retry_count
                  FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY
                    AND e.type='otel.span' COLLATE BINARY
                    AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY
                  JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                  WHERE e.run_id IS NOT NULL AND ms.operation='chat' COLLATE BINARY AND f.retry_count IS NOT NULL
                    AND (SELECT COUNT(*) FROM monitor_spans owner
                      WHERE lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.span_id)=lower(ms.span_id) COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner
                      WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                        AND owner.type='otel.span' COLLATE BINARY
                        AND lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.source_event_id)=lower(ms.trace_id||'/'||ms.span_id) COLLATE BINARY)=1
                    AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
                totals AS (SELECT session_id,SUM(retry_count) retry_count FROM exact_spans GROUP BY session_id)
                UPDATE local_workspace_session_activity AS a SET state='recorded',count=t.retry_count FROM totals t WHERE a.session_id=t.session_id AND a.kind='retry' AND a.state='not_observed';
                """, idsJson);
            ExecuteWithIds(connection, transaction, """
                WITH exact_tools AS (
                  SELECT e.session_id,ms.trace_id,ms.span_id
                  FROM session_events e JOIN monitor_spans ms
                    ON e.source_adapter='otel-exact' COLLATE BINARY
                   AND e.type='otel.span' COLLATE BINARY
                   AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY
                   AND e.trace_id=ms.trace_id COLLATE BINARY
                  WHERE ms.operation='execute_tool' COLLATE BINARY AND ms.category IN ('tool_call','error')
                    AND length(ms.trace_id)=32 AND ms.trace_id=lower(ms.trace_id) AND ms.trace_id NOT GLOB '*[^0-9a-f]*'
                    AND length(ms.span_id)=16 AND ms.span_id=lower(ms.span_id) AND ms.span_id NOT GLOB '*[^0-9a-f]*'
                    AND (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=ms.trace_id COLLATE BINARY AND lower(owner.span_id)=ms.span_id COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                      AND owner.type='otel.span' COLLATE BINARY
                      AND lower(owner.trace_id)=ms.trace_id COLLATE BINARY AND lower(owner.source_event_id)=ms.trace_id||'/'||ms.span_id COLLATE BINARY)=1
                    AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
                totals AS (SELECT session_id,COUNT(*) tool_count FROM exact_tools GROUP BY session_id)
                UPDATE local_workspace_session_activity AS a SET state='recorded',count=t.tool_count
                FROM totals t WHERE a.session_id=t.session_id AND a.kind='tool' AND a.state='not_observed';
                """, idsJson);
        }
        ApplyLabels(connection, transaction, idsJson, now, labelProofInstalled);
        var skillProjection = HasSkillInvocationInputs(connection, transaction, idsJson)
            ? SkillProjectionReadService.ReadCurrentInvocationProjection(
                connection, transaction, sessionIds, now, skillRegistryAuthority)
            : new Dictionary<string, SkillProjectionCurrentInvocationProjection>(StringComparer.Ordinal);
        ApplySearchFacts(connection, transaction, idsJson, now, skillProjection);
        if (TableExists(connection, transaction, "local_workspace_execution_headers"))
            RefreshDetailProjection(connection, transaction, sessionIds, skillProjection, now,
                skillProjection.Values.Any(static projection => projection.Invocations.Count > 0)
                    ? ReadRegistryGenerationIdentity(skillRegistryAuthority)
                    : "unavailable");
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO local_workspace_projection_state(projector_key,session_frontier,refreshed_at) VALUES('local-workspace-projection-v1',(SELECT MAX(updated_at) FROM sessions),$now) ON CONFLICT(projector_key) DO UPDATE SET session_frontier=excluded.session_frontier,refreshed_at=excluded.refreshed_at;";
        state.Parameters.AddWithValue("$now", Canonical(now)); SqliteCommandExecutionObserver.Executing(); state.ExecuteNonQuery();
    }

    private static bool HasSkillInvocationInputs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idsJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var otelArm = TableExists(connection, transaction, "monitor_spans")
            ? """
              UNION ALL
              SELECT 1 FROM monitor_spans span
              JOIN session_runs run ON run.trace_id=span.trace_id
              WHERE run.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND span.category='skill'
            """
            : string.Empty;
        var sdkClaimArm = TableExists(connection, transaction, "skill_projection_sdk_claims")
            ? """
              UNION ALL
              SELECT 1 FROM skill_projection_sdk_claims claim
              WHERE claim.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : string.Empty;
        var projectedOtelArm = TableExists(connection, transaction, "monitor_skill_invocations")
            ? """
              UNION ALL
              SELECT 1 FROM monitor_skill_invocations invocation
              WHERE invocation.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : string.Empty;
        command.CommandText = $"""
            SELECT EXISTS(
              SELECT 1 FROM session_events event
              WHERE event.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND event.type='skill.invoked'
              {otelArm}
              {sdkClaimArm}
              {projectedOtelArm});
            """;
        command.Parameters.AddWithValue("$ids", idsJson);
        SqliteCommandExecutionObserver.Executing();
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void RefreshDetailProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> skillProjection,
        DateTimeOffset now,
        string registryGenerationIdentity)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var idsJson = JsonSerializer.Serialize(ids);
        var semanticTablesInstalled = TableExists(connection, transaction, "local_workspace_semantic_receipts");
        var sessionEventContentInstalled = TableExists(connection, transaction, "session_event_content");
        var retentionAuthorityInstalled = RetentionContentAuthorityInstalled(connection, transaction);
        if (semanticTablesInstalled)
        {
            if (!TempTableExists(connection, transaction, "local_workspace_preserved_semantic_nodes"))
                PreserveSemanticProjection(connection, transaction, idsJson);
            ExecuteWithIds(connection, transaction, """
                DELETE FROM local_workspace_tool_metadata WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
                DELETE FROM local_workspace_skill_metadata WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
                DELETE FROM local_workspace_subagent_lifecycle WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
                DELETE FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
                DELETE FROM local_workspace_semantic_receipts WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
                """, idsJson);
        }
        ExecuteWithIds(connection, transaction, """
            DELETE FROM local_workspace_node_content_refs WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
            DELETE FROM local_workspace_node_edges WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))) OR related_node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)));
            DELETE FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_execution_headers WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            """, idsJson);

        Execute(connection, transaction, """
            WITH ranked_runs AS (
              SELECT r.*,row_number() OVER(PARTITION BY r.session_id ORDER BY r.run_id COLLATE BINARY)-1 AS workspace_ordinal
              FROM session_runs r WHERE r.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)))
            INSERT INTO local_workspace_execution_headers(execution_id,session_id,source_kind,source_identity,source_ordinal,lifecycle,status,model,trace_id,time_authority,start_utc_ticks,end_utc_ticks,duration_ms)
            SELECT local_workspace_execution_id('session_run',r.run_id),r.session_id,'session_run',r.run_id,
                   r.workspace_ordinal,
                   CASE r.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE r.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,r.model,
                   CASE WHEN length(r.trace_id)=32 AND r.trace_id NOT GLOB '*[^0-9a-f]*' THEN r.trace_id END,
                   CASE WHEN r.status IN ('completed','failed') THEN
                          CASE WHEN local_workspace_ticks(r.started_at) IS NOT NULL
                                  AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at) THEN 'recorded' ELSE 'invalid' END
                        WHEN r.status='active' THEN CASE WHEN local_workspace_ticks(r.started_at) IS NOT NULL AND r.ended_at IS NULL THEN 'recorded' WHEN r.started_at IS NULL AND r.ended_at IS NULL THEN 'missing' ELSE 'invalid' END
                        WHEN local_workspace_ticks(r.started_at) IS NOT NULL AND (r.ended_at IS NULL OR local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at)) THEN 'recorded'
                        WHEN r.started_at IS NULL AND r.ended_at IS NULL THEN 'missing' ELSE 'invalid' END,
                   CASE WHEN (r.status='active' AND local_workspace_ticks(r.started_at) IS NOT NULL AND r.ended_at IS NULL)
                               OR (r.status IN ('completed','failed') AND local_workspace_ticks(r.started_at) IS NOT NULL AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at))
                               OR (r.status NOT IN ('active','completed','failed') AND local_workspace_ticks(r.started_at) IS NOT NULL AND (r.ended_at IS NULL OR local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at)))
                        THEN local_workspace_ticks(r.started_at) END,
                   CASE WHEN r.status<>'active' AND local_workspace_ticks(r.started_at) IS NOT NULL AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at) THEN local_workspace_ticks(r.ended_at) END,
                   CASE WHEN r.status<>'active' AND local_workspace_ticks(r.started_at) IS NOT NULL AND local_workspace_ticks(r.ended_at)>=local_workspace_ticks(r.started_at) THEN (local_workspace_ticks(r.ended_at)-local_workspace_ticks(r.started_at))/10000 END
            FROM ranked_runs r WHERE r.workspace_ordinal<=256;
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,token_state)
            SELECT local_workspace_node_id('execution_root',h.source_identity),h.session_id,h.execution_id,'execution_root',h.source_identity,0,NULL,'exact','execution','not_observed',NULL,
                   CASE h.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE h.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   h.time_authority,h.start_utc_ticks,h.end_utc_ticks,h.duration_ms,'not_observed'
            FROM local_workspace_execution_headers h WHERE h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            WITH ranked_events AS (
              SELECT e.*,h.execution_id,
                     row_number() OVER(PARTITION BY e.run_id ORDER BY e.event_id COLLATE BINARY) source_ordinal,
                     row_number() OVER(PARTITION BY e.session_id ORDER BY h.source_ordinal,e.event_id COLLATE BINARY) session_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_execution_headers x WHERE x.session_id=e.session_id) execution_count
              FROM session_events e JOIN local_workspace_execution_headers h ON h.session_id=e.session_id AND h.source_identity=e.run_id
              WHERE e.run_id IS NOT NULL AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)))
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,token_state,trace_id,span_id,event_id)
            SELECT local_workspace_node_id('session_event',e.event_id),e.session_id,e.execution_id,'session_event',e.event_id,
                   e.source_ordinal,NULL,'unknown',local_workspace_node_kind(e.type),'recorded',e.type,
                   CASE e.status WHEN 'active' THEN 'started' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE e.status WHEN 'active' THEN 'active' WHEN 'completed' THEN 'completed' WHEN 'failed' THEN 'failed' ELSE 'unknown' END,
                   CASE WHEN local_workspace_ticks(e.occurred_at) IS NOT NULL THEN 'recorded' WHEN e.occurred_at IS NULL THEN 'missing' ELSE 'invalid' END,
                   local_workspace_ticks(e.occurred_at),CASE WHEN e.status='active' THEN NULL ELSE local_workspace_ticks(e.occurred_at) END,
                   CASE WHEN e.status='active' OR local_workspace_ticks(e.occurred_at) IS NULL THEN NULL ELSE 0 END,'not_observed',
                   CASE WHEN e.source_adapter='otel-exact' AND e.type='otel.span'
                              AND length(e.trace_id)=32 AND e.trace_id=lower(e.trace_id) AND e.trace_id NOT GLOB '*[^0-9a-f]*'
                              AND length(e.source_event_id)=49 AND substr(e.source_event_id,1,33)=e.trace_id||'/' COLLATE BINARY
                              AND substr(e.source_event_id,34)=lower(substr(e.source_event_id,34))
                              AND substr(e.source_event_id,34) NOT GLOB '*[^0-9a-f]*'
                        THEN e.trace_id END,
                   CASE WHEN e.source_adapter='otel-exact' AND e.type='otel.span'
                              AND length(e.trace_id)=32 AND e.trace_id=lower(e.trace_id) AND e.trace_id NOT GLOB '*[^0-9a-f]*'
                              AND length(e.source_event_id)=49 AND substr(e.source_event_id,1,33)=e.trace_id||'/' COLLATE BINARY
                              AND substr(e.source_event_id,34)=lower(substr(e.source_event_id,34))
                              AND substr(e.source_event_id,34) NOT GLOB '*[^0-9a-f]*'
                        THEN substr(e.source_event_id,34) END,e.event_id
            FROM ranked_events e
            WHERE e.session_ordinal<=MAX(0,4097-e.execution_count);
            WITH candidates AS (
              SELECT h.* FROM local_workspace_execution_headers h
              WHERE h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND EXISTS(SELECT 1 FROM session_events e LEFT JOIN session_events p ON p.event_id=e.parent_event_id AND p.run_id=e.run_id
                           WHERE e.run_id=h.source_identity AND e.parent_event_id IS NOT NULL AND p.event_id IS NULL)),
            ranked AS (
              SELECT candidate.*,row_number() OVER(PARTITION BY candidate.session_id ORDER BY candidate.source_identity COLLATE BINARY) candidate_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes existing WHERE existing.session_id=candidate.session_id) existing_count
              FROM candidates candidate)
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
            SELECT local_workspace_node_id('unknown_relation_group',source_identity),session_id,execution_id,'unknown_relation_group',source_identity,
                   (SELECT COUNT(*)+1 FROM session_events x WHERE x.run_id=ranked.source_identity),NULL,'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
            FROM ranked WHERE existing_count+candidate_ordinal<=4097;
            UPDATE local_workspace_nodes AS n SET
              parent_node_id=CASE WHEN e.parent_event_id IS NULL THEN local_workspace_node_id('execution_root',e.run_id)
                                  WHEN p.event_id IS NOT NULL THEN local_workspace_node_id('session_event',p.event_id)
                                  WHEN EXISTS(SELECT 1 FROM local_workspace_nodes relation_group
                                    WHERE relation_group.node_id=local_workspace_node_id('unknown_relation_group',e.run_id))
                                    THEN local_workspace_node_id('unknown_relation_group',e.run_id) END,
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
                CASE WHEN o.input_tokens IS NULL AND o.output_tokens IS NULL AND o.total_tokens IS NULL
                           AND o.reasoning_tokens IS NULL AND o.cache_read_tokens IS NULL AND o.cache_creation_tokens IS NULL
                     THEN 1 ELSE 0 END,
                o.authority_rank,o.source_identity COLLATE BINARY) ordinal
              FROM local_workspace_token_observations o WHERE o.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
            chosen AS (SELECT * FROM ranked WHERE ordinal=1)
            UPDATE local_workspace_execution_headers AS h SET
              skill_activity_state=COALESCE((SELECT CASE state WHEN 'unavailable' THEN 'projection_invalid' ELSE state END FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='skill'),'not_observed'),
              skill_activity_count=CASE WHEN (SELECT state FROM local_workspace_session_activity a WHERE a.session_id=h.session_id AND a.kind='skill')='recorded' THEN 0 END,
              tool_activity_state=CASE WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.run_id=h.source_identity AND (
                (e.type='tool.execution_start' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'') OR
                (e.type='PreToolUse' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>''
                  AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>'' AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                  AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'') OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'))))) THEN 'recorded' ELSE 'not_observed' END,
              tool_activity_count=(SELECT CASE WHEN COUNT(DISTINCT e.event_id)=0 THEN NULL ELSE COUNT(DISTINCT e.event_id) END FROM session_events e WHERE e.run_id=h.source_identity AND (
                (e.type='tool.execution_start' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'') OR
                (e.type='PreToolUse' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>''
                  AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>'' AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                  AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'') OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'))))),
              subagent_activity_state=CASE WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.run_id=h.source_identity AND (
                (e.type='subagent.started' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'') OR
                (e.type='SubagentStart' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>''
                  AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>'' AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                  AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'') OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'))))) THEN 'recorded' ELSE 'not_observed' END,
              subagent_activity_count=(SELECT CASE WHEN COUNT(DISTINCT e.event_id)=0 THEN NULL ELSE COUNT(DISTINCT e.event_id) END FROM session_events e WHERE e.run_id=h.source_identity AND (
                (e.type='subagent.started' AND e.source_adapter='copilot-sdk-stream' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>'') OR
                (e.type='SubagentStart' AND e.source_surface='claude-code' AND e.source_adapter='claude-code-hook' AND e.source_event_id IS NOT NULL AND trim(e.source_event_id)<>''
                  AND e.adapter_version IS NOT NULL AND trim(e.adapter_version)<>'' AND e.normalization_version IS NOT NULL AND trim(e.normalization_version)<>''
                  AND ((e.source_application_version IS NOT NULL AND trim(e.source_application_version)<>'') OR (length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint) AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'))))),
              error_activity_state=CASE WHEN EXISTS(SELECT 1 FROM session_events e WHERE e.run_id=h.source_identity AND (e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed')) THEN 'recorded' ELSE 'not_observed' END,
              error_activity_count=(SELECT CASE WHEN COUNT(DISTINCT e.event_id)=0 THEN NULL ELSE COUNT(DISTINCT e.event_id) END FROM session_events e WHERE e.run_id=h.source_identity AND (e.type IN ('PostToolUseFailure','StopFailure','subagent.failed') OR e.terminal_outcome='failed')),
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
                  FROM session_events e JOIN monitor_spans ms ON e.source_adapter='otel-exact' COLLATE BINARY
                    AND e.type='otel.span' COLLATE BINARY
                    AND e.source_event_id=ms.trace_id||'/'||ms.span_id COLLATE BINARY AND e.trace_id=ms.trace_id COLLATE BINARY
                  JOIN local_workspace_span_facts f ON f.raw_record_id=ms.raw_record_id AND f.span_ordinal=ms.span_ordinal
                  WHERE e.run_id IS NOT NULL AND ms.operation='chat' COLLATE BINARY AND f.retry_count IS NOT NULL
                    AND (SELECT COUNT(*) FROM monitor_spans owner
                      WHERE lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.span_id)=lower(ms.span_id) COLLATE BINARY)=1
                    AND (SELECT COUNT(*) FROM session_events owner
                      WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                        AND owner.type='otel.span' COLLATE BINARY
                        AND lower(owner.trace_id)=lower(ms.trace_id) COLLATE BINARY
                        AND lower(owner.source_event_id)=lower(ms.trace_id||'/'||ms.span_id) COLLATE BINARY)=1
                    AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))),
                totals AS (SELECT session_id,run_id,SUM(retry_count) retry_count FROM exact_spans GROUP BY session_id,run_id)
                UPDATE local_workspace_execution_headers AS h SET retry_activity_state='recorded',retry_activity_count=t.retry_count
                FROM totals t WHERE h.session_id=t.session_id AND h.source_identity=t.run_id;
                UPDATE local_workspace_nodes AS n SET retry_activity_state=h.retry_activity_state,retry_activity_count=h.retry_activity_count
                FROM local_workspace_execution_headers h WHERE n.source_kind='execution_root' AND n.execution_id=h.execution_id AND h.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, ("$ids", idsJson));
        }

        var canonicalSkillsJson = JsonSerializer.Serialize(skillProjection
            .Where(static pair => pair.Value.State is "current" or "certification_pending")
            .SelectMany(static pair => pair.Value.Invocations.Select(invocation => new
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
                sdkAdapter = invocation.SdkSourceAdapter,
                source = invocation.SkillSource,
                trigger = invocation.InvocationTrigger,
                historical = invocation.HistoricalSnapshotReference,
                state = invocation.CurrentValidState,
            })));
        const string pendingSkillAdmission = "1=1";
        Execute(connection, transaction, $"""
            WITH canonical AS (
              SELECT value->>'identity' canonical_identity,value->>'session' session_id,
                     value->>'trace' trace_id,value->>'span' span_id,value->>'otel' otel_source_identity,
                     value->>'sdk' sdk_source_identity,value->>'name' skill_name,value->>'executionKind' execution_source_kind,
                     value->>'execution' execution_source_identity,value->>'otelEvent' otel_event_id,value->>'sdkEvent' sdk_event_id,
                     value->>'sdkParent' sdk_parent_source_event_id,value->>'sdkAdapter' sdk_source_adapter,value->>'state' projection_state
              FROM json_each($skills)
              WHERE value->>'state'='current' OR {pendingSkillAdmission}),
            unresolved AS (
              SELECT DISTINCT c.session_id,c.execution_source_identity,h.execution_id FROM canonical c JOIN local_workspace_execution_headers h
                ON h.session_id=c.session_id AND h.source_kind=c.execution_source_kind AND h.source_identity=c.execution_source_identity
              WHERE c.sdk_parent_source_event_id IS NOT NULL AND (
                (SELECT COUNT(*) FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_event_id=c.sdk_parent_source_event_id)<>1
                OR NOT EXISTS(SELECT 1 FROM session_events p WHERE p.session_id=c.session_id AND p.run_id=c.execution_source_identity AND p.source_adapter=c.sdk_source_adapter AND p.source_event_id=c.sdk_parent_source_event_id))),
            ranked_unresolved AS (
              SELECT unresolved.*,row_number() OVER(PARTITION BY session_id ORDER BY execution_source_identity COLLATE BINARY) candidate_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes existing WHERE existing.session_id=unresolved.session_id) existing_count
              FROM unresolved)
            INSERT OR IGNORE INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
              SELECT local_workspace_node_id('unknown_relation_group',u.execution_source_identity),u.session_id,u.execution_id,'unknown_relation_group',u.execution_source_identity,
                     (SELECT COUNT(*)+1 FROM local_workspace_nodes n WHERE n.execution_id=u.execution_id),NULL,'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
              FROM ranked_unresolved u WHERE u.existing_count+u.candidate_ordinal<=4097;
            WITH canonical AS (
              SELECT value->>'identity' canonical_identity,value->>'session' session_id,
                     value->>'trace' trace_id,value->>'span' span_id,value->>'otel' otel_source_identity,
                     value->>'sdk' sdk_source_identity,value->>'name' skill_name,value->>'executionKind' execution_source_kind,
                     value->>'execution' execution_source_identity,value->>'otelEvent' otel_event_id,value->>'sdkEvent' sdk_event_id,
                     value->>'sdkParent' sdk_parent_source_event_id,value->>'sdkAdapter' sdk_source_adapter,value->>'state' projection_state
              FROM json_each($skills)
              WHERE value->>'state'='current' OR {pendingSkillAdmission}),
            candidate_rows AS (
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
              FROM canonical c JOIN local_workspace_execution_headers h ON h.session_id=c.session_id AND h.source_kind=c.execution_source_kind AND h.source_identity=c.execution_source_identity),
            rows AS (
              SELECT candidate_rows.*,row_number() OVER(PARTITION BY session_id ORDER BY canonical_identity COLLATE BINARY) candidate_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes existing WHERE existing.session_id=candidate_rows.session_id) existing_count
              FROM candidate_rows)
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,end_utc_ticks,duration_ms,skill_activity_state,skill_activity_count,token_state,trace_id,span_id,event_id,otel_source_identity,sdk_source_identity)
            SELECT node_id,session_id,execution_id,'skill_invocation',canonical_identity,source_ordinal,parent_node_id,relation_authority,'skill',
                   CASE WHEN skill_name IS NULL OR trim(skill_name)='' THEN 'invalid' ELSE 'recorded' END,
                   CASE WHEN skill_name IS NULL OR trim(skill_name)='' THEN NULL ELSE skill_name END,
                   'completed','completed',CASE WHEN local_workspace_ticks(occurred_at) IS NULL THEN CASE WHEN occurred_at IS NULL THEN 'missing' ELSE 'invalid' END ELSE 'recorded' END,
                   local_workspace_ticks(occurred_at),local_workspace_ticks(occurred_at),CASE WHEN local_workspace_ticks(occurred_at) IS NULL THEN NULL ELSE 0 END,
                   CASE projection_state WHEN 'current' THEN 'recorded' ELSE projection_state END,
                   CASE projection_state WHEN 'current' THEN 1 END,'not_observed',trace_id,span_id,event_id,otel_source_identity,sdk_source_identity
            FROM rows WHERE existing_count+candidate_ordinal<=4097;
            INSERT INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
            SELECT node_id,parent_node_id,'parent',relationship_authority,source_ordinal FROM local_workspace_nodes
            WHERE source_kind='skill_invocation' AND session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) AND relationship_authority IN ('exact','explicit');
            WITH canonical AS (
              SELECT value->>'session' session_id,value->>'executionKind' execution_source_kind,
                     value->>'execution' execution_source_identity,value->>'state' projection_state
              FROM json_each($skills)),
            projected_sessions AS (SELECT DISTINCT session_id FROM canonical),
            execution_facts AS (
              SELECT session_id,execution_source_kind,execution_source_identity,
                     CASE WHEN SUM(CASE WHEN projection_state='certification_pending' THEN 1 ELSE 0 END)>0
                       THEN 'certification_pending' ELSE 'recorded' END state,
                     CASE WHEN SUM(CASE WHEN projection_state='certification_pending' THEN 1 ELSE 0 END)>0
                       THEN NULL ELSE COUNT(*) END count
              FROM canonical GROUP BY session_id,execution_source_kind,execution_source_identity)
            UPDATE local_workspace_execution_headers AS h SET
              skill_activity_state=COALESCE((SELECT f.state FROM execution_facts f
                WHERE f.session_id=h.session_id AND f.execution_source_kind=h.source_kind
                  AND f.execution_source_identity=h.source_identity),'not_observed'),
              skill_activity_count=(SELECT f.count FROM execution_facts f
                WHERE f.session_id=h.session_id AND f.execution_source_kind=h.source_kind
                  AND f.execution_source_identity=h.source_identity)
            WHERE h.session_id IN (SELECT session_id FROM projected_sessions);
            UPDATE local_workspace_nodes AS n SET
              skill_activity_state=h.skill_activity_state,skill_activity_count=h.skill_activity_count
            FROM local_workspace_execution_headers h WHERE n.source_kind='execution_root' AND n.execution_id=h.execution_id
              AND h.session_id IN (SELECT DISTINCT value->>'session' FROM json_each($skills));
            """, ("$ids", idsJson), ("$skills", canonicalSkillsJson));

        if (semanticTablesInstalled)
        {
            Execute(connection, transaction, """
                INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
                SELECT n.node_id,0,'session_run',n.source_identity,NULL,NULL,NULL,
                       h.source_kind||'|'||h.source_identity||'|'||h.lifecycle||'|'||h.status||'|'||COALESCE(CAST(h.start_utc_ticks AS TEXT),'')
                FROM local_workspace_nodes n JOIN local_workspace_execution_headers h ON h.execution_id=n.execution_id
                WHERE n.source_kind='execution_root' AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
                SELECT n.node_id,0,'session_event',e.event_id,n.trace_id,n.span_id,e.event_id,
                       e.source_adapter||'|'||e.source_event_id||'|'||e.type||'|'||COALESCE(e.occurred_at,'')
                FROM local_workspace_nodes n JOIN session_events e ON n.source_kind='session_event' AND n.source_identity=e.event_id
                WHERE n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
                SELECT n.node_id,0,'skill_claim',n.otel_source_identity,n.trace_id,n.span_id,j.value->>'otelEvent',
                       n.source_identity||'|'||COALESCE(n.otel_source_identity,'')||'|'||COALESCE(n.sdk_source_identity,'')
                FROM local_workspace_nodes n JOIN json_each($skills) j ON j.value->>'identity'=n.source_identity
                WHERE n.source_kind='skill_invocation'
                  AND n.otel_source_identity IS NOT NULL AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
                SELECT n.node_id,CASE WHEN n.otel_source_identity IS NULL THEN 0 ELSE 1 END,'skill_claim',n.sdk_source_identity,NULL,NULL,j.value->>'sdkEvent',
                       n.source_identity||'|'||COALESCE(n.otel_source_identity,'')||'|'||COALESCE(n.sdk_source_identity,'')
                FROM local_workspace_nodes n JOIN json_each($skills) j ON j.value->>'identity'=n.source_identity
                WHERE n.source_kind='skill_invocation'
                  AND n.sdk_source_identity IS NOT NULL AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, ("$ids", idsJson), ("$skills", canonicalSkillsJson));
            Execute(connection, transaction, $"""
                INSERT INTO local_workspace_skill_metadata(node_id,current_valid_state,source_state,source,trigger_state,trigger,
                  inventory_reference_state,inventory_reference,historical_snapshot_reference_state,historical_snapshot_reference,registry_generation_identity)
                SELECT local_workspace_node_id('skill_invocation',value->>'identity'),value->>'state',
                       CASE WHEN value->>'source' IS NULL THEN 'not_observed' ELSE 'recorded' END,value->>'source',
                       CASE WHEN value->>'trigger' IS NULL THEN 'not_observed' ELSE 'recorded' END,value->>'trigger',
                       'unavailable',NULL,CASE WHEN value->>'historical' IS NULL THEN 'not_observed' ELSE 'recorded' END,value->>'historical',$registry
                FROM json_each($skills)
                WHERE (value->>'state'='current' OR {pendingSkillAdmission})
                  AND EXISTS(SELECT 1 FROM local_workspace_nodes n WHERE n.node_id=local_workspace_node_id('skill_invocation',value->>'identity'));
                """, ("$skills", canonicalSkillsJson), ("$registry", registryGenerationIdentity));
        }

        if (sessionEventContentInstalled)
        {
            var contentSql = retentionAuthorityInstalled
                ? """
                  INSERT INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,retention_ownership_receipt,retention_owner_token,availability_state)
                  SELECT n.node_id,COALESCE(t.part,local_workspace_content_part(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json))),'session_event_content',e.event_id,
                    COALESCE(t.locator_kind,CASE WHEN local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json) IS NULL THEN 'whole_event' ELSE 'json_pointer' END),
                    COALESCE(t.json_pointer,local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json)),
                    COALESCE(t.selected_utf8_bytes,local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json)),
                    e.content_state||'|'||COALESCE(c.captured_at,i.captured_at,'')||'|'||COALESCE(c.expires_at,i.expires_at,'')||'|'||COALESCE(i.item_id,'')||'|'||COALESCE(i.store_instance_id,'')||'|'||COALESCE(CAST(i.revision AS TEXT),'')||'|'||COALESCE(i.state,'')||'|'||COALESCE(t.deleted_at,''),
                    i.item_id,CASE WHEN t.source_item_id IS NULL THEN i.store_instance_id END,CASE WHEN t.source_item_id IS NULL THEN COALESCE(c.captured_at,i.captured_at) END,CASE WHEN t.source_item_id IS NULL THEN COALESCE(c.expires_at,i.expires_at) END,i.revision,CASE WHEN t.source_item_id IS NULL THEN i.ownership_receipt END,
                    CASE WHEN e.content_state='available' AND c.event_id IS NOT NULL AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                      AND c.content_kind='application/json' COLLATE BINARY
                      AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at AND typeof(c.retention_owner_token)='blob' AND length(c.retention_owner_token)=32
                      AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1
                      AND NOT EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id)
                      AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now))
                      AND local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json) BETWEEN 0 AND 1048576 THEN c.retention_owner_token END,
                    CASE WHEN t.source_item_id IS NOT NULL AND (i.state='deleted' OR i.deleted_at IS NOT NULL OR EXISTS(SELECT 1 FROM retention_tombstones rt WHERE rt.item_id=i.item_id)) THEN 'deleted' WHEN c.event_id IS NOT NULL AND i.read_denied_at IS NOT NULL THEN 'read_denied'
                         WHEN e.content_state='expired_pending_deletion' OR i.state IN ('expired_pending_deletion','deletion_queued','deleting','deletion_failed') OR (i.state='expiring' AND i.expires_at<=$now) THEN 'expired'
                         WHEN e.content_state='available' AND c.event_id IS NOT NULL AND c.content_kind='application/json' COLLATE BINARY
                           AND local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json)>1048576 THEN 'oversized'
                         WHEN e.content_state='not_captured' OR c.event_id IS NULL THEN 'not_captured'
                         WHEN e.content_state='available' AND c.content_kind='application/json' COLLATE BINARY
                           AND local_workspace_content_bytes(local_workspace_content_pointer(e.source_adapter,e.schema_fingerprint,e.type,c.content_json),c.content_json) BETWEEN 0 AND 1048576
                           AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1) AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at
                           AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1
                           AND NOT EXISTS(SELECT 1 FROM retention_tombstones t WHERE t.item_id=i.item_id) AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now)) THEN 'available'
                         ELSE 'invalid' END
                  FROM local_workspace_nodes n JOIN session_events e ON n.source_kind='session_event' AND n.source_identity=e.event_id
                  LEFT JOIN session_event_content c ON c.event_id=e.event_id
                  LEFT JOIN local_workspace_content_tombstones t ON t.store_kind='session_event_content' AND t.source_item_id=e.event_id
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
        if (retentionAuthorityInstalled)
            RestoreAuthenticatedReadDeniedContentReferences(connection, transaction);

        if (semanticTablesInstalled)
        {
            ExecuteWithIds(connection, transaction, """
                UPDATE local_workspace_sessions AS session SET node_overflow=CASE WHEN
                  (SELECT COUNT(*) FROM local_workspace_nodes node WHERE node.session_id=session.session_id)>4096 THEN 1 ELSE 0 END
                WHERE session.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                DELETE FROM local_workspace_nodes WHERE node_id IN (
                  SELECT node_id FROM (
                    SELECT node_id,row_number() OVER(PARTITION BY session_id ORDER BY CASE source_kind WHEN 'execution_root' THEN 0 ELSE 1 END,execution_id COLLATE BINARY,source_ordinal,node_id COLLATE BINARY) ordinal
                    FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)))
                  WHERE ordinal>4097);
                """, idsJson);
            RefreshSemanticProjection(connection, transaction, idsJson);
            RestoreSemanticProjection(connection, transaction, "[]");
            RefreshOtelSemanticFacts(connection, transaction, idsJson);
        }

        ExecuteWithIds(connection, transaction, """
            UPDATE local_workspace_sessions AS s SET node_overflow=CASE WHEN
              s.node_overflow=1 OR (SELECT COUNT(*) FROM local_workspace_nodes n WHERE n.session_id=s.session_id)>4096 THEN 1 ELSE 0 END
            WHERE s.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            DELETE FROM local_workspace_nodes WHERE node_id IN (
              SELECT node_id FROM (
                SELECT node_id,row_number() OVER(PARTITION BY session_id ORDER BY CASE source_kind WHEN 'execution_root' THEN 0 ELSE 1 END,execution_id COLLATE BINARY,source_ordinal,node_id COLLATE BINARY) ordinal
                FROM local_workspace_nodes WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)))
              WHERE ordinal>4097);
            """, idsJson);
    }

    private static void PreserveAuthenticatedReadDeniedContentReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idsJson) =>
        Execute(connection, transaction, """
            DROP TABLE IF EXISTS temp.local_workspace_preserved_read_denied_content_refs;
            CREATE TEMP TABLE local_workspace_preserved_read_denied_content_refs(
              node_id TEXT NOT NULL,
              part TEXT NOT NULL,
              store_kind TEXT NOT NULL,
              source_item_id TEXT NOT NULL,
              locator_kind TEXT NOT NULL,
              json_pointer TEXT NULL,
              selected_utf8_bytes INTEGER NULL,
              revision_input TEXT NOT NULL,
              retention_item_id TEXT NOT NULL,
              retention_store_instance_id TEXT NOT NULL,
              source_captured_at TEXT NOT NULL,
              source_expires_at TEXT NOT NULL,
              retention_revision INTEGER NOT NULL,
              retention_ownership_receipt BLOB NOT NULL,
              PRIMARY KEY(node_id,part)
            ) WITHOUT ROWID;
            INSERT INTO local_workspace_preserved_read_denied_content_refs
            SELECT content.node_id,content.part,content.store_kind,content.source_item_id,content.locator_kind,
                   content.json_pointer,content.selected_utf8_bytes,content.revision_input,
                   content.retention_item_id,content.retention_store_instance_id,content.source_captured_at,
                   content.source_expires_at,content.retention_revision,content.retention_ownership_receipt
            FROM local_workspace_node_content_refs content
            JOIN local_workspace_nodes node
              ON node.node_id=content.node_id
             AND node.source_kind='session_event'
             AND node.source_identity=content.source_item_id
             AND node.event_id=content.source_item_id
            JOIN session_events event
              ON event.event_id=content.source_item_id
             AND event.session_id=node.session_id
             AND node.execution_id=local_workspace_execution_id('session_run',event.run_id)
            JOIN session_runs run
              ON run.run_id=event.run_id
             AND run.session_id=event.session_id
             AND run.source_surface=event.source_surface COLLATE BINARY
            JOIN local_workspace_node_source_references source_reference
              ON source_reference.node_id=node.node_id
             AND source_reference.source_ordinal=0
             AND source_reference.source_kind='session_event'
             AND source_reference.source_identity=event.event_id
             AND source_reference.event_id=event.event_id
             AND source_reference.revision_input=event.source_adapter||'|'||event.source_event_id||'|'||event.type||'|'||COALESCE(event.occurred_at,'')
            JOIN retention_items item
              ON item.item_id=content.retention_item_id
             AND item.store_kind=content.store_kind
             AND item.source_item_id=content.source_item_id
             AND item.store_instance_id=content.retention_store_instance_id
             AND item.captured_at=content.source_captured_at
             AND item.expires_at=content.source_expires_at
             AND item.revision=content.retention_revision
             AND item.ownership_receipt=content.retention_ownership_receipt
            JOIN retention_store_instances singleton
              ON singleton.id=1 AND singleton.store_instance_id=item.store_instance_id
            WHERE node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND content.store_kind='session_event_content'
              AND content.availability_state='read_denied'
              AND content.retention_owner_token IS NULL
              AND typeof(content.retention_ownership_receipt)='blob'
              AND length(content.retention_ownership_receipt)=32
              AND item.read_denied_at IS NOT NULL
              AND item.state<>'deleted'
              AND item.deleted_at IS NULL
              AND event.type<>'skill.invoked'
              AND content.revision_input=event.content_state||'|'||item.captured_at||'|'||item.expires_at||'|'||item.item_id||'|'||item.store_instance_id||'|'||CAST(item.revision AS TEXT)||'|'||item.state||'|'
              AND NOT EXISTS(SELECT 1 FROM session_event_content source WHERE source.event_id=event.event_id)
              AND NOT EXISTS(SELECT 1 FROM retention_tombstones tombstone WHERE tombstone.item_id=item.item_id)
              AND NOT EXISTS(SELECT 1 FROM local_workspace_content_tombstones tombstone
                WHERE tombstone.store_kind=content.store_kind AND tombstone.source_item_id=content.source_item_id AND tombstone.part=content.part)
            ORDER BY content.node_id COLLATE BINARY,content.part COLLATE BINARY;
            """, ("$ids", idsJson));

    private static void RestoreAuthenticatedReadDeniedContentReferences(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            INSERT OR REPLACE INTO local_workspace_node_content_refs(
              node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,
              retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,
              retention_ownership_receipt,retention_owner_token,availability_state)
            SELECT preserved.node_id,preserved.part,preserved.store_kind,preserved.source_item_id,preserved.locator_kind,
                   preserved.json_pointer,preserved.selected_utf8_bytes,preserved.revision_input,
                   preserved.retention_item_id,preserved.retention_store_instance_id,preserved.source_captured_at,
                   preserved.source_expires_at,preserved.retention_revision,preserved.retention_ownership_receipt,
                   NULL,'read_denied'
            FROM local_workspace_preserved_read_denied_content_refs preserved
            JOIN local_workspace_nodes node
              ON node.node_id=preserved.node_id
             AND node.source_kind='session_event'
             AND node.source_identity=preserved.source_item_id
             AND node.event_id=preserved.source_item_id
            JOIN session_events event
              ON event.event_id=preserved.source_item_id
             AND event.session_id=node.session_id
             AND node.execution_id=local_workspace_execution_id('session_run',event.run_id)
            JOIN session_runs run
              ON run.run_id=event.run_id
             AND run.session_id=event.session_id
             AND run.source_surface=event.source_surface COLLATE BINARY
            JOIN local_workspace_node_source_references source_reference
              ON source_reference.node_id=node.node_id
             AND source_reference.source_ordinal=0
             AND source_reference.source_kind='session_event'
             AND source_reference.source_identity=event.event_id
             AND source_reference.event_id=event.event_id
             AND source_reference.revision_input=event.source_adapter||'|'||event.source_event_id||'|'||event.type||'|'||COALESCE(event.occurred_at,'')
            JOIN retention_items item
              ON item.item_id=preserved.retention_item_id
             AND item.store_kind=preserved.store_kind
             AND item.source_item_id=preserved.source_item_id
             AND item.store_instance_id=preserved.retention_store_instance_id
             AND item.captured_at=preserved.source_captured_at
             AND item.expires_at=preserved.source_expires_at
             AND item.revision=preserved.retention_revision
             AND item.ownership_receipt=preserved.retention_ownership_receipt
            JOIN retention_store_instances singleton
              ON singleton.id=1 AND singleton.store_instance_id=item.store_instance_id
            WHERE item.read_denied_at IS NOT NULL
              AND item.state<>'deleted'
              AND item.deleted_at IS NULL
              AND preserved.revision_input=event.content_state||'|'||item.captured_at||'|'||item.expires_at||'|'||item.item_id||'|'||item.store_instance_id||'|'||CAST(item.revision AS TEXT)||'|'||item.state||'|'
              AND NOT EXISTS(SELECT 1 FROM session_event_content source WHERE source.event_id=event.event_id)
              AND NOT EXISTS(SELECT 1 FROM retention_tombstones tombstone WHERE tombstone.item_id=item.item_id)
              AND NOT EXISTS(SELECT 1 FROM local_workspace_content_tombstones tombstone
                WHERE tombstone.store_kind=preserved.store_kind AND tombstone.source_item_id=preserved.source_item_id AND tombstone.part=preserved.part)
              AND EXISTS(SELECT 1 FROM local_workspace_node_content_refs fallback
                WHERE fallback.node_id=node.node_id
                  AND fallback.store_kind=preserved.store_kind
                  AND fallback.source_item_id=preserved.source_item_id
                  AND fallback.retention_owner_token IS NULL
                  AND fallback.availability_state='not_captured')
            ORDER BY preserved.node_id COLLATE BINARY,preserved.part COLLATE BINARY;
            DELETE FROM local_workspace_node_content_refs AS fallback
            WHERE fallback.store_kind='session_event_content'
              AND fallback.availability_state='not_captured'
              AND EXISTS(
                SELECT 1
                FROM local_workspace_preserved_read_denied_content_refs preserved
                JOIN local_workspace_node_content_refs restored
                  ON restored.node_id=preserved.node_id
                 AND restored.part=preserved.part
                 AND restored.store_kind=preserved.store_kind
                 AND restored.source_item_id=preserved.source_item_id
                 AND restored.retention_item_id=preserved.retention_item_id
                 AND restored.retention_revision=preserved.retention_revision
                 AND restored.retention_ownership_receipt=preserved.retention_ownership_receipt
                 AND restored.retention_owner_token IS NULL
                 AND restored.availability_state='read_denied'
                WHERE preserved.node_id=fallback.node_id
                  AND preserved.source_item_id=fallback.source_item_id);
            DROP TABLE local_workspace_preserved_read_denied_content_refs;
            """);

    private static bool RetentionContentAuthorityInstalled(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        TableExists(connection, transaction, "session_event_content")
        && TableExists(connection, transaction, "retention_items")
        && TableExists(connection, transaction, "retention_store_instances")
        && TableExists(connection, transaction, "retention_tombstones")
        && ColumnExists(connection, transaction, "local_workspace_content_tombstones", "store_kind")
        && ColumnExists(connection, transaction, "retention_items", "item_id")
        && ColumnExists(connection, transaction, "retention_items", "ownership_receipt");

    internal static string StableExecutionId(string sessionId, string sourceKind, string sourceIdentity)
    {
        _ = sessionId;
        var bytes = Hash("local-workspace-execution-id\0v1\0", sourceKind, sourceIdentity);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true).ToString("D", CultureInfo.InvariantCulture);
    }

    private static void PreserveSemanticProjection(SqliteConnection connection, SqliteTransaction transaction, string idsJson)
    {
        Execute(connection, transaction, """
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_nodes;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_receipts;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_references;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_tools;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_subagents;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_semantic_edges;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_pending_skill_nodes;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_pending_skill_metadata;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_pending_skill_references;
            DROP TABLE IF EXISTS temp.local_workspace_preserved_pending_skill_edges;
            CREATE TEMP TABLE local_workspace_preserved_semantic_nodes AS
              SELECT node.* FROM local_workspace_nodes node
              JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
              WHERE node.source_kind IN ('semantic_tool','semantic_subagent')
                AND receipt.source_family IN ('session_sdk','otel')
                AND node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            CREATE TEMP TABLE local_workspace_preserved_semantic_receipts AS
              SELECT receipt.* FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_preserved_semantic_nodes node ON node.node_id=receipt.node_id;
            CREATE TEMP TABLE local_workspace_preserved_semantic_references AS
              SELECT reference.* FROM local_workspace_node_source_references reference
              JOIN local_workspace_preserved_semantic_nodes node ON node.node_id=reference.node_id;
            CREATE TEMP TABLE local_workspace_preserved_semantic_tools AS
              SELECT metadata.* FROM local_workspace_tool_metadata metadata
              JOIN local_workspace_preserved_semantic_nodes node ON node.node_id=metadata.node_id;
            CREATE TEMP TABLE local_workspace_preserved_semantic_subagents AS
              SELECT lifecycle.* FROM local_workspace_subagent_lifecycle lifecycle
              JOIN local_workspace_preserved_semantic_nodes node ON node.node_id=lifecycle.node_id;
            CREATE TEMP TABLE local_workspace_preserved_semantic_edges AS
              SELECT edge.* FROM local_workspace_node_edges edge
              JOIN local_workspace_preserved_semantic_nodes node ON node.node_id=edge.node_id
              WHERE edge.relation_kind='parent';
            CREATE TEMP TABLE local_workspace_preserved_pending_skill_nodes AS
              SELECT node.* FROM local_workspace_nodes node
              JOIN local_workspace_skill_metadata metadata ON metadata.node_id=node.node_id
              WHERE node.source_kind='skill_invocation' AND metadata.current_valid_state IN ('current','certification_pending')
                AND node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
            CREATE TEMP TABLE local_workspace_preserved_pending_skill_metadata AS
              SELECT metadata.* FROM local_workspace_skill_metadata metadata
              JOIN local_workspace_preserved_pending_skill_nodes node ON node.node_id=metadata.node_id;
            CREATE TEMP TABLE local_workspace_preserved_pending_skill_references AS
              SELECT reference.* FROM local_workspace_node_source_references reference
              JOIN local_workspace_preserved_pending_skill_nodes node ON node.node_id=reference.node_id;
            CREATE TEMP TABLE local_workspace_preserved_pending_skill_edges AS
              SELECT edge.* FROM local_workspace_node_edges edge
              JOIN local_workspace_preserved_pending_skill_nodes node ON node.node_id=edge.node_id
              WHERE edge.relation_kind='parent';
            """, ("$ids", idsJson));
        var exactOtelProof = TableExists(connection, transaction, "monitor_spans")
            && ColumnExists(connection, transaction, "monitor_spans", "trace_id")
            && ColumnExists(connection, transaction, "monitor_spans", "span_id")
            && ColumnExists(connection, transaction, "monitor_spans", "operation")
            && ColumnExists(connection, transaction, "monitor_spans", "category")
            ? """
              EXISTS(
                SELECT 1 FROM local_workspace_preserved_semantic_references reference
                JOIN session_events event ON event.event_id=reference.event_id
                JOIN monitor_spans span ON span.trace_id=reference.trace_id COLLATE BINARY AND span.span_id=reference.span_id COLLATE BINARY
                WHERE reference.node_id=node.node_id AND reference.source_kind='otel_span'
                  AND event.source_adapter='otel-exact' COLLATE BINARY
                  AND event.type='otel.span' COLLATE BINARY
                  AND event.trace_id=span.trace_id COLLATE BINARY AND event.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                  AND span.operation='execute_tool' COLLATE BINARY AND span.category IN ('tool_call','error')
                  AND length(span.trace_id)=32 AND span.trace_id=lower(span.trace_id) AND span.trace_id NOT GLOB '*[^0-9a-f]*'
                  AND length(span.span_id)=16 AND span.span_id=lower(span.span_id) AND span.span_id NOT GLOB '*[^0-9a-f]*'
                  AND (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=span.trace_id COLLATE BINARY AND lower(owner.span_id)=span.span_id COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                    AND owner.type='otel.span' COLLATE BINARY
                    AND lower(owner.trace_id)=span.trace_id COLLATE BINARY AND lower(owner.source_event_id)=span.trace_id||'/'||span.span_id COLLATE BINARY)=1)
              """
            : "0";
        const string exactSdkToolProof = """
            EXISTS(
              SELECT 1 FROM local_workspace_preserved_semantic_receipts receipt
              JOIN local_workspace_preserved_semantic_references anchor ON anchor.node_id=receipt.node_id
              JOIN session_events start ON start.event_id=anchor.event_id
              JOIN session_runs run ON run.session_id=start.session_id AND run.run_id=start.run_id
              WHERE receipt.node_id=node.node_id AND receipt.source_family='session_sdk' AND receipt.semantic_kind='tool'
                AND anchor.source_kind='session_event' AND anchor.source_identity=start.event_id
                AND start.source_surface='copilot-sdk' COLLATE BINARY
                AND start.source_adapter='copilot-sdk-stream' COLLATE BINARY AND start.type='tool.execution_start'
                AND start.source_event_id IS NOT NULL AND length(start.source_event_id)>0
                AND run.source_surface='copilot-sdk' COLLATE BINARY
                AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
                AND (SELECT COUNT(*) FROM local_workspace_preserved_semantic_references candidate
                     JOIN session_events candidate_start ON candidate_start.event_id=candidate.event_id
                     WHERE candidate.node_id=node.node_id AND candidate_start.type='tool.execution_start')=1
                AND (SELECT COUNT(*) FROM session_runs candidate
                     WHERE candidate.session_id=start.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY
                       AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
                AND (SELECT COUNT(*) FROM session_native_ids binding
                     WHERE binding.session_id=start.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
                AND receipt.carrier_digest=local_workspace_semantic_digest('session_sdk_tool',
                  (SELECT binding.native_session_id FROM session_native_ids binding
                   WHERE binding.session_id=start.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY),
                  local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,start.source_event_id))
                AND EXISTS(SELECT 1 FROM session_events completion
                  WHERE completion.session_id=start.session_id AND completion.run_id=start.run_id
                    AND completion.source_surface=start.source_surface COLLATE BINARY
                    AND completion.source_adapter=start.source_adapter COLLATE BINARY
                    AND completion.type='tool.execution_complete' AND completion.parent_event_id=start.event_id))
            """;
        Execute(connection, transaction, $"""
            DELETE FROM local_workspace_preserved_semantic_nodes AS node
            WHERE (EXISTS(SELECT 1 FROM local_workspace_preserved_semantic_receipts receipt
                     WHERE receipt.node_id=node.node_id AND receipt.source_family='otel') AND NOT ({exactOtelProof}))
               OR (EXISTS(SELECT 1 FROM local_workspace_preserved_semantic_receipts receipt
                     WHERE receipt.node_id=node.node_id AND receipt.source_family='session_sdk' AND receipt.semantic_kind='tool')
                   AND NOT ({exactSdkToolProof}));
            DELETE FROM local_workspace_preserved_semantic_receipts WHERE node_id NOT IN (SELECT node_id FROM local_workspace_preserved_semantic_nodes);
            DELETE FROM local_workspace_preserved_semantic_references WHERE node_id NOT IN (SELECT node_id FROM local_workspace_preserved_semantic_nodes);
            DELETE FROM local_workspace_preserved_semantic_tools WHERE node_id NOT IN (SELECT node_id FROM local_workspace_preserved_semantic_nodes);
            DELETE FROM local_workspace_preserved_semantic_subagents WHERE node_id NOT IN (SELECT node_id FROM local_workspace_preserved_semantic_nodes);
            DELETE FROM local_workspace_preserved_semantic_edges WHERE node_id NOT IN (SELECT node_id FROM local_workspace_preserved_semantic_nodes);
            """);
    }

    private static void RestoreSemanticProjection(SqliteConnection connection, SqliteTransaction transaction, string pendingSkillSessionIdsJson) =>
        Execute(connection, transaction, """
            WITH missing AS (
              SELECT preserved.node_id,preserved.session_id,preserved.execution_id,
                     row_number() OVER(PARTITION BY preserved.execution_id ORDER BY preserved.node_id COLLATE BINARY) execution_ordinal,
                     row_number() OVER(PARTITION BY preserved.session_id ORDER BY preserved.execution_id COLLATE BINARY,preserved.node_id COLLATE BINARY) session_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes current WHERE current.session_id=preserved.session_id) existing_count
              FROM local_workspace_preserved_semantic_nodes preserved
              WHERE NOT EXISTS(SELECT 1 FROM local_workspace_nodes current WHERE current.node_id=preserved.node_id))
            UPDATE local_workspace_preserved_semantic_nodes AS preserved SET source_ordinal=
              COALESCE((SELECT MAX(current.source_ordinal) FROM local_workspace_nodes current WHERE current.execution_id=preserved.execution_id),0)
              +(SELECT missing.execution_ordinal FROM missing WHERE missing.node_id=preserved.node_id)
            WHERE preserved.node_id IN (SELECT node_id FROM missing WHERE existing_count+session_ordinal<=4097);
            WITH missing AS (
              SELECT preserved.node_id,preserved.session_id,
                     row_number() OVER(PARTITION BY preserved.session_id ORDER BY preserved.execution_id COLLATE BINARY,preserved.node_id COLLATE BINARY) session_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes current WHERE current.session_id=preserved.session_id) existing_count
              FROM local_workspace_preserved_semantic_nodes preserved
              WHERE NOT EXISTS(SELECT 1 FROM local_workspace_nodes current WHERE current.node_id=preserved.node_id))
            INSERT INTO local_workspace_nodes SELECT * FROM local_workspace_preserved_semantic_nodes
              WHERE node_id NOT IN (SELECT node_id FROM local_workspace_nodes)
                AND node_id IN (SELECT node_id FROM missing WHERE existing_count+session_ordinal<=4097);
            DELETE FROM local_workspace_preserved_semantic_nodes
              WHERE NOT EXISTS(SELECT 1 FROM local_workspace_nodes current WHERE current.node_id=local_workspace_preserved_semantic_nodes.node_id);
            CREATE TEMP TABLE local_workspace_merged_semantic_receipts AS
              WITH combined AS (
                SELECT * FROM local_workspace_semantic_receipts
                UNION ALL
                SELECT * FROM local_workspace_preserved_semantic_receipts)
              SELECT node_id,MIN(semantic_kind) semantic_kind,MIN(source_family) source_family,MIN(scope_kind) scope_kind,
                     MIN(carrier_digest) carrier_digest,MIN(authority_receipt) authority_receipt,
                     COUNT(DISTINCT authority_receipt) authority_count,
                     COUNT(DISTINCT semantic_kind||'|'||source_family||'|'||scope_kind||'|'||carrier_digest) identity_count
              FROM combined GROUP BY node_id;
            DELETE FROM local_workspace_semantic_receipts
              WHERE node_id IN (SELECT node_id FROM local_workspace_merged_semantic_receipts);
            INSERT INTO local_workspace_semantic_receipts(node_id,semantic_kind,source_family,scope_kind,carrier_digest,authority_receipt)
              SELECT node_id,semantic_kind,source_family,scope_kind,carrier_digest,authority_receipt
              FROM local_workspace_merged_semantic_receipts;
            CREATE TEMP TABLE local_workspace_merged_semantic_references AS
              WITH combined AS (
                SELECT reference.node_id,reference.source_kind,reference.source_identity,reference.trace_id,reference.span_id,reference.event_id,reference.revision_input,0 freshness
                FROM local_workspace_node_source_references reference
                JOIN local_workspace_merged_semantic_receipts receipt ON receipt.node_id=reference.node_id
                UNION ALL
                SELECT node_id,source_kind,source_identity,trace_id,span_id,event_id,revision_input,1
                FROM local_workspace_preserved_semantic_references),
              ranked AS (
                SELECT combined.*,row_number() OVER(PARTITION BY node_id,source_kind,source_identity,event_id
                  ORDER BY freshness,revision_input COLLATE BINARY) identity_rank FROM combined)
              SELECT node_id,source_kind,source_identity,trace_id,span_id,event_id,revision_input
              FROM ranked WHERE identity_rank=1;
            DELETE FROM local_workspace_node_source_references
              WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);
            INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
            SELECT node_id,ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input
            FROM (
              SELECT merged.*,row_number() OVER(PARTITION BY node_id ORDER BY
                CASE WHEN EXISTS(SELECT 1 FROM session_events event WHERE event.event_id=merged.event_id AND event.type='tool.execution_start') THEN 0 ELSE 1 END,
                source_kind COLLATE BINARY,source_identity COLLATE BINARY,event_id COLLATE BINARY)-1 ordinal
              FROM local_workspace_merged_semantic_references merged)
            WHERE ordinal<16;
            INSERT OR IGNORE INTO local_workspace_tool_metadata SELECT * FROM local_workspace_preserved_semantic_tools;
            INSERT OR IGNORE INTO local_workspace_subagent_lifecycle SELECT * FROM local_workspace_preserved_semantic_subagents;
            WITH facts AS (
              SELECT receipt.node_id,
                     receipt.authority_count authority_count,
                     receipt.identity_count,COUNT(reference.event_id) reference_count,
                     SUM(event.type='tool.execution_start' OR instr(reference.revision_input,'|otel.tool.started|')>0) started_count,
                     SUM(event.type='tool.execution_complete' OR instr(reference.revision_input,'|otel.tool.completed|')>0) completed_count,
                     SUM(instr(reference.revision_input,'|otel.tool.failed|')>0) failed_count
              FROM local_workspace_merged_semantic_receipts receipt
              JOIN local_workspace_merged_semantic_references reference ON reference.node_id=receipt.node_id
              JOIN session_events event ON event.event_id=reference.event_id
              WHERE receipt.semantic_kind='tool' GROUP BY receipt.node_id)
            UPDATE local_workspace_tool_metadata AS metadata SET
              started_state=CASE WHEN metadata.started_state='inconsistent' OR facts.started_count>1 OR facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1 THEN 'inconsistent' WHEN facts.started_count=1 THEN 'recorded' ELSE 'not_observed' END,
              completed_state=CASE WHEN metadata.completed_state='inconsistent' OR facts.completed_count>1 OR facts.completed_count>0 AND facts.failed_count>0 OR facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1 THEN 'inconsistent' WHEN facts.completed_count=1 THEN 'recorded' ELSE 'not_observed' END,
              failed_state=CASE WHEN metadata.failed_state='inconsistent' OR facts.failed_count>1 OR facts.completed_count>0 AND facts.failed_count>0 OR facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1 THEN 'inconsistent' WHEN facts.failed_count=1 THEN 'recorded' ELSE 'not_observed' END
            FROM facts WHERE metadata.node_id=facts.node_id;
            WITH facts AS (
              SELECT receipt.node_id,receipt.source_family,
                     receipt.authority_count authority_count,
                     receipt.identity_count,COUNT(reference.event_id) reference_count,
                     SUM(event.type='tool.execution_start' OR instr(reference.revision_input,'|otel.tool.started|')>0) started_count,
                     SUM(event.type='tool.execution_complete' OR instr(reference.revision_input,'|otel.tool.completed|')>0) completed_count,
                     SUM(instr(reference.revision_input,'|otel.tool.failed|')>0) failed_count
              FROM local_workspace_merged_semantic_receipts receipt
              JOIN local_workspace_merged_semantic_references reference ON reference.node_id=receipt.node_id
              JOIN session_events event ON event.event_id=reference.event_id
              WHERE receipt.semantic_kind='tool' GROUP BY receipt.node_id,receipt.source_family)
            UPDATE local_workspace_nodes AS node SET
              lifecycle=CASE WHEN EXISTS(SELECT 1 FROM local_workspace_tool_metadata metadata WHERE metadata.node_id=facts.node_id
                               AND (metadata.started_state='inconsistent' OR metadata.completed_state='inconsistent' OR metadata.failed_state='inconsistent')) THEN 'unknown'
                             WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.failed_count=1 AND facts.completed_count=0 AND facts.started_count<=1 THEN 'failed'
                             WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.completed_count=1 AND facts.failed_count=0 AND facts.started_count<=1 THEN 'completed'
                             WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.started_count=1 AND facts.completed_count=0 AND facts.failed_count=0 THEN 'started' ELSE 'unknown' END,
              status=CASE WHEN facts.source_family='session_sdk' THEN 'unknown'
                          WHEN EXISTS(SELECT 1 FROM local_workspace_tool_metadata metadata WHERE metadata.node_id=facts.node_id
                            AND (metadata.started_state='inconsistent' OR metadata.completed_state='inconsistent' OR metadata.failed_state='inconsistent')) THEN 'unknown'
                          WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.failed_count=1 AND facts.completed_count=0 AND facts.started_count<=1 THEN 'failed'
                          WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.completed_count=1 AND facts.failed_count=0 AND facts.started_count<=1 THEN 'completed'
                          WHEN facts.authority_count=1 AND facts.identity_count=1 AND facts.reference_count<=16 AND facts.started_count=1 AND facts.completed_count=0 AND facts.failed_count=0 THEN 'active' ELSE 'unknown' END
            FROM facts WHERE node.node_id=facts.node_id;
            WITH facts AS (
              SELECT receipt.node_id,
                     receipt.authority_count authority_count,
                     receipt.identity_count,COUNT(reference.event_id) reference_count,
                     SUM(event.type='subagent.selected') selected_count,SUM(event.type='subagent.started') started_count,
                     SUM(event.type='subagent.completed') completed_count,SUM(event.type='subagent.failed') failed_count,
                     SUM(event.type='subagent.deselected') deselected_count
              FROM local_workspace_merged_semantic_receipts receipt
              JOIN local_workspace_merged_semantic_references reference ON reference.node_id=receipt.node_id
              JOIN session_events event ON event.event_id=reference.event_id
              WHERE receipt.semantic_kind='subagent' GROUP BY receipt.node_id)
            UPDATE local_workspace_subagent_lifecycle AS lifecycle SET
              selected_state=CASE WHEN lifecycle.selected_state='inconsistent' OR facts.selected_count>1 OR facts.selected_count>0 AND (facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1) THEN 'inconsistent' WHEN facts.selected_count=1 THEN 'recorded' ELSE 'not_observed' END,
              started_state=CASE WHEN lifecycle.started_state='inconsistent' OR facts.started_count>1 OR facts.started_count>0 AND (facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1) THEN 'inconsistent' WHEN facts.started_count=1 THEN 'recorded' ELSE 'not_observed' END,
              completed_state=CASE WHEN lifecycle.completed_state='inconsistent' OR facts.completed_count>1 OR facts.completed_count>0 AND (facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1) THEN 'inconsistent' WHEN facts.completed_count=1 THEN 'recorded' ELSE 'not_observed' END,
              failed_state=CASE WHEN lifecycle.failed_state='inconsistent' OR facts.failed_count>1 OR facts.failed_count>0 AND (facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1) THEN 'inconsistent' WHEN facts.failed_count=1 THEN 'recorded' ELSE 'not_observed' END,
              deselected_state=CASE WHEN lifecycle.deselected_state='inconsistent' OR facts.deselected_count>1 OR facts.deselected_count>0 AND (facts.reference_count>16 OR facts.authority_count>1 OR facts.identity_count>1) THEN 'inconsistent' WHEN facts.deselected_count=1 THEN 'recorded' ELSE 'not_observed' END
            FROM facts WHERE lifecycle.node_id=facts.node_id;
            INSERT OR IGNORE INTO local_workspace_node_edges
            SELECT edge.* FROM local_workspace_preserved_semantic_edges edge
            JOIN local_workspace_nodes node ON node.node_id=edge.node_id
            WHERE node.relationship_authority IN ('exact','explicit')
              AND edge.relation_kind='parent'
              AND edge.related_node_id=node.parent_node_id
              AND edge.relationship_authority=node.relationship_authority;
            INSERT OR IGNORE INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,
              retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,retention_ownership_receipt,retention_owner_token,availability_state)
            SELECT DISTINCT receipt.node_id,content.part,content.store_kind,content.source_item_id,content.locator_kind,content.json_pointer,content.selected_utf8_bytes,content.revision_input,
              content.retention_item_id,content.retention_store_instance_id,content.source_captured_at,content.source_expires_at,content.retention_revision,content.retention_ownership_receipt,content.retention_owner_token,content.availability_state
            FROM local_workspace_semantic_receipts receipt
            JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.event_id IS NOT NULL
            JOIN session_events event ON event.event_id=reference.event_id
            JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=reference.event_id
            JOIN local_workspace_node_content_refs content ON content.node_id=raw.node_id
            WHERE receipt.semantic_kind='tool'
              AND (content.availability_state<>'not_captured' OR event.type='tool.execution_start' AND NOT EXISTS(
                SELECT 1 FROM local_workspace_node_source_references available_reference
                JOIN local_workspace_nodes available_raw ON available_raw.source_kind='session_event' AND available_raw.source_identity=available_reference.event_id
                JOIN local_workspace_node_content_refs available_content ON available_content.node_id=available_raw.node_id AND available_content.part=content.part
                WHERE available_reference.node_id=receipt.node_id AND available_content.availability_state<>'not_captured'))
              AND NOT EXISTS (
                SELECT 1 FROM local_workspace_node_source_references other_reference
                JOIN local_workspace_nodes other_raw ON other_raw.source_kind='session_event' AND other_raw.source_identity=other_reference.event_id
                JOIN local_workspace_node_content_refs other_content ON other_content.node_id=other_raw.node_id AND other_content.part=content.part
                WHERE other_reference.node_id=receipt.node_id AND other_content.source_item_id<>content.source_item_id);
            WITH competing AS (
              SELECT receipt.node_id,content.part,MIN(content.source_item_id) source_item_id,COUNT(DISTINCT content.source_item_id) source_count
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_merged_semantic_references reference ON reference.node_id=receipt.node_id
              JOIN session_events event ON event.event_id=reference.event_id
              JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=reference.event_id
              JOIN local_workspace_node_content_refs content ON content.node_id=raw.node_id
              WHERE receipt.semantic_kind='tool'
                AND (content.availability_state<>'not_captured' OR event.type='tool.execution_start' AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_merged_semantic_references available_reference
                  JOIN local_workspace_nodes available_raw ON available_raw.source_kind='session_event' AND available_raw.source_identity=available_reference.event_id
                  JOIN local_workspace_node_content_refs available_content ON available_content.node_id=available_raw.node_id AND available_content.part=content.part
                  WHERE available_reference.node_id=receipt.node_id AND available_content.availability_state<>'not_captured'))
              GROUP BY receipt.node_id,content.part HAVING COUNT(DISTINCT content.source_item_id)>1)
            INSERT OR REPLACE INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,availability_state)
              SELECT node_id,part,'session_event_content',source_item_id,
                     CASE part WHEN 'event_content' THEN 'whole_event' ELSE 'json_pointer' END,
                     CASE part WHEN 'instruction' THEN '/prompt' WHEN 'tool_input' THEN '/tool_input' WHEN 'tool_result' THEN '/tool_response' WHEN 'error_message' THEN '/error' END,NULL,
                     'projection_invalid|competing_exact_sources|'||source_count,'invalid'
              FROM competing;
            DROP TABLE local_workspace_preserved_semantic_nodes;
            DROP TABLE local_workspace_preserved_semantic_receipts;
            DROP TABLE local_workspace_preserved_semantic_references;
            DROP TABLE local_workspace_preserved_semantic_tools;
            DROP TABLE local_workspace_preserved_semantic_subagents;
            DROP TABLE local_workspace_preserved_semantic_edges;
            DROP TABLE local_workspace_merged_semantic_references;
            DROP TABLE local_workspace_merged_semantic_receipts;
            WITH candidates AS (
              SELECT preserved.node_id,row_number() OVER(PARTITION BY preserved.session_id ORDER BY preserved.execution_id COLLATE BINARY,preserved.node_id COLLATE BINARY) candidate_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes current WHERE current.session_id=preserved.session_id) existing_count
              FROM local_workspace_preserved_pending_skill_nodes preserved
              WHERE preserved.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($pending_skill_ids))
                AND EXISTS(SELECT 1 FROM local_workspace_execution_headers execution WHERE execution.execution_id=preserved.execution_id)
                AND NOT EXISTS(SELECT 1 FROM local_workspace_nodes current WHERE current.node_id=preserved.node_id))
            INSERT INTO local_workspace_nodes
              SELECT preserved.* FROM local_workspace_preserved_pending_skill_nodes preserved
              JOIN candidates ON candidates.node_id=preserved.node_id
              WHERE candidates.existing_count+candidates.candidate_ordinal<=4097;
            INSERT INTO local_workspace_skill_metadata
              SELECT metadata.node_id,'certification_pending',metadata.source_state,metadata.source,metadata.trigger_state,metadata.trigger,
                     'unavailable',NULL,metadata.historical_snapshot_reference_state,metadata.historical_snapshot_reference,metadata.registry_generation_identity
              FROM local_workspace_preserved_pending_skill_metadata metadata
              JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
              WHERE node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($pending_skill_ids))
                AND NOT EXISTS(SELECT 1 FROM local_workspace_skill_metadata current WHERE current.node_id=metadata.node_id);
            UPDATE local_workspace_nodes AS node SET skill_activity_state='certification_pending',skill_activity_count=NULL
              WHERE node.node_id IN (SELECT metadata.node_id FROM local_workspace_skill_metadata metadata
                WHERE metadata.current_valid_state='certification_pending');
            INSERT INTO local_workspace_node_source_references
              SELECT reference.* FROM local_workspace_preserved_pending_skill_references reference
              JOIN local_workspace_nodes node ON node.node_id=reference.node_id
              WHERE node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($pending_skill_ids));
            INSERT OR IGNORE INTO local_workspace_node_edges
              SELECT edge.* FROM local_workspace_preserved_pending_skill_edges edge
              JOIN local_workspace_nodes node ON node.node_id=edge.node_id
              WHERE node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($pending_skill_ids))
                AND edge.relation_kind='parent'
                AND node.relationship_authority IN ('exact','explicit')
                AND edge.related_node_id=node.parent_node_id
                AND edge.relationship_authority=node.relationship_authority;
            DROP TABLE local_workspace_preserved_pending_skill_nodes;
            DROP TABLE local_workspace_preserved_pending_skill_metadata;
            DROP TABLE local_workspace_preserved_pending_skill_references;
            DROP TABLE local_workspace_preserved_pending_skill_edges;
            """, ("$pending_skill_ids", pendingSkillSessionIdsJson));

    private static void RefreshSemanticProjection(SqliteConnection connection, SqliteTransaction transaction, string idsJson)
    {
        Execute(connection, transaction, """
            DROP TABLE IF EXISTS temp.local_workspace_semantic_candidates;
            CREATE TEMP TABLE local_workspace_semantic_candidates AS
            SELECT 'tool' semantic_kind,'session_sdk' source_family,'native_run' scope_kind,
                   local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                     local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,e.source_event_id)) carrier_digest,
                   e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,e.type,e.occurred_at,
                   NULL tool_name,NULL mcp_tool_name,
                   e.source_adapter||'|exact_sdk_tool|v1' authority_receipt
            FROM session_events e JOIN session_runs run ON run.session_id=e.session_id AND run.run_id=e.run_id
            JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
            JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=e.event_id
            WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND e.source_surface='copilot-sdk' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
              AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY AND e.type='tool.execution_start'
              AND e.source_event_id IS NOT NULL AND length(e.source_event_id)>0
              AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
              AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
              AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=e.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
              AND EXISTS(SELECT 1 FROM session_events completion
                WHERE completion.session_id=e.session_id AND completion.run_id=e.run_id
                  AND completion.source_surface=e.source_surface COLLATE BINARY AND completion.source_adapter=e.source_adapter COLLATE BINARY
                  AND completion.type='tool.execution_complete' AND completion.parent_event_id=e.event_id)
            UNION ALL
            SELECT 'tool','session_sdk','native_run',
                   local_workspace_semantic_digest('session_sdk_tool',native.native_session_id,
                     local_workspace_semantic_digest('session_sdk_tool_run',run.native_run_id,p.source_event_id)),
                   e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,e.type,e.occurred_at,NULL,NULL,
                   e.source_adapter||'|exact_sdk_tool|v1'
            FROM session_events e JOIN session_events p
              ON p.event_id=e.parent_event_id AND p.session_id=e.session_id AND p.run_id=e.run_id
             AND p.source_adapter=e.source_adapter COLLATE BINARY AND p.type='tool.execution_start'
            JOIN session_runs run ON run.session_id=e.session_id AND run.run_id=e.run_id
            JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
            JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=e.event_id
            WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND e.source_surface='copilot-sdk' COLLATE BINARY AND p.source_surface='copilot-sdk' COLLATE BINARY AND run.source_surface='copilot-sdk' COLLATE BINARY
              AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY AND e.type='tool.execution_complete'
              AND p.source_event_id IS NOT NULL AND length(p.source_event_id)>0
              AND run.native_run_id IS NOT NULL AND length(run.native_run_id)>0
              AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
              AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=e.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=run.native_run_id COLLATE BINARY)=1
            UNION ALL
            SELECT 'subagent','session_sdk','native_run',
                   local_workspace_semantic_digest('session_sdk_subagent',native.native_session_id,r.native_run_id),
                   e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,e.type,e.occurred_at,NULL,NULL,
                   e.source_adapter||'|native_run|v1'
            FROM session_events e JOIN session_runs r ON r.session_id=e.session_id AND r.run_id=e.run_id
            JOIN session_native_ids native ON native.session_id=e.session_id AND native.source_surface='copilot-sdk' COLLATE BINARY
            JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=e.event_id
            WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND e.source_adapter='copilot-sdk-stream' COLLATE BINARY
              AND e.source_surface='copilot-sdk' COLLATE BINARY AND r.source_surface='copilot-sdk' COLLATE BINARY
              AND e.type IN ('subagent.selected','subagent.started','subagent.completed','subagent.failed','subagent.deselected')
              AND r.native_run_id IS NOT NULL AND length(r.native_run_id)>0
              AND (SELECT COUNT(*) FROM session_native_ids binding WHERE binding.session_id=e.session_id AND binding.source_surface='copilot-sdk' COLLATE BINARY)=1
              AND (SELECT COUNT(*) FROM session_runs candidate WHERE candidate.session_id=e.session_id AND candidate.source_surface='copilot-sdk' COLLATE BINARY AND candidate.native_run_id=r.native_run_id COLLATE BINARY)=1;

            """, ("$ids", idsJson));

        var hasSemanticMonitorSpans = TableExists(connection, transaction, "monitor_spans")
            && ColumnExists(connection, transaction, "monitor_spans", "parent_span_id")
            && ColumnExists(connection, transaction, "monitor_spans", "operation")
            && ColumnExists(connection, transaction, "monitor_spans", "category")
            && ColumnExists(connection, transaction, "monitor_spans", "status")
            && ColumnExists(connection, transaction, "monitor_spans", "start_time")
            && ColumnExists(connection, transaction, "monitor_spans", "end_time")
            && ColumnExists(connection, transaction, "monitor_spans", "mcp_tool_name")
            && ColumnExists(connection, transaction, "monitor_spans", "mcp_server_hash");
        if (hasSemanticMonitorSpans)
        {
            Execute(connection, transaction, """
                INSERT INTO local_workspace_semantic_candidates
                SELECT 'tool','otel','otel_span',
                       local_workspace_semantic_digest('otel_tool',e.trace_id,m.span_id),
                       e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,
                       CASE WHEN local_workspace_ticks(m.start_time) IS NOT NULL THEN 'otel.tool.started' ELSE 'otel.tool.observed' END,
                       e.occurred_at,m.tool_name,m.mcp_tool_name,'otel-exact|normalized-tool-span|v1'
                FROM session_events e JOIN monitor_spans m
                  ON e.source_adapter='otel-exact' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY
                 AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
                JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=e.event_id
                WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                  AND e.run_id IS NOT NULL AND e.type='otel.span' COLLATE BINARY
                  AND m.operation='execute_tool' COLLATE BINARY AND m.category IN ('tool_call','error')
                  AND length(m.trace_id)=32 AND m.trace_id=lower(m.trace_id) AND m.trace_id NOT GLOB '*[^0-9a-f]*'
                  AND length(m.span_id)=16 AND m.span_id=lower(m.span_id) AND m.span_id NOT GLOB '*[^0-9a-f]*'
                  AND (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=m.trace_id COLLATE BINARY AND lower(owner.span_id)=m.span_id COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                       AND owner.type='otel.span' COLLATE BINARY
                       AND lower(owner.trace_id)=m.trace_id COLLATE BINARY AND lower(owner.source_event_id)=m.trace_id||'/'||m.span_id COLLATE BINARY)=1;
                INSERT INTO local_workspace_semantic_candidates
                SELECT 'tool','otel','otel_span',
                       local_workspace_semantic_digest('otel_tool',e.trace_id,m.span_id),
                       e.session_id,e.run_id,e.event_id,e.source_adapter,e.source_event_id,
                       CASE WHEN m.status='error' COLLATE BINARY THEN 'otel.tool.failed' ELSE 'otel.tool.completed' END,
                       e.occurred_at,m.tool_name,m.mcp_tool_name,'otel-exact|normalized-tool-span|v1'
                FROM session_events e JOIN monitor_spans m
                  ON e.source_adapter='otel-exact' COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY
                 AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
                JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=e.event_id
                WHERE e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                  AND e.run_id IS NOT NULL AND e.type='otel.span' COLLATE BINARY
                  AND m.operation='execute_tool' COLLATE BINARY AND m.category IN ('tool_call','error')
                  AND (m.status IN ('ok','error') OR local_workspace_ticks(m.end_time) IS NOT NULL)
                  AND length(m.trace_id)=32 AND m.trace_id=lower(m.trace_id) AND m.trace_id NOT GLOB '*[^0-9a-f]*'
                  AND length(m.span_id)=16 AND m.span_id=lower(m.span_id) AND m.span_id NOT GLOB '*[^0-9a-f]*'
                  AND (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=m.trace_id COLLATE BINARY AND lower(owner.span_id)=m.span_id COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                       AND owner.type='otel.span' COLLATE BINARY
                       AND lower(owner.trace_id)=m.trace_id COLLATE BINARY AND lower(owner.source_event_id)=m.trace_id||'/'||m.span_id COLLATE BINARY)=1;
                """, ("$ids", idsJson));
            Execute(connection, transaction, """
                WITH unresolved AS (
                  SELECT DISTINCT candidate.session_id,candidate.run_id,header.execution_id
                  FROM local_workspace_semantic_candidates candidate
                  JOIN local_workspace_execution_headers header
                    ON header.session_id=candidate.session_id AND header.source_identity=candidate.run_id
                  JOIN session_events child ON child.event_id=candidate.event_id
                  JOIN monitor_spans span
                    ON span.trace_id=child.trace_id COLLATE BINARY
                   AND child.source_event_id=span.trace_id||'/'||span.span_id COLLATE BINARY
                  WHERE candidate.source_family='otel' AND candidate.semantic_kind='tool'
                    AND span.parent_span_id IS NOT NULL
                    AND ((SELECT COUNT(*) FROM session_events parent
                          WHERE parent.session_id=child.session_id AND parent.run_id=child.run_id
                            AND parent.source_adapter='otel-exact' COLLATE BINARY
                            AND parent.type='otel.span' COLLATE BINARY
                            AND parent.trace_id=span.trace_id COLLATE BINARY
                            AND parent.source_event_id=span.trace_id||'/'||span.parent_span_id COLLATE BINARY)<>1
                      OR (SELECT COUNT(*) FROM monitor_spans parent_owner
                          WHERE lower(parent_owner.trace_id)=span.trace_id COLLATE BINARY
                            AND lower(parent_owner.span_id)=span.parent_span_id COLLATE BINARY)<>1)),
                ranked AS (
                  SELECT unresolved.*,
                         row_number() OVER(PARTITION BY session_id ORDER BY run_id COLLATE BINARY) candidate_ordinal,
                         (SELECT COUNT(*) FROM local_workspace_nodes existing
                          WHERE existing.session_id=unresolved.session_id) existing_count
                  FROM unresolved)
                INSERT OR IGNORE INTO local_workspace_nodes(
                  node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,
                  relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state)
                SELECT local_workspace_node_id('unknown_relation_group',run_id),session_id,execution_id,
                       'unknown_relation_group',run_id,
                       (SELECT COUNT(*)+1 FROM local_workspace_nodes node WHERE node.execution_id=ranked.execution_id),
                       NULL,'unknown','unknown_relation_group','not_observed',NULL,'unknown','unknown','missing',NULL,'not_observed'
                FROM ranked WHERE existing_count+candidate_ordinal<=4097;
                """);
        }

        Execute(connection, transaction, """
            WITH groups AS (
              SELECT c.semantic_kind,c.source_family,c.scope_kind,c.carrier_digest,
                     (SELECT chosen.session_id FROM local_workspace_semantic_candidates chosen WHERE chosen.semantic_kind=c.semantic_kind AND chosen.source_family=c.source_family AND chosen.carrier_digest=c.carrier_digest ORDER BY chosen.event_id COLLATE BINARY LIMIT 1) session_id,
                     (SELECT chosen.run_id FROM local_workspace_semantic_candidates chosen WHERE chosen.semantic_kind=c.semantic_kind AND chosen.source_family=c.source_family AND chosen.carrier_digest=c.carrier_digest ORDER BY chosen.event_id COLLATE BINARY LIMIT 1) run_id,
                     MIN(c.authority_receipt) authority_receipt,COUNT(DISTINCT c.authority_receipt) authority_count,COUNT(DISTINCT c.event_id) reference_count,
                     SUM(c.type IN ('PreToolUse','tool.execution_start','otel.tool.started')) started_count,SUM(c.type IN ('PostToolUse','tool.execution_complete','otel.tool.completed')) completed_count,SUM(c.type IN ('PostToolUseFailure','otel.tool.failed')) failed_count,
                     COUNT(DISTINCT c.tool_name) tool_name_count,MIN(c.tool_name) tool_name,
                     COUNT(DISTINCT c.mcp_tool_name) mcp_tool_name_count,MIN(c.mcp_tool_name) mcp_tool_name
              FROM local_workspace_semantic_candidates c GROUP BY c.semantic_kind,c.source_family,c.scope_kind,c.carrier_digest),
            candidate_rows AS (
              SELECT g.*,h.execution_id,(SELECT COUNT(*) FROM local_workspace_nodes n WHERE n.execution_id=h.execution_id)+
                     row_number() OVER(PARTITION BY h.execution_id ORDER BY g.semantic_kind,g.carrier_digest COLLATE BINARY) source_ordinal
              FROM groups g JOIN local_workspace_execution_headers h ON h.session_id=g.session_id AND h.source_identity=g.run_id),
            ranked AS (
              SELECT candidate_rows.*,row_number() OVER(PARTITION BY session_id ORDER BY execution_id COLLATE BINARY,semantic_kind,carrier_digest COLLATE BINARY) candidate_ordinal,
                     (SELECT COUNT(*) FROM local_workspace_nodes existing WHERE existing.session_id=candidate_rows.session_id) existing_count
              FROM candidate_rows)
            INSERT INTO local_workspace_nodes(node_id,session_id,execution_id,source_kind,source_identity,source_ordinal,parent_node_id,relationship_authority,kind,name_state,name_text,lifecycle,status,time_authority,start_utc_ticks,token_state,
                tool_activity_state,tool_activity_count,subagent_activity_state,subagent_activity_count)
            SELECT local_workspace_node_id(CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest),session_id,execution_id,
                   CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest,source_ordinal,
                   local_workspace_node_id('execution_root',run_id),'exact',semantic_kind,
                   CASE WHEN semantic_kind='tool' AND tool_name_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN semantic_kind='tool' AND tool_name_count=1 THEN tool_name END,
                   CASE WHEN semantic_kind='tool' AND authority_count=1 AND failed_count=1 AND completed_count=0 AND started_count<=1 THEN 'failed'
                        WHEN semantic_kind='tool' AND authority_count=1 AND completed_count=1 AND failed_count=0 AND started_count<=1 THEN 'completed'
                        WHEN semantic_kind='tool' AND authority_count=1 AND started_count=1 AND completed_count=0 AND failed_count=0 THEN 'started'
                        ELSE 'unknown' END,
                   CASE WHEN source_family='otel' AND semantic_kind='tool' AND authority_count=1 AND failed_count=1 AND completed_count=0 AND started_count<=1 THEN 'failed'
                        WHEN source_family='otel' AND semantic_kind='tool' AND authority_count=1 AND completed_count=1 AND failed_count=0 AND started_count<=1 THEN 'completed'
                        WHEN source_family='otel' AND semantic_kind='tool' AND authority_count=1 AND started_count=1 AND completed_count=0 AND failed_count=0 THEN 'active'
                        ELSE 'unknown' END,
                   'missing',NULL,'not_observed',
                   CASE semantic_kind WHEN 'tool' THEN 'recorded' ELSE 'not_observed' END,CASE semantic_kind WHEN 'tool' THEN 1 END,
                   CASE semantic_kind WHEN 'subagent' THEN 'recorded' ELSE 'not_observed' END,CASE semantic_kind WHEN 'subagent' THEN 1 END
            FROM ranked WHERE existing_count+candidate_ordinal<=4097;

            INSERT INTO local_workspace_semantic_receipts(node_id,semantic_kind,source_family,scope_kind,carrier_digest,authority_receipt)
            SELECT local_workspace_node_id(CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest),semantic_kind,source_family,scope_kind,carrier_digest,MIN(authority_receipt)
            FROM local_workspace_semantic_candidates
            WHERE EXISTS(SELECT 1 FROM local_workspace_nodes node WHERE node.node_id=local_workspace_node_id(
              CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest))
            GROUP BY semantic_kind,source_family,scope_kind,carrier_digest;

            WITH exact_references AS (
              SELECT c.semantic_kind,c.source_family,c.carrier_digest,c.event_id,MIN(c.source_adapter) source_adapter,
                     MIN(c.source_event_id) source_event_id,MIN(c.type)||'|'||MAX(c.type)||'|'||COUNT(DISTINCT c.type) type,MIN(c.occurred_at) occurred_at,MIN(c.authority_receipt) authority_receipt,
                     MAX(c.source_family='session_sdk' AND c.type='tool.execution_start') required_start
              FROM local_workspace_semantic_candidates c GROUP BY c.semantic_kind,c.source_family,c.carrier_digest,c.event_id),
            ranked AS (
              SELECT c.*,row_number() OVER(PARTITION BY c.semantic_kind,c.source_family,c.carrier_digest ORDER BY c.required_start DESC,c.event_id COLLATE BINARY)-1 ordinal
              FROM exact_references c)
            INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input)
            SELECT local_workspace_node_id(CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest),ordinal,
                   CASE source_family WHEN 'otel' THEN 'otel_span' ELSE 'session_event' END,ranked.event_id,
                   CASE source_family WHEN 'otel' THEN e.trace_id END,
                   CASE source_family WHEN 'otel' THEN substr(e.source_event_id,length(e.trace_id)+2) END,ranked.event_id,
                   ranked.source_adapter||'|'||ranked.source_event_id||'|'||ranked.type||'|'||COALESCE(ranked.occurred_at,'')||'|'||ranked.authority_receipt
            FROM ranked JOIN session_events e ON e.event_id=ranked.event_id
            WHERE ordinal<16 AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
              WHERE receipt.node_id=local_workspace_node_id(CASE semantic_kind WHEN 'tool' THEN 'semantic_tool' ELSE 'semantic_subagent' END,carrier_digest));

            UPDATE local_workspace_nodes AS node SET
              trace_id=reference.trace_id,span_id=reference.span_id,event_id=reference.event_id
            FROM local_workspace_semantic_receipts receipt
            JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.source_ordinal=0
            WHERE node.node_id=receipt.node_id AND receipt.semantic_kind='tool' AND receipt.source_family='otel';

            WITH groups AS (
              SELECT source_family,carrier_digest,SUM(type IN ('PreToolUse','tool.execution_start','otel.tool.started')) started_count,SUM(type IN ('PostToolUse','tool.execution_complete','otel.tool.completed')) completed_count,
                     SUM(type IN ('PostToolUseFailure','otel.tool.failed')) failed_count,COUNT(DISTINCT event_id) reference_count,COUNT(DISTINCT authority_receipt) authority_count,
                     COUNT(DISTINCT mcp_tool_name) mcp_tool_name_count,MIN(mcp_tool_name) mcp_tool_name
              FROM local_workspace_semantic_candidates WHERE semantic_kind='tool' GROUP BY source_family,carrier_digest)
            INSERT INTO local_workspace_tool_metadata(node_id,caller_state,caller_node_id,started_state,completed_state,failed_state,exit_state,exit_code,
              mcp_server_identity_state,mcp_server_identity,mcp_server_name_state,mcp_server_name,mcp_tool_name_state,mcp_tool_name,retry_state,recovery_state,child_activity_state,child_activity_count)
            SELECT local_workspace_node_id('semantic_tool',carrier_digest),'not_observed',NULL,
                   CASE WHEN started_count>1 OR reference_count>16 OR authority_count>1 THEN 'inconsistent' WHEN started_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN completed_count>1 OR completed_count>0 AND failed_count>0 OR reference_count>16 OR authority_count>1 THEN 'inconsistent' WHEN completed_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN failed_count>1 OR completed_count>0 AND failed_count>0 OR reference_count>16 OR authority_count>1 THEN 'inconsistent' WHEN failed_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   'source_unsupported',NULL,'not_observed',NULL,'source_unsupported',NULL,
                   CASE WHEN mcp_tool_name_count=1 THEN 'recorded' WHEN mcp_tool_name_count=0 THEN 'not_observed' ELSE 'invalid' END,
                   CASE WHEN mcp_tool_name_count=1 THEN mcp_tool_name END,
                   'not_observed','not_observed','not_observed',NULL
            FROM groups WHERE EXISTS(SELECT 1 FROM local_workspace_nodes node
              WHERE node.node_id=local_workspace_node_id('semantic_tool',groups.carrier_digest));

            UPDATE local_workspace_nodes AS node SET
              parent_node_id=local_workspace_node_id('session_event',parent.event_id),relationship_authority='exact'
            FROM local_workspace_semantic_receipts receipt
            JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id
            JOIN session_events start ON start.event_id=reference.event_id AND start.type='tool.execution_start'
            JOIN session_events parent ON parent.event_id=start.parent_event_id AND parent.session_id=start.session_id AND parent.run_id=start.run_id
              AND parent.source_adapter=start.source_adapter COLLATE BINARY
            WHERE node.node_id=receipt.node_id AND receipt.source_family='session_sdk';
            UPDATE local_workspace_nodes AS node SET
              parent_node_id=local_workspace_node_id('unknown_relation_group',start.run_id),relationship_authority='unknown'
            FROM local_workspace_semantic_receipts receipt
            JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id
            JOIN session_events start ON start.event_id=reference.event_id AND start.type='tool.execution_start'
            WHERE node.node_id=receipt.node_id AND receipt.source_family='session_sdk'
              AND start.parent_event_id IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM session_events parent
                WHERE parent.event_id=start.parent_event_id AND parent.session_id=start.session_id
                  AND parent.run_id=start.run_id AND parent.source_adapter=start.source_adapter COLLATE BINARY)
              AND EXISTS(SELECT 1 FROM local_workspace_nodes relation_group
                WHERE relation_group.node_id=local_workspace_node_id('unknown_relation_group',start.run_id));
            UPDATE local_workspace_tool_metadata AS metadata SET
              caller_state='recorded',caller_node_id=node.parent_node_id
            FROM local_workspace_nodes node JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
            WHERE metadata.node_id=node.node_id AND receipt.source_family='session_sdk' AND node.parent_node_id IS NOT NULL
              AND node.relationship_authority='exact'
              AND node.parent_node_id<>(SELECT local_workspace_node_id('execution_root',h.source_identity) FROM local_workspace_execution_headers h WHERE h.execution_id=node.execution_id);

            WITH groups AS (
              SELECT carrier_digest,
                     SUM(type='subagent.selected') selected_count,SUM(type IN ('subagent.started','SubagentStart')) started_count,
                     SUM(type IN ('subagent.completed','SubagentStop')) completed_count,SUM(type='subagent.failed') failed_count,
                     SUM(type='subagent.deselected') deselected_count,COUNT(DISTINCT event_id) reference_count,
                     COUNT(DISTINCT authority_receipt) authority_count
              FROM local_workspace_semantic_candidates WHERE semantic_kind='subagent' GROUP BY carrier_digest)
            INSERT INTO local_workspace_subagent_lifecycle(node_id,selected_state,started_state,completed_state,failed_state,deselected_state,input_state)
            SELECT local_workspace_node_id('semantic_subagent',carrier_digest),
                   CASE WHEN selected_count>1 OR selected_count>0 AND (reference_count>16 OR authority_count>1) THEN 'inconsistent' WHEN selected_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN started_count>1 OR started_count>0 AND (reference_count>16 OR authority_count>1) THEN 'inconsistent' WHEN started_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN completed_count>1 OR completed_count>0 AND (failed_count>0 OR reference_count>16 OR authority_count>1) THEN 'inconsistent' WHEN completed_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN failed_count>1 OR failed_count>0 AND (completed_count>0 OR reference_count>16 OR authority_count>1) THEN 'inconsistent' WHEN failed_count=1 THEN 'recorded' ELSE 'not_observed' END,
                   CASE WHEN deselected_count>1 OR deselected_count>0 AND (reference_count>16 OR authority_count>1) THEN 'inconsistent' WHEN deselected_count=1 THEN 'recorded' ELSE 'not_observed' END,'source_unsupported'
            FROM groups WHERE EXISTS(SELECT 1 FROM local_workspace_nodes node
              WHERE node.node_id=local_workspace_node_id('semantic_subagent',groups.carrier_digest));

            INSERT OR IGNORE INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal)
            SELECT n.node_id,n.parent_node_id,'parent','exact',n.source_ordinal FROM local_workspace_nodes n
            WHERE n.source_kind IN ('semantic_tool','semantic_subagent') AND n.relationship_authority='exact'
              AND n.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));

            WITH candidate_content AS (
              SELECT DISTINCT c.carrier_digest,c.event_id,r.*
              FROM local_workspace_semantic_candidates c
              JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=c.event_id
              JOIN local_workspace_node_content_refs r ON r.node_id=raw.node_id
              WHERE c.semantic_kind='tool' AND EXISTS(SELECT 1 FROM local_workspace_nodes semantic
                WHERE semantic.node_id=local_workspace_node_id('semantic_tool',c.carrier_digest))
                AND (r.availability_state<>'not_captured' OR c.type='tool.execution_start' AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_semantic_candidates available_candidate
                  JOIN local_workspace_nodes available_raw ON available_raw.source_kind='session_event' AND available_raw.source_identity=available_candidate.event_id
                  JOIN local_workspace_node_content_refs available_content ON available_content.node_id=available_raw.node_id AND available_content.part=r.part
                  WHERE available_candidate.semantic_kind='tool' AND available_candidate.carrier_digest=c.carrier_digest
                    AND available_content.availability_state<>'not_captured'))),
            content_groups AS (
              SELECT carrier_digest,part,COUNT(*) source_count FROM candidate_content GROUP BY carrier_digest,part),
            ranked AS (
              SELECT content.*,groups.source_count,
                     row_number() OVER(PARTITION BY carrier_digest,part ORDER BY event_id COLLATE BINARY) source_rank
              FROM candidate_content content JOIN content_groups groups USING(carrier_digest,part))
            INSERT INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,
              retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,retention_ownership_receipt,retention_owner_token,availability_state)
            SELECT local_workspace_node_id('semantic_tool',carrier_digest),part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,
                   retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,retention_ownership_receipt,retention_owner_token,availability_state
            FROM ranked WHERE source_count=1 AND source_rank=1;

            WITH candidate_content AS (
              SELECT c.carrier_digest,c.event_id,r.part,r.json_pointer
              FROM local_workspace_semantic_candidates c
              JOIN local_workspace_nodes raw ON raw.source_kind='session_event' AND raw.source_identity=c.event_id
              JOIN local_workspace_node_content_refs r ON r.node_id=raw.node_id
              WHERE c.semantic_kind='tool' AND EXISTS(SELECT 1 FROM local_workspace_nodes semantic
                WHERE semantic.node_id=local_workspace_node_id('semantic_tool',c.carrier_digest))
                AND (r.availability_state<>'not_captured' OR c.type='tool.execution_start' AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_semantic_candidates available_candidate
                  JOIN local_workspace_nodes available_raw ON available_raw.source_kind='session_event' AND available_raw.source_identity=available_candidate.event_id
                  JOIN local_workspace_node_content_refs available_content ON available_content.node_id=available_raw.node_id AND available_content.part=r.part
                  WHERE available_candidate.semantic_kind='tool' AND available_candidate.carrier_digest=c.carrier_digest
                    AND available_content.availability_state<>'not_captured'))),
            collisions AS (
              SELECT carrier_digest,part,MIN(event_id) anchor_event_id,MIN(json_pointer) json_pointer,COUNT(DISTINCT event_id) source_count
              FROM candidate_content GROUP BY carrier_digest,part HAVING COUNT(DISTINCT event_id)>1)
            INSERT INTO local_workspace_node_content_refs(node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,availability_state)
            SELECT local_workspace_node_id('semantic_tool',carrier_digest),part,'session_event_content',anchor_event_id,
                   CASE part WHEN 'event_content' THEN 'whole_event' ELSE 'json_pointer' END,
                   CASE part WHEN 'event_content' THEN NULL ELSE COALESCE(json_pointer,'/'||part) END,NULL,
                   'projection_invalid|competing_exact_sources|'||source_count,'invalid'
            FROM collisions;

            DROP TABLE local_workspace_semantic_candidates;
            """, ("$ids", idsJson));
        if (hasSemanticMonitorSpans)
        {
            Execute(connection, transaction, """
                UPDATE local_workspace_nodes AS node SET
                  parent_node_id=local_workspace_node_id('session_event',parent.event_id),relationship_authority='exact'
                FROM local_workspace_semantic_receipts receipt
                JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.source_kind='otel_span'
                JOIN session_events child ON child.event_id=reference.event_id
                JOIN monitor_spans span ON span.trace_id=reference.trace_id AND span.span_id=reference.span_id
                JOIN session_events parent ON parent.session_id=child.session_id AND parent.run_id=child.run_id
                  AND parent.source_adapter='otel-exact' COLLATE BINARY AND parent.trace_id=span.trace_id COLLATE BINARY
                  AND parent.type='otel.span' COLLATE BINARY
                  AND parent.source_event_id=span.trace_id||'/'||span.parent_span_id COLLATE BINARY
                WHERE node.node_id=receipt.node_id AND receipt.source_family='otel' AND span.parent_span_id IS NOT NULL
                  AND (SELECT COUNT(*) FROM session_events candidate WHERE candidate.session_id=child.session_id AND candidate.run_id=child.run_id
                    AND candidate.source_adapter='otel-exact' COLLATE BINARY AND candidate.trace_id=span.trace_id COLLATE BINARY
                    AND candidate.type='otel.span' COLLATE BINARY
                    AND candidate.source_event_id=span.trace_id||'/'||span.parent_span_id COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM monitor_spans parent_owner
                    WHERE lower(parent_owner.trace_id)=span.trace_id COLLATE BINARY
                      AND lower(parent_owner.span_id)=span.parent_span_id COLLATE BINARY)=1;
                UPDATE local_workspace_nodes AS node SET
                  parent_node_id=local_workspace_node_id('unknown_relation_group',child.run_id),relationship_authority='unknown'
                FROM local_workspace_semantic_receipts receipt
                JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.source_kind='otel_span'
                JOIN session_events child ON child.event_id=reference.event_id
                JOIN monitor_spans span ON span.trace_id=reference.trace_id AND span.span_id=reference.span_id
                WHERE node.node_id=receipt.node_id AND receipt.source_family='otel' AND span.parent_span_id IS NOT NULL
                  AND ((SELECT COUNT(*) FROM session_events candidate
                        WHERE candidate.session_id=child.session_id AND candidate.run_id=child.run_id
                          AND candidate.source_adapter='otel-exact' COLLATE BINARY AND candidate.trace_id=span.trace_id COLLATE BINARY
                          AND candidate.type='otel.span' COLLATE BINARY
                          AND candidate.source_event_id=span.trace_id||'/'||span.parent_span_id COLLATE BINARY)<>1
                    OR (SELECT COUNT(*) FROM monitor_spans parent_owner
                        WHERE lower(parent_owner.trace_id)=span.trace_id COLLATE BINARY
                          AND lower(parent_owner.span_id)=span.parent_span_id COLLATE BINARY)<>1)
                  AND EXISTS(SELECT 1 FROM local_workspace_nodes relation_group
                    WHERE relation_group.node_id=local_workspace_node_id('unknown_relation_group',child.run_id)
                      AND relation_group.session_id=child.session_id AND relation_group.execution_id=node.execution_id);
                UPDATE local_workspace_node_edges AS edge SET
                  related_node_id=node.parent_node_id,relationship_authority='exact'
                FROM local_workspace_nodes node JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
                WHERE edge.node_id=node.node_id AND edge.relation_kind='parent' AND receipt.source_family='otel'
                  AND node.relationship_authority='exact';
                DELETE FROM local_workspace_node_edges
                WHERE relation_kind='parent' AND node_id IN (
                  SELECT node.node_id FROM local_workspace_nodes node
                  JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
                  WHERE receipt.source_family='otel' AND node.relationship_authority='unknown');
                UPDATE local_workspace_tool_metadata AS metadata SET
                  caller_state='recorded',caller_node_id=node.parent_node_id
                FROM local_workspace_nodes node JOIN local_workspace_semantic_receipts receipt ON receipt.node_id=node.node_id
                WHERE metadata.node_id=node.node_id AND receipt.source_family='otel' AND node.parent_node_id IS NOT NULL
                  AND node.relationship_authority='exact'
                  AND node.parent_node_id<>(SELECT local_workspace_node_id('execution_root',h.source_identity) FROM local_workspace_execution_headers h WHERE h.execution_id=node.execution_id);

                UPDATE local_workspace_tool_metadata AS metadata SET
                  mcp_server_identity_state='recorded',
                  mcp_server_identity=(SELECT m.mcp_server_hash FROM local_workspace_semantic_receipts receipt
                    JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id
                    JOIN session_events e ON e.event_id=reference.event_id
                    JOIN monitor_spans m ON e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY
                      AND e.trace_id=m.trace_id COLLATE BINARY
                      AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
                    WHERE receipt.node_id=metadata.node_id AND receipt.source_family='otel'
                      AND length(m.mcp_server_hash)=64 AND m.mcp_server_hash NOT GLOB '*[^0-9a-f]*'),
                  mcp_server_name_state='source_unsupported'
                WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts WHERE source_family='otel')
                  AND EXISTS(SELECT 1 FROM local_workspace_semantic_receipts receipt
                    JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id
                    JOIN session_events e ON e.event_id=reference.event_id
                    JOIN monitor_spans m ON e.source_adapter='otel-exact' COLLATE BINARY AND e.type='otel.span' COLLATE BINARY
                      AND e.trace_id=m.trace_id COLLATE BINARY
                      AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
                    WHERE receipt.node_id=metadata.node_id AND receipt.source_family='otel'
                      AND length(m.mcp_server_hash)=64 AND m.mcp_server_hash NOT GLOB '*[^0-9a-f]*');
                """);
        }
    }

    private static void RefreshOtelSemanticFacts(SqliteConnection connection, SqliteTransaction transaction, string idsJson)
    {
        if (!TableExists(connection, transaction, "monitor_spans")
            || !ColumnExists(connection, transaction, "monitor_spans", "status")
            || !ColumnExists(connection, transaction, "monitor_spans", "start_time")
            || !ColumnExists(connection, transaction, "monitor_spans", "end_time"))
            return;
        Execute(connection, transaction, """
            WITH exact AS (
              SELECT receipt.node_id,span.status,span.start_time,span.end_time,
                     local_workspace_ticks(span.start_time) start_ticks,local_workspace_ticks(span.end_time) end_ticks
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.source_kind='otel_span'
              JOIN monitor_spans span ON span.trace_id=reference.trace_id COLLATE BINARY AND span.span_id=reference.span_id COLLATE BINARY
              WHERE receipt.source_family='otel' AND receipt.semantic_kind='tool'
                AND EXISTS(SELECT 1 FROM local_workspace_nodes owned WHERE owned.node_id=receipt.node_id
                  AND owned.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))))
            UPDATE local_workspace_nodes AS node SET
              lifecycle=CASE WHEN exact.status='error' COLLATE BINARY THEN 'failed'
                             WHEN exact.status='ok' COLLATE BINARY OR exact.end_ticks IS NOT NULL THEN 'completed'
                             WHEN exact.start_ticks IS NOT NULL THEN 'started' ELSE 'unknown' END,
              status=CASE exact.status WHEN 'error' THEN 'failed' WHEN 'ok' THEN 'completed' ELSE 'unknown' END,
              time_authority=CASE WHEN exact.status COLLATE BINARY IN ('ok','error') OR exact.end_ticks IS NOT NULL THEN
                                    CASE WHEN exact.start_ticks IS NOT NULL AND exact.end_ticks>=exact.start_ticks THEN 'recorded' ELSE 'invalid' END
                                  WHEN exact.start_time IS NULL AND exact.end_time IS NULL THEN 'missing'
                                  WHEN exact.start_ticks IS NULL OR exact.end_time IS NOT NULL AND (exact.end_ticks IS NULL OR exact.end_ticks<exact.start_ticks) THEN 'invalid'
                                  ELSE 'recorded' END,
              start_utc_ticks=CASE WHEN (exact.status COLLATE BINARY IN ('ok','error') OR exact.end_ticks IS NOT NULL)
                                          AND (exact.start_ticks IS NULL OR exact.end_ticks IS NULL OR exact.end_ticks<exact.start_ticks) THEN NULL
                                    WHEN exact.start_ticks IS NOT NULL AND (exact.end_time IS NULL OR exact.end_ticks>=exact.start_ticks) THEN exact.start_ticks END,
              end_utc_ticks=CASE WHEN exact.start_ticks IS NOT NULL AND exact.end_ticks>=exact.start_ticks THEN exact.end_ticks END,
              duration_ms=CASE WHEN exact.start_ticks IS NOT NULL AND exact.end_ticks>=exact.start_ticks THEN (exact.end_ticks-exact.start_ticks)/10000 END
            FROM exact WHERE node.node_id=exact.node_id;
            WITH exact AS (
              SELECT receipt.node_id,span.status,local_workspace_ticks(span.start_time) start_ticks,local_workspace_ticks(span.end_time) end_ticks
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_node_source_references reference ON reference.node_id=receipt.node_id AND reference.source_kind='otel_span'
              JOIN monitor_spans span ON span.trace_id=reference.trace_id COLLATE BINARY AND span.span_id=reference.span_id COLLATE BINARY
              WHERE receipt.source_family='otel' AND receipt.semantic_kind='tool'
                AND EXISTS(SELECT 1 FROM local_workspace_nodes owned WHERE owned.node_id=receipt.node_id
                  AND owned.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))))
            UPDATE local_workspace_tool_metadata AS metadata SET
              started_state=CASE WHEN exact.start_ticks IS NOT NULL THEN 'recorded' ELSE 'not_observed' END,
              completed_state=CASE WHEN exact.status='error' COLLATE BINARY THEN 'not_observed'
                                   WHEN exact.status='ok' COLLATE BINARY OR exact.end_ticks IS NOT NULL THEN 'recorded' ELSE 'not_observed' END,
              failed_state=CASE WHEN exact.status='error' COLLATE BINARY THEN 'recorded' ELSE 'not_observed' END
            FROM exact WHERE metadata.node_id=exact.node_id;
            WITH totals AS (
              SELECT node.execution_id,COUNT(*) tool_count
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE receipt.source_family='otel' AND receipt.semantic_kind='tool'
                AND node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              GROUP BY node.execution_id)
            UPDATE local_workspace_execution_headers AS header SET tool_activity_state='recorded',tool_activity_count=totals.tool_count
            FROM totals WHERE header.execution_id=totals.execution_id AND header.tool_activity_state='not_observed';
            WITH totals AS (
              SELECT node.execution_id,COUNT(*) tool_count
              FROM local_workspace_semantic_receipts receipt
              JOIN local_workspace_nodes node ON node.node_id=receipt.node_id
              WHERE receipt.source_family='otel' AND receipt.semantic_kind='tool'
                AND node.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              GROUP BY node.execution_id)
            UPDATE local_workspace_nodes AS root SET tool_activity_state='recorded',tool_activity_count=totals.tool_count
            FROM totals WHERE root.execution_id=totals.execution_id AND root.source_kind='execution_root'
              AND root.tool_activity_state='not_observed';
            """, ("$ids", idsJson));
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

    private static string ReadRegistryGenerationIdentity(ISkillRegistryGenerationAuthority? authority)
    {
        if (authority is null) return "unavailable";
        var capture = authority.CaptureGeneration();
        if (capture is null || !authority.TryAcquireGenerationReadLease(capture, out var lease)) return "unavailable";
        using (lease)
            return authority.VerifyGenerationIdentity(capture, lease)
                ? authority.GetCanonicalGenerationIdentity(capture, lease)
                : "unavailable";
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
        "PostToolUseFailure" or "StopFailure" or "subagent.failed" => "error",
        "PermissionRequest" => "permission",
        _ => "event",
    };

    internal static string ContentPart(string? pointer) => pointer switch
    {
        "/prompt" => "instruction",
        "/tool_input" => "tool_input",
        "/tool_response" => "tool_result",
        "/error" => "error_message",
        _ => "event_content",
    };

    internal static (string Pointer, string Property, JsonValueKind RequiredKind)? ContentSelectorCandidate(
        string? adapter,
        string? fingerprint,
        string type)
    {
        if (!string.Equals(adapter, "claude-code-hook", StringComparison.Ordinal)
            || fingerprint is null || fingerprint.Length != 64)
            return null;
        return type switch
        {
            "UserPromptSubmit" => ("/prompt", "prompt", JsonValueKind.String),
            "PreToolUse" => ("/tool_input", "tool_input", JsonValueKind.Object),
            "PostToolUse" => ("/tool_response", "tool_response", JsonValueKind.Undefined),
            "PostToolUseFailure" or "StopFailure" => ("/error", "error", JsonValueKind.String),
            _ => null,
        };
    }

    internal static string? ContentPointer(string? adapter, string? fingerprint, string type, string? json)
    {
        if (json is null || ContentSelectorCandidate(adapter, fingerprint, type) is not { } candidate)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(candidate.Property, out var value)
                && (candidate.RequiredKind == JsonValueKind.Undefined || value.ValueKind == candidate.RequiredKind)
                ? candidate.Pointer
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static long? ContentBytes(string? pointer, string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (pointer is null) return Encoding.UTF8.GetByteCount(json);
            var property = pointer[1..];
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(property, out var value)) return null;
            return Encoding.UTF8.GetByteCount(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }
        catch (JsonException) { return null; }
    }

    private static void ApplyLabels(SqliteConnection connection, SqliteTransaction transaction, string idsJson, DateTimeOffset now, bool labelProofInstalled)
    {
        connection.CreateFunction<string, string, string?>("local_workspace_label", static (type, json) => TryReadInstruction(type, json, out var text) ? text : null, isDeterministic: true);
        connection.CreateFunction<string, string?>("local_workspace_search", static text => text is null ? null : Search(text), isDeterministic: true);
        connection.CreateFunction<string, string, long>("local_workspace_future", static (expiry, instant) =>
            TryCanonicalFuture(expiry, DateTimeOffset.ParseExact(instant, "O", CultureInfo.InvariantCulture)) ? 1 : 0, isDeterministic: true);
        var candidates = TableExists(connection, transaction, "retention_items")
            ? """
              SELECT e.session_id,e.event_id,e.type,e.occurred_at,c.captured_at,c.expires_at source_expires_at,
                     CASE WHEN i.state='retained_by_policy' THEN NULL ELSE i.expires_at END effective_expires_at,
                     local_workspace_label(e.type,c.content_json) label_text
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                AND i.expires_at=c.expires_at
              WHERE e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted') AND e.content_state='available'
                AND i.state IN ('expiring','retained_by_policy') AND i.read_denied_at IS NULL
                AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                AND i.captured_at=c.captured_at AND i.expires_at=c.expires_at
                AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,
                  e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1
                AND i.deleted_at IS NULL AND i.error_code IS NULL
                AND (i.state='retained_by_policy' OR local_workspace_future(i.expires_at,$now)=1)
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              """
            : """
              SELECT e.session_id,e.event_id,e.type,e.occurred_at,c.captured_at,c.expires_at source_expires_at,c.expires_at effective_expires_at,local_workspace_label(e.type,c.content_json) label_text
              FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted') AND e.content_state='available'
                AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
                AND local_workspace_future(c.expires_at,$now)=1
              """;
        var proofUpdate = labelProofInstalled
            ? """
              instruction_count=r.instruction_count,
              label_owner_revision=local_workspace_semantic_digest('session_label_owner',
                local_workspace_semantic_digest('session_label_source',r.event_id,r.type),
                local_workspace_semantic_digest('session_label_value',r.label_text,
                  local_workspace_semantic_digest('session_label_time',r.occurred_at,r.captured_at)||
                  local_workspace_semantic_digest('session_label_expiry',r.source_expires_at,CAST(r.instruction_count AS TEXT)))),
              """
            : string.Empty;
        var revisionSuffix = labelProofInstalled
            ? """
              local_workspace_semantic_digest('session_label_owner',
                local_workspace_semantic_digest('session_label_source',r.event_id,r.type),
                local_workspace_semantic_digest('session_label_value',r.label_text,
                  local_workspace_semantic_digest('session_label_time',r.occurred_at,r.captured_at)||
                  local_workspace_semantic_digest('session_label_expiry',r.source_expires_at,CAST(r.instruction_count AS TEXT))))
              """
            : "r.event_id||'|'||r.source_expires_at";
        ExecuteWithIds(connection, transaction, $"""
            WITH parsed_candidates AS (
              {candidates}),
            candidates AS (
              SELECT * FROM parsed_candidates WHERE label_text IS NOT NULL),
            ranked AS (
              SELECT *,row_number() OVER(PARTITION BY session_id ORDER BY occurred_at COLLATE BINARY,event_id COLLATE BINARY) ordinal,
                     count(*) OVER(PARTITION BY session_id) instruction_count
              FROM candidates)
            UPDATE local_workspace_sessions AS p SET
              label_state='recorded',label_text=r.label_text,
              label_source_identity=r.event_id,label_expires_at=r.source_expires_at,
              {proofUpdate}revision_seed=p.revision_seed||'|'||{revisionSuffix}
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
                FROM session_events e JOIN monitor_spans m ON e.source_adapter='otel-exact' COLLATE BINARY
                  AND e.type='otel.span' COLLATE BINARY
                  AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY AND e.trace_id=m.trace_id COLLATE BINARY
                JOIN raw_records r ON r.id=m.raw_record_id
                JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(m.raw_record_id AS TEXT)
                WHERE m.operation='execute_tool' COLLATE BINARY AND m.category='tool_call' COLLATE BINARY
                  AND length(m.trace_id)=32 AND m.trace_id=lower(m.trace_id) AND m.trace_id NOT GLOB '*[^0-9a-f]*'
                  AND length(m.span_id)=16 AND m.span_id=lower(m.span_id) AND m.span_id NOT GLOB '*[^0-9a-f]*'
                  AND (SELECT COUNT(*) FROM monitor_spans owner WHERE lower(owner.trace_id)=m.trace_id COLLATE BINARY
                    AND lower(owner.span_id)=m.span_id COLLATE BINARY)=1
                  AND (SELECT COUNT(*) FROM session_events owner WHERE owner.source_adapter='otel-exact' COLLATE BINARY
                    AND owner.type='otel.span' COLLATE BINARY AND lower(owner.trace_id)=m.trace_id COLLATE BINARY
                    AND lower(owner.source_event_id)=m.trace_id||'/'||m.span_id COLLATE BINARY)=1
                  AND m.tool_name IS NOT NULL AND length(m.tool_name)>0 AND i.state IN ('expiring','retained_by_policy')
                  AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                  AND (i.state='retained_by_policy' OR i.expires_at COLLATE BINARY > $now COLLATE BINARY)
                  AND e.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids));
                """, idsJson, Canonical(now));
    }

    internal static void RegisterProjectionFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, long?>("local_workspace_epoch", static value => TryInstant(value, out var instant) ? instant.ToUnixTimeMilliseconds() : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_canonical", static value => TryInstant(value, out var instant) ? Canonical(instant) : null, isDeterministic: true);
        connection.CreateFunction<string?, string?>("local_workspace_search", static value => value is null ? null : Search(value), isDeterministic: true);
        connection.CreateFunction<string, string, string?>("local_workspace_label", static (type, json) => TryReadInstruction(type, json, out var text) ? text : null, isDeterministic: true);
        connection.CreateFunction<string, string, string>("local_workspace_node_id", StableNodeId, isDeterministic: true);
        connection.CreateFunction<string, string, string, string>("local_workspace_semantic_digest", static (kind, scope, carrier) =>
            Convert.ToHexString(Hash("local-workspace-semantic-carrier\0v1\0" + kind + "\0", scope, carrier)).ToLowerInvariant(), isDeterministic: true);
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
    private static string[] ReadSessionIdBatch(SqliteConnection connection, SqliteTransaction transaction, string? after)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM sessions WHERE $after IS NULL OR session_id COLLATE BINARY>$after COLLATE BINARY ORDER BY session_id COLLATE BINARY LIMIT 200;";
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var result = new List<string>(200);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private static string[] ReadStagedSessionIdBatch(SqliteConnection connection, SqliteTransaction transaction, string? after)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM local_workspace_target_session_ids WHERE $after IS NULL OR session_id COLLATE BINARY>$after COLLATE BINARY ORDER BY session_id COLLATE BINARY LIMIT 200;";
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        SqliteCommandExecutionObserver.Executing();
        using var reader = command.ExecuteReader();
        var result = new List<string>(200);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private sealed class PinnedSkillRegistryGenerationAuthority :
        ISkillRegistryGenerationAuthority,
        ISkillRegistryGenerationCapture,
        IDisposable
    {
        private readonly ISkillRegistryGenerationAuthority inner;
        private readonly ISkillRegistryGenerationCapture capture;
        private readonly ISkillRegistryGenerationLease lease;
        private readonly string canonicalIdentity;

        private PinnedSkillRegistryGenerationAuthority(
            ISkillRegistryGenerationAuthority inner,
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease,
            string canonicalIdentity) =>
            (this.inner, this.capture, this.lease, this.canonicalIdentity) = (inner, capture, lease, canonicalIdentity);

        internal static PinnedSkillRegistryGenerationAuthority Create(ISkillRegistryGenerationAuthority authority)
        {
            var capture = authority.CaptureGeneration();
            if (capture is null || !authority.TryAcquireGenerationReadLease(capture, out var lease))
                throw new InvalidOperationException("skill_registry_generation_unavailable");
            try
            {
                if (!authority.VerifyGenerationIdentity(capture, lease))
                    throw new InvalidOperationException("skill_registry_generation_not_current");
                return new(authority, capture, lease, authority.GetCanonicalGenerationIdentity(capture, lease));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public ISkillRegistryGenerationCapture CaptureGeneration() => this;

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture candidate,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? generationLease)
        {
            if (!ReferenceEquals(candidate, this))
            {
                generationLease = null;
                return false;
            }
            generationLease = new BorrowedGenerationLease(this);
            return true;
        }

        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture candidate, ISkillRegistryGenerationLease generationLease) =>
            ReferenceEquals(candidate, this)
            && generationLease is BorrowedGenerationLease borrowed
            && ReferenceEquals(borrowed.Owner, this);

        public string GetCanonicalGenerationIdentity(ISkillRegistryGenerationCapture candidate, ISkillRegistryGenerationLease generationLease)
        {
            if (!VerifyGenerationIdentity(candidate, generationLease))
                throw new InvalidOperationException("skill_registry_generation_not_current");
            return canonicalIdentity;
        }

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease generationLease, SkillRegistryProducerTuple tuple)
        {
            if (generationLease is not BorrowedGenerationLease borrowed || !ReferenceEquals(borrowed.Owner, this))
                return false;
            return inner.IsProducerTupleAccepted(lease, tuple);
        }

        public void Dispose() => lease.Dispose();

        private sealed class BorrowedGenerationLease(PinnedSkillRegistryGenerationAuthority owner) : ISkillRegistryGenerationLease
        {
            internal PinnedSkillRegistryGenerationAuthority Owner { get; } = owner;
            public void Dispose() { }
        }
    }
    private static void ExecuteWithIds(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids, string? now = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now); SqliteCommandExecutionObserver.Executing(); command.ExecuteNonQuery(); }
    private static bool TempTableExists(SqliteConnection connection, SqliteTransaction transaction, string name) { using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_temp_schema WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name",name); return Convert.ToInt64(command.ExecuteScalar(),CultureInfo.InvariantCulture)!=0; }
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

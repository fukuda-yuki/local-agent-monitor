using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceProjectionStore
{
    internal static void Refresh(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Execute(connection, transaction, "DELETE FROM local_workspace_session_sources; DELETE FROM local_workspace_session_models; DELETE FROM local_workspace_session_activity; DELETE FROM local_workspace_token_observations; DELETE FROM local_workspace_sessions;");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_workspace_sessions(
                    session_id,sort_group,sort_epoch_ms,label_state,label_text,label_search_text,
                    label_source_identity,label_expires_at,status,completeness,timing_state,started_at,
                    ended_at,duration_ms,capture_notes,revision_seed)
                SELECT s.session_id,
                       CASE WHEN s.started_at IS NULL THEN 2 WHEN s.ended_at IS NULL THEN 0 ELSE 1 END,
                       MAX(0,CAST((julianday(COALESCE(s.started_at,s.last_seen_at)) - 2440587.5) * 86400000 AS INTEGER)),
                       CASE WHEN label.text IS NOT NULL THEN 'recorded'
                            WHEN s.raw_retention_state='expired_pending_deletion' THEN 'expired'
                            WHEN s.raw_retention_state='not_captured' THEN 'not_captured'
                            ELSE 'not_observed' END,
                       label.text,
                       CASE WHEN label.text IS NULL THEN NULL ELSE lower(label.text) END,
                       label.event_id,
                       label.expires_at,
                       s.status,s.completeness,
                       CASE WHEN s.started_at IS NOT NULL AND s.ended_at IS NOT NULL AND julianday(s.ended_at)>=julianday(s.started_at) THEN 'recorded'
                            WHEN s.started_at IS NULL AND s.ended_at IS NULL THEN 'not_observed' ELSE 'inconsistent' END,
                       s.started_at,s.ended_at,
                       CASE WHEN s.started_at IS NOT NULL AND s.ended_at IS NOT NULL AND julianday(s.ended_at)>=julianday(s.started_at)
                            THEN CAST((julianday(s.ended_at)-julianday(s.started_at))*86400000 AS INTEGER) END,
                       CASE WHEN s.raw_retention_state='expired_pending_deletion' THEN 'raw_content_expired'
                            WHEN s.raw_retention_state='not_captured' THEN 'raw_content_not_captured' ELSE '' END,
                       s.status || '|' || s.completeness || '|' || COALESCE(s.started_at,'') || '|' || COALESCE(s.ended_at,'') || '|' || s.last_seen_at || '|' || COALESCE(label.event_id,'') || '|' || COALESCE(label.expires_at,'')
                FROM sessions s
                LEFT JOIN (
                    SELECT event_id,session_id,expires_at,text FROM (
                        SELECT e.event_id,e.session_id,c.expires_at,
                               substr(trim(replace(replace(COALESCE(json_extract(c.content_json,'$.instruction'),json_extract(c.content_json,'$.prompt'),json_extract(c.content_json,'$.message')),'\r',' '),'\n',' ')),1,160) AS text,
                               row_number() OVER(PARTITION BY e.session_id ORDER BY e.occurred_at,e.event_id) AS ordinal
                        FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
                        WHERE c.expires_at>$now AND json_valid(c.content_json)
                    ) WHERE ordinal=1 AND text<>''
                ) label ON label.session_id=s.session_id;
                """;
            command.Parameters.AddWithValue("$now", timestamp);
            command.ExecuteNonQuery();
        }
        var skillCount = TableExists(connection, transaction, "skill_projection_invocations")
            ? "(SELECT COUNT(*) FROM skill_projection_invocations i WHERE i.session_id=s.session_id)"
            : "0";
        Execute(connection, transaction, $$"""
            INSERT INTO local_workspace_session_sources SELECT session_id,source FROM (
              SELECT session_id,source_surface AS source FROM session_native_ids WHERE source_surface IS NOT NULL
              UNION SELECT session_id,source_surface FROM session_runs WHERE source_surface IS NOT NULL
              UNION SELECT session_id,source_surface FROM session_events WHERE source_surface IS NOT NULL) ORDER BY session_id,source;
            INSERT INTO local_workspace_session_models SELECT DISTINCT session_id,model FROM session_runs WHERE model IS NOT NULL AND trim(model)<>'' ORDER BY session_id,model;
            INSERT INTO local_workspace_session_activity
              SELECT s.session_id,k.kind,'recorded',CASE k.kind
                WHEN 'skill' THEN {{skillCount}}
                WHEN 'tool' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=s.session_id AND lower(e.type) LIKE '%tool%')
                WHEN 'subagent' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=s.session_id AND lower(e.type) LIKE '%subagent%')
                WHEN 'error' THEN (SELECT COUNT(*) FROM session_events e WHERE e.session_id=s.session_id AND (e.status='failed' OR e.terminal_outcome='failed'))
                ELSE (SELECT COUNT(*) FROM session_events e WHERE e.session_id=s.session_id AND lower(e.type) LIKE '%retry%') END
              FROM sessions s CROSS JOIN (SELECT 'skill' kind UNION ALL SELECT 'tool' UNION ALL SELECT 'subagent' UNION ALL SELECT 'error' UNION ALL SELECT 'retry') k;
            INSERT INTO local_workspace_token_observations(session_id,execution_id,authority,authority_rank,source_identity,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens)
              SELECT session_id,run_id,'session_run',0,run_id,input_tokens,output_tokens,total_tokens,NULL,NULL,NULL FROM session_runs;
            INSERT INTO local_workspace_projection_state(projector_key,session_frontier,refreshed_at)
              VALUES('local-workspace-projection-v1',(SELECT MAX(updated_at) FROM sessions),CURRENT_TIMESTAMP)
              ON CONFLICT(projector_key) DO UPDATE SET session_frontier=excluded.session_frontier,refreshed_at=excluded.refreshed_at;
            """);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }
}

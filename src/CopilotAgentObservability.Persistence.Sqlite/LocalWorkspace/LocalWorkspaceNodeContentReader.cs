using System.Text;
using System.Text.Json;
using System.Data;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalWorkspaceNodeContentReadDisposition
{
    Granted,
    Stale,
    Unavailable,
    Busy,
    Expired,
    Deleted,
    ReadDenied,
}

internal enum LocalWorkspaceNodeContentTerminalResult { Sealed, CompletedWithoutRaw, Lost, Busy }

internal sealed record LocalWorkspaceNodeContentReadResult(
    LocalWorkspaceNodeContentReadDisposition Disposition,
    LocalWorkspaceNodeContentReadLease? Lease);

internal static class LocalWorkspaceContentAuthority
{
    internal const string EffectiveAvailabilitySql = """
        CASE
          WHEN c.availability_state='invalid' AND c.revision_input LIKE 'projection_invalid|competing_exact_sources|%' THEN 'invalid'
          WHEN i.item_id IS NULL THEN CASE
            WHEN c.availability_state='not_captured' AND s.event_id IS NULL AND e.content_state='not_captured' THEN 'not_captured'
            ELSE 'invalid' END
          WHEN i.state='deleted' AND i.deleted_at IS NOT NULL AND tmb.item_id=i.item_id
            AND tmb.receipt_at=i.deleted_at AND tmb.deleted_at=i.deleted_at AND s.event_id IS NULL THEN 'deleted'
          WHEN i.state='deleted' OR i.deleted_at IS NOT NULL OR tmb.item_id IS NOT NULL THEN 'invalid'
          WHEN i.state='expired_pending_deletion'
            OR (i.state='expiring' AND i.expires_at COLLATE BINARY <= $now COLLATE BINARY)
            OR e.content_state='expired_pending_deletion' THEN 'expired'
          WHEN i.state IN ('deletion_queued','deleting','deletion_failed')
            OR i.read_denied_at IS NOT NULL OR i.error_code IS NOT NULL THEN 'read_denied'
          WHEN e.content_state='not_captured' OR s.event_id IS NULL THEN 'not_captured'
          WHEN e.content_state='available' AND c.selected_utf8_bytes>1048576 THEN 'oversized'
          WHEN e.content_state='available'
            AND i.state IN ('retained_by_policy','expiring')
            AND (i.state='retained_by_policy' OR i.expires_at COLLATE BINARY>$now COLLATE BINARY)
            AND i.store_kind='session_event_content' AND i.source_item_id=e.event_id
            AND i.store_instance_id=c.retention_store_instance_id
            AND i.captured_at=s.captured_at AND i.expires_at=s.expires_at
            AND i.ownership_receipt=c.retention_ownership_receipt
            AND s.retention_owner_token=c.retention_owner_token
            AND local_workspace_retention_receipt_matches(
              i.store_instance_id,e.event_id,s.content_kind,s.captured_at,s.expires_at,
              e.session_id,e.run_id,e.source_adapter,e.source_event_id,s.retention_owner_token,i.ownership_receipt)=1
            THEN 'available'
          ELSE 'invalid'
        END
        """;

    internal static bool ValidateSessionGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        DateTimeOffset acceptedAt)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            WITH raw_nodes AS (
              SELECT n.node_id,n.session_id,n.source_identity,e.event_id,e.type,e.source_adapter,e.schema_fingerprint,e.content_state
              FROM local_workspace_nodes n
              JOIN session_events e ON e.event_id=n.source_identity AND e.session_id=n.session_id
              WHERE n.session_id=$session_id AND n.source_kind='session_event'),
            scoped_refs AS (
              SELECT r.node_id,r.part,r.store_kind,r.source_item_id,r.locator_kind,r.json_pointer,
                     r.selected_utf8_bytes,r.revision_input,r.retention_item_id,r.retention_store_instance_id,
                     r.source_captured_at,r.source_expires_at,r.retention_revision,r.retention_ownership_receipt,
                     r.retention_owner_token,r.availability_state
              FROM local_workspace_node_content_refs r
              JOIN local_workspace_nodes n ON n.node_id=r.node_id
              WHERE n.session_id=$session_id),
            invalid_binding AS (
              SELECT 1 FROM scoped_refs r
              JOIN local_workspace_nodes n ON n.node_id=r.node_id
              LEFT JOIN session_events e ON e.event_id=r.source_item_id AND e.session_id=n.session_id
              WHERE r.store_kind<>'session_event_content' OR e.event_id IS NULL OR e.type='skill.invoked'
                OR NOT (
                  n.source_kind='session_event' AND n.source_identity=r.source_item_id
                  OR n.source_kind='semantic_tool' AND EXISTS(
                    SELECT 1 FROM local_workspace_node_source_references source
                    WHERE source.node_id=n.node_id AND source.event_id=r.source_item_id))
                OR NOT (
                  r.part='event_content' AND r.locator_kind='whole_event' AND r.json_pointer IS NULL
                  OR e.source_adapter='claude-code-hook' COLLATE BINARY
                    AND length(e.schema_fingerprint)=64 AND e.schema_fingerprint=lower(e.schema_fingerprint)
                    AND e.schema_fingerprint NOT GLOB '*[^0-9a-f]*'
                    AND r.locator_kind='json_pointer' AND (
                      e.type='UserPromptSubmit' AND r.part='instruction' AND r.json_pointer='/prompt'
                      OR e.type='PreToolUse' AND r.part='tool_input' AND r.json_pointer='/tool_input'
                      OR e.type='PostToolUse' AND r.part='tool_result' AND r.json_pointer='/tool_response'
                      OR e.type IN ('PostToolUseFailure','StopFailure') AND r.part='error_message' AND r.json_pointer='/error'))),
            raw_coverage_drift AS (
              SELECT 1 FROM raw_nodes raw
              LEFT JOIN scoped_refs r ON r.node_id=raw.node_id
              GROUP BY raw.node_id,raw.type
              HAVING COUNT(r.node_id)<>CASE WHEN raw.type='skill.invoked' THEN 0 ELSE 1 END),
            raw_owner_rows AS (
              SELECT raw.*,c.part,c.locator_kind,c.json_pointer,c.selected_utf8_bytes,c.revision_input,
                     c.retention_item_id,c.retention_store_instance_id,c.source_captured_at,c.source_expires_at,
                     c.retention_revision,c.retention_ownership_receipt,c.retention_owner_token,c.availability_state,
                     s.event_id content_event_id,s.content_kind,s.captured_at content_captured_at,s.expires_at content_expires_at,
                     s.retention_owner_token content_owner_token,
                     i.item_id,i.store_instance_id,i.store_kind,i.source_item_id,i.captured_at,i.expires_at,i.state,
                     i.revision,i.ownership_receipt,i.read_denied_at,i.deleted_at,i.error_code,
                     tmb.item_id retention_tombstone_id,tmb.receipt_at retention_tombstone_receipt_at,
                     tmb.deleted_at retention_tombstone_deleted_at,
                     x.source_item_id local_tombstone_source,x.locator_kind tombstone_locator_kind,
                     x.json_pointer tombstone_json_pointer,x.selected_utf8_bytes tombstone_selected_utf8_bytes,
                     x.deleted_at tombstone_deleted_at,x.retention_item_id tombstone_item_id,x.retention_revision tombstone_revision,
                     {{EffectiveAvailabilitySql}} effective_state
              FROM raw_nodes raw
              JOIN scoped_refs c ON c.node_id=raw.node_id
              LEFT JOIN session_event_content s ON s.event_id=raw.event_id
              LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
              LEFT JOIN retention_tombstones tmb ON tmb.item_id=i.item_id
              LEFT JOIN local_workspace_content_tombstones x
                ON x.store_kind=c.store_kind AND x.source_item_id=c.source_item_id AND x.part=c.part
              JOIN local_workspace_nodes n ON n.node_id=c.node_id
              JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id),
            invalid_raw_owner AS (
              SELECT 1 FROM raw_owner_rows owner
              WHERE owner.local_tombstone_source IS NULL AND owner.revision_input IS NOT (
                    owner.content_state||'|'||COALESCE(owner.content_captured_at,owner.captured_at,'')||'|'||
                    COALESCE(owner.content_expires_at,owner.expires_at,'')||'|'||COALESCE(owner.item_id,'')||'|'||
                    COALESCE(owner.store_instance_id,'')||'|'||COALESCE(CAST(owner.revision AS TEXT),'')||'|'||
                    COALESCE(owner.state,'')||'|')
                OR owner.local_tombstone_source IS NULL AND (
                  owner.retention_item_id IS NOT owner.item_id
                  OR owner.retention_store_instance_id IS NOT owner.store_instance_id
                  OR owner.source_captured_at IS NOT COALESCE(owner.content_captured_at,owner.captured_at)
                  OR owner.source_expires_at IS NOT COALESCE(owner.content_expires_at,owner.expires_at)
                  OR owner.retention_revision IS NOT owner.revision
                  OR owner.retention_ownership_receipt IS NOT owner.ownership_receipt)
                OR owner.local_tombstone_source IS NOT NULL AND (
                  owner.retention_item_id IS NOT owner.tombstone_item_id
                  OR owner.retention_revision IS NOT owner.tombstone_revision
                  OR owner.retention_store_instance_id IS NOT NULL OR owner.source_captured_at IS NOT NULL
                  OR owner.source_expires_at IS NOT NULL OR owner.retention_ownership_receipt IS NOT NULL
                  OR owner.retention_owner_token IS NOT NULL
                  OR owner.locator_kind IS NOT owner.tombstone_locator_kind
                  OR owner.json_pointer IS NOT owner.tombstone_json_pointer
                  OR owner.selected_utf8_bytes IS NOT owner.tombstone_selected_utf8_bytes
                  OR owner.state<>'deleted' OR owner.deleted_at IS NULL OR owner.content_event_id IS NOT NULL
                  OR owner.retention_tombstone_id IS NOT owner.item_id
                  OR owner.retention_tombstone_receipt_at IS NOT owner.deleted_at
                  OR owner.retention_tombstone_deleted_at IS NOT owner.deleted_at
                  OR owner.tombstone_deleted_at IS NOT owner.deleted_at
                  OR owner.retention_revision IS NOT owner.revision
                  OR owner.revision_input IS NOT (owner.content_state||'|'||owner.captured_at||'|'||owner.expires_at||'|'||
                    owner.item_id||'|'||owner.store_instance_id||'|'||CAST(owner.revision AS TEXT)||'|deleted|'||owner.deleted_at))
                OR (owner.state='deleted' OR owner.deleted_at IS NOT NULL OR owner.retention_tombstone_id IS NOT NULL)
                  AND NOT (owner.state='deleted' AND owner.deleted_at IS NOT NULL
                    AND owner.retention_tombstone_id=owner.item_id
                    AND owner.retention_tombstone_receipt_at=owner.deleted_at
                    AND owner.retention_tombstone_deleted_at=owner.deleted_at
                    AND owner.content_event_id IS NULL)
                OR owner.item_id IS NOT NULL AND owner.local_tombstone_source IS NULL AND owner.content_event_id IS NOT NULL AND (
                  owner.store_kind<>'session_event_content' OR owner.source_item_id<>owner.event_id
                  OR owner.store_instance_id<>(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                  OR owner.store_kind<>'session_event_content' OR owner.source_item_id<>owner.event_id
                  OR owner.captured_at IS NOT owner.content_captured_at OR owner.expires_at IS NOT owner.content_expires_at
                  OR local_workspace_retention_receipt_matches(
                    owner.store_instance_id,owner.event_id,owner.content_kind,owner.content_captured_at,owner.content_expires_at,
                    owner.session_id,(SELECT run_id FROM session_events WHERE event_id=owner.event_id),owner.source_adapter,
                    (SELECT source_event_id FROM session_events WHERE event_id=owner.event_id),owner.content_owner_token,owner.ownership_receipt)<>1)
                OR owner.item_id IS NOT NULL AND owner.state<>'deleted' AND owner.content_event_id IS NULL
                OR owner.effective_state='available' AND (
                  owner.availability_state<>'available' OR owner.retention_revision<>owner.revision
                  OR owner.retention_owner_token IS NOT owner.content_owner_token)
                OR owner.effective_state='oversized' AND owner.availability_state<>'oversized'
                OR owner.effective_state='not_captured' AND owner.availability_state<>'not_captured'
                OR owner.effective_state='invalid' AND owner.availability_state<>'invalid'
                OR owner.effective_state='expired' AND owner.availability_state NOT IN ('available','oversized','expired')
                OR owner.effective_state='read_denied' AND owner.availability_state NOT IN ('available','oversized','expired','read_denied')
                OR owner.effective_state='deleted' AND owner.availability_state<>'deleted'),
            semantic_candidates AS (
              SELECT semantic.node_id,raw_ref.*,event.type,
                     COUNT(*) OVER(PARTITION BY semantic.node_id,raw_ref.part) source_count,
                     row_number() OVER(PARTITION BY semantic.node_id,raw_ref.part ORDER BY event.event_id COLLATE BINARY) source_rank
              FROM local_workspace_nodes semantic
              JOIN local_workspace_node_source_references source ON source.node_id=semantic.node_id AND source.event_id IS NOT NULL
              JOIN session_events event ON event.event_id=source.event_id AND event.session_id=semantic.session_id
              JOIN local_workspace_nodes raw ON raw.session_id=semantic.session_id AND raw.source_kind='session_event' AND raw.source_identity=event.event_id
              JOIN local_workspace_node_content_refs raw_ref ON raw_ref.node_id=raw.node_id
              WHERE semantic.session_id=$session_id AND semantic.source_kind='semantic_tool'
                AND (raw_ref.availability_state<>'not_captured' OR event.type='tool.execution_start' AND NOT EXISTS(
                  SELECT 1 FROM local_workspace_node_source_references available_source
                  JOIN local_workspace_nodes available_raw ON available_raw.session_id=semantic.session_id
                    AND available_raw.source_kind='session_event' AND available_raw.source_identity=available_source.event_id
                  JOIN local_workspace_node_content_refs available_ref ON available_ref.node_id=available_raw.node_id AND available_ref.part=raw_ref.part
                  WHERE available_source.node_id=semantic.node_id AND available_ref.availability_state<>'not_captured'))),
            expected_semantic AS (
              SELECT node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,revision_input,
                     retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,retention_revision,
                     retention_ownership_receipt,retention_owner_token,availability_state
              FROM semantic_candidates WHERE source_count=1 AND source_rank=1
              UNION ALL
              SELECT node_id,part,'session_event_content',MIN(source_item_id),
                     CASE part WHEN 'event_content' THEN 'whole_event' ELSE 'json_pointer' END,
                     CASE part WHEN 'instruction' THEN '/prompt' WHEN 'tool_input' THEN '/tool_input'
                       WHEN 'tool_result' THEN '/tool_response' WHEN 'error_message' THEN '/error' END,
                     NULL,'projection_invalid|competing_exact_sources|'||COUNT(DISTINCT source_item_id),
                     NULL,NULL,NULL,NULL,NULL,NULL,NULL,'invalid'
              FROM semantic_candidates
              GROUP BY node_id,part HAVING COUNT(DISTINCT source_item_id)>1),
            actual_semantic AS (
              SELECT r.node_id,r.part,r.store_kind,r.source_item_id,r.locator_kind,r.json_pointer,r.selected_utf8_bytes,r.revision_input,
                     r.retention_item_id,r.retention_store_instance_id,r.source_captured_at,r.source_expires_at,r.retention_revision,
                     r.retention_ownership_receipt,r.retention_owner_token,r.availability_state
              FROM scoped_refs r JOIN local_workspace_nodes n ON n.node_id=r.node_id
              WHERE n.source_kind='semantic_tool'),
            semantic_drift AS (
              SELECT 1 FROM (SELECT node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,
                    revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,
                    retention_revision,retention_ownership_receipt,retention_owner_token,availability_state
                  FROM expected_semantic EXCEPT
                  SELECT node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,
                    revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,
                    retention_revision,retention_ownership_receipt,retention_owner_token,availability_state FROM actual_semantic)
              UNION ALL SELECT 1 FROM (SELECT node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,
                    revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,
                    retention_revision,retention_ownership_receipt,retention_owner_token,availability_state
                  FROM actual_semantic EXCEPT
                  SELECT node_id,part,store_kind,source_item_id,locator_kind,json_pointer,selected_utf8_bytes,
                    revision_input,retention_item_id,retention_store_instance_id,source_captured_at,source_expires_at,
                    retention_revision,retention_ownership_receipt,retention_owner_token,availability_state FROM expected_semantic)),
            expected_tombstones AS (
              SELECT DISTINCT r.store_kind,r.source_item_id,r.part,r.locator_kind,r.json_pointer,r.selected_utf8_bytes,
                     i.deleted_at,r.retention_item_id,r.retention_revision
              FROM scoped_refs r
              JOIN retention_items i ON i.item_id=r.retention_item_id
              JOIN retention_tombstones terminal ON terminal.item_id=i.item_id
                AND terminal.receipt_at=i.deleted_at AND terminal.deleted_at=i.deleted_at
              LEFT JOIN session_event_content source ON source.event_id=r.source_item_id
              WHERE r.availability_state='deleted' AND i.state='deleted' AND i.deleted_at IS NOT NULL
                AND r.retention_revision=i.revision AND source.event_id IS NULL),
            actual_tombstones AS (
              SELECT x.store_kind,x.source_item_id,x.part,x.locator_kind,x.json_pointer,x.selected_utf8_bytes,
                     x.deleted_at,x.retention_item_id,x.retention_revision
              FROM local_workspace_content_tombstones x
              JOIN session_events e ON e.event_id=x.source_item_id
              WHERE e.session_id=$session_id),
            tombstone_drift AS (
              SELECT 1 FROM (SELECT store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                    deleted_at,retention_item_id,retention_revision FROM expected_tombstones EXCEPT
                  SELECT store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                    deleted_at,retention_item_id,retention_revision FROM actual_tombstones)
              UNION ALL SELECT 1 FROM (SELECT store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                    deleted_at,retention_item_id,retention_revision FROM actual_tombstones EXCEPT
                  SELECT store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                    deleted_at,retention_item_id,retention_revision FROM expected_tombstones)),
            missing_deleted_graph AS (
              SELECT 1 FROM retention_items i
              JOIN session_events e ON e.event_id=i.source_item_id
              LEFT JOIN session_event_content source ON source.event_id=e.event_id
              LEFT JOIN retention_tombstones terminal ON terminal.item_id=i.item_id
              LEFT JOIN local_workspace_nodes raw ON raw.session_id=e.session_id
                AND raw.source_kind='session_event' AND raw.source_identity=e.event_id
              LEFT JOIN local_workspace_node_content_refs r ON r.node_id=raw.node_id
              LEFT JOIN local_workspace_content_tombstones x ON x.store_kind='session_event_content'
                AND x.source_item_id=e.event_id AND x.part=r.part
              WHERE e.session_id=$session_id AND i.store_kind='session_event_content'
                AND e.type<>'skill.invoked'
                AND raw.node_id IS NOT NULL
                AND i.state='deleted' AND (i.deleted_at IS NULL OR source.event_id IS NOT NULL
                  OR terminal.item_id IS NULL OR terminal.receipt_at IS NOT i.deleted_at OR terminal.deleted_at IS NOT i.deleted_at
                  OR r.node_id IS NULL OR r.availability_state<>'deleted'
                  OR r.retention_item_id IS NOT i.item_id OR r.retention_revision IS NOT i.revision
                  OR x.source_item_id IS NULL OR x.retention_item_id IS NOT i.item_id OR x.retention_revision IS NOT i.revision
                  OR x.deleted_at IS NOT i.deleted_at))
            SELECT NOT EXISTS(
              SELECT 1 FROM invalid_binding
              UNION ALL SELECT 1 FROM raw_coverage_drift
              UNION ALL SELECT 1 FROM invalid_raw_owner
              UNION ALL SELECT 1 FROM semantic_drift
              UNION ALL SELECT 1 FROM tombstone_drift
              UNION ALL SELECT 1 FROM missing_deleted_graph);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$now", acceptedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
            return true;
        return ValidateTerminalSessionGraph(connection, transaction, sessionId, acceptedAt);
    }

    private static bool ValidateTerminalSessionGraph(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, DateTimeOffset acceptedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM local_workspace_node_content_refs r
              JOIN local_workspace_nodes n ON n.node_id=r.node_id
              JOIN session_events e ON e.event_id=r.source_item_id AND e.session_id=n.session_id
              JOIN retention_items i ON i.item_id=r.retention_item_id AND i.source_item_id=r.source_item_id
              WHERE n.session_id=$session_id AND r.availability_state IN ('deleted','expired','read_denied'))
            AND NOT EXISTS(
              SELECT 1 FROM local_workspace_node_content_refs r
              JOIN local_workspace_nodes n ON n.node_id=r.node_id
              JOIN session_events e ON e.event_id=r.source_item_id AND e.session_id=n.session_id
              LEFT JOIN session_event_content c ON c.event_id=e.event_id
              LEFT JOIN retention_items i ON i.item_id=r.retention_item_id
              LEFT JOIN retention_tombstones terminal ON terminal.item_id=i.item_id
              LEFT JOIN local_workspace_content_tombstones tombstone
                ON tombstone.store_kind=r.store_kind AND tombstone.source_item_id=r.source_item_id AND tombstone.part=r.part
              WHERE n.session_id=$session_id AND (
                NOT (n.source_kind='session_event' AND n.source_identity=r.source_item_id
                  OR n.source_kind='semantic_tool' AND EXISTS(SELECT 1 FROM local_workspace_node_source_references source
                    WHERE source.node_id=n.node_id AND source.event_id=r.source_item_id))
                OR r.availability_state='deleted' AND NOT (
                  i.state='deleted' AND i.deleted_at IS NOT NULL AND c.event_id IS NULL
                  AND terminal.item_id=i.item_id AND terminal.receipt_at=i.deleted_at AND terminal.deleted_at=i.deleted_at
                  AND tombstone.source_item_id=r.source_item_id AND tombstone.deleted_at=i.deleted_at
                  AND tombstone.retention_item_id=i.item_id AND tombstone.retention_revision=i.revision
                  AND r.retention_item_id=i.item_id AND r.retention_revision=i.revision
                  AND r.retention_store_instance_id IS NULL AND r.retention_ownership_receipt IS NULL AND r.retention_owner_token IS NULL)
                OR r.availability_state='expired' AND NOT (
                  i.state IN ('expiring','expired_pending_deletion') AND c.event_id=e.event_id
                  AND i.store_instance_id=r.retention_store_instance_id AND i.revision=r.retention_revision
                  AND i.ownership_receipt=r.retention_ownership_receipt AND c.retention_owner_token=r.retention_owner_token
                  AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,
                    e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1)
                OR r.availability_state='read_denied' AND NOT (
                  i.read_denied_at IS NOT NULL AND c.event_id=e.event_id
                  AND i.store_instance_id=r.retention_store_instance_id AND i.revision=r.retention_revision
                  AND i.ownership_receipt=r.retention_ownership_receipt AND c.retention_owner_token=r.retention_owner_token
                  AND local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,
                    e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt)=1)
                OR r.availability_state NOT IN ('deleted','expired','read_denied','not_captured')
                OR r.availability_state='not_captured' AND NOT (e.content_state='not_captured' AND c.event_id IS NULL AND r.retention_item_id IS NULL)));
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$now", acceptedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

internal interface ILocalWorkspaceNodeContentReader
{
    ValueTask<LocalWorkspaceNodeContentReadResult> ReadAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken);
}

internal sealed class LocalWorkspaceNodeContentReadLease : IAsyncDisposable
{
    private readonly RetentionReadLease<byte[]> lease;

    internal LocalWorkspaceNodeContentReadLease(RetentionReadLease<byte[]> lease) =>
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));

    internal RetentionReadValueReference<byte[]> AcquireBytesReference() => lease.AcquireValueReference();

    internal LocalWorkspaceNodeContentTerminalResult TrySealRawResponse() => Map(lease.TrySealRawResponse());

    internal LocalWorkspaceNodeContentTerminalResult TryCompleteWithoutRaw() => Map(lease.TryCompleteWithoutRaw());

    public ValueTask DisposeAsync() => lease.DisposeAsync();

    private static LocalWorkspaceNodeContentTerminalResult Map(RetentionRawTerminalResult result) => result switch
    {
        RetentionRawTerminalResult.Sealed => LocalWorkspaceNodeContentTerminalResult.Sealed,
        RetentionRawTerminalResult.CompletedWithoutRaw => LocalWorkspaceNodeContentTerminalResult.CompletedWithoutRaw,
        RetentionRawTerminalResult.Busy => LocalWorkspaceNodeContentTerminalResult.Busy,
        _ => LocalWorkspaceNodeContentTerminalResult.Lost,
    };
}

internal sealed class LocalWorkspaceNodeContentReader(
    RetentionCatalogContext retentionContext,
    TimeProvider? timeProvider = null,
    Action? postGrantFailureObserver = null) : ILocalWorkspaceNodeContentReader
{
    private const int MaximumBytes = 1_048_576;
    private const int MaximumEncodedStringBytes = MaximumBytes * 6 + 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<LocalWorkspaceNodeContentReadResult> ReadAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(sessionId, nodeId, locator, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalWorkspaceNodeContentReadDisposition.Busy, null);
        }
        catch (SqliteException)
        {
            return new(LocalWorkspaceNodeContentReadDisposition.Unavailable, null);
        }
    }

    private async ValueTask<LocalWorkspaceNodeContentReadResult> ReadCoreAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(locator);
        if (!CompleteLocator(locator)) return new(LocalWorkspaceNodeContentReadDisposition.Stale, null);

        var catalog = new RetentionCatalogStore(retentionContext, timeProvider);
        using var gate = await catalog.EnterAdmissionGateAsync(cancellationToken).ConfigureAwait(false);
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(
            retentionContext.DatabasePath, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction(deferred: false);
        var acceptedAt = timeProvider.GetUtcNow();

        try
        {
            if (!MatchesSnapshotTuple(connection, transaction, sessionId, nodeId, locator))
            {
                var lifecycle = ClassifyLifecycle(connection, transaction, sessionId, nodeId, locator.Part, acceptedAt);
                transaction.Rollback();
                return new(lifecycle is LocalWorkspaceNodeContentReadDisposition.Expired
                    or LocalWorkspaceNodeContentReadDisposition.Deleted
                    or LocalWorkspaceNodeContentReadDisposition.ReadDenied
                    ? lifecycle
                    : LocalWorkspaceNodeContentReadDisposition.Stale, null);
            }
            if (!LocalWorkspaceContentAuthority.ValidateSessionGraph(connection, transaction, sessionId, acceptedAt))
            {
                transaction.Rollback();
                return new(LocalWorkspaceNodeContentReadDisposition.Unavailable, null);
            }
            var currentLifecycle = ClassifyLifecycle(connection, transaction, sessionId, nodeId, locator.Part, acceptedAt);
            if (currentLifecycle is LocalWorkspaceNodeContentReadDisposition.Expired
                or LocalWorkspaceNodeContentReadDisposition.Deleted
                or LocalWorkspaceNodeContentReadDisposition.ReadDenied)
            {
                transaction.Rollback();
                return new(currentLifecycle, null);
            }

            var request = new RetentionReadRequest(
                new(locator.RetentionStoreInstanceId!, RetentionStoreKind.SessionEventContent, locator.SourceItemId!),
                RetentionReadKind.Access,
                acceptedAt,
                locator.RetentionRevision);
            var result = await catalog.ReadWithinCallerTransactionAsync(
                connection,
                transaction,
                request,
                (c, t, grant, token) => SelectBoundedWithFailureObservationAsync(
                    c, t, grant, sessionId, locator, token),
                cancellationToken).ConfigureAwait(false);

            if (result.Lease is { } postGrantLease && result.Disposition is { } postGrantDisposition)
            {
                postGrantFailureObserver?.Invoke();
                await using (postGrantLease.ConfigureAwait(false))
                {
                    var terminal = result.CompletePostGrantFailure();
                    return new(
                        postGrantDisposition == RetentionReadDisposition.Busy
                            || terminal != RetentionRawTerminalResult.CompletedWithoutRaw
                            ? LocalWorkspaceNodeContentReadDisposition.Busy
                            : LocalWorkspaceNodeContentReadDisposition.Unavailable,
                        null);
                }
            }
            if (result.Lease is { } lease)
                return new(LocalWorkspaceNodeContentReadDisposition.Granted, new(lease));
            return new(result.Disposition switch
            {
                RetentionReadDisposition.Busy => LocalWorkspaceNodeContentReadDisposition.Busy,
                RetentionReadDisposition.SelectorUnavailable or RetentionReadDisposition.ConsumptionUnavailable =>
                    LocalWorkspaceNodeContentReadDisposition.Unavailable,
                _ => LocalWorkspaceNodeContentReadDisposition.Stale,
            }, null);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            TryRollback(transaction);
            return new(LocalWorkspaceNodeContentReadDisposition.Busy, null);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or FormatException or DecoderFallbackException or JsonException)
        {
            TryRollback(transaction);
            return new(LocalWorkspaceNodeContentReadDisposition.Unavailable, null);
        }
    }

    private async ValueTask<byte[]?> SelectBoundedWithFailureObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        string sessionId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        try
        {
            var selected = await SelectBoundedAsync(
                connection, transaction, grant, sessionId, locator, cancellationToken).ConfigureAwait(false);
            if (selected is null) postGrantFailureObserver?.Invoke();
            return selected;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or DecoderFallbackException or JsonException)
        {
            postGrantFailureObserver?.Invoke();
            throw;
        }
    }

    private static bool CompleteLocator(LocalWorkspaceContentAvailability locator) =>
        locator.State == "available"
        && locator.StoreKind == "session_event_content"
        && locator.SourceItemId is not null
        && locator.RevisionInput is not null
        && locator.RetentionItemId is not null
        && locator.RetentionStoreInstanceId is not null
        && locator.SourceCapturedAt is not null
        && locator.SourceExpiresAt is not null
        && locator.RetentionRevision is not null
        && locator.RetentionOwnershipReceipt is { Length: 32 }
        && locator.RetentionOwnerToken is { Length: 32 }
        && locator.SelectedUtf8Bytes is >= 0 and <= MaximumBytes
        && (locator.LocatorKind == "whole_event" && locator.JsonPointer is null
            || locator.LocatorKind == "json_pointer" && locator.JsonPointer is not null);

    private static bool MatchesSnapshotTuple(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM local_workspace_node_content_refs r
            JOIN local_workspace_nodes n ON n.node_id=r.node_id
            JOIN session_events e ON e.event_id=r.source_item_id AND e.session_id=n.session_id
            JOIN session_event_content c ON c.event_id=e.event_id
            JOIN retention_items i ON i.item_id=r.retention_item_id
            WHERE r.node_id=$node_id AND n.session_id=$session_id AND e.type<>'skill.invoked'
              AND r.part=$part AND r.availability_state='available'
              AND r.source_item_id=$source_item_id AND r.revision_input=$revision_input
              AND r.store_kind=$store_kind AND r.locator_kind=$locator_kind
              AND r.json_pointer IS $json_pointer AND r.selected_utf8_bytes=$selected_utf8_bytes
              AND r.retention_item_id=$retention_item_id
              AND r.retention_store_instance_id=$retention_store_instance_id
              AND r.source_captured_at=$source_captured_at AND r.source_expires_at=$source_expires_at
              AND r.retention_revision=$retention_revision
              AND r.retention_ownership_receipt=$retention_ownership_receipt
              AND r.retention_owner_token=$retention_owner_token
              AND i.store_instance_id=r.retention_store_instance_id
              AND i.store_kind=r.store_kind AND i.source_item_id=r.source_item_id
              AND i.revision=r.retention_revision AND i.ownership_receipt=r.retention_ownership_receipt
              AND i.captured_at=r.source_captured_at AND i.expires_at=r.source_expires_at
              AND c.captured_at=r.source_captured_at AND c.expires_at=r.source_expires_at
              AND c.retention_owner_token=r.retention_owner_token;
            """;
        Add(command, "$node_id", nodeId);
        Add(command, "$session_id", sessionId);
        Add(command, "$part", locator.Part);
        Add(command, "$source_item_id", locator.SourceItemId!);
        Add(command, "$revision_input", locator.RevisionInput!);
        Add(command, "$store_kind", locator.StoreKind!);
        Add(command, "$locator_kind", locator.LocatorKind!);
        Add(command, "$json_pointer", locator.JsonPointer);
        Add(command, "$selected_utf8_bytes", locator.SelectedUtf8Bytes!.Value);
        Add(command, "$retention_item_id", locator.RetentionItemId!);
        Add(command, "$retention_store_instance_id", locator.RetentionStoreInstanceId!);
        Add(command, "$source_captured_at", locator.SourceCapturedAt!);
        Add(command, "$source_expires_at", locator.SourceExpiresAt!);
        Add(command, "$retention_revision", locator.RetentionRevision!.Value);
        Add(command, "$retention_ownership_receipt", locator.RetentionOwnershipReceipt!);
        Add(command, "$retention_owner_token", locator.RetentionOwnerToken!);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static LocalWorkspaceNodeContentReadDisposition ClassifyLifecycle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string nodeId,
        string part,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.state,i.expires_at,i.read_denied_at
            FROM local_workspace_node_content_refs c
            JOIN local_workspace_nodes n ON n.node_id=c.node_id
            JOIN session_events e ON e.event_id=c.source_item_id AND e.session_id=n.session_id
            LEFT JOIN retention_items i ON i.item_id=c.retention_item_id
            WHERE n.session_id=$session_id AND c.node_id=$node_id AND c.part=$part;
            """;
        Add(command, "$session_id", sessionId);
        Add(command, "$node_id", nodeId);
        Add(command, "$part", part);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
            return LocalWorkspaceNodeContentReadDisposition.Stale;
        var state = reader.GetString(0) switch
        {
            "expiring" => RetentionItemLifecycle.Expiring,
            "retained_by_policy" => RetentionItemLifecycle.RetainedByPolicy,
            "expired_pending_deletion" => RetentionItemLifecycle.ExpiredPendingDeletion,
            "deletion_queued" => RetentionItemLifecycle.DeletionQueued,
            "deleting" => RetentionItemLifecycle.Deleting,
            "deleted" => RetentionItemLifecycle.Deleted,
            "deletion_failed" => RetentionItemLifecycle.DeletionFailed,
            _ => (RetentionItemLifecycle?)null,
        };
        if (state is null || !DateTimeOffset.TryParseExact(
                reader.GetString(1), "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var expiresAt))
            return LocalWorkspaceNodeContentReadDisposition.Stale;
        var readability = RetentionCatalogStore.ClassifyRowReadability(
            state.Value, expiresAt, !reader.IsDBNull(2), now);
        if (state == RetentionItemLifecycle.Deleted)
            return LocalWorkspaceNodeContentReadDisposition.Deleted;
        if (state == RetentionItemLifecycle.ExpiredPendingDeletion)
            return LocalWorkspaceNodeContentReadDisposition.Expired;
        return readability switch
        {
            RetentionRowReadability.AlreadyDenied => LocalWorkspaceNodeContentReadDisposition.ReadDenied,
            RetentionRowReadability.ExpiredExpiring => LocalWorkspaceNodeContentReadDisposition.Expired,
            RetentionRowReadability.LifecycleDenied when state == RetentionItemLifecycle.Deleted =>
                LocalWorkspaceNodeContentReadDisposition.Deleted,
            RetentionRowReadability.LifecycleDenied => LocalWorkspaceNodeContentReadDisposition.Expired,
            _ => LocalWorkspaceNodeContentReadDisposition.Stale,
        };
    }

    private static async ValueTask<byte[]?> SelectBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        string sessionId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.rowid,e.source_adapter,e.schema_fingerprint,e.type,c.content_json
            FROM session_event_content c
            JOIN session_events e ON e.event_id=c.event_id
            JOIN retention_items i ON i.item_id=$retention_read_item_id
              AND i.store_instance_id=$retention_store_instance_id
              AND i.store_kind='session_event_content' AND i.source_item_id=c.event_id
              AND i.revision=$retention_read_revision
            JOIN retention_leases l ON l.item_id=i.item_id
              AND l.lease_kind=$retention_read_lease_kind AND l.owner=$retention_read_lease_owner
              AND l.generation=$retention_read_lease_generation AND l.expires_at=$retention_read_lease_expires_at
            WHERE e.session_id=$session_id AND c.event_id=$event_id
              AND e.type<>'skill.invoked' AND c.retention_owner_token=$retention_read_source_token;
            """;
        Add(command, "$session_id", sessionId);
        Add(command, "$event_id", locator.SourceItemId!);
        Add(command, "$retention_store_instance_id", locator.RetentionStoreInstanceId!);
        grant.BindAdmissionSelectorCapability(command);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(4)) return null;
        var sourceAdapter = reader.GetString(1);
        var schemaFingerprint = reader.IsDBNull(2) ? null : reader.GetString(2);
        var eventType = reader.GetString(3);
        var candidate = LocalWorkspaceProjectionStore.ContentSelectorCandidate(sourceAdapter, schemaFingerprint, eventType);
        await using var stream = reader.GetStream(4);
        byte[]? bytes;
        if (locator.LocatorKind == "whole_event")
        {
            bytes = ReadWholeEvent(stream);
            if (bytes is not null && LocalWorkspaceProjectionStore.ContentPointer(
                    sourceAdapter, schemaFingerprint, eventType, StrictUtf8.GetString(bytes)) is not null)
                return null;
        }
        else
        {
            if (candidate is null
                || !string.Equals(locator.JsonPointer, candidate.Value.Pointer, StringComparison.Ordinal)
                || !string.Equals(locator.Part, LocalWorkspaceProjectionStore.ContentPart(candidate.Value.Pointer), StringComparison.Ordinal))
                return null;
            var selected = TopLevelJsonValueExtractor.Read(stream, candidate.Value.Property);
            if (selected is null || candidate.Value.RequiredKind != JsonValueKind.Undefined
                && selected.Kind != candidate.Value.RequiredKind)
                return null;
            bytes = selected.Bytes;
        }
        if (bytes is null || bytes.LongLength != locator.SelectedUtf8Bytes || bytes.Length > MaximumBytes) return null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return bytes;
    }

    private static byte[]? ReadWholeEvent(Stream stream)
    {
        using var result = new MemoryStream(MaximumBytes + 1);
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)result.Length));
            if (read == 0) break;
            result.Write(buffer, 0, read);
            if (result.Length > MaximumBytes) return null;
        }
        var bytes = result.ToArray();
        _ = StrictUtf8.GetString(bytes);
        using (JsonDocument.Parse(bytes)) { }
        return bytes;
    }

    private sealed record SelectedTopLevelJsonValue(byte[] Bytes, JsonValueKind Kind);

    private sealed class TopLevelJsonValueExtractor(Stream stream)
    {
        private readonly Decoder decoder = StrictUtf8.GetDecoder();
        private int pending = -1;

        internal static SelectedTopLevelJsonValue? Read(Stream stream, string property) =>
            new TopLevelJsonValueExtractor(stream).ReadObject(property);

        private SelectedTopLevelJsonValue? ReadObject(string property)
        {
            if (NextNonWhitespace() != (byte)'{') throw new JsonException();
            SelectedTopLevelJsonValue? selected = null;
            var next = NextNonWhitespace();
            if (next == (byte)'}') { Finish(); return null; }
            PutBack(next);
            while (true)
            {
                var keyToken = ReadString(true, checked(property.Length * 6 + 2));
                var matchesProperty = keyToken is not null
                    && StringComparer.Ordinal.Equals(JsonSerializer.Deserialize<string>(keyToken), property);
                if (NextNonWhitespace() != (byte)':') throw new JsonException();
                var value = ReadValue(matchesProperty);
                if (matchesProperty) selected = value;
                next = NextNonWhitespace();
                if (next == (byte)'}') break;
                if (next != (byte)',') throw new JsonException();
            }
            if (NextNonWhitespaceOrEnd() is not null) throw new JsonException();
            Finish();
            return selected;
        }

        private SelectedTopLevelJsonValue? ReadValue(bool capture)
        {
            var first = NextNonWhitespace();
            var maximumBytes = first == (byte)'"' ? MaximumEncodedStringBytes : MaximumBytes;
            using var output = capture ? new MemoryStream(Math.Min(maximumBytes + 1, 4096)) : null;
            Write(output, first, maximumBytes);
            var kind = ParseValue(first, output, maximumBytes, 0);
            if (!capture) return null;
            if (output!.Length > maximumBytes) return null;
            var bytes = output.ToArray();
            if (kind != JsonValueKind.String) return new(bytes, kind);
            var text = JsonSerializer.Deserialize<string>(bytes) ?? throw new JsonException();
            return new(StrictUtf8.GetBytes(text), JsonValueKind.String);
        }

        private byte[]? ReadString(bool capture, int maximumBytes)
        {
            if (Next() != (byte)'"') throw new JsonException();
            using var output = capture ? new MemoryStream(Math.Min(maximumBytes + 1, 4096)) : null;
            Write(output, (byte)'"', maximumBytes);
            ReadStringBody(output, maximumBytes);
            if (!capture) return null;
            if (output!.Length > maximumBytes) return null;
            return output.ToArray();
        }

        private JsonValueKind ParseValue(byte first, MemoryStream? output, int maximumBytes, int depth)
        {
            if (depth > 64) throw new JsonException();
            switch (first)
            {
                case (byte)'"':
                    ReadStringBody(output, maximumBytes);
                    return JsonValueKind.String;
                case (byte)'{':
                    ReadObjectBody(output, maximumBytes, depth + 1);
                    return JsonValueKind.Object;
                case (byte)'[':
                    ReadArrayBody(output, maximumBytes, depth + 1);
                    return JsonValueKind.Array;
                case (byte)'t':
                    ReadLiteral("rue"u8, output, maximumBytes);
                    return JsonValueKind.True;
                case (byte)'f':
                    ReadLiteral("alse"u8, output, maximumBytes);
                    return JsonValueKind.False;
                case (byte)'n':
                    ReadLiteral("ull"u8, output, maximumBytes);
                    return JsonValueKind.Null;
                default:
                    if (first != (byte)'-' && (first < (byte)'0' || first > (byte)'9')) throw new JsonException();
                    ReadNumber(first, output, maximumBytes);
                    return JsonValueKind.Number;
            }
        }

        private void ReadObjectBody(MemoryStream? output, int maximumBytes, int depth)
        {
            var next = NextNonWhitespace(output, maximumBytes);
            if (next == (byte)'}') return;
            while (true)
            {
                if (next != (byte)'"') throw new JsonException();
                ReadStringBody(output, maximumBytes);
                if (NextNonWhitespace(output, maximumBytes) != (byte)':') throw new JsonException();
                var first = NextNonWhitespace(output, maximumBytes);
                ParseValue(first, output, maximumBytes, depth);
                next = NextNonWhitespace(output, maximumBytes);
                if (next == (byte)'}') return;
                if (next != (byte)',') throw new JsonException();
                next = NextNonWhitespace(output, maximumBytes);
            }
        }

        private void ReadArrayBody(MemoryStream? output, int maximumBytes, int depth)
        {
            var next = NextNonWhitespace(output, maximumBytes);
            if (next == (byte)']') return;
            while (true)
            {
                ParseValue(next, output, maximumBytes, depth);
                next = NextNonWhitespace(output, maximumBytes);
                if (next == (byte)']') return;
                if (next != (byte)',') throw new JsonException();
                next = NextNonWhitespace(output, maximumBytes);
            }
        }

        private void ReadStringBody(MemoryStream? output, int maximumBytes)
        {
            while (true)
            {
                var value = Next();
                Write(output, value, maximumBytes);
                if (value == (byte)'"') return;
                if (value < 0x20) throw new JsonException();
                if (value != (byte)'\\') continue;
                var escaped = Next();
                Write(output, escaped, maximumBytes);
                if (escaped is (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')
                    continue;
                if (escaped != (byte)'u') throw new JsonException();
                var codePoint = ReadHexEscape(output, maximumBytes);
                if (codePoint is >= 0xdc00 and <= 0xdfff) throw new JsonException();
                if (codePoint is not (>= 0xd800 and <= 0xdbff)) continue;
                var slash = Next();
                var unicode = Next();
                Write(output, slash, maximumBytes);
                Write(output, unicode, maximumBytes);
                if (slash != (byte)'\\' || unicode != (byte)'u') throw new JsonException();
                var low = ReadHexEscape(output, maximumBytes);
                if (low is not (>= 0xdc00 and <= 0xdfff)) throw new JsonException();
            }
        }

        private int ReadHexEscape(MemoryStream? output, int maximumBytes)
        {
            var result = 0;
            for (var index = 0; index < 4; index++)
            {
                var value = Next();
                Write(output, value, maximumBytes);
                result = checked(result * 16 + value switch
                {
                    >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
                    _ => throw new JsonException(),
                });
            }
            return result;
        }

        private void ReadLiteral(ReadOnlySpan<byte> remainder, MemoryStream? output, int maximumBytes)
        {
            foreach (var expected in remainder)
            {
                var value = Next();
                Write(output, value, maximumBytes);
                if (value != expected) throw new JsonException();
            }
        }

        private void ReadNumber(byte first, MemoryStream? output, int maximumBytes)
        {
            var current = first;
            if (current == (byte)'-')
            {
                current = Next();
                Write(output, current, maximumBytes);
                if (current < (byte)'0' || current > (byte)'9') throw new JsonException();
            }
            if (current == (byte)'0')
            {
                current = Next();
                if (current is >= (byte)'0' and <= (byte)'9') throw new JsonException();
            }
            else
            {
                if (current < (byte)'1' || current > (byte)'9') throw new JsonException();
                do
                {
                    current = Next();
                    if (current is >= (byte)'0' and <= (byte)'9') Write(output, current, maximumBytes);
                } while (current is >= (byte)'0' and <= (byte)'9');
            }
            if (current == (byte)'.')
            {
                Write(output, current, maximumBytes);
                current = Next();
                if (current < (byte)'0' || current > (byte)'9') throw new JsonException();
                do
                {
                    Write(output, current, maximumBytes);
                    current = Next();
                } while (current is >= (byte)'0' and <= (byte)'9');
            }
            if (current is (byte)'e' or (byte)'E')
            {
                Write(output, current, maximumBytes);
                current = Next();
                if (current is (byte)'+' or (byte)'-')
                {
                    Write(output, current, maximumBytes);
                    current = Next();
                }
                if (current < (byte)'0' || current > (byte)'9') throw new JsonException();
                do
                {
                    Write(output, current, maximumBytes);
                    current = Next();
                } while (current is >= (byte)'0' and <= (byte)'9');
            }
            PutBack(current);
        }

        private byte NextNonWhitespace()
        {
            byte value;
            do value = Next(); while (IsWhitespace(value));
            return value;
        }

        private byte NextNonWhitespace(MemoryStream? output, int maximumBytes)
        {
            byte value;
            do
            {
                value = Next();
                Write(output, value, maximumBytes);
            } while (IsWhitespace(value));
            return value;
        }

        private byte? NextNonWhitespaceOrEnd()
        {
            while (true)
            {
                var value = NextOrEnd();
                if (value is null || !IsWhitespace(value.Value)) return value;
            }
        }

        private byte Next() => NextOrEnd() ?? throw new JsonException();

        private byte? NextOrEnd()
        {
            if (pending >= 0) { var value = (byte)pending; pending = -1; return value; }
            var valueRead = stream.ReadByte();
            if (valueRead < 0) return null;
            Span<byte> input = stackalloc byte[1] { (byte)valueRead };
            Span<char> chars = stackalloc char[2];
            decoder.Convert(input, chars, false, out _, out _, out _);
            return (byte)valueRead;
        }

        private void Finish()
        {
            Span<byte> input = [];
            Span<char> chars = stackalloc char[2];
            decoder.Convert(input, chars, true, out _, out _, out _);
        }

        private void PutBack(byte value)
        {
            if (pending >= 0) throw new InvalidOperationException();
            pending = value;
        }

        private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        private static void Write(MemoryStream? output, byte value, int maximumBytes = MaximumBytes)
        {
            if (output is null || output.Length > maximumBytes) return;
            output.WriteByte(value);
        }
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException) { }
    }
}

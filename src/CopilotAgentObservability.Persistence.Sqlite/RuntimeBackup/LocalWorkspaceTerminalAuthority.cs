using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal sealed class LocalWorkspaceTerminalAuthority
{
    private readonly ReadDeniedReference[] readDeniedReferences;
    private readonly DeletedTombstone[] deletedTombstones;

    private LocalWorkspaceTerminalAuthority(
        ReadDeniedReference[] readDeniedReferences,
        DeletedTombstone[] deletedTombstones) =>
        (this.readDeniedReferences, this.deletedTombstones) =
        (readDeniedReferences, deletedTombstones);

    internal static LocalWorkspaceTerminalAuthority Capture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool includeSourceBackedReadDenied = false)
    {
        if (!TableExists(connection, transaction, "local_workspace_node_content_refs")
            || !TableExists(connection, transaction, "local_workspace_content_tombstones")
            || !TableExists(connection, transaction, "retention_items"))
            return new([], []);
        var v5ContentShape = ColumnExists(
            connection,
            transaction,
            "local_workspace_content_tombstones",
            "store_kind");
        var legacyContentFilter = v5ContentShape
            ? string.Empty
            : " AND r.part<>'subagent_input' AND NOT (r.json_pointer IS '/agent_id')";
        var semanticContentBinding = TableExists(
            connection,
            transaction,
            "local_workspace_node_source_references")
            ? """
                OR (n.source_kind='semantic_tool' AND EXISTS(
                  SELECT 1 FROM local_workspace_node_source_references x
                  WHERE x.node_id=n.node_id
                    AND x.source_kind='session_event'
                    AND x.event_id=e.event_id
                    AND x.source_identity=e.event_id))
                """
            : string.Empty;
        var readDeniedRevisionFilter = v5ContentShape
            ? """
                AND r.revision_input=e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||
                    i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|'
                """
            : string.Empty;
        var readDeniedSourceFilter = includeSourceBackedReadDenied
            ? string.Empty
            : " AND NOT EXISTS(SELECT 1 FROM session_event_content c WHERE c.event_id=r.source_item_id)";

        var readDenied = new List<ReadDeniedReference>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $$"""
                SELECT r.node_id,r.part,r.store_kind,r.source_item_id,r.locator_kind,
                       r.json_pointer,r.selected_utf8_bytes,r.revision_input,
                       i.item_id,i.store_instance_id,i.captured_at,i.expires_at,
                       i.revision,i.ownership_receipt
                FROM local_workspace_node_content_refs r
                JOIN local_workspace_nodes n ON n.node_id=r.node_id
                JOIN session_events e ON e.event_id=r.source_item_id
                JOIN retention_items i
                  ON i.store_kind='session_event_content'
                 AND i.source_item_id=r.source_item_id
                 AND r.retention_item_id=i.item_id
                 AND r.retention_store_instance_id=i.store_instance_id
                 AND r.source_captured_at=i.captured_at
                 AND r.source_expires_at=i.expires_at
                 AND r.retention_revision=i.revision
                 AND r.retention_ownership_receipt=i.ownership_receipt
                WHERE r.store_kind='session_event_content'
                  AND r.availability_state='read_denied'
                  AND r.retention_owner_token IS NULL
                   AND i.read_denied_at IS NOT NULL
                   AND i.state<>'deleted'
                   AND i.deleted_at IS NULL
                   AND ((n.source_kind='session_event' AND n.source_identity=e.event_id)
                     {{semanticContentBinding}})
                {{readDeniedRevisionFilter}}
                {{legacyContentFilter}}
                {{readDeniedSourceFilter}}
                ORDER BY r.node_id COLLATE BINARY,r.part COLLATE BINARY;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                readDenied.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetInt64(12),
                    (byte[])reader.GetValue(13)));
            }
        }
        if (readDenied.Count != Count(connection, transaction,
                "local_workspace_node_content_refs",
                "availability_state='read_denied' AND store_kind='session_event_content'" +
                (v5ContentShape ? string.Empty : " AND part<>'subagent_input' AND NOT (json_pointer IS '/agent_id')") +
                (includeSourceBackedReadDenied
                    ? string.Empty
                    : " AND NOT EXISTS(SELECT 1 FROM session_event_content c WHERE c.event_id=source_item_id)")))
            throw new InvalidOperationException("local_workspace_terminal_authority_invalid");

        var tombstones = new List<DeletedTombstone>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $$"""
                SELECT {{(v5ContentShape ? "t.store_kind" : "'session_event_content'")}},t.source_item_id,t.part,t.locator_kind,t.json_pointer,
                       t.selected_utf8_bytes,t.deleted_at,t.retention_item_id,t.retention_revision
                FROM local_workspace_content_tombstones t
                JOIN retention_items i
                  ON i.item_id=t.retention_item_id
                 AND i.store_kind={{(v5ContentShape ? "t.store_kind" : "'session_event_content'")}}
                 AND i.source_item_id=t.source_item_id
                 AND i.revision=t.retention_revision
                 AND i.deleted_at=t.deleted_at
                JOIN retention_tombstones r
                  ON r.item_id=i.item_id AND r.deleted_at=i.deleted_at
                WHERE i.state='deleted'
                {{(v5ContentShape ? string.Empty : "AND t.part<>'subagent_input' AND NOT (t.json_pointer IS '/agent_id')")}}
                ORDER BY t.source_item_id COLLATE BINARY,t.part COLLATE BINARY;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tombstones.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt64(8)));
            }
        }
        if (tombstones.Count != Count(
                connection,
                transaction,
                "local_workspace_content_tombstones",
                v5ContentShape ? "1=1" : "part<>'subagent_input' AND NOT (json_pointer IS '/agent_id')"))
            throw new InvalidOperationException("local_workspace_terminal_authority_invalid");
        return new(readDenied.ToArray(), tombstones.ToArray());
    }

    internal void ApplyTombstones(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var row in deletedTombstones)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_workspace_content_tombstones(
                    store_kind,source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                    deleted_at,retention_item_id,retention_revision)
                VALUES($store_kind,$source_item_id,$part,$locator_kind,$json_pointer,
                    $selected_utf8_bytes,$deleted_at,$retention_item_id,$retention_revision)
                ON CONFLICT(store_kind,source_item_id,part) DO UPDATE SET
                    locator_kind=excluded.locator_kind,
                    json_pointer=excluded.json_pointer,
                    selected_utf8_bytes=excluded.selected_utf8_bytes,
                    deleted_at=excluded.deleted_at,
                    retention_item_id=excluded.retention_item_id,
                    retention_revision=excluded.retention_revision;
                """;
            Bind(command, row);
            command.ExecuteNonQuery();
        }
    }

    internal void ApplyReadDenied(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var group in readDeniedReferences.GroupBy(
                     static row => (row.NodeId, row.StoreKind, row.SourceItemId)))
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM local_workspace_node_content_refs
                    WHERE node_id=$node_id
                      AND store_kind=$store_kind
                      AND source_item_id=$source_item_id;
                    """;
                delete.Parameters.AddWithValue("$node_id", group.Key.NodeId);
                delete.Parameters.AddWithValue("$store_kind", group.Key.StoreKind);
                delete.Parameters.AddWithValue("$source_item_id", group.Key.SourceItemId);
                delete.ExecuteNonQuery();
            }
            foreach (var row in group)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_workspace_node_content_refs(
                        node_id,part,store_kind,source_item_id,locator_kind,json_pointer,
                        selected_utf8_bytes,revision_input,retention_item_id,
                        retention_store_instance_id,source_captured_at,source_expires_at,
                        retention_revision,retention_ownership_receipt,retention_owner_token,
                        availability_state)
                    SELECT $node_id,$part,$store_kind,$source_item_id,$locator_kind,$json_pointer,
                           $selected_utf8_bytes,
                           (SELECT e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||
                                   i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|'
                            FROM session_events e
                            JOIN retention_items i ON i.item_id=$retention_item_id
                            WHERE e.event_id=$source_item_id),
                           $retention_item_id,
                           $retention_store_instance_id,$source_captured_at,$source_expires_at,
                           $retention_revision,$retention_ownership_receipt,NULL,'read_denied'
                    WHERE NOT EXISTS(
                            SELECT 1 FROM session_event_content c
                            WHERE c.event_id=$source_item_id)
                      AND EXISTS(
                        SELECT 1 FROM retention_items i
                        WHERE i.item_id=$retention_item_id
                          AND i.store_kind=$store_kind
                          AND i.source_item_id=$source_item_id
                          AND i.store_instance_id=$retention_store_instance_id
                          AND i.store_instance_id=(
                            SELECT store_instance_id FROM retention_store_instances WHERE id=1)
                          AND i.captured_at=$source_captured_at
                          AND i.expires_at=$source_expires_at
                          AND i.revision=$retention_revision
                          AND i.ownership_receipt=$retention_ownership_receipt
                          AND i.read_denied_at IS NOT NULL
                          AND i.state<>'deleted'
                          AND i.deleted_at IS NULL)
                      AND EXISTS(
                        SELECT 1 FROM local_workspace_nodes n
                        WHERE n.node_id=$node_id
                          AND ((n.source_kind='session_event' AND n.source_identity=$source_item_id)
                            OR (n.source_kind='semantic_tool' AND EXISTS(
                              SELECT 1 FROM local_workspace_node_source_references r
                              WHERE r.node_id=n.node_id AND r.source_kind='session_event'
                                AND r.event_id=$source_item_id
                                AND r.source_identity=$source_item_id))));
                    """;
                Bind(command, row);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("local_workspace_terminal_authority_invalid");
            }
        }
    }

    private static void Bind(SqliteCommand command, ReadDeniedReference row)
    {
        command.Parameters.AddWithValue("$node_id", row.NodeId);
        command.Parameters.AddWithValue("$part", row.Part);
        command.Parameters.AddWithValue("$store_kind", row.StoreKind);
        command.Parameters.AddWithValue("$source_item_id", row.SourceItemId);
        command.Parameters.AddWithValue("$locator_kind", row.LocatorKind);
        command.Parameters.AddWithValue("$json_pointer", (object?)row.JsonPointer ?? DBNull.Value);
        command.Parameters.AddWithValue("$selected_utf8_bytes", (object?)row.SelectedUtf8Bytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision_input", row.RevisionInput);
        command.Parameters.AddWithValue("$retention_item_id", row.RetentionItemId);
        command.Parameters.AddWithValue("$retention_store_instance_id", row.RetentionStoreInstanceId);
        command.Parameters.AddWithValue("$source_captured_at", row.SourceCapturedAt);
        command.Parameters.AddWithValue("$source_expires_at", row.SourceExpiresAt);
        command.Parameters.AddWithValue("$retention_revision", row.RetentionRevision);
        command.Parameters.AddWithValue("$retention_ownership_receipt", row.RetentionOwnershipReceipt);
    }

    private static void Bind(SqliteCommand command, DeletedTombstone row)
    {
        command.Parameters.AddWithValue("$store_kind", row.StoreKind);
        command.Parameters.AddWithValue("$source_item_id", row.SourceItemId);
        command.Parameters.AddWithValue("$part", row.Part);
        command.Parameters.AddWithValue("$locator_kind", row.LocatorKind);
        command.Parameters.AddWithValue("$json_pointer", (object?)row.JsonPointer ?? DBNull.Value);
        command.Parameters.AddWithValue("$selected_utf8_bytes", (object?)row.SelectedUtf8Bytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted_at", row.DeletedAt);
        command.Parameters.AddWithValue("$retention_item_id", row.RetentionItemId);
        command.Parameters.AddWithValue("$retention_revision", row.RetentionRevision);
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name COLLATE BINARY);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_xinfo($table) WHERE name=$column COLLATE BINARY);";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static long Count(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string predicate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ReadDeniedReference(
        string NodeId,
        string Part,
        string StoreKind,
        string SourceItemId,
        string LocatorKind,
        string? JsonPointer,
        long? SelectedUtf8Bytes,
        string RevisionInput,
        string RetentionItemId,
        string RetentionStoreInstanceId,
        string SourceCapturedAt,
        string SourceExpiresAt,
        long RetentionRevision,
        byte[] RetentionOwnershipReceipt);

    private sealed record DeletedTombstone(
        string StoreKind,
        string SourceItemId,
        string Part,
        string LocatorKind,
        string? JsonPointer,
        long? SelectedUtf8Bytes,
        string DeletedAt,
        string RetentionItemId,
        long RetentionRevision);
}

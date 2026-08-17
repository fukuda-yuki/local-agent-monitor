using System.Data;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

// Structural and link-graph proof. The content-document digest, base64 grammar, and
// reclassification proofs need the raw document that only the ingest writer produces; they
// land with that writer rather than here, so this validator never selects content_json.
internal static class SkillInvocationSnapshotBackupValidation
{
    private const string InvalidState = "skill_invocation_snapshot_backup_invalid";

    private static readonly (string Name, string Sql)[] Violations =
    [
        ("event_binding", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                LEFT JOIN session_events e
                    ON e.session_id=s.session_id AND e.event_id=s.event_id
                WHERE e.event_id IS NULL
                   OR e.type<>'skill.invoked'
                   OR e.source_adapter<>'copilot-sdk-stream'
                   OR e.source_surface<>'copilot-sdk'
                   OR e.content_state<>'available'
                   OR e.parent_event_id IS NOT NULL
                   OR e.status IS NOT NULL
                   OR e.match_kind IS NOT NULL
                   OR e.terminal_outcome IS NOT NULL
                   OR e.terminal_policy_version IS NOT NULL
                   OR e.source_application_version IS NOT s.source_application_version
                   OR e.adapter_version IS NOT s.adapter_version
                   OR e.normalization_version IS NOT s.normalization_version
                   OR e.schema_fingerprint IS NOT s.schema_fingerprint
                   OR (s.run_id IS NOT NULL AND e.run_id IS NOT s.run_id));
            """),
        ("receipt_cardinality", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                WHERE (SELECT COUNT(*) FROM skill_invocation_snapshot_receipts r
                       WHERE r.snapshot_id=s.snapshot_id)<>1);
            """),
        ("receipt_source_identity", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshot_receipts r
                JOIN skill_invocation_snapshots s ON s.snapshot_id=r.snapshot_id
                LEFT JOIN session_events e
                    ON e.session_id=s.session_id AND e.event_id=s.event_id
                WHERE e.event_id IS NULL
                   OR e.source_event_id<>r.source_event_id
                   OR e.source_adapter<>r.source_adapter);
            """),
        ("retention_ownership", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                LEFT JOIN retention_items i ON i.item_id=s.content_item_id
                WHERE i.item_id IS NULL
                   OR i.store_kind<>'session_event_content'
                   OR i.source_item_id<>s.event_id
                   OR i.captured_at<>s.captured_at);
            """),
        ("selected_native_binding", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                LEFT JOIN session_native_ids n
                    ON n.source_surface='copilot-sdk' AND n.native_session_id=s.native_session_id
                WHERE n.native_session_id IS NULL
                   OR n.session_id<>s.session_id
                   OR n.binding_kind NOT IN ('native','explicit_resume','explicit_handoff'));
            """),
        ("run_cardinality", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                WHERE s.run_id IS NOT NULL
                  AND (SELECT COUNT(*) FROM session_runs r
                       WHERE r.session_id=s.session_id AND r.run_id=s.run_id)<>1);
            """),
        ("claim_equality", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                WHERE s.claim_id IS NOT NULL
                  AND NOT EXISTS(
                    SELECT 1 FROM skill_projection_sdk_claims c
                    WHERE c.claim_id=s.claim_id
                      AND c.session_id=s.session_id
                      AND c.event_id=s.event_id
                      AND c.created_at=s.captured_at
                      AND c.skill_name IS s.name
                      AND c.skill_source IS s.source
                      AND c.invocation_trigger IS s.trigger
                      AND c.payload_schema=s.payload_schema
                      AND c.schema_fingerprint=s.schema_fingerprint
                      AND c.payload_sha256=s.payload_sha256));
            """),
        ("write_at_equalities", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                WHERE s.created_at<>s.captured_at)
                OR EXISTS(
                SELECT 1 FROM skill_invocation_snapshot_receipts r
                JOIN skill_invocation_snapshots s ON s.snapshot_id=r.snapshot_id
                WHERE r.created_at<>s.captured_at);
            """),
        // Exactly two graphs are legal: the raw row present under a non-deleted item with no
        // tombstone, or the raw row absent under an exactly deleted item with its tombstone.
        // Everything else is corruption, including a tombstone beside surviving content.
        ("raw_lifecycle_graph", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                LEFT JOIN session_event_content c ON c.event_id=s.event_id
                LEFT JOIN retention_items i ON i.item_id=s.content_item_id
                LEFT JOIN retention_tombstones t ON t.item_id=s.content_item_id
                WHERE i.item_id IS NULL
                   OR (c.event_id IS NOT NULL
                       AND (i.state='deleted'
                            OR t.item_id IS NOT NULL
                            OR c.content_kind<>'application/json'
                            OR c.captured_at<>s.captured_at
                            OR c.expires_at<>i.expires_at))
                   OR (c.event_id IS NULL
                       AND (i.state<>'deleted' OR t.item_id IS NULL)));
            """),
        ("session_time_envelope", """
            SELECT EXISTS(
                SELECT 1 FROM skill_invocation_snapshots s
                JOIN sessions x ON x.session_id=s.session_id
                WHERE NOT (x.created_at<=s.captured_at AND s.captured_at<=x.updated_at));
            """),
    ];

    internal static void Validate(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        if (connection.State != ConnectionState.Open
            || !ReferenceEquals(transaction.Connection, connection))
        {
            Reject();
        }

        try
        {
            SkillInvocationSnapshotSchemaV1Validator.Validate(connection, transaction);
            foreach (var (name, sql) in Violations)
            {
                if (Probe(connection, transaction, sql))
                    Reject(name);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or SqliteException
            && exception.Message != InvalidState)
        {
            throw new InvalidOperationException(InvalidState, exception);
        }
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            Validate(connection, transaction);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Probe(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void Reject(string? violation = null) =>
        throw new InvalidOperationException(InvalidState, violation is null ? null : new InvalidOperationException(violation));
}

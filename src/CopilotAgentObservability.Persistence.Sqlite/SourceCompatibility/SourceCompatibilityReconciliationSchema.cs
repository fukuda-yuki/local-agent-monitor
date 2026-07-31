namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SourceCompatibilityReconciliationSchema
{
    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_trace_version_interpretation_supersessions (
                supersession_id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_observation_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                previous_interpretation_revision INTEGER NOT NULL CHECK(previous_interpretation_revision >= 0),
                new_interpretation_revision INTEGER NOT NULL CHECK(new_interpretation_revision = previous_interpretation_revision + 1),
                derived_state TEXT NOT NULL CHECK(derived_state IN ('resolved','missing','unrecognised','conflicting')),
                exact_version TEXT NULL CHECK(exact_version IS NULL OR length(exact_version) BETWEEN 1 AND 128),
                reason TEXT NOT NULL CHECK(reason IN ('decoder_revision','registry_revision')),
                raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0),
                input_evidence_kind TEXT NOT NULL CHECK(input_evidence_kind IN ('payload_sha256','deleted_before_digest_v10')),
                raw_payload_sha256 TEXT NULL CHECK(raw_payload_sha256 IS NULL OR (length(raw_payload_sha256)=64 AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*')),
                resolver_revision TEXT NOT NULL CHECK(length(resolver_revision) BETWEEN 1 AND 128),
                registry_revision TEXT NOT NULL CHECK(length(registry_revision) BETWEEN 1 AND 128),
                projector_version TEXT NOT NULL CHECK(length(projector_version) BETWEEN 1 AND 128),
                created_at TEXT NOT NULL,
                operation_fingerprint TEXT NOT NULL CHECK(length(operation_fingerprint)=64 AND operation_fingerprint NOT GLOB '*[^0-9a-f]*'),
                UNIQUE(source_observation_id,trace_id,new_interpretation_revision),
                FOREIGN KEY(source_observation_id,trace_id)
                    REFERENCES source_trace_version_observations(source_observation_id,trace_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                CHECK(
                    (derived_state='resolved' AND exact_version IS NOT NULL)
                    OR (derived_state IN ('missing','conflicting') AND exact_version IS NULL)
                    OR derived_state='unrecognised'),
                CHECK(
                    (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                    OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL))
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_trace_version_interpretation_heads (
                source_observation_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                current_interpretation_revision INTEGER NOT NULL CHECK(current_interpretation_revision > 0),
                current_supersession_id INTEGER NOT NULL UNIQUE,
                PRIMARY KEY(source_observation_id,trace_id),
                FOREIGN KEY(source_observation_id,trace_id)
                    REFERENCES source_trace_version_observations(source_observation_id,trace_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY(current_supersession_id)
                    REFERENCES source_trace_version_interpretation_supersessions(supersession_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_trace_compatibility_revisions (
                trace_id TEXT PRIMARY KEY CHECK(length(trace_id)=32 AND trace_id NOT GLOB '*[^0-9a-f]*'),
                current_revision INTEGER NOT NULL CHECK(current_revision >= 0),
                current_effective_state TEXT NOT NULL CHECK(current_effective_state IN ('resolved','missing','unrecognised','conflicting')),
                current_exact_version TEXT NULL CHECK(current_exact_version IS NULL OR length(current_exact_version) BETWEEN 1 AND 128),
                updated_at TEXT NOT NULL,
                CHECK(
                    (current_effective_state='resolved' AND current_exact_version IS NOT NULL)
                    OR (current_effective_state IN ('missing','conflicting') AND current_exact_version IS NULL)
                    OR current_effective_state='unrecognised')
            );
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS source_compatibility_reconciliation_receipts (
                operation_key TEXT PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
                request_fingerprint TEXT NOT NULL CHECK(length(request_fingerprint)=64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'),
                source_observation_id INTEGER NOT NULL,
                trace_id TEXT NOT NULL,
                expected_interpretation_revision INTEGER NOT NULL CHECK(expected_interpretation_revision >= 0),
                raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0),
                input_evidence_kind TEXT NOT NULL CHECK(input_evidence_kind IN ('payload_sha256','deleted_before_digest_v10')),
                raw_payload_sha256 TEXT NULL CHECK(raw_payload_sha256 IS NULL OR (length(raw_payload_sha256)=64 AND raw_payload_sha256 NOT GLOB '*[^0-9a-f]*')),
                resolver_revision TEXT NOT NULL CHECK(length(resolver_revision) BETWEEN 1 AND 128),
                registry_revision TEXT NOT NULL CHECK(length(registry_revision) BETWEEN 1 AND 128),
                projector_version TEXT NOT NULL CHECK(length(projector_version) BETWEEN 1 AND 128),
                outcome TEXT NOT NULL CHECK(outcome IN ('changed','no_change','input_unavailable')),
                resulting_supersession_id INTEGER NULL,
                resulting_interpretation_revision INTEGER NOT NULL CHECK(resulting_interpretation_revision >= 0),
                resulting_compatibility_revision INTEGER NULL CHECK(resulting_compatibility_revision IS NULL OR resulting_compatibility_revision >= 0),
                resulting_generation_id INTEGER NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(source_observation_id,trace_id)
                    REFERENCES source_trace_version_observations(source_observation_id,trace_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                FOREIGN KEY(resulting_supersession_id)
                    REFERENCES source_trace_version_interpretation_supersessions(supersession_id)
                    ON UPDATE RESTRICT ON DELETE RESTRICT,
                CHECK(
                    (input_evidence_kind='payload_sha256' AND raw_payload_sha256 IS NOT NULL)
                    OR (input_evidence_kind='deleted_before_digest_v10' AND raw_payload_sha256 IS NULL)),
                CHECK(
                    outcome<>'input_unavailable'
                    OR (resulting_supersession_id IS NULL
                        AND resulting_compatibility_revision IS NULL
                        AND resulting_generation_id IS NULL))
            );
            """);
        foreach (var table in new[]
        {
            "source_trace_version_interpretation_supersessions",
            "source_compatibility_reconciliation_receipts",
        })
        {
            Execute(
                connection,
                transaction,
                $"CREATE TRIGGER IF NOT EXISTS {table}_update_rejected BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;");
            Execute(
                connection,
                transaction,
                $"CREATE TRIGGER IF NOT EXISTS {table}_delete_rejected BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;");
        }
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_trace_version_interpretation_heads_delete_rejected
            BEFORE DELETE ON source_trace_version_interpretation_heads
            BEGIN
                SELECT RAISE(ABORT,'source_compatibility_head_delete_rejected');
            END;
            """);
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS source_trace_version_interpretation_heads_update_guard
            BEFORE UPDATE ON source_trace_version_interpretation_heads
            WHEN NOT EXISTS(
                SELECT 1
                FROM source_trace_version_interpretation_supersessions AS supersession
                WHERE supersession.supersession_id=NEW.current_supersession_id
                  AND supersession.source_observation_id=OLD.source_observation_id
                  AND supersession.trace_id=OLD.trace_id
                  AND supersession.previous_interpretation_revision=OLD.current_interpretation_revision
                  AND supersession.new_interpretation_revision=NEW.current_interpretation_revision
            )
            BEGIN
                SELECT RAISE(ABORT,'source_compatibility_head_transition_invalid');
            END;
            """);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

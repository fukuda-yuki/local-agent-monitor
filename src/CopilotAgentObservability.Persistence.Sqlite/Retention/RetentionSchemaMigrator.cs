using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal static class RetentionSchemaMigrator
{
    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, transaction, "retention_items")
            && (!ColumnExists(connection, transaction, "retention_items", "receipt_version")
                || !ColumnExists(connection, transaction, "retention_items", "ownership_receipt")
                || ColumnExists(connection, transaction, "retention_items", "owner_reference")))
            throw new RetentionMigrationBlockedException();
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_component_versions(component TEXT PRIMARY KEY CHECK(component = 'retention'), version INTEGER NOT NULL CHECK(version = 1));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_store_instances(id INTEGER PRIMARY KEY CHECK(id = 1), store_instance_id TEXT NOT NULL UNIQUE);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_items (
                item_id TEXT PRIMARY KEY, store_instance_id TEXT NOT NULL REFERENCES retention_store_instances(store_instance_id), store_kind TEXT NOT NULL CHECK (store_kind IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory')),
                source_item_id TEXT NOT NULL, receipt_version INTEGER NOT NULL CHECK(receipt_version = 1), ownership_receipt BLOB NOT NULL CHECK(typeof(ownership_receipt) = 'blob' AND length(ownership_receipt) = 32), private_locator TEXT NULL,
                captured_at TEXT NOT NULL, expires_at TEXT NOT NULL, policy_id TEXT NOT NULL, policy_version INTEGER NOT NULL CHECK (policy_version > 0),
                state TEXT NOT NULL CHECK (state IN ('expiring','retained_by_policy','expired_pending_deletion','deletion_queued','deleting','deleted','deletion_failed')), revision INTEGER NOT NULL CHECK (revision > 0), read_denied_at TEXT NULL, queued_at TEXT NULL,
                lease_owner TEXT NULL, lease_expires_at TEXT NULL, lease_generation INTEGER NOT NULL DEFAULT 0,
                attempt_count INTEGER NOT NULL DEFAULT 0, next_retry_at TEXT NULL, error_code TEXT NULL,
                retry_exhausted INTEGER NOT NULL DEFAULT 0 CHECK (retry_exhausted IN (0,1)), deleted_at TEXT NULL, adapter_coverage_version INTEGER NOT NULL CHECK (adapter_coverage_version = 1),
                UNIQUE(store_instance_id, store_kind, source_item_id)
            );
            """);
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_tombstones(item_id TEXT PRIMARY KEY, receipt_at TEXT NOT NULL, deleted_at TEXT NOT NULL, CHECK(receipt_at = deleted_at), FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_capture_journal(item_id TEXT PRIMARY KEY, phase TEXT NOT NULL CHECK (phase IN ('reserved','staging','published_pending_catalog','complete')), durable_cursor TEXT NULL, FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_file_capture_reservations (
                capture_id TEXT PRIMARY KEY CHECK(length(capture_id) = 32 AND capture_id NOT GLOB '*[^0-9a-f]*'),
                store_instance_id TEXT NOT NULL REFERENCES retention_store_instances(store_instance_id),
                store_kind TEXT NOT NULL CHECK(store_kind = 'sensitive_bundle'),
                source_item_id TEXT NOT NULL UNIQUE CHECK(length(source_item_id) = 32 AND source_item_id NOT GLOB '*[^0-9a-f]*'),
                reserved_at TEXT NOT NULL,
                reserved_at_utc_ticks INTEGER NOT NULL,
                policy_id TEXT NOT NULL,
                policy_version INTEGER NOT NULL CHECK(policy_version > 0),
                parent_locator TEXT NOT NULL,
                staging_locator TEXT NOT NULL,
                final_locator TEXT NOT NULL,
                owner_token BLOB NOT NULL CHECK(typeof(owner_token) = 'blob' AND length(owner_token) = 32),
                marker_sha256 BLOB NULL CHECK(marker_sha256 IS NULL OR (typeof(marker_sha256) = 'blob' AND length(marker_sha256) = 32)),
                manifest_sha256 BLOB NULL CHECK(manifest_sha256 IS NULL OR (typeof(manifest_sha256) = 'blob' AND length(manifest_sha256) = 32)),
                phase TEXT NOT NULL CHECK(phase IN ('reserved','staging','published_pending_catalog','complete')),
                durable_cursor INTEGER NULL CHECK(durable_cursor IS NULL OR durable_cursor >= 0),
                planned_member_count INTEGER NOT NULL CHECK(planned_member_count BETWEEN 0 AND 256),
                planned_total_bytes INTEGER NOT NULL CHECK(planned_total_bytes BETWEEN 0 AND 134217728),
                error_code TEXT NULL CHECK(error_code IS NULL OR error_code IN ('retention_capture_incomplete','retention_item_limit_exceeded','retention_ownership_mismatch')),
                updated_at TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_file_capture_members (
                capture_id TEXT NOT NULL REFERENCES retention_file_capture_reservations(capture_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 255),
                relative_path TEXT NOT NULL,
                member_kind TEXT NOT NULL CHECK(member_kind IN ('file','directory','owner_marker')),
                byte_length INTEGER NULL CHECK(byte_length IS NULL OR byte_length BETWEEN 0 AND 134217728),
                sha256 BLOB NULL CHECK(sha256 IS NULL OR (typeof(sha256) = 'blob' AND length(sha256) = 32)),
                deletion_order INTEGER NOT NULL CHECK(deletion_order >= 0),
                PRIMARY KEY(capture_id, ordinal),
                UNIQUE(capture_id, relative_path),
                UNIQUE(capture_id, deletion_order),
                CHECK((member_kind = 'file' AND byte_length IS NOT NULL AND sha256 IS NOT NULL) OR (member_kind IN ('directory','owner_marker') AND byte_length IS NULL AND sha256 IS NULL))
            );
            """);
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_leases(item_id TEXT NOT NULL, lease_kind TEXT NOT NULL CHECK (lease_kind IN ('access','operation','deletion')), owner TEXT NOT NULL, expires_at TEXT NOT NULL, generation INTEGER NOT NULL CHECK (generation > 0), PRIMARY KEY(item_id, lease_kind), FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_delete_journal(item_id TEXT PRIMARY KEY, durable_cursor TEXT NULL, intent_at TEXT NOT NULL, expected_revision INTEGER NOT NULL CHECK(expected_revision > 0), FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_adapter_coverage(store_kind TEXT PRIMARY KEY CHECK (store_kind IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory')), coverage_version INTEGER NOT NULL CHECK (coverage_version = 1));");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_items_expiry ON retention_items(expires_at, item_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_file_capture_reservations_phase_updated ON retention_file_capture_reservations(phase, updated_at, capture_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_file_capture_members_deletion_order ON retention_file_capture_members(capture_id, deletion_order);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_analysis_sdk_directory_reservations (
                capture_id TEXT PRIMARY KEY CHECK(length(capture_id) = 32 AND capture_id NOT GLOB '*[^0-9a-f]*'),
                analysis_run_id INTEGER NOT NULL UNIQUE CHECK(analysis_run_id > 0),
                store_instance_id TEXT NOT NULL REFERENCES retention_store_instances(store_instance_id),
                requested_at TEXT NOT NULL,
                requested_at_utc_ticks INTEGER NOT NULL,
                parent_locator TEXT NOT NULL,
                child_locator TEXT NOT NULL UNIQUE,
                analysis_owner_token_sha256 BLOB NOT NULL CHECK(typeof(analysis_owner_token_sha256) = 'blob' AND length(analysis_owner_token_sha256) = 32),
                owner_token BLOB NOT NULL CHECK(typeof(owner_token) = 'blob' AND length(owner_token) = 32),
                marker_sha256 BLOB NOT NULL CHECK(typeof(marker_sha256) = 'blob' AND length(marker_sha256) = 32),
                phase TEXT NOT NULL CHECK(phase IN ('reserved','active','sealed')),
                error_code TEXT NULL CHECK(error_code IS NULL OR error_code IN ('retention_capture_incomplete','retention_ownership_mismatch')),
                revision INTEGER NOT NULL CHECK(revision > 0),
                updated_at TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_analysis_sdk_directory_reservations_phase_updated ON retention_analysis_sdk_directory_reservations(phase, updated_at, capture_id);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_analysis_sdk_directory_members (
                capture_id TEXT NOT NULL REFERENCES retention_analysis_sdk_directory_reservations(capture_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 255),
                relative_path TEXT NOT NULL,
                member_kind TEXT NOT NULL CHECK(member_kind IN ('file','directory','owner_marker')),
                byte_length INTEGER NULL CHECK(byte_length IS NULL OR byte_length BETWEEN 0 AND 134217728),
                sha256 BLOB NULL CHECK(sha256 IS NULL OR (typeof(sha256) = 'blob' AND length(sha256) = 32)),
                deletion_order INTEGER NOT NULL CHECK(deletion_order >= 0),
                PRIMARY KEY(capture_id, ordinal),
                UNIQUE(capture_id, relative_path),
                UNIQUE(capture_id, deletion_order),
                CHECK((member_kind = 'file' AND byte_length IS NOT NULL AND sha256 IS NOT NULL) OR (member_kind IN ('directory','owner_marker') AND byte_length IS NULL AND sha256 IS NULL))
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_analysis_sdk_directory_members_deletion_order ON retention_analysis_sdk_directory_members(capture_id, deletion_order);");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_legacy_bundle_blockers (root_locator TEXT PRIMARY KEY, classification TEXT NOT NULL CHECK(classification='legacy_bundle_unverifiable'), recorded_at TEXT NOT NULL);");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_legacy_bundle_journal(capture_id TEXT PRIMARY KEY REFERENCES retention_file_capture_reservations(capture_id), root_locator TEXT NOT NULL UNIQUE, legacy_manifest_sha256 BLOB NOT NULL CHECK(typeof(legacy_manifest_sha256)='blob' AND length(legacy_manifest_sha256)=32), legacy_staging_locator TEXT NOT NULL, replacement_temp_locator TEXT NOT NULL, subphase TEXT NOT NULL CHECK(subphase IN ('root_rename_pending','root_renamed','parent_recreated','final_moved','manifest_replaced','marker_written','catalog_completed')));");
        if (!ColumnExists(connection, transaction, "retention_legacy_bundle_journal", "root_locator"))
            Execute(connection, transaction, "ALTER TABLE retention_legacy_bundle_journal ADD COLUMN root_locator TEXT NULL;");
        if (!ColumnExists(connection, transaction, "retention_legacy_bundle_journal", "replacement_temp_locator"))
            Execute(connection, transaction, "ALTER TABLE retention_legacy_bundle_journal ADD COLUMN replacement_temp_locator TEXT NULL;");
        Execute(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS IX_retention_legacy_bundle_journal_root_locator ON retention_legacy_bundle_journal(root_locator) WHERE root_locator IS NOT NULL;");
        if (!ColumnExists(connection, transaction, "retention_file_capture_reservations", "legacy_v1"))
            Execute(connection, transaction, "ALTER TABLE retention_file_capture_reservations ADD COLUMN legacy_v1 INTEGER NOT NULL DEFAULT 0 CHECK(legacy_v1 IN (0,1));");
        if (!ColumnExists(connection, transaction, "retention_items", "deletion_started_at"))
            Execute(connection, transaction, "ALTER TABLE retention_items ADD COLUMN deletion_started_at TEXT NULL;");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_worker_state (id INTEGER PRIMARY KEY CHECK(id=1), last_successful_run_at TEXT NULL, worker_error_code TEXT NULL CHECK(worker_error_code IS NULL OR worker_error_code='retention_adapter_coverage_mismatch'), maintenance_due_at TEXT NULL, maintenance_error_code TEXT NULL CHECK(maintenance_error_code IS NULL OR maintenance_error_code='retention_maintenance_busy'), maintenance_owner TEXT NULL, maintenance_lease_expires_at TEXT NULL, maintenance_generation INTEGER NOT NULL DEFAULT 0 CHECK(maintenance_generation >= 0), CHECK((maintenance_owner IS NULL AND maintenance_lease_expires_at IS NULL) OR (maintenance_owner IS NOT NULL AND maintenance_lease_expires_at IS NOT NULL AND maintenance_generation > 0)));");
        Execute(connection, transaction, "INSERT INTO retention_worker_state(id) VALUES(1) ON CONFLICT(id) DO NOTHING;");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_items_worker_order ON retention_items(state, expires_at, item_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_leases_kind_expiry ON retention_leases(lease_kind, expires_at, item_id);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_mutation_previews (
                preview_id TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                target_kind TEXT NOT NULL CHECK(target_kind IN ('session','item')),
                target_id TEXT NOT NULL,
                operation TEXT NOT NULL CHECK(operation IN ('pin','unpin','delete_now')),
                scope TEXT NOT NULL CHECK(scope IN ('session_items','single_item')),
                preview_json TEXT NOT NULL,
                expected_state_version TEXT NOT NULL,
                target_item_set_digest TEXT NOT NULL,
                preview_digest TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                rejection_code TEXT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_mutation_previews_expiry ON retention_mutation_previews(expires_at, preview_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_mutation_previews_target ON retention_mutation_previews(target_kind, target_id, preview_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_mutation_previews_digest ON retention_mutation_previews(preview_digest, preview_id);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_confirmation_bindings (
                confirmation_id TEXT PRIMARY KEY,
                preview_id TEXT NOT NULL REFERENCES retention_mutation_previews(preview_id),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                token_sha256 BLOB NOT NULL CHECK(typeof(token_sha256) = 'blob' AND length(token_sha256) = 32),
                nonce BLOB NOT NULL CHECK(typeof(nonce) = 'blob' AND length(nonce) = 16),
                target_kind TEXT NOT NULL CHECK(target_kind IN ('session','item')),
                target_id TEXT NOT NULL,
                operation TEXT NOT NULL CHECK(operation IN ('pin','unpin','delete_now')),
                scope TEXT NOT NULL CHECK(scope IN ('session_items','single_item')),
                preview_digest TEXT NOT NULL,
                expected_state_version TEXT NOT NULL,
                target_item_set_digest TEXT NOT NULL,
                active_conflict_snapshot TEXT NOT NULL,
                conflict_version TEXT NOT NULL,
                confirmation_expires_at TEXT NOT NULL,
                workflow_idempotency_key TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                comment_sha256 BLOB NULL CHECK(comment_sha256 IS NULL OR (typeof(comment_sha256) = 'blob' AND length(comment_sha256) = 32)),
                created_at TEXT NOT NULL,
                consumed_at TEXT NULL,
                invalidated_at TEXT NULL,
                operation_id TEXT NULL,
                UNIQUE(nonce)
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_confirmation_bindings_expiry ON retention_confirmation_bindings(confirmation_expires_at, confirmation_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_confirmation_bindings_preview ON retention_confirmation_bindings(preview_id, invalidated_at, consumed_at, confirmation_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_confirmation_bindings_token_hash ON retention_confirmation_bindings(token_sha256);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_mutation_idempotency (
                key_digest BLOB NOT NULL CHECK(typeof(key_digest) = 'blob' AND length(key_digest) = 32),
                step TEXT NOT NULL CHECK(step IN ('preview','confirmation_issue','mutation')),
                request_fingerprint BLOB NOT NULL CHECK(typeof(request_fingerprint) = 'blob' AND length(request_fingerprint) = 32),
                result_json TEXT NOT NULL,
                completion_code TEXT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                PRIMARY KEY(key_digest, step)
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_mutation_idempotency_expiry ON retention_mutation_idempotency(expires_at, key_digest, step);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_operation_receipts (
                operation_id TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                result_code TEXT NOT NULL,
                target_kind TEXT NOT NULL CHECK(target_kind IN ('session','item')),
                target_id TEXT NOT NULL,
                operation TEXT NOT NULL CHECK(operation IN ('pin','unpin','delete_now')),
                scope TEXT NOT NULL CHECK(scope IN ('session_items','single_item')),
                target_item_count INTEGER NOT NULL CHECK(target_item_count >= 0),
                result_json TEXT NOT NULL,
                completion_code TEXT NOT NULL,
                expected_version TEXT NOT NULL,
                result_version TEXT NOT NULL,
                target_item_set_digest TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_operation_receipts_target ON retention_operation_receipts(target_kind, target_id, operation_id);");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS retention_audit_events (
                event_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL,
                event_type TEXT NOT NULL CHECK(event_type = 'retention_mutation'),
                target_kind TEXT NOT NULL CHECK(target_kind IN ('session','item')),
                target_id TEXT NOT NULL,
                session_id TEXT NULL,
                occurred_at TEXT NOT NULL,
                actor_label TEXT NOT NULL CHECK(actor_label = 'local-user'),
                operation TEXT NOT NULL CHECK(operation IN ('pin','unpin','delete_now')),
                reason_code TEXT NOT NULL,
                comment TEXT NULL,
                previous_pin_state TEXT NOT NULL CHECK(previous_pin_state IN ('pinned','unpinned','not_applicable','mixed')),
                new_pin_state TEXT NOT NULL CHECK(new_pin_state IN ('pinned','unpinned','not_applicable','mixed')),
                previous_operation_state TEXT NOT NULL,
                new_operation_state TEXT NOT NULL,
                request_idempotency_key TEXT NOT NULL,
                expected_version TEXT NOT NULL,
                result_version TEXT NOT NULL,
                target_item_set_digest TEXT NOT NULL,
                completion_code TEXT NOT NULL,
                error_code TEXT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_audit_events_target ON retention_audit_events(target_kind, target_id, occurred_at, event_id);");
        Execute(connection, transaction, "INSERT INTO retention_component_versions(component,version) VALUES('retention',1) ON CONFLICT(component) DO NOTHING;");
        Execute(connection, transaction, "INSERT INTO retention_store_instances(id,store_instance_id) VALUES(1,lower(hex(randomblob(16)))) ON CONFLICT(id) DO NOTHING;");
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM retention_component_versions WHERE component='retention';";
            if (Convert.ToInt64(version.ExecuteScalar()) != RetentionV1Constants.CatalogSchemaVersion) throw new RetentionMigrationBlockedException();
        }
    }

    internal static string Wire(RetentionStoreKind kind) => kind switch
    {
        RetentionStoreKind.SessionEventContent => "session_event_content", RetentionStoreKind.RawRecord => "raw_record",
        RetentionStoreKind.AnalysisRunRaw => "analysis_run_raw", RetentionStoreKind.SensitiveBundle => "sensitive_bundle", _ => "analysis_sdk_directory"
    };

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", table); return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) return true;
        return false;
    }
}

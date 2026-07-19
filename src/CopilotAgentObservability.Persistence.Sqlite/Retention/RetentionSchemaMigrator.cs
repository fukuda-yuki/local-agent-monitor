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
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_leases(item_id TEXT NOT NULL, lease_kind TEXT NOT NULL CHECK (lease_kind IN ('access','operation','deletion')), owner TEXT NOT NULL, expires_at TEXT NOT NULL, generation INTEGER NOT NULL CHECK (generation > 0), PRIMARY KEY(item_id, lease_kind), FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_delete_journal(item_id TEXT PRIMARY KEY, durable_cursor TEXT NULL, intent_at TEXT NOT NULL, expected_revision INTEGER NOT NULL CHECK(expected_revision > 0), FOREIGN KEY(item_id) REFERENCES retention_items(item_id));");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_adapter_coverage(store_kind TEXT PRIMARY KEY CHECK (store_kind IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory')), coverage_version INTEGER NOT NULL CHECK (coverage_version = 1));");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_items_expiry ON retention_items(expires_at, item_id);");
        if (!ColumnExists(connection, transaction, "retention_items", "deletion_started_at"))
            Execute(connection, transaction, "ALTER TABLE retention_items ADD COLUMN deletion_started_at TEXT NULL;");
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS retention_worker_state (id INTEGER PRIMARY KEY CHECK(id=1), last_successful_run_at TEXT NULL, worker_error_code TEXT NULL CHECK(worker_error_code IS NULL OR worker_error_code='retention_adapter_coverage_mismatch'), maintenance_due_at TEXT NULL, maintenance_error_code TEXT NULL CHECK(maintenance_error_code IS NULL OR maintenance_error_code='retention_maintenance_busy'), maintenance_owner TEXT NULL, maintenance_lease_expires_at TEXT NULL, maintenance_generation INTEGER NOT NULL DEFAULT 0 CHECK(maintenance_generation >= 0), CHECK((maintenance_owner IS NULL AND maintenance_lease_expires_at IS NULL) OR (maintenance_owner IS NOT NULL AND maintenance_lease_expires_at IS NOT NULL AND maintenance_generation > 0)));");
        Execute(connection, transaction, "INSERT INTO retention_worker_state(id) VALUES(1) ON CONFLICT(id) DO NOTHING;");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_items_worker_order ON retention_items(state, expires_at, item_id);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_retention_leases_kind_expiry ON retention_leases(lease_kind, expires_at, item_id);");
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

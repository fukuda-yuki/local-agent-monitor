using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationPersistenceSchemaTests
{
    private static readonly string[] MutationTables =
    [
        "retention_mutation_previews",
        "retention_confirmation_bindings",
        "retention_mutation_idempotency",
        "retention_operation_receipts",
        "retention_audit_events"
    ];

    private static readonly string[] MutationIndexes =
    [
        "IX_retention_mutation_previews_expiry",
        "IX_retention_mutation_previews_target",
        "IX_retention_mutation_previews_digest",
        "IX_retention_confirmation_bindings_expiry",
        "IX_retention_confirmation_bindings_preview",
        "IX_retention_confirmation_bindings_token_hash",
        "IX_retention_mutation_idempotency_expiry",
        "IX_retention_operation_receipts_target",
        "IX_retention_audit_events_target"
    ];

    [Fact]
    public void CreateSchema_FreshCatalogCreatesMutationTablesAndIndexesWithoutSourceDuplication()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-mutation-schema-{Guid.NewGuid():N}.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();

            Assert.Equal(1L, Scalar(path, "SELECT version FROM retention_component_versions WHERE component='retention';"));
            Assert.All(MutationTables, table => Assert.True(TableExists(path, table), table));
            Assert.All(MutationIndexes, index => Assert.True(IndexExists(path, index), index));
            Assert.DoesNotContain("session_id", Columns(path, "retention_items"), StringComparer.Ordinal);
            Assert.DoesNotContain("pin_state", Columns(path, "retention_items"), StringComparer.Ordinal);
            Assert.DoesNotContain("queue", TableNames(path), StringComparer.OrdinalIgnoreCase);

            var auditColumns = Columns(path, "retention_audit_events");
            Assert.Equal(
                [
                    "event_id", "operation_id", "event_type", "target_kind", "target_id", "session_id",
                    "occurred_at", "actor_label", "operation", "reason_code", "comment", "previous_pin_state",
                    "new_pin_state", "previous_operation_state", "new_operation_state", "request_idempotency_key",
                    "expected_version", "result_version", "target_item_set_digest", "completion_code", "error_code"
                ],
                auditColumns);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void CreateSchema_CommittedCatalogAndPopulatedCatalogPreserveRowsAndTimestampBytesAcrossTwoFreshReopens()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", "retention-catalog-v1.sqlite");
        var path = Path.Combine(Path.GetTempPath(), $"retention-mutation-catalog-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(source, path);
            var store = new RetentionCatalogStore(path);
            store.CreateSchema();
            Assert.Empty(ReadItems(path));

            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(path)).CreateSchema();
            Assert.Empty(ReadItems(path));

            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(path)).CreateSchema();
            Assert.Empty(ReadItems(path));
            Assert.All(MutationTables, table => Assert.True(TableExists(path, table), table));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }

        var populatedSource = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var populatedPath = Path.Combine(Path.GetTempPath(), $"retention-mutation-populated-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(populatedSource, populatedPath);
            var store = new RetentionCatalogStore(populatedPath);
            store.CreateSchema();
            var expected = ReadItems(populatedPath);

            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(populatedPath)).CreateSchema();
            Assert.Equal(expected, ReadItems(populatedPath));

            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(populatedPath)).CreateSchema();
            Assert.Equal(expected, ReadItems(populatedPath));
            Assert.All(MutationTables, table => Assert.True(TableExists(populatedPath, table), table));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(populatedPath);
        }
    }

    [Fact]
    public void CreateSchema_FailureInjectionRollsBackMutationTablesAndRowsWithTheExistingCatalog()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var path = Path.Combine(Path.GetTempPath(), $"retention-mutation-rollback-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(source, path);
            var originalRawCount = Scalar(path, "SELECT COUNT(*) FROM raw_records;");
            var injected = new RetentionCatalogStore(path, static (connection, transaction) =>
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO retention_mutation_previews(preview_id,schema_version,target_kind,target_id,operation,scope,preview_json,expected_state_version,target_item_set_digest,preview_digest,created_at,expires_at,rejection_code) VALUES('rpv1_synthetic',1,'item','synthetic','pin','single_item','{}','v1-synthetic','sha256-synthetic','sha256-preview','2026-07-20T00:00:00.0000000+00:00','2026-07-20T00:05:00.0000000+00:00',NULL);";
                insert.ExecuteNonQuery();

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);";
                delete.ExecuteNonQuery();
            });

            Assert.Throws<RetentionMigrationBlockedException>(injected.CreateSchema);
            Assert.All(MutationTables, table => Assert.False(TableExists(path, table), table));
            Assert.Equal(5L, Scalar(path, "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Equal(originalRawCount, Scalar(path, "SELECT COUNT(*) FROM raw_records;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static IReadOnlyList<CatalogItemRow> ReadItems(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,store_kind,source_item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items ORDER BY item_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<CatalogItemRow>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }

    private static string[] Columns(string path, string table)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static string[] TableNames(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static bool TableExists(string path, string name) => Exists(path, "table", name);
    private static bool IndexExists(string path, string name) => Exists(path, "index", name);

    private static bool Exists(string path, string type, string name)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name);";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed record CatalogItemRow(string ItemId, string StoreKind, string SourceItemId, string CapturedAt, string ExpiresAt, string State, long Revision, string? ReadDeniedAt);
}

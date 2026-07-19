using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionWorkerMigrationQueryPlanTests
{
    private const string RetentionFixtureName = "retention-catalog-v1.sqlite";
    private const string WorkerOrderIndex = "IX_retention_items_worker_order";
    private const string LeaseExpiryIndex = "IX_retention_leases_kind_expiry";

    [Fact]
    public void PreWorkerRetentionV1Fixture_MigratesIdempotentlyWithoutChangingSourceEvidence()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", RetentionFixtureName);
        var manifest = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", "manifest.json");
        var copy = Path.Combine(Path.GetTempPath(), $"retention-worker-migration-{Guid.NewGuid():N}.sqlite");

        Assert.Equal(ManifestFixtureHash(manifest), Hash(fixture));
        Assert.Equal(1L, Scalar<long>(fixture, "SELECT version FROM retention_component_versions WHERE component='retention';"));
        Assert.False(ColumnExists(fixture, "retention_items", "deletion_started_at"));
        Assert.False(TableExists(fixture, "retention_worker_state"));
        Assert.Empty(NamedWorkerIndexes(fixture));

        File.Copy(fixture, copy);
        try
        {
            var fixtureHash = Hash(fixture);
            var sourceEvidence = CaptureNonRetentionEvidence(copy);

            new RetentionCatalogStore(copy).CreateSchema();
            var first = CaptureWorkerMigrationEvidence(copy);
            Assert.Equal(sourceEvidence, CaptureNonRetentionEvidence(copy));
            Assert.Equal(fixtureHash, Hash(fixture));

            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(copy).CreateSchema();
            var second = CaptureWorkerMigrationEvidence(copy);

            Assert.Equal(first, second);
            Assert.Equal(sourceEvidence, CaptureNonRetentionEvidence(copy));
            Assert.Equal(fixtureHash, Hash(fixture));
            Assert.Equal(1L, Scalar<long>(copy, "SELECT version FROM retention_component_versions WHERE component='retention';"));
            Assert.True(ColumnExists(copy, "retention_items", "deletion_started_at"));
            Assert.Equal(1L, Scalar<long>(copy, "SELECT COUNT(*) FROM retention_worker_state WHERE id=1;"));
            Assert.Equal(new[] { WorkerOrderIndex, LeaseExpiryIndex }, NamedWorkerIndexes(copy));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(copy);
        }
    }

    [Fact]
    public void WorkerAndMaintenanceQueries_UseTheirDedicatedV1Indexes()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", RetentionFixtureName);
        var copy = Path.Combine(Path.GetTempPath(), $"retention-worker-query-plan-{Guid.NewGuid():N}.sqlite");
        File.Copy(fixture, copy);

        try
        {
            new RetentionCatalogStore(copy).CreateSchema();

            var workerPlan = Explain(copy, """
                SELECT i.item_id,i.revision,i.state
                FROM retention_items i
                WHERE i.state='deletion_queued'
                   OR (i.state='deleting'
                       AND EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=i.item_id AND j.expected_revision=i.revision)
                       AND NOT EXISTS(SELECT 1 FROM retention_leases l WHERE l.item_id=i.item_id AND l.lease_kind='deletion' AND l.expires_at>$now))
                ORDER BY i.expires_at,i.item_id
                LIMIT $limit;
                """, ("$now", "2026-07-19T00:00:00.0000000+00:00"), ("$limit", 100));
            var maintenancePlan = Explain(copy, """
                SELECT EXISTS(
                    SELECT 1 FROM retention_leases
                    WHERE lease_kind IN ('access','operation','deletion') AND expires_at>$now);
                """, ("$now", "2026-07-19T00:00:00.0000000+00:00"));

            Assert.Contains(workerPlan, detail => detail.Contains(WorkerOrderIndex, StringComparison.Ordinal));
            Assert.Contains(maintenancePlan, detail => detail.Contains(LeaseExpiryIndex, StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(copy);
        }
    }

    private static WorkerMigrationEvidence CaptureWorkerMigrationEvidence(string path) => new(
        Scalar<long>(path, "SELECT version FROM retention_component_versions WHERE component='retention';"),
        ColumnExists(path, "retention_items", "deletion_started_at"),
        Scalar<long>(path, "SELECT COUNT(*) FROM retention_worker_state WHERE id=1;"),
        string.Join("|", NamedWorkerIndexes(path)),
        Scalar<string>(path, "SELECT store_instance_id FROM retention_store_instances WHERE id=1;"));

    private static IReadOnlyList<string> Explain(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        return details;
    }

    private static IReadOnlyList<string> NamedWorkerIndexes(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN ($worker,$lease) ORDER BY name;";
        command.Parameters.AddWithValue("$worker", WorkerOrderIndex);
        command.Parameters.AddWithValue("$lease", LeaseExpiryIndex);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static string CaptureNonRetentionEvidence(string path)
    {
        using var connection = Open(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var objects = connection.CreateCommand();
        objects.CommandText = "SELECT type,name,tbl_name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name NOT LIKE 'retention_%' AND tbl_name NOT LIKE 'retention_%' ORDER BY type,name;";
        using var reader = objects.ExecuteReader();
        while (reader.Read())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++) Append(digest, reader.GetString(ordinal));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ManifestFixtureHash(string manifestPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(entry => entry.GetProperty("file").GetString() == RetentionFixtureName)
            .GetProperty("sha256").GetString()!;
    }

    private static bool ColumnExists(string path, string table, string column)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1) == column) return true;
        return false;
    }

    private static bool TableExists(string path, string table) => Scalar<long>(path, "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);", ("$name", table)) == 1;
    private static T Scalar<T>(string path, string sql, params (string Name, object Value)[] parameters) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T)); }
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open(); return connection; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Append(IncrementalHash digest, string value) { var bytes = Encoding.UTF8.GetBytes(value); digest.AppendData(BitConverter.GetBytes(bytes.Length)); digest.AppendData(bytes); }
    private static void DeleteDatabaseFiles(string path) { foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate); }

    private sealed record WorkerMigrationEvidence(long Version, bool HasDeletionStartedAt, long WorkerStateCount, string WorkerIndexes, string StoreInstanceId);
}

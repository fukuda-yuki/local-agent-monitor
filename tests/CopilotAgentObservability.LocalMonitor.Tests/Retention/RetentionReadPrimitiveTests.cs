using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionReadPrimitiveTests
{
    [Fact]
    public void AdoptExistingCatalogV1_RejectsAbsentDatabaseWithoutCreatingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-adopt-{Guid.NewGuid():N}.sqlite");

        var exception = Assert.Throws<RetentionCatalogUnavailableException>(() => RetentionCatalogContext.AdoptExistingCatalogV1(path));

        Assert.Equal("retention_catalog_unavailable", exception.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ReadAsync_GrantsFullyMaterializedValueOnlyAfterSelectorUsesOwnershipCapability()
    {
        var path = CopyFixture();
        try
        {
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero)));
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, item.CapturedAt, item.Revision),
                (connection, transaction, grant, cancellationToken) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        SELECT payload_json FROM raw_records
                        WHERE id=$id AND retention_owner_token=$retention_read_source_token
                        AND EXISTS (SELECT 1 FROM retention_items WHERE item_id=$retention_read_item_id AND revision=$retention_read_revision)
                        AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
                        """;
                    command.Parameters.AddWithValue("$id", long.Parse(key.SourceItemId));
                    grant.BindSelectorCapability(command);
                    return ValueTask.FromResult<string?>(command.ExecuteScalar() as string);
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease);
            Assert.NotEmpty(lease.Value);
            Assert.NotNull(lease.RevisionFence);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadAsync_StaleRevisionReturnsDeniedWithoutExposingValue()
    {
        var path = CopyFixture();
        try
        {
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero)));
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, item.CapturedAt, item.Revision - 1),
                static (_, _, _, _) => ValueTask.FromResult<string?>("must-not-leak"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
        }
        finally { Delete(path); }
    }

    private static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var target = Path.Combine(Path.GetTempPath(), $"retention-read-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, target);
        return target;
    }

    private static T Scalar<T>(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}

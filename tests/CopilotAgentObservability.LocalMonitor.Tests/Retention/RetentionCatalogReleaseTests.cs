using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCatalogReleaseTests
{
    [Theory]
    [InlineData("item")]
    [InlineData("kind")]
    [InlineData("owner")]
    [InlineData("generation")]
    public async Task DirectLease_DisposeLeavesLeaseWhenAnyPersistedTupleFieldNoLongerMatches(string field)
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var (lease, admitted) = await AcquireAccessLeaseAsync(path, now);
            var changed = admitted;

            switch (field)
            {
                case "item":
                    var changedItemId = Guid.NewGuid().ToString("N");
                    CloneItem(path, admitted.ItemId, changedItemId);
                    Assert.Equal(
                        1,
                        Execute(
                            path,
                            "UPDATE retention_leases SET item_id=$changed WHERE item_id=$item AND lease_kind=$kind AND owner=$owner AND generation=$generation;",
                            ("$changed", changedItemId),
                            ("$item", admitted.ItemId),
                            ("$kind", admitted.Kind),
                            ("$owner", admitted.Owner),
                            ("$generation", admitted.Generation)));
                    changed = admitted with { ItemId = changedItemId };
                    break;
                case "kind":
                    const string changedKind = "operation";
                    Assert.Equal(
                        1,
                        Execute(
                            path,
                            "UPDATE retention_leases SET lease_kind=$changed WHERE item_id=$item AND lease_kind=$kind AND owner=$owner AND generation=$generation;",
                            ("$changed", changedKind),
                            ("$item", admitted.ItemId),
                            ("$kind", admitted.Kind),
                            ("$owner", admitted.Owner),
                            ("$generation", admitted.Generation)));
                    changed = admitted with { Kind = changedKind };
                    break;
                case "owner":
                    var changedOwner = admitted.Owner + "-changed";
                    Assert.Equal(
                        1,
                        Execute(
                            path,
                            "UPDATE retention_leases SET owner=$changed WHERE item_id=$item AND lease_kind=$kind AND owner=$owner AND generation=$generation;",
                            ("$changed", changedOwner),
                            ("$item", admitted.ItemId),
                            ("$kind", admitted.Kind),
                            ("$owner", admitted.Owner),
                            ("$generation", admitted.Generation)));
                    changed = admitted with { Owner = changedOwner };
                    break;
                case "generation":
                    var changedGeneration = admitted.Generation + 1;
                    Assert.Equal(
                        1,
                        Execute(
                            path,
                            "UPDATE retention_leases SET generation=$changed WHERE item_id=$item AND lease_kind=$kind AND owner=$owner AND generation=$generation;",
                            ("$changed", changedGeneration),
                            ("$item", admitted.ItemId),
                            ("$kind", admitted.Kind),
                            ("$owner", admitted.Owner),
                            ("$generation", admitted.Generation)));
                    changed = admitted with { Generation = changedGeneration };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }

            lease.Dispose();

            Assert.Equal([changed], ReadLeases(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task DirectLease_DisposeRemovesOnlyExactTupleAndPreservesCollateralLeases()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var (lease, admitted) = await AcquireAccessLeaseAsync(path, now);
            var itemOnlyMismatch = admitted with { ItemId = Guid.NewGuid().ToString("N") };
            var kindOnlyMismatch = admitted with { Kind = "operation" };
            var ownerMismatch = admitted with
            {
                ItemId = Guid.NewGuid().ToString("N"),
                Owner = admitted.Owner + "-sentinel",
            };
            var generationMismatch = admitted with
            {
                ItemId = Guid.NewGuid().ToString("N"),
                Generation = admitted.Generation + 1,
            };
            var sentinels = new[]
            {
                itemOnlyMismatch,
                kindOnlyMismatch,
                ownerMismatch,
                generationMismatch,
            };

            foreach (var sentinel in sentinels.Where(sentinel => sentinel.ItemId != admitted.ItemId))
                CloneItem(path, admitted.ItemId, sentinel.ItemId);
            foreach (var sentinel in sentinels)
                InsertLease(path, sentinel);

            lease.Dispose();

            Assert.Equal(
                sentinels.OrderBy(static leaseRow => leaseRow.ItemId).ThenBy(static leaseRow => leaseRow.Kind),
                ReadLeases(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task DirectLease_SecondDisposeAfterSuccessfulReleaseAndDatabaseMoveIsNoOp()
    {
        var path = CopyFixture();
        var relocatedPath = path + ".relocated";
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawRecordId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var lease = Assert.IsType<RetentionReadLeaseHandle>(
                await store.TryAcquireAsync(
                    key,
                    item.Revision,
                    RetentionLeaseKind.Access,
                    now,
                    CancellationToken.None));

            lease.Dispose();
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            SqliteConnection.ClearAllPools();
            MoveDatabase(path, relocatedPath);
            Assert.False(File.Exists(path));

            var secondDisposeFailure = Record.Exception(lease.Dispose);

            Assert.Null(secondDisposeFailure);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Delete(path);
            Delete(relocatedPath);
        }
    }

    [Fact]
    public async Task DirectLease_DisposeAfterClosedDatabaseMovesDoesNotCreatePhantomDatabase()
    {
        var path = CopyFixture();
        var relocatedPath = path + ".relocated";
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawRecordId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var lease = Assert.IsType<RetentionReadLeaseHandle>(
                await store.TryAcquireAsync(
                    key,
                    item.Revision,
                    RetentionLeaseKind.Access,
                    now,
                    CancellationToken.None));

            MoveDatabase(path, relocatedPath);
            Assert.False(File.Exists(path));

            var releaseFailure = Record.Exception(lease.Dispose);

            Assert.False(
                File.Exists(path),
                $"Lease release created a phantom catalog; failure={releaseFailure?.GetType().Name ?? "none"}.");
            Assert.IsType<SqliteException>(releaseFailure);
        }
        finally
        {
            Delete(path);
            Delete(relocatedPath);
        }
    }

    private static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
        var target = Path.Combine(Path.GetTempPath(), $"retention-release-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, target);
        return target;
    }

    private static async Task<(RetentionReadLeaseHandle Lease, LeaseTuple Tuple)> AcquireAccessLeaseAsync(
        string path,
        DateTimeOffset now)
    {
        var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
        store.CreateSchema();
        var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
        var key = new RetentionOwnershipKey(
            store.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            rawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
        var lease = Assert.IsType<RetentionReadLeaseHandle>(
            await store.TryAcquireAsync(
                key,
                item.Revision,
                RetentionLeaseKind.Access,
                now,
                CancellationToken.None));
        return (lease, Assert.Single(ReadLeases(path)));
    }

    private static void CloneItem(string path, string sourceItemId, string clonedItemId) =>
        Assert.Equal(
            1,
            Execute(
                path,
                """
                INSERT INTO retention_items(
                    item_id,
                    store_instance_id,
                    store_kind,
                    source_item_id,
                    receipt_version,
                    ownership_receipt,
                    private_locator,
                    captured_at,
                    expires_at,
                    policy_id,
                    policy_version,
                    state,
                    revision,
                    adapter_coverage_version)
                SELECT
                    $cloned_item_id,
                    store_instance_id,
                    store_kind,
                    $cloned_item_id,
                    receipt_version,
                    ownership_receipt,
                    private_locator,
                    captured_at,
                    expires_at,
                    policy_id,
                    policy_version,
                    state,
                    revision,
                    adapter_coverage_version
                FROM retention_items
                WHERE item_id=$source_item_id;
                """,
                ("$cloned_item_id", clonedItemId),
                ("$source_item_id", sourceItemId)));

    private static void InsertLease(string path, LeaseTuple lease) =>
        Assert.Equal(
            1,
            Execute(
                path,
                "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,$kind,$owner,$expiry,$generation);",
                ("$item", lease.ItemId),
                ("$kind", lease.Kind),
                ("$owner", lease.Owner),
                ("$expiry", lease.ExpiresAt),
                ("$generation", lease.Generation)));

    private static IReadOnlyList<LeaseTuple> ReadLeases(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,lease_kind,owner,expires_at,generation FROM retention_leases ORDER BY item_id,lease_kind;";
        using var reader = command.ExecuteReader();
        var leases = new List<LeaseTuple>();
        while (reader.Read())
            leases.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        return leases;
    }

    private static void MoveDatabase(string sourcePath, string destinationPath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = sourcePath + suffix;
            if (File.Exists(source))
                File.Move(source, destinationPath + suffix);
        }
    }

    private static T Scalar<T>(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static int Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteNonQuery();
    }

    private static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed record LeaseTuple(string ItemId, string Kind, string Owner, string ExpiresAt, long Generation);
}

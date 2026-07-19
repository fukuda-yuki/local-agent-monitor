using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionReadPrimitiveTests
{
    [Fact]
    public async Task RawStore_ListRecordsAsync_GrantsOrderedMaterializedRecordsUnderOneCompositeLease()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));

            var result = await store.ListRecordsAsync(RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(result.Lease);
            Assert.Equal(ReadIds(path), lease.Value.Select(record => record.Id!.Value));
            Assert.All(lease.Value, record => Assert.NotEmpty(record.PayloadJson));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_ListRecordsAsync_ReturnsBusyWithoutACompositeLeaseWhenAnyCandidateIsDeletionLeased()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var id = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, id.ToString());
            InsertDeletionLease(path, key, now);
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));

            var result = await store.ListRecordsAsync(RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Busy, result.Disposition);
            Assert.Null(result.Lease);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_ListRecordsAsync_ReturnsDeniedWithoutRecordsAfterExpiry()
    {
        var path = CopyFixture();
        try
        {
            var capturedAt = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(capturedAt));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(capturedAt.AddDays(91)));

            var result = await store.ListRecordsAsync(RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_ListRecordsAsync_DisposeReleasesEveryCompositeGeneration()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));
            var result = await store.ListRecordsAsync(RetentionReadKind.Operation, CancellationToken.None);
            var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(result.Lease);

            await lease.DisposeAsync();

            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_GetRawRecordByIdAsync_RejectsReplacedOwnershipReceiptWithoutAValue()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var id = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            Execute(path, "UPDATE retention_items SET ownership_receipt = randomblob(32) WHERE store_kind = 'raw_record' AND source_item_id = $id;", ("$id", id));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));

            var result = await store.GetRawRecordByIdAsync(id, RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_ReadRecordByIdAsync_GrantsMaterializedRawRecordUnderAnAccessLease()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));
            var id = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");

            var result = await store.GetRawRecordByIdAsync(id, RetentionReadKind.Access, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(result.Lease);
            Assert.Equal(id, lease.Value.Id);
            Assert.NotEmpty(lease.Value.PayloadJson);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RawStore_ReadRawRecordsAsync_GrantsEveryRequestedRecordUnderOneCompositeLease()
    {
        var path = CopyFixture();
        try
        {
            var now = ReadCapturedAt(path);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, new MutableTimeProvider(now));
            var store = new RawTelemetryStore(path, context, new MutableTimeProvider(now));
            var ids = ReadIds(path);

            var result = await store.ReadRawRecordsAsync(ids, RetentionReadKind.Operation, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(result.Lease);
            Assert.Equal(ids, lease.Value.Select(record => record.Id!.Value));
        }
        finally { Delete(path); }
    }

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

    private static long[] ReadIds(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM raw_records ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    private static DateTimeOffset ReadCapturedAt(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT received_at FROM raw_records ORDER BY id LIMIT 1;";
        return DateTimeOffset.Parse((string)command.ExecuteScalar()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static void InsertDeletionLease(string path, RetentionOwnershipKey key, DateTimeOffset now)
    {
        Execute(
            path,
            """
            INSERT INTO retention_leases(item_id, lease_kind, owner, expires_at, generation)
            SELECT item_id, 'deletion', 'test-deletion-lease', $expires_at, 1
            FROM retention_items
            WHERE store_instance_id = $store_instance_id AND store_kind = 'raw_record' AND source_item_id = $source_item_id;
            """,
            ("$expires_at", now.AddMinutes(1).ToString("O")),
            ("$store_instance_id", key.StoreInstanceId),
            ("$source_item_id", key.SourceItemId));
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}

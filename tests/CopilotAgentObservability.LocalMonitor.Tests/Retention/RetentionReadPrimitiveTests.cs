using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionReadPrimitiveTests
{
    private const string CompleteAdmissionSelectorSql = """
        SELECT r.payload
        FROM retention_items i
        JOIN raw_records r
          ON r.id=CAST(i.source_item_id AS INTEGER)
         AND r.retention_owner_token=$retention_read_source_token
        JOIN retention_leases l
          ON i.item_id=$retention_read_item_id
         AND i.revision=$retention_read_revision
         AND l.item_id=i.item_id
         AND l.lease_kind=$retention_read_lease_kind
         AND l.owner=$retention_read_lease_owner
         AND l.generation=$retention_read_lease_generation
         AND l.expires_at=$retention_read_lease_expires_at;
        """;

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
                        AND EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$retention_read_item_id AND lease_kind=$retention_read_lease_kind AND owner=$retention_read_lease_owner AND generation=$retention_read_lease_generation AND expires_at=$retention_read_lease_expires_at);
                        """;
                    command.Parameters.AddWithValue("$id", long.Parse(key.SourceItemId));
                    grant.BindAdmissionSelectorCapability(command);
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
    public async Task ReadAsync_SelectorCannotMutateGrantSourceTokenThroughBoundParameter()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawRecordId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, now, item.Revision),
                (connection, transaction, grant, _) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        SELECT r.payload_json
                        FROM raw_records r
                        JOIN retention_items i ON i.item_id=$retention_read_item_id AND i.revision=$retention_read_revision
                        JOIN retention_leases l ON l.item_id=i.item_id AND l.lease_kind=$retention_read_lease_kind
                          AND l.owner=$retention_read_lease_owner AND l.generation=$retention_read_lease_generation
                          AND l.expires_at=$retention_read_lease_expires_at
                        WHERE r.id=$id AND r.retention_owner_token=$retention_read_source_token;
                        """;
                    command.Parameters.AddWithValue("$id", rawRecordId);
                    grant.BindAdmissionSelectorCapability(command);
                    var value = command.ExecuteScalar() as string;
                    var parameterToken = Assert.IsType<byte[]>(command.Parameters["$retention_read_source_token"].Value);
                    for (var index = 0; index < parameterToken.Length; index++)
                        parameterToken[index] ^= byte.MaxValue;
                    return ValueTask.FromResult(value);
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease);
            try
            {
                Assert.True(ConsumeOperationGrant(path, lease.Grant, rawRecordId, now));
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RetentionReadGrant_ConstructorSnapshotsSourceTokenBeforeCallerMutation()
    {
        var sourceToken = new byte[32];
        var grant = new RetentionReadGrant(
            new RetentionOwnershipKey("store", RetentionStoreKind.RawRecord, "1"),
            "item-a",
            7,
            RetentionLeaseKind.Operation,
            "owner-1",
            11,
            new DateTimeOffset(2026, 8, 1, 0, 2, 0, TimeSpan.Zero),
            sourceToken);
        Array.Fill(sourceToken, byte.MaxValue);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE raw_records(id INTEGER PRIMARY KEY, retention_owner_token BLOB NOT NULL, payload TEXT NOT NULL);");
        Execute(connection, "CREATE TABLE retention_items(item_id TEXT PRIMARY KEY, source_item_id TEXT NOT NULL, revision INTEGER NOT NULL);");
        Execute(connection, "CREATE TABLE retention_leases(item_id TEXT NOT NULL, lease_kind TEXT NOT NULL, owner TEXT NOT NULL, generation INTEGER NOT NULL, expires_at TEXT NOT NULL);");
        Execute(connection, "INSERT INTO raw_records VALUES(1, zeroblob(32), 'admitted');");
        Execute(connection, "INSERT INTO retention_items VALUES('item-a', '1', 7);");
        Execute(connection, "INSERT INTO retention_leases VALUES('item-a', 'operation', 'owner-1', 11, '2026-08-01T00:02:00.0000000+00:00');");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql;

        grant.BindAdmissionSelectorCapability(command);

        Assert.Equal("admitted", command.ExecuteScalar());
        Assert.All(
            Assert.IsType<byte[]>(command.Parameters["$retention_read_source_token"].Value),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void RetentionReadGrant_AdmittedExpiryStaysImmutableWhilePublicationAdvancesPublishedExpiry()
    {
        var initialExpiry = new DateTimeOffset(2026, 8, 1, 0, 1, 0, TimeSpan.Zero);
        var renewedExpiry = initialExpiry.AddMinutes(2);
        var grant = new RetentionReadGrant(
            new RetentionOwnershipKey("store", RetentionStoreKind.RawRecord, "7"),
            "item",
            7,
            RetentionLeaseKind.Access,
            "owner",
            11,
            initialExpiry,
            new byte[32]);

        using (var publication = grant.EnterLeasePublication())
        {
            Assert.Equal(initialExpiry, publication.LeaseExpiresAt);
            publication.AdvanceExpiry(renewedExpiry);
        }

        Assert.Equal(initialExpiry, grant.LeaseExpiresAt);
        using (var republished = grant.EnterLeasePublication())
        {
            Assert.Equal(renewedExpiry, republished.LeaseExpiresAt);
        }

        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql;
        grant.BindAdmissionSelectorCapability(command);
        Assert.Equal(
            renewedExpiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Assert.IsType<string>(command.Parameters["$retention_read_lease_expires_at"].Value));
    }

    [Theory]
    [InlineData("$retention_read_source_token")]
    [InlineData("$retention_read_item_id")]
    [InlineData("$retention_read_revision")]
    [InlineData("$retention_read_lease_kind")]
    [InlineData("$retention_read_lease_owner")]
    [InlineData("$retention_read_lease_generation")]
    [InlineData("$retention_read_lease_expires_at")]
    public void RetentionReadGrant_AdmissionSelectorRejectsEachIncompleteCapability(string omittedParameter)
    {
        var grant = CreateGrant("item-a", "1", 1);
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql.Replace(
            omittedParameter,
            $"NULL /* {omittedParameter} is intentionally omitted */",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => grant.BindAdmissionSelectorCapability(command));
    }

    [Fact]
    public void RetentionReadGrant_AdmissionSelectorRejectsCapabilityEmbeddedInPrefixedIdentifier()
    {
        var grant = CreateGrant("item-a", "1", 1);
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql.Replace(
            "$retention_read_item_id",
            "abc$retention_read_item_id",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => grant.BindAdmissionSelectorCapability(command));
    }

    [Fact]
    public void RetentionReadGrant_AdmissionSelectorRejectsAllCapabilitiesEmbeddedInDollarIdentifiers()
    {
        var grant = CreateGrant("item-a", "1", 0);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE raw_records(id INTEGER PRIMARY KEY, retention_owner_token BLOB NOT NULL, payload TEXT NOT NULL);");
        Execute(connection, "CREATE TABLE retention_items(item_id TEXT PRIMARY KEY, source_item_id TEXT NOT NULL, revision INTEGER NOT NULL);");
        Execute(connection, "CREATE TABLE retention_leases(item_id TEXT NOT NULL, lease_kind TEXT NOT NULL, owner TEXT NOT NULL, generation INTEGER NOT NULL, expires_at TEXT NOT NULL);");
        Execute(connection, "INSERT INTO raw_records VALUES(1, zeroblob(32), 'admitted');");
        Execute(connection, "INSERT INTO retention_items VALUES('item-a', '1', 7);");
        Execute(connection, "INSERT INTO retention_leases VALUES('item-a', 'operation', 'owner-1', 11, '2026-08-01T00:02:00.0000000+00:00');");
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH capability_aliases AS (
                SELECT zeroblob(32) AS x$$retention_read_source_token,
                       'item-a' AS x$$retention_read_item_id,
                       7 AS x$$retention_read_revision,
                       'operation' AS x$$retention_read_lease_kind,
                       'owner-1' AS x$$retention_read_lease_owner,
                       11 AS x$$retention_read_lease_generation,
                       '2026-08-01T00:02:00.0000000+00:00' AS x$$retention_read_lease_expires_at
            )
            SELECT r.payload
            FROM capability_aliases a
            JOIN retention_items i ON i.item_id=a.x$$retention_read_item_id
              AND i.revision=a.x$$retention_read_revision
            JOIN raw_records r ON r.id=CAST(i.source_item_id AS INTEGER)
              AND r.retention_owner_token=a.x$$retention_read_source_token
            JOIN retention_leases l ON l.item_id=i.item_id
              AND l.lease_kind=a.x$$retention_read_lease_kind
              AND l.owner=a.x$$retention_read_lease_owner
              AND l.generation=a.x$$retention_read_lease_generation
              AND l.expires_at=a.x$$retention_read_lease_expires_at;
            """;

        Assert.Equal("admitted", command.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => grant.BindAdmissionSelectorCapability(command));
    }

    [Fact]
    public void RetentionReadGrant_AdmissionSelectorRejectsCapabilityInsideCrLfLineComment()
    {
        var grant = CreateGrant("item-a", "1", 1);
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql.Replace(
            "$retention_read_item_id",
            "NULL -- intentionally omitted\r$retention_read_item_id\n",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => grant.BindAdmissionSelectorCapability(command));
    }

    [Fact]
    public void RetentionReadGrant_AdmissionSelectorRejectsCapabilitiesInsideOneTclVariableToken()
    {
        const string decoyVariable = "$cap::scope($retention_read_source_token,$retention_read_item_id,$retention_read_revision,$retention_read_lease_kind,$retention_read_lease_owner,$retention_read_lease_generation,$retention_read_lease_expires_at)";
        var grant = CreateGrant("item-a", "1", 0);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE raw_records(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);");
        Execute(connection, "INSERT INTO raw_records VALUES(1, 'admitted');");
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM raw_records WHERE id=1 AND {decoyVariable}=1;";
        command.Parameters.AddWithValue(decoyVariable, 1);

        Assert.Equal("admitted", command.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => grant.BindAdmissionSelectorCapability(command));
    }

    [Fact]
    public void RetentionReadGrant_AdmissionSelectorBindsCompleteCapabilityForMaterialization()
    {
        var grant = CreateGrant("item-a", "1", 0);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE raw_records(id INTEGER PRIMARY KEY, retention_owner_token BLOB NOT NULL, payload TEXT NOT NULL);");
        Execute(connection, "CREATE TABLE retention_items(item_id TEXT PRIMARY KEY, source_item_id TEXT NOT NULL, revision INTEGER NOT NULL);");
        Execute(connection, "CREATE TABLE retention_leases(item_id TEXT NOT NULL, lease_kind TEXT NOT NULL, owner TEXT NOT NULL, generation INTEGER NOT NULL, expires_at TEXT NOT NULL);");
        Execute(connection, "INSERT INTO raw_records VALUES(1, zeroblob(32), 'admitted');");
        Execute(connection, "INSERT INTO retention_items VALUES('item-a', '1', 7);");
        Execute(connection, "INSERT INTO retention_leases VALUES('item-a', 'operation', 'owner-1', 11, '2026-08-01T00:02:00.0000000+00:00');");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql;

        grant.BindAdmissionSelectorCapability(command);

        Assert.Equal("admitted", command.ExecuteScalar());
    }

    [Fact]
    public void RetentionGrantPublicationSet_HostileListIsSnapshottedOnceAndEveryAcquiredPublicationIsReleased()
    {
        var first = CreateGrant("item-a", "1", 1);
        var second = CreateGrant("item-b", "2", 2);
        var members = new SingleEnumerationReadOnlyList<RetentionGrantPublicationMember>(
        [
            new(first, 10),
            new(second, 20),
        ]);
        using var invocationCompleted = new ManualResetEventSlim();
        using var allowWorkerExit = new ManualResetEventSlim();
        Exception? invocationFailure = null;
        var publicationCount = 0;
        var storedMembersMatch = false;
        var worker = new Thread(() =>
        {
            try
            {
                using var publications = RetentionGrantPublicationSet.EnterInOrder(members);
                publicationCount = publications.Count;
                storedMembersMatch = publications.IsForGrant(0, first) && publications.IsForGrant(1, second);
            }
            catch (Exception exception)
            {
                invocationFailure = exception;
            }
            finally
            {
                invocationCompleted.Set();
                allowWorkerExit.Wait();
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();
        Assert.True(invocationCompleted.Wait(TimeSpan.FromSeconds(5)));
        var firstPublicationWasReleased = TryBindGrant(first);
        var secondPublicationWasReleased = TryBindGrant(second);
        allowWorkerExit.Set();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.True(
            invocationFailure is null
            && publicationCount == 2
            && storedMembersMatch
            && firstPublicationWasReleased
            && secondPublicationWasReleased
            && members.EnumerationCount == 1,
            $"failure={invocationFailure?.GetType().Name ?? "none"}; count={publicationCount}; stored={storedMembersMatch}; released={firstPublicationWasReleased},{secondPublicationWasReleased}; enumerations={members.EnumerationCount}");
    }

    [Fact]
    public void RetentionGrantPublicationSet_DisposeReleasesFrontierOrdinalsInReverseOrder()
    {
        var releasedOrdinals = new List<long>();
        var publications = RetentionGrantPublicationSet.EnterInOrder(
        [
            new(CreateGrant("item-a", "1", 1), 10),
            new(CreateGrant("item-b", "2", 2), 20),
            new(CreateGrant("item-c", "3", 3), 30),
        ],
            releasedOrdinals.Add);

        publications.Dispose();
        publications.Dispose();

        Assert.Equal([30, 20, 10], releasedOrdinals);
    }

    [Fact]
    public void RetentionGrantPublicationSet_PartialAcquisitionFailureUnwindsAcquiredOrdinalsInReverseOrder()
    {
        var first = CreateGrant("item-a", "1", 1);
        var second = CreateGrant("item-b", "2", 2);
        var blocked = CreateGrant("item-c", "3", 3);
        var releasedOrdinals = new List<long>();
        Exception? acquisitionFailure = null;
        var acquisitionUnexpectedlyCompleted = false;
        var blockedPublication = blocked.EnterLeasePublication();
        var worker = new Thread(() =>
        {
            try
            {
                using var publications = RetentionGrantPublicationSet.EnterInOrder(
                [
                    new(first, 10),
                    new(second, 20),
                    new(blocked, 30),
                ],
                    releasedOrdinals.Add);
                acquisitionUnexpectedlyCompleted = true;
            }
            catch (Exception exception)
            {
                acquisitionFailure = exception;
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            worker.Start();
            Assert.True(
                SpinWait.SpinUntil(
                    () => !TryBindGrant(first) && !TryBindGrant(second),
                    TimeSpan.FromSeconds(5)),
                "The worker did not acquire the first two publication scopes before the timeout.");
            worker.Interrupt();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            blockedPublication.Dispose();
            if (worker.IsAlive)
            {
                worker.Interrupt();
                worker.Join(TimeSpan.FromSeconds(5));
            }
        }

        Assert.False(acquisitionUnexpectedlyCompleted);
        Assert.IsType<ThreadInterruptedException>(acquisitionFailure);
        Assert.Equal([20, 10], releasedOrdinals);
        Assert.True(TryBindGrant(first));
        Assert.True(TryBindGrant(second));
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

    [Fact]
    public async Task ReadAsync_SelectorNullAfterLeaseAcquisitionReleasesLeaseBeforeDenying()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, item.CapturedAt, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>(null),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadAsync_ExpiryAfterSelectorMaterializationReleasesLeaseBeforeDeletionCanAcquire()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, now, item.Revision),
                (_, _, _, _) =>
                {
                    time.Advance(item.ExpiresAt - now);
                    return ValueTask.FromResult<string?>("materialized-before-expiry-check");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            var queued = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Execute(path, "UPDATE retention_items SET state='deletion_queued', revision=revision+1 WHERE item_id=$item_id;", ("$item_id", queued.ItemId));
            var deletionQueued = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            using var deletion = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, deletionQueued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(ReadPrimitivePath.Single, false)]
    [InlineData(ReadPrimitivePath.Single, true)]
    [InlineData(ReadPrimitivePath.FixedBatch, false)]
    [InlineData(ReadPrimitivePath.FixedBatch, true)]
    public async Task ReadAdmission_AfterImmediateWriterWaitUsesPostBeginTimeForTheFullLease(
        ReadPrimitivePath primitivePath,
        bool pinned)
    {
        var path = CopyFixture();
        try
        {
            if (primitivePath == ReadPrimitivePath.FixedBatch)
                InsertLegacyRawRows(path, 601, 602);
            var time = new MutableTimeProvider(ReadCapturedAt(path));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var sourceIds = primitivePath == ReadPrimitivePath.Single ? [ReadIds(path)[0]] : new long[] { 601, 602 };
            var keys = sourceIds
                .Select(sourceId => new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sourceId.ToString()))
                .ToArray();
            if (pinned)
            {
                foreach (var key in keys)
                    PinItem(path, key);
            }
            var items = keys.Select(key => Assert.IsType<RetentionCatalogItem>(store.Find(key))).ToArray();
            Assert.All(items, item => Assert.Equal(items[0].ExpiresAt, item.ExpiresAt));
            var requestAt = pinned ? items[0].ExpiresAt.AddDays(1) : items[0].ExpiresAt.AddMinutes(-10);
            time.Advance(requestAt - time.GetUtcNow());
            var requests = keys
                .Select((key, index) => new RetentionReadRequest(key, RetentionReadKind.Operation, requestAt, items[index].Revision))
                .ToArray();
            var materializationCount = 0;
            using var blocker = new SqliteConnection($"Data Source={path};Pooling=False");
            blocker.Open();
            using var blockerTransaction = blocker.BeginTransaction(deferred: false);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var pendingRead = Task.Run(async () =>
            {
                readStarted.SetResult();
                return await ReadThroughPrimitiveAsync(
                    store,
                    primitivePath,
                    requests,
                    () => Interlocked.Increment(ref materializationCount));
            });
            try
            {
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await AssertBlockedOnImmediateTransactionAsync(context, pendingRead);
                Assert.Equal(0, materializationCount);
                time.Advance(TimeSpan.FromSeconds(30));
            }
            finally
            {
                blockerTransaction.Rollback();
            }

            var result = await pendingRead.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsAssignableFrom<IAsyncDisposable>(result.Lease);
            try
            {
                Assert.Equal(1, materializationCount);
                Assert.Equal((long)requests.Length, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
                Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(DISTINCT expires_at) FROM retention_leases;"));
                Assert.Equal(
                    requestAt.AddSeconds(30).AddMinutes(2).ToString("O"),
                    Scalar<string>(path, "SELECT MIN(expires_at) FROM retention_leases;"));
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(ReadPrimitivePath.Single)]
    [InlineData(ReadPrimitivePath.FixedBatch)]
    public async Task ReadAdmission_AfterImmediateWriterWaitDeniesAnItemThatExpiresBeforeBeginWithoutMaterializing(
        ReadPrimitivePath primitivePath)
    {
        var path = CopyFixture();
        try
        {
            if (primitivePath == ReadPrimitivePath.FixedBatch)
                InsertLegacyRawRows(path, 601, 602);
            var time = new MutableTimeProvider(ReadCapturedAt(path));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var sourceIds = primitivePath == ReadPrimitivePath.Single ? [ReadIds(path)[0]] : new long[] { 601, 602 };
            var keys = sourceIds
                .Select(sourceId => new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sourceId.ToString()))
                .ToArray();
            var items = keys.Select(key => Assert.IsType<RetentionCatalogItem>(store.Find(key))).ToArray();
            Assert.All(items, item => Assert.Equal(items[0].ExpiresAt, item.ExpiresAt));
            var requestAt = items[0].ExpiresAt.AddTicks(-1);
            time.Advance(requestAt - time.GetUtcNow());
            var requests = keys
                .Select((key, index) => new RetentionReadRequest(key, RetentionReadKind.Access, requestAt, items[index].Revision))
                .ToArray();
            var materializationCount = 0;
            using var blocker = new SqliteConnection($"Data Source={path};Pooling=False");
            blocker.Open();
            using var blockerTransaction = blocker.BeginTransaction(deferred: false);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var pendingRead = Task.Run(async () =>
            {
                readStarted.SetResult();
                return await ReadThroughPrimitiveAsync(
                    store,
                    primitivePath,
                    requests,
                    () => Interlocked.Increment(ref materializationCount));
            });
            try
            {
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await AssertBlockedOnImmediateTransactionAsync(context, pendingRead);
                Assert.Equal(0, materializationCount);
                time.Advance(TimeSpan.FromTicks(1));
            }
            finally
            {
                blockerTransaction.Rollback();
            }

            var result = await pendingRead.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(0, materializationCount);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            foreach (var key in keys)
            {
                var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
                Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
                Assert.Equal(denied.ExpiresAt, denied.ReadDeniedAt);
            }
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadSelectedBatchAsync_CandidateDelayUsesOnePostSelectionTimeForEveryFullLease(bool pinned)
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var time = new MutableTimeProvider(ReadCapturedAt(path));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var keys = new[]
            {
                new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, "601"),
                new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, "602"),
            };
            if (pinned)
            {
                foreach (var key in keys)
                    PinItem(path, key);
            }
            var items = keys.Select(key => Assert.IsType<RetentionCatalogItem>(store.Find(key))).ToArray();
            var requestAt = pinned ? items[0].ExpiresAt.AddDays(1) : items[0].ExpiresAt.AddMinutes(-10);
            time.Advance(requestAt - time.GetUtcNow());
            var materializationCount = 0;

            var result = await store.ReadSelectedBatchAsync<string>(
                (_, _, _) =>
                {
                    var firstRequestAt = time.GetUtcNow();
                    time.Advance(TimeSpan.FromSeconds(30));
                    var secondRequestAt = time.GetUtcNow();
                    return ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(
                    [
                        new(keys[0], RetentionReadKind.Operation, firstRequestAt, items[0].Revision),
                        new(keys[1], RetentionReadKind.Operation, secondRequestAt, items[1].Revision),
                    ]);
                },
                (_, _, grants, _) =>
                {
                    Interlocked.Increment(ref materializationCount);
                    return ValueTask.FromResult<string?>($"values={grants.Count}");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            try
            {
                Assert.Equal(1, materializationCount);
                Assert.Equal(2L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
                Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(DISTINCT expires_at) FROM retention_leases;"));
                Assert.Equal(
                    time.GetUtcNow().AddMinutes(2).ToString("O"),
                    Scalar<string>(path, "SELECT MIN(expires_at) FROM retention_leases;"));
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadSelectedBatchAsync_CandidateCrossingExpiryDeniesBeforeMaterialization()
    {
        var path = CopyFixture();
        try
        {
            var time = new MutableTimeProvider(ReadCapturedAt(path));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var sourceId = ReadIds(path)[0];
            var key = new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sourceId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            time.Advance(item.ExpiresAt.AddTicks(-1) - time.GetUtcNow());
            var materializationCount = 0;

            var result = await store.ReadSelectedBatchAsync<string>(
                (_, _, _) =>
                {
                    var requestAt = time.GetUtcNow();
                    time.Advance(TimeSpan.FromTicks(1));
                    return ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(
                    [
                        new(key, RetentionReadKind.Access, requestAt, item.Revision),
                    ]);
                },
                (_, _, _, _) =>
                {
                    Interlocked.Increment(ref materializationCount);
                    return ValueTask.FromResult<string?>("must-not-materialize");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(0, materializationCount);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
            Assert.Equal(item.ExpiresAt, denied.ReadDeniedAt);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadAsync_FutureRequestTimeDoesNotOverrideTrustedAdmissionAndBoundaryClockSamples()
    {
        var path = CopyFixture();
        try
        {
            var capturedAt = ReadCapturedAt(path);
            var admissionAt = capturedAt.AddHours(1);
            var boundaryAt = admissionAt.AddSeconds(1);
            var time = new SequencedTimeProvider(capturedAt);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var sourceId = ReadIds(path)[0];
            var key = new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sourceId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var itemBefore = FullRowDump(path, "retention_items", "item_id", item.ItemId);
            var futureRequestAt = item.ExpiresAt.AddYears(1);
            time.Schedule(admissionAt, boundaryAt);
            time.ResetReadCount();

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, futureRequestAt, item.Revision),
                (_, _, _, _) =>
                {
                    Assert.Equal(1, time.ReadCount);
                    return ValueTask.FromResult<string?>("materialized");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease);
            Assert.Equal(2, time.ReadCount);
            Assert.Equal(itemBefore, FullRowDump(path, "retention_items", "item_id", item.ItemId));
            Assert.Equal(
                admissionAt.AddMinutes(2).ToString("O"),
                Scalar<string>(path, $"SELECT expires_at FROM retention_leases WHERE item_id='{item.ItemId}';"));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchRead_FutureRequestTimesUseOneTrustedAdmissionAndBoundaryClockSampleAcrossMembers(
        bool selectedBatch)
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602, 603);
            var capturedAt = ReadCapturedAt(path);
            var admissionAt = capturedAt.AddHours(1);
            var boundaryAt = admissionAt.AddSeconds(1);
            var time = new SequencedTimeProvider(capturedAt);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var keys = new[]
            {
                new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, "601"),
                new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, "602"),
                new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, "603"),
            };
            var items = keys.Select(key => Assert.IsType<RetentionCatalogItem>(store.Find(key))).ToArray();
            var itemRowsBefore = items
                .Select(item => FullRowDump(path, "retention_items", "item_id", item.ItemId))
                .ToArray();
            var futureRequestAt = items[0].ExpiresAt.AddYears(1);
            var requests = keys
                .Select((key, index) => new RetentionReadRequest(
                    key,
                    index % 2 == 0 ? RetentionReadKind.Access : RetentionReadKind.Operation,
                    futureRequestAt,
                    items[index].Revision))
                .ToArray();
            time.Schedule(admissionAt, boundaryAt);
            time.ResetReadCount();

            ValueTask<string?> Materialize(
                SqliteConnection _,
                SqliteTransaction __,
                IReadOnlyList<RetentionReadGrant> grants,
                CancellationToken ___)
            {
                Assert.Equal(1, time.ReadCount);
                Assert.Equal(requests.Length, grants.Count);
                return ValueTask.FromResult<string?>("materialized");
            }

            RetentionBatchReadResult<string> result;
            if (selectedBatch)
            {
                result = await store.ReadSelectedBatchAsync<string>(
                    (_, _, _) =>
                    {
                        Assert.Equal(0, time.ReadCount);
                        return ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(requests);
                    },
                    Materialize,
                    CancellationToken.None);
            }
            else
            {
                result = await store.ReadBatchAsync<string>(requests, Materialize, CancellationToken.None);
            }

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            Assert.Equal(2, time.ReadCount);
            Assert.Equal(
                items.Length,
                Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            Assert.Equal(
                1L,
                Scalar<long>(path, "SELECT COUNT(DISTINCT expires_at) FROM retention_leases;"));
            Assert.Equal(
                admissionAt.AddMinutes(2).ToString("O"),
                Scalar<string>(path, "SELECT MIN(expires_at) FROM retention_leases;"));
            Assert.Equal(
                itemRowsBefore,
                items.Select(item => FullRowDump(path, "retention_items", "item_id", item.ItemId)).ToArray());
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(ReadPrimitivePath.Single, false)]
    [InlineData(ReadPrimitivePath.Single, true)]
    [InlineData(ReadPrimitivePath.FixedBatch, false)]
    [InlineData(ReadPrimitivePath.FixedBatch, true)]
    [InlineData(ReadPrimitivePath.SelectedBatch, false)]
    [InlineData(ReadPrimitivePath.SelectedBatch, true)]
    public async Task ReadAdmission_SelectorReachingExactLeaseExpiryDeniesAndReleasesOnlyAdmittedTuples(
        ReadPrimitivePath primitivePath,
        bool pinned)
    {
        await AssertSelectorLeaseBoundaryAsync(
            primitivePath,
            pinned,
            TimeSpan.FromMinutes(2),
            RetentionReadDisposition.Denied);
    }

    [Theory]
    [InlineData(ReadPrimitivePath.Single, false)]
    [InlineData(ReadPrimitivePath.Single, true)]
    [InlineData(ReadPrimitivePath.FixedBatch, false)]
    [InlineData(ReadPrimitivePath.FixedBatch, true)]
    [InlineData(ReadPrimitivePath.SelectedBatch, false)]
    [InlineData(ReadPrimitivePath.SelectedBatch, true)]
    public async Task ReadAdmission_SelectorCompletingOneTickBeforeLeaseExpiryStillGrantsFullResult(
        ReadPrimitivePath primitivePath,
        bool pinned)
    {
        await AssertSelectorLeaseBoundaryAsync(
            primitivePath,
            pinned,
            TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1),
            RetentionReadDisposition.Granted);
    }

    [Fact]
    public async Task ReadAsync_PinnedRowPastHistoricalExpiryGrantsByteIdenticalRows()
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            PinItem(path, key);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var admissionAt = item.ExpiresAt.AddTicks(1);
            time.Advance(admissionAt - time.GetUtcNow());
            var itemBefore = FullRowDump(path, "retention_items", "item_id", item.ItemId);
            var sourceBefore = FullRowDump(path, "raw_records", "id", 601L);

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, admissionAt, item.Revision),
                (connection, transaction, grant, cancellationToken) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        SELECT r.payload_json
                        FROM raw_records r
                        JOIN retention_items i ON i.item_id=$retention_read_item_id AND i.revision=$retention_read_revision
                        JOIN retention_leases l ON l.item_id=i.item_id AND l.lease_kind=$retention_read_lease_kind
                          AND l.owner=$retention_read_lease_owner AND l.generation=$retention_read_lease_generation
                          AND l.expires_at=$retention_read_lease_expires_at
                        WHERE r.id=601 AND r.retention_owner_token=$retention_read_source_token;
                        """;
                    grant.BindAdmissionSelectorCapability(command);
                    return ValueTask.FromResult<string?>(command.ExecuteScalar() as string);
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            await using var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease);
            Assert.NotEmpty(lease.Value);

            Assert.Equal(itemBefore, FullRowDump(path, "retention_items", "item_id", item.ItemId));
            Assert.Equal(sourceBefore, FullRowDump(path, "raw_records", "id", 601L));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadBatchAsync_AllReadableMembersIncludingPinnedPastExpiryGrantByteIdenticalRows()
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601);
            InsertLegacyRawRow(path, 602, new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var pinnedKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            PinItem(path, pinnedKey);
            var pinnedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey));
            var expiringKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var expiringItem = Assert.IsType<RetentionCatalogItem>(store.Find(expiringKey));
            var admissionAt = pinnedItem.ExpiresAt.AddTicks(1);
            Assert.True(admissionAt < expiringItem.ExpiresAt);
            time.Advance(admissionAt - time.GetUtcNow());
            var pinnedBefore = FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId);
            var expiringBefore = FullRowDump(path, "retention_items", "item_id", expiringItem.ItemId);

            var result = await store.ReadBatchAsync<string>(
                new[]
                {
                    new RetentionReadRequest(pinnedKey, RetentionReadKind.Access, admissionAt, pinnedItem.Revision),
                    new RetentionReadRequest(expiringKey, RetentionReadKind.Access, admissionAt, expiringItem.Revision),
                },
                static (_, _, grants, _) => ValueTask.FromResult<string?>($"values={grants.Count}"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            Assert.Equal("values=2", lease.Value);
            await lease.DisposeAsync();

            Assert.Equal(pinnedBefore, FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId));
            Assert.Equal(expiringBefore, FullRowDump(path, "retention_items", "item_id", expiringItem.ItemId));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadBatchAsync_ExactBoundaryMemberDeniesBatchWithoutTouchingPinnedSibling()
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var pinnedKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            PinItem(path, pinnedKey);
            var pinnedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey));
            var boundaryKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var boundaryItem = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            var admissionAt = boundaryItem.ExpiresAt;
            time.Advance(admissionAt - time.GetUtcNow());
            var pinnedBefore = FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId);

            var result = await store.ReadBatchAsync<string>(
                new[]
                {
                    new RetentionReadRequest(pinnedKey, RetentionReadKind.Access, admissionAt, pinnedItem.Revision),
                    new RetentionReadRequest(boundaryKey, RetentionReadKind.Access, admissionAt, boundaryItem.Revision),
                },
                static (_, _, grants, _) => ValueTask.FromResult<string?>($"values={grants.Count}"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);

            var boundaryDenied = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, boundaryDenied.State);
            Assert.Equal(boundaryItem.ExpiresAt, boundaryDenied.ReadDeniedAt);
            Assert.Equal(boundaryItem.Revision + 1, boundaryDenied.Revision);
            Assert.Equal(boundaryItem.ExpiresAt.ToString("O"), (string)Scalar<object>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{boundaryDenied.ItemId}';")!);

            Assert.Equal(pinnedBefore, FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadSelectedBatchAsync_AllReadableMembersIncludingPinnedPastExpiryGrantByteIdenticalRows()
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601);
            InsertLegacyRawRow(path, 602, new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var pinnedKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            PinItem(path, pinnedKey);
            var pinnedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey));
            var expiringKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var expiringItem = Assert.IsType<RetentionCatalogItem>(store.Find(expiringKey));
            var admissionAt = pinnedItem.ExpiresAt.AddTicks(1);
            Assert.True(admissionAt < expiringItem.ExpiresAt);
            time.Advance(admissionAt - time.GetUtcNow());
            var pinnedBefore = FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId);
            var expiringBefore = FullRowDump(path, "retention_items", "item_id", expiringItem.ItemId);
            var requests = new RetentionReadRequest[]
            {
                new(pinnedKey, RetentionReadKind.Access, admissionAt, pinnedItem.Revision),
                new(expiringKey, RetentionReadKind.Access, admissionAt, expiringItem.Revision),
            };

            var result = await store.ReadSelectedBatchAsync<string>(
                (_, _, _) => ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(requests),
                static (_, _, grants, _) => ValueTask.FromResult<string?>($"values={grants.Count}"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            Assert.Equal("values=2", lease.Value);
            await lease.DisposeAsync();

            Assert.Equal(pinnedBefore, FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId));
            Assert.Equal(expiringBefore, FullRowDump(path, "retention_items", "item_id", expiringItem.ItemId));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadSelectedBatchAsync_ExactBoundaryMemberDeniesBatchWithoutTouchingPinnedSibling()
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var pinnedKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            PinItem(path, pinnedKey);
            var pinnedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey));
            var boundaryKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var boundaryItem = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            var admissionAt = boundaryItem.ExpiresAt;
            time.Advance(admissionAt - time.GetUtcNow());
            var pinnedBefore = FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId);
            var requests = new RetentionReadRequest[]
            {
                new(pinnedKey, RetentionReadKind.Access, admissionAt, pinnedItem.Revision),
                new(boundaryKey, RetentionReadKind.Access, admissionAt, boundaryItem.Revision),
            };

            var result = await store.ReadSelectedBatchAsync<string>(
                (_, _, _) => ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(requests),
                static (_, _, grants, _) => ValueTask.FromResult<string?>($"values={grants.Count}"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);

            var boundaryDenied = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, boundaryDenied.State);
            Assert.Equal(boundaryItem.ExpiresAt, boundaryDenied.ReadDeniedAt);
            Assert.Equal(boundaryItem.Revision + 1, boundaryDenied.Revision);
            Assert.Equal(boundaryItem.ExpiresAt.ToString("O"), (string)Scalar<object>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{boundaryDenied.ItemId}';")!);

            Assert.Equal(pinnedBefore, FullRowDump(path, "retention_items", "item_id", pinnedItem.ItemId));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BatchRead_DisposeUsesImmutableAdmittedTuplesAfterCallerMutatesBackingLists(
        bool selectedBatch,
        bool mutateGrantList)
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var firstKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            var secondKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var firstItem = Assert.IsType<RetentionCatalogItem>(store.Find(firstKey));
            var secondItem = Assert.IsType<RetentionCatalogItem>(store.Find(secondKey));
            var requests = new List<RetentionReadRequest>
            {
                new(firstKey, RetentionReadKind.Operation, now, firstItem.Revision),
                new(secondKey, RetentionReadKind.Operation, now, secondItem.Revision),
            };
            IReadOnlyList<RetentionReadGrant>? exposedGrants = null;

            ValueTask<string?> Materialize(
                SqliteConnection connection,
                SqliteTransaction transaction,
                IReadOnlyList<RetentionReadGrant> grants,
                CancellationToken cancellationToken)
            {
                _ = connection;
                _ = transaction;
                _ = cancellationToken;
                exposedGrants = grants;
                return ValueTask.FromResult<string?>("materialized");
            }

            var result = selectedBatch
                ? await store.ReadSelectedBatchAsync<string>(
                    (_, _, _) => ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(requests),
                    Materialize,
                    CancellationToken.None)
                : await store.ReadBatchAsync<string>(requests, Materialize, CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            var admittedGrants = Assert.IsAssignableFrom<IReadOnlyList<RetentionReadGrant>>(exposedGrants);
            Assert.Equal(2, admittedGrants.Count);
            Assert.Equal(2L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));

            if (mutateGrantList)
            {
                if (admittedGrants is IList<RetentionReadGrant> mutableGrants && !mutableGrants.IsReadOnly)
                {
                    var admitted = admittedGrants[0];
                    mutableGrants[0] = new RetentionReadGrant(
                        admitted.OwnershipKey,
                        admitted.ItemId,
                        admitted.AdmissionRevision,
                        admitted.LeaseKind,
                        admitted.LeaseOwner,
                        admitted.LeaseGeneration + 1,
                        admitted.LeaseExpiresAt,
                        new byte[32]);
                }
            }
            else
            {
                requests[0] = requests[0] with { LeaseKind = RetentionReadKind.Access };
            }

            await lease.DisposeAsync();

            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{secondItem.ItemId}';"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchRead_DisposeAttemptsEveryAdmittedTupleWhenAnEarlierReleaseFails(bool selectedBatch)
    {
        var path = CopyFixture();
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var firstKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "601");
            var secondKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, "602");
            var firstItem = Assert.IsType<RetentionCatalogItem>(store.Find(firstKey));
            var secondItem = Assert.IsType<RetentionCatalogItem>(store.Find(secondKey));
            var requests = new List<RetentionReadRequest>
            {
                new(firstKey, RetentionReadKind.Operation, now, firstItem.Revision),
                new(secondKey, RetentionReadKind.Operation, now, secondItem.Revision),
            };
            var result = selectedBatch
                ? await store.ReadSelectedBatchAsync<string>(
                    (_, _, _) => ValueTask.FromResult<IReadOnlyList<RetentionReadRequest>>(requests),
                    static (_, _, _, _) => ValueTask.FromResult<string?>("materialized"),
                    CancellationToken.None)
                : await store.ReadBatchAsync<string>(
                    requests,
                    static (_, _, _, _) => ValueTask.FromResult<string?>("materialized"),
                    CancellationToken.None);
            var lease = Assert.IsType<RetentionBatchReadLease<string>>(result.Lease);
            Execute(
                path,
                $"CREATE TRIGGER reject_first_release BEFORE DELETE ON retention_leases WHEN OLD.item_id='{firstItem.ItemId}' BEGIN SELECT RAISE(ABORT,'reject_first_release'); END;");

            var exception = await Record.ExceptionAsync(async () => await lease.DisposeAsync());

            var sqliteException = Assert.IsType<SqliteException>(exception);
            Assert.Contains("reject_first_release", sqliteException.Message, StringComparison.Ordinal);
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{firstItem.ItemId}';"));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{secondItem.ItemId}';"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadAsync_SelectorReturningNullDeniesWithoutItemMutation()
    {
        var path = CopyFixture();
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var before = FullRowDump(path, "retention_items", "item_id", item.ItemId);

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, time.GetUtcNow(), item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>(null),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.Denied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
            Assert.Equal(before, FullRowDump(path, "retention_items", "item_id", item.ItemId));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task AdmittedOperationGrant_ConsumptionTracksOnlyLeaseAndSourceTupleUntilExactExpiry()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawRecordId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, now, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("materialized"),
                CancellationToken.None);
            Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
            var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease);
            var grant = Assert.IsType<RetentionReadGrant>(lease.Grant);
            try
            {
                var originalOwner = Scalar<string>(path, $"SELECT owner FROM retention_leases WHERE item_id='{item.ItemId}';");
                var originalGeneration = Scalar<long>(path, $"SELECT generation FROM retention_leases WHERE item_id='{item.ItemId}';");
                var originalLeaseExpiry = Scalar<string>(path, $"SELECT expires_at FROM retention_leases WHERE item_id='{item.ItemId}';");
                var originalTokenLiteral = Scalar<string>(path, $"SELECT quote(retention_owner_token) FROM raw_records WHERE id={rawRecordId};");
                var originalItemDump = FullRowDump(path, "retention_items", "item_id", item.ItemId);
                Assert.True(ConsumeOperationGrant(path, grant, rawRecordId, now));

                Execute(path, "UPDATE retention_leases SET lease_kind='access' WHERE item_id=$id;", ("$id", item.ItemId));
                AssertConsumptionFailsWithoutItemMutation(path, grant, rawRecordId, now, item.ItemId, originalItemDump);
                RestoreLease(path, item.ItemId, "operation", originalOwner, originalGeneration, originalLeaseExpiry);

                Execute(path, "UPDATE retention_leases SET owner=$owner WHERE item_id=$id;", ("$owner", originalOwner + "-replaced"), ("$id", item.ItemId));
                AssertConsumptionFailsWithoutItemMutation(path, grant, rawRecordId, now, item.ItemId, originalItemDump);
                RestoreLease(path, item.ItemId, "operation", originalOwner, originalGeneration, originalLeaseExpiry);

                Execute(path, "UPDATE retention_leases SET generation=$generation WHERE item_id=$id;", ("$generation", originalGeneration + 1), ("$id", item.ItemId));
                AssertConsumptionFailsWithoutItemMutation(path, grant, rawRecordId, now, item.ItemId, originalItemDump);
                RestoreLease(path, item.ItemId, "operation", originalOwner, originalGeneration, originalLeaseExpiry);

                Execute(path, "UPDATE retention_leases SET expires_at=$expires WHERE item_id=$id;", ("$expires", (grant.LeaseExpiresAt + TimeSpan.FromTicks(1)).ToString("O")), ("$id", item.ItemId));
                AssertConsumptionFailsWithoutItemMutation(path, grant, rawRecordId, now, item.ItemId, originalItemDump);
                RestoreLease(path, item.ItemId, "operation", originalOwner, originalGeneration, originalLeaseExpiry);

                Execute(path, "DROP TRIGGER retention_raw_records_token_immutable;");
                Execute(path, "UPDATE raw_records SET retention_owner_token=randomblob(32) WHERE id=$id;", ("$id", rawRecordId));
                AssertConsumptionFailsWithoutItemMutation(path, grant, rawRecordId, now, item.ItemId, originalItemDump);
                Execute(path, $"UPDATE raw_records SET retention_owner_token={originalTokenLiteral} WHERE id={rawRecordId};");
                Execute(path, "CREATE TRIGGER retention_raw_records_token_immutable BEFORE UPDATE OF retention_owner_token ON raw_records WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");

                foreach (var mutation in new[]
                {
                    "UPDATE retention_items SET revision=revision+1 WHERE item_id=$id;",
                    "UPDATE retention_items SET state='retained_by_policy' WHERE item_id=$id;",
                    "UPDATE retention_items SET state='expired_pending_deletion', read_denied_at=$now, queued_at=$now WHERE item_id=$id;",
                    "UPDATE retention_items SET expires_at=$past WHERE item_id=$id;"
                })
                {
                    Execute(path, mutation, ("$id", item.ItemId), ("$now", now.ToString("O")), ("$past", (now - TimeSpan.FromSeconds(1)).ToString("O")));
                    var mutatedDump = FullRowDump(path, "retention_items", "item_id", item.ItemId);
                    Assert.True(ConsumeOperationGrant(path, grant, rawRecordId, now));
                    Assert.Equal(mutatedDump, FullRowDump(path, "retention_items", "item_id", item.ItemId));
                    Execute(
                        path,
                        "UPDATE retention_items SET state='expiring', revision=$revision, read_denied_at=NULL, queued_at=NULL, expires_at=$expires WHERE item_id=$id;",
                        ("$revision", item.Revision),
                        ("$expires", item.ExpiresAt.ToString("O")),
                        ("$id", item.ItemId));
                    Assert.Equal(originalItemDump, FullRowDump(path, "retention_items", "item_id", item.ItemId));
                }

                Assert.False(ConsumeOperationGrant(path, grant, rawRecordId, grant.LeaseExpiresAt));
            }
            finally { await lease.DisposeAsync(); }
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task AdmittedOperationLease_ExactTupleReleaseSurvivesItemMutationAndRejectsWrongTuples()
    {
        var path = CopyFixture();
        try
        {
            var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
            var store = new RetentionCatalogStore(path, new MutableTimeProvider(now));
            store.CreateSchema();
            var rawRecordId = Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;");
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawRecordId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var handle = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Operation, now, CancellationToken.None));
            var owner = Scalar<string>(path, $"SELECT owner FROM retention_leases WHERE item_id='{item.ItemId}';");

            Execute(path, "UPDATE retention_items SET state='retained_by_policy', revision=revision+1 WHERE item_id=$id;", ("$id", item.ItemId));
            Execute(path, "UPDATE retention_items SET state='expiring', revision=revision+1 WHERE item_id=$id;", ("$id", item.ItemId));
            Execute(path, "UPDATE retention_items SET state='deletion_queued', revision=revision+1 WHERE item_id=$id;", ("$id", item.ItemId));
            Execute(path, "UPDATE retention_items SET state='expired_pending_deletion', read_denied_at=$now, queued_at=$now, revision=revision+1 WHERE item_id=$id;", ("$id", item.ItemId), ("$now", now.ToString("O")));
            Assert.Equal(item.Revision + 4, Scalar<long>(path, $"SELECT revision FROM retention_items WHERE item_id='{item.ItemId}';"));

            foreach (var (kind, tupleOwner, generation) in new[]
            {
                ("access", owner, handle.Generation),
                ("operation", owner + "-replaced", handle.Generation),
                ("operation", owner, handle.Generation + 1)
            })
            {
                Execute(
                    path,
                    "DELETE FROM retention_leases WHERE item_id=$id AND lease_kind=$kind AND owner=$owner AND generation=$generation;",
                    ("$id", item.ItemId),
                    ("$kind", kind),
                    ("$owner", tupleOwner),
                    ("$generation", generation));
            }
            Execute(
                path,
                "DELETE FROM retention_leases WHERE item_id='no-such-item' AND lease_kind='operation' AND owner=$owner AND generation=$generation;",
                ("$owner", owner),
                ("$generation", handle.Generation));
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{item.ItemId}';"));

            handle.Dispose();
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{item.ItemId}';"));
        }
        finally { Delete(path); }
    }

    private static bool ConsumeOperationGrant(string path, RetentionReadGrant grant, long rawRecordId, DateTimeOffset at)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var usable = RetentionCatalogStore.ValidateSourceCompatibilityOperationLease(connection, transaction, grant, rawRecordId, at);
        transaction.Commit();
        return usable;
    }

    private static async Task<(RetentionReadDisposition Disposition, IAsyncDisposable? Lease)> ReadThroughPrimitiveAsync(
        RetentionCatalogStore store,
        ReadPrimitivePath primitivePath,
        IReadOnlyList<RetentionReadRequest> requests,
        Action materialized)
    {
        switch (primitivePath)
        {
            case ReadPrimitivePath.Single:
                {
                    var result = await store.ReadAsync<string>(
                        Assert.Single(requests),
                        (_, _, _, _) =>
                        {
                            materialized();
                            return ValueTask.FromResult<string?>("materialized");
                        },
                        CancellationToken.None);
                    return (result.Disposition, result.Lease);
                }
            case ReadPrimitivePath.FixedBatch:
                {
                    var result = await store.ReadBatchAsync<string>(
                        requests,
                        (_, _, grants, _) =>
                        {
                            materialized();
                            return ValueTask.FromResult<string?>($"values={grants.Count}");
                        },
                        CancellationToken.None);
                    return (result.Disposition, result.Lease);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(primitivePath));
        }
    }

    private static async Task AssertSelectorLeaseBoundaryAsync(
        ReadPrimitivePath primitivePath,
        bool pinned,
        TimeSpan selectorDuration,
        RetentionReadDisposition expectedDisposition)
    {
        var path = CopyFixture();
        try
        {
            var sourceIds = primitivePath == ReadPrimitivePath.Single ? new long[] { 601 } : [601, 602];
            InsertLegacyRawRows(path, sourceIds);
            var time = new MutableTimeProvider(ReadCapturedAt(path));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RetentionCatalogStore(context, time);
            var keys = sourceIds
                .Select(sourceId => new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sourceId.ToString()))
                .ToArray();
            if (pinned)
            {
                foreach (var key in keys)
                    PinItem(path, key);
            }
            var items = keys.Select(key => Assert.IsType<RetentionCatalogItem>(store.Find(key))).ToArray();
            var admissionAt = items[0].CapturedAt.AddHours(1);
            time.Advance(admissionAt - time.GetUtcNow());
            var requests = keys
                .Select((key, index) => new RetentionReadRequest(key, RetentionReadKind.Operation, admissionAt, items[index].Revision))
                .ToArray();
            var itemRowsBefore = items
                .Select(item => FullRowDump(path, "retention_items", "item_id", item.ItemId))
                .ToArray();
            var sourceRowsBefore = sourceIds
                .Select(sourceId => FullRowDump(path, "raw_records", "id", sourceId))
                .ToArray();
            var sentinelSourceId = ReadIds(path).First(sourceId => !sourceIds.Contains(sourceId));
            var sentinelKey = new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, sentinelSourceId.ToString());
            var sentinelItem = Assert.IsType<RetentionCatalogItem>(store.Find(sentinelKey));
            Execute(
                path,
                "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item_id,'operation','selector-boundary-sentinel',$expires_at,97);",
                ("$item_id", sentinelItem.ItemId),
                ("$expires_at", admissionAt.AddMinutes(10).ToString("O")));
            var sentinelBefore = FullRowDump(path, "retention_leases", "item_id", sentinelItem.ItemId);
            var admittedGrants = new List<RetentionReadGrant>();
            var materializationCount = 0;

            void Materialize(IReadOnlyList<RetentionReadGrant> grants)
            {
                materializationCount++;
                Assert.Equal(requests.Length, grants.Count);
                Assert.All(grants, grant => Assert.Equal(admissionAt.AddMinutes(2), grant.LeaseExpiresAt));
                admittedGrants.AddRange(grants);
                time.Advance(selectorDuration);
            }

            var result = await ReadThroughEveryPrimitiveAsync(
                store,
                primitivePath,
                requests,
                Materialize);

            Assert.Equal(expectedDisposition, result.Disposition);
            Assert.Equal(1, materializationCount);
            Assert.Equal(requests.Length, admittedGrants.Count);
            Assert.Equal(
                itemRowsBefore,
                items.Select(item => FullRowDump(path, "retention_items", "item_id", item.ItemId)).ToArray());
            Assert.Equal(
                sourceRowsBefore,
                sourceIds.Select(sourceId => FullRowDump(path, "raw_records", "id", sourceId)).ToArray());
            Assert.Equal(sentinelBefore, FullRowDump(path, "retention_leases", "item_id", sentinelItem.ItemId));

            if (expectedDisposition == RetentionReadDisposition.Denied)
            {
                Assert.Null(result.Lease);
                Assert.All(admittedGrants, grant => Assert.Equal(0L, LeaseTupleCount(path, grant)));
            }
            else
            {
                var lease = Assert.IsAssignableFrom<IAsyncDisposable>(result.Lease);
                try
                {
                    Assert.All(admittedGrants, grant => Assert.Equal(1L, LeaseTupleCount(path, grant)));
                }
                finally
                {
                    await lease.DisposeAsync();
                }
                Assert.All(admittedGrants, grant => Assert.Equal(0L, LeaseTupleCount(path, grant)));
            }

            Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    private static async Task<(RetentionReadDisposition Disposition, IAsyncDisposable? Lease)> ReadThroughEveryPrimitiveAsync(
        RetentionCatalogStore store,
        ReadPrimitivePath primitivePath,
        IReadOnlyList<RetentionReadRequest> requests,
        Action<IReadOnlyList<RetentionReadGrant>> materialized)
    {
        switch (primitivePath)
        {
            case ReadPrimitivePath.Single:
                {
                    var result = await store.ReadAsync<string>(
                        Assert.Single(requests),
                        (_, _, grant, _) =>
                        {
                            materialized([grant]);
                            return ValueTask.FromResult<string?>("materialized");
                        },
                        CancellationToken.None);
                    return (result.Disposition, result.Lease);
                }
            case ReadPrimitivePath.FixedBatch:
                {
                    var result = await store.ReadBatchAsync<string>(
                        requests,
                        (_, _, grants, _) =>
                        {
                            materialized(grants);
                            return ValueTask.FromResult<string?>($"values={grants.Count}");
                        },
                        CancellationToken.None);
                    return (result.Disposition, result.Lease);
                }
            case ReadPrimitivePath.SelectedBatch:
                {
                    var result = await store.ReadSelectedBatchAsync<string>(
                        (_, _, _) => ValueTask.FromResult(requests),
                        (_, _, grants, _) =>
                        {
                            materialized(grants);
                            return ValueTask.FromResult<string?>($"values={grants.Count}");
                        },
                        CancellationToken.None);
                    return (result.Disposition, result.Lease);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(primitivePath));
        }
    }

    private static long LeaseTupleCount(string path, RetentionReadGrant grant)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item_id AND lease_kind=$lease_kind AND owner=$owner AND generation=$generation;";
        command.Parameters.AddWithValue("$item_id", grant.ItemId);
        command.Parameters.AddWithValue("$lease_kind", grant.LeaseKind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$owner", grant.LeaseOwner);
        command.Parameters.AddWithValue("$generation", grant.LeaseGeneration);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertBlockedOnImmediateTransactionAsync(
        RetentionCatalogContext context,
        Task pendingRead)
    {
        Assert.True(
            SpinWait.SpinUntil(
                () => context.Gate.CurrentCount == 0,
                TimeSpan.FromSeconds(5)),
            "The read did not acquire its in-process gate before the timeout.");
        var observationWindow = Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Same(observationWindow, await Task.WhenAny(pendingRead, observationWindow));
        Assert.Equal(0, context.Gate.CurrentCount);
    }

    private static void AssertConsumptionFailsWithoutItemMutation(string path, RetentionReadGrant grant, long rawRecordId, DateTimeOffset at, string itemId, string expectedItemDump)
    {
        Assert.False(ConsumeOperationGrant(path, grant, rawRecordId, at));
        Assert.Equal(expectedItemDump, FullRowDump(path, "retention_items", "item_id", itemId));
    }

    private static void RestoreLease(string path, string itemId, string leaseKind, string owner, long generation, string expiresAt) =>
        Execute(
            path,
            "UPDATE retention_leases SET lease_kind=$kind, owner=$owner, generation=$generation, expires_at=$expires WHERE item_id=$id;",
            ("$kind", leaseKind),
            ("$owner", owner),
            ("$generation", generation),
            ("$expires", expiresAt),
            ("$id", itemId));

    private static RetentionReadGrant CreateGrant(string itemId, string sourceItemId, byte tokenByte) =>
        new(
            new RetentionOwnershipKey("store", RetentionStoreKind.RawRecord, sourceItemId),
            itemId,
            7,
            RetentionLeaseKind.Operation,
            $"owner-{sourceItemId}",
            11,
            new DateTimeOffset(2026, 8, 1, 0, 2, 0, TimeSpan.Zero),
            Enumerable.Repeat(tokenByte, 32).ToArray());

    private static bool TryBindGrant(RetentionReadGrant grant)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = CompleteAdmissionSelectorSql;
        return grant.TryBindAdmissionSelectorCapability(command);
    }

    private static void InsertLegacyRawRows(string path, params long[] ids)
    {
        foreach (var id in ids)
            InsertLegacyRawRow(path, id, new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
    }

    private static void InsertLegacyRawRow(string path, long id, DateTimeOffset receivedAt) =>
        Execute(
            path,
            "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version) VALUES($id,'raw-otlp','fixture-monitor-v5-trace',$received_at,NULL,'{\"fixture\":true}',1);",
            ("$id", id),
            ("$received_at", receivedAt.ToString("O")));

    private static void PinItem(string path, RetentionOwnershipKey key) =>
        Execute(
            path,
            "UPDATE retention_items SET state='retained_by_policy', revision=revision+1 WHERE store_instance_id=$store_instance_id AND store_kind='raw_record' AND source_item_id=$source_item_id;",
            ("$store_instance_id", key.StoreInstanceId),
            ("$source_item_id", key.SourceItemId));

    private static string FullRowDump(string path, string table, string keyColumn, object keyValue)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"quote({column})"))} FROM {table} WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$key", keyValue);
        using var rows = command.ExecuteReader();
        return rows.Read()
            ? string.Join("|", columns.Select((column, index) => $"{column}={rows.GetString(index)}"))
            : $"{table}:absent";
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed class SingleEnumerationReadOnlyList<T>(T[] values) : IReadOnlyList<T>
    {
        private int enumerationCount;

        public int Count => values.Length;
        public T this[int index] => values[index];
        internal int EnumerationCount => Volatile.Read(ref enumerationCount);

        public IEnumerator<T> GetEnumerator()
        {
            if (Interlocked.Increment(ref enumerationCount) != 1)
                throw new InvalidOperationException("repeated_enumeration_rejected");
            return ((IEnumerable<T>)values).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SequencedTimeProvider(DateTimeOffset fallback) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> scheduled = [];

        internal int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return scheduled.Count > 0 ? scheduled.Dequeue() : fallback;
        }

        internal void Schedule(params DateTimeOffset[] instants)
        {
            foreach (var instant in instants)
                scheduled.Enqueue(instant);
        }

        internal void ResetReadCount() => ReadCount = 0;
    }

    public enum ReadPrimitivePath
    {
        Single,
        FixedBatch,
        SelectedBatch,
    }
}

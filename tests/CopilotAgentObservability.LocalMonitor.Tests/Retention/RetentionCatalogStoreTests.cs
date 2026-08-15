using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCatalogStoreTests
{
    [Fact]
    public void OrdinaryCatalogConnections_UseFiveSecondBusyTimeout()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            long createTimeout = -1;
            var initializing = new RetentionCatalogStore(path, backfillValidationCheckpoint: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "PRAGMA busy_timeout;";
                createTimeout = Convert.ToInt64(command.ExecuteScalar());
            });

            initializing.CreateSchema();
            var adopted = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(path));
            using var existing = adopted.OpenMutationConnection();
            using var existingTimeout = existing.CreateCommand();
            existingTimeout.CommandText = "PRAGMA busy_timeout;";

            Assert.Equal(5000L, createTimeout);
            Assert.Equal(5000L, Convert.ToInt64(existingTimeout.ExecuteScalar()));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RebuildsRawSourceWithRequiredImmutableOwnerToken()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();

            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='raw_records';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", sql, StringComparison.Ordinal);
            Assert.Contains("length(retention_owner_token) = 32", sql, StringComparison.Ordinal);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records WHERE typeof(retention_owner_token) <> 'blob' OR length(retention_owner_token) <> 32;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RebuildsSessionContentWithRequiredOwnerTokenAndContentKindReceiptBinding()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();

            Assert.Equal(14L, Scalar<long>(path, "SELECT version FROM schema_version WHERE component='session';"));
            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='session_event_content';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", sql, StringComparison.Ordinal);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM session_event_content WHERE typeof(retention_owner_token) <> 'blob' OR length(retention_owner_token) <> 32;"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND ownership_receipt IS NULL;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RebuildsAnalysisSourcesWithRequiredTokenAndForeignKey()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            new RetentionCatalogStore(path).CreateSchema();

            var runsSql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='monitor_analysis_runs';");
            var eventsSql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='monitor_analysis_events';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", runsSql, StringComparison.Ordinal);
            Assert.Contains("FOREIGN KEY (run_id) REFERENCES monitor_analysis_runs(id)", eventsSql, StringComparison.Ordinal);
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO monitor_analysis_events(run_id,event_type,message,occurred_at) VALUES(999,'test','synthetic','2026-07-12T00:00:00.0000000+00:00');"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_BackfillsRealSessionFixtureAndTwoReopensKeepExactIdentity()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            var first = new RetentionCatalogStore(path); first.CreateSchema();
            var firstInstance = first.StoreInstanceId;
            var firstItems = ReadItems(path);

            new RetentionCatalogStore(path).CreateSchema();
            var third = new RetentionCatalogStore(path); third.CreateSchema();

            Assert.NotEmpty(firstItems);
            Assert.Equal(firstInstance, third.StoreInstanceId);
            Assert.Equal(firstItems, ReadItems(path));
            Assert.Equal(1L, Scalar<long>(path, "SELECT version FROM retention_component_versions WHERE component='retention';"));
            Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_component_versions;"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND (captured_at <> (SELECT captured_at FROM session_event_content WHERE event_id=retention_items.source_item_id) OR expires_at <> (SELECT expires_at FROM session_event_content WHERE event_id=retention_items.source_item_id));"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_BackfillsRealMonitorFixtureAndActualAnalysisRawSchema()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            var store = new RetentionCatalogStore(path); store.CreateSchema();

            Assert.Contains(ReadItems(path), item => item.Kind == "raw_record");
            var analysis = Assert.Single(ReadItems(path), item => item.Kind == "analysis_run_raw");
            Assert.Equal("2026-07-12T01:02:03.0000000+00:00", analysis.CapturedAt);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND captured_at <> (SELECT received_at FROM raw_records WHERE id=retention_items.source_item_id);"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_InvalidLegacyTimestampRollsBackEntireCatalogAndReturnsSanitizedCode()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            Execute(path, "UPDATE raw_records SET received_at='not-a-timestamp';");
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_component_versions"));
            Assert.False(TableExists(path, "retention_items"));
            Execute(path, "UPDATE raw_records SET received_at='2026-07-12T00:00:00.0000000+00:00';");
            new RetentionCatalogStore(path).CreateSchema();
            Assert.True(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_OrphanedSessionContentBlocksWithoutPartialCatalog()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            Execute(path, "PRAGMA foreign_keys=OFF; DELETE FROM session_events WHERE event_id IN (SELECT event_id FROM session_event_content); PRAGMA foreign_keys=ON;");

            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiryCommitsIrreversibleDenialAndStaleRevisionChangesNothing()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            time.Advance(item.ExpiresAt - time.GetUtcNow());

            Assert.Null(await store.TryAcquireAsync(key, item.Revision - 1, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None));
            Assert.Equal(item, store.Find(key));
            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None));
            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
            Assert.NotNull(denied.ReadDeniedAt);

            var reopened = new RetentionCatalogStore(path).Find(key);
            Assert.Equal(denied, reopened);
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("readability")]
    [InlineData("source_receipt")]
    [InlineData("coverage")]
    public async Task RenewOperationLease_NotDueIgnoresCurrentRenewalProofDrift(string drift)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var at = new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(at);
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            Execute(
                path,
                """
                INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
                VALUES
                    ('session_event_content',1),
                    ('raw_record',1),
                    ('analysis_run_raw',1),
                    ('sensitive_bundle',1),
                    ('analysis_sdk_directory',1);
                """);
            var key = RawKey(path, store);
            var rawRecordId = long.Parse(key.SourceItemId, CultureInfo.InvariantCulture);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var admission = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, at, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("value"),
                CancellationToken.None);
            await using var lease = Assert.IsType<RetentionReadLease<string>>(admission.Lease);
            var grant = lease.Grant;

            switch (drift)
            {
                case "revision":
                    Execute(path, $"UPDATE retention_items SET revision=revision+1 WHERE item_id='{grant.ItemId}';");
                    break;
                case "readability":
                    Execute(path, $"UPDATE retention_items SET read_denied_at='{at:O}' WHERE item_id='{grant.ItemId}';");
                    break;
                case "source_receipt":
                    Execute(path, $"UPDATE raw_records SET received_at='{at.AddSeconds(1):O}' WHERE id={rawRecordId};");
                    break;
                case "coverage":
                    Execute(path, "DELETE FROM retention_adapter_coverage WHERE store_kind='raw_record';");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(drift));
            }

            var authorityBefore = RetentionAuthorityDump(path);
            var sourceBefore = SourceRowDump(path, rawRecordId);
            var publishedExpiryBefore = PublishedLeaseExpiry(grant);
            Assert.True(GrantIsUsable(path, grant, rawRecordId, at));

            var disposition = store.RenewOperationLease(grant);

            Assert.Equal(RetentionOperationRenewalDisposition.NotDue, disposition);
            Assert.Equal(authorityBefore, RetentionAuthorityDump(path));
            Assert.Equal(sourceBefore, SourceRowDump(path, rawRecordId));
            Assert.Equal(publishedExpiryBefore, PublishedLeaseExpiry(grant));
            Assert.True(GrantIsUsable(path, grant, rawRecordId, at));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task RenewOperationLease_CommittedRenewalMovesHiddenHandleExpiryNotification()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var admissionAt = new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(admissionAt);
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            Execute(
                path,
                """
                INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
                VALUES
                    ('session_event_content',1),
                    ('raw_record',1),
                    ('analysis_run_raw',1),
                    ('sensitive_bundle',1),
                    ('analysis_sdk_directory',1);
                """);
            var key = RawKey(path, store);
            var rawRecordId = long.Parse(key.SourceItemId, CultureInfo.InvariantCulture);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var admission = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, admissionAt, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("value"),
                CancellationToken.None);
            var lease = Assert.IsType<RetentionReadLease<string>>(admission.Lease);
            var grant = lease.Grant;
            var admissionExpiry = PublishedLeaseExpiry(grant);
            try
            {
                time.Advance(RetentionV1Constants.LeaseRenewalDeadline);

                Assert.Equal(
                    RetentionOperationRenewalDisposition.Renewed,
                    store.RenewOperationLease(grant));
                Assert.True(PublishedLeaseExpiry(grant) > admissionExpiry);

                time.Advance(admissionExpiry - time.GetUtcNow());
                time.Advance(TimeSpan.Zero);

                Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{grant.ItemId}';"));
                Assert.True(GrantIsUsable(path, grant, rawRecordId, time.GetUtcNow()));
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task RenewOperationLease_PublicationWaitCrossingExactExpiryReturnsLeaseLostWithoutResurrection()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 601, 602);
            var admissionAt = new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(admissionAt);
            var initializing = new RetentionCatalogStore(path, time);
            initializing.CreateSchema();
            Execute(
                path,
                """
                INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
                VALUES
                    ('session_event_content',1),
                    ('raw_record',1),
                    ('analysis_run_raw',1),
                    ('sensitive_bundle',1),
                    ('analysis_sdk_directory',1);
                """);
            var renewalTransactionBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkpointCount = 0;
            var store = new RetentionCatalogStore(
                RetentionCatalogContext.AdoptExistingCatalogV1(path),
                time,
                static _ => { },
                checkpoint =>
                {
                    if (checkpoint != "renewal_transaction_began")
                        return;
                    Interlocked.Increment(ref checkpointCount);
                    renewalTransactionBegan.TrySetResult();
                });
            var key = RawKeyFor(store, 601);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var admission = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Operation, admissionAt, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("materialized"),
                CancellationToken.None);
            var lease = Assert.IsType<RetentionReadLease<string>>(admission.Lease);
            var grant = lease.Grant;
            RetentionCatalogItem? unrelatedItem = null;
            string? unrelatedLeaseBefore = null;
            try
            {
                var publishedExpiryBefore = PublishedLeaseExpiry(grant);
                var persistedExpiryBefore = Scalar<string>(
                    path,
                    $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';");
                unrelatedItem = Assert.IsType<RetentionCatalogItem>(store.Find(RawKeyFor(store, 602)));
                Execute(
                    path,
                    "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item_id,'operation',$owner,$expiry,$generation);",
                    ("$item_id", unrelatedItem.ItemId),
                    ("$owner", grant.LeaseOwner),
                    ("$expiry", publishedExpiryBefore.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture)),
                    ("$generation", grant.LeaseGeneration));
                unrelatedLeaseBefore = FullRowDump(path, "retention_leases", "item_id", unrelatedItem.ItemId);
                var itemBefore = ItemRowDump(path, item.ItemId);
                var sourceBefore = SourceRowDump(path, 601);
                var authorityBefore = RetentionAuthorityDump(path);
                time.Advance(TimeSpan.FromMinutes(1));

                var publicationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var releasePublication = new ManualResetEventSlim();
                var publicationHolder = Task.Run(() =>
                {
                    using var publication = grant.EnterLeasePublication();
                    publicationHeld.TrySetResult();
                    if (!releasePublication.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Publication scope was not released by the test.");
                });
                Task<RetentionOperationRenewalDisposition>? renewal = null;
                var checkpointCountAtBoundary = 0;
                var completedAfterBegin = true;
                var completedAtBoundary = true;
                try
                {
                    await publicationHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    renewal = Task.Run(() => store.RenewOperationLease(grant));
                    await renewalTransactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    checkpointCountAtBoundary = Volatile.Read(ref checkpointCount);
                    completedAfterBegin = renewal.IsCompleted;
                    time.Advance(publishedExpiryBefore - time.GetUtcNow());
                    completedAtBoundary = renewal.IsCompleted;
                }
                finally
                {
                    releasePublication.Set();
                    await publicationHolder.WaitAsync(TimeSpan.FromSeconds(5));
                }

                var disposition = await Assert.IsType<Task<RetentionOperationRenewalDisposition>>(renewal)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, checkpointCountAtBoundary);
                Assert.False(completedAfterBegin);
                Assert.Equal(publishedExpiryBefore, time.GetUtcNow());
                Assert.False(completedAtBoundary);
                Assert.Equal(RetentionOperationRenewalDisposition.LeaseLost, disposition);
                Assert.Equal(persistedExpiryBefore, Scalar<string>(
                    path,
                    $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';"));
                Assert.Equal(publishedExpiryBefore, PublishedLeaseExpiry(grant));
                Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
                Assert.Equal(sourceBefore, SourceRowDump(path, 601));
                Assert.Equal(authorityBefore, RetentionAuthorityDump(path));
                Assert.Equal(unrelatedLeaseBefore, FullRowDump(path, "retention_leases", "item_id", unrelatedItem.ItemId));
            }
            finally
            {
                await lease.DisposeAsync();
            }

            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{grant.ItemId}';"));
            Assert.Equal(unrelatedLeaseBefore, FullRowDump(path, "retention_leases", "item_id", unrelatedItem.ItemId));
            Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void CreateSchema_UsesClosedRetentionDomains()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();
            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='retention_items';");
            Assert.Contains("CHECK (store_kind IN", sql, StringComparison.Ordinal);
            Assert.Contains("CHECK (state IN", sql, StringComparison.Ordinal);
            Assert.Contains("UNIQUE(store_instance_id, store_kind, source_item_id)", sql, StringComparison.Ordinal);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RejectsInvalidCatalogRowsWithoutChangingCommittedState()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var before = Scalar<long>(path, "SELECT COUNT(*) FROM retention_items;");

            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-fk','not-a-store','raw_record','1','1','2026-07-12T00:00:00.0000000+00:00','2026-10-10T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-domain',(SELECT store_instance_id FROM retention_store_instances),'unknown','2','2','2026-07-12T00:00:00.0000000+00:00','2026-10-10T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-null',(SELECT store_instance_id FROM retention_store_instances),'raw_record','3','3','2026-07-12T00:00:00.0000000+00:00',NULL,'raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));

            Assert.Equal(before, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiredLeaseIsReclaimedWithNewGenerationAndStaleDisposeCannotReleaseIt()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var first = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));

            time.Advance(RetentionV1Constants.LeaseDuration);
            var replacement = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            Assert.True(replacement.Generation > first.Generation);
            first.Dispose();

            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            replacement.Dispose();
            Assert.NotNull(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_AlreadyExpiredLegacyRowsAreDeniedAtInjectedStartupTime()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            new RetentionCatalogStore(path, new MutableTimeProvider(now)).CreateSchema();

            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE state <> 'expired_pending_deletion' OR read_denied_at <> '2026-10-11T00:00:00.0000000+00:00' OR queued_at <> '2026-10-11T00:00:00.0000000+00:00';"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_MissingExactRawRecordFailsClosedWithoutGrantingLease()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Execute(path, "DELETE FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);");

            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.CapturedAt, CancellationToken.None));
            Assert.Equal(RetentionItemLifecycle.DeletionFailed, Assert.IsType<RetentionCatalogItem>(store.Find(key)).State);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_DeletionLeaseRequiresQueuedDenialAndExcludesLiveReadOperationLeases()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = RawKey(path, store);
            var readable = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            Assert.Null(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            using var access = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            PromoteToQueued(path, readable.ItemId, time.GetUtcNow());
            var queued = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            access.Dispose();
            using var deletion = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Operation, time.GetUtcNow(), CancellationToken.None));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiredReadLeaseDoesNotBlockSingleConcurrentDeletionClaim()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = RawKey(path, store);
            var readable = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var expiredAccess = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Operation, time.GetUtcNow(), CancellationToken.None));
            time.Advance(RetentionV1Constants.LeaseDuration);
            PromoteToQueued(path, readable.ItemId, time.GetUtcNow());
            var queued = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            using var start = new Barrier(3);
            var first = Task.Run(async () => { start.SignalAndWait(); return await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None); });
            var second = Task.Run(async () => { start.SignalAndWait(); return await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None); });
            start.SignalAndWait();
            var claims = await Task.WhenAll(first, second);

            Assert.Single(claims, static claim => claim is not null);
            expiredAccess.Dispose();
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_DeletingRecoveryAfterRestartRequiresCurrentDeleteIntentAndReclaimsExpiredLease()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var initialized = new RetentionCatalogStore(path, time);
            initialized.CreateSchema();
            var key = RawKey(path, initialized);
            var item = Assert.IsType<RetentionCatalogItem>(initialized.Find(key));
            var now = time.GetUtcNow();
            Execute(path, $"UPDATE retention_items SET state='deleting', read_denied_at='{now:O}', revision=revision+1 WHERE item_id='{item.ItemId}'; DELETE FROM raw_records WHERE id={key.SourceItemId};");
            var deleting = Assert.IsType<RetentionCatalogItem>(initialized.Find(key));
            Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{deleting.ItemId}','deletion','expired-owner','{now.AddSeconds(-1):O}',9);");

            var reopened = new RetentionCatalogStore(path, time);
            Assert.Null(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));

            Execute(path, $"INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{deleting.ItemId}','{now:O}',{deleting.Revision - 1});");
            Assert.Null(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));

            Execute(path, $"DELETE FROM retention_delete_journal; INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{deleting.ItemId}','{now:O}',{deleting.Revision});");
            using var claim = Assert.IsType<RetentionReadLeaseHandle>(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));
            Assert.Equal(10, claim.Generation);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RejectsInvalidJournalLeaseAndCoverageDomains()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var itemId = Scalar<string>(path, "SELECT item_id FROM retention_items ORDER BY item_id LIMIT 1;");

            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_capture_journal(item_id,phase) VALUES('{itemId}','invalid');"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('{itemId}','2026-07-12T00:00:00.0000000+00:00','2026-07-12T00:00:01.0000000+00:00');"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{itemId}',NULL,1);"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{itemId}','invalid','owner','2026-07-12T00:00:00.0000000+00:00',1);"));
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES('raw_record',0);"));
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES('invalid',1);"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_adapter_coverage;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_NewerComponentVersionIsSanitizedAndAtomic()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            Execute(path, "CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO retention_component_versions VALUES('retention',2);");
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());
            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_PostBackfillSourceMismatchRollsBackThenRepairsAcrossTwoReopens()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var originalRawCount = Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;");
            var injected = new RetentionCatalogStore(path, backfillValidationCheckpoint: static (connection, transaction) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);";
                delete.ExecuteNonQuery();
            });

            Assert.Throws<RetentionMigrationBlockedException>(injected.CreateSchema);
            Assert.Equal(5L, Scalar<long>(path, "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;"));
            Assert.False(TableExists(path, "retention_items"));

            var repaired = new RetentionCatalogStore(path);
            repaired.CreateSchema();
            repaired.CreateSchema();
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;"));
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record';"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_RowReadableMatrixUsesPinnedStateInsteadOfHistoricalExpiry()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 601, 602, 603, 604, 605, 606);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var at = time.GetUtcNow();

            // Row 1: a pinned row stays readable after its historical expiry passes.
            var pinnedKey = RawKeyFor(store, 601);
            PinItem(path, Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey)).ItemId);
            var pinnedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedKey));
            var pinnedBefore = ItemRowDump(path, pinnedItem.ItemId);
            var pinnedSourceBefore = SourceRowDump(path, 601);
            time.Advance(pinnedItem.ExpiresAt.AddTicks(1) - time.GetUtcNow());
            using (Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(pinnedKey, pinnedItem.Revision, RetentionLeaseKind.Access, pinnedItem.ExpiresAt.AddTicks(1), CancellationToken.None)))
            {
            }
            Assert.Equal(pinnedBefore, ItemRowDump(path, pinnedItem.ItemId));
            Assert.Equal(pinnedSourceBefore, SourceRowDump(path, 601));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{pinnedItem.ItemId}';"));

            // Row 2: an expiring row stays readable strictly before its expiry instant.
            var earlyKey = RawKey(path, store);
            var earlyItem = Assert.IsType<RetentionCatalogItem>(store.Find(earlyKey));
            var earlyBefore = ItemRowDump(path, earlyItem.ItemId);
            var earlySourceBefore = SourceRowDump(path, 501);
            time.Advance(earlyItem.ExpiresAt.AddTicks(-1) - time.GetUtcNow());
            using (Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(earlyKey, earlyItem.Revision, RetentionLeaseKind.Access, earlyItem.ExpiresAt.AddTicks(-1), CancellationToken.None)))
            {
            }
            Assert.Equal(earlyBefore, ItemRowDump(path, earlyItem.ItemId));
            Assert.Equal(earlySourceBefore, SourceRowDump(path, 501));

            // Row 3: an expiring row at its exact expiry instant is denied and queued exactly once.
            var boundaryKey = RawKeyFor(store, 602);
            var boundaryItem = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            var boundarySourceBefore = SourceRowDump(path, 602);
            time.Advance(boundaryItem.ExpiresAt - time.GetUtcNow());
            Assert.Null(await store.TryAcquireAsync(boundaryKey, boundaryItem.Revision, RetentionLeaseKind.Access, boundaryItem.ExpiresAt, CancellationToken.None));
            var boundaryDenied = Assert.IsType<RetentionCatalogItem>(store.Find(boundaryKey));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, boundaryDenied.State);
            Assert.Equal(boundaryItem.ExpiresAt, boundaryDenied.ReadDeniedAt);
            Assert.Equal(boundaryItem.Revision + 1, boundaryDenied.Revision);
            Assert.Equal(boundaryItem.ExpiresAt.ToString("O"), Scalar<string>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{boundaryDenied.ItemId}';"));
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_items WHERE item_id='{boundaryDenied.ItemId}' AND error_code IS NULL;"));
            Assert.Equal(boundarySourceBefore, SourceRowDump(path, 602));

            // Row 4: a pinned row with a recorded denial stays denied byte-identically.
            var pinnedDeniedKey = RawKeyFor(store, 603);
            var pinnedDeniedSeed = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedDeniedKey));
            Execute(path, $"UPDATE retention_items SET state='retained_by_policy', read_denied_at='{at:O}', revision=revision+1 WHERE item_id='{pinnedDeniedSeed.ItemId}';");
            var pinnedDeniedItem = Assert.IsType<RetentionCatalogItem>(store.Find(pinnedDeniedKey));
            var pinnedDeniedBefore = ItemRowDump(path, pinnedDeniedItem.ItemId);
            Assert.Null(await store.TryAcquireAsync(pinnedDeniedKey, pinnedDeniedItem.Revision, RetentionLeaseKind.Access, pinnedDeniedItem.ExpiresAt.AddDays(1), CancellationToken.None));
            Assert.Equal(pinnedDeniedBefore, ItemRowDump(path, pinnedDeniedItem.ItemId));

            // Row 5: denied, deleting, and deleted lifecycles stay denied byte-identically.
            var queuedKey = RawKeyFor(store, 604);
            PromoteToQueued(path, Assert.IsType<RetentionCatalogItem>(store.Find(queuedKey)).ItemId, at);
            var deletingKey = RawKeyFor(store, 605);
            Execute(path, $"UPDATE retention_items SET state='deleting', read_denied_at='{at:O}', queued_at='{at:O}', revision=revision+1 WHERE item_id='{Assert.IsType<RetentionCatalogItem>(store.Find(deletingKey)).ItemId}';");
            var deletedKey = RawKeyFor(store, 606);
            Execute(path, $"UPDATE retention_items SET state='deleted', read_denied_at='{at:O}', queued_at='{at:O}', deleted_at='{at:O}', revision=revision+1 WHERE item_id='{Assert.IsType<RetentionCatalogItem>(store.Find(deletedKey)).ItemId}';");
            foreach (var lifecycleKey in new[] { queuedKey, deletingKey, deletedKey })
            {
                var lifecycleItem = Assert.IsType<RetentionCatalogItem>(store.Find(lifecycleKey));
                var lifecycleBefore = ItemRowDump(path, lifecycleItem.ItemId);
                Assert.Null(await store.TryAcquireAsync(lifecycleKey, lifecycleItem.Revision, RetentionLeaseKind.Access, at.AddDays(1), CancellationToken.None));
                Assert.Equal(lifecycleBefore, ItemRowDump(path, lifecycleItem.ItemId));
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ReadGate_LifecycleResultsTakePrecedenceOverSourceProofEvaluation()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 611, 612, 613, 614);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var now = time.GetUtcNow();

            // A stale revision is rejected before any source evaluation happens.
            var staleKey = RawKeyFor(store, 611);
            var staleItem = Assert.IsType<RetentionCatalogItem>(store.Find(staleKey));
            Execute(path, "DELETE FROM raw_records WHERE id=611;");
            var staleBefore = ItemRowDump(path, staleItem.ItemId);
            Assert.Null(await store.TryAcquireAsync(staleKey, staleItem.Revision - 1, RetentionLeaseKind.Access, now, CancellationToken.None));
            Assert.Equal(staleBefore, ItemRowDump(path, staleItem.ItemId));

            // An already denied row is rejected before any source evaluation happens.
            var deniedKey = RawKeyFor(store, 612);
            PromoteToQueued(path, Assert.IsType<RetentionCatalogItem>(store.Find(deniedKey)).ItemId, now);
            Execute(path, "DELETE FROM raw_records WHERE id=612;");
            var deniedItem = Assert.IsType<RetentionCatalogItem>(store.Find(deniedKey));
            var deniedBefore = ItemRowDump(path, deniedItem.ItemId);
            Assert.Null(await store.TryAcquireAsync(deniedKey, deniedItem.Revision, RetentionLeaseKind.Access, now, CancellationToken.None));
            Assert.Equal(deniedBefore, ItemRowDump(path, deniedItem.ItemId));

            // A deleting row with a NULL denial is rejected without mutation even when its source is missing.
            var deletingKey = RawKeyFor(store, 613);
            Execute(path, $"UPDATE retention_items SET state='deleting', revision=revision+1 WHERE item_id='{Assert.IsType<RetentionCatalogItem>(store.Find(deletingKey)).ItemId}';");
            Execute(path, "DELETE FROM raw_records WHERE id=613;");
            var deletingItem = Assert.IsType<RetentionCatalogItem>(store.Find(deletingKey));
            var deletingBefore = ItemRowDump(path, deletingItem.ItemId);
            Assert.Null(await store.TryAcquireAsync(deletingKey, deletingItem.Revision, RetentionLeaseKind.Access, now, CancellationToken.None));
            Assert.Equal(deletingBefore, ItemRowDump(path, deletingItem.ItemId));

            // An expired readable row records only the expiry denial, never a source denial.
            var expiredKey = RawKeyFor(store, 614);
            var expiredItem = Assert.IsType<RetentionCatalogItem>(store.Find(expiredKey));
            Execute(path, "DELETE FROM raw_records WHERE id=614;");
            time.Advance(expiredItem.ExpiresAt - time.GetUtcNow());
            Assert.Null(await store.TryAcquireAsync(expiredKey, expiredItem.Revision, RetentionLeaseKind.Access, expiredItem.ExpiresAt, CancellationToken.None));
            var expiredDenied = Assert.IsType<RetentionCatalogItem>(store.Find(expiredKey));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, expiredDenied.State);
            Assert.Equal(expiredItem.ExpiresAt, expiredDenied.ReadDeniedAt);
            Assert.Equal(expiredItem.Revision + 1, expiredDenied.Revision);
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_items WHERE item_id='{expiredDenied.ItemId}' AND error_code IS NULL;"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData("already_denied")]
    [InlineData("lifecycle_denied")]
    [InlineData("expired_expiring")]
    public async Task TryAcquireAsync_FirstResultPrecedenceBeatsBusySdkSourceProof(string scenario)
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-sdk-read-precedence-{Guid.NewGuid():N}.sqlite");
        var parent = Path.Combine(Path.GetTempPath(), $"retention-sdk-read-precedence-{Guid.NewGuid():N}");
        try
        {
            var activatedAt = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
            var time = new MutableTimeProvider(activatedAt);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "CREATE TABLE monitor_analysis_runs(id INTEGER PRIMARY KEY, requested_at TEXT NOT NULL, retention_owner_token BLOB NOT NULL); " +
                    "INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(7,'2026-07-19T01:02:03.0000000+00:00',zeroblob(32));";
                command.ExecuteNonQuery();
            }

            var store = new RetentionCatalogStore(context, time);
            var reservation = store.ReserveAnalysisSdkDirectory(7, parent);
            Directory.CreateDirectory(reservation.ChildLocator);
            var markerPath = Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName);
            File.WriteAllBytes(markerPath, reservation.OwnershipMarker);
            var activation = store.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
                reservation,
                reservation.OwnershipMarker,
                exclusivelyCreatedEmptyChild: true);
            var operationLease = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
            Assert.Equal(RetentionMutationDisposition.Applied, store.ReleaseAnalysisSdkDirectoryOperationLease(operationLease));

            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.AnalysisSdkDirectory, reservation.CaptureId);
            var readable = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            using (Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Access, activatedAt, CancellationToken.None)))
            {
            }

            using var markerLock = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var readableBeforeBusyProof = ItemRowDump(path, readable.ItemId);
            Assert.Null(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Access, activatedAt, CancellationToken.None));
            Assert.Equal(readableBeforeBusyProof, ItemRowDump(path, readable.ItemId));

            switch (scenario)
            {
                case "already_denied":
                    Execute(path, $"UPDATE retention_items SET state='retained_by_policy',read_denied_at='{activatedAt:O}',revision=revision+1 WHERE item_id='{readable.ItemId}';");
                    break;
                case "lifecycle_denied":
                    Execute(path, $"UPDATE retention_items SET state='deleting',revision=revision+1 WHERE item_id='{readable.ItemId}';");
                    break;
                case "expired_expiring":
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            var classified = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var classifiedBefore = ItemRowDump(path, classified.ItemId);
            var at = scenario == "expired_expiring" ? classified.ExpiresAt : activatedAt;
            time.Advance(at - time.GetUtcNow());

            Assert.Null(await store.TryAcquireAsync(key, classified.Revision, RetentionLeaseKind.Access, at, CancellationToken.None));

            if (scenario == "expired_expiring")
            {
                var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
                Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
                Assert.Equal(classified.ExpiresAt, denied.ReadDeniedAt);
                Assert.Equal(classified.Revision + 1, denied.Revision);
                Assert.Equal(classified.ExpiresAt.ToString("O"), Scalar<string>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{denied.ItemId}';"));
                Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_items WHERE item_id='{denied.ItemId}' AND error_code IS NULL;"));
            }
            else
            {
                Assert.Equal(classifiedBefore, ItemRowDump(path, classified.ItemId));
            }
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
            Delete(path);
        }
    }

    [Fact]
    public async Task ReadGate_ConcurrentExpiryDenialsApplyExactlyOneRevisionIncrement()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = RawKey(path, store);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            time.Advance(item.ExpiresAt - time.GetUtcNow());
            using var barrier = new Barrier(2);
            var racers = new[]
            {
                Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None);
                }),
                Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None);
                }),
            };
            var results = await Task.WhenAll(racers);
            Assert.All(results, result => Assert.Null(result));

            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(item.Revision + 1, denied.Revision);
            Assert.Equal(item.ExpiresAt, denied.ReadDeniedAt);
            Assert.Null(await store.TryAcquireAsync(key, denied.Revision, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None));
            Assert.Equal(denied, Assert.IsType<RetentionCatalogItem>(store.Find(key)));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(ReadDenialPath.Expiry, DenialCasGuard.Revision)]
    [InlineData(ReadDenialPath.Expiry, DenialCasGuard.State)]
    [InlineData(ReadDenialPath.Expiry, DenialCasGuard.ReadDeniedAt)]
    [InlineData(ReadDenialPath.MissingSource, DenialCasGuard.Revision)]
    [InlineData(ReadDenialPath.MissingSource, DenialCasGuard.State)]
    [InlineData(ReadDenialPath.MissingSource, DenialCasGuard.ReadDeniedAt)]
    public async Task ReadAsync_DenialCasLosesExactGuard_ReturnsDeniedWithoutRetryOrBroaderMutation(
        ReadDenialPath denialPath,
        DenialCasGuard guard)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 640, 641);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var initialized = new RetentionCatalogStore(path, time);
            initialized.CreateSchema();
            var key = RawKeyFor(initialized, 640);
            var item = Assert.IsType<RetentionCatalogItem>(initialized.Find(key));
            var unrelatedKey = RawKeyFor(initialized, 641);
            var unrelated = Assert.IsType<RetentionCatalogItem>(initialized.Find(unrelatedKey));
            var at = denialPath == ReadDenialPath.Expiry ? item.ExpiresAt : time.GetUtcNow();
            time.Advance(at - time.GetUtcNow());
            if (denialPath == ReadDenialPath.MissingSource)
            {
                Execute(path, "DELETE FROM raw_records WHERE id=640;");
            }

            var sourceBefore = SourceRowDump(path, 640);
            var unrelatedSourceBefore = SourceRowDump(path, 641);
            var unrelatedItemBefore = ItemRowDump(path, unrelated.ItemId);
            var competingDenialAt = item.CapturedAt.AddMinutes(1);
            var checkpointCalls = 0;
            string? expectedTargetAfterCheckpoint = null;
            string? expectedAuthorityAfterCheckpoint = null;
            var store = new RetentionCatalogStore(
                path,
                time,
                denialCasCheckpoint: (connection, transaction, itemId) =>
                {
                    checkpointCalls++;
                    Assert.Equal(item.ItemId, itemId);
                    using var mutation = connection.CreateCommand();
                    mutation.Transaction = transaction;
                    mutation.CommandText = guard switch
                    {
                        DenialCasGuard.Revision =>
                            "UPDATE retention_items SET revision=revision+1 WHERE item_id=$item;",
                        DenialCasGuard.State =>
                            "UPDATE retention_items SET state=$state WHERE item_id=$item;",
                        DenialCasGuard.ReadDeniedAt =>
                            "UPDATE retention_items SET read_denied_at=$denied WHERE item_id=$item;",
                        _ => throw new ArgumentOutOfRangeException(nameof(guard)),
                    };
                    mutation.Parameters.AddWithValue("$item", itemId);
                    mutation.Parameters.AddWithValue(
                        "$state",
                        denialPath == ReadDenialPath.Expiry ? "retained_by_policy" : "deleting");
                    mutation.Parameters.AddWithValue("$denied", competingDenialAt.ToString("O", CultureInfo.InvariantCulture));
                    Assert.Equal(1, mutation.ExecuteNonQuery());
                    expectedTargetAfterCheckpoint = FullRowDump(
                        connection,
                        transaction,
                        "retention_items",
                        "item_id",
                        itemId);
                    expectedAuthorityAfterCheckpoint = RetentionAuthorityDump(connection, transaction);
                });
            var selectorCalls = 0;

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, at, item.Revision),
                (_, _, _, _) =>
                {
                    selectorCalls++;
                    return ValueTask.FromResult<string?>("value");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.LifecycleDenied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(1, checkpointCalls);
            Assert.Equal(0, selectorCalls);
            Assert.NotNull(expectedTargetAfterCheckpoint);
            Assert.Equal(expectedTargetAfterCheckpoint, ItemRowDump(path, item.ItemId));
            Assert.NotNull(expectedAuthorityAfterCheckpoint);
            Assert.Equal(expectedAuthorityAfterCheckpoint, RetentionAuthorityDump(path));
            Assert.Equal(unrelatedItemBefore, ItemRowDump(path, unrelated.ItemId));
            Assert.Equal(sourceBefore, SourceRowDump(path, 640));
            Assert.Equal(unrelatedSourceBefore, SourceRowDump(path, 641));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));

            var stale = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(guard == DenialCasGuard.Revision ? item.Revision + 1 : item.Revision, stale.Revision);
            Assert.Equal(
                guard == DenialCasGuard.State
                    ? denialPath == ReadDenialPath.Expiry
                        ? RetentionItemLifecycle.RetainedByPolicy
                        : RetentionItemLifecycle.Deleting
                    : item.State,
                stale.State);
            Assert.Equal(
                guard == DenialCasGuard.ReadDeniedAt ? competingDenialAt : null,
                stale.ReadDeniedAt);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ReadGate_ImmediateWriterConflictReturnsBusyWithoutCatalogOrSourceMutation()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = RawKey(path, store);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var itemBefore = ItemRowDump(path, item.ItemId);
            var sourceBefore = SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture));
            using var blocker = Open(path);
            using var blockerTransaction = blocker.BeginTransaction(deferred: false);

            var result = await store.TryAcquireAsync(
                key,
                item.Revision,
                RetentionLeaseKind.Access,
                time.GetUtcNow(),
                CancellationToken.None);

            blockerTransaction.Rollback();
            Assert.Null(result);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(sourceBefore, SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture)));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{item.ItemId}';"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(RetentionLeaseKind.Access, "stale")]
    [InlineData(RetentionLeaseKind.Access, "future")]
    [InlineData(RetentionLeaseKind.Operation, "stale")]
    [InlineData(RetentionLeaseKind.Operation, "future")]
    public async Task ReadGate_PostBeginTrustedClockAtExactExpiryDeniesCallerTimeWithoutGrantingLease(
        RetentionLeaseKind leaseKind,
        string callerTime)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var initializing = new RetentionCatalogStore(path);
            initializing.CreateSchema();
            var key = RawKey(path, initializing);
            var item = Assert.IsType<RetentionCatalogItem>(initializing.Find(key));
            var time = new MutableTimeProvider(item.ExpiresAt.AddTicks(-1));
            using var checkpoint = new DirectReadBeginClockGate();
            var store = new RetentionCatalogStore(path, time, checkpoint);
            var suppliedAt = callerTime == "stale"
                ? item.CapturedAt.AddDays(-1)
                : item.ExpiresAt.AddYears(1);
            var sourceBefore = SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture));

            var acquisition = Task.Run(async () => await store.TryAcquireAsync(
                key,
                item.Revision,
                leaseKind,
                suppliedAt,
                CancellationToken.None));
            try
            {
                await checkpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                time.Advance(TimeSpan.FromTicks(1));
            }
            finally
            {
                checkpoint.Resume();
            }
            var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, checkpoint.Count);
            Assert.Null(result);
            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
            Assert.Equal(item.ExpiresAt, denied.ReadDeniedAt);
            Assert.Equal(item.Revision + 1, denied.Revision);
            Assert.Equal(
                item.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                Scalar<string>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{item.ItemId}';"));
            Assert.Equal(sourceBefore, SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture)));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{item.ItemId}';"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(RetentionLeaseKind.Access, "stale")]
    [InlineData(RetentionLeaseKind.Access, "future")]
    [InlineData(RetentionLeaseKind.Operation, "stale")]
    [InlineData(RetentionLeaseKind.Operation, "future")]
    public async Task ReadGate_PostBeginTrustedClockGrantsFullLeaseDespiteCallerTime(
        RetentionLeaseKind leaseKind,
        string callerTime)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var initializing = new RetentionCatalogStore(path);
            initializing.CreateSchema();
            var key = RawKey(path, initializing);
            var item = Assert.IsType<RetentionCatalogItem>(initializing.Find(key));
            var transactionStartedAt = item.ExpiresAt.AddMinutes(-10);
            var admittedAt = transactionStartedAt.AddSeconds(30);
            var time = new MutableTimeProvider(transactionStartedAt);
            using var checkpoint = new DirectReadBeginClockGate();
            var store = new RetentionCatalogStore(path, time, checkpoint);
            var itemBefore = ItemRowDump(path, item.ItemId);
            var sourceBefore = SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture));
            var suppliedAt = callerTime == "stale"
                ? item.CapturedAt.AddDays(-1)
                : item.ExpiresAt.AddYears(1);

            var acquisition = Task.Run(async () => await store.TryAcquireAsync(
                key,
                item.Revision,
                leaseKind,
                suppliedAt,
                CancellationToken.None));
            try
            {
                await checkpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                time.Advance(admittedAt - time.GetUtcNow());
            }
            finally
            {
                checkpoint.Resume();
            }
            using var lease = Assert.IsType<RetentionReadLeaseHandle>(await acquisition.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, checkpoint.Count);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(sourceBefore, SourceRowDump(path, long.Parse(key.SourceItemId, CultureInfo.InvariantCulture)));
            Assert.Equal(
                admittedAt.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture),
                Scalar<string>(path, $"SELECT expires_at FROM retention_leases WHERE item_id='{item.ItemId}';"));
            Assert.Equal(
                leaseKind == RetentionLeaseKind.Access ? "access" : "operation",
                Scalar<string>(path, $"SELECT lease_kind FROM retention_leases WHERE item_id='{item.ItemId}';"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ReadGate_PinnedSourceFailuresUseIrreversibleSourceDenialsNotHistoricalExpiry(bool pastHistoricalExpiry, bool sourceMissing)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 621, 629);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = RawKeyFor(store, 621);
            PinItem(path, Assert.IsType<RetentionCatalogItem>(store.Find(key)).ItemId);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var sourceBefore = SourceRowDump(path, 621);
            var unrelatedSourceBefore = SourceRowDump(path, 629);
            var unrelatedItem = Assert.IsType<RetentionCatalogItem>(store.Find(RawKeyFor(store, 629)));
            var unrelatedCatalogBefore = ItemRowDump(path, unrelatedItem.ItemId);
            var catalogReceiptBefore = Scalar<string>(path, $"SELECT quote(ownership_receipt) FROM retention_items WHERE item_id='{item.ItemId}';");
            if (sourceMissing)
            {
                Execute(path, "DELETE FROM raw_records WHERE id=621;");
            }
            else
            {
                ReplaceRawRecordOwnerToken(path, 621);
            }
            var failedSource = SourceRowDump(path, 621);
            if (sourceMissing) Assert.Equal("raw_records:absent", failedSource);
            else Assert.NotEqual(sourceBefore, failedSource);
            var at = pastHistoricalExpiry ? item.ExpiresAt.AddDays(1) : item.CapturedAt;
            time.Advance(at - time.GetUtcNow());

            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, at, CancellationToken.None));

            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.DeletionFailed, denied.State);
            Assert.Equal(at, denied.ReadDeniedAt);
            Assert.Equal(item.Revision + 1, denied.Revision);
            Assert.Equal(
                sourceMissing ? "retention_unexpected_source_missing" : "retention_ownership_mismatch",
                Scalar<string>(path, $"SELECT error_code FROM retention_items WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_items WHERE item_id='{denied.ItemId}' AND queued_at IS NULL;"));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(catalogReceiptBefore, Scalar<string>(path, $"SELECT quote(ownership_receipt) FROM retention_items WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(failedSource, SourceRowDump(path, 621));
            Assert.Equal(unrelatedSourceBefore, SourceRowDump(path, 629));
            Assert.Equal(unrelatedCatalogBefore, ItemRowDump(path, unrelatedItem.ItemId));

            var deniedCatalog = ItemRowDump(path, denied.ItemId);
            Assert.Null(await store.TryAcquireAsync(key, denied.Revision, RetentionLeaseKind.Access, at, CancellationToken.None));
            Assert.Equal(denied, Assert.IsType<RetentionCatalogItem>(store.Find(key)));
            Assert.Equal(deniedCatalog, ItemRowDump(path, denied.ItemId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ReadAsync_PinnedSourceFailuresUseIrreversibleSourceDenialsNotHistoricalExpiry(bool pastHistoricalExpiry, bool sourceMissing)
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 622, 628);
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = RawKeyFor(store, 622);
            PinItem(path, Assert.IsType<RetentionCatalogItem>(store.Find(key)).ItemId);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var sourceBefore = SourceRowDump(path, 622);
            var unrelatedSourceBefore = SourceRowDump(path, 628);
            var unrelatedItem = Assert.IsType<RetentionCatalogItem>(store.Find(RawKeyFor(store, 628)));
            var unrelatedCatalogBefore = ItemRowDump(path, unrelatedItem.ItemId);
            var catalogReceiptBefore = Scalar<string>(path, $"SELECT quote(ownership_receipt) FROM retention_items WHERE item_id='{item.ItemId}';");
            if (sourceMissing)
            {
                Execute(path, "DELETE FROM raw_records WHERE id=622;");
            }
            else
            {
                ReplaceRawRecordOwnerToken(path, 622);
            }
            var failedSource = SourceRowDump(path, 622);
            if (sourceMissing) Assert.Equal("raw_records:absent", failedSource);
            else Assert.NotEqual(sourceBefore, failedSource);
            var at = pastHistoricalExpiry ? item.ExpiresAt.AddDays(1) : item.CapturedAt;
            time.Advance(at - time.GetUtcNow());

            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, at, item.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("value"),
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.LifecycleDenied, result.Disposition);
            Assert.Null(result.Lease);

            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.DeletionFailed, denied.State);
            Assert.Equal(at, denied.ReadDeniedAt);
            Assert.Equal(item.Revision + 1, denied.Revision);
            Assert.Equal(
                sourceMissing ? "retention_unexpected_source_missing" : "retention_ownership_mismatch",
                Scalar<string>(path, $"SELECT error_code FROM retention_items WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(1L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_items WHERE item_id='{denied.ItemId}' AND queued_at IS NULL;"));
            Assert.Equal(0L, Scalar<long>(path, $"SELECT COUNT(*) FROM retention_leases WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(catalogReceiptBefore, Scalar<string>(path, $"SELECT quote(ownership_receipt) FROM retention_items WHERE item_id='{denied.ItemId}';"));
            Assert.Equal(failedSource, SourceRowDump(path, 622));
            Assert.Equal(unrelatedSourceBefore, SourceRowDump(path, 628));
            Assert.Equal(unrelatedCatalogBefore, ItemRowDump(path, unrelatedItem.ItemId));

            var deniedCatalog = ItemRowDump(path, denied.ItemId);
            var retry = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, at, denied.Revision),
                static (_, _, _, _) => ValueTask.FromResult<string?>("value"),
                CancellationToken.None);
            Assert.Equal(RetentionReadDisposition.LifecycleDenied, retry.Disposition);
            Assert.Equal(deniedCatalog, ItemRowDump(path, denied.ItemId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_CommitBoundarySamplesClockOnceAndQueuesAtThatInstant()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            InsertLegacyRawRows(path, 623);
            var time = new SequencedTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            var key = RawKeyFor(store, 623);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var requestNow = item.ExpiresAt.AddTicks(-1);

            time.Schedule(requestNow, item.ExpiresAt);
            time.ResetReadCount();
            var selectorCallCount = 0;
            var result = await store.ReadAsync(
                new RetentionReadRequest(key, RetentionReadKind.Access, requestNow, item.Revision),
                (_, _, _, _) =>
                {
                    Interlocked.Increment(ref selectorCallCount);
                    return ValueTask.FromResult<string?>("value");
                },
                CancellationToken.None);

            Assert.Equal(RetentionReadDisposition.LifecycleDenied, result.Disposition);
            Assert.Null(result.Lease);
            Assert.Equal(2, time.ReadCount);
            Assert.Equal(0, selectorCallCount);
            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(item.ExpiresAt, denied.ReadDeniedAt);
            Assert.Equal(item.ExpiresAt.ToString("O"), Scalar<string>(path, $"SELECT queued_at FROM retention_items WHERE item_id='{denied.ItemId}';"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Registration_PinnedAnalysisRunRawAfterHistoricalExpiryRemainsWritable()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            Execute(path, "INSERT INTO monitor_analysis_runs(id,trace_id,raw_record_id,span_id,focus,status,requested_at,started_at,completed_at,result_markdown,error_message) VALUES(8,'unrelated-analysis-trace',NULL,NULL,'trace','completed','2026-07-12T01:03:04.0000000+00:00',NULL,NULL,'unrelated synthetic result',NULL);");
            var time = new SequencedTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            long runId;
            DateTimeOffset requestedAt;
            long? rawRecordId;
            string? spanId;
            byte[] ownerToken;
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id,requested_at,raw_record_id,span_id,retention_owner_token FROM monitor_analysis_runs ORDER BY id LIMIT 1;";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                runId = reader.GetInt64(0);
                requestedAt = DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                rawRecordId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                spanId = reader.IsDBNull(3) ? null : reader.GetString(3);
                ownerToken = reader.GetFieldValue<byte[]>(4);
            }
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.AnalysisRunRaw, runId.ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            PinItem(path, item.ItemId);
            var unrelatedKey = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.AnalysisRunRaw, "8");
            var unrelatedItem = Assert.IsType<RetentionCatalogItem>(store.Find(unrelatedKey));
            var itemBefore = ItemRowDump(path, item.ItemId);
            var sourceBefore = FullRowDump(path, "monitor_analysis_runs", "id", runId);
            var unrelatedItemBefore = ItemRowDump(path, unrelatedItem.ItemId);
            var unrelatedSourceBefore = FullRowDump(path, "monitor_analysis_runs", "id", 8L);
            time.Schedule(item.ExpiresAt.AddTicks(1));
            time.ResetReadCount();

            using var mutation = store.OpenMutationConnection();
            using var transaction = mutation.BeginTransaction();
            var exception = Record.Exception(() => store.AssertAnalysisRunRawWritable(mutation, transaction, runId, requestedAt, rawRecordId, spanId, ownerToken));
            transaction.Commit();

            Assert.Null(exception);
            Assert.Equal(1, time.ReadCount);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(sourceBefore, FullRowDump(path, "monitor_analysis_runs", "id", runId));
            Assert.Equal(unrelatedItemBefore, ItemRowDump(path, unrelatedItem.ItemId));
            Assert.Equal(unrelatedSourceBefore, FullRowDump(path, "monitor_analysis_runs", "id", 8L));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Registration_ExpiringAnalysisRunRawAtExactBoundaryIsNotWritable()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            var time = new SequencedTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            long runId;
            DateTimeOffset requestedAt;
            long? rawRecordId;
            string? spanId;
            byte[] ownerToken;
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id,requested_at,raw_record_id,span_id,retention_owner_token FROM monitor_analysis_runs ORDER BY id LIMIT 1;";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                runId = reader.GetInt64(0);
                requestedAt = DateTimeOffset.Parse(reader.GetString(1), null, DateTimeStyles.RoundtripKind);
                rawRecordId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                spanId = reader.IsDBNull(3) ? null : reader.GetString(3);
                ownerToken = reader.GetFieldValue<byte[]>(4);
            }
            var key = new RetentionOwnershipKey(
                store.StoreInstanceId,
                RetentionStoreKind.AnalysisRunRaw,
                runId.ToString(CultureInfo.InvariantCulture));
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var itemBefore = ItemRowDump(path, item.ItemId);
            var sourceBefore = FullRowDump(path, "monitor_analysis_runs", "id", runId);
            time.Schedule(item.ExpiresAt);
            time.ResetReadCount();

            using var mutation = store.OpenMutationConnection();
            using var transaction = mutation.BeginTransaction();
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() =>
                store.AssertAnalysisRunRawWritable(
                    mutation,
                    transaction,
                    runId,
                    requestedAt,
                    rawRecordId,
                    spanId,
                    ownerToken));
            transaction.Commit();

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.Equal(1, time.ReadCount);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(sourceBefore, FullRowDump(path, "monitor_analysis_runs", "id", runId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Registration_PinnedSessionEventContentAfterHistoricalExpiryRemainsRegistrable()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            var time = new SequencedTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 30, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            string eventId;
            string contentKind;
            DateTimeOffset capturedAt;
            DateTimeOffset expiresAt;
            string sessionId;
            string? runId;
            string sourceAdapter;
            string sourceEventId;
            byte[] ownerToken;
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,c.retention_owner_token,
                           e.session_id,e.run_id,e.source_adapter,e.source_event_id
                    FROM session_event_content c
                    JOIN session_events e ON e.event_id=c.event_id
                    ORDER BY c.event_id
                    LIMIT 1;
                    """;
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                eventId = reader.GetString(0);
                contentKind = reader.GetString(1);
                capturedAt = DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);
                expiresAt = DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);
                ownerToken = reader.GetFieldValue<byte[]>(4);
                sessionId = reader.GetString(5);
                runId = reader.IsDBNull(6) ? null : reader.GetString(6);
                sourceAdapter = reader.GetString(7);
                sourceEventId = reader.GetString(8);
            }
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.SessionEventContent, eventId);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            PinItem(path, item.ItemId);
            var itemBefore = ItemRowDump(path, item.ItemId);
            var contentBefore = FullRowDump(path, "session_event_content", "event_id", eventId);
            var checkAt = item.ExpiresAt.AddTicks(1);
            time.Schedule(checkAt, checkAt);
            time.ResetReadCount();

            using var mutation = store.OpenMutationConnection();
            using var transaction = mutation.BeginTransaction();
            var exception = Record.Exception(() => store.RegisterSessionEventContent(mutation, transaction, eventId, contentKind, capturedAt, expiresAt, sessionId, runId, sourceAdapter, sourceEventId, ownerToken));
            transaction.Commit();

            Assert.Null(exception);
            Assert.Equal(2, time.ReadCount);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(contentBefore, FullRowDump(path, "session_event_content", "event_id", eventId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Registration_ExpiringSessionEventContentAtExactBoundaryIsNotRegistrable()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            var time = new SequencedTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 30, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time);
            store.CreateSchema();
            string eventId;
            string contentKind;
            DateTimeOffset capturedAt;
            DateTimeOffset expiresAt;
            string sessionId;
            string? runId;
            string sourceAdapter;
            string sourceEventId;
            byte[] ownerToken;
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,c.retention_owner_token,
                           e.session_id,e.run_id,e.source_adapter,e.source_event_id
                    FROM session_event_content c
                    JOIN session_events e ON e.event_id=c.event_id
                    ORDER BY c.event_id
                    LIMIT 1;
                    """;
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                eventId = reader.GetString(0);
                contentKind = reader.GetString(1);
                capturedAt = DateTimeOffset.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind);
                expiresAt = DateTimeOffset.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind);
                ownerToken = reader.GetFieldValue<byte[]>(4);
                sessionId = reader.GetString(5);
                runId = reader.IsDBNull(6) ? null : reader.GetString(6);
                sourceAdapter = reader.GetString(7);
                sourceEventId = reader.GetString(8);
            }
            var key = new RetentionOwnershipKey(
                store.StoreInstanceId,
                RetentionStoreKind.SessionEventContent,
                eventId);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var itemBefore = ItemRowDump(path, item.ItemId);
            var contentBefore = FullRowDump(path, "session_event_content", "event_id", eventId);
            time.Schedule(item.ExpiresAt, item.ExpiresAt);
            time.ResetReadCount();

            using var mutation = store.OpenMutationConnection();
            using var transaction = mutation.BeginTransaction();
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() =>
                store.RegisterSessionEventContent(
                    mutation,
                    transaction,
                    eventId,
                    contentKind,
                    capturedAt,
                    expiresAt,
                    sessionId,
                    runId,
                    sourceAdapter,
                    sourceEventId,
                    ownerToken));
            transaction.Commit();

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.Equal(2, time.ReadCount);
            Assert.Equal(itemBefore, ItemRowDump(path, item.ItemId));
            Assert.Equal(contentBefore, FullRowDump(path, "session_event_content", "event_id", eventId));
        }
        finally
        {
            Delete(path);
        }
    }

    private static RetentionOwnershipKey RawKeyFor(RetentionCatalogStore store, long rawId) =>
        new(store.StoreInstanceId, RetentionStoreKind.RawRecord, rawId.ToString());

    private static DateTimeOffset PublishedLeaseExpiry(RetentionReadGrant grant)
    {
        using var publication = grant.EnterLeasePublication();
        return publication.LeaseExpiresAt;
    }

    private static bool GrantIsUsable(
        string path,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        var usable = RetentionCatalogStore.IsGrantUsable(
            connection,
            transaction,
            grant,
            rawRecordId,
            at);
        transaction.Rollback();
        return usable;
    }

    private static void PinItem(string path, string itemId) =>
        Execute(path, $"UPDATE retention_items SET state='retained_by_policy', revision=revision+1 WHERE item_id='{itemId}';");
    private static void InsertLegacyRawRows(string path, params long[] ids)
    {
        foreach (var id in ids)
        {
            Execute(path,
                "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version) " +
                $"VALUES({id},'raw-otlp','fixture-monitor-v5-trace','2026-07-12T00:00:00.0000000+00:00',NULL,'{{\"fixture\":true}}',1);");
        }
    }
    private static void ReplaceRawRecordOwnerToken(string path, long rawId)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        byte[] replacement;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT retention_owner_token FROM raw_records WHERE id=$id;";
            read.Parameters.AddWithValue("$id", rawId);
            replacement = Assert.IsType<byte[]>(read.ExecuteScalar()).ToArray();
        }
        replacement[0] ^= byte.MaxValue;
        using (var replace = connection.CreateCommand())
        {
            replace.Transaction = transaction;
            replace.CommandText =
                "DROP TRIGGER retention_raw_records_token_immutable; " +
                "UPDATE raw_records SET retention_owner_token=$token WHERE id=$id; " +
                "CREATE TRIGGER retention_raw_records_token_immutable BEFORE UPDATE OF retention_owner_token ON raw_records WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;";
            replace.Parameters.AddWithValue("$token", replacement);
            replace.Parameters.AddWithValue("$id", rawId);
            replace.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    private static string ItemRowDump(string path, string itemId) => FullRowDump(path, "retention_items", "item_id", itemId);
    private static string SourceRowDump(string path, long rawId) => FullRowDump(path, "raw_records", "id", rawId);
    private static string FullRowDump(string path, string table, string keyColumn, object keyValue)
    {
        using var connection = Open(path);
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"quote({column})"))} FROM {table} WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$key", keyValue);
        using var rows = command.ExecuteReader();
        return rows.Read()
            ? string.Join("|", columns.Select((column, index) => $"{column}={rows.GetString(index)}"))
            : $"{table}:absent";
    }

    private static string FullRowDump(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string keyColumn,
        object keyValue)
    {
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"quote({column})"))} FROM {table} WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$key", keyValue);
        using var rows = command.ExecuteReader();
        return rows.Read()
            ? string.Join("|", columns.Select((column, index) => $"{column}={rows.GetString(index)}"))
            : $"{table}:absent";
    }

    private static string RetentionAuthorityDump(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        return RetentionAuthorityDump(connection, transaction);
    }

    private static string RetentionAuthorityDump(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var tables = new List<string>();
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.Transaction = transaction;
            tableCommand.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'retention_%' ORDER BY name;";
            using var tableReader = tableCommand.ExecuteReader();
            while (tableReader.Read())
            {
                tables.Add(tableReader.GetString(0));
            }
        }

        var dump = new List<string>();
        foreach (var table in tables)
        {
            var columns = new List<string>();
            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = $"PRAGMA table_info({table});";
                using var columnReader = pragma.ExecuteReader();
                while (columnReader.Read())
                {
                    columns.Add(columnReader.GetString(1));
                }
            }

            var rows = new List<string>();
            using (var rowCommand = connection.CreateCommand())
            {
                rowCommand.Transaction = transaction;
                rowCommand.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"quote({column})"))} FROM {table};";
                using var rowReader = rowCommand.ExecuteReader();
                while (rowReader.Read())
                {
                    rows.Add(string.Join("|", columns.Select((column, index) => $"{column}={rowReader.GetString(index)}")));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            dump.Add($"{table}:[{string.Join(";", rows)}]");
        }
        return string.Join(Environment.NewLine, dump);
    }

    private static IReadOnlyList<ItemRow> ReadItems(string path)
    {
        using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,store_kind,source_item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items ORDER BY store_kind,source_item_id;";
        using var reader = command.ExecuteReader(); var rows = new List<ItemRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }

    private static void CreateActualAnalysisRaw(string path) => Execute(path, """
        CREATE TABLE monitor_analysis_runs (id INTEGER PRIMARY KEY, trace_id TEXT NOT NULL, raw_record_id INTEGER NULL, span_id TEXT NULL, focus TEXT NOT NULL, status TEXT NOT NULL, requested_at TEXT NOT NULL, started_at TEXT NULL, completed_at TEXT NULL, result_markdown TEXT NULL, error_message TEXT NULL);
        CREATE TABLE monitor_analysis_events (id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL, event_type TEXT NOT NULL, message TEXT NOT NULL, occurred_at TEXT NOT NULL);
        INSERT INTO monitor_analysis_runs VALUES(7,'fixture-trace',1,NULL,'trace','completed','2026-07-12T01:02:03.0000000+00:00',NULL,NULL,'synthetic result',NULL);
        """);
    private static RetentionOwnershipKey RawKey(string path, RetentionCatalogStore store) => new(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
    private static void PromoteToQueued(string path, string itemId, DateTimeOffset now) => Execute(path, $"UPDATE retention_items SET state='deletion_queued', read_denied_at='{now:O}', queued_at='{now:O}', revision=revision+1 WHERE item_id='{itemId}';");
    private static string CopyFixture(string component, string file) { var path = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", component, file); var copy = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}.sqlite"); File.Copy(path, copy); return copy; }
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA foreign_keys=ON;"; q.ExecuteNonQuery(); return c; }
    private static T Scalar<T>(string path, string sql) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; return (T)Convert.ChangeType(q.ExecuteScalar()!, typeof(T)); }
    private static bool TableExists(string path, string name) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);"; q.Parameters.AddWithValue("$name", name); return Convert.ToInt64(q.ExecuteScalar()) == 1; }
    private static void Execute(string path, string sql) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var parameter in parameters) q.Parameters.AddWithValue(parameter.Name, parameter.Value); q.ExecuteNonQuery(); }
    private static void Delete(string path) { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); }
    private sealed record ItemRow(string Id, string Kind, string Source, string CapturedAt, string ExpiresAt, string State, long Revision, string? ReadDeniedAt);

    public enum ReadDenialPath { Expiry, MissingSource }
    public enum DenialCasGuard { Revision, State, ReadDeniedAt }

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

    private sealed class DirectReadBeginClockGate : IRetentionReadAdmissionCheckpoint, IDisposable
    {
        private readonly ManualResetEventSlim resumed = new();

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count { get; private set; }

        public void Reached(RetentionReadAdmissionCheckpoint checkpoint)
        {
            Assert.Equal(RetentionReadAdmissionCheckpoint.AfterImmediateTransactionBegunBeforeClockSample, checkpoint);
            Count++;
            Entered.TrySetResult();
            if (!resumed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("direct read admission checkpoint was not resumed");
        }

        internal void Resume() => resumed.Set();

        public void Dispose()
        {
            resumed.Set();
            resumed.Dispose();
        }
    }
}

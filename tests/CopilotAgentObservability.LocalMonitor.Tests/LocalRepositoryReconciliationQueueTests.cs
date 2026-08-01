using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryReconciliationQueueTests
{
    [Fact]
    public void ClaimNext_LeasesTheOldestPendingItemForExactlyThirtySeconds()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        Execute(connection, """
            INSERT INTO local_repository_reconciliation_queue VALUES
            ('01900000-0000-7000-8000-000000000001',17,'payload_sha256','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','local-repository-catalog:1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','pending',0,NULL,NULL,NULL,'2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00');
            """);
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, new FixedTimeProvider(at), static () => new string('a', 64));

        var lease = store.TryClaimNext(at).Lease;

        Assert.NotNull(lease);
        Assert.Equal(17, lease.RawRecordId);
        Assert.Equal(1, lease.AttemptCount);
        Assert.Equal(at.AddSeconds(30), lease.LeaseExpiresAt);
        Assert.Equal(new string('a', 64), lease.LeaseToken);
    }

    [Fact]
    public void ClaimNext_WaitsForTheExactFiveSecondBoundaryAndNeverClaimsLiveLeases()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000002", 2, "waiting_session", "2026-08-01T00:00:00.0000000+00:00");
        InsertQueue(connection, "01900000-0000-7000-8000-000000000003", 3, "leased", "2026-08-01T00:00:00.0000000+00:00", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "2026-08-01T01:00:00.0000000+00:00");
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('b', 64));
        var before = new DateTimeOffset(2026, 8, 1, 0, 0, 4, 999, TimeSpan.Zero);

        Assert.Null(store.TryClaimNext(before).Lease);
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(before.AddMilliseconds(1)).Lease);

        Assert.Equal(2, lease.RawRecordId);
        Assert.Equal(0, ScalarLong(connection, "SELECT attempt_count FROM local_repository_reconciliation_queue WHERE raw_record_id=3;"));
    }

    [Fact]
    public void Recovery_OnlyReturnsExpiredLeasesToPendingAndPreservesAttempts()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000004", 4, "leased", "2026-08-01T00:00:00.0000000+00:00", new string('c', 64), "2026-08-01T00:00:00.0000000+00:00", attempts: 7);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000005", 5, "leased", "2026-08-01T00:00:00.0000000+00:00", new string('d', 64), "2026-08-01T00:00:01.0000000+00:00", attempts: 9);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.RecoverExpiredLeases(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("pending", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=4;"));
        Assert.Equal(7, ScalarLong(connection, "SELECT attempt_count FROM local_repository_reconciliation_queue WHERE raw_record_id=4;"));
        Assert.Equal("leased", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=5;"));
    }

    [Fact]
    public void Finalizer_IsBoundToTheCallersTransactionAndRollsBackWithIt()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000006", 6, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('e', 64));
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);

        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.TryComplete(connection, transaction, lease, at));
            transaction.Rollback();
        }

        Assert.Equal("leased", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=6;"));
    }

    [Fact]
    public void Finalizer_RejectsAForeignDatabaseBeforeChangingTheQueue()
    {
        using var first = new TestDatabase();
        using var second = new TestDatabase();
        using var firstConnection = OpenCatalog(first.Path);
        using var secondConnection = OpenCatalog(second.Path);
        InsertQueue(firstConnection, "01900000-0000-7000-8000-000000000061", 61, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(first.Path, leaseTokenFactory: static () => new string('6', 64));
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);

        using var transaction = secondConnection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => store.TryComplete(secondConnection, transaction, lease, at));
        transaction.Rollback();

        Assert.Equal("leased", ScalarText(firstConnection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=61;"));
    }

    [Fact]
    public void EveryTaskSixFinalizer_RollsBackWithTheCallerTransaction()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000009", 9, "pending", "2026-08-01T00:00:00.0000000+00:00");
        InsertQueue(connection, "01900000-0000-7000-8000-000000000010", 10, "pending", "2026-08-01T00:00:00.0000000+00:00");
        InsertQueue(connection, "01900000-0000-7000-8000-000000000011", 11, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('2', 64));
        var first = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var second = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var third = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);

        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.TryComplete(connection, transaction, first, at));
            Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.TryWaitForSession(connection, transaction, second, at));
            Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.TryFailTerminal(connection, transaction, third, at, "catalog_parse_failure"));
            transaction.Rollback();
        }

        Assert.Equal(3, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='leased';"));
    }

    [Fact]
    public void OwnedTerminalTransition_RejectsStolenOrExpiredTokens()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000007", 7, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('f', 64));
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);

        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, store.RecordInputUnavailable(lease with { LeaseToken = new string('0', 64) }, at));
        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, store.RecordInputUnavailable(lease, at.AddSeconds(30)));
        Assert.Equal("leased", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=7;"));
    }

    [Fact]
    public void Renewal_ExtendsExactlyThirtySecondsAndRejectsStolenOrExpiredTokens()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000015", 15, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('7', 64));
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);

        var renewed = Assert.IsType<LocalRepositoryQueueLease>(store.Renew(lease, at.AddSeconds(10)).Lease);

        Assert.Equal(at.AddSeconds(40), renewed.LeaseExpiresAt);
        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, store.Renew(renewed with { LeaseToken = new string('0', 64) }, at.AddSeconds(11)).Status);
        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, store.Renew(renewed, renewed.LeaseExpiresAt).Status);
        Assert.Equal(at.AddSeconds(40).ToString("O", System.Globalization.CultureInfo.InvariantCulture), ScalarText(connection, "SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id=15;"));
    }

    [Fact]
    public void Renewal_ReportsSqliteBusyWithoutChangingTheLeaseAndReleasesItsConnection()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000025", 25, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('c', 64));
        var lease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var originalExpiry = ScalarText(connection, "SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id=25;");

        using (HoldImmediateWriteLock(database.Path))
            Assert.Equal(LocalRepositoryQueueTransitionResult.Busy, store.Renew(lease, at.AddSeconds(10)).Status);

        Assert.Equal(originalExpiry, ScalarText(connection, "SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id=25;"));
        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.Renew(lease, at.AddSeconds(10)).Status);
    }

    [Fact]
    public void ClaimNext_InvalidTokenFactoryRollsBackAndReleasesTheImmediateTransaction()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000026", 26, "pending", "2026-08-01T00:00:00.0000000+00:00");
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var invalid = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => "invalid");

        Assert.Throws<InvalidOperationException>(() => invalid.TryClaimNext(at));
        Assert.Equal("pending", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=26;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT attempt_count FROM local_repository_reconciliation_queue WHERE raw_record_id=26;"));
        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('d', 64)).TryClaimNext(at).Status);
    }

    [Fact]
    public async Task Heartbeat_AtomicallyExtendsQueueAndDueRetentionOperationLease()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000019",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var at = clock.GetUtcNow();
        var store = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('8', 64));
        var queueLease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var availability = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        await using var raw = await availability.ReadAsync(rawId, queueLease.RawPayloadSha256, RetentionReadKind.Operation, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(raw.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(retentionLease.Grant);
        var dueRetentionExpiry = at.AddSeconds(20);
        SetRetentionLeaseExpiry(connection, grant, dueRetentionExpiry);

        var heartbeatAt = at.AddSeconds(10);
        var result = store.Heartbeat(queueLease, retentionLease, heartbeatAt);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, result.Status);
        Assert.Equal(heartbeatAt.AddSeconds(30), result.Lease!.LeaseExpiresAt);
        Assert.Equal(heartbeatAt.AddSeconds(30).ToString("O", System.Globalization.CultureInfo.InvariantCulture), ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(heartbeatAt.Add(RetentionV1Constants.LeaseDuration), grant.LeaseExpiresAt);
        Assert.Equal(heartbeatAt.Add(RetentionV1Constants.LeaseDuration).ToString("O", System.Globalization.CultureInfo.InvariantCulture), ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';"));
    }

    [Fact]
    public async Task Heartbeat_PublishesRetentionRenewalBeforeTaskSixFinalizerCanValidateIt()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000031",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var at = clock.GetUtcNow();
        var checkpoint = new TaskSixFinalizerHeartbeatCheckpoint(temp.DatabasePath);
        var store = new SqliteLocalRepositoryReconciliationStore(
            temp.DatabasePath,
            clock,
            static () => new string('3', 64),
            checkpoint);
        var queueLease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var availability = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        await using var raw = await availability.ReadAsync(rawId, queueLease.RawPayloadSha256, RetentionReadKind.Operation, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(raw.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(retentionLease.Grant);
        SetRetentionLeaseExpiry(connection, grant, at.AddSeconds(20));
        var heartbeatAt = at.AddSeconds(10);
        checkpoint.Configure(store, queueLease, grant, heartbeatAt);

        var result = store.Heartbeat(queueLease, retentionLease, heartbeatAt);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, result.Status);
        Assert.True(checkpoint.GrantBindingWasBlocked);
        Assert.Equal(
            LocalRepositoryQueueTransitionResult.Applied,
            await Assert.IsType<Task<LocalRepositoryQueueTransitionResult>>(checkpoint.ConcurrentFinalizer).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("leased", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(heartbeatAt.Add(RetentionV1Constants.LeaseDuration), grant.LeaseExpiresAt);
    }

    [Theory]
    [InlineData("queue_token")]
    [InlineData("retention_generation")]
    [InlineData("retention_expiry")]
    [InlineData("retention_revision")]
    public async Task Heartbeat_FenceLossRollsBackBothLeaseExpiries(string lostFence)
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000020",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var at = clock.GetUtcNow();
        var store = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('9', 64));
        var queueLease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var availability = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        await using var raw = await availability.ReadAsync(rawId, queueLease.RawPayloadSha256, RetentionReadKind.Operation, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(raw.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(retentionLease.Grant);
        SetRetentionLeaseExpiry(connection, grant, at.AddSeconds(20));

        var attemptedQueueLease = queueLease;
        switch (lostFence)
        {
            case "queue_token":
                attemptedQueueLease = queueLease with { LeaseToken = new string('0', 64) };
                break;
            case "retention_generation":
                Execute(connection, $"UPDATE retention_leases SET generation=generation+1 WHERE item_id='{grant.ItemId}' AND lease_kind='operation';");
                break;
            case "retention_expiry":
                SetRetentionLeaseExpiry(connection, grant, at.AddSeconds(5));
                break;
            case "retention_revision":
                Execute(connection, $"UPDATE retention_items SET revision=revision+1 WHERE item_id='{grant.ItemId}';");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(lostFence));
        }
        var queueExpiryBefore = ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};");
        var retentionExpiryBefore = ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';");

        var result = store.Heartbeat(attemptedQueueLease, retentionLease, at.AddSeconds(10));

        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(queueExpiryBefore, ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(retentionExpiryBefore, ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';"));
    }

    [Fact]
    public async Task Heartbeat_RetentionRenewalRejectionRollsBackTheEarlierQueueRenewal()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000021",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var at = clock.GetUtcNow();
        var store = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('a', 64));
        var queueLease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var availability = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        await using var raw = await availability.ReadAsync(rawId, queueLease.RawPayloadSha256, RetentionReadKind.Operation, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(raw.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(retentionLease.Grant);
        SetRetentionLeaseExpiry(connection, grant, at.AddSeconds(20));
        Execute(connection, """
            CREATE TRIGGER test_reject_repository_retention_renewal
            BEFORE UPDATE OF expires_at ON retention_leases
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """);
        var queueExpiryBefore = ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};");
        var retentionExpiryBefore = ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';");

        var result = store.Heartbeat(queueLease, retentionLease, at.AddSeconds(10));

        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(queueExpiryBefore, ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(retentionExpiryBefore, ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';"));
        Execute(connection, "DROP TRIGGER test_reject_repository_retention_renewal;");
    }

    [Fact]
    public async Task Heartbeat_ReportsImmediateTransactionBusyWithoutChangingEitherExpiryAndCanRetryAfterRelease()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(connection, "01900000-0000-7000-8000-000000000027", rawId, "pending", "1970-01-01T00:00:00.0000000+00:00", rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var at = clock.GetUtcNow();
        var store = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('e', 64));
        var queueLease = Assert.IsType<LocalRepositoryQueueLease>(store.TryClaimNext(at).Lease);
        var availability = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        await using var raw = await availability.ReadAsync(rawId, queueLease.RawPayloadSha256, RetentionReadKind.Operation, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(raw.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(retentionLease.Grant);
        SetRetentionLeaseExpiry(connection, grant, at.AddSeconds(20));
        var queueExpiry = ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};");
        var retentionExpiry = ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';");

        using (HoldImmediateWriteLock(temp.DatabasePath))
            Assert.Equal(LocalRepositoryQueueTransitionResult.Busy, store.Heartbeat(queueLease, retentionLease, at.AddSeconds(10)).Status);

        Assert.Equal(queueExpiry, ScalarText(connection, $"SELECT lease_expires_at FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(retentionExpiry, ScalarText(connection, $"SELECT expires_at FROM retention_leases WHERE item_id='{grant.ItemId}' AND lease_kind='operation';"));
        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.Heartbeat(queueLease, retentionLease, at.AddSeconds(10)).Status);
    }

    [Fact]
    public void ClaimNext_ReportsBusyAndSaturatesTheUnboundedAttemptCounter()
    {
        using var database = new TestDatabase();
        using var connection = OpenCatalog(database.Path);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000008", 8, "pending", "2026-08-01T00:00:00.0000000+00:00", attempts: long.MaxValue);
        var at = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new SqliteLocalRepositoryReconciliationStore(database.Path, leaseTokenFactory: static () => new string('1', 64));

        using (connection.BeginTransaction(deferred: false))
            Assert.Equal(LocalRepositoryQueueTransitionResult.Busy, store.TryClaimNext(at).Status);
        var claimed = store.TryClaimNext(at);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, claimed.Status);
        Assert.Equal(long.MaxValue, claimed.Lease!.AttemptCount);
    }

    [Fact]
    public async Task Discovery_UsesOneAscendingSpanCursorAndGroupsExactRawIds()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var firstRaw = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "11111111111111111111111111111111", DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        var secondRaw = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "22222222222222222222222222222222", DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, firstRaw, 0, "11111111111111111111111111111111");
        InsertSpan(connection, secondRaw, 0, "22222222222222222222222222222222");
        InsertSpan(connection, firstRaw, 1, "11111111111111111111111111111111");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, await queue.DiscoverAsync(reader, CancellationToken.None));
        Assert.Equal(new[] { firstRaw, secondRaw }, QueryLongs(connection, "SELECT raw_record_id FROM local_repository_reconciliation_queue ORDER BY raw_record_id;"));
        const string payload = "{\"resourceSpans\":[]}";
        var digest = SkillProjectionHashing.InputDigest(payload);
        Assert.Equal(digest, ScalarText(connection, $"SELECT raw_payload_sha256 FROM local_repository_reconciliation_queue WHERE raw_record_id={firstRaw};"));
        Assert.Equal(
            LocalRepositoryIdentityHashing.ReconciliationFingerprint(LocalRepositoryReconciliationEvidence.PayloadSha256(firstRaw, digest)),
            ScalarText(connection, $"SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue WHERE raw_record_id={firstRaw};"));
        Assert.Equal(3, ScalarLong(connection, "SELECT last_discovered_span_id FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1';"));
        Assert.Equal(LocalRepositoryQueueTransitionResult.NoWork, await queue.DiscoverAsync(reader, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignRawStoreIsRejectedBeforeDiscoveryOrWorkerCanJoinTheSameNumericRawId(bool equalPayload)
    {
        using var catalog = new MonitorTempDirectory();
        using var foreign = new MonitorTempDirectory();
        var localRaw = catalog.CreateRawStore();
        var foreignRaw = foreign.CreateRawStore();
        localRaw.CreateMonitorSchema();
        foreignRaw.CreateMonitorSchema();
        new SqliteSessionStore(catalog.DatabasePath).CreateSchema();
        using var connection = OpenCatalog(catalog.DatabasePath);
        var localId = localRaw.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        var foreignId = foreignRaw.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, equalPayload ? "{\"resourceSpans\":[]}" : "{\"different\":true}"));
        Assert.Equal(localId, foreignId);
        InsertSpan(connection, localId, 0, "11111111111111111111111111111111");
        var queue = new SqliteLocalRepositoryReconciliationStore(catalog.DatabasePath, catalog.TimeProvider);
        var foreignReader = new LocalRepositoryRawAvailabilityReader(foreignRaw, foreign.RetentionContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DiscoverAsync(foreignReader, CancellationToken.None).AsTask());
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Throws<InvalidOperationException>(() => new LocalRepositoryReconciliationWorker(queue, foreignReader, new RecordingProcessor(), catalog.TimeProvider));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
    }

    [Fact]
    public async Task Discovery_CancellationAtTheTypedPublicationCheckpointLeavesNoCursorOrQueue()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, rawId, 0, "11111111111111111111111111111111");
        using var cancellation = new CancellationTokenSource();
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, checkpoint: new CancellingDiscoveryCheckpoint(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.DiscoverAsync(new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext), cancellation.Token).AsTask());
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task Discovery_RepeatedRawAcrossThePinnedSpanWindowAdvancesWithoutDuplicateOrSkip()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        for (var ordinal = 0; ordinal < 257; ordinal++)
            InsertSpan(connection, rawId, ordinal, "11111111111111111111111111111111");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, await queue.DiscoverAsync(reader, CancellationToken.None));
        Assert.Equal(256, ScalarLong(connection, "SELECT last_discovered_span_id FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1';"));
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, await queue.DiscoverAsync(reader, CancellationToken.None));
        Assert.Equal(257, ScalarLong(connection, "SELECT last_discovered_span_id FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1';"));
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(rawId, ScalarLong(connection, "SELECT raw_record_id FROM local_repository_reconciliation_queue;"));
        Assert.Equal(LocalRepositoryQueueTransitionResult.NoWork, await queue.DiscoverAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Discovery_PreservesEachDistinctUnavailableRawIdBeforeAdvancingCursor()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        InsertSpan(connection, 101, 0, "11111111111111111111111111111111");
        InsertSpan(connection, 102, 0, "22222222222222222222222222222222");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, await queue.DiscoverAsync(reader, CancellationToken.None));

        Assert.Equal(new[] { 101L, 102L }, QueryLongs(connection, "SELECT raw_record_id FROM local_repository_reconciliation_queue ORDER BY raw_record_id;"));
        Assert.Equal(2, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='input_unavailable';"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE raw_payload_sha256 IS NOT NULL;"));
        Assert.Equal(
            LocalRepositoryIdentityHashing.ReconciliationFingerprint(LocalRepositoryReconciliationEvidence.InputUnavailable(101)),
            ScalarText(connection, "SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue WHERE raw_record_id=101;"));
        Assert.Equal(2, ScalarLong(connection, "SELECT last_discovered_span_id FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1';"));
    }

    [Fact]
    public async Task Discovery_CorruptRetentionEvidencePublishesNoQueueRowsAndDoesNotAdvanceTheCursor()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, rawId, 0, "11111111111111111111111111111111");
        Execute(connection, $"UPDATE retention_items SET state='deleted',read_denied_at='2026-08-01T00:00:00.0000000+00:00' WHERE store_kind='raw_record' AND source_item_id='{rawId}';");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Corrupt, await queue.DiscoverAsync(reader, CancellationToken.None));

        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task Discovery_EqualityConflictIsTypedAndRollsBackEarlierQueueRowsAndCursor()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var first = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        var conflicting = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, first, 0, "11111111111111111111111111111111");
        InsertSpan(connection, conflicting, 0, "22222222222222222222222222222222");
        InsertQueue(connection, "01900000-0000-7000-8000-000000000013", conflicting, "pending", "1970-01-01T00:00:00.0000000+00:00");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        var outcome = await queue.DiscoverAsync(reader, CancellationToken.None);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Corrupt, outcome);
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(conflicting, ScalarLong(connection, "SELECT raw_record_id FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
    }

    [Fact]
    public async Task Discovery_FinalOperationGrantExpiryRollsBackEveryQueueRowAndCursor()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, rawId, 0, "11111111111111111111111111111111");
        var queue = new SqliteLocalRepositoryReconciliationStore(
            temp.DatabasePath,
            clock,
            checkpoint: new AdvancingDiscoveryCheckpoint(clock, TimeSpan.FromSeconds(121)));
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        var outcome = await queue.DiscoverAsync(reader, CancellationToken.None);

        Assert.Equal(LocalRepositoryQueueTransitionResult.StaleOwner, outcome);
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
    }

    [Fact]
    public async Task Discovery_FinalSqliteBusyIsTypedAndReleasesEveryAcquiredRetentionOperationLease()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, rawId, 0, "11111111111111111111111111111111");
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);

        LocalRepositoryQueueTransitionResult outcome;
        using (HoldImmediateWriteLock(temp.DatabasePath))
            outcome = await queue.DiscoverAsync(reader, CancellationToken.None);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Busy, outcome);
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task Worker_InputUnavailableNeverInvokesTheTypedProcessor()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        InsertQueue(connection, "01900000-0000-7000-8000-000000000012", 812, "pending", "1970-01-01T00:00:00.0000000+00:00");
        var processor = new RecordingProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('3', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.InputUnavailable, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, processor.CallCount);
        Assert.Equal("input_unavailable", ScalarText(connection, "SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id=812;"));
    }

    [Fact]
    public async Task Worker_CorruptRetentionEvidenceTerminalizesAsCatalogSchemaViolationWithoutInputOrDigestMisclassification()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(connection, "01900000-0000-7000-8000-000000000030", rawId, "pending", "1970-01-01T00:00:00.0000000+00:00", rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        Execute(connection, $"UPDATE retention_items SET state='deleted',read_denied_at='2026-08-01T00:00:00.0000000+00:00' WHERE store_kind='raw_record' AND source_item_id='{rawId}';");
        var processor = new RecordingProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('0', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Corrupt, await worker.RunOnceAsync(CancellationToken.None));

        Assert.Equal(0, processor.CallCount);
        Assert.Equal("failed_terminal", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal("catalog_schema_violation", ScalarText(connection, $"SELECT terminal_reason FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task Worker_InitialRetentionOperationAcquisitionBusyReturnsQueuePendingWithoutProcessorCall()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        InsertQueue(connection, "01900000-0000-7000-8000-000000000028", rawId, "pending", "1970-01-01T00:00:00.0000000+00:00", rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var processor = new RecordingProcessor();
        var checkpoint = new RawReadBusyCheckpoint(temp.DatabasePath);
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('f', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider,
            checkpoint);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, processor.CallCount);
        Assert.Equal("pending", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(new[] { LocalRepositoryReconciliationCheckpoint.BeforeRawAvailabilityRead, LocalRepositoryReconciliationCheckpoint.AfterRawAvailabilityRead }, checkpoint.Points);
    }

    [Fact]
    public async Task Worker_InvokesTheProcessorOnceWithOnlyTheClaimedRawRecord()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        var unrelatedRawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000014",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var processor = new RecordingProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('4', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, processor.CallCount);
        Assert.Equal(rawId, processor.RawRecordIds.Single());
        Assert.NotEqual(unrelatedRawId, processor.RawRecordIds.Single());
    }

    [Fact]
    public async Task Worker_CallerCancellationPropagatesReleasesRetentionLeaseAndLeavesQueueForStartupRecovery()
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        var unrelatedRawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000016",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000017",
            unrelatedRawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: SkillProjectionHashing.InputDigest("{}"));
        var processor = new CancellationBlockingProcessor();
        var store = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('5', 64));
        var worker = new LocalRepositoryReconciliationWorker(
            store,
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            clock);
        using var cancellation = new CancellationTokenSource();

        var work = worker.RunOnceAsync(cancellation.Token).AsTask();
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(1, processor.CallCount);
        Assert.Equal(new[] { rawId }, processor.RawRecordIds);
        Assert.Equal("leased", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal("pending", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={unrelatedRawId};"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, store.RecoverExpiredLeases(clock.GetUtcNow()));
        Assert.Equal("pending", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
    }

    [Theory]
    [InlineData("item_revision")]
    [InlineData("lease_generation")]
    [InlineData("lease_expiry")]
    public async Task Worker_ExecutionTimeRetentionFenceLossCancelsProcessorReturnsOwnedQueuePendingAndReleasesLease(string lostFence)
    {
        using var temp = new MonitorTempDirectory();
        var clock = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        const string payload = "{\"resourceSpans\":[]}";
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, payload));
        var alternateRawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{}"));
        InsertQueue(connection, "01900000-0000-7000-8000-000000000029", rawId, "pending", "1970-01-01T00:00:00.0000000+00:00", rawPayloadSha256: SkillProjectionHashing.InputDigest(payload));
        var processor = new FenceCancellationProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, clock, static () => new string('1', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            clock);

        var work = worker.RunOnceAsync(CancellationToken.None).AsTask();
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var originalLeaseGeneration = ScalarLong(connection, $"SELECT generation FROM retention_leases WHERE item_id=(SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{rawId}') AND lease_kind='operation';");
        switch (lostFence)
        {
            case "item_revision":
                Execute(connection, $"UPDATE retention_items SET revision=revision+1 WHERE store_kind='raw_record' AND source_item_id='{rawId}';");
                break;
            case "lease_generation":
                Execute(connection, $"UPDATE retention_leases SET generation=generation+1 WHERE item_id=(SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{rawId}') AND lease_kind='operation';");
                break;
            case "lease_expiry":
                Execute(connection, $"UPDATE retention_leases SET expires_at='{clock.GetUtcNow().AddSeconds(5):O}' WHERE item_id=(SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{rawId}') AND lease_kind='operation';");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(lostFence));
        }
        clock.Advance(TimeSpan.FromSeconds(10));

        await processor.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, processor.CallCount);
        Assert.Equal(new[] { rawId }, processor.RawRecordIds);
        Assert.DoesNotContain(alternateRawId, processor.RawRecordIds);
        Assert.False(processor.CompletedSuccessfully);
        Assert.Equal("pending", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal(0, ScalarLong(connection, $"SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation' AND generation={originalLeaseGeneration};"));
        Assert.Equal(lostFence == "lease_generation" ? 1 : 0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task Worker_PayloadDigestMismatchNeverInvokesProcessorAndRecordsTheExactTerminalReason()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertQueue(
            connection,
            "01900000-0000-7000-8000-000000000018",
            rawId,
            "pending",
            "1970-01-01T00:00:00.0000000+00:00",
            rawPayloadSha256: new string('0', 64));
        var processor = new RecordingProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('6', 64)),
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.DigestMismatch, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, processor.CallCount);
        Assert.Equal("failed_terminal", ScalarText(connection, $"SELECT state FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
        Assert.Equal("catalog_payload_digest_mismatch", ScalarText(connection, $"SELECT terminal_reason FROM local_repository_reconciliation_queue WHERE raw_record_id={rawId};"));
    }

    private static SqliteConnection OpenCatalog(string path)
    {
        new SqliteSessionStore(path).CreateSchema();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertQueue(SqliteConnection connection, string queueId, long rawRecordId, string state, string updatedAt, string? token = null, string? expiry = null, long attempts = 0, string? rawPayloadSha256 = null)
    {
        var digest = rawPayloadSha256 ?? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Execute(connection, $"""
            INSERT INTO local_repository_reconciliation_queue VALUES
            ('{queueId}',{rawRecordId},'payload_sha256','{digest}','local-repository-catalog:1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','{state}',{attempts},{(token is null ? "NULL" : $"'{token}'")},{(expiry is null ? "NULL" : $"'{expiry}'")},NULL,'{updatedAt}','{updatedAt}');
            """);
    }

    private static void SetRetentionLeaseExpiry(SqliteConnection connection, RetentionReadGrant grant, DateTimeOffset expiry)
    {
        Execute(connection, $"UPDATE retention_leases SET expires_at='{expiry.ToUniversalTime():O}' WHERE item_id='{grant.ItemId}' AND lease_kind='operation' AND owner='{grant.LeaseOwner}' AND generation={grant.LeaseGeneration};");
        grant.AdvanceExpiry(expiry);
    }

    private static IDisposable HoldImmediateWriteLock(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var transaction = connection.BeginTransaction(deferred: false);
        return new ImmediateWriteLock(connection, transaction);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void InsertSpan(SqliteConnection connection, long rawRecordId, int ordinal, string traceId)
    {
        Execute(connection, $"""
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,conversation_id,duration_ms,start_time,end_time,projected_at)
            VALUES({rawRecordId},'{traceId}',NULL,NULL,{ordinal},NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'1970-01-01T00:00:00.0000000+00:00');
            """);
    }

    private static long[] QueryLongs(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read()) values.Add(reader.GetInt64(0));
        return values.ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProcessor : ILocalRepositoryRawRecordProcessor
    {
        public int CallCount { get; private set; }
        public List<long> RawRecordIds { get; } = [];

        public ValueTask ProcessAsync(LocalRepositoryQueueLease queueLease, RawTelemetryRecord rawRecord, RetentionReadLease<RawTelemetryRecord> retentionLease, CancellationToken cancellationToken)
        {
            CallCount++;
            RawRecordIds.Add(rawRecord.Id ?? throw new InvalidOperationException("expected persisted raw record id"));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationBlockingProcessor : ILocalRepositoryRawRecordProcessor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public List<long> RawRecordIds { get; } = [];

        public async ValueTask ProcessAsync(
            LocalRepositoryQueueLease queueLease,
            RawTelemetryRecord rawRecord,
            RetentionReadLease<RawTelemetryRecord> retentionLease,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RawRecordIds.Add(rawRecord.Id ?? throw new InvalidOperationException("expected persisted raw record id"));
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FenceCancellationProcessor : ILocalRepositoryRawRecordProcessor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public List<long> RawRecordIds { get; } = [];
        public bool CompletedSuccessfully { get; private set; }

        public async ValueTask ProcessAsync(LocalRepositoryQueueLease queueLease, RawTelemetryRecord rawRecord, RetentionReadLease<RawTelemetryRecord> retentionLease, CancellationToken cancellationToken)
        {
            CallCount++;
            RawRecordIds.Add(rawRecord.Id ?? throw new InvalidOperationException("expected persisted raw record id"));
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                CompletedSuccessfully = true;
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class TaskSixFinalizerHeartbeatCheckpoint(string databasePath) : ILocalRepositoryReconciliationCheckpoint
    {
        private SqliteLocalRepositoryReconciliationStore? store;
        private LocalRepositoryQueueLease? queueLease;
        private RetentionReadGrant? grant;
        private DateTimeOffset at;

        internal bool GrantBindingWasBlocked { get; private set; }
        internal Task<LocalRepositoryQueueTransitionResult>? ConcurrentFinalizer { get; private set; }

        internal void Configure(
            SqliteLocalRepositoryReconciliationStore configuredStore,
            LocalRepositoryQueueLease configuredQueueLease,
            RetentionReadGrant configuredGrant,
            DateTimeOffset configuredAt)
        {
            store = configuredStore;
            queueLease = configuredQueueLease;
            grant = configuredGrant;
            at = configuredAt;
        }

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryReconciliationCheckpoint.BeforeRetentionRenewalPublication)
                return;

            using var transactionStarted = new ManualResetEventSlim();
            using var grantBindingAttempted = new ManualResetEventSlim();
            var finalizerCompletion = new TaskCompletionSource<LocalRepositoryQueueTransitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalizerThread = new Thread(() =>
            {
                var transactionStartedSignaled = false;
                var grantBindingAttemptedSignaled = false;
                try
                {
                    using var concurrentConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath,
                        Pooling = false,
                    }.ToString());
                    concurrentConnection.Open();
                    using var concurrentTransaction = concurrentConnection.BeginTransaction(deferred: false);
                    transactionStartedSignaled = true;
                    Signal(transactionStarted);
                    var configuredGrant = grant ?? throw new InvalidOperationException("heartbeat checkpoint was not configured");
                    using var bindingProbe = concurrentConnection.CreateCommand();
                    bindingProbe.Transaction = concurrentTransaction;
                    if (configuredGrant.TryBindSelectorCapability(bindingProbe))
                        throw new InvalidOperationException("retention renewal publication was not protected after commit");

                    GrantBindingWasBlocked = true;
                    grantBindingAttemptedSignaled = true;
                    Signal(grantBindingAttempted);
                    var result = Finalize(concurrentConnection, concurrentTransaction);
                    concurrentTransaction.Rollback();
                    finalizerCompletion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    if (!transactionStartedSignaled)
                    {
                        transactionStartedSignaled = true;
                        Signal(transactionStarted);
                    }
                    if (!grantBindingAttemptedSignaled)
                    {
                        grantBindingAttemptedSignaled = true;
                        Signal(grantBindingAttempted);
                    }
                    finalizerCompletion.TrySetException(exception);
                }
            });
            finalizerThread.IsBackground = true;
            ConcurrentFinalizer = finalizerCompletion.Task;
            finalizerThread.Start();
            Assert.True(transactionStarted.Wait(TimeSpan.FromSeconds(5)), "finalizer did not acquire its immediate transaction");
            Assert.True(grantBindingAttempted.Wait(TimeSpan.FromSeconds(5)), "finalizer did not attempt grant binding");
            Assert.False(finalizerCompletion.Task.IsCompleted);
        }

        private static void Signal(ManualResetEventSlim signal)
        {
            try { signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private LocalRepositoryQueueTransitionResult Finalize(SqliteConnection finalizerConnection, SqliteTransaction transaction)
        {
            var configuredGrant = grant ?? throw new InvalidOperationException("heartbeat checkpoint was not configured");
            var configuredQueueLease = queueLease ?? throw new InvalidOperationException("heartbeat checkpoint was not configured");
            return RetentionCatalogStore.ValidateLocalRepositoryOperationLease(
                finalizerConnection,
                transaction,
                configuredGrant,
                configuredQueueLease.RawRecordId,
                at)
                ? (store ?? throw new InvalidOperationException("heartbeat checkpoint was not configured")).TryComplete(
                    finalizerConnection,
                    transaction,
                    configuredQueueLease,
                    at)
                : LocalRepositoryQueueTransitionResult.StaleOwner;
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"local-repository-queue-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
            _ = RetentionCatalogContext.InitializeNewOwnedDatabase(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ImmediateWriteLock(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        public void Dispose()
        {
            transaction.Dispose();
            connection.Dispose();
        }
    }

    private sealed class AdvancingDiscoveryCheckpoint(MutableTimeProvider clock, TimeSpan advance) : ILocalRepositoryReconciliationCheckpoint
    {
        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryReconciliationCheckpoint.BeforeDiscoveryPublication) clock.Advance(advance);
        }
    }

    private sealed class CancellingDiscoveryCheckpoint(CancellationTokenSource cancellation) : ILocalRepositoryReconciliationCheckpoint
    {
        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryReconciliationCheckpoint.BeforeDiscoveryPublication) cancellation.Cancel();
        }
    }

    private sealed class RawReadBusyCheckpoint(string databasePath) : ILocalRepositoryReconciliationCheckpoint
    {
        private IDisposable? writeLock;
        public List<LocalRepositoryReconciliationCheckpoint> Points { get; } = [];

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            Points.Add(checkpoint);
            if (checkpoint == LocalRepositoryReconciliationCheckpoint.BeforeRawAvailabilityRead)
                writeLock = HoldImmediateWriteLock(databasePath);
            else if (checkpoint == LocalRepositoryReconciliationCheckpoint.AfterRawAvailabilityRead)
            {
                writeLock?.Dispose();
                writeLock = null;
            }
        }
    }

}

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

    [Fact]
    public async Task DiscoveryMissingProjectorStateFailsClosedWithoutQueueCursorOrRawPublication()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        var reader = new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext);
        var checkpoint = new CountingDiscoveryRawReadCheckpoint();
        var queue = new SqliteLocalRepositoryReconciliationStore(
            temp.DatabasePath,
            temp.TimeProvider,
            checkpoint: checkpoint);
        var firstRawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, firstRawId, 0, "11111111111111111111111111111111");
        Assert.Equal(LocalRepositoryQueueTransitionResult.Applied, await queue.DiscoverAsync(reader, CancellationToken.None));
        Assert.Equal(1, checkpoint.Count);
        var firstQueueCount = ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;");
        Execute(connection, "DELETE FROM local_repository_reconciliation_state;");
        var laterRawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, DateTimeOffset.UnixEpoch, null, "{\"resourceSpans\":[]}"));
        InsertSpan(connection, laterRawId, 0, "22222222222222222222222222222222");

        var outcome = await queue.DiscoverAsync(reader, CancellationToken.None);

        Assert.Equal(LocalRepositoryQueueTransitionResult.Corrupt, outcome);
        Assert.Equal(1, checkpoint.Count);
        Assert.Equal(firstQueueCount, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
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
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1' AND last_discovered_span_id IS NULL;"));
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
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1' AND last_discovered_span_id IS NULL;"));
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
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1' AND last_discovered_span_id IS NULL;"));
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
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1' AND last_discovered_span_id IS NULL;"));
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
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_state WHERE projector_key='local-repository-catalog-v1' AND last_discovered_span_id IS NULL;"));
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

    [Fact]
    public void RestoreNormalization_IsUnconditionalTransactionBoundAndPreservesEveryOtherStoredValue()
    {
        using var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = OpenCatalog(temp.DatabasePath);
        for (var rawRecordId = 901; rawRecordId <= 907; rawRecordId++)
            InsertSpan(connection, rawRecordId, 0, rawRecordId.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000901", 901, "leased", 7, "9998-12-31T23:59:00.0000000+00:00", new string('a', 64), "9998-12-31T23:59:30.0000000+00:00");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000902", 902, "leased", 9, "2000-01-01T00:00:00.0000000+00:00", new string('b', 64), "2000-01-01T00:00:30.0000000+00:00");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000903", 903, "completed", 1, "2026-08-01T02:00:00.0000000+00:00");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000904", 904, "pending", 0, "2026-08-01T03:00:00.0000000+00:00");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000905", 905, "waiting_session", 1, "2026-08-01T04:00:00.0000000+00:00");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000906", 906, "input_unavailable", 0, "2026-08-01T05:00:00.0000000+00:00", evidenceKind: "input_unavailable");
        InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000907", 907, "failed_terminal", 1, "2026-08-01T06:00:00.0000000+00:00", terminalReason: "catalog_parse_failure");
        InsertDiscoveryCursor(connection, 7);
        var before = QueueStoredValues(connection);

        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            _ = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);
            SqliteLocalRepositoryReconciliationStore.NormalizeRestoredLeases(connection, transaction);
            transaction.Rollback();
        }
        Assert.Equal(before, QueueStoredValues(connection));

        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            _ = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);
            SqliteLocalRepositoryReconciliationStore.NormalizeRestoredLeases(connection, transaction);
            transaction.Commit();
        }

        var after = QueueStoredValues(connection);
        Assert.Equal(2, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE raw_record_id IN (901,902) AND state='pending' AND lease_token IS NULL AND lease_expires_at IS NULL;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM local_repository_reconciliation_queue WHERE state='leased';"));
        for (var row = 0; row < 2; row++)
            foreach (var column in Enumerable.Range(0, 13).Except([6, 8, 9]))
                Assert.Equal(before[row][column], after[row][column]);
        for (var row = 2; row < before.Count; row++)
            Assert.Equal(before[row], after[row]);
    }

    [Theory]
    [InlineData("UPDATE local_repository_reconciliation_queue SET attempt_count=-1;")]
    [InlineData("UPDATE local_repository_reconciliation_queue SET state='completed',attempt_count=0;")]
    [InlineData("UPDATE local_repository_reconciliation_queue SET lease_expires_at='2026-08-01T00:00:31.0000000+00:00';")]
    [InlineData("UPDATE local_repository_reconciliation_queue SET terminal_reason='catalog_parse_failure';")]
    public void RestoreValidation_RejectsUnreachableLeaseStateWithOneValueFreeToken(string corruption)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 903, 0, "33333333333333333333333333333333");
            InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000903", 903, "leased", 1, "2026-08-01T00:00:00.0000000+00:00", new string('c', 64), "2026-08-01T00:00:30.0000000+00:00");
            InsertDiscoveryCursor(connection, 1);
            Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            Execute(connection, corruption);
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(() => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Theory]
    [InlineData("input_unavailable", "input_unavailable", 0L, null)]
    [InlineData("payload_sha256", "pending", 0L, null)]
    [InlineData("payload_sha256", "pending", long.MaxValue, null)]
    [InlineData("payload_sha256", "waiting_session", 1L, null)]
    [InlineData("payload_sha256", "leased", 1L, null)]
    [InlineData("payload_sha256", "completed", 1L, null)]
    [InlineData("payload_sha256", "input_unavailable", 1L, null)]
    [InlineData("payload_sha256", "failed_terminal", 1L, "catalog_parse_failure")]
    public void RestoreValidation_AcceptsEveryReachableEvidenceStateAttemptArm(string evidence, string state, long attempts, string? terminalReason)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 905, 0, "55555555555555555555555555555555");
            InsertRestorableQueue(
                connection,
                "01900000-0000-7000-8000-000000000905",
                905,
                state,
                attempts,
                "2026-08-01T00:00:00.0000000+00:00",
                state == "leased" ? new string('d', 64) : null,
                state == "leased" ? "2026-08-01T00:00:30.0000000+00:00" : null,
                terminalReason,
                evidence);
            InsertDiscoveryCursor(connection, 1);
            using var transaction = connection.BeginTransaction(deferred: true);

            _ = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);

            transaction.Rollback();
        }
    }

    [Theory]
    [InlineData("input_unavailable", "pending", 0L)]
    [InlineData("input_unavailable", "waiting_session", 1L)]
    [InlineData("input_unavailable", "completed", 1L)]
    [InlineData("input_unavailable", "failed_terminal", 1L)]
    [InlineData("payload_sha256", "waiting_session", 0L)]
    [InlineData("payload_sha256", "completed", 0L)]
    [InlineData("payload_sha256", "input_unavailable", 0L)]
    [InlineData("payload_sha256", "failed_terminal", 0L)]
    public void RestoreValidation_RejectsEveryUnreachableEvidenceStateAttemptArm(string evidence, string state, long attempts)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 906, 0, "66666666666666666666666666666666");
            InsertRestorableQueue(
                connection,
                "01900000-0000-7000-8000-000000000906",
                906,
                state,
                attempts,
                "2026-08-01T00:00:00.0000000+00:00",
                terminalReason: state == "failed_terminal" ? "catalog_parse_failure" : null,
                evidenceKind: evidence);
            InsertDiscoveryCursor(connection, 1);
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(() => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Fact]
    public void RestoreValidation_UsesBoundedBinaryQueuePagesAndFindsLaterPageCorruption()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            for (var index = 130; index >= 1; index--)
            {
                InsertSpan(connection, index, 0, index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
                InsertRestorableQueue(connection, $"01900000-0000-7000-8000-{index:x12}", index, "pending", 0, "2026-08-01T00:00:00.0000000+00:00");
            }
            InsertDiscoveryCursor(connection, 130);
            var pageCounts = new List<int>();
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                _ = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction, pageCounts.Add, null);
                transaction.Rollback();
            }
            Assert.True(pageCounts.Count > 1);
            Assert.Equal(130, pageCounts.Sum());
            Assert.All(pageCounts, count => Assert.InRange(count, 1, 64));

            Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            Execute(connection, "UPDATE local_repository_reconciliation_queue SET attempt_count=-1 WHERE raw_record_id=130;");
            pageCounts.Clear();
            using var corruptTransaction = connection.BeginTransaction(deferred: true);
            var error = Assert.Throws<InvalidOperationException>(() => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, corruptTransaction, pageCounts.Add, null));
            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            Assert.True(pageCounts.Count > 1);
            Assert.All(pageCounts, count => Assert.InRange(count, 1, 64));
            corruptTransaction.Rollback();
        }
    }

    [Fact]
    public void QueueValidation_FirstPageIncludesCheckBypassedEmptyQueueId()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            InsertSpan(connection, 951, 0, "11111111111111111111111111111111");
            InsertRestorableQueue(connection, string.Empty, 951, "pending", 0, "2026-08-01T00:00:00.0000000+00:00");
            InsertDiscoveryCursor(connection, 1);

            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var restoreError = Assert.Throws<InvalidOperationException>(
                    () => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));
                Assert.Equal("local_repository_reconciliation_restore_invalid", restoreError.Message);
                transaction.Rollback();
            }

            var catalogError = Assert.Throws<InvalidOperationException>(
                () => LocalRepositoryCatalogValidation.Validate(connection, transaction: null));
            Assert.Equal("local_repository_catalog_canonical_value_invalid", catalogError.Message);
        }
    }

    [Fact]
    public void ValidatedRestoreState_HasOnlyAPrivateConstructionPath()
    {
        var capabilityType = typeof(LocalRepositoryValidatedReconciliationState);

        Assert.Equal(
            "CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryValidatedReconciliationState",
            capabilityType.FullName);
        Assert.True(capabilityType.IsNotPublic);
        var constructor = Assert.Single(capabilityType.GetConstructors(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
    }

    [Fact]
    public void ValidatedRestoreState_FactoryRejectsCorruptStateBeforeReturningProof()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 952, 0, "22222222222222222222222222222222");
            var fingerprint = InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000952", 952, "completed", 1, "2026-08-01T00:00:00.0000000+00:00");
            InsertSourceReconciliationHistory(connection, fingerprint);
            using var transaction = connection.BeginTransaction(deferred: true);

            var restoreError = Assert.Throws<InvalidOperationException>(
                () => LocalRepositoryValidatedReconciliationState.ValidateAndCreate(connection, transaction, null, null));

            Assert.Equal("local_repository_reconciliation_restore_invalid", restoreError.Message);
            transaction.Rollback();
        }
    }

    [Fact]
    public void ValidatedRestoreState_IsReferenceBoundAndPerformsOnlyLazyExactLookups()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 904, 0, "44444444444444444444444444444444");
            var fingerprint = InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000904", 904, "completed", 1, "2026-08-01T00:00:00.0000000+00:00");
            InsertDiscoveryCursor(connection, 1);
            InsertSourceReconciliationHistory(connection, fingerprint);
            var lookupCounts = new List<int>();
            using var transaction = connection.BeginTransaction(deferred: true);

            var state = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction, null, lookupCounts.Add);

            Assert.Empty(lookupCounts);
            Assert.True(state.IsBoundTo(connection, transaction));
            Assert.True(state.TryGetCompletedPayloadRawRecordId(fingerprint, out var rawRecordId));
            Assert.Equal(904, rawRecordId);
            Assert.Equal([1], lookupCounts);
            Assert.False(state.TryGetCompletedPayloadRawRecordId(new string('f', 64), out _));
            Assert.Equal([1, 0], lookupCounts);
            var malformed = Assert.Throws<InvalidOperationException>(() => state.TryGetCompletedPayloadRawRecordId("904", out _));
            Assert.Equal("local_repository_reconciliation_restore_fingerprint_invalid", malformed.Message);
            Assert.Equal([1, 0], lookupCounts);

            using var otherConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
            otherConnection.Open();
            using var otherTransaction = otherConnection.BeginTransaction(deferred: true);
            Assert.False(state.IsBoundTo(otherConnection, otherTransaction));
            otherTransaction.Rollback();
            transaction.Rollback();
            using var laterTransaction = connection.BeginTransaction(deferred: true);
            Assert.False(state.IsBoundTo(connection, laterTransaction));
            laterTransaction.Rollback();
        }
    }

    [Theory]
    [InlineData("queue_without_cursor")]
    [InlineData("null_frontier_with_queue")]
    [InlineData("frontier_span_missing")]
    [InlineData("queue_raw_beyond_frontier")]
    [InlineData("frontier_skips_raw")]
    public void RestoreValidation_RejectsEveryCursorFrontierContradiction(string contradiction)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 911, 0, "11111111111111111111111111111111");
            if (contradiction == "frontier_skips_raw")
                InsertSpan(connection, 912, 0, "22222222222222222222222222222222");
            InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000911", contradiction == "queue_raw_beyond_frontier" ? 912 : 911, "pending", 0, "2026-08-01T00:00:00.0000000+00:00");
            switch (contradiction)
            {
                case "queue_without_cursor":
                    Execute(connection, "DELETE FROM local_repository_reconciliation_state;");
                    break;
                case "null_frontier_with_queue":
                    InsertDiscoveryCursor(connection, null);
                    break;
                case "frontier_span_missing":
                    InsertDiscoveryCursor(connection, 2);
                    break;
                default:
                    InsertDiscoveryCursor(connection, contradiction == "frontier_skips_raw" ? 2 : 1);
                    break;
            }
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(() => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Fact]
    public void RestoreRejectsMissingProjectorStateEvenWhenQueueIsEmpty()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            Execute(connection, "DELETE FROM local_repository_reconciliation_state;");
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(
                () => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("wrong_digest")]
    [InlineData("unavailable_evidence")]
    [InlineData("noncompleted")]
    public void RestoreValidation_RejectsObservationWithoutCompletedPayloadOwnership(string contradiction)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 921, 0, "11111111111111111111111111111111");
            var evidenceKind = contradiction == "unavailable_evidence" ? "input_unavailable" : "payload_sha256";
            var state = contradiction switch
            {
                "unavailable_evidence" => "input_unavailable",
                "noncompleted" => "waiting_session",
                _ => "completed",
            };
            InsertRestorableQueue(
                connection,
                "01900000-0000-7000-8000-000000000921",
                921,
                state,
                state == "input_unavailable" ? 0 : 1,
                "2026-08-01T00:00:00.0000000+00:00",
                evidenceKind: evidenceKind);
            InsertDiscoveryCursor(connection, 1);
            var queueDigest = evidenceKind == "payload_sha256"
                ? ScalarText(connection, "SELECT raw_payload_sha256 FROM local_repository_reconciliation_queue WHERE raw_record_id=921;")
                : new string('a', 64);
            InsertObservation(
                connection,
                contradiction == "orphan" ? 922 : 921,
                contradiction == "wrong_digest" ? new string('f', 64) : queueDigest);
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(
                () => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Theory]
    [InlineData("waiting_session")]
    [InlineData("input_unavailable")]
    [InlineData("failed_terminal")]
    [InlineData("missing")]
    public void RestoreValidation_RejectsSourceHistoryWithoutCompletedPayloadOwnership(string contradiction)
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 922, 0, "22222222222222222222222222222222");
            var queueState = contradiction == "missing" ? "completed" : contradiction;
            var evidenceKind = queueState == "input_unavailable" ? "input_unavailable" : "payload_sha256";
            var fingerprint = InsertRestorableQueue(
                connection,
                "01900000-0000-7000-8000-000000000922",
                922,
                queueState,
                queueState == "input_unavailable" ? 0 : 1,
                "2026-08-01T00:00:00.0000000+00:00",
                terminalReason: queueState == "failed_terminal" ? "catalog_parse_failure" : null,
                evidenceKind: evidenceKind);
            InsertDiscoveryCursor(connection, 1);
            InsertSourceReconciliationHistory(
                connection,
                contradiction == "missing" ? new string('f', 64) : fingerprint);
            using var transaction = connection.BeginTransaction(deferred: true);

            var error = Assert.Throws<InvalidOperationException>(
                () => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction));

            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            transaction.Rollback();
        }
    }

    [Fact]
    public void ValidatedRestoreState_LazyLookupRejectsAmbiguousSourceHistoryMembership()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            InsertSpan(connection, 923, 0, "33333333333333333333333333333333");
            var fingerprint = InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000923", 923, "completed", 1, "2026-08-01T00:00:00.0000000+00:00");
            InsertDiscoveryCursor(connection, 1);
            InsertSourceReconciliationHistory(connection, fingerprint);
            var lookupCounts = new List<int>();
            using var transaction = connection.BeginTransaction(deferred: false);
            var state = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction, null, lookupCounts.Add);
            var digest = ScalarText(connection, "SELECT raw_payload_sha256 FROM local_repository_reconciliation_queue WHERE raw_record_id=923;");
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_repository_reconciliation_queue
                    (queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,terminal_reason,created_at,updated_at)
                    VALUES('01900000-0000-7000-8000-000000000924',924,'payload_sha256',$digest,'local-repository-catalog:1',$fingerprint,'completed',1,NULL,NULL,NULL,
                           '2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00');
                    """;
                command.Parameters.AddWithValue("$digest", digest);
                command.Parameters.AddWithValue("$fingerprint", fingerprint);
                command.ExecuteNonQuery();
            }

            Assert.False(state.TryGetCompletedPayloadRawRecordId(fingerprint, out var rawRecordId));
            Assert.Equal(0, rawRecordId);
            Assert.Equal([0], lookupCounts);
            transaction.Rollback();
        }
    }

    [Fact]
    public void RestoreValidation_EnforcesTheSessionGlobalCandidateBoundAcrossQueueItems()
    {
        using var temp = CreateRestorableQueueDatabase(out var connection);
        using (connection)
        {
            InsertSpan(connection, 931, 0, "11111111111111111111111111111111");
            InsertSpan(connection, 932, 0, "22222222222222222222222222222222");
            InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000931", 931, "completed", 1, "2026-08-01T00:00:00.0000000+00:00");
            InsertRestorableQueue(connection, "01900000-0000-7000-8000-000000000932", 932, "completed", 1, "2026-08-01T00:00:00.0000000+00:00");
            InsertDiscoveryCursor(connection, 2);
            Execute(connection, "PRAGMA foreign_keys=OFF;");
            for (var index = 1; index <= 128; index++)
                InsertSyntheticAdmittedContext(connection, index);
            using (var accepted = connection.BeginTransaction(deferred: true))
            {
                _ = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, accepted);
                accepted.Rollback();
            }

            InsertSyntheticAdmittedContext(connection, 129);
            using var rejected = connection.BeginTransaction(deferred: true);
            var error = Assert.Throws<InvalidOperationException>(() => SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, rejected));
            Assert.Equal("local_repository_reconciliation_restore_invalid", error.Message);
            rejected.Rollback();
        }
    }

    private static SqliteConnection OpenCatalog(string path)
    {
        new SqliteSessionStore(path).CreateSchema();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        return connection;
    }

    private static MonitorTempDirectory CreateRestorableQueueDatabase(out SqliteConnection connection)
    {
        var temp = new MonitorTempDirectory();
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        connection = OpenCatalog(temp.DatabasePath);
        return temp;
    }

    private static string InsertRestorableQueue(
        SqliteConnection connection,
        string queueId,
        long rawRecordId,
        string state,
        long attempts,
        string updatedAt,
        string? token = null,
        string? expiry = null,
        string? terminalReason = null,
        string evidenceKind = "payload_sha256")
    {
        var digest = evidenceKind == "payload_sha256" ? new string((char)('a' + rawRecordId % 6), 64) : null;
        var evidence = digest is null
            ? LocalRepositoryReconciliationEvidence.InputUnavailable(rawRecordId)
            : LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest);
        var fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(evidence);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_repository_reconciliation_queue
            (queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,terminal_reason,created_at,updated_at)
            VALUES($queue_id,$raw_id,$evidence,$digest,'local-repository-catalog:1',$fingerprint,$state,$attempts,$token,$expiry,$reason,$updated,$updated);
            """;
        command.Parameters.AddWithValue("$queue_id", queueId);
        command.Parameters.AddWithValue("$raw_id", rawRecordId);
        command.Parameters.AddWithValue("$evidence", evidenceKind);
        command.Parameters.AddWithValue("$digest", digest ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$token", token ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$expiry", expiry ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$reason", terminalReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAt);
        command.ExecuteNonQuery();
        return fingerprint;
    }

    private static void InsertDiscoveryCursor(SqliteConnection connection, long? frontier)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE local_repository_reconciliation_state SET last_discovered_span_id=$frontier,updated_at='2026-08-01T00:00:00.0000000+00:00' WHERE projector_key='local-repository-catalog-v1';";
        command.Parameters.AddWithValue("$frontier", frontier ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertSourceReconciliationHistory(SqliteConnection connection, string fingerprint)
    {
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_repository_assignment_history
            (history_id,session_id,action,previous_revision,new_revision,previous_assignment_state_sha256,new_assignment_state_sha256,
             previous_state,new_state,previous_authority,new_authority,previous_repository_id,new_repository_id,cause_kind,operation_key,reconciliation_fingerprint,occurred_at)
            VALUES('01900000-0000-7000-8000-000000000905','01900000-0000-7000-8000-000000000906','automatic_reconcile',0,1,
                   $before,$after,'unassigned','unassigned','none','none',NULL,NULL,'source_reconciliation',NULL,$fingerprint,'2026-08-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$before", new string('1', 64));
        command.Parameters.AddWithValue("$after", new string('2', 64));
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.ExecuteNonQuery();
    }

    private static void InsertObservation(SqliteConnection connection, long rawRecordId, string digest)
    {
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(rawRecordId, 0, 0, 0, 0, "vcs.repository.url.full"));
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_repository_observations
            (observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
             scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,locator_sha256,display_owner,display_repository,
             source_surface,source_application_version,observed_at)
            VALUES('01900000-0000-7000-8000-000000000925',$source,$raw_id,$digest,0,0,0,0,'span','vcs.repository.url.full','invalid_locator',
                   NULL,NULL,NULL,NULL,NULL,'github-copilot-vscode',NULL,'2026-08-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$source", sourceIdentity);
        command.Parameters.AddWithValue("$raw_id", rawRecordId);
        command.Parameters.AddWithValue("$digest", digest);
        command.ExecuteNonQuery();
    }

    private static void InsertSyntheticAdmittedContext(SqliteConnection connection, int index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_repository_observation_contexts
            (context_id,observation_id,context_identity_sha256,session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
            VALUES($context,$observation,$identity,$event,'01900000-0000-7000-8000-000000000940','11111111111111111111111111111111','2222222222222222',
                   'admitted',$repository,$locator,'2026-08-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$context", $"01900000-0000-7000-8001-{index:x12}");
        command.Parameters.AddWithValue("$observation", $"01900000-0000-7000-8002-{index:x12}");
        command.Parameters.AddWithValue("$identity", index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event", $"01900000-0000-7000-8003-{index:x12}");
        command.Parameters.AddWithValue("$repository", $"01900000-0000-7000-8004-{index:x12}");
        command.Parameters.AddWithValue("$locator", $"01900000-0000-7000-8005-{index:x12}");
        command.ExecuteNonQuery();
    }

    private static List<string[]> QueueStoredValues(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT typeof(queue_id),CAST(queue_id AS BLOB),typeof(raw_record_id),CAST(raw_record_id AS BLOB),
                   typeof(input_evidence_kind),CAST(input_evidence_kind AS BLOB),typeof(raw_payload_sha256),CAST(raw_payload_sha256 AS BLOB),
                   typeof(projector_version),CAST(projector_version AS BLOB),typeof(reconciliation_fingerprint),CAST(reconciliation_fingerprint AS BLOB),
                   typeof(state),CAST(state AS BLOB),typeof(attempt_count),CAST(attempt_count AS BLOB),
                   typeof(lease_token),CAST(lease_token AS BLOB),typeof(lease_expires_at),CAST(lease_expires_at AS BLOB),
                   typeof(terminal_reason),CAST(terminal_reason AS BLOB),typeof(created_at),CAST(created_at AS BLOB),
                   typeof(updated_at),CAST(updated_at AS BLOB)
            FROM local_repository_reconciliation_queue
            ORDER BY queue_id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount / 2];
            for (var index = 0; index < row.Length; index++)
                row[index] = $"{reader.GetString(index * 2)}:{(reader.IsDBNull((index * 2) + 1) ? string.Empty : Convert.ToHexString(reader.GetFieldValue<byte[]>((index * 2) + 1)))}";
            rows.Add(row);
        }
        return rows;
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

    private sealed class CountingDiscoveryRawReadCheckpoint : ILocalRepositoryReconciliationCheckpoint
    {
        internal int Count { get; private set; }

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryReconciliationCheckpoint.BeforeDiscoveryRawAvailabilityRead)
                Count++;
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

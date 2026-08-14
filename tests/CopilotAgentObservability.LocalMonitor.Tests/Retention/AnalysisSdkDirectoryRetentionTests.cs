using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class AnalysisSdkDirectoryRetentionTests
{
    [Fact]
    public async Task OpenAsync_ReservesBeforeCreatingTheConfiguredParentAndCreatesOnlyTheOwnedChildMarker()
    {
        using var fixture = Fixture.Create();
        Assert.False(Directory.Exists(fixture.Parent));

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.True(Directory.Exists(fixture.Parent));
        Assert.True(Directory.Exists(scope.ChildDirectory));
        Assert.Equal(scope.ChildDirectory, Path.Combine(fixture.Parent, fixture.CaptureId()));
        Assert.Equal(new[] { AnalysisSdkDirectoryOwner.MarkerFileName }, Directory.EnumerateFileSystemEntries(scope.ChildDirectory).Select(Path.GetFileName));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_PreservesAnExistingGeneratedChildAndAbandonsTheReservation()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "unrelated.txt"), "keep");

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(reservation.ChildLocator, "unrelated.txt")));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_PreservesAnInvalidMarkerNamedEntryAndAbandonsTheReservation()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        var markerPath = Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName);
        var invalidMarker = reservation.OwnershipMarker.ToArray();
        invalidMarker[0] ^= byte.MaxValue;
        File.WriteAllBytes(markerPath, invalidMarker);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.Equal(invalidMarker, File.ReadAllBytes(markerPath));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_CancellationAfterReservationAbandonsWithoutCreatingTheParent()
    {
        using var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var owner = new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, cancellation.Token));

        Assert.False(Directory.Exists(fixture.Parent));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_RecoversOnlyTheReservationBoundMarkerOnlyChild()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        await using (var stream = new FileStream(Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(reservation.OwnershipMarker);
            stream.Flush(flushToDisk: true);
        }

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.Equal(reservation.ChildLocator, scope.ChildDirectory);
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_AfterRestartCleansAnEmptyReservedChildAndOpensWithFreshAuthority()
    {
        using var fixture = Fixture.Create();
        var stale = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(stale.ChildLocator);
        var sibling = Path.Combine(fixture.Parent, "foreign-sibling.bin");
        var siblingBytes = new byte[] { 17, 23, 42, 99 };
        File.WriteAllBytes(sibling, siblingBytes);
        var reopened = new RetentionCatalogStore(
            RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath),
            fixture.Time);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(reopened, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.False(Directory.Exists(stale.ChildLocator));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        await using var scope = await new AnalysisSdkDirectoryOwner(
                reopened,
                fixture.Time,
                reservationCheckpoint: () => Assert.Equal(
                    RetentionCaptureMutationDisposition.StaleNoOp,
                    reopened.AbandonReservedAnalysisSdkDirectory(stale)))
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.NotEqual(stale.ChildLocator, scope.ChildDirectory);
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_AfterRestartCleansAnExpiredExactMarkerOnlyReservationAndFreshRetriesRemainUnwedged()
    {
        using var fixture = Fixture.Create();
        var stale = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(stale.ChildLocator);
        var markerPath = Path.Combine(stale.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName);
        File.WriteAllBytes(markerPath, stale.OwnershipMarker);
        var sibling = Path.Combine(fixture.Parent, "foreign-sibling.bin");
        var siblingBytes = new byte[] { 17, 23, 42, 99 };
        File.WriteAllBytes(sibling, siblingBytes);
        fixture.Time.Advance(TimeSpan.FromDays(91));
        var reopened = new RetentionCatalogStore(
            RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath),
            fixture.Time);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(reopened, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.False(Directory.Exists(stale.ChildLocator));
        Assert.False(File.Exists(markerPath));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        string? freshCaptureId = null;
        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(
                    reopened,
                    fixture.Time,
                    reservationCheckpoint: () =>
                    {
                        freshCaptureId = fixture.CaptureId();
                        Assert.Equal(
                            RetentionCaptureMutationDisposition.StaleNoOp,
                            reopened.AbandonReservedAnalysisSdkDirectory(stale));
                    })
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        var fresh = Assert.IsType<string>(freshCaptureId);
        Assert.NotEqual(stale.CaptureId, fresh);
        Assert.False(Directory.Exists(Path.Combine(fixture.Parent, fresh)));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task AbandonReservedAnalysisSdkDirectory_CatalogFailureAfterExactChildCleanupIsForwardRecoverableAfterRestart()
    {
        var faultInjected = false;
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (!string.Equals(phase, "abandon_child_cleaned_before_catalog_commit", StringComparison.Ordinal)) return;
                faultInjected = true;
                throw new IOException("catalog commit fault");
            });
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        var markerPath = Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName);
        File.WriteAllBytes(markerPath, reservation.OwnershipMarker);
        var sibling = Path.Combine(fixture.Parent, "foreign-sibling.bin");
        var siblingBytes = new byte[] { 17, 23, 42, 99 };
        File.WriteAllBytes(sibling, siblingBytes);

        var disposition = fixture.Catalog.AbandonReservedAnalysisSdkDirectory(reservation);

        Assert.True(faultInjected);
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, disposition);
        Assert.False(Directory.Exists(reservation.ChildLocator));
        Assert.False(File.Exists(markerPath));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal(reservation.CaptureId, fixture.Scalar<string>(
            "SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7 AND phase='reserved'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        var reopened = new RetentionCatalogStore(
            RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath),
            fixture.Time);
        Assert.Equal(
            RetentionCaptureMutationDisposition.Applied,
            reopened.AbandonReservedAnalysisSdkDirectory(reservation));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.False(Directory.Exists(reservation.ChildLocator));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        await using var scope = await new AnalysisSdkDirectoryOwner(reopened, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.NotEqual(reservation.ChildLocator, scope.ChildDirectory);
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task AbandonReservedAnalysisSdkDirectory_ForeignEntryRacedAfterMarkerDeletionIsPreservedAndForwardRecoverable()
    {
        var markerDeleted = false;
        Action injectForeignEntry = static () => { };
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (!string.Equals(phase, "abandon_marker_deleted_before_empty_recheck", StringComparison.Ordinal)) return;
                injectForeignEntry();
                markerDeleted = true;
            });
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        var markerPath = Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName);
        File.WriteAllBytes(markerPath, reservation.OwnershipMarker);
        var foreignPath = Path.Combine(reservation.ChildLocator, "raced-foreign.bin");
        var foreignBytes = new byte[] { 3, 1, 4, 1, 5, 9 };
        injectForeignEntry = () => File.WriteAllBytes(foreignPath, foreignBytes);
        var sibling = Path.Combine(fixture.Parent, "foreign-sibling.bin");
        var siblingBytes = new byte[] { 17, 23, 42, 99 };
        File.WriteAllBytes(sibling, siblingBytes);
        var reservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            reservation.CaptureId);

        var firstDisposition = fixture.Catalog.AbandonReservedAnalysisSdkDirectory(reservation);

        Assert.True(markerDeleted);
        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, firstDisposition);
        Assert.False(File.Exists(markerPath));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignPath));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal(
            reservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", reservation.CaptureId));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        var reopened = new RetentionCatalogStore(
            RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath),
            fixture.Time);
        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(reopened, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.True(Directory.Exists(reservation.ChildLocator));
        Assert.False(File.Exists(markerPath));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignPath));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        var scope = await new AnalysisSdkDirectoryOwner(reopened, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        try
        {
            var freshCaptureId = fixture.CaptureId();
            Assert.NotEqual(reservation.CaptureId, freshCaptureId);
            Assert.NotEqual(reservation.ChildLocator, scope.ChildDirectory);
            Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
            Assert.Equal(1L, fixture.Scalar<long>(
                $"SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id='{freshCaptureId}'"));
            Assert.Equal(0L, fixture.Scalar<long>(
                $"SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id='{reservation.CaptureId}'"));
            Assert.Equal(1L, fixture.Scalar<long>(
                $"SELECT COUNT(*) FROM retention_leases AS lease JOIN retention_items AS item ON item.item_id=lease.item_id WHERE lease.lease_kind='operation' AND item.store_kind='analysis_sdk_directory' AND item.source_item_id='{freshCaptureId}'"));
            Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
            Assert.Equal(foreignBytes, File.ReadAllBytes(foreignPath));
            Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
        }
        finally
        {
            await scope.DisposeAsync();
        }

        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignPath));
        Assert.Equal(siblingBytes, File.ReadAllBytes(sibling));
    }

    [Fact]
    public void AbandonReservedAnalysisSdkDirectory_SqliteFailureIsConflictAndLeavesTheExactReservationUnchanged()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        var markerPath = Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName);
        File.WriteAllBytes(markerPath, reservation.OwnershipMarker);
        using var blocker = fixture.OpenConnection();
        using var command = blocker.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=0;";
        command.ExecuteNonQuery();
        using var transaction = blocker.BeginTransaction(deferred: false);

        var disposition = fixture.Catalog.AbandonReservedAnalysisSdkDirectory(reservation);

        Assert.Equal(RetentionCaptureMutationDisposition.Conflict, disposition);
        Assert.Equal(reservation.OwnershipMarker, File.ReadAllBytes(markerPath));
        Assert.Equal(reservation.CaptureId, fixture.Scalar<string>(
            "SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7 AND phase='reserved'"));
    }

    [Fact]
    public async Task OpenAsync_ActivationFailureDeletesTheExactOwnedChildBeforeAbandoning()
    {
        using var fixture = Fixture.Create();
        fixture.Time.Advance(TimeSpan.FromDays(91));

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(Directory.Exists(fixture.Parent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Parent));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_FutureOwnerClockCannotOverrideCatalogActivationClock()
    {
        using var fixture = Fixture.Create();
        var ownerTime = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero));

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, ownerTime)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal("2026-07-20T01:04:03.0000000+00:00", fixture.LeaseExpiry());
        Assert.False(scope.IsLeaseLost);
    }

    [Fact]
    public async Task Scope_FutureOwnerTimerCannotOverrideTheFrozenCatalogClock()
    {
        var catalogTime = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
        var renewalCallbacks = 0;
        using var fixture = Fixture.Create(
            catalogTime,
            phase =>
            {
                if (string.Equals(phase, "renewal_transaction_began", StringComparison.Ordinal))
                    Interlocked.Increment(ref renewalCallbacks);
            });
        var ownerTime = new MutableTimeProvider(new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero));
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, ownerTime)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var itemId = fixture.Scalar<string>("SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory'");
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", itemId);
        var callbacksBeforeTimer = Volatile.Read(ref renewalCallbacks);

        try
        {
            ownerTime.Advance(TimeSpan.FromMinutes(1));

            Assert.False(scope.IsLeaseLost);
            Assert.False(scope.LeaseLostToken.IsCancellationRequested);
            Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", itemId));
            Assert.Equal(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero), catalogTime.GetUtcNow());
            Assert.Equal(callbacksBeforeTimer + 1, Volatile.Read(ref renewalCallbacks));
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(150)]
    public async Task OpenAsync_PreparationAtOrAfterPublishedExpiryFailsBeforeReturningAndReleasesTheExactLease(int preparationDelaySeconds)
    {
        using var fixture = Fixture.Create();
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        IAnalysisSdkDirectoryScope? unexpectedScope = null;
        var exception = await Record.ExceptionAsync(async () =>
            unexpectedScope = await new AnalysisSdkDirectoryOwner(
                    fixture.Catalog,
                    fixture.Time,
                    childCreatedCheckpoint: _ => fixture.Time.Advance(TimeSpan.FromSeconds(preparationDelaySeconds)))
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        try
        {
            var targetItemId = fixture.Scalar<string>(
                "SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)");
            Assert.IsType<AnalysisOwnershipException>(exception);
            Assert.Null(unexpectedScope);
            Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.Equal(1L, fixture.Scalar<long>(
                "SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)"));
            Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", targetItemId));
            Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        }
        finally
        {
            if (unexpectedScope is not null) await unexpectedScope.DisposeAsync();
            Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
        }
    }

    [Fact]
    public async Task OpenAsync_UnexpectedEntryDuringOwnedChildCleanupAbandonsTheReservationAndFreshOpenPreservesTheForeignBytes()
    {
        using var fixture = Fixture.Create();
        string? preparedChild = null;
        var owner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            childCreatedCheckpoint: child =>
            {
                preparedChild = child;
                File.WriteAllText(Path.Combine(child, "replacement.txt"), "keep");
            });

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        var staleChild = Assert.IsType<string>(preparedChild);
        var replacementPath = Path.Combine(staleChild, "replacement.txt");
        var markerPath = Path.Combine(staleChild, AnalysisSdkDirectoryOwner.MarkerFileName);
        var markerBytes = File.ReadAllBytes(markerPath);
        Assert.Equal("keep", File.ReadAllText(replacementPath));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.NotEqual(staleChild, scope.ChildDirectory);
        Assert.Equal("keep", File.ReadAllText(replacementPath));
        Assert.Equal(markerBytes, File.ReadAllBytes(markerPath));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_LiveStoreIdentityDriftDuringOwnedChildCleanupPreservesTheChildAndReservation()
    {
        Action replaceStoreIdentity = static () => { };
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (string.Equals(phase, "activation_item_inserted", StringComparison.Ordinal))
                    throw new InvalidOperationException("activation checkpoint");
                if (string.Equals(phase, "cleanup_and_abandon_transaction_starting", StringComparison.Ordinal))
                    replaceStoreIdentity();
            });
        replaceStoreIdentity = fixture.ReplaceStoreIdentity;
        string? createdChild = null;
        var owner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            childCreatedCheckpoint: child => createdChild = child);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        var child = Assert.IsType<string>(createdChild);
        Assert.True(Directory.Exists(child));
        Assert.True(File.Exists(Path.Combine(child, AnalysisSdkDirectoryOwner.MarkerFileName)));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Theory]
    [InlineData("store_identity")]
    [InlineData("run_requested_at")]
    public async Task OpenAsync_LiveAuthorityDriftBeforePreparationDoesNotCreateTheChild(string mutation)
    {
        using var fixture = Fixture.Create();
        var childPreparationInvoked = false;
        var owner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            reservationCheckpoint: () =>
            {
                if (mutation == "store_identity")
                {
                    fixture.ReplaceStoreIdentity();
                }
                else
                {
                    fixture.Execute(
                        "UPDATE monitor_analysis_runs SET requested_at='2026-07-19T01:02:04.0000000+00:00' WHERE id=7;");
                }
            },
            childCreatedCheckpoint: _ => childPreparationInvoked = true);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.False(childPreparationInvoked);
        Assert.False(Directory.Exists(Path.Combine(fixture.Parent, fixture.CaptureId())));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task OpenAsync_TimerConstructionFailurePreservesTheActiveOwnedChildAndReleasesTheOperationLease()
    {
        using var fixture = Fixture.Create();
        var time = new ThrowingTimerTimeProvider(fixture.Time.GetUtcNow());

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(Directory.Exists(Path.Combine(fixture.Parent, fixture.CaptureId())));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task OpenAsync_ActiveDuplicatePreservesTheLiveMarkerOnlyChildAndOperationLease()
    {
        using var fixture = Fixture.Create();
        await using var first = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var marker = Path.Combine(first.ChildDirectory, AnalysisSdkDirectoryOwner.MarkerFileName);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(File.Exists(marker));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task OpenAsync_ConcurrentStaleReservationLoserPreservesTheWinnerActiveChildAndOperationLease()
    {
        using var fixture = Fixture.Create();
        var loserReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeLoser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losingOwner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            reservationCheckpoint: () =>
            {
                loserReserved.TrySetResult();
                resumeLoser.Task.GetAwaiter().GetResult();
            });
        var losingOpen = Task.Run(async () =>
            await losingOwner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));
        await loserReserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IAnalysisSdkDirectoryScope? winner = null;
        try
        {
            winner = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
            var marker = Path.Combine(winner.ChildDirectory, AnalysisSdkDirectoryOwner.MarkerFileName);
            var itemId = fixture.Scalar<string>("SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory'");
            var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", itemId);

            resumeLoser.TrySetResult();
            await Assert.ThrowsAsync<AnalysisOwnershipException>(async () => await losingOpen);

            Assert.True(Directory.Exists(winner.ChildDirectory));
            Assert.True(File.Exists(marker));
            Assert.Equal(
                new[] { AnalysisSdkDirectoryOwner.MarkerFileName },
                Directory.EnumerateFileSystemEntries(winner.ChildDirectory).Select(Path.GetFileName));
            Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", itemId));
            Assert.False(winner.IsLeaseLost);
            Assert.False(winner.LeaseLostToken.IsCancellationRequested);
        }
        finally
        {
            resumeLoser.TrySetResult();
            if (winner is not null) await winner.DisposeAsync();
            try
            {
                await losingOpen.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task OpenAsync_CancelledReservationPeerPreservesAChildBeingPreparedForActivation()
    {
        var abandonStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (string.Equals(phase, "abandon_transaction_starting", StringComparison.Ordinal))
                    abandonStarting.TrySetResult();
            });
        using var cancellation = new CancellationTokenSource();
        var cancelledPeerReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeCancelledPeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerChildCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledOwner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            reservationCheckpoint: () =>
            {
                cancelledPeerReserved.TrySetResult();
                resumeCancelledPeer.Task.GetAwaiter().GetResult();
            });
        var winningOwner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            childCreatedCheckpoint: child =>
            {
                winnerChildCreated.TrySetResult(child);
                resumeWinner.Task.GetAwaiter().GetResult();
            });
        Task<IAnalysisSdkDirectoryScope>? cancelledOpen = null;
        Task<IAnalysisSdkDirectoryScope>? winningOpen = null;
        IAnalysisSdkDirectoryScope? winner = null;
        try
        {
            cancelledOpen = Task.Factory.StartNew(
                async () => await cancelledOwner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            await cancelledPeerReserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            winningOpen = Task.Factory.StartNew(
                async () => await winningOwner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            var child = await winnerChildCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();
            resumeCancelledPeer.TrySetResult();
            await abandonStarting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(Directory.Exists(child));
            Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.False(cancelledOpen.IsCompleted);

            resumeWinner.TrySetResult();
            winner = await winningOpen.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledOpen);

            Assert.Equal(child, winner.ChildDirectory);
            Assert.True(File.Exists(Path.Combine(child, AnalysisSdkDirectoryOwner.MarkerFileName)));
            Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
            Assert.False(winner.IsLeaseLost);
            Assert.False(winner.LeaseLostToken.IsCancellationRequested);
        }
        finally
        {
            resumeCancelledPeer.TrySetResult();
            resumeWinner.TrySetResult();
            await DrainAndDisposeScopesAsync(winningOpen, cancelledOpen);
        }
    }

    [Fact]
    public async Task OpenAsync_AbandonAfterAbsentObservationPreventsAPeerFromCreatingAnOrphan()
    {
        var abandonObservedAbsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAbandon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (string.Equals(phase, "abandon_child_observed_absent", StringComparison.Ordinal))
                {
                    abandonObservedAbsent.TrySetResult();
                    resumeAbandon.Task.GetAwaiter().GetResult();
                }
                else if (string.Equals(phase, "activation_transaction_starting", StringComparison.Ordinal))
                {
                    activationStarting.TrySetResult();
                }
            });
        using var cancellation = new CancellationTokenSource();
        var peerReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumePeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerOwner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            reservationCheckpoint: () =>
            {
                peerReserved.TrySetResult();
                resumePeer.Task.GetAwaiter().GetResult();
            });
        Task<IAnalysisSdkDirectoryScope>? peerOpen = null;
        Task<IAnalysisSdkDirectoryScope>? cancelledOpen = null;
        try
        {
            peerOpen = Task.Factory.StartNew(
                async () => await peerOwner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            await peerReserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var child = Path.Combine(fixture.Parent, fixture.CaptureId());
            var cancelledOwner = new AnalysisSdkDirectoryOwner(
                fixture.Catalog,
                fixture.Time,
                reservationCheckpoint: cancellation.Cancel);
            cancelledOpen = Task.Factory.StartNew(
                async () => await cancelledOwner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            await abandonObservedAbsent.Task.WaitAsync(TimeSpan.FromSeconds(10));

            resumePeer.TrySetResult();
            await activationStarting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resumeAbandon.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledOpen);
            await Assert.ThrowsAsync<AnalysisOwnershipException>(async () => await peerOpen);

            Assert.False(Directory.Exists(child));
            Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
            Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
            Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        }
        finally
        {
            resumePeer.TrySetResult();
            resumeAbandon.TrySetResult();
            await DrainAndDisposeScopesAsync(cancelledOpen, peerOpen);
        }
    }

    private static async Task DrainAndDisposeScopesAsync(params Task<IAnalysisSdkDirectoryScope>?[] tasks)
    {
        var scopes = new List<IAnalysisSdkDirectoryScope>();
        foreach (var task in tasks)
        {
            if (task is null) continue;
            try
            {
                scopes.Add(await task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            catch (Exception)
            {
            }
        }

        var failures = new List<Exception>();
        foreach (var scope in scopes)
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    [Fact]
    public async Task Scope_ThrowingTimerDisposalStillReleasesTheOperationLease()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, new ThrowingTimerDisposeTimeProvider(fixture.Time.GetUtcNow()))
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () => await scope.DisposeAsync());

        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task Scope_RenewsOnTheFixedTimerAndReleasesTheOperationLeaseOnce()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var initialExpiry = fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'");

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotEqual(initialExpiry, fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'"));
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task Scope_NonrenewableGrantRemainsActiveUntilItsPublishedExpiry()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        Assert.Equal("2026-07-20T01:04:03.0000000+00:00", fixture.LeaseExpiry());
        fixture.Execute("UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='analysis_sdk_directory';");

        try
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(1));

            Assert.False(scope.IsLeaseLost);
            Assert.False(scope.LeaseLostToken.IsCancellationRequested);
            Assert.Equal("2026-07-20T01:04:03.0000000+00:00", fixture.LeaseExpiry());

            fixture.Time.Advance(TimeSpan.FromMinutes(1));

            Assert.True(scope.IsLeaseLost);
            Assert.True(scope.LeaseLostToken.IsCancellationRequested);
        }
        finally
        {
            fixture.Execute("UPDATE retention_items SET state='expiring',revision=1 WHERE store_kind='analysis_sdk_directory';");
            await scope.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(30, "2026-07-20T01:04:03.0000000+00:00")]
    [InlineData(60, "2026-07-20T01:05:03.0000000+00:00")]
    [InlineData(90, "2026-07-20T01:05:33.0000000+00:00")]
    public async Task Scope_ActivationPreparationDelayKeepsCallbacksOnThePublishedLeaseBoundaries(
        int preparationDelaySeconds,
        string expectedPublishedExpiryText)
    {
        var renewalCallbacks = 0;
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (string.Equals(phase, "renewal_transaction_began", StringComparison.Ordinal))
                    Interlocked.Increment(ref renewalCallbacks);
            });
        var preparationDelay = TimeSpan.FromSeconds(preparationDelaySeconds);
        var scope = await new AnalysisSdkDirectoryOwner(
                fixture.Catalog,
                fixture.Time,
                childCreatedCheckpoint: _ => fixture.Time.Advance(preparationDelay))
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var expectedPublishedExpiry = DateTimeOffset.Parse(
            expectedPublishedExpiryText,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(expectedPublishedExpiryText, fixture.LeaseExpiry());
        fixture.Execute("UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='analysis_sdk_directory';");
        var callbacksBeforeTimer = Volatile.Read(ref renewalCallbacks);

        try
        {
            var renewalBoundary = expectedPublishedExpiry - TimeSpan.FromMinutes(1);
            var firstCallbackAt = fixture.Time.GetUtcNow() > renewalBoundary
                ? fixture.Time.GetUtcNow()
                : renewalBoundary;
            fixture.Time.Advance(firstCallbackAt - fixture.Time.GetUtcNow());

            Assert.Equal(callbacksBeforeTimer + 1, Volatile.Read(ref renewalCallbacks));
            Assert.False(scope.IsLeaseLost);
            Assert.False(scope.LeaseLostToken.IsCancellationRequested);
            Assert.Equal(expectedPublishedExpiryText, fixture.LeaseExpiry());

            fixture.Time.Advance(expectedPublishedExpiry - fixture.Time.GetUtcNow());

            Assert.Equal(callbacksBeforeTimer + 2, Volatile.Read(ref renewalCallbacks));
            Assert.True(scope.IsLeaseLost);
            Assert.True(scope.LeaseLostToken.IsCancellationRequested);
            Assert.Equal(expectedPublishedExpiryText, fixture.LeaseExpiry());
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("marker_deleted")]
    [InlineData("marker_replaced")]
    [InlineData("reservation_marker_digest_replaced")]
    [InlineData("item_receipt_replaced")]
    public async Task Scope_SourceProofLossCancelsOnlyAtPublishedExpiryAndDisposesTheExactLease(string mutation)
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var exactItemId = fixture.Scalar<string>(
            "SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)");
        var captureId = fixture.CaptureId();
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var markerPath = Path.Combine(scope.ChildDirectory, AnalysisSdkDirectoryOwner.MarkerFileName);
        switch (mutation)
        {
            case "marker_deleted":
                File.Delete(markerPath);
                break;
            case "marker_replaced":
                var replacementMarker = File.ReadAllBytes(markerPath);
                replacementMarker[^1] ^= 1;
                File.WriteAllBytes(markerPath, replacementMarker);
                break;
            case "reservation_marker_digest_replaced":
                fixture.Execute(
                    "UPDATE retention_analysis_sdk_directory_reservations SET marker_sha256=randomblob(32) WHERE capture_id=$capture;",
                    ("$capture", captureId));
                break;
            case "item_receipt_replaced":
                fixture.Execute(
                    "UPDATE retention_items SET ownership_receipt=randomblob(32) WHERE item_id=$item;",
                    ("$item", exactItemId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var markerBefore = File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null;
        var itemBefore = fixture.FullRowDump("retention_items", "item_id", exactItemId);
        var reservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            captureId);
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", exactItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        var publishedExpiry = fixture.LeaseExpiry();

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(scope.IsLeaseLost);
        Assert.False(scope.LeaseLostToken.IsCancellationRequested);
        Assert.Equal(publishedExpiry, fixture.LeaseExpiry());
        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exactItemId));
        Assert.Equal(
            reservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", captureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.True(Directory.Exists(scope.ChildDirectory));
        Assert.Equal(markerBefore, File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(scope.IsLeaseLost);
        Assert.True(scope.LeaseLostToken.IsCancellationRequested);
        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exactItemId));
        Assert.Equal(
            reservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", captureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.True(Directory.Exists(scope.ChildDirectory));
        Assert.Equal(markerBefore, File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null);

        await scope.DisposeAsync();

        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.True(Directory.Exists(scope.ChildDirectory));
        Assert.Equal(markerBefore, File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task Scope_ReservationOwnerTokenReplacementCancelsAtHeartbeatAndDisposalPreservesReplacementAuthority()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var exactItemId = fixture.Scalar<string>(
            "SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)");
        var captureId = fixture.CaptureId();
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var replacementOwnerToken = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        fixture.Execute(
            "UPDATE retention_analysis_sdk_directory_reservations SET owner_token=$token WHERE capture_id=$capture;",
            ("$token", replacementOwnerToken),
            ("$capture", captureId));
        var itemBefore = fixture.FullRowDump("retention_items", "item_id", exactItemId);
        var replacementReservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            captureId);
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", exactItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(scope.IsLeaseLost);
        Assert.True(scope.LeaseLostToken.IsCancellationRequested);
        Assert.Contains(Convert.ToHexString(replacementOwnerToken), replacementReservationBefore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exactItemId));
        Assert.Equal(
            replacementReservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", captureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));

        await scope.DisposeAsync();

        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(
            replacementReservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", captureId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task Scope_ReplacementLeaseTupleCancelsAtHeartbeatAndDisposalLeavesOtherTuplesUnchanged()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var exactItemId = fixture.Scalar<string>(
            "SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory' AND source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)");
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        fixture.Execute(
            "UPDATE retention_leases SET owner='replacement-owner',generation=generation+1 WHERE item_id=$item AND lease_kind='operation';",
            ("$item", exactItemId));
        var replacementBefore = fixture.FullRowDump("retention_leases", "item_id", exactItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(scope.IsLeaseLost);
        Assert.True(scope.LeaseLostToken.IsCancellationRequested);
        Assert.Equal(replacementBefore, fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () => await scope.DisposeAsync());

        Assert.Equal(replacementBefore, fixture.FullRowDump("retention_leases", "item_id", exactItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        fixture.Execute("DELETE FROM retention_leases WHERE item_id=$item;", ("$item", exactItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task Scope_RenewedPublishedExpiryControlsTheCatalogBusyBoundary()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        try
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("2026-07-20T01:05:03.0000000+00:00", fixture.LeaseExpiry());

            using var blocker = fixture.OpenConnection();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            try
            {
                fixture.Time.Advance(TimeSpan.FromMinutes(1));

                Assert.False(scope.IsLeaseLost);
                Assert.False(scope.LeaseLostToken.IsCancellationRequested);

                fixture.Time.Advance(TimeSpan.FromMinutes(1));

                Assert.True(scope.IsLeaseLost);
                Assert.True(scope.LeaseLostToken.IsCancellationRequested);
            }
            finally
            {
                using var rollback = blocker.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task Scope_RenewalBlockedPastPublishedExpiryDoesNotResurrectTheLeaseAndCancelsBeforeReturning()
    {
        var time = new BlockedBoundaryTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
        var renewalTransactionBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockRenewal = false;
        using var fixture = Fixture.Create(
            time,
            phase =>
            {
                if (!blockRenewal || !string.Equals(phase, "renewal_transaction_began", StringComparison.Ordinal)) return;
                renewalTransactionBegan.TrySetResult();
                resumeRenewal.Task.GetAwaiter().GetResult();
            });
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        blockRenewal = true;
        Assert.Equal("2026-07-20T01:04:03.0000000+00:00", fixture.LeaseExpiry());

        try
        {
            time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 1, 3, 3, TimeSpan.Zero));
            time.BeginCallback();
            try
            {
                await renewalTransactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(10));
                time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 1, 4, 3, TimeSpan.Zero));
            }
            finally
            {
                resumeRenewal.TrySetResult();
            }

            await time.CallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(
                () => Assert.Equal("2026-07-20T01:04:03.0000000+00:00", fixture.LeaseExpiry()),
                () => Assert.True(scope.IsLeaseLost),
                () => Assert.True(scope.LeaseLostToken.IsCancellationRequested));
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task Scope_RenewalObservedAtItsNewPublishedExpiryCancelsInTheSameCallback()
    {
        var advanceObservation = false;
        Action advanceCatalogClock = static () => { };
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (advanceObservation && string.Equals(phase, "renewal_observation_starting", StringComparison.Ordinal))
                    advanceCatalogClock();
            });
        advanceCatalogClock = () => fixture.Time.Advance(RetentionV1Constants.LeaseDuration);
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        try
        {
            advanceObservation = true;
            fixture.Time.Advance(RetentionV1Constants.LeaseRenewalDeadline);

            Assert.Equal("2026-07-20T01:05:03.0000000+00:00", fixture.LeaseExpiry());
            Assert.Equal(new DateTimeOffset(2026, 7, 20, 1, 5, 3, TimeSpan.Zero), fixture.Time.GetUtcNow());
            Assert.True(scope.IsLeaseLost);
            Assert.True(scope.LeaseLostToken.IsCancellationRequested);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("cleanup_read_denied")]
    public async Task Scope_DisposeAfterLifecycleMutationReleasesOnlyTheExactOperationLeaseOnce(string mutation)
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var itemId = fixture.Scalar<string>("SELECT item_id FROM retention_items WHERE source_item_id=(SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7)");
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        fixture.ApplyLifecycleMutation(itemId, mutation);
        var itemBeforeRelease = fixture.FullRowDump("retention_items", "item_id", itemId);

        await scope.DisposeAsync();

        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", itemId));
        Assert.Equal(itemBeforeRelease, fixture.FullRowDump("retention_items", "item_id", itemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        await scope.DisposeAsync();
        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", itemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task Scope_DisposeWaitsForAnInFlightRenewalBeforeReleasing()
    {
        using var fixture = Fixture.Create();
        var time = new GatedTimerTimeProvider(fixture.RequestedAt.AddDays(1));
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        time.BeginCallback();
        await time.CallbackStarted.Task;

        var dispose = scope.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        time.AllowCallback.SetResult();
        await dispose;
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string databasePath, string parent, RetentionCatalogStore catalog, MutableTimeProvider? time)
            => (DatabasePath, Parent, Catalog, timeForTests) = (databasePath, parent, catalog, time);

        private readonly MutableTimeProvider? timeForTests;

        internal string DatabasePath { get; }
        internal string Parent { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal MutableTimeProvider Time => Assert.IsType<MutableTimeProvider>(timeForTests);
        internal DateTimeOffset RequestedAt => new(2026, 7, 19, 1, 2, 3, TimeSpan.Zero);

        internal static Fixture Create(TimeProvider? timeProvider = null, Action<string>? checkpoint = null)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"analysis-sdk-directory-owner-{Guid.NewGuid():N}.sqlite");
            var mutableTime = timeProvider as MutableTimeProvider;
            if (timeProvider is null)
            {
                mutableTime = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
                timeProvider = mutableTime;
            }
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, timeProvider);
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE monitor_analysis_runs(id INTEGER PRIMARY KEY, requested_at TEXT NOT NULL, retention_owner_token BLOB NOT NULL); INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(7,'2026-07-19T01:02:03.0000000+00:00',zeroblob(32)); INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";
                command.ExecuteNonQuery();
            }
            var catalog = checkpoint is null
                ? new RetentionCatalogStore(context, timeProvider)
                : new RetentionCatalogStore(context, timeProvider, _ => { }, checkpoint);
            return new Fixture(databasePath, Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"sdk-owner-parent-{Guid.NewGuid():N}")), catalog, mutableTime);
        }

        internal string CaptureId() => Scalar<string>("SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7");
        internal string LeaseExpiry() => Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'");
        internal void ReplaceStoreIdentity()
        {
            using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(DatabasePath, SqliteOpenMode.ReadWrite, enforceForeignKeys: false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE retention_store_instances SET store_instance_id=lower(hex(randomblob(16))) WHERE id=1;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        internal SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            return connection;
        }
        internal void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        internal T Scalar<T>(string sql)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is null || value is DBNull ? default! : (T)Convert.ChangeType(value, typeof(T));
        }

        internal RetentionAnalysisSdkDirectoryOperationLease ActivateUnrelatedAnalysisSdkDirectory()
        {
            Execute("INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(8,'2026-07-19T01:02:04.0000000+00:00',zeroblob(32));");
            var reservation = Catalog.ReserveAnalysisSdkDirectory(8, Path.Combine(Parent, "unrelated-parent"));
            Directory.CreateDirectory(reservation.ChildLocator);
            File.WriteAllBytes(
                Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName),
                reservation.OwnershipMarker);
            var activation = Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
                reservation,
                reservation.OwnershipMarker,
                exclusivelyCreatedEmptyChild: true);
            return Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        }

        internal void ApplyLifecycleMutation(string itemId, string mutation)
        {
            var at = Time.GetUtcNow().ToString("O");
            switch (mutation)
            {
                case "pin":
                    Execute(
                        "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id=$item;",
                        ("$item", itemId));
                    break;
                case "unpin":
                    Execute(
                        "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id=$item; " +
                        "UPDATE retention_items SET state='expiring',revision=revision+1 WHERE item_id=$item;",
                        ("$item", itemId));
                    break;
                case "cleanup_read_denied":
                    Execute(
                        "UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$at,revision=revision+1 WHERE item_id=$item; " +
                        "UPDATE retention_items SET state='deletion_queued',queued_at=$at,revision=revision+1 WHERE item_id=$item;",
                        ("$item", itemId),
                        ("$at", at));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }

        internal string FullRowDump(string table, string keyColumn, object keyValue)
        {
            using var connection = OpenConnection();
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
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
            foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }) if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class GatedTimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly GatedTimer timer = new();
        public override DateTimeOffset GetUtcNow() => now;
        public TaskCompletionSource CallbackStarted => timer.CallbackStarted;
        public TaskCompletionSource AllowCallback => timer.AllowCallback;
        public void BeginCallback() => timer.BeginCallback();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer.SetCallback(callback, state);
            return timer;
        }

        private sealed class GatedTimer : ITimer
        {
            private TimerCallback? callback;
            private object? state;
            private Task? callbackTask;
            internal TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource AllowCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void SetCallback(TimerCallback value, object? valueState) => (callback, state) = (value, valueState);
            internal void BeginCallback() => callbackTask = Task.Run(async () =>
            {
                CallbackStarted.SetResult();
                await AllowCallback.Task;
                callback!(state);
            });
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public async ValueTask DisposeAsync() { if (callbackTask is not null) await callbackTask; }
        }
    }

    private sealed class BlockedBoundaryTimeProvider(DateTimeOffset initialNow) : TimeProvider
    {
        private readonly object gate = new();
        private readonly ManualTimer timer = new();
        private DateTimeOffset now = initialNow;
        internal TaskCompletionSource CallbackCompleted => timer.CallbackCompleted;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        internal void SetUtcNow(DateTimeOffset value)
        {
            lock (gate) now = value;
        }

        internal void BeginCallback() => timer.BeginCallback();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer.SetCallback(callback, state);
            return timer;
        }

        private sealed class ManualTimer : ITimer
        {
            private TimerCallback? callback;
            private object? state;
            private Task? callbackTask;

            internal TaskCompletionSource CallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal void SetCallback(TimerCallback value, object? valueState) => (callback, state) = (value, valueState);

            internal void BeginCallback()
            {
                callbackTask = Task.Run(() =>
                {
                    try
                    {
                        callback!(state);
                        CallbackCompleted.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        CallbackCompleted.TrySetException(exception);
                    }
                });
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => callbackTask is null ? ValueTask.CompletedTask : new ValueTask(callbackTask);
        }
    }

    private sealed class ThrowingTimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => throw new InvalidOperationException();
    }

    private sealed class ThrowingTimerDisposeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new ThrowingTimer();

        private sealed class ThrowingTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException());
        }
    }
}

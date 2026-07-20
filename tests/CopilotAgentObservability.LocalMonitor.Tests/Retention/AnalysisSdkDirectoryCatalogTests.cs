using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class AnalysisSdkDirectoryCatalogTests
{
    [Fact]
    public void ReserveAnalysisSdkDirectory_BindsTheExactRunAuthorityWithoutMutatingTheFilesystem()
    {
        using var fixture = Fixture.Create();

        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);

        Assert.Equal(7, reservation.AnalysisRunId);
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal("2026-07-19T01:02:03.0000000+00:00", fixture.Scalar<string>("SELECT requested_at FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(639200197230000000L, fixture.Scalar<long>("SELECT requested_at_utc_ticks FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.False(Directory.Exists(reservation.ChildLocator));
        Assert.DoesNotContain(fixture.Parent, reservation.ToString(), StringComparison.Ordinal);
        Assert.False(fixture.RunOwnerToken.SequenceEqual(reservation.OwnerToken), "Fresh ownership material was not generated.");
    }

    [Fact]
    public void ActivateAnalysisSdkDirectory_CreatesTheItemAndOperationLeaseAtomically()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);

        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, exclusivelyCreatedEmptyChild: true, new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));

        Assert.True(activation.IsActive);
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(RetentionRenewalResult.Renewed, fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(activation.Lease!, new DateTimeOffset(2026, 7, 20, 1, 3, 3, TimeSpan.Zero)));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
        Assert.Equal(RetentionMutationDisposition.StaleNoOp, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
    }

    [Fact]
    public void ReserveAnalysisSdkDirectory_FailsClosedForMissingMalformedOrNonCanonicalAuthority()
    {
        using var fixture = Fixture.Create();
        Assert.Throws<RetentionCatalogUnavailableException>(() => fixture.Catalog.ReserveAnalysisSdkDirectory(8, fixture.Parent));
        fixture.Execute("INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(8,'not-a-timestamp',zeroblob(32));");
        Assert.Throws<RetentionCatalogUnavailableException>(() => fixture.Catalog.ReserveAnalysisSdkDirectory(8, fixture.Parent));
        Assert.Throws<RetentionCatalogUnavailableException>(() => fixture.Catalog.ReserveAnalysisSdkDirectory(7, "relative-parent"));
    }

    [Fact]
    public async Task ReserveAnalysisSdkDirectory_SameRunConcurrentCallsReturnOneCapability()
    {
        using var fixture = Fixture.Create(); using var barrier = new Barrier(2); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { barrier.SignalAndWait(cancellation.Token); return fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent); }, cancellation.Token)).ToArray();
        var reservations = await Task.WhenAll(calls).WaitAsync(cancellation.Token);
        Assert.Single(reservations.Select(x => x.CaptureId).Distinct());
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public void ActivateAnalysisSdkDirectory_RejectsWrongMarkerAndExpiryBoundaryWithoutCreatingAnItem()
    {
        using var fixture = Fixture.Create(); var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, new byte[] { 1 }, true, new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero)).IsActive);
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true, new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero)).IsActive);
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
    }

    [Fact]
    public void Reservation_ReopensIdempotentlyAndOnlyExactReservedCapabilityCanAbandon()
    {
        using var fixture = Fixture.Create(); var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path));
        Assert.Equal(reservation.CaptureId, reopened.ReserveAnalysisSdkDirectory(7, fixture.Parent).CaptureId);
        Assert.NotNull(reopened.LoadAnalysisSdkDirectoryRecovery(7));
        Assert.Equal(RetentionCaptureMutationDisposition.Applied, reopened.AbandonReservedAnalysisSdkDirectory(reservation));
        Assert.Null(reopened.LoadAnalysisSdkDirectoryRecovery(7));
    }

    [Fact]
    public void Reservation_ReopenWithConfiguredParentDriftFailsClosedWithoutMutatingReservedAuthority()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        var changedParent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sdk-parent-drift-{Guid.NewGuid():N}");
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path));

        Assert.Throws<RetentionCatalogUnavailableException>(() => reopened.ReserveAnalysisSdkDirectory(7, changedParent));

        Assert.Equal(reservation.CaptureId, fixture.Scalar<string>("SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(fixture.Parent, fixture.Scalar<string>("SELECT parent_locator FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public void Reservation_ActiveExistingReservationWithTheSameParentRemainsAvailableForOwnerRejection()
    {
        using var fixture = Fixture.Create(); var reserved = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Assert.True(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reserved, reserved.OwnershipMarker, true, new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero)).IsActive);
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path));
        var active = reopened.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Assert.Equal(reserved.CaptureId, active.CaptureId);
        Assert.Equal(RetentionAnalysisSdkDirectoryPhase.Active, active.Phase);
    }

    [Fact]
    public async Task FirstDeleteIntent_SnapshotsOnlyTheOwnedChildAndSealsTheReservationAtomically()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        File.WriteAllBytes(Path.Combine(reservation.ChildLocator, RetentionFileCaptureContracts.OwnerMarkerName), reservation.OwnershipMarker);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "result.txt"), "synthetic");
        var now = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true, now);
        Assert.True(activation.IsActive);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
        var item = fixture.Scalar<string>("SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory'");
        fixture.Execute("INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
        fixture.Execute($"UPDATE retention_items SET state='deletion_queued',read_denied_at='{now:O}',queued_at='{now:O}' WHERE item_id='{item}';");
        var claim = (await fixture.Catalog.TryClaimDeletionAsync(new(item, 1, RetentionWorkKind.Queued), "worker", now, CancellationToken.None)).Claim!;

        var intent = await fixture.Catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);

        Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
        Assert.Equal("sealed", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(2L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_analysis_sdk_directory_members"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_analysis_sdk_directory_members WHERE relative_path LIKE '%..%'"));

        var result = await new AnalysisSdkDirectoryRetentionAdapter(fixture.Catalog, new MutableTimeProvider(now)).DeleteAsync(new(
            claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration,
            claim.SourceIdentity, claim.PrivateLocator, intent.IntentCursor, CancellationToken.None));
        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.False(Directory.Exists(reservation.ChildLocator));
    }

    [Fact]
    public async Task DeletionAdapter_RejectsUnexpectedChildEntryWithoutTouchingTheParentSibling()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.Parent);
        var sibling = Path.Combine(fixture.Parent, "sibling.txt"); File.WriteAllText(sibling, "keep");
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        File.WriteAllBytes(Path.Combine(reservation.ChildLocator, RetentionFileCaptureContracts.OwnerMarkerName), reservation.OwnershipMarker);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "result.txt"), "synthetic");
        var now = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true, now);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
        var item = fixture.Scalar<string>("SELECT item_id FROM retention_items WHERE store_kind='analysis_sdk_directory'");
        fixture.Execute("INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
        fixture.Execute($"UPDATE retention_items SET state='deletion_queued',read_denied_at='{now:O}',queued_at='{now:O}' WHERE item_id='{item}';");
        var claim = (await fixture.Catalog.TryClaimDeletionAsync(new(item, 1, RetentionWorkKind.Queued), "worker", now, CancellationToken.None)).Claim!;
        var intent = await fixture.Catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
        var extra = Path.Combine(reservation.ChildLocator, "unexpected.txt"); File.WriteAllText(extra, "do-not-delete");

        var result = await new AnalysisSdkDirectoryRetentionAdapter(fixture.Catalog, new MutableTimeProvider(now)).DeleteAsync(new(
            claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration,
            claim.SourceIdentity, claim.PrivateLocator, intent.IntentCursor, CancellationToken.None));

        Assert.Equal(RetentionAdapterDisposition.TerminalFailure, result.Disposition);
        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
        Assert.True(File.Exists(extra));
        Assert.True(File.Exists(sibling));
    }

    [Theory]
    [InlineData("activation_item_inserted")]
    [InlineData("activation_lease_inserted")]
    [InlineData("activation_phase_updated")]
    public void ActivateAnalysisSdkDirectory_CheckpointFailureRollsBackAllDurableWrites(string checkpoint)
    {
        using var fixture = Fixture.Create(phase => { if (phase == checkpoint) throw new InvalidOperationException("checkpoint"); });
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true, new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero)).IsActive);
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Theory]
    [InlineData("UPDATE retention_analysis_sdk_directory_reservations SET requested_at_utc_ticks=1 WHERE analysis_run_id=7")]
    [InlineData("UPDATE retention_analysis_sdk_directory_reservations SET child_locator='C:/tampered' WHERE analysis_run_id=7")]
    [InlineData("UPDATE retention_analysis_sdk_directory_reservations SET owner_token=randomblob(32) WHERE analysis_run_id=7")]
    public void Reservation_SchemaValidOrDirectTamperingFailsClosed(string sql)
    {
        using var fixture = Fixture.Create();
        fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.Execute(sql);
        Assert.Throws<RetentionCatalogUnavailableException>(() => fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, string parent, RetentionCatalogStore catalog, byte[] runOwnerToken) => (Path, Parent, Catalog, RunOwnerToken) = (path, parent, catalog, runOwnerToken);
        internal string Path { get; }
        internal string Parent { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal byte[] RunOwnerToken { get; }

        internal static Fixture Create(Action<string>? checkpoint = null)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-sdk-directory-{Guid.NewGuid():N}.sqlite");
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE monitor_analysis_runs(id INTEGER PRIMARY KEY, requested_at TEXT NOT NULL, retention_owner_token BLOB NOT NULL); INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(7,'2026-07-19T01:02:03.0000000+00:00',zeroblob(32));";
                command.ExecuteNonQuery();
            }
            var catalog = checkpoint is null ? new RetentionCatalogStore(context) : new RetentionCatalogStore(context, TimeProvider.System, _ => { }, checkpoint);
            return new(path, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sdk-parent-{Guid.NewGuid():N}"), catalog, new byte[32]);
        }

        internal T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}

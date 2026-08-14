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
        fixture.CreateOwnedChild(reservation);
        fixture.RegisterCoverage();

        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, exclusivelyCreatedEmptyChild: true);

        Assert.True(activation.IsActive);
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RetentionOperationRenewalDisposition.Renewed, fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(activation.Lease!));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
        Assert.Equal(RetentionMutationDisposition.StaleNoOp, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
    }

    [Theory]
    [InlineData("missing_child")]
    [InlineData("missing_marker")]
    [InlineData("mismatched_marker")]
    public void ActivateAnalysisSdkDirectory_RequiresTheExactPhysicalOwnershipMarker(string scenario)
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        if (scenario != "missing_child")
            Directory.CreateDirectory(reservation.ChildLocator);
        if (scenario == "mismatched_marker")
        {
            File.WriteAllBytes(
                Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName),
                new byte[reservation.OwnershipMarker.Length]);
        }

        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);

        Assert.False(activation.IsActive);
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task ActivateAnalysisSdkDirectory_NearPolicyBoundaryPublishesFullLeaseAndDefersCleanupClaimUntilExactExpiry()
    {
        var policyBoundary = new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);
        var activatedAt = policyBoundary.AddTicks(-1);
        using var fixture = Fixture.Create(timeProvider: new MutableTimeProvider(activatedAt));
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);

        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);

        Assert.True(activation.IsActive);
        var expectedLeaseExpiry = activatedAt.Add(RetentionV1Constants.LeaseDuration);
        Assert.Equal(policyBoundary.ToString("O"), fixture.Scalar<string>("SELECT expires_at FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(expectedLeaseExpiry.ToString("O"), fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'"));

        fixture.RegisterCoverage();
        var batch = await fixture.Catalog.PrepareCleanupBatchAsync(
            policyBoundary,
            RetentionV1Constants.ExpiryScanItemLimit,
            RetentionV1Constants.ClaimBatchLimit,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var work = Assert.Single(batch.Work);
        Assert.Equal("deletion_queued", fixture.Scalar<string>("SELECT state FROM retention_items WHERE item_id='" + work.ItemId + "'"));
        var grant = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease).Grant;
        Assert.True(fixture.GrantUsable(grant, expectedLeaseExpiry.AddTicks(-1)));

        var quiescing = await fixture.Catalog.TryClaimDeletionAsync(
            work,
            "sdk-boundary-owner",
            expectedLeaseExpiry.AddTicks(-1),
            CancellationToken.None);
        Assert.Equal(RetentionClaimDisposition.Quiescing, quiescing.Disposition);
        Assert.Equal(expectedLeaseExpiry, quiescing.QuiescenceRetryAt);
        Assert.False(fixture.GrantUsable(grant, expectedLeaseExpiry));

        var claimed = await fixture.Catalog.TryClaimDeletionAsync(
            work,
            "sdk-boundary-owner",
            expectedLeaseExpiry,
            CancellationToken.None);
        Assert.Equal(RetentionClaimDisposition.Claimed, claimed.Disposition);
    }

    [Fact]
    public async Task ActivateAnalysisSdkDirectory_ExpiryAdmissionUsesBoundaryTimeAfterImmediateTransactionWait()
    {
        var policyBoundary = new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);
        var time = new MutableTimeProvider(policyBoundary.AddTicks(-1));
        var transactionBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (!string.Equals(phase, "activation_transaction_began", StringComparison.Ordinal)) return;
                transactionBegan.TrySetResult();
                resumeActivation.Task.GetAwaiter().GetResult();
            },
            timeProvider: time);
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activationTask = Task.Run(() => fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true));

        try
        {
            await transactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(activationTask.IsCompleted);
            time.Advance(TimeSpan.FromTicks(1));
        }
        finally
        {
            resumeActivation.TrySetResult();
        }

        var activation = await activationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(activation.IsActive);
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task ActivateAnalysisSdkDirectory_FullLeaseUsesBoundaryTimeAfterImmediateTransactionWait()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
        var transactionBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (!string.Equals(phase, "activation_transaction_began", StringComparison.Ordinal)) return;
                transactionBegan.TrySetResult();
                resumeActivation.Task.GetAwaiter().GetResult();
            },
            timeProvider: time);
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activationTask = Task.Run(() => fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true));

        try
        {
            await transactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(activationTask.IsCompleted);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        finally
        {
            resumeActivation.TrySetResult();
        }

        var activation = await activationTask.WaitAsync(TimeSpan.FromSeconds(5));
        var lease = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);

        Assert.True(activation.IsActive);
        Assert.Equal("2026-07-20T01:05:03.0000000+00:00", fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'"));
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 1, 5, 3, TimeSpan.Zero), lease.Grant.LeaseExpiresAt);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(lease));
    }

    [Fact]
    public void RenewAnalysisSdkDirectoryOperationLease_PinRevisionDriftIsNonrenewableUntilPublishedExpiry()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var lease = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var initialExpiry = fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'");
        fixture.RegisterCoverage();
        fixture.Execute("UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='analysis_sdk_directory';");
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable,
            fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(lease));
        Assert.Equal(initialExpiry, fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'"));

        fixture.Execute("UPDATE retention_items SET state='expiring',revision=revision+1 WHERE store_kind='analysis_sdk_directory'; UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='analysis_sdk_directory';");
        Assert.Equal(
            RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable,
            fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(lease));
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(
            RetentionOperationRenewalDisposition.LeaseLost,
            fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(lease));
    }

    [Theory]
    [InlineData("marker_deleted")]
    [InlineData("marker_replaced")]
    [InlineData("reservation_marker_digest_replaced")]
    [InlineData("item_receipt_replaced")]
    public void RenewAnalysisSdkDirectoryOperationLease_SourceProofLossDoesNotExtendOrMutateAuthority(string mutation)
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var exact = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        fixture.RegisterCoverage();
        var markerPath = System.IO.Path.Combine(
            reservation.ChildLocator,
            RetentionAnalysisSdkDirectoryOwnershipMarker.FileName);
        switch (mutation)
        {
            case "marker_deleted":
                File.Delete(markerPath);
                break;
            case "marker_replaced":
                var replacementMarker = reservation.OwnershipMarker.ToArray();
                replacementMarker[^1] ^= 1;
                File.WriteAllBytes(markerPath, replacementMarker);
                break;
            case "reservation_marker_digest_replaced":
                fixture.Execute(
                    "UPDATE retention_analysis_sdk_directory_reservations SET marker_sha256=randomblob(32) WHERE capture_id=$capture;",
                    ("$capture", reservation.CaptureId));
                break;
            case "item_receipt_replaced":
                fixture.Execute(
                    "UPDATE retention_items SET ownership_receipt=randomblob(32) WHERE item_id=$item;",
                    ("$item", exact.Grant.ItemId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var markerBefore = File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null;
        var itemBefore = fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId);
        var reservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            reservation.CaptureId);
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        DateTimeOffset publishedExpiryBefore;
        using (var publication = exact.Grant.EnterLeasePublication())
            publishedExpiryBefore = publication.LeaseExpiresAt;
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable,
            fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(exact));

        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId));
        Assert.Equal(
            reservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", reservation.CaptureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.True(Directory.Exists(reservation.ChildLocator));
        Assert.Equal(markerBefore, File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null);
        using (var publication = exact.Grant.EnterLeasePublication())
            Assert.Equal(publishedExpiryBefore, publication.LeaseExpiresAt);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.True(Directory.Exists(reservation.ChildLocator));
        Assert.Equal(markerBefore, File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public void RenewAnalysisSdkDirectoryOperationLease_ReservationOwnerTokenReplacementLosesTheGrantAndExactReleasePreservesReplacementAuthority()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var exact = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        fixture.RegisterCoverage();
        var replacementOwnerToken = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        fixture.Execute(
            "UPDATE retention_analysis_sdk_directory_reservations SET owner_token=$token WHERE capture_id=$capture;",
            ("$token", replacementOwnerToken),
            ("$capture", reservation.CaptureId));
        var itemBefore = fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId);
        var replacementReservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            reservation.CaptureId);
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        DateTimeOffset publishedExpiryBefore;
        using (var publication = exact.Grant.EnterLeasePublication())
            publishedExpiryBefore = publication.LeaseExpiresAt;
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            RetentionOperationRenewalDisposition.LeaseLost,
            fixture.Catalog.RenewAnalysisSdkDirectoryOperationLease(exact));

        Assert.Contains(Convert.ToHexString(replacementOwnerToken), replacementReservationBefore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId));
        Assert.Equal(
            replacementReservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", reservation.CaptureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        using (var publication = exact.Grant.EnterLeasePublication())
            Assert.Equal(publishedExpiryBefore, publication.LeaseExpiresAt);

        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(
            replacementReservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", reservation.CaptureId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task RenewAndObserveAnalysisSdkDirectoryOperationLease_PublicationWaitCrossingExactExpiryDoesNotResurrectAuthority()
    {
        var renewalTransactionBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture.Create(
            checkpoint: phase =>
            {
                if (string.Equals(phase, "renewal_transaction_began", StringComparison.Ordinal))
                    renewalTransactionBegan.TrySetResult();
            });
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var exact = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var itemBefore = fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId);
        var reservationBefore = fixture.FullRowDump(
            "retention_analysis_sdk_directory_reservations",
            "capture_id",
            reservation.CaptureId);
        var leaseBefore = fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId);
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        DateTimeOffset publishedExpiryBefore;
        using (var publication = exact.Grant.EnterLeasePublication())
            publishedExpiryBefore = publication.LeaseExpiresAt;
        fixture.Time.Advance(RetentionV1Constants.LeaseRenewalDeadline);

        var publicationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var publicationHolder = Task.Run(() =>
        {
            using var publication = exact.Grant.EnterLeasePublication();
            publicationHeld.TrySetResult();
            if (!releasePublication.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Publication scope was not released by the test.");
        });
        Task<RetentionAnalysisSdkDirectoryLeaseObservation>? renewal = null;
        var completedAfterBegin = true;
        var completedAtBoundary = true;
        try
        {
            await publicationHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            renewal = Task.Run(() => fixture.Catalog.RenewAndObserveAnalysisSdkDirectoryOperationLease(exact));
            await renewalTransactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completedAfterBegin = renewal.IsCompleted;
            fixture.Time.Advance(publishedExpiryBefore - fixture.Time.GetUtcNow());
            completedAtBoundary = renewal.IsCompleted;
        }
        finally
        {
            releasePublication.Set();
            Exception? backgroundFailure = null;
            try
            {
                await publicationHolder.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                backgroundFailure = exception;
            }
            if (renewal is not null)
            {
                try
                {
                    await renewal.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    backgroundFailure ??= exception;
                }
            }
            if (backgroundFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(backgroundFailure).Throw();
        }

        var observation = await Assert.IsType<Task<RetentionAnalysisSdkDirectoryLeaseObservation>>(renewal)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completedAfterBegin);
        Assert.Equal(publishedExpiryBefore, fixture.Time.GetUtcNow());
        Assert.False(completedAtBoundary);
        Assert.Equal(RetentionOperationRenewalDisposition.LeaseLost, observation.Disposition);
        Assert.Equal(publishedExpiryBefore, observation.ObservedAt);
        Assert.Equal(publishedExpiryBefore, observation.PublishedLeaseExpiresAt);
        Assert.Equal(itemBefore, fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId));
        Assert.Equal(
            reservationBefore,
            fixture.FullRowDump("retention_analysis_sdk_directory_reservations", "capture_id", reservation.CaptureId));
        Assert.Equal(leaseBefore, fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        using (var publication = exact.Grant.EnterLeasePublication())
            Assert.Equal(publishedExpiryBefore, publication.LeaseExpiresAt);

        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Fact]
    public async Task RenewOperationLease_UnchangedPinnedSdkGrantRenewsPastHistoricalItemExpiry()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(activation.Lease!));
        var policyExpiry = DateTimeOffset.Parse(
            fixture.Scalar<string>("SELECT expires_at FROM retention_items WHERE store_kind='analysis_sdk_directory'"));
        fixture.Execute("UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='analysis_sdk_directory';");
        fixture.RegisterCoverage();
        var key = new RetentionOwnershipKey(
            reservation.StoreInstanceId,
            RetentionStoreKind.AnalysisSdkDirectory,
            reservation.CaptureId);
        var readAt = policyExpiry.AddTicks(1);
        fixture.Time.Advance(readAt - fixture.Time.GetUtcNow());
        var admission = await fixture.Catalog.ReadAsync(
            new RetentionReadRequest(key, RetentionReadKind.Operation, readAt, 2),
            static (_, _, _, _) => ValueTask.FromResult<string?>("materialized"),
            CancellationToken.None);
        var readLease = Assert.IsType<RetentionReadLease<string>>(admission.Lease);
        var grant = Assert.IsType<RetentionReadGrant>(readLease.Grant);
        var renewedAt = readAt.AddMinutes(1);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        try
        {
            Assert.Equal(
                RetentionOperationRenewalDisposition.Renewed,
                fixture.Catalog.RenewOperationLease(grant));
            using var publication = grant.EnterLeasePublication();
            Assert.Equal(renewedAt.Add(RetentionV1Constants.LeaseDuration), publication.LeaseExpiresAt);
        }
        finally
        {
            await readLease.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("cleanup_read_denied")]
    public void ReleaseAnalysisSdkDirectoryOperationLease_LifecycleMutationDoesNotChangeTheExactTupleAuthority(string mutation)
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var now = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var exact = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        fixture.ApplyLifecycleMutation(exact.Grant.ItemId, mutation, now);
        var itemBeforeRelease = fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId);

        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal("retention_leases:absent", fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(itemBeforeRelease, fixture.FullRowDump("retention_items", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));

        Assert.Equal(RetentionMutationDisposition.StaleNoOp, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("kind")]
    [InlineData("owner")]
    [InlineData("generation")]
    public void ReleaseAnalysisSdkDirectoryOperationLease_WrongTupleIsAStaleNoOp(string field)
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        fixture.CreateOwnedChild(reservation);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
            reservation,
            reservation.OwnershipMarker,
            exclusivelyCreatedEmptyChild: true);
        var exact = Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        var unrelated = fixture.ActivateUnrelatedAnalysisSdkDirectory();
        var unrelatedBefore = fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId);
        RetentionAnalysisSdkDirectoryOperationLease wrong;
        if (field == "kind")
        {
            fixture.Execute(
                "UPDATE retention_leases SET lease_kind='access' WHERE item_id=$item AND lease_kind='operation';",
                ("$item", exact.Grant.ItemId));
            wrong = exact;
        }
        else
        {
            var wrongGrant = new RetentionReadGrant(
                exact.Grant.OwnershipKey,
                field == "item" ? Guid.NewGuid().ToString("N") : exact.Grant.ItemId,
                exact.Grant.AdmissionRevision,
                exact.Grant.LeaseKind,
                field == "owner" ? Guid.NewGuid().ToString("N") : exact.Grant.LeaseOwner,
                field == "generation" ? exact.Grant.LeaseGeneration + 1 : exact.Grant.LeaseGeneration,
                exact.Grant.LeaseExpiresAt,
                new byte[32]);
            wrong = new RetentionAnalysisSdkDirectoryOperationLease(wrongGrant, exact.CaptureId, exact.Capability);
        }
        var exactBefore = fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId);

        Assert.Equal(RetentionMutationDisposition.StaleNoOp, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(wrong));
        Assert.Equal(exactBefore, fixture.FullRowDump("retention_leases", "item_id", exact.Grant.ItemId));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        if (field == "kind")
        {
            fixture.Execute(
                "UPDATE retention_leases SET lease_kind='operation' WHERE item_id=$item AND lease_kind='access';",
                ("$item", exact.Grant.ItemId));
        }
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(exact));
        Assert.Equal(unrelatedBefore, fixture.FullRowDump("retention_leases", "item_id", unrelated.Grant.ItemId));
        Assert.Equal(RetentionMutationDisposition.Applied, fixture.Catalog.ReleaseAnalysisSdkDirectoryOperationLease(unrelated));
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
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, new byte[] { 1 }, true).IsActive);
        fixture.Time.Advance(TimeSpan.FromDays(89));
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true).IsActive);
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
        fixture.CreateOwnedChild(reserved);
        Assert.True(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reserved, reserved.OwnershipMarker, true).IsActive);
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
        File.WriteAllBytes(Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName), reservation.OwnershipMarker);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "result.txt"), "synthetic");
        var now = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true);
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
        File.WriteAllBytes(Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName), reservation.OwnershipMarker);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "result.txt"), "synthetic");
        var now = new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero);
        var activation = fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true);
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
        fixture.CreateOwnedChild(reservation);
        Assert.False(fixture.Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, true).IsActive);
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
        private Fixture(string path, string parent, RetentionCatalogStore catalog, byte[] runOwnerToken, MutableTimeProvider time) => (Path, Parent, Catalog, RunOwnerToken, Time) = (path, parent, catalog, runOwnerToken, time);
        internal string Path { get; }
        internal string Parent { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal byte[] RunOwnerToken { get; }
        internal MutableTimeProvider Time { get; }

        internal static Fixture Create(Action<string>? checkpoint = null, TimeProvider? timeProvider = null)
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
            timeProvider ??= new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
            var catalog = checkpoint is null ? new RetentionCatalogStore(context, timeProvider) : new RetentionCatalogStore(context, timeProvider, _ => { }, checkpoint);
            return new(path, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sdk-parent-{Guid.NewGuid():N}"), catalog, new byte[32], Assert.IsType<MutableTimeProvider>(timeProvider));
        }

        internal SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            return connection;
        }

        internal T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        internal void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        internal void RegisterCoverage() => Execute(
            "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES " +
            "('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");

        internal void CreateOwnedChild(RetentionAnalysisSdkDirectoryReservation reservation)
        {
            Directory.CreateDirectory(reservation.ChildLocator);
            File.WriteAllBytes(
                System.IO.Path.Combine(reservation.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName),
                reservation.OwnershipMarker);
        }

        internal RetentionAnalysisSdkDirectoryOperationLease ActivateUnrelatedAnalysisSdkDirectory()
        {
            Execute("INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(8,'2026-07-19T01:02:04.0000000+00:00',zeroblob(32));");
            var reservation = Catalog.ReserveAnalysisSdkDirectory(8, System.IO.Path.Combine(Parent, "unrelated-parent"));
            CreateOwnedChild(reservation);
            var activation = Catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(
                reservation,
                reservation.OwnershipMarker,
                exclusivelyCreatedEmptyChild: true);
            return Assert.IsType<RetentionAnalysisSdkDirectoryOperationLease>(activation.Lease);
        }

        internal void ApplyLifecycleMutation(string itemId, string mutation, DateTimeOffset at)
        {
            var atText = at.ToString("O");
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
                        ("$at", atText));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }

        internal string FullRowDump(string table, string keyColumn, object keyValue)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
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

        internal bool GrantUsable(RetentionReadGrant grant, DateTimeOffset at)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var usable = RetentionCatalogStore.IsGrantUsable(connection, transaction, grant, at);
            transaction.Rollback();
            return usable;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
            foreach (var candidate in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}

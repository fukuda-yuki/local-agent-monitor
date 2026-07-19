using System.Threading.Channels;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class RetentionCleanupCoordinator
{
    private readonly RetentionCatalogStore? catalog;
    private readonly RetentionAdapterRegistry? adapters;
    private readonly RetentionSqliteMaintenance maintenance;
    private readonly TimeProvider time;
    private long drainNotAfterTicks;

    internal RetentionCleanupCoordinator() : this(null, null, TimeProvider.System) { }
    internal RetentionCleanupCoordinator(RetentionCatalogStore? catalog, RetentionAdapterRegistry? adapters, TimeProvider? timeProvider = null)
    {
        this.catalog = catalog;
        this.adapters = adapters;
        time = timeProvider ?? TimeProvider.System;
        maintenance = catalog is null ? new RetentionSqliteMaintenance() : new RetentionSqliteMaintenance(catalog);
    }

    internal ValueTask<RetentionCycleResult> RunOneCycleAsync(CancellationToken stopScanningToken, CancellationToken drainToken) => RunCoreAsync(stopScanningToken, drainToken);
    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken) => await RunCoreAsync(cancellationToken, cancellationToken).ConfigureAwait(false);
    internal void BeginDrain(DateTimeOffset notAfter) => Interlocked.Exchange(ref drainNotAfterTicks, notAfter.UtcTicks);

    private async ValueTask<RetentionCycleResult> RunCoreAsync(CancellationToken stopScanningToken, CancellationToken drainToken)
    {
        if (catalog is null || adapters is null) return new(0, 0, false, false, null);
        var batch = await catalog.PrepareCleanupBatchAsync(time.GetUtcNow(), RetentionV1Constants.ExpiryScanItemLimit, RetentionV1Constants.ClaimBatchLimit, RetentionV1Constants.ScanElapsedBudget, stopScanningToken).ConfigureAwait(false);
        if (batch.CoverageBlocked) return new(0, 0, false, false, batch.NextEligibleAt);
        var work = Channel.CreateBounded<RetentionWorkReference>(new BoundedChannelOptions(RetentionV1Constants.ClaimBatchLimit) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        foreach (var candidate in batch.Work)
        {
            if (stopScanningToken.IsCancellationRequested) break;
            await work.Writer.WriteAsync(candidate, stopScanningToken).ConfigureAwait(false);
        }
        work.Writer.TryComplete();

        var completed = 0;
        var clean = true;
        var sqlite = false;
        var coverageBlocked = 0;
        var stateGate = new object();
        async Task ConsumeAsync()
        {
            await foreach (var candidate in work.Reader.ReadAllAsync())
            {
                if (stopScanningToken.IsCancellationRequested || Volatile.Read(ref coverageBlocked) != 0) return;
                var outcome = await ProcessAsync(candidate, stopScanningToken, drainToken).ConfigureAwait(false);
                if (outcome.CoverageBlocked)
                {
                    Interlocked.Exchange(ref coverageBlocked, 1);
                    return;
                }
                lock (stateGate)
                {
                    completed += outcome.Completed;
                    clean &= outcome.Clean;
                    sqlite |= outcome.SqliteCompletion;
                }
            }
        }

        await Task.WhenAll(ConsumeAsync(), ConsumeAsync()).ConfigureAwait(false);
        if (clean)
        {
            await catalog.RecordCleanCycleAsync(sqlite, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            if (await catalog.IsMaintenanceDueAsync(time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
                await maintenance.RunAsync(time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        return new(batch.Work.Count, completed, clean, sqlite, await catalog.GetNextCleanupEligibilityAsync(time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false));
    }

    private async ValueTask<(int Completed, bool Clean, bool SqliteCompletion, bool CoverageBlocked)> ProcessAsync(RetentionWorkReference work, CancellationToken stopScanningToken, CancellationToken drainToken)
    {
        if (stopScanningToken.IsCancellationRequested) return (0, true, false, false);
        var attemptedAt = time.GetUtcNow();
        var claim = await catalog!.TryClaimDeletionAsync(work, Guid.NewGuid().ToString("N"), attemptedAt, stopScanningToken).ConfigureAwait(false);
        if (claim.Disposition == RetentionClaimDisposition.Quiescing && claim.QuiescenceRetryAt is { } retryAt)
        {
            var deadline = attemptedAt + RetentionV1Constants.ActiveOperationQuiescenceBound;
            var waitUntil = retryAt < deadline ? retryAt : deadline;
            if (waitUntil > time.GetUtcNow())
                await Task.Delay(waitUntil - time.GetUtcNow(), time, stopScanningToken).ConfigureAwait(false);
            if (!stopScanningToken.IsCancellationRequested && time.GetUtcNow() < deadline)
                claim = await catalog.TryClaimDeletionAsync(work, Guid.NewGuid().ToString("N"), time.GetUtcNow(), stopScanningToken).ConfigureAwait(false);
        }
        if (claim.Disposition is RetentionClaimDisposition.Contended or RetentionClaimDisposition.StaleNoOp) return (0, true, false, false);
        if (claim.Disposition == RetentionClaimDisposition.CoverageBlocked) return (0, false, false, true);
        if (claim.Disposition != RetentionClaimDisposition.Claimed || claim.Claim is null) return (0, false, false, false);
        if (drainToken.IsCancellationRequested)
        {
            await catalog.TryCancelBeforeIntentAsync(claim.Claim.Fence, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            return (0, false, false, false);
        }

        var intent = await catalog.EnsureDeleteIntentAsync(claim.Claim.Fence, claim.Claim.IntentCursor, time.GetUtcNow(), drainToken).ConfigureAwait(false);
        if (intent.Disposition is not RetentionIntentDisposition.Committed and not RetentionIntentDisposition.AlreadyCommitted) return (0, false, false, false);

        using var leaseLoss = new CancellationTokenSource();
        using var adapterToken = CancellationTokenSource.CreateLinkedTokenSource(drainToken, leaseLoss.Token);
        var renewal = RenewUntilDoneAsync(claim.Claim.Fence, leaseLoss, adapterToken.Token);
        RetentionAdapterResult result;
        var leaseWasLost = false;
        try
        {
            result = await adapters!.Get(claim.Claim.StoreKind).DeleteAsync(new RetentionDeleteContext(
                claim.Claim.Fence.ItemId, claim.Claim.StoreInstanceId, claim.Claim.StoreKind, claim.Claim.Fence.ExpectedRevision,
                claim.Claim.Fence.LeaseOwner, claim.Claim.Fence.LeaseGeneration, claim.Claim.SourceIdentity,
                claim.Claim.PrivateLocator, intent.IntentCursor, adapterToken.Token)).ConfigureAwait(false);
            leaseWasLost = leaseLoss.IsCancellationRequested || drainToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return (0, false, false, false); }
        catch { result = RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        finally { leaseLoss.Cancel(); try { await renewal.ConfigureAwait(false); } catch (OperationCanceledException) { } }

        if (result.Disposition == RetentionAdapterDisposition.Deleted)
        {
            var completed = await catalog.TryCompleteDeletionAsync(claim.Claim.Fence, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            var final = completed is RetentionMutationDisposition.Applied or RetentionMutationDisposition.NoOpAlreadyFinalized;
            return (final ? 1 : 0, final, final && IsSqlite(claim.Claim.StoreKind), false);
        }
        if (leaseWasLost)
            return (0, false, false, false);
        if (result.Disposition == RetentionAdapterDisposition.TransientFailure)
            await catalog.TryRecordTransientFailureAsync(claim.Claim.Fence, result.ErrorCode!.Value, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        else if (result.Disposition == RetentionAdapterDisposition.TerminalFailure)
            await catalog.TryRecordTerminalFailureAsync(claim.Claim.Fence, result.ErrorCode!.Value, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return (0, false, false, false);
    }

    private DateTimeOffset? DrainNotAfter
    {
        get
        {
            var ticks = Interlocked.Read(ref drainNotAfterTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private async Task RenewUntilDoneAsync(RetentionDeleteFence fence, CancellationTokenSource leaseLoss, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetentionV1Constants.LeaseRenewalDeadline, time, cancellationToken).ConfigureAwait(false);
                var notAfter = DrainNotAfter;
                if (notAfter is { } deadline && time.GetUtcNow() >= deadline) { leaseLoss.Cancel(); return; }
                var result = await catalog!.TryRenewDeletionLeaseAsync(fence, time.GetUtcNow(), notAfter, CancellationToken.None).ConfigureAwait(false);
                if (result != RetentionRenewalResult.Renewed) { leaseLoss.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsSqlite(RetentionStoreKind kind) => kind is RetentionStoreKind.SessionEventContent or RetentionStoreKind.RawRecord or RetentionStoreKind.AnalysisRunRaw;
}

internal sealed record RetentionCycleResult(int Dispatched, int Completed, bool Clean, bool QualifiedSqliteBatch, DateTimeOffset? NextEligibleAt);

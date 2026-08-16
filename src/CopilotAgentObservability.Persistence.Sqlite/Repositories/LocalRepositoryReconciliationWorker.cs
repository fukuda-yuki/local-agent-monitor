using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryReconciliationWorkOutcome { NoWork, ProcessorInvoked, InputUnavailable, DigestMismatch, Corrupt, Retrying, Busy, StaleOwner }

internal sealed class LocalRepositoryReconciliationWorker
{
    private readonly SqliteLocalRepositoryReconciliationStore queue;
    private readonly LocalRepositoryRawAvailabilityReader rawAvailability;
    private readonly ILocalRepositoryRawRecordProcessor processor;
    private readonly TimeProvider timeProvider;
    private readonly ILocalRepositoryReconciliationCheckpoint? checkpoint;
    private readonly Action<Func<RawTelemetryRecord>>? lastRawAccessObserverForTesting;

    internal LocalRepositoryReconciliationWorker(
        SqliteLocalRepositoryReconciliationStore queue,
        LocalRepositoryRawAvailabilityReader rawAvailability,
        ILocalRepositoryRawRecordProcessor processor,
        TimeProvider? timeProvider = null,
        ILocalRepositoryReconciliationCheckpoint? checkpoint = null,
        Action<Func<RawTelemetryRecord>>? lastRawAccessObserverForTesting = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rawAvailability = rawAvailability ?? throw new ArgumentNullException(nameof(rawAvailability));
        if (!this.queue.IsBoundTo(this.rawAvailability)) throw new InvalidOperationException("local_repository_store_binding_mismatch");
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
        this.lastRawAccessObserverForTesting = lastRawAccessObserverForTesting;
    }

    internal async ValueTask<LocalRepositoryReconciliationWorkOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        if (queue.RecoverExpiredLeases(now) == LocalRepositoryQueueTransitionResult.Busy)
            return LocalRepositoryReconciliationWorkOutcome.Busy;
        var claim = queue.TryClaimNext(now);
        if (claim.Status == LocalRepositoryQueueTransitionResult.Busy) return LocalRepositoryReconciliationWorkOutcome.Busy;
        var lease = claim.Lease;
        if (lease is null) return LocalRepositoryReconciliationWorkOutcome.NoWork;
        LocalRepositoryRawAvailabilityResult raw;
        checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeRawAvailabilityRead);
        try
        {
            raw = await rawAvailability.ReadAsync(lease.RawRecordId, lease.RawPayloadSha256, RetentionReadKind.Operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterRawAvailabilityRead);
        }
        await using (raw)
        {
            if (raw.Status == LocalRepositoryRawAvailabilityStatus.Busy)
                return MapTransition(queue.ReturnPending(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Retrying);
            if (raw.Status == LocalRepositoryRawAvailabilityStatus.PayloadDigestMismatch)
                return MapTransition(queue.RecordPayloadDigestMismatch(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.DigestMismatch);
            if (raw.Status == LocalRepositoryRawAvailabilityStatus.Corrupt)
                return MapTransition(queue.RecordCatalogSchemaViolation(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Corrupt);
            if (raw.Availability != LocalRepositoryRawAvailability.Available || raw.Lease is null)
                return MapTransition(queue.RecordInputUnavailable(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.InputUnavailable);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var handoffRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var heartbeat = HeartbeatAsync(
                lease,
                raw.Lease,
                attemptCancellation,
                heartbeatStop.Token,
                handoffRequested.Task);
            ILocalRepositoryPreparedRawRecord? prepared = null;
            var ownedLease = lease;
            var heartbeatDrained = false;
            try
            {
                using var rawReference = raw.Lease.AcquireValueReference();
                lastRawAccessObserverForTesting?.Invoke(() => rawReference.Value);
                prepared = await processor.PrepareAsync(
                    lease,
                    rawReference.Value,
                    raw.Lease,
                    attemptCancellation.Token).ConfigureAwait(false);
                attemptCancellation.Token.ThrowIfCancellationRequested();

                checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeHandoffHeartbeat);
                attemptCancellation.Token.ThrowIfCancellationRequested();
                handoffRequested.TrySetResult();
                var heartbeatResult = await DrainHeartbeatAsync(heartbeat, ownedLease).ConfigureAwait(false);
                heartbeatDrained = true;
                ownedLease = heartbeatResult.Lease;
                cancellationToken.ThrowIfCancellationRequested();
                if (!heartbeatResult.AuthorityHeld || !heartbeatResult.HandoffApplied)
                {
                    return MapTransition(
                        queue.ReturnPending(ownedLease, timeProvider.GetUtcNow()),
                        LocalRepositoryReconciliationWorkOutcome.Retrying);
                }
                attemptCancellation.Token.ThrowIfCancellationRequested();
                await prepared.FinalizeAsync(ownedLease, attemptCancellation.Token).ConfigureAwait(false);
                return LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await heartbeatStop.CancelAsync().ConfigureAwait(false);
                if (!heartbeatDrained)
                {
                    ownedLease = (await DrainHeartbeatAsync(heartbeat, ownedLease).ConfigureAwait(false)).Lease;
                    heartbeatDrained = true;
                }
                return MapTransition(queue.ReturnPending(ownedLease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Retrying);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await heartbeatStop.CancelAsync().ConfigureAwait(false);
                if (!heartbeatDrained)
                {
                    ownedLease = (await DrainHeartbeatAsync(heartbeat, ownedLease).ConfigureAwait(false)).Lease;
                    heartbeatDrained = true;
                }
                return MapTransition(queue.ReturnPending(ownedLease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Retrying);
            }
            finally
            {
                await heartbeatStop.CancelAsync().ConfigureAwait(false);
                if (!heartbeatDrained)
                    await DrainHeartbeatAsync(heartbeat, ownedLease).ConfigureAwait(false);
                if (prepared is not null)
                    await prepared.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<HeartbeatResult> HeartbeatAsync(
        LocalRepositoryQueueLease initialLease,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationTokenSource attemptCancellation,
        CancellationToken heartbeatStop,
        Task handoffRequested)
    {
        var lease = initialLease;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
            while (true)
            {
                var tick = timer.WaitForNextTickAsync(heartbeatStop).AsTask();
                var ready = await Task.WhenAny(handoffRequested, tick).ConfigureAwait(false);
                if (ready == handoffRequested || handoffRequested.IsCompleted)
                {
                    heartbeatStop.ThrowIfCancellationRequested();
                    var handoff = queue.Heartbeat(lease, retentionLease);
                    if (handoff.Status != LocalRepositoryQueueTransitionResult.Applied || handoff.Lease is null)
                    {
                        checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterHandoffRejected);
                        return new(false, lease, false);
                    }
                    lease = handoff.Lease;
                    checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterHandoffHeartbeat);
                    return new(true, lease, true);
                }
                if (!await tick.ConfigureAwait(false))
                    break;
                var at = timeProvider.GetUtcNow().ToUniversalTime();
                if (at >= lease.LeaseExpiresAt)
                {
                    checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.HeartbeatLeaseExpired);
                    attemptCancellation.Cancel();
                    return new(false, lease, false);
                }
                var renewed = queue.Heartbeat(lease, retentionLease);
                if (renewed.Status == LocalRepositoryQueueTransitionResult.Busy)
                {
                    checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterHeartbeatBusy);
                    continue;
                }
                if (renewed.Status != LocalRepositoryQueueTransitionResult.Applied || renewed.Lease is null)
                {
                    checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatRejected);
                    attemptCancellation.Cancel();
                    return new(false, lease, false);
                }
                lease = renewed.Lease;
                checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatApplied);
            }
        }
        catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        catch
        {
            attemptCancellation.Cancel();
            return new(false, lease, false);
        }
        return new(true, lease, false);
    }

    private sealed record HeartbeatResult(bool AuthorityHeld, LocalRepositoryQueueLease Lease, bool HandoffApplied);

    private static async Task<HeartbeatResult> DrainHeartbeatAsync(
        Task<HeartbeatResult> heartbeat,
        LocalRepositoryQueueLease fallbackLease)
    {
        try
        {
            return await heartbeat.ConfigureAwait(false);
        }
        catch
        {
            return new(false, fallbackLease, false);
        }
    }

    private static LocalRepositoryReconciliationWorkOutcome MapTransition(
        LocalRepositoryQueueTransitionResult result,
        LocalRepositoryReconciliationWorkOutcome applied) => result switch
        {
            LocalRepositoryQueueTransitionResult.Applied => applied,
            LocalRepositoryQueueTransitionResult.Busy => LocalRepositoryReconciliationWorkOutcome.Busy,
            _ => LocalRepositoryReconciliationWorkOutcome.StaleOwner,
        };
}

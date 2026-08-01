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

    internal LocalRepositoryReconciliationWorker(
        SqliteLocalRepositoryReconciliationStore queue,
        LocalRepositoryRawAvailabilityReader rawAvailability,
        ILocalRepositoryRawRecordProcessor processor,
        TimeProvider? timeProvider = null,
        ILocalRepositoryReconciliationCheckpoint? checkpoint = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rawAvailability = rawAvailability ?? throw new ArgumentNullException(nameof(rawAvailability));
        if (!this.queue.IsBoundTo(this.rawAvailability)) throw new InvalidOperationException("local_repository_store_binding_mismatch");
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
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
            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = HeartbeatAsync(lease, raw.Lease, heartbeatCancellation);
            try
            {
                await processor.ProcessAsync(lease, raw.Lease.Value, raw.Lease, heartbeatCancellation.Token).ConfigureAwait(false);
                await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
                return await heartbeat.ConfigureAwait(false)
                    ? LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked
                    : LocalRepositoryReconciliationWorkOutcome.StaleOwner;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return MapTransition(queue.ReturnPending(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Retrying);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return MapTransition(queue.ReturnPending(lease, timeProvider.GetUtcNow()), LocalRepositoryReconciliationWorkOutcome.Retrying);
            }
            finally
            {
                await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> HeartbeatAsync(
        LocalRepositoryQueueLease initialLease,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationTokenSource cancellation)
    {
        var lease = initialLease;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                var renewed = queue.Heartbeat(lease, retentionLease, timeProvider.GetUtcNow());
                if (renewed.Status != LocalRepositoryQueueTransitionResult.Applied || renewed.Lease is null)
                {
                    cancellation.Cancel();
                    return false;
                }
                lease = renewed.Lease;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        return true;
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

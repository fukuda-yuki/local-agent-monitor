using System.Diagnostics;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SkillProjectionWorker
{
    private readonly SqliteSkillProjectionStore store;
    private readonly Action<SkillProjectionQueueLease>? beforePublish;
    private readonly TimeProvider? timeProvider;
    private readonly Func<
        SkillProjectionQueueLease,
        CancellationToken,
        ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>>> readFrontier;

    internal SkillProjectionWorker(
        SqliteSkillProjectionStore store,
        Action<SkillProjectionQueueLease>? beforePublish = null,
        TimeProvider? timeProvider = null,
        Func<
            SkillProjectionQueueLease,
            CancellationToken,
            ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>>>? readFrontier = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.beforePublish = beforePublish;
        this.timeProvider = timeProvider;
        this.readFrontier = readFrontier ?? store.ReadFrontierAsync;
    }

    internal async Task<SkillProjectionWorkOutcome> RunNextAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var queueLease = store.ClaimNext(now);
        if (queueLease is null)
            return SkillProjectionWorkOutcome.NoWork;
        var elapsed = Stopwatch.StartNew();
        var read = await readFrontier(queueLease, cancellationToken).ConfigureAwait(false);
        if (read.Disposition == RetentionReadDisposition.Busy)
            return store.RecordRetry(
                queueLease,
                CurrentTime(now, elapsed),
                "retention_busy");
        if (read.Lease is null)
            return store.RecordInputUnavailable(
                queueLease,
                CurrentTime(now, elapsed));

        await using var retentionLease = read.Lease;
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = MaintainLeasesAsync(
            queueLease,
            retentionLease,
            now,
            elapsed,
            heartbeatCancellation.Token);
        var records = retentionLease.Value;
        string? terminalErrorCode = null;
        var leasesHeld = false;
        var projected = new List<SkillProjectionProjectedInput>(records.Count);
        try
        {
            if (records.Count != queueLease.Inputs.Count)
                terminalErrorCode = "skill_projection_frontier_mismatch";
            else
            {
                for (var index = 0; index < records.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var input = queueLease.Inputs[index];
                    var record = records[index];
                    if (input.EvidenceKind != SkillProjectionInputEvidenceKind.PayloadSha256
                        || record.Id != input.RawRecordId
                        || !string.Equals(
                            SkillProjectionHashing.InputDigest(record.PayloadJson),
                            input.RawPayloadSha256,
                            StringComparison.Ordinal))
                    {
                        terminalErrorCode = "skill_projection_input_digest_mismatch";
                        break;
                    }
                    var projection = MonitorSkillProjectionBuilder.Build(
                        record,
                        input.SourceSurface,
                        traceId => string.Equals(traceId, queueLease.TraceId, StringComparison.Ordinal)
                            ? (TraceSourceVersionResolutionState.Resolved, queueLease.ExactVersion)
                            : null);
                    projected.Add(new(input.RawRecordId, record, projection));
                }
            }
            if (terminalErrorCode is null)
                beforePublish?.Invoke(queueLease);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            leasesHeld = await heartbeat.ConfigureAwait(false);
        }
        var completedAt = CurrentTime(now, elapsed);
        if (!leasesHeld)
            return store.RecordRetry(queueLease, completedAt, "retention_lease_lost");
        if (terminalErrorCode is not null)
            return store.RecordTerminal(queueLease, completedAt, terminalErrorCode);
        return store.Publish(queueLease, projected, retentionLease, completedAt);
    }

    private async Task<bool> MaintainLeasesAsync(
        SkillProjectionQueueLease queueLease,
        RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease,
        DateTimeOffset startedAt,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var renewed = store.Heartbeat(
                    queueLease,
                    retentionLease,
                    CurrentTime(startedAt, elapsed));
                if (renewed is null)
                    return false;
                queueLease = renewed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return true;
    }

    private DateTimeOffset CurrentTime(
        DateTimeOffset startedAt,
        Stopwatch elapsed) =>
        timeProvider?.GetUtcNow() ?? startedAt + elapsed.Elapsed;
}

using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalRepositoryRawRecordProcessor
{
    async ValueTask<ILocalRepositoryPreparedRawRecord> PrepareAsync(
        LocalRepositoryQueueLease queueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new LegacyPreparedRawRecord(this, queueLease, rawRecord, retentionLease);
    }

    ValueTask ProcessAsync(
        LocalRepositoryQueueLease queueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationToken cancellationToken);

    private sealed class LegacyPreparedRawRecord(
        ILocalRepositoryRawRecordProcessor processor,
        LocalRepositoryQueueLease initialQueueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease) : ILocalRepositoryPreparedRawRecord
    {
        public ValueTask FinalizeAsync(LocalRepositoryQueueLease queueLease, CancellationToken cancellationToken)
        {
            if (queueLease.QueueId != initialQueueLease.QueueId
                || queueLease.RawRecordId != initialQueueLease.RawRecordId
                || queueLease.LeaseToken != initialQueueLease.LeaseToken)
            {
                throw new InvalidOperationException("local_repository_prepared_queue_mismatch");
            }
            return processor.ProcessAsync(queueLease, rawRecord, retentionLease, cancellationToken);
        }
    }
}

internal interface ILocalRepositoryPreparedRawRecord : IAsyncDisposable
{
    ValueTask FinalizeAsync(LocalRepositoryQueueLease queueLease, CancellationToken cancellationToken);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

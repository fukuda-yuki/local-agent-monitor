using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalRepositoryRawRecordProcessor
{
    ValueTask ProcessAsync(
        LocalRepositoryQueueLease queueLease,
        RawTelemetryRecord rawRecord,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        CancellationToken cancellationToken);
}

using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class RawTelemetryStoreProjectionTestExtensions
{
    internal static bool ApplyProjection(
        this RawTelemetryStore store,
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt,
        int? expectedDispositionRevision = null)
    {
        var fencedStore = CreateFencedStore(store);
        var read = fencedStore.ReadRawRecordsAsync([rawRecordId], RetentionReadKind.Operation, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        try
        {
            return expectedDispositionRevision is { } revision
                ? fencedStore.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt, revision, lease)
                : fencedStore.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt, lease);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static bool ApplySpanProjection(
        this RawTelemetryStore store,
        long rawRecordId,
        IReadOnlyList<MonitorSpanProjection> spans,
        DateTimeOffset projectedAt)
    {
        var fencedStore = CreateFencedStore(store);
        var read = fencedStore.ReadRawRecordsAsync([rawRecordId], RetentionReadKind.Operation, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        try
        {
            return fencedStore.ApplySpanProjection(rawRecordId, spans, projectedAt, lease);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static RawTelemetryStore CreateFencedStore(RawTelemetryStore store) =>
        new(
            store.DatabasePath,
            RetentionCatalogContext.AdoptExistingCatalogV1(store.DatabasePath),
            store.Clock,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
}

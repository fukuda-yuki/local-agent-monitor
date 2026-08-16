using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Fail-closed default implementation for projection-store test seams. Tests that
/// exercise raw content must opt in with a granted lease rather than receiving a
/// payload from an unmodelled synchronous fallback.
/// </summary>
internal abstract class ProjectionStoreTestDouble : IMonitorProjectionStore
{
    protected static RetentionBatchReadResult<T> Granted<T>(T records)
    {
        var now = TimeProvider.System.GetUtcNow();
        var ownershipKey = new RetentionOwnershipKey("test-store", RetentionStoreKind.RawRecord, Guid.NewGuid().ToString("N"));
        var grant = new RetentionReadGrant(
            ownershipKey,
            Guid.NewGuid().ToString("N"),
            1,
            RetentionLeaseKind.Access,
            Guid.NewGuid().ToString("N"),
            1,
            now.AddMinutes(2),
            new byte[32]);
        var handle = new RetentionCommittedReadHandle(
            [grant],
            TimeProvider.System,
            _ => true,
            terminalAuthority: static (committed, operation) =>
            {
                if (!committed.TryMoveTerminalAttemptToPending(operation)) return RetentionRawTerminalResult.Lost;
                return committed.PublishTerminal(operation);
            });
        Assert.True(handle.Activate());
        Assert.True(handle.Publish());
        return RetentionBatchReadResult<T>.FromHandle(new RetentionBatchReadLease<T>(
            records,
            RetentionRevisionFence.Create(),
            [grant],
            handle,
            CancellationToken.None));
    }

    protected static RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> NotFoundBatch() =>
        RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.FromDisposition(RetentionReadDisposition.LifecycleDenied);

    protected static RetentionReadResult<RawTelemetryRecord> NotFound() =>
        RetentionReadResult<RawTelemetryRecord>.FromDisposition(RetentionReadDisposition.LifecycleDenied);

    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult(NotFoundBatch());

    public virtual bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt, RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease) => false;
    public virtual ProjectionDisposition? GetProjectionDisposition(long rawRecordId) => null;
    public virtual bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) => false;
    public virtual bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) => false;
    public virtual bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt, int expectedDispositionRevision, RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease) => false;
    public virtual MonitorProjectionStatus GetProjectionStatus() => new(0, null);
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken) => ValueTask.FromResult(NotFoundBatch());
    public virtual bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt, RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease) => false;
    public virtual MonitorProjectionStatus GetSpanProjectionStatus() => new(0, null);
    public virtual MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) => new([], false);
    public virtual MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) => new([], false);
    public virtual MonitorTraceRow? GetMonitorTrace(string traceId) => null;
    public virtual MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) => new([], false);
    public virtual IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => [];
    public virtual ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(long id, RetentionReadKind readKind, CancellationToken cancellationToken) => ValueTask.FromResult(NotFound());
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(IReadOnlyList<long> ids, RetentionReadKind readKind, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ids.Count == 0
            ? RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.Empty([])
            : NotFoundBatch());
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(string traceId, int limit, RetentionReadKind readKind, CancellationToken cancellationToken) => ValueTask.FromResult(NotFoundBatch());
    public virtual IReadOnlyList<long> ListRecentRawRecordIdsForRepositoryMetadataDiagnostics(int limit, int maxPayloadBytes, int maxTotalPayloadBytes) => [];
    public virtual MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) => new(0, 0, 0, 0, 0, 0, 0, 0);
    public virtual IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) => [];
    public virtual IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) => [];
    public virtual IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) => [];
    public virtual IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) => [];
    public virtual MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) => new([], 0, 0);
    public virtual MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) => null;
    public virtual IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) => [];
}

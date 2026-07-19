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
    protected static RetentionBatchReadResult<T> Granted<T>(T records) =>
        new(RetentionReadDisposition.Granted, new RetentionBatchReadLease<T>(records, RetentionRevisionFence.Create(), () => ValueTask.CompletedTask));

    protected static RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> NotFoundBatch() =>
        new(RetentionReadDisposition.NotFound, null);

    protected static RetentionReadResult<RawTelemetryRecord> NotFound() =>
        new(RetentionReadDisposition.NotFound, null);

    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult(NotFoundBatch());

    public virtual bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt) => false;
    public virtual ProjectionDisposition? GetProjectionDisposition(long rawRecordId) => null;
    public virtual bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) => false;
    public virtual bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) => false;
    public virtual bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt, int expectedDispositionRevision) => false;
    public virtual MonitorProjectionStatus GetProjectionStatus() => new(0, null);
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken) => ValueTask.FromResult(NotFoundBatch());
    public virtual bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) => false;
    public virtual MonitorProjectionStatus GetSpanProjectionStatus() => new(0, null);
    public virtual MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) => new([], false);
    public virtual MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) => new([], false);
    public virtual MonitorTraceRow? GetMonitorTrace(string traceId) => null;
    public virtual MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) => new([], false);
    public virtual IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => [];
    public virtual ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(long id, RetentionReadKind readKind, CancellationToken cancellationToken) => ValueTask.FromResult(NotFound());
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(IReadOnlyList<long> ids, RetentionReadKind readKind, CancellationToken cancellationToken) => ValueTask.FromResult(NotFoundBatch());
    public virtual ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(string traceId, int limit, RetentionReadKind readKind, CancellationToken cancellationToken) => ValueTask.FromResult(NotFoundBatch());
    public virtual MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) => new(0, 0, 0, 0, 0, 0, 0, 0);
    public virtual IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) => [];
    public virtual IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) => [];
    public virtual IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) => [];
    public virtual IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) => [];
    public virtual MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) => new([], 0, 0);
    public virtual MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) => null;
    public virtual IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) => [];
}

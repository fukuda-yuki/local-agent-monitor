using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Projection;

/// <summary>
/// Persistence seam for the projection worker and the read APIs. Concrete SQLite
/// busy errors (5 / 6) are translated to <see cref="PersistenceBusyException"/> so
/// the worker can retry and the HTTP layer can map to <c>503</c> without leaking
/// raw exception text. Each call opens its own connection, so the worker and HTTP
/// request threads can use one instance concurrently.
/// </summary>
internal interface IMonitorProjectionStore
{
    ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken);

    bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt);

    ProjectionDisposition? GetProjectionDisposition(long rawRecordId);

    bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt);

    bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt);

    bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt,
        int expectedDispositionRevision);

    MonitorProjectionStatus GetProjectionStatus();

    ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken);

    bool ApplySpanProjection(
        long rawRecordId,
        IReadOnlyList<MonitorSpanProjection> spans,
        DateTimeOffset projectedAt);

    MonitorProjectionStatus GetSpanProjectionStatus();

    MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit);

    MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit);

    MonitorTraceRow? GetMonitorTrace(string traceId);

    MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit);

    IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId);

    ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(long id, RetentionReadKind readKind, CancellationToken cancellationToken);

    ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(IReadOnlyList<long> ids, RetentionReadKind readKind, CancellationToken cancellationToken);

    ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(string traceId, int limit, RetentionReadKind readKind, CancellationToken cancellationToken);

    MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive);

    IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive);

    IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive);

    IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit);

    IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit);

    MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query);

    MonitorSpanRow? GetMonitorSpan(string traceId, string spanId);

    IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId);
}

internal sealed class RawTelemetryStoreProjectionStore : IMonitorProjectionStore
{
    private readonly RawTelemetryStore store;

    public RawTelemetryStoreProjectionStore(RawTelemetryStore store)
    {
        this.store = store;
    }

    public ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken) =>
        GuardAsync(() => store.ListUnprocessedForProjectionAsync(limit, RetentionReadKind.Operation, cancellationToken));

    public bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt) =>
        Guard(() => store.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt));

    public ProjectionDisposition? GetProjectionDisposition(long rawRecordId) =>
        Guard(() => store.GetProjectionDisposition(rawRecordId));

    public bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
        Guard(() => store.TryBeginProjection(rawRecordId, expectedRevision, updatedAt));

    public bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt) =>
        Guard(() => store.RecordProjectionFailure(rawRecordId, expectedRevision, updatedAt));

    public bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt,
        int expectedDispositionRevision) =>
        Guard(() => store.ApplyProjection(
            rawRecordId,
            source,
            receivedAt,
            projection,
            projectedAt,
            expectedDispositionRevision));

    public MonitorProjectionStatus GetProjectionStatus() =>
        Guard(store.GetProjectionStatus);

    public ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken) =>
        GuardAsync(() => store.ListUnprocessedForSpanProjectionAsync(limit, RetentionReadKind.Operation, cancellationToken));

    public bool ApplySpanProjection(
        long rawRecordId,
        IReadOnlyList<MonitorSpanProjection> spans,
        DateTimeOffset projectedAt) =>
        Guard(() => store.ApplySpanProjection(rawRecordId, spans, projectedAt));

    public MonitorProjectionStatus GetSpanProjectionStatus() =>
        Guard(store.GetSpanProjectionStatus);

    public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
        Guard(() => store.ListMonitorIngestions(afterRawRecordId, limit));

    public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
        Guard(() => store.ListMonitorTraces(afterId, limit));

    public MonitorTraceRow? GetMonitorTrace(string traceId) =>
        Guard(() => store.GetMonitorTrace(traceId));

    public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) =>
        Guard(() => store.ListMonitorSpans(traceId, afterId, limit));

    public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) =>
        Guard(() => store.GetSpansForTrace(traceId));

    public ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(long id, RetentionReadKind readKind, CancellationToken cancellationToken) =>
        GuardAsync(() => store.GetRawRecordByIdAsync(id, readKind, cancellationToken));

    public ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(IReadOnlyList<long> ids, RetentionReadKind readKind, CancellationToken cancellationToken) =>
        GuardAsync(() => store.ReadRawRecordsAsync(ids, readKind, cancellationToken));

    public ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(string traceId, int limit, RetentionReadKind readKind, CancellationToken cancellationToken) =>
        GuardAsync(() => store.ListRawRecordsByTraceIdAsync(traceId, limit, readKind, cancellationToken));

    public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) =>
        Guard(() => store.GetPeriodSummary(startInclusive, endExclusive));

    public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) =>
        Guard(() => store.GetPerModelPeriodSummary(startInclusive, endExclusive));

    public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) =>
        Guard(() => store.GetHourlyTokenDistribution(startInclusive, endExclusive));

    public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) =>
        Guard(() => store.ListTopTokenTraces(startInclusive, endExclusive, limit));

    public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) =>
        Guard(() => store.ListRecentMonitorTraces(limit));

    public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) =>
        Guard(() => store.ListMonitorTracesFiltered(query));

    public MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) =>
        Guard(() => store.GetMonitorSpan(traceId, spanId));

    public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) =>
        Guard(() => store.ListConversationTraces(conversationId));

    private static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new PersistenceBusyException();
        }
    }

    private static async ValueTask<T> GuardAsync<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new PersistenceBusyException();
        }
    }
}

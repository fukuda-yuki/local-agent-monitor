using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite;
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
    IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit);

    bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt);

    MonitorProjectionStatus GetProjectionStatus();

    IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit);

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

    RawTelemetryRecord? GetRawRecordById(long id);

    IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit);
}

internal sealed class RawTelemetryStoreProjectionStore : IMonitorProjectionStore
{
    private readonly RawTelemetryStore store;

    public RawTelemetryStoreProjectionStore(RawTelemetryStore store)
    {
        this.store = store;
    }

    public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) =>
        Guard(() => store.ListUnprocessedForProjection(limit));

    public bool ApplyProjection(
        long rawRecordId,
        string source,
        DateTimeOffset receivedAt,
        MonitorRecordProjection projection,
        DateTimeOffset projectedAt) =>
        Guard(() => store.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt));

    public MonitorProjectionStatus GetProjectionStatus() =>
        Guard(store.GetProjectionStatus);

    public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) =>
        Guard(() => store.ListUnprocessedForSpanProjection(limit));

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

    public RawTelemetryRecord? GetRawRecordById(long id) =>
        Guard(() => store.GetRawRecordById(id));

    public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit) =>
        Guard(() => store.ListRawRecordsByTraceId(traceId, limit));

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
}

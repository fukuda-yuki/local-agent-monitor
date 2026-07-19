using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class RawTelemetryStoreTestReads
{
    public static IReadOnlyList<RawTelemetryRecord> ListRecords(this RawTelemetryStore store)
    {
        var ids = RawRecordIds(store.DatabasePath);
        return ids.Count == 0 ? [] : ReadBatch(store, ids, RetentionReadKind.Access);
    }

    public static IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(this RawTelemetryStore store, int limit) =>
        ReadLease(store.ListUnprocessedForProjectionAsync(limit, RetentionReadKind.Operation, CancellationToken.None));

    public static IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(this RawTelemetryStore store, int limit) =>
        ReadLease(store.ListUnprocessedForSpanProjectionAsync(limit, RetentionReadKind.Operation, CancellationToken.None));

    public static RawTelemetryRecord? GetRawRecordById(this RawTelemetryStore store, long id)
    {
        var result = store.GetRawRecordByIdAsync(id, RetentionReadKind.Access, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try { return result.Lease?.Value; }
        finally { result.Lease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(this RawTelemetryStore store, string traceId, int limit) =>
        ReadLease(store.ListRawRecordsByTraceIdAsync(traceId, limit, RetentionReadKind.Access, CancellationToken.None));

    private static IReadOnlyList<RawTelemetryRecord> ReadBatch(RawTelemetryStore store, IReadOnlyList<long> ids, RetentionReadKind kind) =>
        ReadLease(store.ReadRawRecordsAsync(ids, kind, CancellationToken.None));

    private static IReadOnlyList<RawTelemetryRecord> ReadLease(ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> pending)
    {
        var result = pending.AsTask().GetAwaiter().GetResult();
        try { return result.Lease?.Value.ToArray() ?? []; }
        finally { result.Lease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static IReadOnlyList<long> RawRecordIds(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM raw_records ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }
}

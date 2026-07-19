using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli.Tests;

internal static class RawTelemetryStoreTestReads
{
    public static IReadOnlyList<RawTelemetryRecord> ListRecords(this RawTelemetryStore store)
    {
        var context = RetentionCatalogContext.AdoptExistingCatalogV1(store.DatabasePath);
        var leasedStore = new RawTelemetryStore(store.DatabasePath, context);
        var result = leasedStore.ListRecordsAsync(RetentionReadKind.Access, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        try { return result.Lease?.Value.ToArray() ?? []; }
        finally { result.Lease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}

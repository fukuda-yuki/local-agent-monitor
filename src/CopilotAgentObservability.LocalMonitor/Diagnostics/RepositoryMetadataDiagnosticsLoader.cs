using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.Diagnostics;

internal sealed record RepositoryMetadataStatusSummary(
    string Status,
    int RecordCount,
    bool RepositoryLabelPresent,
    bool UrlFallbackUsed);

internal sealed record RepositoryMetadataInventorySummary(
    string Key,
    int Count,
    string Scope,
    string Classification);

internal sealed record RepositoryMetadataDiagnosticsSnapshot(
    int AnalyzedRecordCount,
    bool Unavailable,
    IReadOnlyList<RepositoryMetadataStatusSummary> StatusRows,
    IReadOnlyList<RepositoryMetadataInventorySummary> InventoryRows)
{
    internal static RepositoryMetadataDiagnosticsSnapshot Empty(bool unavailable = false) =>
        new(0, unavailable, [], []);
}

internal sealed class RepositoryMetadataDiagnosticsLoader(IMonitorProjectionStore store)
{
    private const int CandidateLimit = 50;
    private const int MaximumPayloadBytes = 1_048_576;
    private const int MaximumTotalPayloadBytes = 4_194_304;

    internal async ValueTask<RepositoryMetadataDiagnosticsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<long> candidateIds;
        try
        {
            candidateIds = store.ListRecentRawRecordIdsForRepositoryMetadataDiagnostics(
                CandidateLimit,
                MaximumPayloadBytes,
                MaximumTotalPayloadBytes);
        }
        catch (PersistenceBusyException)
        {
            return RepositoryMetadataDiagnosticsSnapshot.Empty(unavailable: true);
        }

        if (candidateIds.Count == 0)
        {
            return RepositoryMetadataDiagnosticsSnapshot.Empty();
        }

        RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> read;
        try
        {
            read = await store.ReadRawRecordsAsync(
                candidateIds,
                RetentionReadKind.Access,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceBusyException)
        {
            return RepositoryMetadataDiagnosticsSnapshot.Empty(unavailable: true);
        }

        if (read.Disposition != RetentionReadDisposition.Granted || read.Lease is null)
        {
            return RepositoryMetadataDiagnosticsSnapshot.Empty(unavailable: true);
        }

        await using var lease = read.Lease;
        var diagnostics = new List<RepositoryMetadataDiagnostic>(lease.Value.Count);
        var unavailable = false;
        foreach (var record in lease.Value)
        {
            try
            {
                diagnostics.Add(RepositoryMetadataDiagnostics.Build(record.PayloadJson));
            }
            catch (JsonException)
            {
                unavailable = true;
            }
        }

        var statusRows = diagnostics
            .GroupBy(diagnostic => new
            {
                diagnostic.Status,
                diagnostic.RepositoryLabelPresent,
                diagnostic.UrlFallbackUsed,
            })
            .OrderBy(group => group.Key.Status)
            .Select(group => new RepositoryMetadataStatusSummary(
                RepositoryMetadataDiagnostics.StatusWire(group.Key.Status),
                group.Count(),
                group.Key.RepositoryLabelPresent,
                group.Key.UrlFallbackUsed))
            .ToArray();
        var inventoryRows = diagnostics
            .SelectMany(diagnostic => diagnostic.Inventory)
            .GroupBy(row => new { row.Key, row.Scope, row.Classification })
            .OrderBy(group => group.Key.Key, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Scope)
            .Select(group => new RepositoryMetadataInventorySummary(
                group.Key.Key,
                group.Sum(row => row.Count),
                RepositoryMetadataDiagnostics.ScopeWire(group.Key.Scope),
                RepositoryMetadataDiagnostics.ClassificationWire(group.Key.Classification)))
            .ToArray();

        return new(diagnostics.Count, unavailable, statusRows, inventoryRows);
    }
}

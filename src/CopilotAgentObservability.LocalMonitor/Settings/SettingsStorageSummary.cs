using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Settings;

internal sealed class SettingsStorageSummary(
    string databasePath,
    RetentionCatalogStore retention,
    Func<bool> retentionWorkerEnabled,
    SqliteHistoricalImportStore imports,
    RuntimeBackupStatusSnapshot backup,
    RuntimeBackupMonitorLease monitorLease)
{
    internal object Read()
    {
        long? databaseBytes = null;
        try { databaseBytes = new FileInfo(databasePath).Exists ? new FileInfo(databasePath).Length : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        string retentionState;
        try
        {
            retentionState = retention.TryReadStatusSnapshot(retentionWorkerEnabled(), out var value)
                ? value!.WorkerState : "unknown";
        }
        catch { retentionState = "unknown"; }

        var importState = imports.ReadLatestOperationState();
        return new
        {
            schema_version = "settings-storage-summary.v1",
            database_file_size_bytes = databaseBytes,
            retention = new { state = retentionState },
            backup = backup.Read(),
            historical_import = new { state = importState },
            restart_requirement = monitorLease.IsActive ? "not_required" : "unknown",
        };
    }

}

internal sealed class RuntimeBackupStatusSnapshot(TimeProvider timeProvider)
{
    private readonly object gate = new();
    private string state = "idle";
    private string validationState = "unknown";
    private string? lastSuccessfulAt;

    internal object Read() { lock (gate) return new { state, last_successful_at = lastSuccessfulAt, validation_state = validationState }; }
    internal void Running() { lock (gate) state = "running"; }
    internal void Succeeded()
    {
        lock (gate)
        {
            state = "succeeded";
            validationState = "passed";
            lastSuccessfulAt = timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
    }
    internal void Failed() { lock (gate) { state = "failed"; validationState = "unknown"; } }
}

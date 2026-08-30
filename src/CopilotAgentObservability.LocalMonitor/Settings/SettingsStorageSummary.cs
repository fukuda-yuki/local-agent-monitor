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

        object retentionState;
        try
        {
            retentionState = retention.TryReadStatusSnapshot(retentionWorkerEnabled(), out var value)
                ? new { state = value!.WorkerState, pending_count = value.PendingCount, failed_count = value.FailedCount,
                    last_successful_run_at = Timestamp(value.LastSuccessfulRunAt) }
                : new { state = "unknown", pending_count = (long?)null, failed_count = (long?)null, last_successful_run_at = (string?)null };
        }
        catch { retentionState = new { state = "unknown", pending_count = (long?)null, failed_count = (long?)null, last_successful_run_at = (string?)null }; }

        string? importState = null;
        try { importState = imports.ReadLatestOperationStateOrNull(); } catch { }
        return new
        {
            schema_version = "settings-storage-summary.v1",
            database_file_size_bytes = databaseBytes,
            retention = retentionState,
            backup = backup.Read(),
            historical_import = new { state = importState ?? "none" },
            restart_requirement = monitorLease.IsActive ? "not_required" : "unknown",
        };
    }

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
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

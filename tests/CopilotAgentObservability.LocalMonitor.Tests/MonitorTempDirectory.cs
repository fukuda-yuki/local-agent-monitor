namespace CopilotAgentObservability.LocalMonitor.Tests;

internal sealed class MonitorTempDirectory : IDisposable
{
    private CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogContext? retentionContext;

    public MonitorTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"local-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        DatabasePath = System.IO.Path.Combine(Path, "raw-store.db");
        TimeProvider = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
    }

    public string Path { get; }

    public string DatabasePath { get; }

    public TimeProvider TimeProvider { get; set; }

    public CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogContext RetentionContext =>
        retentionContext ??= CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogContext.InitializeNewOwnedDatabase(DatabasePath, TimeProvider);

    public CopilotAgentObservability.Persistence.Sqlite.RawTelemetryStore CreateRawStore(
        CopilotAgentObservability.Persistence.Sqlite.RawTelemetryStoreConnectionOptions? connectionOptions = null) =>
        new(DatabasePath, RetentionContext, TimeProvider, connectionOptions);

    public void Dispose()
    {
        // SQLite file handles (db / WAL / SHM) may take a moment to release on
        // Windows after a connection closes; a hard delete can race that release
        // under parallel test load. Retry briefly, then best-effort give up — a
        // leftover temp directory under %TEMP% must never fail an otherwise-passing test.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Give up: the OS reclaims %TEMP% eventually; do not fail the test.
        }
    }
}

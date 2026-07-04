namespace CopilotAgentObservability.LocalMonitor.Tests;

internal sealed class MonitorTempDirectory : IDisposable
{
    public MonitorTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"local-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        DatabasePath = System.IO.Path.Combine(Path, "raw-store.db");
    }

    public string Path { get; }

    public string DatabasePath { get; }

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

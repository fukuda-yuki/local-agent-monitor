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
        Directory.Delete(Path, recursive: true);
    }
}

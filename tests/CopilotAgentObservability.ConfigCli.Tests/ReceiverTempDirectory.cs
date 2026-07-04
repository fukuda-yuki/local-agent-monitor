namespace CopilotAgentObservability.ConfigCli.Tests;

internal sealed class ReceiverTempDirectory : IDisposable
{
    public ReceiverTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-receiver-{Guid.NewGuid():N}");
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

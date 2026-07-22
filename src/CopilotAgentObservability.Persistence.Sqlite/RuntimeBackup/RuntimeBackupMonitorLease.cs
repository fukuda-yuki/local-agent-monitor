namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

public sealed class RuntimeBackupMonitorLease : IDisposable
{
    private FileStream? stream;

    internal RuntimeBackupMonitorLease(string databasePath, FileStream stream)
    {
        DatabasePath = databasePath;
        this.stream = stream;
    }

    internal string DatabasePath { get; }
    internal bool IsActive => stream is not null;

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
    }
}

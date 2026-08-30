using System.Diagnostics;

namespace CopilotAgentObservability.TestSupport;

internal static class TestFileSystemCleanup
{
    private static readonly TimeSpan WindowsRetryLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WindowsRetryDelay = TimeSpan.FromMilliseconds(10);

    internal static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        Delete(() => File.Delete(path), () => File.Exists(path));
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Delete(() => Directory.Delete(path, recursive: true), () => Directory.Exists(path));
    }

    private static void Delete(Action delete, Func<bool> exists)
    {
        var started = Stopwatch.GetTimestamp();
        while (exists())
        {
            try
            {
                delete();
                return;
            }
            catch (Exception exception) when (
                OperatingSystem.IsWindows()
                && exception is IOException or UnauthorizedAccessException
                && Stopwatch.GetElapsedTime(started) < WindowsRetryLimit)
            {
                Thread.Sleep(WindowsRetryDelay);
            }
        }
    }
}

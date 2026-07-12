using System.Runtime.InteropServices;

namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public sealed class SystemSetupPlatform : ISetupPlatform
{
    public SystemSetupPlatform(ISetupExecution? execution = null, Func<bool>? notificationAttempt = null)
    {
        FileSystem = new SystemSetupFileSystem();
        UserEnvironment = new SystemSetupUserEnvironment(notificationAttempt);
        Clock = new SystemSetupClock();
        Identifiers = new SystemSetupIdentifierGenerator();
        Execution = execution ?? NoOpSetupExecution.Instance;
    }

    public ISetupFileSystem FileSystem { get; }

    public ISetupUserEnvironment UserEnvironment { get; }

    public ISetupClock Clock { get; }

    public ISetupIdentifierGenerator Identifiers { get; }

    public ISetupExecution Execution { get; }

    private sealed class SystemSetupFileSystem : ISetupFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => File.WriteAllBytes(path, bytes);

        public void FlushFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Flush(flushToDisk: true);
        }

        public void ReplaceFile(string sourcePath, string destinationPath) => File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite) => File.Move(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => File.Delete(path);

        public SetupFileMetadata GetFileMetadata(string path)
        {
            if (!File.Exists(path))
            {
                return SetupFileMetadata.Missing;
            }

            var file = new FileInfo(path);
            return new SetupFileMetadata(
                true,
                file.Length,
                file.Attributes,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }

    private sealed class SystemSetupUserEnvironment : ISetupUserEnvironment
    {
        private const string NotificationFailureCode = "setup_environment_notification_failed";
        private readonly Func<bool> notificationAttempt;

        public SystemSetupUserEnvironment(Func<bool>? notificationAttempt)
        {
            this.notificationAttempt = notificationAttempt ?? TryNotifyChange;
        }

        public string? Get(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

        public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

        public void NotifyChange()
        {
            bool notified;
            try
            {
                notified = notificationAttempt();
            }
            catch (Exception)
            {
                throw new InvalidOperationException(NotificationFailureCode);
            }

            if (!notified)
            {
                throw new InvalidOperationException(NotificationFailureCode);
            }
        }

        private static bool TryNotifyChange() => !OperatingSystem.IsWindows() ||
            SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment", 0x0002, 5000, out _) != IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr windowHandle,
            uint message,
            IntPtr wordParameter,
            string textParameter,
            uint flags,
            uint timeoutMilliseconds,
            out IntPtr result);
    }

    private sealed class SystemSetupClock : ISetupClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class SystemSetupIdentifierGenerator : ISetupIdentifierGenerator
    {
        public Guid CreateUuidV7() => Guid.CreateVersion7();
    }

    private sealed class NoOpSetupExecution : ISetupExecution
    {
        public static NoOpSetupExecution Instance { get; } = new();

        public void Checkpoint(string operation)
        {
        }
    }
}

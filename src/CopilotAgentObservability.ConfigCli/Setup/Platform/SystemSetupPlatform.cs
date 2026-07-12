using System.Runtime.InteropServices;

namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public sealed class SystemSetupPlatform : ISetupPlatform
{
    public SystemSetupPlatform(
        ISetupExecution? execution = null,
        Func<bool>? notificationAttempt = null,
        string? localApplicationData = null,
        Func<string, FileStream>? exclusiveFileLockAttempt = null)
    {
        LocalApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        FileSystem = new SystemSetupFileSystem(exclusiveFileLockAttempt);
        UserEnvironment = new SystemSetupUserEnvironment(notificationAttempt);
        Clock = new SystemSetupClock();
        Identifiers = new SystemSetupIdentifierGenerator();
        Execution = execution ?? NoOpSetupExecution.Instance;
    }

    public string LocalApplicationData { get; }

    public SetupPathStyle PathStyle => OperatingSystem.IsWindows() ? SetupPathStyle.Windows : SetupPathStyle.Unix;

    public ISetupFileSystem FileSystem { get; }

    public ISetupUserEnvironment UserEnvironment { get; }

    public ISetupClock Clock { get; }

    public ISetupIdentifierGenerator Identifiers { get; }

    public ISetupExecution Execution { get; }

    internal static bool IsExclusiveFileLockContention(IOException exception)
    {
        const int WindowsSharingViolation = unchecked((int)0x80070020);
        const int WindowsLockViolation = unchecked((int)0x80070021);
        const int LinuxEagain = 11;
        const int MacOsEagain = 35;

        if (OperatingSystem.IsWindows())
        {
            return exception.HResult is WindowsSharingViolation or WindowsLockViolation;
        }

        if (OperatingSystem.IsLinux())
        {
            return exception.HResult == LinuxEagain;
        }

        return OperatingSystem.IsMacOS() && exception.HResult == MacOsEagain;
    }

    private sealed class SystemSetupFileSystem : ISetupFileSystem
    {
        private readonly Func<string, FileStream> exclusiveFileLockAttempt;

        public SystemSetupFileSystem(Func<string, FileStream>? exclusiveFileLockAttempt)
        {
            this.exclusiveFileLockAttempt = exclusiveFileLockAttempt ?? OpenExclusiveFileLock;
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public bool FileExists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => File.WriteAllBytes(path, bytes);

        public void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }

        public void FlushFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Flush(flushToDisk: true);
        }

        public void ReplaceFile(string sourcePath, string destinationPath) => File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite) => File.Move(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => File.Delete(path);

        public SetupPathMetadata GetPathMetadata(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return SetupPathMetadata.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return SetupPathMetadata.Missing;
            }

            var kind = (attributes & FileAttributes.Directory) != 0
                ? SetupPathKind.Directory
                : OperatingSystem.IsWindows()
                    ? SetupPathKind.File
                    : GetUnixPathKind(path);
            return new SetupPathMetadata(true, kind, attributes);
        }

        private static SetupPathKind GetUnixPathKind(string path)
        {
            try
            {
                using var stream = File.Open(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 1,
                });
                return stream.CanSeek ? SetupPathKind.File : SetupPathKind.Other;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return SetupPathKind.Other;
            }
        }

        public ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path)
        {
            try
            {
                return new SystemSetupExclusiveFileLock(exclusiveFileLockAttempt(path));
            }
            catch (IOException exception) when (IsExclusiveFileLockContention(exception))
            {
                return null;
            }
        }

        private static FileStream OpenExclusiveFileLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        private sealed class SystemSetupExclusiveFileLock(FileStream stream) : ISetupExclusiveFileLock
        {
            public void Dispose() => stream.Dispose();
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

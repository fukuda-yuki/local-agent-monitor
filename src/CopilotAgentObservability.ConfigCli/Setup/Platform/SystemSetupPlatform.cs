using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace CopilotAgentObservability.ConfigCli.Setup.Platform;

public sealed class SystemSetupPlatform : ISetupPlatform
{
    public SystemSetupPlatform(
        ISetupExecution? execution = null,
        Func<bool>? notificationAttempt = null,
        string? localApplicationData = null,
        Func<string, FileStream>? exclusiveFileLockAttempt = null,
        Func<string, EnvironmentVariableTarget, string?>? userEnvironmentRead = null,
        Action<string, string?, EnvironmentVariableTarget>? userEnvironmentWrite = null)
    {
        LocalApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        FileSystem = new SystemSetupFileSystem(exclusiveFileLockAttempt);
        UserEnvironment = new SystemSetupUserEnvironment(notificationAttempt, userEnvironmentRead, userEnvironmentWrite);
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

        public SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            if (maximumBytes == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[maximumBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new SetupBoundedFileRead(buffer.AsSpan(0, length).ToArray(), length <= maximumBytes);
        }

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
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return WindowsPathMetadata.Read(path);
                }

                if (OperatingSystem.IsLinux())
                {
                    return LinuxPathMetadata.Read(path);
                }

                if (OperatingSystem.IsMacOS())
                {
                    return MacOsPathMetadata.Read(path);
                }
            }
            catch (Exception)
            {
            }

            return ExistingOther();
        }

        private static SetupPathMetadata ExistingOther(FileAttributes attributes = 0) =>
            new(true, SetupPathKind.Other, attributes);

        private static class WindowsPathMetadata
        {
            private const uint FileReadAttributes = 0x00000080;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint FileShareDelete = 0x00000004;
            private const uint OpenExisting = 3;
            private const uint OpenReparsePoint = 0x00200000;
            private const uint BackupSemantics = 0x02000000;
            private const int FileAttributeTagInfoClass = 9;
            private const uint FileTypeDisk = 0x0001;
            private const int ErrorFileNotFound = 2;
            private const int ErrorPathNotFound = 3;

            public static SetupPathMetadata Read(string path)
            {
                using var handle = CreateFile(
                    path,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    OpenReparsePoint | BackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return IsMissing(Marshal.GetLastPInvokeError()) ? SetupPathMetadata.Missing : ExistingOther();
                }

                if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out var info,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                {
                    return ExistingOther();
                }

                var attributes = (FileAttributes)info.FileAttributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return ExistingOther(attributes);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    return new SetupPathMetadata(true, SetupPathKind.Directory, attributes);
                }

                return GetFileType(handle) == FileTypeDisk
                    ? new SetupPathMetadata(true, SetupPathKind.File, attributes)
                    : ExistingOther(attributes);
            }

            private static bool IsMissing(int error) => error is ErrorFileNotFound or ErrorPathNotFound;

            [StructLayout(LayoutKind.Sequential)]
            private struct FileAttributeTagInfo
            {
                public uint FileAttributes;
                public uint ReparseTag;
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int fileInformationClass,
                out FileAttributeTagInfo fileInformation,
                uint bufferSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint GetFileType(SafeFileHandle file);
        }

        private static class LinuxPathMetadata
        {
            private const int AtFileDescriptorCurrentWorkingDirectory = -100;
            private const int AtSymlinkNoFollow = 0x100;
            private const uint StatxType = 0x00000001;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;
            private const ushort FileTypeMask = 0xf000;
            private const ushort FileTypeFifo = 0x1000;
            private const ushort FileTypeCharacter = 0x2000;
            private const ushort FileTypeDirectory = 0x4000;
            private const ushort FileTypeBlock = 0x6000;
            private const ushort FileTypeRegular = 0x8000;
            private const ushort FileTypeLink = 0xa000;
            private const ushort FileTypeSocket = 0xc000;
            private const int StatxBufferSize = 256;
            private const int StatxModeOffset = 28;

            public static SetupPathMetadata Read(string path)
            {
                var buffer = new byte[StatxBufferSize];
                if (Statx(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymlinkNoFollow,
                    StatxType,
                    buffer) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is ErrorNoEntry or ErrorNotDirectory ? SetupPathMetadata.Missing : ExistingOther();
                }

                var mode = BitConverter.ToUInt16(buffer, StatxModeOffset);
                return (ushort)(mode & FileTypeMask) switch
                {
                    FileTypeRegular => new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal),
                    FileTypeDirectory => new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory),
                    FileTypeLink => ExistingOther(FileAttributes.ReparsePoint),
                    FileTypeFifo or FileTypeSocket or FileTypeCharacter or FileTypeBlock => ExistingOther(),
                    _ => ExistingOther(),
                };
            }

            [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
            private static extern int Statx(
                int directoryFileDescriptor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                int flags,
                uint mask,
                [Out] byte[] buffer);
        }

        private static class MacOsPathMetadata
        {
            private const ushort AttributeBitmapCount = 5;
            private const uint AttributeCommonObjectType = 0x00000008;
            private const ulong FileSystemOptionNoFollow = 0x00000001;
            private const int ErrorNoEntry = 2;
            private const int ErrorNotDirectory = 20;
            private const uint VnodeTypeRegular = 1;
            private const uint VnodeTypeDirectory = 2;
            private const uint VnodeTypeBlock = 3;
            private const uint VnodeTypeCharacter = 4;
            private const uint VnodeTypeLink = 5;
            private const uint VnodeTypeSocket = 6;
            private const uint VnodeTypeFifo = 7;

            public static SetupPathMetadata Read(string path)
            {
                var attributes = new AttributeList
                {
                    BitmapCount = AttributeBitmapCount,
                    CommonAttributes = AttributeCommonObjectType,
                };
                var buffer = new byte[8];
                if (GetAttributeList(path, ref attributes, buffer, (nuint)buffer.Length, FileSystemOptionNoFollow) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is ErrorNoEntry or ErrorNotDirectory ? SetupPathMetadata.Missing : ExistingOther();
                }

                if (BitConverter.ToUInt32(buffer, 0) < buffer.Length)
                {
                    return ExistingOther();
                }

                return BitConverter.ToUInt32(buffer, 4) switch
                {
                    VnodeTypeRegular => new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal),
                    VnodeTypeDirectory => new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory),
                    VnodeTypeLink => ExistingOther(FileAttributes.ReparsePoint),
                    VnodeTypeFifo or VnodeTypeSocket or VnodeTypeCharacter or VnodeTypeBlock => ExistingOther(),
                    _ => ExistingOther(),
                };
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct AttributeList
            {
                public ushort BitmapCount;
                public ushort Reserved;
                public uint CommonAttributes;
                public uint VolumeAttributes;
                public uint DirectoryAttributes;
                public uint FileAttributes;
                public uint ForkAttributes;
            }

            [DllImport("libSystem.B.dylib", EntryPoint = "getattrlist", SetLastError = true)]
            private static extern int GetAttributeList(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                ref AttributeList attributes,
                [Out] byte[] buffer,
                nuint bufferSize,
                ulong options);
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
        private readonly Func<string, EnvironmentVariableTarget, string?> read;
        private readonly Action<string, string?, EnvironmentVariableTarget> write;

        public SystemSetupUserEnvironment(
            Func<bool>? notificationAttempt,
            Func<string, EnvironmentVariableTarget, string?>? read,
            Action<string, string?, EnvironmentVariableTarget>? write)
        {
            this.notificationAttempt = notificationAttempt ?? TryNotifyChange;
            this.read = read ?? Environment.GetEnvironmentVariable;
            this.write = write ?? Environment.SetEnvironmentVariable;
        }

        public string? Get(string name) => read(name, EnvironmentVariableTarget.User);

        public void Set(string name, string? value) => write(name, value, EnvironmentVariableTarget.User);

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

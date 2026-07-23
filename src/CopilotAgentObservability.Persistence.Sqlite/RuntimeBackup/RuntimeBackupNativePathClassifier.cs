using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal enum RuntimeBackupNativePathKind
{
    Missing,
    RegularFile,
    Directory,
    Reparse,
    OtherOrUnavailable,
}

internal static class RuntimeBackupNativePathClassifier
{
    internal static DriveType ReadWindowsDriveType(string rootPath) =>
        OperatingSystem.IsWindows()
            ? WindowsPathMetadata.ReadDriveType(rootPath)
            : DriveType.Unknown;

    internal static RuntimeBackupNativePathKind Read(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPathMetadata.Read(path);
            if (OperatingSystem.IsLinux()) return LinuxPathMetadata.Read(path);
            if (OperatingSystem.IsMacOS()) return MacOsPathMetadata.Read(path);
        }
        catch (Exception)
        {
        }

        return RuntimeBackupNativePathKind.OtherOrUnavailable;
    }

    internal static RuntimeBackupNativePathKind Read(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return RuntimeBackupNativePathKind.OtherOrUnavailable;
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPathMetadata.Read(handle);
            if (OperatingSystem.IsLinux()) return LinuxPathMetadata.Read(handle);
            if (OperatingSystem.IsMacOS()) return MacOsPathMetadata.Read(handle);
        }
        catch (Exception)
        {
        }

        return RuntimeBackupNativePathKind.OtherOrUnavailable;
    }

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

        internal static RuntimeBackupNativePathKind Read(string path)
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
                return IsMissing(Marshal.GetLastPInvokeError())
                    ? RuntimeBackupNativePathKind.Missing
                    : RuntimeBackupNativePathKind.OtherOrUnavailable;
            }

            return Read(handle);
        }

        internal static RuntimeBackupNativePathKind Read(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out var info,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                return RuntimeBackupNativePathKind.OtherOrUnavailable;

            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0) return RuntimeBackupNativePathKind.Reparse;
            if ((attributes & FileAttributes.Directory) != 0) return RuntimeBackupNativePathKind.Directory;
            return GetFileType(handle) == FileTypeDisk
                ? RuntimeBackupNativePathKind.RegularFile
                : RuntimeBackupNativePathKind.OtherOrUnavailable;
        }

        internal static DriveType ReadDriveType(string rootPath) => (DriveType)GetDriveType(rootPath);

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

        [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string rootPathName);
    }

    private static class LinuxPathMetadata
    {
        private const int AtFileDescriptorCurrentWorkingDirectory = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const int AtEmptyPath = 0x1000;
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

        internal static RuntimeBackupNativePathKind Read(string path)
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
                return error is ErrorNoEntry or ErrorNotDirectory
                    ? RuntimeBackupNativePathKind.Missing
                    : RuntimeBackupNativePathKind.OtherOrUnavailable;
            }

            return Classify(buffer);
        }

        internal static RuntimeBackupNativePathKind Read(SafeFileHandle handle)
        {
            var buffer = new byte[StatxBufferSize];
            if (Statx(
                    handle.DangerousGetHandle().ToInt32(),
                    string.Empty,
                    AtEmptyPath | AtSymlinkNoFollow,
                    StatxType,
                    buffer) != 0)
                return RuntimeBackupNativePathKind.OtherOrUnavailable;
            return Classify(buffer);
        }

        private static RuntimeBackupNativePathKind Classify(byte[] buffer)
        {
            var mode = BitConverter.ToUInt16(buffer, StatxModeOffset);
            return (ushort)(mode & FileTypeMask) switch
            {
                FileTypeRegular => RuntimeBackupNativePathKind.RegularFile,
                FileTypeDirectory => RuntimeBackupNativePathKind.Directory,
                FileTypeLink => RuntimeBackupNativePathKind.Reparse,
                FileTypeFifo or FileTypeSocket or FileTypeCharacter or FileTypeBlock => RuntimeBackupNativePathKind.OtherOrUnavailable,
                _ => RuntimeBackupNativePathKind.OtherOrUnavailable,
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
        private const int StatBufferSize = 256;
        private const int StatModeOffset = 4;

        internal static RuntimeBackupNativePathKind Read(string path)
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
                return error is ErrorNoEntry or ErrorNotDirectory
                    ? RuntimeBackupNativePathKind.Missing
                    : RuntimeBackupNativePathKind.OtherOrUnavailable;
            }

            if (BitConverter.ToUInt32(buffer, 0) < buffer.Length)
                return RuntimeBackupNativePathKind.OtherOrUnavailable;

            return Classify(BitConverter.ToUInt32(buffer, 4));
        }

        internal static RuntimeBackupNativePathKind Read(SafeFileHandle handle)
        {
            var buffer = new byte[StatBufferSize];
            if (FStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
                return RuntimeBackupNativePathKind.OtherOrUnavailable;
            var mode = BitConverter.ToUInt16(buffer, StatModeOffset);
            return Classify((uint)(mode & 0xf000) switch
            {
                0x8000 => VnodeTypeRegular,
                0x4000 => VnodeTypeDirectory,
                0x6000 => VnodeTypeBlock,
                0x2000 => VnodeTypeCharacter,
                0xa000 => VnodeTypeLink,
                0xc000 => VnodeTypeSocket,
                0x1000 => VnodeTypeFifo,
                _ => 0,
            });
        }

        private static RuntimeBackupNativePathKind Classify(uint type) => type switch
            {
                VnodeTypeRegular => RuntimeBackupNativePathKind.RegularFile,
                VnodeTypeDirectory => RuntimeBackupNativePathKind.Directory,
                VnodeTypeLink => RuntimeBackupNativePathKind.Reparse,
                VnodeTypeFifo or VnodeTypeSocket or VnodeTypeCharacter or VnodeTypeBlock => RuntimeBackupNativePathKind.OtherOrUnavailable,
                _ => RuntimeBackupNativePathKind.OtherOrUnavailable,
            };

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

        [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(int fileDescriptor, [Out] byte[] buffer);
    }
}

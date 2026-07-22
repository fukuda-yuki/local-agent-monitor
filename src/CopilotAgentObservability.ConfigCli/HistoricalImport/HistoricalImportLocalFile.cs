using Microsoft.Win32.SafeHandles;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using System.Runtime.InteropServices;
using System.Text;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport;

internal enum HistoricalImportPathKind
{
    Missing,
    RegularFile,
    Directory,
    Other,
    Unavailable,
}

internal static class HistoricalImportLocalFile
{
    internal static string NormalizeSafePath(string path)
    {
        if (!HistoricalSourcePathPolicy.TryNormalizeSafeLocalFilePath(path, out var normalizedPath))
        {
            throw new InvalidDataException();
        }

        return normalizedPath;
    }

    internal static HistoricalImportPathKind Inspect(string path)
    {
        try
        {
            if (!HistoricalSourcePathPolicy.TryNormalizeSafeLocalFilePath(path, out var normalizedPath))
            {
                return HistoricalImportPathKind.Other;
            }

            var ancestorKind = InspectAncestors(normalizedPath);
            if (ancestorKind != HistoricalImportPathKind.Directory)
            {
                return ancestorKind;
            }

            return InspectEntry(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return HistoricalImportPathKind.Other;
        }
        catch (Exception exception) when (!IsFatalOrControlFlow(exception))
        {
            return HistoricalImportPathKind.Unavailable;
        }
    }

    internal static byte[] ReadAtMostBytes(string path, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (maximumBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var stream = OpenRegularRead(path, allowWriteShare: false);
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return bytes.AsSpan(0, count).ToArray();
    }

    internal static FileStream OpenRegularRead(string path, bool allowWriteShare)
    {
        if (!HistoricalSourcePathPolicy.TryNormalizeSafeLocalFilePath(path, out var normalizedPath))
        {
            throw new InvalidDataException();
        }

        RequireSafeAncestors(normalizedPath);

        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = WindowsFile.OpenRegularRead(normalizedPath, allowWriteShare);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = LinuxFile.OpenRegularRead(normalizedPath);
        }
        else if (OperatingSystem.IsMacOS())
        {
            handle = MacOsFile.OpenRegularRead(normalizedPath);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        try
        {
            RequireExpectedOpenedPath(normalizedPath, handle);
            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static HistoricalImportFileIdentity ReadIdentity(FileStream stream) =>
        ReadIdentity(stream.SafeFileHandle);

    internal static HistoricalImportFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsFile.ReadIdentity(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxFile.ReadIdentity(handle);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsFile.ReadIdentity(handle);
        }

        throw new PlatformNotSupportedException();
    }

    internal static HistoricalImportFileIdentity ReadWindowsIdentity(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return WindowsFile.ReadIdentity(handle);
    }

    private static void RequireExpectedOpenedPath(string expectedPath, SafeFileHandle handle)
    {
        string? openedPath = null;
        if (OperatingSystem.IsWindows())
        {
            openedPath = WindowsFile.ReadFinalPath(handle);
        }
        else if (OperatingSystem.IsMacOS())
        {
            openedPath = MacOsFile.ReadFinalPath(handle);
        }

        if (openedPath is null)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(expectedPath), Path.GetFullPath(openedPath), comparison))
        {
            throw new InvalidDataException();
        }
    }

    private static HistoricalImportPathKind InspectAncestors(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return HistoricalImportPathKind.Other;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var tail = path[root.Length..].TrimEnd(separators);
        if (tail.Length == 0)
        {
            return HistoricalImportPathKind.Directory;
        }

        var components = tail.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < components.Length - 1; index++)
        {
            current = Path.Combine(current, components[index]);
            var kind = InspectEntry(current);
            if (kind != HistoricalImportPathKind.Directory)
            {
                return kind == HistoricalImportPathKind.RegularFile
                    ? HistoricalImportPathKind.Other
                    : kind;
            }
        }

        return HistoricalImportPathKind.Directory;
    }

    private static HistoricalImportPathKind InspectEntry(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsFile.Inspect(path);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxFile.Inspect(path);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsFile.Inspect(path);
        }

        return HistoricalImportPathKind.Unavailable;
    }

    private static void RequireSafeAncestors(string path)
    {
        switch (InspectAncestors(path))
        {
            case HistoricalImportPathKind.Directory:
                return;
            case HistoricalImportPathKind.Missing:
                throw new FileNotFoundException();
            case HistoricalImportPathKind.Other:
                throw new InvalidDataException();
            default:
                throw new IOException();
        }
    }

    private static Exception OpenFailure(int error, int missing, int notDirectory, int accessDenied, int operationNotPermitted) =>
        error == missing || error == notDirectory
            ? new FileNotFoundException()
            : error == accessDenied || error == operationNotPermitted
                ? new UnauthorizedAccessException()
                : new IOException();

    #pragma warning disable CS0618 // Preserve legacy fatal exception pass-through if injected by a test/runtime host.
    private static bool IsFatalOrControlFlow(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException or
        AppDomainUnloadedException or
        BadImageFormatException or
        CannotUnloadAppDomainException or
        InvalidProgramException or
        System.Threading.ThreadAbortException or
        ThreadInterruptedException or
        OperationCanceledException;
    #pragma warning restore CS0618

    private static class WindowsFile
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const int FileAttributeTagInfoClass = 9;
        private const uint FileTypeDisk = 0x0001;
        private const uint FinalPathVolumeNameDos = 0x00000000;
        private const uint FinalPathFileNameNormalized = 0x00000000;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;

        internal static HistoricalImportPathKind Inspect(string path)
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
                var error = Marshal.GetLastPInvokeError();
                return error is ErrorFileNotFound or ErrorPathNotFound
                    ? HistoricalImportPathKind.Missing
                    : HistoricalImportPathKind.Unavailable;
            }

            return Classify(handle);
        }

        internal static SafeFileHandle OpenRegularRead(string path, bool allowWriteShare)
        {
            var share = FileShareRead | (allowWriteShare ? FileShareWrite : 0u);
            var handle = CreateFile(
                path,
                GenericRead,
                share,
                IntPtr.Zero,
                OpenExisting,
                OpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw OpenFailure(error, ErrorFileNotFound, ErrorPathNotFound, ErrorAccessDenied, ErrorAccessDenied);
            }

            if (Classify(handle) != HistoricalImportPathKind.RegularFile)
            {
                handle.Dispose();
                throw new InvalidDataException();
            }

            return handle;
        }

        internal static HistoricalImportFileIdentity ReadIdentity(SafeFileHandle handle) =>
            ReadIdentity(handle.DangerousGetHandle());

        internal static HistoricalImportFileIdentity ReadIdentity(IntPtr handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException();
            }

            return new(
                information.VolumeSerialNumber,
                0,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                0);
        }

        internal static string ReadFinalPath(SafeFileHandle handle)
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                FinalPathVolumeNameDos | FinalPathFileNameNormalized);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException();
            }

            var path = new string(buffer, 0, checked((int)length));
            return path.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? path[4..]
                : path;
        }

        private static HistoricalImportPathKind Classify(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out var info,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                return HistoricalImportPathKind.Unavailable;
            }

            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return HistoricalImportPathKind.Other;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return HistoricalImportPathKind.Directory;
            }

            return GetFileType(handle) == FileTypeDisk
                ? HistoricalImportPathKind.RegularFile
                : HistoricalImportPathKind.Other;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            IntPtr file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);
    }

    private static class LinuxFile
    {
        private const long OpenAt2SystemCall = 437;
        private const int AtCurrentWorkingDirectory = -100;
        private const int AtEmptyPath = 0x1000;
        private const int AtSymlinkNoFollow = 0x100;
        private const ulong OpenReadOnly = 0;
        private const ulong OpenNonBlocking = 0x800;
        private const ulong OpenNoFollow = 0x20000;
        private const ulong OpenCloseOnExec = 0x80000;
        private const ulong ResolveNoMagicLinks = 0x02;
        private const ulong ResolveNoSymbolicLinks = 0x04;
        private const uint StatxType = 0x00000001;
        private const uint StatxBasicStats = 0x000007ff;
        private const int StatxBufferSize = 256;
        private const int StatxModeOffset = 28;
        private const int ErrorOperationNotPermitted = 1;
        private const int ErrorNoEntry = 2;
        private const int ErrorAccessDenied = 13;
        private const int ErrorNotDirectory = 20;

        internal static HistoricalImportPathKind Inspect(string path)
        {
            var buffer = new byte[StatxBufferSize];
            if (Statx(AtCurrentWorkingDirectory, path, AtSymlinkNoFollow, StatxType, buffer) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return error is ErrorNoEntry or ErrorNotDirectory
                    ? HistoricalImportPathKind.Missing
                    : HistoricalImportPathKind.Unavailable;
            }

            return Classify(BitConverter.ToUInt16(buffer, StatxModeOffset));
        }

        internal static SafeFileHandle OpenRegularRead(string path)
        {
            var how = new OpenHow
            {
                Flags = OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
                Resolve = ResolveNoMagicLinks | ResolveNoSymbolicLinks,
            };
            var descriptor = OpenAt2(
                OpenAt2SystemCall,
                AtCurrentWorkingDirectory,
                path,
                ref how,
                (nuint)Marshal.SizeOf<OpenHow>());
            if (descriptor == -1)
            {
                throw OpenFailure(
                    Marshal.GetLastPInvokeError(),
                    ErrorNoEntry,
                    ErrorNotDirectory,
                    ErrorAccessDenied,
                    ErrorOperationNotPermitted);
            }

            var handle = new SafeFileHandle(descriptor, ownsHandle: true);
            var buffer = new byte[StatxBufferSize];
            if (Statx(checked((int)descriptor), string.Empty, AtEmptyPath, StatxType, buffer) != 0
                || Classify(BitConverter.ToUInt16(buffer, StatxModeOffset)) != HistoricalImportPathKind.RegularFile)
            {
                handle.Dispose();
                throw new InvalidDataException();
            }

            return handle;
        }

        internal static HistoricalImportFileIdentity ReadIdentity(SafeFileHandle handle)
        {
            var buffer = new byte[StatxBufferSize];
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (Statx(descriptor, string.Empty, AtEmptyPath, StatxBasicStats, buffer) != 0)
            {
                throw new IOException();
            }

            return new(
                BitConverter.ToUInt32(buffer, 136),
                BitConverter.ToUInt32(buffer, 140),
                BitConverter.ToUInt64(buffer, 32),
                0);
        }

        private static HistoricalImportPathKind Classify(ushort mode) => (mode & 0xf000) switch
        {
            0x8000 => HistoricalImportPathKind.RegularFile,
            0x4000 => HistoricalImportPathKind.Directory,
            _ => HistoricalImportPathKind.Other,
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenHow
        {
            public ulong Flags;
            public ulong Mode;
            public ulong Resolve;
        }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern nint OpenAt2(
            long number,
            int directoryFileDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            ref OpenHow how,
            nuint size);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int Statx(
            int directoryFileDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            [Out] byte[] buffer);
    }

    private static class MacOsFile
    {
        private const int OpenReadOnly = 0;
        private const int OpenNonBlocking = 0x0004;
        private const int OpenNoFollow = 0x0100;
        private const int OpenCloseOnExec = 0x01000000;
        private const int FileControlGetPath = 50;
        private const int MaximumPathBytes = 1024;
        private const int StatBufferSize = 256;
        private const int StatModeOffset = 4;
        private const ushort AttributeBitmapCount = 5;
        private const uint AttributeCommonObjectType = 0x00000008;
        private const ulong FileSystemOptionNoFollow = 0x00000001;
        private const int ErrorOperationNotPermitted = 1;
        private const int ErrorNoEntry = 2;
        private const int ErrorAccessDenied = 13;
        private const int ErrorNotDirectory = 20;

        internal static HistoricalImportPathKind Inspect(string path)
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
                    ? HistoricalImportPathKind.Missing
                    : HistoricalImportPathKind.Unavailable;
            }

            if (BitConverter.ToUInt32(buffer, 0) < buffer.Length)
            {
                return HistoricalImportPathKind.Unavailable;
            }

            return BitConverter.ToUInt32(buffer, 4) switch
            {
                1 => HistoricalImportPathKind.RegularFile,
                2 => HistoricalImportPathKind.Directory,
                _ => HistoricalImportPathKind.Other,
            };
        }

        internal static SafeFileHandle OpenRegularRead(string path)
        {
            var descriptor = Open(path, OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
            if (descriptor == -1)
            {
                throw OpenFailure(
                    Marshal.GetLastPInvokeError(),
                    ErrorNoEntry,
                    ErrorNotDirectory,
                    ErrorAccessDenied,
                    ErrorOperationNotPermitted);
            }

            var handle = new SafeFileHandle(descriptor, ownsHandle: true);
            var buffer = new byte[StatBufferSize];
            if (FStat(descriptor, buffer) != 0
                || (BitConverter.ToUInt16(buffer, StatModeOffset) & 0xf000) != 0x8000)
            {
                handle.Dispose();
                throw new InvalidDataException();
            }

            return handle;
        }

        internal static HistoricalImportFileIdentity ReadIdentity(SafeFileHandle handle)
        {
            var buffer = new byte[StatBufferSize];
            if (FStat(checked((int)handle.DangerousGetHandle()), buffer) != 0)
            {
                throw new IOException();
            }

            return new(
                BitConverter.ToUInt32(buffer, 0),
                0,
                BitConverter.ToUInt64(buffer, 8),
                0);
        }

        internal static string ReadFinalPath(SafeFileHandle handle)
        {
            var buffer = new byte[MaximumPathBytes];
            if (FileControl(checked((int)handle.DangerousGetHandle()), FileControlGetPath, buffer) != 0)
            {
                throw new IOException();
            }

            var terminator = Array.IndexOf(buffer, (byte)0);
            if (terminator <= 0)
            {
                throw new IOException();
            }

            return Encoding.UTF8.GetString(buffer, 0, terminator);
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
            [Out] byte[] attributeBuffer,
            nuint attributeBufferSize,
            ulong options);

        [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
        private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(int fileDescriptor, [Out] byte[] buffer);

        [DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int FileControl(int fileDescriptor, int command, [Out] byte[] buffer);
    }
}

internal readonly record struct HistoricalImportFileIdentity(
    ulong DeviceHigh,
    ulong DeviceLow,
    ulong FileHigh,
    ulong FileLow);

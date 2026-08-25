using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// The sole Windows native surface for Gate 8 retained-root walking. All structs are marshaled
// through Marshal.AllocHGlobal because the assembly deliberately builds without unsafe blocks.
internal static class WindowsNativeFileApisV1
{
    internal const uint StatusSuccess = 0x00000000;
    internal const uint StatusAccessDenied = 0xC0000022;
    internal const uint StatusObjectNameNotFound = 0xC0000034;
    internal const uint StatusObjectNameCollision = 0xC0000035;
    internal const uint StatusObjectPathNotFound = 0xC000003A;
    internal const uint StatusFileIsADirectory = 0xC00000BA;
    internal const uint StatusNoSuchFile = 0xC000000F;
    internal const uint StatusNotADirectory = 0xC0000103;
    internal const uint StatusSharingViolation = 0xC0000043;

    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileReadData = 0x0001;
    internal const uint Synchronize = 0x100000;

    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint FileShareDelete = 0x4;
    internal const uint ShareAll = FileShareRead | FileShareWrite | FileShareDelete;

    internal const uint FileOpen = 1;

    internal const uint FileDirectoryFile = 0x0001;
    internal const uint FileNonDirectoryFile = 0x0040;
    internal const uint FileSynchronousIoNonAlert = 0x0020;
    internal const uint FileOpenReparsePoint = 0x200000;

    internal const uint AttributeDirectory = 0x0010;
    internal const uint AttributeReparsePoint = 0x0400;

    internal const int FileIdInfoClass = 18;
    internal const int FileBasicInformationClass = 4;
    internal const int FileStandardInformationClass = 5;

    internal const uint DriveFixed = 3;

    internal static readonly uint DirectoryOpenAccess = Synchronize | FileReadAttributes;
    internal static readonly uint FileOpenAccess = Synchronize | FileReadAttributes | FileReadData;
    internal static readonly uint DirectoryCreateOptions = FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert;
    internal static readonly uint FileCreateOptions = FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert;

    // The sole confirmed-not-found NTSTATUS values under a re-proved retained root with no prior
    // identity observed for the looked-up segment (Gate 8, spec 2251-2261).
    internal static bool IsConfirmedNotFoundStatus(uint ntStatus) => ntStatus is
        StatusNoSuchFile or StatusObjectNameNotFound or StatusObjectPathNotFound;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public bool DeletePending;
        public bool Directory;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    internal static extern uint NtCreateFile(
        out SafeFileHandle FileHandle,
        uint DesiredAccess,
        ref ObjectAttributes ObjectAttributes,
        out IoStatusBlock IoStatusBlock,
        IntPtr AllocationSize,
        uint FileAttributes,
        uint ShareAccess,
        uint CreateDisposition,
        uint CreateOptions,
        IntPtr EaBuffer,
        uint EaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    internal static extern uint NtQueryInformationFile(
        SafeFileHandle FileHandle,
        out IoStatusBlock IoStatusBlock,
        out FileBasicInformation FileInformation,
        int Length,
        int FileInformationClass);

    [DllImport("ntdll.dll", ExactSpelling = true, EntryPoint = "NtQueryInformationFile")]
    internal static extern uint NtQueryStandardInformationFile(
        SafeFileHandle FileHandle,
        out IoStatusBlock IoStatusBlock,
        out FileStandardInformation FileInformation,
        int Length,
        int FileInformationClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        out FileIdInfo lpFileInformation,
        uint dwBufferSize);

    // IntPtr buffer rather than byte[]: the walker reads at a moving offset into one pinned
    // request-local buffer, and byte[] marshaling would pin only a per-call slice copy.
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetFileType(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle hFile,
        char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char[]? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetDriveTypeW(string lpRootPathName);

    // One relative open beneath a retained parent handle. The object name is a single segment;
    // no OBJ_INHERIT is set, so the resulting handle is noninheritable.
    internal static uint OpenRelative(
        SafeFileHandle parent,
        string segment,
        uint desiredAccess,
        uint createOptions,
        out SafeFileHandle handle) =>
        OpenUnder(parent.DangerousGetHandle(), segment, desiredAccess, createOptions, out handle);

    // Absolute NT object-name open (for example the drive anchor "\??\C:").
    internal static uint OpenUnder(
        IntPtr rootDirectory,
        string objectName,
        uint desiredAccess,
        uint createOptions,
        out SafeFileHandle handle)
    {
        handle = null!;
        var nameBuffer = IntPtr.Zero;
        var nameStruct = IntPtr.Zero;

        try
        {
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(objectName);
            if (nameBytes.Length > ushort.MaxValue)
            {
                return StatusObjectNameNotFound;
            }

            nameBuffer = Marshal.AllocHGlobal(nameBytes.Length);
            Marshal.Copy(nameBytes, 0, nameBuffer, nameBytes.Length);

            var unicodeString = new UnicodeString
            {
                Length = (ushort)nameBytes.Length,
                MaximumLength = (ushort)nameBytes.Length,
                Buffer = nameBuffer
            };

            nameStruct = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, nameStruct, fDeleteOld: false);

            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = rootDirectory,
                ObjectName = nameStruct,
                Attributes = 0,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };

            return NtCreateFile(
                out handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                0,
                ShareAll,
                FileOpen,
                createOptions,
                IntPtr.Zero,
                0);
        }
        finally
        {
            if (nameStruct != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nameStruct);
            }

            if (nameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
        }
    }

    internal static bool TryGetIdentity(SafeFileHandle handle, out ulong volumeSerial, out byte[] fileId128)
    {
        volumeSerial = 0;
        fileId128 = [];

        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        volumeSerial = info.VolumeSerialNumber;
        fileId128 = new byte[16];
        BitConverter.GetBytes(info.FileIdLow).CopyTo(fileId128, 0);
        BitConverter.GetBytes(info.FileIdHigh).CopyTo(fileId128, 8);
        return true;
    }

    internal static bool TryGetBasicInformation(SafeFileHandle handle, out FileBasicInformation information)
    {
        var status = NtQueryInformationFile(
            handle,
            out _,
            out information,
            Marshal.SizeOf<FileBasicInformation>(),
            FileBasicInformationClass);
        return status == StatusSuccess;
    }
}

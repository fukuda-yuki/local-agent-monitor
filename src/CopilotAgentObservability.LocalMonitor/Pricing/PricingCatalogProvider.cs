using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CopilotAgentObservability.Pricing;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor;

public interface IPricingCatalogProvider
{
    PricingCatalog Catalog { get; }
    ReadOnlyMemory<byte> CanonicalCatalogBytes { get; }
    string CatalogSha256 { get; }
}

public sealed class DefaultPricingCatalogProvider : IPricingCatalogProvider
{
    private readonly byte[] _canonicalCatalogBytes;

    private DefaultPricingCatalogProvider(PricingCatalog catalog)
    {
        Catalog = catalog;
        _canonicalCatalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        CatalogSha256 = catalog.CatalogSha256;
    }

    public PricingCatalog Catalog { get; }
    public ReadOnlyMemory<byte> CanonicalCatalogBytes => _canonicalCatalogBytes.ToArray();
    public string CatalogSha256 { get; }

    public static DefaultPricingCatalogProvider Create(IReadOnlyList<string> overridePaths)
    {
        ArgumentNullException.ThrowIfNull(overridePaths);

        try
        {
            if (overridePaths.Count > 8)
            {
                throw new PricingCatalogUnavailableException();
            }

            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            if (overridePaths.Distinct(pathComparer).Count() != overridePaths.Count)
            {
                throw new PricingCatalogUnavailableException();
            }

            var overrides = overridePaths
                .Select(path => PricingRegistryLoader.Deserialize(
                    StrictLocalFileReader.ReadUtf8(path)))
                .ToArray();
            var catalog = PricingCatalog.Create(BundledPricingRegistry.Load(), overrides);
            return new DefaultPricingCatalogProvider(catalog);
        }
        catch (PricingCatalogUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PricingCatalogUnavailableException();
        }
    }
}

public sealed class PricingCatalogUnavailableException : Exception
{
    public PricingCatalogUnavailableException()
        : base("pricing_catalog_unavailable")
    {
    }
}

internal static class StrictLocalFileReader
{
    private const int MaximumBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string ReadUtf8(string path)
    {
        ValidateLexicalPath(path);

        using var handle = OperatingSystem.IsWindows()
            ? WindowsNativeFile.Open(path)
            : OperatingSystem.IsLinux()
                ? LinuxNativeFile.Open(path)
                : throw new PricingCatalogUnavailableException();
        var before = NativeFileIdentity.Read(handle);
        if (!before.IsRegularFile || before.Length is < 0 or > MaximumBytes)
        {
            throw new PricingCatalogUnavailableException();
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 16_384, isAsync: false);
        var bytes = new byte[MaximumBytes + 1];
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

        var after = NativeFileIdentity.Read(handle);
        if (count > MaximumBytes
            || count != before.Length
            || before != after
            || stream.ReadByte() != -1)
        {
            throw new PricingCatalogUnavailableException();
        }

        return StrictUtf8.GetString(bytes, 0, count);
    }

    private static void ValidateLexicalPath(string path)
    {
        if (string.IsNullOrEmpty(path)
            || !Path.IsPathFullyQualified(path)
            || path.IndexOf('\0') >= 0)
        {
            throw new PricingCatalogUnavailableException();
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                fullPath,
                path,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new PricingCatalogUnavailableException();
        }

        if (OperatingSystem.IsWindows())
        {
            if (path.Length < 4
                || !char.IsAsciiLetter(path[0])
                || path[1] != ':'
                || path[2] != '\\'
                || path.Contains('/', StringComparison.Ordinal)
                || path.AsSpan(2).Contains(':'))
            {
                throw new PricingCatalogUnavailableException();
            }
        }
        else if (!OperatingSystem.IsLinux()
            || path[0] != '/'
            || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new PricingCatalogUnavailableException();
        }
    }
}

internal readonly record struct NativeFileIdentity(
    ulong Device,
    ulong File,
    long Length,
    long LastWriteSeconds,
    long LastWriteNanoseconds,
    bool IsRegularFile)
{
    internal static NativeFileIdentity Read(SafeFileHandle handle) =>
        OperatingSystem.IsWindows()
            ? WindowsNativeFile.Identity(handle)
            : LinuxNativeFile.Identity(handle);
}

internal static class WindowsNativeFile
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DriveUnknown = 0;
    private const uint DriveNoRootDirectory = 1;
    private const uint DriveRemote = 4;

    internal static SafeFileHandle Open(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var driveType = GetDriveTypeW(root);
        if (driveType is DriveUnknown or DriveNoRootDirectory or DriveRemote)
        {
            throw new PricingCatalogUnavailableException();
        }

        var parent = Path.GetDirectoryName(path)!;
        var relativeParent = parent[root.Length..];
        var current = root.TrimEnd('\\');
        var ancestors = new List<SafeFileHandle>();
        try
        {
            foreach (var segment in relativeParent.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = $"{current}\\{segment}";
                var ancestor = CreateFileW(
                    current,
                    0,
                    ShareRead | ShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                    IntPtr.Zero);
                if (ancestor.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    ancestor.Dispose();
                    throw new Win32Exception(error);
                }

                ancestors.Add(ancestor);
                var ancestorInfo = GetInformation(ancestor);
                if ((ancestorInfo.FileAttributes & FileAttributeDirectory) == 0
                    || (ancestorInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new PricingCatalogUnavailableException();
                }
            }

            var handle = CreateFileW(
                path,
                GenericRead,
                ShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            var info = GetInformation(handle);
            if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
            {
                handle.Dispose();
                throw new PricingCatalogUnavailableException();
            }

            return handle;
        }
        finally
        {
            foreach (var ancestor in ancestors)
            {
                ancestor.Dispose();
            }
        }
    }

    internal static NativeFileIdentity Identity(SafeFileHandle handle)
    {
        var info = GetInformation(handle);
        var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var lastWrite = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
        return new NativeFileIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            length,
            lastWrite,
            0,
            (info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) == 0);
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return information;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal static class LinuxNativeFile
{
    private const int AtFdcwd = -100;
    private const int OReadOnly = 0;
    private const int ONonBlock = 0x800;
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloseOnExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x0001;
    private const uint StatxModifiedTime = 0x0040;
    private const uint StatxInode = 0x0100;
    private const uint StatxSize = 0x0200;
    private const uint StatxBasicStats = 0x07ff;
    private const uint RequiredStatxFields = StatxType | StatxModifiedTime | StatxInode | StatxSize;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;

    internal static SafeFileHandle Open(string path)
    {
        var parent = OpenAt(AtFdcwd, "/", OReadOnly | ODirectory | OCloseOnExec);
        if (parent < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var flags = OReadOnly | ONoFollow | OCloseOnExec | ONonBlock;
                if (index < segments.Length - 1)
                {
                    flags |= ODirectory;
                }

                var next = OpenAt(parent, segments[index], flags);
                if (next < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                Close(parent);
                parent = next;
            }

            var handle = new SafeFileHandle((IntPtr)parent, ownsHandle: true);
            parent = -1;
            return handle;
        }
        finally
        {
            if (parent >= 0)
            {
                Close(parent);
            }
        }
    }

    internal static NativeFileIdentity Identity(SafeFileHandle handle)
    {
        if (StatX(handle, string.Empty, AtEmptyPath, StatxBasicStats, out var status) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if ((status.Mask & RequiredStatxFields) != RequiredStatxFields)
        {
            throw new PricingCatalogUnavailableException();
        }

        return new NativeFileIdentity(
            ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
            status.Inode,
            status.Size,
            status.ModifiedTimeSeconds,
            status.ModifiedTimeNanoseconds,
            (status.Mode & FileTypeMask) == RegularFile);
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(
        SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatX status);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatX
    {
        [FieldOffset(0)]
        internal uint Mask;
        [FieldOffset(28)]
        internal uint Mode;
        [FieldOffset(32)]
        internal ulong Inode;
        [FieldOffset(40)]
        internal long Size;
        [FieldOffset(112)]
        internal long ModifiedTimeSeconds;
        [FieldOffset(120)]
        internal uint ModifiedTimeNanoseconds;
        [FieldOffset(136)]
        internal uint DeviceMajor;
        [FieldOffset(140)]
        internal uint DeviceMinor;
    }
}

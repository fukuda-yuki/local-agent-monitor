using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// The sole Linux native surface for Gate 8 retained-root walking. Compiled on every platform;
// executed only on Linux kernels >= 5.8, where openat2 and STATX_MNT_ID both exist.
internal static class LinuxNativeFileApisV1
{
    internal const long SysOpenat2 = 437;
    internal const int AtFdcwd = -100;
    internal const int AtEmptyPath = 0x1000;

    internal const ulong ResolveNoXdev = 0x01;
    internal const ulong ResolveNoMagiclinks = 0x02;
    internal const ulong ResolveNoSymlinks = 0x04;
    internal const ulong ResolveBeneath = 0x08;
    internal const ulong ResolveAnchor = ResolveNoXdev | ResolveNoMagiclinks | ResolveNoSymlinks;
    internal const ulong ResolveAll = ResolveAnchor | ResolveBeneath;

    internal const ulong ORdOnly = 0;
    internal const ulong ODirectory = 0x10000;
    internal const ulong OCloexec = 0x80000;
    internal const nuint OpenHowSize = 24;

    internal const uint StatxType = 0x0001;
    internal const uint StatxMode = 0x0002;
    internal const uint StatxMtime = 0x0040;
    internal const uint StatxCtime = 0x0080;
    internal const uint StatxIno = 0x0100;
    internal const uint StatxSize = 0x0200;
    internal const uint StatxMntId = 0x1000;

    // STATX_MNT_ID|STATX_INO|STATX_TYPE|STATX_MODE on the retained root and every relevant fd.
    internal const uint IdentityMask = StatxType | StatxMode | StatxIno | StatxMntId;

    // Plus STATX_SIZE|STATX_MTIME|STATX_CTIME wherever those fields classify or enter a
    // stability/read comparison.
    internal const uint ClassifiedFileMask = IdentityMask | StatxSize | StatxMtime | StatxCtime;

    internal const ushort SIfMt = 0xF000;
    internal const ushort SIfDir = 0x4000;
    internal const ushort SIfReg = 0x8000;
    internal const ushort SIfLnk = 0xA000;

    internal const int Eperm = 1;
    internal const int Enoent = 2;
    internal const int Eacces = 13;
    internal const int Exdev = 18;
    internal const int Enotdir = 20;
    internal const int Eisdir = 21;
    internal const int Eloop = 40;
    internal const int Estale = 116;
    internal const int Eintr = 4;

    internal static readonly string[] CertifiedFileSystems = ["ext4", "xfs", "btrfs"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint NumberOfLinks;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
        public ulong MountId;
        public uint DioMemoryAlignment;
        public uint DioOffsetAlignment;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public ulong[] Spare3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UtsName
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] SystemName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] NodeName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] Release;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] Version;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] Machine;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
        public byte[] DomainName;
    }

    // glibc's syscall() wrapper converts a negative raw return into -1 + errno.
    internal delegate long OpenAt2Invoker(long number, int dirfd, IntPtr pathname, IntPtr how, nuint size);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int dirfd, IntPtr pathname, IntPtr how, nuint size);

    [DllImport("libc", SetLastError = true)]
    internal static extern int statx(int dirfd, byte[] pathname, int flags, uint mask, out Statx statxBuffer);

    [DllImport("libc", SetLastError = true)]
    internal static extern nint read(int fd, IntPtr buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    internal static extern int uname(out UtsName name);

    // Shared openat2 marshaling for the opener and the reader. glibc's syscall() wrapper sets
    // errno on a -1 return; the returned fd (when nonnegative) is wrapped owning and cloexec.
    internal static SafeFileHandle? OpenAt2(int parentFd, string path, ulong flags, ulong resolve, out int errno) =>
        OpenAt2(parentFd, path, flags, resolve, syscall, out errno);

    internal static SafeFileHandle? OpenAt2(int parentFd, string path, ulong flags, out int errno) =>
        OpenAt2(parentFd, path, flags, ResolveAll, syscall, out errno);

    internal static SafeFileHandle? OpenAt2(
        int parentFd,
        string path,
        ulong flags,
        ulong resolve,
        OpenAt2Invoker invoke,
        out int errno)
    {
        errno = 0;
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(invoke);

        byte[] pathBytes;
        try
        {
            pathBytes = StrictUtf8.GetBytes(path);
        }
        catch (EncoderFallbackException)
        {
            // Parser-validated segments never reach this; an unencodable segment is a sanitized
            // open failure, not an exception.
            errno = 0;
            return null;
        }

        var pathBuffer = IntPtr.Zero;
        var howBuffer = IntPtr.Zero;

        try
        {
            pathBuffer = Marshal.AllocHGlobal(pathBytes.Length + 1);
            Marshal.Copy(pathBytes, 0, pathBuffer, pathBytes.Length);
            Marshal.WriteByte(pathBuffer, pathBytes.Length, 0);

            var how = new OpenHow
            {
                Flags = flags | OCloexec,
                Mode = 0,
                Resolve = resolve
            };

            howBuffer = Marshal.AllocHGlobal((int)OpenHowSize);
            Marshal.StructureToPtr(how, howBuffer, fDeleteOld: false);

            var fd = invoke(SysOpenat2, parentFd, pathBuffer, howBuffer, OpenHowSize);
            if (fd < 0)
            {
                errno = Marshal.GetLastPInvokeError();
                return null;
            }

            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }
        finally
        {
            if (howBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(howBuffer);
            }

            if (pathBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pathBuffer);
            }
        }
    }

    // Pure errno classifier for segment opens (Gate 8 spec 2255-2261). ENOENT is context
    // dependent (missing under an unchanged re-proved root with no observed identity for the
    // looked-up segment, raced otherwise) and is therefore classified by the caller.
    internal static CurrentSkillNativeOutcomeV1 ClassifyOpenErrno(int errno) => errno switch
    {
        Enotdir or Eloop or Exdev or Eisdir => CurrentSkillNativeOutcomeV1.Unsafe,
        Estale => CurrentSkillNativeOutcomeV1.Raced,
        _ => CurrentSkillNativeOutcomeV1.OtherNativeFailure
    };

    // The Linux platform gate. openat2 and its RESOLVE_BENEATH/NO_SYMLINKS/NO_MAGICLINKS/NO_XDEV
    // flags arrived in 5.6 but the statx mount ID this contract compares through the read is only
    // stable from 5.8, so 5.6 and 5.7 cannot satisfy Gate 8 and are uncertified.
    internal static bool IsSupportedKernel() =>
        uname(out var utsName) == 0
        && IsKernelReleaseAtLeast(ReadNulTerminatedAscii(utsName.Release), 5, 8);

    private static string ReadNulTerminatedAscii(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    internal static bool IsKernelReleaseAtLeast(string release, int requiredMajor, int requiredMinor)
    {
        ArgumentNullException.ThrowIfNull(release);

        var dot = release.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        if (!int.TryParse(release[..dot], out var major))
        {
            return false;
        }

        var rest = release[(dot + 1)..];
        var secondDot = rest.IndexOf('.');
        if (secondDot >= 0)
        {
            rest = rest[..secondDot];
        }

        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            rest = rest[..dash];
        }

        if (!int.TryParse(rest, out var minor))
        {
            return false;
        }

        return major > requiredMajor || (major == requiredMajor && minor >= requiredMinor);
    }

    // Parses /proc/self/mountinfo content for the filesystem type of one mount ID. Field 1 is
    // the mount ID and the filesystem type follows the standalone " - " separator.
    internal static bool TryGetFileSystemForMountId(string mountInfoContent, ulong mountId, out string fileSystemType)
    {
        fileSystemType = string.Empty;
        ArgumentNullException.ThrowIfNull(mountInfoContent);

        foreach (var line in mountInfoContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || !ulong.TryParse(fields[0], out var lineMountId) || lineMountId != mountId)
            {
                continue;
            }

            for (var index = 5; index < fields.Length - 2; index++)
            {
                if (fields[index] == "-")
                {
                    fileSystemType = fields[index + 1];
                    return fileSystemType.Length > 0;
                }
            }

            return false;
        }

        return false;
    }
}

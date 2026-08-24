using CopilotAgentObservability.LocalMonitor.SkillNative;
using System.Runtime.InteropServices;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillNativeClassifierTests
{
    [Theory]
    [InlineData(0xC000000Fu)] // StatusNoSuchFile
    [InlineData(0xC0000034u)] // StatusObjectNameNotFound
    [InlineData(0xC000003Au)] // StatusObjectPathNotFound
    public void ConfirmedNotFoundStatusesAreRecognized(uint ntStatus)
    {
        Assert.True(WindowsNativeFileApisV1.IsConfirmedNotFoundStatus(ntStatus));
    }

    [Theory]
    [InlineData(0x00000000u)] // StatusSuccess
    [InlineData(0xC0000022u)] // StatusAccessDenied
    [InlineData(0xC0000043u)] // StatusSharingViolation
    [InlineData(0xC0000103u)] // StatusNotADirectory
    [InlineData(0xC00000BAu)] // StatusFileIsADirectory
    [InlineData(0x80000006u)] // StatusNoMoreFiles (informational boundary)
    public void OtherStatusesAreNotConfirmedNotFound(uint ntStatus)
    {
        Assert.False(WindowsNativeFileApisV1.IsConfirmedNotFoundStatus(ntStatus));
    }

    [Theory]
    [InlineData(LinuxNativeFileApisV1.Enotdir)]
    [InlineData(LinuxNativeFileApisV1.Eloop)]
    [InlineData(LinuxNativeFileApisV1.Exdev)]
    [InlineData(LinuxNativeFileApisV1.Eisdir)]
    public void StructuralOpenErrorsClassifyAsUnsafe(int errno)
    {
        Assert.Equal(CurrentSkillNativeOutcomeV1.Unsafe, LinuxNativeFileApisV1.ClassifyOpenErrno(errno));
    }

    [Fact]
    public void StaleHandleClassifiesAsRaced()
    {
        Assert.Equal(CurrentSkillNativeOutcomeV1.Raced, LinuxNativeFileApisV1.ClassifyOpenErrno(LinuxNativeFileApisV1.Estale));
    }

    [Theory]
    [InlineData(LinuxNativeFileApisV1.Enoent)]
    [InlineData(LinuxNativeFileApisV1.Eacces)]
    [InlineData(LinuxNativeFileApisV1.Eperm)]
    [InlineData(0)]
    [InlineData(999)]
    public void RemainingOpenErrorsClassifyAsOtherNativeFailure(int errno)
    {
        Assert.Equal(CurrentSkillNativeOutcomeV1.OtherNativeFailure, LinuxNativeFileApisV1.ClassifyOpenErrno(errno));
    }

    [Theory]
    [InlineData("5.8.0", true)]
    [InlineData("5.8", true)]
    [InlineData("5.9.1", true)]
    [InlineData("6.1.0-rc1", true)]
    [InlineData("5.10.16", true)]
    [InlineData("5.8-rc1", true)]
    [InlineData("5.7.99", false)]
    [InlineData("4.18.0-553.el8.x86_64", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(".5", false)]
    [InlineData("5", false)]
    [InlineData("5.x", false)]
    public void KernelReleaseGateMatchesOpenAt2Requirement(string release, bool expected)
    {
        Assert.Equal(expected, LinuxNativeFileApisV1.IsKernelReleaseAtLeast(release, 5, 8));
    }

    private const string MountInfoSample =
        "28 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "36 28 0:30 / /sys/fs/cgroup rw,nosuid,nodev,noexec,relatime shared:14 - cgroup2 cgroup2 rw\n";

    [Fact]
    public void MountInfoLookupReturnsFileSystemForMatchingMountId()
    {
        Assert.True(LinuxNativeFileApisV1.TryGetFileSystemForMountId(MountInfoSample, 28, out var fs));
        Assert.Equal("ext4", fs);

        Assert.True(LinuxNativeFileApisV1.TryGetFileSystemForMountId(MountInfoSample, 36, out fs));
        Assert.Equal("cgroup2", fs);
    }

    [Fact]
    public void MountInfoLookupFailsForUnknownMountId()
    {
        Assert.False(LinuxNativeFileApisV1.TryGetFileSystemForMountId(MountInfoSample, 99, out _));
    }

    [Fact]
    public void MountInfoLookupFailsWhenSeparatorIsMissing()
    {
        const string noSeparator = "28 1 8:1 / / rw,relatime shared:1 ext4 /dev/sda1 rw\n";

        Assert.False(LinuxNativeFileApisV1.TryGetFileSystemForMountId(noSeparator, 28, out _));
    }

    [Fact]
    public void MountInfoLookupSkipsShortLines()
    {
        const string shortLine = "28 1 8:1 / /\n";

        Assert.False(LinuxNativeFileApisV1.TryGetFileSystemForMountId(shortLine, 28, out _));
    }

    [Fact]
    public void CertifiedLinuxFileSystemsAreExactlyTheSpecifiedSet()
    {
        Assert.Equal(["ext4", "xfs", "btrfs"], LinuxNativeFileApisV1.CertifiedFileSystems);
    }

    [Fact]
    public void OpenAt2InvocationBindsAbiAndResolveMasks()
    {
        Assert.Equal(0x01UL, LinuxNativeFileApisV1.ResolveNoXdev);
        Assert.Equal(0x02UL, LinuxNativeFileApisV1.ResolveNoMagiclinks);
        Assert.Equal(0x04UL, LinuxNativeFileApisV1.ResolveNoSymlinks);
        Assert.Equal(0x08UL, LinuxNativeFileApisV1.ResolveBeneath);

        var observations = new List<(long Number, int DirFd, nint Pathname, string? Path, bool IsNullTerminated, nint How,
            nuint Size, LinuxNativeFileApisV1.OpenHow Value)>();
        var nextErrno = 71;
        long Invoke(long number, int dirfd, nint pathname, nint how, nuint size)
        {
            var value = Marshal.PtrToStructure<LinuxNativeFileApisV1.OpenHow>(how);
            var path = Marshal.PtrToStringUTF8(pathname);
            var isNullTerminated = path is not null && Marshal.ReadByte(pathname, Encoding.UTF8.GetByteCount(path)) == 0;
            observations.Add((number, dirfd, pathname, path, isNullTerminated, how, size, value));
            Marshal.SetLastPInvokeError(nextErrno++);
            return -1;
        }

        _ = LinuxNativeFileApisV1.OpenAt2(LinuxNativeFileApisV1.AtFdcwd, "/",
            LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
            LinuxNativeFileApisV1.ResolveAnchor, Invoke, out var anchorErrno);
        _ = LinuxNativeFileApisV1.OpenAt2(7, ".",
            LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
            LinuxNativeFileApisV1.ResolveAll, Invoke, out var descendantErrno);

        Assert.Equal(71, anchorErrno);
        Assert.Equal(72, descendantErrno);

        Assert.Collection(observations,
            anchor =>
            {
                Assert.Equal(437, anchor.Number);
                Assert.Equal(LinuxNativeFileApisV1.AtFdcwd, anchor.DirFd);
                Assert.NotEqual(nint.Zero, anchor.Pathname);
                Assert.Equal("/", anchor.Path);
                Assert.True(anchor.IsNullTerminated);
                Assert.NotEqual(nint.Zero, anchor.How);
                Assert.Equal((nuint)Marshal.SizeOf<LinuxNativeFileApisV1.OpenHow>(), anchor.Size);
                Assert.Equal(LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory |
                    LinuxNativeFileApisV1.OCloexec, anchor.Value.Flags);
                Assert.Equal(0UL, anchor.Value.Mode);
                Assert.Equal(0x07UL, anchor.Value.Resolve);
            },
            descendant =>
            {
                Assert.Equal(437, descendant.Number);
                Assert.Equal(7, descendant.DirFd);
                Assert.NotEqual(nint.Zero, descendant.Pathname);
                Assert.Equal(".", descendant.Path);
                Assert.True(descendant.IsNullTerminated);
                Assert.NotEqual(nint.Zero, descendant.How);
                Assert.Equal((nuint)Marshal.SizeOf<LinuxNativeFileApisV1.OpenHow>(), descendant.Size);
                Assert.Equal(LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory |
                    LinuxNativeFileApisV1.OCloexec, descendant.Value.Flags);
                Assert.Equal(0UL, descendant.Value.Mode);
                Assert.Equal(0x0fUL, descendant.Value.Resolve);
            });
    }

    [LinuxFact]
    public void OpenAt2OpensAbsoluteAnchorAndSafeRelativeDescendant()
    {
        using var anchor = LinuxNativeFileApisV1.OpenAt2(LinuxNativeFileApisV1.AtFdcwd, "/",
            LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
            LinuxNativeFileApisV1.ResolveAnchor, out var anchorErrno);
        Assert.NotNull(anchor);
        Assert.Equal(0, anchorErrno);

        using var descendant = LinuxNativeFileApisV1.OpenAt2(anchor.DangerousGetHandle().ToInt32(), ".",
            LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
            LinuxNativeFileApisV1.ResolveAll, out var descendantErrno);
        Assert.NotNull(descendant);
        Assert.Equal(0, descendantErrno);

        using var rejectedAbsolute = LinuxNativeFileApisV1.OpenAt2(anchor.DangerousGetHandle().ToInt32(), "/",
            LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
            LinuxNativeFileApisV1.ResolveAll, out var rejectedAbsoluteErrno);
        Assert.Null(rejectedAbsolute);
        Assert.Equal(LinuxNativeFileApisV1.Exdev, rejectedAbsoluteErrno);
    }
}

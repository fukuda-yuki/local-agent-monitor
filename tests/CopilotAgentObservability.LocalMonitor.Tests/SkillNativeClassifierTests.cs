using CopilotAgentObservability.LocalMonitor.SkillNative;

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
}

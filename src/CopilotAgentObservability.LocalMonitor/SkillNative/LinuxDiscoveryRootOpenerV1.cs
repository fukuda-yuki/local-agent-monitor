using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Linux Gate 8 root preflight: kernel >= 5.8 gate, no-follow openat2 from AT_FDCWD with one
// relative segment per open, statx identity-mask proof on every fd, the closed ext4/xfs/btrfs
// classification of the retained fd's mount ID through /proc/self/mountinfo, and retention of
// the final fd with the identity captured at retention. Every failure collapses to
// skill_discovery_root_configuration_invalid at the composition layer; this type never emits a
// root value or native fact itself.
internal sealed class LinuxDiscoveryRootOpenerV1 : IDiscoveryRootOpenerV1
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
    {
        ArgumentNullException.ThrowIfNull(configuredRootPath);

        if (!SkillProducerPathKeyV1.TryParse(
                configuredRootPath,
                SkillProducerPathKeyPlatform.Linux,
                out var pathKey,
                out _))
        {
            return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.InvalidSyntax);
        }

        if (LinuxNativeFileApisV1.uname(out var utsName) != 0 ||
            !LinuxNativeFileApisV1.IsKernelReleaseAtLeast(ReadNulTerminatedAscii(utsName.Release), 5, 8))
        {
            return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.KernelUnsupported);
        }

        var openedFds = new List<SafeFileHandle>(pathKey.Segments.Count + 1);
        try
        {
            var anchor = LinuxNativeFileApisV1.OpenAt2(
                LinuxNativeFileApisV1.AtFdcwd,
                "/",
                LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
                out var anchorErrno);

            if (anchor is null)
            {
                return DiscoveryRootOpenResultV1.Failed(ClassifyOpenFailure(anchorErrno));
            }

            openedFds.Add(anchor);

            if (!TryProveDirectoryFd(anchor, expectedMountId: null, out var anchorMountId, out var anchorFailure))
            {
                return DiscoveryRootOpenResultV1.Failed(anchorFailure);
            }

            var parent = anchor;
            foreach (var segment in pathKey.Segments)
            {
                var child = LinuxNativeFileApisV1.OpenAt2(
                    parent.DangerousGetHandle().ToInt32(),
                    segment,
                    LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.ODirectory,
                    out var errno);

                if (child is null)
                {
                    return DiscoveryRootOpenResultV1.Failed(ClassifyOpenFailure(errno));
                }

                openedFds.Add(child);

                if (!TryProveDirectoryFd(child, anchorMountId, out _, out var segmentFailure))
                {
                    return DiscoveryRootOpenResultV1.Failed(segmentFailure);
                }

                parent = child;
            }

            var retained = openedFds[^1];
            var retainedFd = retained.DangerousGetHandle().ToInt32();
            if (LinuxNativeFileApisV1.statx(
                    retainedFd,
                    [0],
                    LinuxNativeFileApisV1.AtEmptyPath,
                    LinuxNativeFileApisV1.IdentityMask,
                    out var retainedStat) != 0 ||
                !HasMask(retainedStat.Mask, LinuxNativeFileApisV1.IdentityMask))
            {
                return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.StatxMaskIncomplete);
            }

            if (!TryGetMountInfoContent(out var mountInfoContent) ||
                !LinuxNativeFileApisV1.TryGetFileSystemForMountId(mountInfoContent, retainedStat.MountId, out var fileSystemType) ||
                !LinuxNativeFileApisV1.CertifiedFileSystems.Contains(fileSystemType, StringComparer.Ordinal))
            {
                return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.FilesystemNotCertified);
            }

            // The retained fd transfers out of the disposal list; intermediate fds are released
            // below in reverse acquisition order.
            openedFds.RemoveAt(openedFds.Count - 1);

            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateLinux(
                    retainedStat.MountId,
                    retainedStat.DevMajor,
                    retainedStat.DevMinor,
                    retainedStat.Inode),
                retained));
        }
        finally
        {
            for (var index = openedFds.Count - 1; index >= 0; index--)
            {
                openedFds[index].Dispose();
            }
        }
    }

    public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root.PathKey.Platform != SkillProducerPathKeyPlatform.Linux || root.IsDisposed)
        {
            return false;
        }

        try
        {
            var fd = root.Handle.DangerousGetHandle().ToInt32();
            if (LinuxNativeFileApisV1.statx(fd, [0], LinuxNativeFileApisV1.AtEmptyPath, LinuxNativeFileApisV1.IdentityMask, out var statx) != 0 ||
                !HasMask(statx.Mask, LinuxNativeFileApisV1.IdentityMask))
            {
                return false;
            }

            var current = DiscoveryRootNativeIdentityV1.CreateLinux(statx.MountId, statx.DevMajor, statx.DevMinor, statx.Inode).ToByteArray();
            return current.AsSpan().SequenceEqual(root.NativeIdentity.ToByteArray());
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // A mount crossing during preflight is a non-local configured root; a symlink component is
    // the Linux equivalent of a reparse root; everything else leaves the root unopenable.
    private static DiscoveryRootOpenFailureV1 ClassifyOpenFailure(int errno) => errno switch
    {
        LinuxNativeFileApisV1.Exdev => DiscoveryRootOpenFailureV1.NotLocal,
        LinuxNativeFileApisV1.Eloop => DiscoveryRootOpenFailureV1.ReparseRoot,
        LinuxNativeFileApisV1.Enotdir => DiscoveryRootOpenFailureV1.NotADirectory,
        _ => DiscoveryRootOpenFailureV1.Unopenable
    };

    private static bool TryProveDirectoryFd(
        SafeFileHandle fd,
        ulong? expectedMountId,
        out ulong mountId,
        out DiscoveryRootOpenFailureV1 failure)
    {
        mountId = 0;
        failure = DiscoveryRootOpenFailureV1.Other;

        var rawFd = fd.DangerousGetHandle().ToInt32();
        if (LinuxNativeFileApisV1.statx(rawFd, [0], LinuxNativeFileApisV1.AtEmptyPath, LinuxNativeFileApisV1.IdentityMask, out var statx) != 0 ||
            !HasMask(statx.Mask, LinuxNativeFileApisV1.IdentityMask))
        {
            failure = DiscoveryRootOpenFailureV1.StatxMaskIncomplete;
            return false;
        }

        mountId = statx.MountId;

        if (expectedMountId.HasValue && statx.MountId != expectedMountId.Value)
        {
            failure = DiscoveryRootOpenFailureV1.NotLocal;
            return false;
        }

        if ((ushort)(statx.Mode & LinuxNativeFileApisV1.SIfMt) != LinuxNativeFileApisV1.SIfDir)
        {
            failure = DiscoveryRootOpenFailureV1.NotADirectory;
            return false;
        }

        return true;
    }

    private static bool HasMask(uint returnedMask, uint requiredMask) =>
        (returnedMask & requiredMask) == requiredMask;

    private static bool TryGetMountInfoContent(out string content)
    {
        content = string.Empty;

        try
        {
            content = File.ReadAllText(MountInfoPath);
            return content.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadNulTerminatedAscii(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}

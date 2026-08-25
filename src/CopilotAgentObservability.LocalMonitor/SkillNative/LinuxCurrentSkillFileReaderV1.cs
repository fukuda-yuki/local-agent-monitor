using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Gate 8 retained-root every-segment walker for Linux: kernel >= 5.8 openat2 per segment with
// RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_MAGICLINKS|RESOLVE_NO_XDEV off the retained
// parent fd, statx returned-mask proof on the root and every fd, a bounded same-fd read, and
// repeated mask/identity/metadata/root proofs before any classification. Compiled on every
// platform; executed only on certified Linux hosts. Descendant fds are disposed in reverse
// acquisition order on every arm; the retained root fd belongs to the process generation.
internal sealed class LinuxCurrentSkillFileReaderV1 : ICurrentSkillNativeFileReaderV1
{
    internal const int MaximumBodyBytes = 1_048_576;
    internal const int MaximumReadBytes = MaximumBodyBytes + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<DateTimeOffset> readClock;
    private readonly CurrentSkillFileReaderHooksV1? hooks;

    public LinuxCurrentSkillFileReaderV1(Func<DateTimeOffset>? readClock = null, CurrentSkillFileReaderHooksV1? hooks = null)
    {
        this.readClock = readClock ?? (() => DateTimeOffset.UtcNow);
        this.hooks = hooks;
    }

    public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.RetainedRoot.PathKey.Platform != SkillProducerPathKeyPlatform.Linux)
        {
            throw new ArgumentException("The Linux reader requires a Linux-platform retained root.", nameof(target));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var root = target.RetainedRoot;
        var rootFd = root.Handle.DangerousGetHandle().ToInt32();
        var openedFds = new List<SafeFileHandle>(target.RelativeSegments.Count);
        var pendingReadBuffer = new byte[MaximumReadBytes];

        try
        {
            if (!TryStat(rootFd, LinuxNativeFileApisV1.IdentityMask, out var rootStat) ||
                !HasMask(rootStat.Mask, LinuxNativeFileApisV1.IdentityMask) ||
                !IdentityMatches(rootStat, root.NativeIdentity))
            {
                // The retained identity was observed at retention; an unreprovable root is a
                // disappearance after observation, hence raced.
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            var parentFd = rootFd;
            FinalSegmentMetadata? finalMetadata = null;

            for (var index = 0; index < target.RelativeSegments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isFinal = index == target.RelativeSegments.Count - 1;
                var flags = LinuxNativeFileApisV1.ORdOnly | LinuxNativeFileApisV1.OCloexec;
                if (!isFinal)
                {
                    flags |= LinuxNativeFileApisV1.ODirectory;
                }

                var child = LinuxNativeFileApisV1.OpenAt2(parentFd, target.RelativeSegments[index], flags, out var errno);
                if (child is null)
                {
                    if (errno == LinuxNativeFileApisV1.Enoent)
                    {
                        // No identity was observed for the looked-up segment; confirmed missing
                        // only while the retained root re-proves unchanged, otherwise raced.
                        return TryStat(rootFd, LinuxNativeFileApisV1.IdentityMask, out var reproof)
                               && HasMask(reproof.Mask, LinuxNativeFileApisV1.IdentityMask)
                               && IdentityMatches(reproof, root.NativeIdentity)
                            ? CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing)
                            : CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
                    }

                    return CurrentSkillNativeReadResultV1.Failure(LinuxNativeFileApisV1.ClassifyOpenErrno(errno));
                }

                NotifyOpened(child);
                openedFds.Add(child);

                var childFd = child.DangerousGetHandle().ToInt32();
                if (!TryStat(childFd, LinuxNativeFileApisV1.IdentityMask, out var childStat) ||
                    !HasMask(childStat.Mask, LinuxNativeFileApisV1.IdentityMask))
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                if (childStat.MountId != rootStat.MountId)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                var fileType = (ushort)(childStat.Mode & LinuxNativeFileApisV1.SIfMt);
                if (fileType == LinuxNativeFileApisV1.SIfLnk)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                if (!isFinal)
                {
                    if (fileType != LinuxNativeFileApisV1.SIfDir)
                    {
                        return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                    }

                    parentFd = childFd;
                    continue;
                }

                if (fileType != LinuxNativeFileApisV1.SIfReg)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                if (!TryStat(childFd, LinuxNativeFileApisV1.ClassifiedFileMask, out var classified) ||
                    !HasMask(classified.Mask, LinuxNativeFileApisV1.ClassifiedFileMask))
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                finalMetadata = new FinalSegmentMetadata(
                    classified.MountId,
                    classified.DevMajor,
                    classified.DevMinor,
                    classified.Inode,
                    classified.Mode,
                    classified.Size,
                    classified.ModificationTime.Seconds,
                    classified.ModificationTime.Nanoseconds,
                    classified.ChangeTime.Seconds,
                    classified.ChangeTime.Nanoseconds);

                parentFd = childFd;
            }

            var finalFd = openedFds[^1].DangerousGetHandle().ToInt32();
            hooks?.AfterFinalMetadataCaptured?.Invoke(openedFds[^1]);
            cancellationToken.ThrowIfCancellationRequested();

            var (readTotal, readFailed) = ReadBounded(finalFd, pendingReadBuffer, cancellationToken);
            if (readFailed)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
            }

            hooks?.AfterReadCompleted?.Invoke(openedFds[^1]);
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = finalMetadata!.Value;
            if (!TryStat(finalFd, LinuxNativeFileApisV1.ClassifiedFileMask, out var reproofStat) ||
                !HasMask(reproofStat.Mask, LinuxNativeFileApisV1.ClassifiedFileMask) ||
                reproofStat.MountId != metadata.MountId ||
                reproofStat.DevMajor != metadata.DevMajor ||
                reproofStat.DevMinor != metadata.DevMinor ||
                reproofStat.Inode != metadata.Inode ||
                reproofStat.Mode != metadata.Mode ||
                reproofStat.Size != metadata.Size ||
                reproofStat.ModificationTime.Seconds != metadata.MtimeSeconds ||
                reproofStat.ModificationTime.Nanoseconds != metadata.MtimeNanoseconds ||
                reproofStat.ChangeTime.Seconds != metadata.CtimeSeconds ||
                reproofStat.ChangeTime.Nanoseconds != metadata.CtimeNanoseconds)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            if (!TryStat(rootFd, LinuxNativeFileApisV1.IdentityMask, out var postReadRoot) ||
                !HasMask(postReadRoot.Mask, LinuxNativeFileApisV1.IdentityMask) ||
                !IdentityMatches(postReadRoot, root.NativeIdentity))
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (readTotal > MaximumBodyBytes)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Oversized);
            }

            var body = new byte[readTotal];
            Buffer.BlockCopy(pendingReadBuffer, 0, body, 0, readTotal);

            try
            {
                _ = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Binary);
            }

            return CurrentSkillNativeReadResultV1.Success(body, SHA256.HashData(body), readClock());
        }
        finally
        {
            for (var index = openedFds.Count - 1; index >= 0; index--)
            {
                var fd = openedFds[index];
                openedFds[index] = null!;
                NotifyClosed(fd);
                fd.Dispose();
            }
        }
    }

    private static bool TryStat(int fd, uint mask, out LinuxNativeFileApisV1.Statx statx)
    {
        // AT_EMPTY_PATH stats the fd itself, never a path.
        return LinuxNativeFileApisV1.statx(fd, [0], LinuxNativeFileApisV1.AtEmptyPath, mask, out statx) == 0;
    }

    private static bool HasMask(uint returnedMask, uint requiredMask) =>
        (returnedMask & requiredMask) == requiredMask;

    private static bool IdentityMatches(LinuxNativeFileApisV1.Statx statx, DiscoveryRootNativeIdentityV1 expected)
    {
        var current = DiscoveryRootNativeIdentityV1.CreateLinux(
            statx.MountId,
            statx.DevMajor,
            statx.DevMinor,
            statx.Inode);
        return current.ToByteArray().AsSpan().SequenceEqual(expected.ToByteArray());
    }

    private static (int TotalBytes, bool Failed) ReadBounded(
        int fd,
        byte[] pendingReadBuffer,
        CancellationToken cancellationToken)
    {
        var bufferHandle = GCHandle.Alloc(pendingReadBuffer, GCHandleType.Pinned);
        try
        {
            var offset = 0;
            while (offset < MaximumReadBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = Math.Min(65_536, MaximumReadBytes - offset);
                var destination = bufferHandle.AddrOfPinnedObject() + offset;
                var result = LinuxNativeFileApisV1.read(fd, destination, (nuint)chunk);

                if (result < 0)
                {
                    if (Marshal.GetLastPInvokeError() == LinuxNativeFileApisV1.Eintr)
                    {
                        continue;
                    }

                    return (0, true);
                }

                if (result == 0)
                {
                    break;
                }

                offset += (int)result;
            }

            return (offset, false);
        }
        finally
        {
            bufferHandle.Free();
        }
    }

    private void NotifyOpened(SafeFileHandle handle)
    {
        if (hooks?.HandleOpened is not null)
        {
            hooks.HandleOpened(handle.DangerousGetHandle());
        }
    }

    private void NotifyClosed(SafeFileHandle handle)
    {
        if (hooks?.HandleClosed is not null)
        {
            hooks.HandleClosed(handle.DangerousGetHandle());
        }
    }

    private readonly record struct FinalSegmentMetadata(
        ulong MountId,
        uint DevMajor,
        uint DevMinor,
        ulong Inode,
        ushort Mode,
        ulong Size,
        long MtimeSeconds,
        uint MtimeNanoseconds,
        long CtimeSeconds,
        uint CtimeNanoseconds);
}

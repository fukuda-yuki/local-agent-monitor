using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Gate 8 retained-root every-segment walker for Windows. One relative NtCreateFile per segment
// beneath retained ancestor handles, FILE_OPEN_REPARSE_POINT on every open, identity proof on
// every segment, two bounded same-handle reads, and repeated identity/metadata/root proofs before
// any classification. Descendant handles are noninheritable and disposed in reverse acquisition
// order on every arm; the retained root handle belongs to the process generation and is never
// disposed here.
internal sealed class WindowsCurrentSkillFileReaderV1 : ICurrentSkillNativeFileReaderV1
{
    internal const int MaximumBodyBytes = 1_048_576;
    internal const int MaximumReadBytes = MaximumBodyBytes + 1;

    private const uint FileTypeDisk = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<DateTimeOffset> readClock;
    private readonly CurrentSkillFileReaderHooksV1? hooks;

    public WindowsCurrentSkillFileReaderV1(Func<DateTimeOffset>? readClock = null, CurrentSkillFileReaderHooksV1? hooks = null)
    {
        this.readClock = readClock ?? (() => DateTimeOffset.UtcNow);
        this.hooks = hooks;
    }

    public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.RetainedRoot.PathKey.Platform != SkillProducerPathKeyPlatform.Windows)
        {
            throw new ArgumentException("The Windows reader requires a Windows-platform retained root.", nameof(target));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var root = target.RetainedRoot;
        var openedHandles = new List<SafeFileHandle>(target.RelativeSegments.Count);
        var pendingReadBuffer = new byte[MaximumReadBytes];

        try
        {
            if (!TryProveRootIdentity(root, out var rootVolumeSerial, out var rootIdentity))
            {
                // The retained identity was observed at retention; an unreprovable root is a
                // disappearance after observation, hence raced, never a request-visible error.
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            var finalMetadata = default(FinalSegmentMetadata?);
            var parent = root.Handle;

            for (var index = 0; index < target.RelativeSegments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isFinal = index == target.RelativeSegments.Count - 1;
                var status = WindowsNativeFileApisV1.OpenRelative(
                    parent,
                    target.RelativeSegments[index],
                    isFinal ? WindowsNativeFileApisV1.FileOpenAccess : WindowsNativeFileApisV1.DirectoryOpenAccess,
                    isFinal ? WindowsNativeFileApisV1.FileCreateOptions : WindowsNativeFileApisV1.DirectoryCreateOptions,
                    out var child);

                if (status != WindowsNativeFileApisV1.StatusSuccess)
                {
                    if (WindowsNativeFileApisV1.IsConfirmedNotFoundStatus(status))
                    {
                        // No identity was observed for the looked-up segment, so not-found is
                        // confirmed missing only while the retained root still re-proves
                        // unchanged; a root that moved underneath makes the lookup raced.
                        return TryProveRootIdentity(root, out var missingReproofVolume, out var missingReproofIdentity)
                               && missingReproofVolume == rootVolumeSerial
                               && missingReproofIdentity.SequenceEqual(rootIdentity)
                            ? CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Missing)
                            : CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
                    }

                    if (status is WindowsNativeFileApisV1.StatusNotADirectory
                        or WindowsNativeFileApisV1.StatusFileIsADirectory)
                    {
                        return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                    }

                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                NotifyOpened(child);
                openedHandles.Add(child);

                if (!WindowsNativeFileApisV1.TryGetIdentity(child, out var volumeSerial, out var fileId128))
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                // A first-capture volume disagreement under the retained root is mount crossing,
                // a closed policy violation, not a race between proofs of the same object.
                if (volumeSerial != rootVolumeSerial)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                if (!WindowsNativeFileApisV1.TryGetBasicInformation(child, out var basic))
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                if ((basic.FileAttributes & WindowsNativeFileApisV1.AttributeReparsePoint) != 0)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                if (!isFinal)
                {
                    if ((basic.FileAttributes & WindowsNativeFileApisV1.AttributeDirectory) == 0)
                    {
                        return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                    }

                    parent = child;
                    continue;
                }

                if ((basic.FileAttributes & WindowsNativeFileApisV1.AttributeDirectory) != 0 ||
                    WindowsNativeFileApisV1.GetFileType(child) != FileTypeDisk)
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Unsafe);
                }

                if (!TryGetFileSize(child, out var fileSize))
                {
                    return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
                }

                finalMetadata = new FinalSegmentMetadata(
                    volumeSerial,
                    fileId128,
                    basic.FileAttributes,
                    basic.LastWriteTime,
                    basic.ChangeTime,
                    fileSize);
            }

            var finalHandle = openedHandles[^1];
            hooks?.AfterFinalMetadataCaptured?.Invoke(finalHandle);
            cancellationToken.ThrowIfCancellationRequested();

            var (readTotal, readFailed) = ReadBounded(finalHandle, pendingReadBuffer, cancellationToken);
            if (readFailed)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
            }

            hooks?.AfterReadCompleted?.Invoke(finalHandle);
            cancellationToken.ThrowIfCancellationRequested();

            var reproofReadBuffer = new byte[MaximumReadBytes];
            var (reproofReadTotal, reproofReadFailed) = ReadBounded(finalHandle, reproofReadBuffer, cancellationToken);
            if (reproofReadFailed)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.OtherNativeFailure);
            }

            if (reproofReadTotal != readTotal ||
                !reproofReadBuffer.AsSpan(0, reproofReadTotal).SequenceEqual(pendingReadBuffer.AsSpan(0, readTotal)))
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            var metadata = finalMetadata!.Value;
            if (!TryGetFileSize(finalHandle, out var reproofSize) ||
                !WindowsNativeFileApisV1.TryGetIdentity(finalHandle, out var reproofVolume, out var reproofFileId) ||
                !WindowsNativeFileApisV1.TryGetBasicInformation(finalHandle, out var reproofBasic) ||
                reproofSize != metadata.FileSize ||
                reproofVolume != metadata.VolumeSerial ||
                !reproofFileId.AsSpan().SequenceEqual(metadata.FileId128) ||
                reproofBasic.FileAttributes != metadata.FileAttributes ||
                reproofBasic.LastWriteTime != metadata.LastWriteTime ||
                reproofBasic.ChangeTime != metadata.ChangeTime)
            {
                return CurrentSkillNativeReadResultV1.Failure(CurrentSkillNativeOutcomeV1.Raced);
            }

            if (!TryProveRootIdentity(root, out var postReadRootVolume, out var postReadRootIdentity) ||
                postReadRootVolume != rootVolumeSerial ||
                !postReadRootIdentity.SequenceEqual(rootIdentity))
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
            for (var index = openedHandles.Count - 1; index >= 0; index--)
            {
                var handle = openedHandles[index];
                openedHandles[index] = null!;
                NotifyClosed(handle);
                handle.Dispose();
            }
        }
    }

    private static bool TryProveRootIdentity(
        RetainedDiscoveryRootV1 root,
        out ulong volumeSerial,
        out byte[] rootIdentity)
    {
        volumeSerial = 0;
        rootIdentity = [];

        try
        {
            if (root.IsDisposed ||
                !WindowsNativeFileApisV1.TryGetIdentity(root.Handle, out volumeSerial, out var fileId128))
            {
                return false;
            }

            rootIdentity = DiscoveryRootNativeIdentityV1.CreateWindows(volumeSerial, fileId128).ToByteArray();
            return rootIdentity.AsSpan().SequenceEqual(root.NativeIdentity.ToByteArray());
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool TryGetFileSize(SafeFileHandle handle, out long fileSize)
    {
        fileSize = 0;
        var status = WindowsNativeFileApisV1.NtQueryStandardInformationFile(
            handle,
            out _,
            out WindowsNativeFileApisV1.FileStandardInformation standard,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsNativeFileApisV1.FileStandardInformation>(),
            WindowsNativeFileApisV1.FileStandardInformationClass);

        if (status != WindowsNativeFileApisV1.StatusSuccess)
        {
            return false;
        }

        fileSize = standard.EndOfFile;
        return true;
    }

    private static (int TotalBytes, bool Failed) ReadBounded(
        SafeFileHandle handle,
        byte[] pendingReadBuffer,
        CancellationToken cancellationToken)
    {
        if (!WindowsNativeFileApisV1.SetFilePointerEx(
                handle,
                0,
                out _,
                WindowsNativeFileApisV1.FileBegin))
        {
            return (0, true);
        }

        var bufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(pendingReadBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var offset = 0;
            while (offset < MaximumReadBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = Math.Min(65_536, MaximumReadBytes - offset);
                var destination = bufferHandle.AddrOfPinnedObject() + offset;
                if (!WindowsNativeFileApisV1.ReadFile(handle, destination, (uint)chunk, out var bytesRead, IntPtr.Zero))
                {
                    return (0, true);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                offset += (int)bytesRead;
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
        ulong VolumeSerial,
        byte[] FileId128,
        uint FileAttributes,
        long LastWriteTime,
        long ChangeTime,
        long FileSize);
}

// Test seams for the Gate 8 native matrix: race injection between the required proofs and
// handle-acquisition observability for the reverse-disposal arm. Null delegates leave production
// behavior untouched. Shared by the Windows and Linux readers.
internal sealed class CurrentSkillFileReaderHooksV1
{
    public Action<SafeFileHandle>? AfterFinalMetadataCaptured { get; init; }

    public Action<SafeFileHandle>? AfterReadCompleted { get; init; }

    public Action<IntPtr>? HandleOpened { get; init; }

    public Action<IntPtr>? HandleClosed { get; init; }
}

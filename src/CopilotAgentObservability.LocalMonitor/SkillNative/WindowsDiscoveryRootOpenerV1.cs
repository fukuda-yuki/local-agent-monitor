using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Windows Gate 8 root preflight: parse with the sole producer path parser, open no-follow from
// the drive anchor with one relative NtCreateFile per segment, classify the closed local
// filesystem allowlist, and retain the final handle with the identity captured at retention.
// Every failure collapses to skill_discovery_root_configuration_invalid at the composition
// layer; this type never emits a root value or native fact itself.
internal sealed class WindowsDiscoveryRootOpenerV1 : IDiscoveryRootOpenerV1
{
    private static readonly string[] CertifiedFileSystems = ["NTFS", "ReFS"];

    public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
    {
        ArgumentNullException.ThrowIfNull(configuredRootPath);

        if (!SkillProducerPathKeyV1.TryParse(
                configuredRootPath,
                SkillProducerPathKeyPlatform.Windows,
                out var pathKey,
                out _))
        {
            return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.InvalidSyntax);
        }

        var driveRoot = $"{char.ToUpperInvariant(pathKey.DriveLetter)}:\\";
        if (WindowsNativeFileApisV1.GetDriveTypeW(driveRoot) != WindowsNativeFileApisV1.DriveFixed)
        {
            return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.NotLocal);
        }

        var openedHandles = new List<SafeFileHandle>(pathKey.Segments.Count + 1);
        try
        {
            // The trailing backslash matters: "\??\C:" resolves to the volume device object,
            // while "\??\C:\" parses the remaining "\" and opens the root directory file object,
            // which is the only shape that supports identity queries and relative opens beneath it.
            var anchorStatus = WindowsNativeFileApisV1.OpenUnder(
                IntPtr.Zero,
                $@"\??\{char.ToUpperInvariant(pathKey.DriveLetter)}:\",
                WindowsNativeFileApisV1.DirectoryOpenAccess,
                WindowsNativeFileApisV1.DirectoryCreateOptions,
                out var anchorHandle);

            if (anchorStatus != WindowsNativeFileApisV1.StatusSuccess)
            {
                return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.Unopenable);
            }

            openedHandles.Add(anchorHandle);

            if (!TryClassifyDirectoryHandle(anchorHandle, out var anchorVolumeSerial, out var failure))
            {
                return DiscoveryRootOpenResultV1.Failed(failure);
            }

            var parent = anchorHandle;
            foreach (var segment in pathKey.Segments)
            {
                var status = WindowsNativeFileApisV1.OpenRelative(
                    parent,
                    segment,
                    WindowsNativeFileApisV1.DirectoryOpenAccess,
                    WindowsNativeFileApisV1.DirectoryCreateOptions,
                    out var child);

                if (status != WindowsNativeFileApisV1.StatusSuccess)
                {
                    return DiscoveryRootOpenResultV1.Failed(
                        status is WindowsNativeFileApisV1.StatusNotADirectory or WindowsNativeFileApisV1.StatusFileIsADirectory
                            ? DiscoveryRootOpenFailureV1.NotADirectory
                            : DiscoveryRootOpenFailureV1.Unopenable);
                }

                openedHandles.Add(child);

                if (!WindowsNativeFileApisV1.TryGetIdentity(child, out var volumeSerial, out _))
                {
                    return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.Unopenable);
                }

                if (volumeSerial != anchorVolumeSerial)
                {
                    return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.NotLocal);
                }

                if (!TryClassifyDirectoryHandle(child, out _, out var segmentFailure))
                {
                    return DiscoveryRootOpenResultV1.Failed(segmentFailure);
                }

                parent = child;
            }

            var retained = openedHandles[^1];
            if (!WindowsNativeFileApisV1.TryGetIdentity(retained, out var retainedVolumeSerial, out var fileId128) ||
                !TryGetFileSystemName(retained, out var fileSystemName) ||
                !CertifiedFileSystems.Contains(fileSystemName, StringComparer.OrdinalIgnoreCase))
            {
                return DiscoveryRootOpenResultV1.Failed(DiscoveryRootOpenFailureV1.FilesystemNotCertified);
            }

            // The retained handle transfers out of the disposal list; intermediate handles are
            // released below in reverse acquisition order.
            openedHandles.RemoveAt(openedHandles.Count - 1);

            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateWindows(retainedVolumeSerial, fileId128),
                retained));
        }
        finally
        {
            for (var index = openedHandles.Count - 1; index >= 0; index--)
            {
                openedHandles[index].Dispose();
            }
        }
    }

    public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root.PathKey.Platform != SkillProducerPathKeyPlatform.Windows || root.IsDisposed)
        {
            return false;
        }

        try
        {
            if (!WindowsNativeFileApisV1.TryGetIdentity(root.Handle, out var volumeSerial, out var fileId128))
            {
                return false;
            }

            var current = DiscoveryRootNativeIdentityV1.CreateWindows(volumeSerial, fileId128).ToByteArray();
            return current.AsSpan().SequenceEqual(root.NativeIdentity.ToByteArray());
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool TryClassifyDirectoryHandle(
        SafeFileHandle handle,
        out ulong volumeSerial,
        out DiscoveryRootOpenFailureV1 failure)
    {
        volumeSerial = 0;
        failure = DiscoveryRootOpenFailureV1.Other;

        if (!WindowsNativeFileApisV1.TryGetIdentity(handle, out volumeSerial, out _))
        {
            failure = DiscoveryRootOpenFailureV1.Unopenable;
            return false;
        }

        if (!WindowsNativeFileApisV1.TryGetBasicInformation(handle, out var basic))
        {
            failure = DiscoveryRootOpenFailureV1.Unopenable;
            return false;
        }

        if ((basic.FileAttributes & WindowsNativeFileApisV1.AttributeReparsePoint) != 0)
        {
            failure = DiscoveryRootOpenFailureV1.ReparseRoot;
            return false;
        }

        if ((basic.FileAttributes & WindowsNativeFileApisV1.AttributeDirectory) == 0)
        {
            failure = DiscoveryRootOpenFailureV1.NotADirectory;
            return false;
        }

        return true;
    }

    private static bool TryGetFileSystemName(SafeFileHandle handle, out string fileSystemName)
    {
        fileSystemName = string.Empty;
        var nameBuffer = new char[256];

        if (!WindowsNativeFileApisV1.GetVolumeInformationByHandleW(
                handle,
                null,
                0,
                out _,
                out _,
                out _,
                nameBuffer,
                (uint)nameBuffer.Length))
        {
            return false;
        }

        var end = Array.IndexOf(nameBuffer, '\0');
        fileSystemName = new string(nameBuffer, 0, end < 0 ? nameBuffer.Length : end);
        return fileSystemName.Length > 0;
    }
}

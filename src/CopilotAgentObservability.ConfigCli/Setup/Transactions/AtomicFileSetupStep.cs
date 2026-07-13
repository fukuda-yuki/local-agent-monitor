using System.Buffers.Binary;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed record AtomicFileApplyResult(string PreviousHash, string AppliedHash, bool Changed);

internal sealed record AtomicFileRestoreResult(string RestoredHash);

internal sealed class AtomicFileCapture(bool exists, byte[] bytes)
{
    private readonly byte[] bytes = bytes.ToArray();

    public string Hash { get; } = SetupHash.File(exists, bytes);

    internal bool Exists { get; } = exists;

    internal byte[] CopyBytes() => bytes.ToArray();
}

internal sealed class AtomicFileSetupStep(ISetupPlatform platform)
{
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("CAOSETUP1");

    public AtomicFileCapture Capture(string allowedRoot, string targetPath)
    {
        var target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
        var current = ReadTarget(target);
        return new AtomicFileCapture(current.Exists, current.Bytes);
    }

    public void CreateBackup(string backupPath, AtomicFileCapture capture)
    {
        try
        {
            platform.FileSystem.WriteNewAllBytes(backupPath, SerializeBackup(capture.Exists, capture.CopyBytes()));
            platform.FileSystem.FlushFile(backupPath);
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }
    }

    public void CreateOrValidateBackup(string backupPath, AtomicFileCapture expected)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(expected);
            var expectedBytes = SerializeBackup(expected.Exists, expected.CopyBytes());
            var metadata = platform.FileSystem.GetPathMetadata(backupPath);
            if (metadata.Exists)
            {
                ValidateExistingBackup(backupPath, expectedBytes, metadata);
                return;
            }

            if (!platform.FileSystem.TryWriteNewAllBytesAndFlush(backupPath, expectedBytes))
            {
                ValidateExistingBackup(backupPath, expectedBytes);
                return;
            }

            ValidateExistingBackup(backupPath, expectedBytes);
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }
    }

    public AtomicFileApplyResult Apply(
        string allowedRoot,
        string targetPath,
        string expectedBaseHash,
        ReadOnlySpan<byte> desiredBytes)
    {
        var target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
        var current = ReadTarget(target);
        var previousHash = SetupHash.File(current.Exists, current.Bytes);
        if (!string.Equals(previousHash, expectedBaseHash, StringComparison.Ordinal))
        {
            throw new SetupFileStepException(SetupCodes.StalePlan);
        }

        var desired = desiredBytes.ToArray();
        var appliedHash = SetupHash.File(true, desired);
        if (string.Equals(previousHash, appliedHash, StringComparison.Ordinal))
        {
            return new AtomicFileApplyResult(previousHash, appliedHash, false);
        }

        var temporary = CreateTemporaryPath(target);
        try
        {
            platform.FileSystem.WriteNewAllBytes(temporary, desired);
            platform.FileSystem.FlushFile(temporary);
            target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
            current = ReadTarget(target);
            if (!string.Equals(SetupHash.File(current.Exists, current.Bytes), expectedBaseHash, StringComparison.Ordinal))
            {
                throw new SetupFileStepException(SetupCodes.StalePlan);
            }

            if (current.Exists)
            {
                platform.FileSystem.ReplaceFile(temporary, target);
            }
            else
            {
                platform.FileSystem.MoveFile(temporary, target, overwrite: false);
            }
        }
        catch (SetupFileStepException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }

        return new AtomicFileApplyResult(previousHash, appliedHash, true);
    }

    public AtomicFileRestoreResult Restore(
        string allowedRoot,
        string targetPath,
        string backupPath,
        string expectedAppliedHash,
        string expectedPreviousHash)
    {
        var backup = ReadAndValidateBackup(backupPath, expectedPreviousHash);

        var target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
        var current = ReadTarget(target);
        if (!string.Equals(SetupHash.File(current.Exists, current.Bytes), expectedAppliedHash, StringComparison.Ordinal))
        {
            throw new SetupFileStepException(SetupCodes.RollbackStale);
        }

        target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
        current = ReadTarget(target);
        if (!string.Equals(SetupHash.File(current.Exists, current.Bytes), expectedAppliedHash, StringComparison.Ordinal))
        {
            throw new SetupFileStepException(SetupCodes.RollbackStale);
        }

        if (!backup.Exists)
        {
            try
            {
                platform.FileSystem.DeleteFile(target);
            }
            catch (Exception)
            {
                throw new SetupFileStepException(SetupCodes.InternalError);
            }

            return new AtomicFileRestoreResult(expectedPreviousHash);
        }

        var temporary = CreateTemporaryPath(target);
        try
        {
            platform.FileSystem.WriteNewAllBytes(temporary, backup.Bytes);
            platform.FileSystem.FlushFile(temporary);
            target = SetupPathPolicy.ValidateFileTarget(platform, allowedRoot, targetPath);
            current = ReadTarget(target);
            if (!string.Equals(SetupHash.File(current.Exists, current.Bytes), expectedAppliedHash, StringComparison.Ordinal))
            {
                throw new SetupFileStepException(SetupCodes.RollbackStale);
            }

            platform.FileSystem.ReplaceFile(temporary, target);
        }
        catch (SetupFileStepException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }

        return new AtomicFileRestoreResult(expectedPreviousHash);
    }

    public void ValidateBackup(string backupPath, string expectedPreviousHash) =>
        _ = ReadAndValidateBackup(backupPath, expectedPreviousHash);

    public AtomicFileCapture ReadBackup(string backupPath, string expectedPreviousHash)
    {
        var backup = ReadAndValidateBackup(backupPath, expectedPreviousHash);
        return new AtomicFileCapture(backup.Exists, backup.Bytes);
    }

    private TargetState ReadTarget(string path)
    {
        try
        {
            var metadata = platform.FileSystem.GetPathMetadata(path);
            if (!metadata.Exists)
            {
                return new TargetState(false, []);
            }

            if (metadata.Kind != SetupPathKind.File || (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SetupFileStepException(SetupCodes.UnsafePath);
            }

            return new TargetState(true, platform.FileSystem.ReadAllBytes(path));
        }
        catch (SetupFileStepException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }
    }

    private string CreateTemporaryPath(string target) =>
        target + ".cao-" + platform.Identifiers.CreateUuidV7().ToString("D") + ".tmp";

    private void ValidateExistingBackup(
        string backupPath,
        byte[] expectedBytes,
        SetupPathMetadata? initialMetadata = null)
    {
        var before = initialMetadata ?? platform.FileSystem.GetPathMetadata(backupPath);
        ValidateBackupMetadata(before);
        var read = platform.FileSystem.ReadAtMostBytes(backupPath, expectedBytes.Length);
        if (!read.IsComplete || !read.Bytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new FormatException();
        }

        ValidateBackupMetadata(platform.FileSystem.GetPathMetadata(backupPath));
    }

    private BackupState ReadAndValidateBackup(string backupPath, string expectedPreviousHash)
    {
        try
        {
            var metadata = platform.FileSystem.GetPathMetadata(backupPath);
            if (!metadata.Exists || metadata.Kind != SetupPathKind.File || (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new FormatException();
            }

            var backup = DeserializeBackup(platform.FileSystem.ReadAllBytes(backupPath));
            if (!string.Equals(SetupHash.File(backup.Exists, backup.Bytes), expectedPreviousHash, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            var after = platform.FileSystem.GetPathMetadata(backupPath);
            if (!after.Exists || after.Kind != SetupPathKind.File || (after.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new FormatException();
            }

            return backup;
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.InternalError);
        }
    }

    private static void ValidateBackupMetadata(SetupPathMetadata metadata)
    {
        if (!metadata.Exists ||
            metadata.Kind != SetupPathKind.File ||
            (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FormatException();
        }
    }

    private static byte[] SerializeBackup(bool exists, byte[] content)
    {
        var bytes = new byte[BackupMagic.Length + 9 + content.Length];
        BackupMagic.CopyTo(bytes, 0);
        bytes[BackupMagic.Length] = exists ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(BackupMagic.Length + 1, 8), exists ? (ulong)content.Length : 0);
        if (exists)
        {
            content.CopyTo(bytes, BackupMagic.Length + 9);
        }

        return bytes;
    }

    private static BackupState DeserializeBackup(byte[] bytes)
    {
        var headerLength = BackupMagic.Length + 9;
        if (bytes.Length < headerLength || !bytes.AsSpan(0, BackupMagic.Length).SequenceEqual(BackupMagic))
        {
            throw new FormatException();
        }

        var state = bytes[BackupMagic.Length];
        var length = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(BackupMagic.Length + 1, 8));
        if (state > 1 || length > int.MaxValue || bytes.Length != headerLength + (int)length || state == 0 && length != 0)
        {
            throw new FormatException();
        }

        return new BackupState(state == 1, bytes.AsSpan(headerLength).ToArray());
    }

    private sealed record TargetState(bool Exists, byte[] Bytes);

    private sealed record BackupState(bool Exists, byte[] Bytes);
}

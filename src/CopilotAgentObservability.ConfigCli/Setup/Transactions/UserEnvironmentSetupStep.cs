using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed class SetupEnvironmentStepException : Exception
{
    public SetupEnvironmentStepException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record UserEnvironmentValue
{
    private UserEnvironmentValue(bool exists, string? value)
    {
        Exists = exists;
        Value = value;
    }

    public bool Exists { get; }

    public string? Value { get; }

    public static UserEnvironmentValue Missing { get; } = new(false, null);

    public static UserEnvironmentValue Present(string value) => new(true, value ?? throw new ArgumentNullException(nameof(value)));
}

internal sealed record UserEnvironmentMemberCapture(string Name, UserEnvironmentValue Value, string Hash);

internal sealed record UserEnvironmentCapture(IReadOnlyList<UserEnvironmentMemberCapture> Members, string AggregateHash);

internal sealed record UserEnvironmentApplyResult(string PreviousHash, string AppliedHash, bool Changed);

internal sealed record UserEnvironmentRestoreResult(string RestoredHash);

internal sealed class UserEnvironmentSetupStep(ISetupPlatform platform)
{
    private const int MaximumMembers = 32;
    private const int MaximumNameCharacters = 255;
    private const int MaximumValueCharacters = 32_767;
    private const int MaximumBackupBytes = 2 * 1024 * 1024;
    private const ushort BackupVersion = 1;
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("CAOENV");
    private static readonly byte[] MemberHashDomain = Encoding.ASCII.GetBytes("CAO-USER-ENV-MEMBER\0");
    private static readonly byte[] AggregateHashDomain = Encoding.ASCII.GetBytes("CAO-USER-ENV-AGGREGATE\0");
    private static readonly Encoding CanonicalEncoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    public UserEnvironmentCapture Capture(IReadOnlyList<string> orderedNames)
    {
        ValidateNames(orderedNames);
        var members = new UserEnvironmentMemberCapture[orderedNames.Count];
        try
        {
            for (var index = 0; index < orderedNames.Count; index++)
            {
                var name = orderedNames[index];
                var raw = platform.UserEnvironment.Get(name);
                var value = raw is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(raw);
                ValidateValue(value);
                members[index] = new UserEnvironmentMemberCapture(name, value, HashMemberCore(name, value));
            }
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }

        return CreateCapture(members);
    }

    public string HashMember(string name, UserEnvironmentValue value)
    {
        ValidateNames([name]);
        ValidateValue(value);
        return HashMemberCore(name, value);
    }

    internal static string HashPlannedMember(SetupPrivatePlanMember member)
    {
        var desired = SetupEnvironmentPlanValue.Desired(member);
        ValidateNames([member.SettingKey]);
        ValidateValue(desired);
        return HashMemberCore(member.SettingKey, desired);
    }

    public void CreateBackup(string backupPath, UserEnvironmentCapture capture)
    {
        ValidateCapture(capture);
        try
        {
            platform.FileSystem.WriteNewAllBytes(backupPath, SerializeBackup(capture));
            platform.FileSystem.FlushFile(backupPath);
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    public void CreateOrValidateBackup(string backupPath, UserEnvironmentCapture expected)
    {
        try
        {
            ValidateCapture(expected);
            var expectedBytes = SerializeBackup(expected);
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
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    public UserEnvironmentCapture ReadBackup(string backupPath, IReadOnlyList<string> orderedNames)
    {
        ValidateNames(orderedNames);
        try
        {
            var capture = DeserializeBackup(ReadValidatedBackupBytes(backupPath));
            if (capture.Members.Count != orderedNames.Count ||
                capture.Members.Where((member, index) =>
                    !string.Equals(member.Name, orderedNames[index], StringComparison.Ordinal)).Any())
            {
                throw new FormatException();
            }

            return capture;
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    public UserEnvironmentApplyResult ApplyMember(
        string name,
        string expectedPreviousHash,
        UserEnvironmentValue desired)
    {
        ValidateNames([name]);
        ValidateHash(expectedPreviousHash);
        ValidateValue(desired);
        var current = ReadMember(name);
        var previousHash = HashMemberCore(name, current);
        if (!string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal))
        {
            throw new SetupEnvironmentStepException(SetupCodes.StalePlan);
        }

        var appliedHash = HashMemberCore(name, desired);
        if (string.Equals(previousHash, appliedHash, StringComparison.Ordinal))
        {
            return new UserEnvironmentApplyResult(previousHash, appliedHash, false);
        }

        WriteMember(name, desired);
        return new UserEnvironmentApplyResult(previousHash, appliedHash, true);
    }

    public UserEnvironmentRestoreResult RestoreMember(
        string name,
        string backupPath,
        string expectedAppliedHash,
        string expectedPreviousHash)
    {
        ValidateNames([name]);
        ValidateHash(expectedAppliedHash);
        ValidateHash(expectedPreviousHash);
        var backup = ReadBackupContainingMember(backupPath, name);
        if (!string.Equals(backup.Hash, expectedPreviousHash, StringComparison.Ordinal))
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }

        var current = ReadMember(name);
        if (!string.Equals(HashMemberCore(name, current), expectedAppliedHash, StringComparison.Ordinal))
        {
            throw new SetupEnvironmentStepException(SetupCodes.RollbackStale);
        }

        if (!string.Equals(expectedAppliedHash, expectedPreviousHash, StringComparison.Ordinal))
        {
            WriteMember(name, backup.Value);
        }

        return new UserEnvironmentRestoreResult(expectedPreviousHash);
    }

    public void NotifyFinalState()
    {
        try
        {
            platform.UserEnvironment.NotifyChange();
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    private UserEnvironmentMemberCapture ReadBackupContainingMember(string backupPath, string name)
    {
        try
        {
            return DeserializeBackup(ReadValidatedBackupBytes(backupPath)).Members.Single(member =>
                string.Equals(member.Name, name, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    private byte[] ReadValidatedBackupBytes(string backupPath)
    {
        var before = platform.FileSystem.GetPathMetadata(backupPath);
        ValidateBackupMetadata(before);
        var read = platform.FileSystem.ReadAtMostBytes(backupPath, MaximumBackupBytes);
        if (!read.IsComplete)
        {
            throw new FormatException();
        }

        var after = platform.FileSystem.GetPathMetadata(backupPath);
        ValidateBackupMetadata(after);
        if (before != after)
        {
            throw new FormatException();
        }

        return read.Bytes;
    }

    private UserEnvironmentValue ReadMember(string name)
    {
        try
        {
            var raw = platform.UserEnvironment.Get(name);
            var value = raw is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(raw);
            ValidateValue(value);
            return value;
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    private void WriteMember(string name, UserEnvironmentValue value)
    {
        try
        {
            platform.UserEnvironment.Set(name, value.Exists ? value.Value : null);
        }
        catch (Exception)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InternalError);
        }
    }

    private static UserEnvironmentCapture CreateCapture(IReadOnlyList<UserEnvironmentMemberCapture> members)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(AggregateHashDomain);
        AppendUInt32(hash, (uint)members.Count);
        foreach (var member in members)
        {
            AppendCanonicalMember(hash, member.Name, member.Value);
        }

        return new UserEnvironmentCapture(members, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string HashMemberCore(string name, UserEnvironmentValue value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemberHashDomain);
        AppendCanonicalMember(hash, name, value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendCanonicalMember(IncrementalHash hash, string name, UserEnvironmentValue value)
    {
        AppendString(hash, name);
        hash.AppendData([value.Exists ? (byte)1 : (byte)0]);
        if (value.Exists)
        {
            AppendString(hash, value.Value!);
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = CanonicalEncoding.GetBytes(value);
        AppendUInt32(hash, (uint)bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static byte[] SerializeBackup(UserEnvironmentCapture capture)
    {
        using var stream = new MemoryStream();
        stream.Write(BackupMagic);
        WriteUInt16(stream, BackupVersion);
        WriteUInt16(stream, checked((ushort)capture.Members.Count));
        foreach (var member in capture.Members)
        {
            WriteString(stream, member.Name);
            stream.WriteByte(member.Value.Exists ? (byte)1 : (byte)0);
            if (member.Value.Exists)
            {
                WriteString(stream, member.Value.Value!);
            }
        }

        var payload = stream.ToArray();
        var checksum = SHA256.HashData(payload);
        if (payload.Length + checksum.Length > MaximumBackupBytes)
        {
            throw new FormatException();
        }

        var envelope = new byte[payload.Length + checksum.Length];
        payload.CopyTo(envelope, 0);
        checksum.CopyTo(envelope, payload.Length);
        return envelope;
    }

    private void ValidateExistingBackup(
        string backupPath,
        byte[] expectedBytes,
        SetupPathMetadata? initialMetadata = null)
    {
        var before = initialMetadata ?? platform.FileSystem.GetPathMetadata(backupPath);
        ValidateBackupMetadata(before);
        var read = platform.FileSystem.ReadAtMostBytes(backupPath, MaximumBackupBytes);
        if (!read.IsComplete || !read.Bytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new FormatException();
        }

        _ = DeserializeBackup(read.Bytes);
        ValidateBackupMetadata(platform.FileSystem.GetPathMetadata(backupPath));
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

    private static UserEnvironmentCapture DeserializeBackup(byte[] bytes)
    {
        const int checksumLength = 32;
        if (bytes.Length <= checksumLength)
        {
            throw new FormatException();
        }

        var payload = bytes.AsSpan(0, bytes.Length - checksumLength);
        var expectedChecksum = bytes.AsSpan(bytes.Length - checksumLength);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), expectedChecksum))
        {
            throw new FormatException();
        }

        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var magic = ReadBytes(stream, BackupMagic.Length);
        if (!magic.AsSpan().SequenceEqual(BackupMagic) || ReadUInt16(stream) != BackupVersion)
        {
            throw new FormatException();
        }

        var count = ReadUInt16(stream);
        if (count is 0 or > MaximumMembers)
        {
            throw new FormatException();
        }

        var members = new UserEnvironmentMemberCapture[count];
        for (var index = 0; index < count; index++)
        {
            var name = ReadString(stream, MaximumNameCharacters);
            var state = stream.ReadByte();
            var value = state switch
            {
                0 => UserEnvironmentValue.Missing,
                1 => UserEnvironmentValue.Present(ReadString(stream, MaximumValueCharacters)),
                _ => throw new FormatException(),
            };
            members[index] = new UserEnvironmentMemberCapture(name, value, HashMemberCore(name, value));
        }

        if (stream.Position != stream.Length)
        {
            throw new FormatException();
        }

        ValidateNames(members.Select(member => member.Name).ToArray());
        return CreateCapture(members);
    }

    private static void ValidateCapture(UserEnvironmentCapture? capture)
    {
        if (capture is null)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
        }

        ValidateNames(capture.Members.Select(member => member.Name).ToArray());
        foreach (var member in capture.Members)
        {
            ValidateValue(member.Value);
            if (!string.Equals(member.Hash, HashMemberCore(member.Name, member.Value), StringComparison.Ordinal))
            {
                throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
            }
        }

        var canonical = CreateCapture(capture.Members);
        if (!string.Equals(canonical.AggregateHash, capture.AggregateHash, StringComparison.Ordinal))
        {
            throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
        }
    }

    private static void ValidateNames(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count is 0 or > MaximumMembers)
        {
            throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaximumNameCharacters ||
                !IsWellFormedUtf16(name) ||
                name.Contains('=') || name.Any(character => char.IsControl(character)) ||
                !unique.Add(name))
            {
                throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
            }
        }
    }

    private static void ValidateValue(UserEnvironmentValue value)
    {
        if (value is null || value.Exists && (value.Value is null || value.Value.Length > MaximumValueCharacters) ||
            !value.Exists && value.Value is not null ||
            value.Value is not null && !IsWellFormedUtf16(value.Value))
        {
            throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
        }
    }

    private static bool IsWellFormedUtf16(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new SetupEnvironmentStepException(SetupCodes.InvalidArguments);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = CanonicalEncoding.GetBytes(value);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static string ReadString(Stream stream, int maximumCharacters)
    {
        var length = ReadUInt32(stream);
        if ((length & 1) != 0 || length > maximumCharacters * 2u)
        {
            throw new FormatException();
        }

        return CanonicalEncoding.GetString(ReadBytes(stream, checked((int)length)));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        var bytes = ReadBytes(stream, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static uint ReadUInt32(Stream stream)
    {
        var bytes = ReadBytes(stream, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int length)
    {
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

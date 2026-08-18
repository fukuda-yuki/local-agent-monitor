using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public enum DiscoveryRootKindV1 : byte
{
    ProjectPath = 1,
    SkillDirectory = 2
}

public readonly struct DiscoveryRootNativeIdentityV1
{
    public const int ByteLength = 24;

    private readonly byte[] identityBytes;

    private DiscoveryRootNativeIdentityV1(SkillProducerPathKeyPlatform platform, byte[] identityBytes)
    {
        Platform = platform;
        this.identityBytes = identityBytes;
    }

    public SkillProducerPathKeyPlatform Platform { get; }

    public static DiscoveryRootNativeIdentityV1 CreateWindows(ulong volumeSerial, ReadOnlySpan<byte> fileId128)
    {
        if (fileId128.Length != 16)
        {
            throw new ArgumentException("A Windows FILE_ID_128 must be exactly 16 bytes.", nameof(fileId128));
        }

        var bytes = new byte[ByteLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), volumeSerial);
        fileId128.CopyTo(bytes.AsSpan(8, 16));
        return new DiscoveryRootNativeIdentityV1(SkillProducerPathKeyPlatform.Windows, bytes);
    }

    public static DiscoveryRootNativeIdentityV1 CreateLinux(ulong mountId, uint deviceMajor, uint deviceMinor, ulong inode)
    {
        var bytes = new byte[ByteLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), mountId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), deviceMajor);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), deviceMinor);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8), inode);
        return new DiscoveryRootNativeIdentityV1(SkillProducerPathKeyPlatform.Linux, bytes);
    }

    public byte[] ToByteArray() => (byte[])identityBytes.Clone();

    internal ReadOnlySpan<byte> AsSpan() => identityBytes;
}

public sealed record DiscoveryRootCandidateV1(
    DiscoveryRootKindV1 RootKind,
    DiscoveryRootNativeIdentityV1 NativeIdentity,
    SkillProducerPathKeyV1 SdkRootPathKey);

public sealed class DiscoveryRootSetV1
{
    public const int MaxProjectPaths = 16;
    public const int MaxSkillDirectories = 32;

    private static readonly byte[] FramePrefix = Encoding.UTF8.GetBytes("skill-discovery-roots\0v1\0");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] frame;
    private readonly ReadOnlyCollection<string> projectPathKeys;
    private readonly ReadOnlyCollection<string> skillDirectoryKeys;

    private DiscoveryRootSetV1(
        SkillProducerPathKeyPlatform platform,
        byte[] frame,
        string revision,
        ReadOnlyCollection<string> projectPathKeys,
        ReadOnlyCollection<string> skillDirectoryKeys)
    {
        Platform = platform;
        this.frame = frame;
        Revision = revision;
        this.projectPathKeys = projectPathKeys;
        this.skillDirectoryKeys = skillDirectoryKeys;
    }

    [JsonIgnore]
    public SkillProducerPathKeyPlatform Platform { get; }

    [JsonIgnore]
    public string Revision { get; }

    [JsonIgnore]
    public IReadOnlyList<string> ProjectPathKeys => projectPathKeys;

    [JsonIgnore]
    public IReadOnlyList<string> SkillDirectoryKeys => skillDirectoryKeys;

    // Internal rather than public: nothing outside this assembly (and its test twin, via
    // InternalsVisibleTo) needs the raw wire frame, only the Revision it hashes to and the two
    // canonical key arrays. Keeping it internal shrinks the request-memory-only surface.
    [JsonIgnore]
    internal ReadOnlyMemory<byte> Frame => frame.ToArray();

    public static DiscoveryRootSetV1 Create(SkillProducerPathKeyPlatform platform, IEnumerable<DiscoveryRootCandidateV1> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Grouped on (RootKind, native identity) rather than native identity alone: a directory
        // configured as both a ProjectPath and a SkillDirectory must reach the SDK call in both
        // canonical arrays, so it deliberately survives dedupe as two entries, not one.
        var winners = new Dictionary<(DiscoveryRootKindV1 RootKind, string NativeIdentityHex), Entry>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            // A mismatched candidate is rejected, never skipped: an explicitly configured root that
            // silently vanished would degrade the set to a valid-looking subset, and Gate 8 admits no
            // silent partial-root reduction.
            if (candidate.NativeIdentity.Platform != platform || candidate.SdkRootPathKey.Platform != platform)
            {
                throw new ArgumentException(
                    "skill_discovery_root_platform_mismatch",
                    nameof(candidates));
            }

            var nativeIdentityBytes = candidate.NativeIdentity.ToByteArray();
            var pathKeyUtf8 = StrictUtf8.GetBytes(candidate.SdkRootPathKey.Key);
            var groupKey = (candidate.RootKind, Convert.ToHexStringLower(nativeIdentityBytes));

            // Ordinally smallest means the UTF-8 byte sequence, never string.CompareOrdinal: a
            // supplementary-plane character (encoded as a UTF-16 surrogate pair) can sort before
            // a BMP character in UTF-16 code-unit order while sorting after it in UTF-8 byte
            // order, and the wire contract is defined over the encoded bytes.
            if (!winners.TryGetValue(groupKey, out var current) ||
                pathKeyUtf8.AsSpan().SequenceCompareTo(current.PathKeyUtf8) < 0)
            {
                winners[groupKey] = new Entry(candidate.RootKind, nativeIdentityBytes, pathKeyUtf8, candidate.SdkRootPathKey.Key);
            }
        }

        var projectPathCount = winners.Keys.Count(key => key.RootKind == DiscoveryRootKindV1.ProjectPath);
        if (projectPathCount > MaxProjectPaths)
        {
            throw new ArgumentException($"At most {MaxProjectPaths} distinct ProjectPath roots survive dedupe.", nameof(candidates));
        }

        var skillDirectoryCount = winners.Keys.Count(key => key.RootKind == DiscoveryRootKindV1.SkillDirectory);
        if (skillDirectoryCount > MaxSkillDirectories)
        {
            throw new ArgumentException($"At most {MaxSkillDirectories} distinct SkillDirectory roots survive dedupe.", nameof(candidates));
        }

        var sortedEntries = winners.Values
            .OrderBy(entry => (byte)entry.RootKind)
            .ThenBy(entry => entry.NativeIdentityBytes, OrdinalByteSequenceComparer.Instance)
            .ThenBy(entry => entry.PathKeyUtf8, OrdinalByteSequenceComparer.Instance)
            .ToList();

        var frame = BuildFrame(platform, sortedEntries);
        var revision = Convert.ToHexStringLower(SHA256.HashData(frame));

        var projectPathKeys = sortedEntries
            .Where(entry => entry.RootKind == DiscoveryRootKindV1.ProjectPath)
            .Select(entry => entry.PathKeyString)
            .ToArray();
        var skillDirectoryKeys = sortedEntries
            .Where(entry => entry.RootKind == DiscoveryRootKindV1.SkillDirectory)
            .Select(entry => entry.PathKeyString)
            .ToArray();

        return new DiscoveryRootSetV1(
            platform,
            frame,
            revision,
            new ReadOnlyCollection<string>(projectPathKeys),
            new ReadOnlyCollection<string>(skillDirectoryKeys));
    }

    // No persistence, logging, metrics, or static state here by design: the Gate 8 contract keeps
    // the revision, path keys, and native identities request-memory-only, so ToString must never
    // become a leak path and nothing about this instance is cached beyond the caller's own scope.
    public override string ToString() =>
        $"{nameof(DiscoveryRootSetV1)} {{ EntryCount = {projectPathKeys.Count + skillDirectoryKeys.Count} }}";

    private static byte[] BuildFrame(SkillProducerPathKeyPlatform platform, IReadOnlyList<Entry> sortedEntries)
    {
        var totalLength = FramePrefix.Length + 1 + 2;
        foreach (var entry in sortedEntries)
        {
            totalLength += 1 + 2 + DiscoveryRootNativeIdentityV1.ByteLength + 4 + entry.PathKeyUtf8.Length;
        }

        var frame = new byte[totalLength];
        var offset = 0;

        FramePrefix.CopyTo(frame.AsSpan(offset));
        offset += FramePrefix.Length;

        frame[offset] = PlatformWireByte(platform);
        offset += 1;

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), (ushort)sortedEntries.Count);
        offset += 2;

        foreach (var entry in sortedEntries)
        {
            frame[offset] = (byte)entry.RootKind;
            offset += 1;

            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), (ushort)DiscoveryRootNativeIdentityV1.ByteLength);
            offset += 2;

            entry.NativeIdentityBytes.CopyTo(frame.AsSpan(offset, DiscoveryRootNativeIdentityV1.ByteLength));
            offset += DiscoveryRootNativeIdentityV1.ByteLength;

            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset, 4), (uint)entry.PathKeyUtf8.Length);
            offset += 4;

            entry.PathKeyUtf8.CopyTo(frame.AsSpan(offset, entry.PathKeyUtf8.Length));
            offset += entry.PathKeyUtf8.Length;
        }

        return frame;
    }

    private static byte PlatformWireByte(SkillProducerPathKeyPlatform platform) => platform switch
    {
        SkillProducerPathKeyPlatform.Windows => 1,
        SkillProducerPathKeyPlatform.Linux => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported discovery root set platform.")
    };

    private sealed record Entry(DiscoveryRootKindV1 RootKind, byte[] NativeIdentityBytes, byte[] PathKeyUtf8, string PathKeyString);

    private sealed class OrdinalByteSequenceComparer : IComparer<byte[]>
    {
        public static readonly OrdinalByteSequenceComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => (x ?? []).AsSpan().SequenceCompareTo(y ?? []);
    }
}

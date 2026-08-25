using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class DiscoveryRootSetV1Tests
{
    private const string GoldenSha256 = "7d72e2b7213c04012f49dafc37f55a7573e59eab9c3be7e9c72fdc0778a82a28";

    [Fact]
    public void Golden_ChecksumMatchesCheckedInFixture()
    {
        var bytes = File.ReadAllBytes(GoldenPath());

        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Golden_Vectors_MatchFrameBytesLengthAndRevisionExactly()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var expectedFrame = Convert.FromHexString(vector.GetProperty("frame_hex").GetString()!);
            var expectedFrameBytes = vector.GetProperty("frame_bytes").GetInt32();
            var expectedSha256 = vector.GetProperty("sha256").GetString()!;

            var set = BuildVector(name);
            var actualFrame = set.Frame.ToArray();

            Assert.True(
                expectedFrame.AsSpan().SequenceEqual(actualFrame),
                $"Vector '{name}': frame bytes did not match golden.\nExpected: {Convert.ToHexStringLower(expectedFrame)}\nActual:   {Convert.ToHexStringLower(actualFrame)}");
            Assert.True(
                expectedFrameBytes == actualFrame.Length,
                $"Vector '{name}': expected frame_bytes {expectedFrameBytes}, got {actualFrame.Length}.");
            Assert.True(
                string.Equals(expectedSha256, set.Revision, StringComparison.Ordinal),
                $"Vector '{name}': expected Revision {expectedSha256}, got {set.Revision}.");
        }
    }

    [Fact]
    public void Header_FirstTwentyFiveBytesAndPlatformByte_MatchTheFixedPrefixAndPlatform()
    {
        var windowsFrame = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, []).Frame.ToArray();
        var linuxFrame = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Linux, []).Frame.ToArray();
        var expectedPrefix = Encoding.UTF8.GetBytes("skill-discovery-roots\0v1\0");

        Assert.Equal(25, expectedPrefix.Length);
        Assert.True(expectedPrefix.AsSpan().SequenceEqual(windowsFrame.AsSpan(0, 25)), "Windows frame prefix mismatch.");
        Assert.True(expectedPrefix.AsSpan().SequenceEqual(linuxFrame.AsSpan(0, 25)), "Linux frame prefix mismatch.");
        Assert.Equal(1, windowsFrame[25]);
        Assert.Equal(2, linuxFrame[25]);
    }

    [Fact]
    public void Dedupe_SameRoleSameNativeIdentity_YieldsOneEntry_KeepingTheOrdinallySmallestKey()
    {
        var smaller = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\Alpha", 1, SequentialFileId());
        var larger = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\Bravo", 1, SequentialFileId());

        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [larger, smaller]);

        Assert.Single(set.ProjectPathKeys);
        Assert.Equal("C:\\Alpha", set.ProjectPathKeys[0]);
    }

    [Fact]
    public void Dedupe_SameNativeIdentityInBothRoles_YieldsTwoEntries()
    {
        var project = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\repo", 1, SequentialFileId());
        var skill = WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, "C:\\skills", 1, SequentialFileId());

        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [project, skill]);

        Assert.Single(set.ProjectPathKeys);
        Assert.Single(set.SkillDirectoryKeys);
        Assert.Equal("C:\\repo", set.ProjectPathKeys[0]);
        Assert.Equal("C:\\skills", set.SkillDirectoryKeys[0]);
    }

    [Fact]
    public void Dedupe_ComparesUtf8BytesNotUtf16CodeUnits()
    {
        // U+10000 requires a UTF-16 surrogate pair (leading unit 0xD800) but encodes to UTF-8 as
        // F0 90 80 80. U+FFFD is a single BMP code unit (0xFFFD) that encodes to UTF-8 as
        // EF BF BD. 0xD800 < 0xFFFD makes the U+10000 key smaller under UTF-16 ordinal
        // comparison, while 0xEF < 0xF0 makes the U+FFFD key smaller under UTF-8 byte comparison.
        // This is exactly the divergence the contract calls out: "ordinally smallest" means the
        // UTF-8 bytes, so the U+FFFD key must be the one that survives dedupe.
        var supplementaryPlaneSegment = char.ConvertFromUtf32(0x10000);
        var keyAboveBmp = "C:\\" + supplementaryPlaneSegment;
        var keyReplacementCharacter = "C:\\\uFFFD";

        Assert.True(string.CompareOrdinal(keyAboveBmp, keyReplacementCharacter) < 0, "Test setup assumption failed: UTF-16 ordinal should favor the above-BMP key.");
        Assert.True(
            Encoding.UTF8.GetBytes(keyReplacementCharacter).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(keyAboveBmp)) < 0,
            "Test setup assumption failed: UTF-8 byte order should favor the U+FFFD key.");

        var above = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, keyAboveBmp, 1, SequentialFileId());
        var replacement = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, keyReplacementCharacter, 1, SequentialFileId());

        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [above, replacement]);

        Assert.Single(set.ProjectPathKeys);
        Assert.Equal(keyReplacementCharacter, set.ProjectPathKeys[0]);
    }

    [Fact]
    public void Bounds_SixteenProjectPathsAndThirtyTwoSkillDirectories_BuildTogetherAndYield48Entries()
    {
        var candidates = new List<DiscoveryRootCandidateV1>();
        for (var index = 0; index < DiscoveryRootSetV1.MaxProjectPaths; index++)
        {
            candidates.Add(WindowsCandidate(DiscoveryRootKindV1.ProjectPath, $"C:\\p{index}", (ulong)index + 1, DistinctFileId(index)));
        }

        for (var index = 0; index < DiscoveryRootSetV1.MaxSkillDirectories; index++)
        {
            candidates.Add(WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, $"C:\\s{index}", (ulong)index + 1000, DistinctFileId(index + 100)));
        }

        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, candidates);

        Assert.Equal(DiscoveryRootSetV1.MaxProjectPaths, set.ProjectPathKeys.Count);
        Assert.Equal(DiscoveryRootSetV1.MaxSkillDirectories, set.SkillDirectoryKeys.Count);
        Assert.Equal(48, ReadEntryCount(set.Frame.ToArray()));
    }

    [Fact]
    public void Bounds_SeventeenthDistinctProjectPath_IsRejected()
    {
        var candidates = new List<DiscoveryRootCandidateV1>();
        for (var index = 0; index < DiscoveryRootSetV1.MaxProjectPaths + 1; index++)
        {
            candidates.Add(WindowsCandidate(DiscoveryRootKindV1.ProjectPath, $"C:\\p{index}", (ulong)index + 1, DistinctFileId(index)));
        }

        Assert.Throws<ArgumentException>(() => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, candidates));
    }

    [Fact]
    public void Bounds_ThirtyThirdDistinctSkillDirectory_IsRejected()
    {
        var candidates = new List<DiscoveryRootCandidateV1>();
        for (var index = 0; index < DiscoveryRootSetV1.MaxSkillDirectories + 1; index++)
        {
            candidates.Add(WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, $"C:\\s{index}", (ulong)index + 1, DistinctFileId(index)));
        }

        Assert.Throws<ArgumentException>(() => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, candidates));
    }

    [Fact]
    public void Bounds_SeventeenProjectPathCandidatesCollapsingToSixteenGroups_IsAccepted()
    {
        var candidates = new List<DiscoveryRootCandidateV1>();
        for (var index = 0; index < DiscoveryRootSetV1.MaxProjectPaths; index++)
        {
            candidates.Add(WindowsCandidate(DiscoveryRootKindV1.ProjectPath, $"C:\\p{index}", (ulong)index + 1, DistinctFileId(index)));
        }

        candidates.Add(WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\p0-duplicate", 1, DistinctFileId(0)));

        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, candidates);

        Assert.Equal(DiscoveryRootSetV1.MaxProjectPaths, set.ProjectPathKeys.Count);
    }

    [Fact]
    public void ZeroRoots_BuildsSuccessfully_AndProducesTheHeaderOnlyFrameForEachPlatform()
    {
        var windowsSet = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, []);
        var linuxSet = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Linux, []);

        Assert.Equal(28, windowsSet.Frame.Length);
        Assert.Equal(28, linuxSet.Frame.Length);
        Assert.Empty(windowsSet.ProjectPathKeys);
        Assert.Empty(windowsSet.SkillDirectoryKeys);
        Assert.Empty(linuxSet.ProjectPathKeys);
        Assert.Empty(linuxSet.SkillDirectoryKeys);
    }

    [Fact]
    public void NativeIdentity_WindowsFileIdNotSixteenBytes_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => DiscoveryRootNativeIdentityV1.CreateWindows(1, new byte[15]));
        Assert.Throws<ArgumentException>(() => DiscoveryRootNativeIdentityV1.CreateWindows(1, new byte[17]));
        Assert.Throws<ArgumentException>(() => DiscoveryRootNativeIdentityV1.CreateWindows(1, []));
    }

    [Fact]
    public void NativeIdentity_WindowsAndLinuxEncodings_MatchTheGoldenByteLayoutExactly()
    {
        var windows = DiscoveryRootNativeIdentityV1.CreateWindows(1, SequentialFileId());
        Assert.Equal(Convert.FromHexString("0000000000000001000102030405060708090a0b0c0d0e0f"), windows.ToByteArray());

        var linux = DiscoveryRootNativeIdentityV1.CreateLinux(1, 2, 3, 4);
        Assert.Equal(Convert.FromHexString("000000000000000100000002000000030000000000000004"), linux.ToByteArray());
    }

    [Fact]
    public void NativeIdentity_ToByteArray_ReturnsACopy_MutatingItDoesNotAffectLaterReads()
    {
        var identity = DiscoveryRootNativeIdentityV1.CreateWindows(1, SequentialFileId());

        var copy = identity.ToByteArray();
        copy[0] = 0xFF;

        Assert.Equal((byte)0x00, identity.ToByteArray()[0]);
    }

    [Fact]
    public void ToString_ContainsOnlyTheTypeNameAndEntryCount_NeverAPathKeyRevisionOrIdentity()
    {
        var candidate = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\SENTINEL", 1, SequentialFileId());
        var set = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [candidate]);

        var text = set.ToString();

        Assert.Equal("DiscoveryRootSetV1 { EntryCount = 1 }", text);
        Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
        Assert.DoesNotContain(set.Revision, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Determinism_RepeatedBuildsAndDifferentInputOrder_ProduceIdenticalFramesAndRevisions()
    {
        var a = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\a", 1, DistinctFileId(1));
        var b = WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, "C:\\b", 2, DistinctFileId(2));
        var c = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\c", 3, DistinctFileId(3));

        var first = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [a, b, c]);
        var second = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [a, b, c]);
        var reordered = DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [c, a, b]);

        Assert.True(first.Frame.ToArray().AsSpan().SequenceEqual(second.Frame.ToArray()), "Repeated build produced a different frame.");
        Assert.True(first.Frame.ToArray().AsSpan().SequenceEqual(reordered.Frame.ToArray()), "Reordered build produced a different frame.");
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.Revision, reordered.Revision);
    }

    private static DiscoveryRootSetV1 BuildVector(string name) => name switch
    {
        "windows-empty" => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, []),
        "linux-empty" => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Linux, []),
        "windows-one-project" => DiscoveryRootSetV1.Create(
            SkillProducerPathKeyPlatform.Windows,
            [WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\repo", 1, SequentialFileId())]),
        "windows-same-native-both-roles" => DiscoveryRootSetV1.Create(
            SkillProducerPathKeyPlatform.Windows,
            [
                WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\repo", 1, SequentialFileId()),
                WindowsCandidate(DiscoveryRootKindV1.SkillDirectory, "C:\\skills", 1, SequentialFileId())
            ]),
        "linux-one-project" => DiscoveryRootSetV1.Create(
            SkillProducerPathKeyPlatform.Linux,
            [LinuxCandidate(DiscoveryRootKindV1.ProjectPath, "/srv/repo", 1, 2, 3, 4)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown golden vector name.")
    };

    [Fact]
    public void Create_RejectsAPlatformMismatchedCandidateInsteadOfSkippingIt()
    {
        var windows = WindowsCandidate(DiscoveryRootKindV1.ProjectPath, "C:\\repo", 1, FileId());
        var linux = LinuxCandidate(DiscoveryRootKindV1.ProjectPath, "/srv/repo", 1, 2, 3, 4);

        Assert.Throws<ArgumentException>(
            () => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, [windows, linux]));
        Assert.Throws<ArgumentException>(
            () => DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Linux, [linux, windows]));
    }

    private static byte[] FileId() =>
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    private static DiscoveryRootCandidateV1 WindowsCandidate(DiscoveryRootKindV1 kind, string path, ulong volumeSerial, byte[] fileId128)
    {
        var parsed = SkillProducerPathKeyV1.TryParse(path, SkillProducerPathKeyPlatform.Windows, out var key, out var reason);
        Assert.True(parsed, $"Test setup failed to parse Windows path '{path}': {reason}.");

        return new DiscoveryRootCandidateV1(kind, DiscoveryRootNativeIdentityV1.CreateWindows(volumeSerial, fileId128), key);
    }

    private static DiscoveryRootCandidateV1 LinuxCandidate(DiscoveryRootKindV1 kind, string path, ulong mountId, uint deviceMajor, uint deviceMinor, ulong inode)
    {
        var parsed = SkillProducerPathKeyV1.TryParse(path, SkillProducerPathKeyPlatform.Linux, out var key, out var reason);
        Assert.True(parsed, $"Test setup failed to parse Linux path '{path}': {reason}.");

        return new DiscoveryRootCandidateV1(kind, DiscoveryRootNativeIdentityV1.CreateLinux(mountId, deviceMajor, deviceMinor, inode), key);
    }

    private static byte[] SequentialFileId() =>
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    private static byte[] DistinctFileId(int index)
    {
        var fileId = new byte[16];
        fileId[14] = (byte)(index >> 8);
        fileId[15] = (byte)index;
        return fileId;
    }

    private static int ReadEntryCount(byte[] frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(26, 2));

    private static string GoldenPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "CopilotAgentObservability.LocalMonitor.Tests",
                "TestData",
                "SkillInvocationSnapshot",
                "discovery-revision-v1.golden.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Checked-in discovery root set golden was not found.");
    }
}

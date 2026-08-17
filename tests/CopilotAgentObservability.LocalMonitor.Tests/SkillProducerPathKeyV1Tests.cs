using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProducerPathKeyV1Tests
{
    private const string GoldenSha256 = "e397becf9711b6fe51bd1174cbca00a450b699a4fe941cc24c298d7739b1370e";

    [Fact]
    public void Golden_ChecksumMatchesCheckedInFixture()
    {
        var bytes = File.ReadAllBytes(GoldenPath());

        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Golden_ParseVectors_MatchExpectedOutcomeAndKey()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("parse_vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString();
            var platform = ParsePlatform(vector.GetProperty("platform").GetString()!);
            var input = vector.GetProperty("input").GetString();
            var outcome = vector.GetProperty("outcome").GetString();

            var parsed = SkillProducerPathKeyV1.TryParse(input, platform, out var key, out var reason);

            if (outcome == "ok")
            {
                Assert.True(parsed, $"Expected {name} to parse.");
                Assert.Equal(SkillProducerPathKeyParseReason.None, reason);
                Assert.Equal(vector.GetProperty("key").GetString(), key.Key);
            }
            else
            {
                Assert.False(parsed, $"Expected {name} to be rejected.");
                Assert.True(
                    string.Equals(outcome, ReasonToken(reason), StringComparison.Ordinal),
                    $"Unexpected reason for {name}: expected {outcome}, got {ReasonToken(reason)}.");
            }
        }
    }

    [Fact]
    public void Golden_EqualityVectors_MatchExpectedEquality()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("equality_vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString();
            var platform = ParsePlatform(vector.GetProperty("platform").GetString()!);
            var expectedEqual = vector.GetProperty("equal").GetBoolean();

            Assert.True(SkillProducerPathKeyV1.TryParse(vector.GetProperty("left").GetString(), platform, out var left, out _));
            Assert.True(SkillProducerPathKeyV1.TryParse(vector.GetProperty("right").GetString(), platform, out var right, out _));

            Assert.Equal(expectedEqual, left.Equals(right));
            Assert.Equal(expectedEqual, right.Equals(left));
            if (expectedEqual)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        }
    }

    [Fact]
    public void Golden_RelationVectors_MatchExpectedOutcome()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("relation_vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var platform = ParsePlatform(vector.GetProperty("platform").GetString()!);
            var expectedOutcome = vector.GetProperty("outcome").GetString();

            Assert.True(SkillProducerPathKeyV1.TryParse(vector.GetProperty("candidate").GetString(), platform, out var candidate, out _), name);

            if (vector.TryGetProperty("roots", out var rootsElement))
            {
                var roots = rootsElement.EnumerateArray()
                    .Select(rootValue =>
                    {
                        Assert.True(SkillProducerPathKeyV1.TryParse(rootValue.GetString(), platform, out var root, out _), name);
                        return root;
                    })
                    .ToArray();

                var matchCount = roots.Count(candidate.IsStrictDescendantOf);
                Assert.Equal("unsafe_multiple_targets", expectedOutcome);
                Assert.True(matchCount > 1, $"Expected {name} to match more than one root.");
                continue;
            }

            Assert.True(SkillProducerPathKeyV1.TryParse(vector.GetProperty("root").GetString(), platform, out var root2, out _), name);
            var descendant = candidate.IsStrictDescendantOf(root2);

            if (expectedOutcome == "not_descendant")
            {
                Assert.False(descendant, name);
                continue;
            }

            Assert.True(descendant, name);
            var relativeSegments = RelativeSegments(platform, root2.Key, candidate.Key);

            if (expectedOutcome == "not_skill_file")
            {
                Assert.NotEqual("SKILL.md", relativeSegments[^1], StringComparer.Ordinal);
                continue;
            }

            Assert.Equal("descendant", expectedOutcome);
            Assert.Equal("SKILL.md", relativeSegments[^1], StringComparer.Ordinal);
            var expectedRelative = vector.GetProperty("relative_segments").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(expectedRelative, relativeSegments);
        }
    }

    [Theory]
    [InlineData("c:\\Foo\\Bar", "C:\\Foo\\Bar", true)]
    [InlineData("C:\\Foo\\Bar", "C:\\Foo\\Bar", true)]
    public void TryParse_WindowsDriveLetterCase_FoldsToUppercaseKey(string left, string right, bool expectedEqual)
    {
        Assert.True(SkillProducerPathKeyV1.TryParse(left, SkillProducerPathKeyPlatform.Windows, out var leftKey, out _));
        Assert.True(SkillProducerPathKeyV1.TryParse(right, SkillProducerPathKeyPlatform.Windows, out var rightKey, out _));

        Assert.Equal("C:\\Foo\\Bar", leftKey.Key);
        Assert.Equal(expectedEqual, leftKey.Equals(rightKey));
    }

    [Fact]
    public void TryParse_WindowsSegmentCase_IsNotFolded()
    {
        Assert.True(SkillProducerPathKeyV1.TryParse("C:\\foo\\bar", SkillProducerPathKeyPlatform.Windows, out var lower, out _));
        Assert.True(SkillProducerPathKeyV1.TryParse("C:\\Foo\\Bar", SkillProducerPathKeyPlatform.Windows, out var mixed, out _));

        Assert.NotEqual(mixed.Key, lower.Key);
        Assert.False(lower.Equals(mixed));
        Assert.False(mixed.Equals(lower));
    }

    [Theory]
    [InlineData("C:/repo/SKILL.md")]
    [InlineData("\\\\server\\share\\SKILL.md")]
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("C:foo")]
    [InlineData("C:\\a:b")]
    [InlineData("C:\\a\\")]
    [InlineData("C:\\a\\\\b")]
    [InlineData("C:\\.")]
    [InlineData("C:\\..")]
    public void TryParse_WindowsRejections_ReturnFalse(string input)
    {
        Assert.False(SkillProducerPathKeyV1.TryParse(input, SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.NotEqual(SkillProducerPathKeyParseReason.None, reason);
    }

    [Fact]
    public void TryParse_WindowsSegmentTooLong_IsRejected()
    {
        var input = "C:\\" + new string('a', 256);

        Assert.False(SkillProducerPathKeyV1.TryParse(input, SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InvalidSegment, reason);
    }

    [Fact]
    public void TryParse_WindowsMaximumSegmentLength_IsAccepted()
    {
        var input = "C:\\" + new string('a', 255);

        Assert.True(SkillProducerPathKeyV1.TryParse(input, SkillProducerPathKeyPlatform.Windows, out var key, out _));
        Assert.Equal(input, key.Key);
    }

    [Theory]
    [InlineData("C:\\repo.")]
    [InlineData("C:\\repo ")]
    public void TryParse_WindowsSegmentTrailingDotOrSpace_IsRejected(string input)
    {
        Assert.False(SkillProducerPathKeyV1.TryParse(input, SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InvalidSegment, reason);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("CON.txt")]
    public void TryParse_WindowsReservedDeviceStem_IsRejected(string segment)
    {
        Assert.False(SkillProducerPathKeyV1.TryParse($"C:\\{segment}", SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InvalidSegment, reason);
    }

    [Fact]
    public void TryParse_WindowsReservedStemLookalike_IsAccepted()
    {
        Assert.True(SkillProducerPathKeyV1.TryParse("C:\\CONS", SkillProducerPathKeyPlatform.Windows, out var key, out _));
        Assert.Equal("C:\\CONS", key.Key);
    }

    [Fact]
    public void TryParse_WindowsRootAlone_IsAccepted()
    {
        Assert.True(SkillProducerPathKeyV1.TryParse("C:\\", SkillProducerPathKeyPlatform.Windows, out var key, out _));
        Assert.Equal("C:\\", key.Key);
    }

    [Fact]
    public void TryParse_WindowsDriveColonAlone_IsRejected()
    {
        Assert.False(SkillProducerPathKeyV1.TryParse("C:", SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InvalidAnchor, reason);
    }

    [Fact]
    public void TryParse_LinuxRootAlone_IsAccepted()
    {
        Assert.True(SkillProducerPathKeyV1.TryParse("/", SkillProducerPathKeyPlatform.Linux, out var key, out _));
        Assert.Equal("/", key.Key);
    }

    [Theory]
    [InlineData("//a")]
    [InlineData("/a/")]
    [InlineData("/a//b")]
    [InlineData("/a\\b")]
    [InlineData("/.")]
    [InlineData("/..")]
    public void TryParse_LinuxRejections_ReturnFalse(string input)
    {
        Assert.False(SkillProducerPathKeyV1.TryParse(input, SkillProducerPathKeyPlatform.Linux, out _, out var reason));
        Assert.NotEqual(SkillProducerPathKeyParseReason.None, reason);
    }

    [Fact]
    public void TryParse_LinuxSegmentByteBound_Is255BytesNot255Characters()
    {
        var accepted = "/" + new string('\u00e9', 127) + "a";
        var rejected = "/" + new string('\u00e9', 128);

        Assert.True(SkillProducerPathKeyV1.TryParse(accepted, SkillProducerPathKeyPlatform.Linux, out var key, out _));
        Assert.Equal(accepted, key.Key);

        Assert.False(SkillProducerPathKeyV1.TryParse(rejected, SkillProducerPathKeyPlatform.Linux, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InvalidSegment, reason);
    }

    [Theory]
    [InlineData(SkillProducerPathKeyPlatform.Windows)]
    [InlineData(SkillProducerPathKeyPlatform.Linux)]
    public void TryParse_ControlCharacters_AreRejected(SkillProducerPathKeyPlatform platform)
    {
        foreach (var scalar in new[] { 0x0000, 0x001f, 0x007f })
        {
            var input = AnchoredInput(platform, ((char)scalar).ToString());

            Assert.False(SkillProducerPathKeyV1.TryParse(input, platform, out _, out var reason));
            Assert.Equal(SkillProducerPathKeyParseReason.ControlCharacter, reason);
        }
    }

    [Theory]
    [InlineData(SkillProducerPathKeyPlatform.Windows)]
    [InlineData(SkillProducerPathKeyPlatform.Linux)]
    public void TryParse_UnpairedSurrogates_AreRejected(SkillProducerPathKeyPlatform platform)
    {
        var highOnly = AnchoredInput(platform, "\ud800");
        var lowOnly = AnchoredInput(platform, "\udc00");

        Assert.False(SkillProducerPathKeyV1.TryParse(highOnly, platform, out _, out var highReason));
        Assert.Equal(SkillProducerPathKeyParseReason.UnpairedSurrogate, highReason);

        Assert.False(SkillProducerPathKeyV1.TryParse(lowOnly, platform, out _, out var lowReason));
        Assert.Equal(SkillProducerPathKeyParseReason.UnpairedSurrogate, lowReason);
    }

    [Theory]
    [InlineData(SkillProducerPathKeyPlatform.Windows)]
    [InlineData(SkillProducerPathKeyPlatform.Linux)]
    public void TryParse_EmptyOrNull_IsRejected(SkillProducerPathKeyPlatform platform)
    {
        Assert.False(SkillProducerPathKeyV1.TryParse(null, platform, out _, out var nullReason));
        Assert.Equal(SkillProducerPathKeyParseReason.InputEmpty, nullReason);

        Assert.False(SkillProducerPathKeyV1.TryParse(string.Empty, platform, out _, out var emptyReason));
        Assert.Equal(SkillProducerPathKeyParseReason.InputEmpty, emptyReason);
    }

    [Fact]
    public void TryParse_WindowsInputByteBound_Exactly4096AcceptedOver4096Rejected()
    {
        var segment = new string('a', 177);
        var accepted = "C:\\" + string.Join('\\', Enumerable.Repeat(segment, 23));
        Assert.Equal(4_096, Encoding.UTF8.GetByteCount(accepted));

        Assert.True(SkillProducerPathKeyV1.TryParse(accepted, SkillProducerPathKeyPlatform.Windows, out var key, out _));
        Assert.Equal(accepted, key.Key);

        var rejected = accepted + "\\b";
        Assert.True(Encoding.UTF8.GetByteCount(rejected) > 4_096);
        Assert.False(SkillProducerPathKeyV1.TryParse(rejected, SkillProducerPathKeyPlatform.Windows, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InputTooLarge, reason);
    }

    [Fact]
    public void TryParse_LinuxInputByteBound_Exactly4096AcceptedOver4096Rejected()
    {
        var segment = new string('a', 255);
        var accepted = "/" + string.Join('/', Enumerable.Repeat(segment, 16));
        Assert.Equal(4_096, Encoding.UTF8.GetByteCount(accepted));

        Assert.True(SkillProducerPathKeyV1.TryParse(accepted, SkillProducerPathKeyPlatform.Linux, out var key, out _));
        Assert.Equal(accepted, key.Key);

        var rejected = accepted + "/b";
        Assert.True(Encoding.UTF8.GetByteCount(rejected) > 4_096);
        Assert.False(SkillProducerPathKeyV1.TryParse(rejected, SkillProducerPathKeyPlatform.Linux, out _, out var reason));
        Assert.Equal(SkillProducerPathKeyParseReason.InputTooLarge, reason);
    }

    [Fact]
    public void IsStrictDescendantOf_WindowsRelations_MatchExpectedRules()
    {
        Assert.True(Parse("C:\\a\\b", SkillProducerPathKeyPlatform.Windows).IsStrictDescendantOf(Parse("C:\\a", SkillProducerPathKeyPlatform.Windows)));

        var self = Parse("C:\\a", SkillProducerPathKeyPlatform.Windows);
        Assert.False(self.IsStrictDescendantOf(self));

        Assert.False(Parse("C:\\ab", SkillProducerPathKeyPlatform.Windows).IsStrictDescendantOf(Parse("C:\\a", SkillProducerPathKeyPlatform.Windows)));
        Assert.False(Parse("C:\\a\\b", SkillProducerPathKeyPlatform.Windows).IsStrictDescendantOf(Parse("C:\\b", SkillProducerPathKeyPlatform.Windows)));
    }

    [Fact]
    public void IsStrictDescendantOf_LinuxRelations_MatchExpectedRules()
    {
        Assert.True(Parse("/a/b", SkillProducerPathKeyPlatform.Linux).IsStrictDescendantOf(Parse("/a", SkillProducerPathKeyPlatform.Linux)));

        var self = Parse("/a", SkillProducerPathKeyPlatform.Linux);
        Assert.False(self.IsStrictDescendantOf(self));

        Assert.False(Parse("/ab", SkillProducerPathKeyPlatform.Linux).IsStrictDescendantOf(Parse("/a", SkillProducerPathKeyPlatform.Linux)));
        Assert.False(Parse("/a/b", SkillProducerPathKeyPlatform.Linux).IsStrictDescendantOf(Parse("/b", SkillProducerPathKeyPlatform.Linux)));
    }

    [Fact]
    public void Equals_WindowsKeyNeverEqualsLinuxKey_EvenWhenTextCouldCoincide()
    {
        var windowsKey = Parse("C:\\srv\\SKILL.md", SkillProducerPathKeyPlatform.Windows);
        var linuxKey = Parse("/srv/SKILL.md", SkillProducerPathKeyPlatform.Linux);

        Assert.False(windowsKey.Equals(linuxKey));
        Assert.False(linuxKey.Equals(windowsKey));
    }

    private static SkillProducerPathKeyV1 Parse(string input, SkillProducerPathKeyPlatform platform)
    {
        Assert.True(SkillProducerPathKeyV1.TryParse(input, platform, out var key, out _));
        return key;
    }

    private static string AnchoredInput(SkillProducerPathKeyPlatform platform, string segment) =>
        platform == SkillProducerPathKeyPlatform.Windows ? $"C:\\{segment}" : $"/{segment}";

    private static SkillProducerPathKeyPlatform ParsePlatform(string value) => value switch
    {
        "windows" => SkillProducerPathKeyPlatform.Windows,
        "linux" => SkillProducerPathKeyPlatform.Linux,
        _ => throw new InvalidOperationException($"Unknown golden platform '{value}'.")
    };

    private static string ReasonToken(SkillProducerPathKeyParseReason reason) => reason switch
    {
        SkillProducerPathKeyParseReason.InvalidAnchor => "invalid_anchor",
        SkillProducerPathKeyParseReason.InvalidSeparator => "invalid_separator",
        SkillProducerPathKeyParseReason.InvalidSegment => "invalid_segment",
        SkillProducerPathKeyParseReason.InvalidTrailingSeparator => "invalid_trailing_separator",
        _ => throw new InvalidOperationException($"Reason {reason} has no golden token.")
    };

    private static string[] RelativeSegments(SkillProducerPathKeyPlatform platform, string rootKey, string candidateKey)
    {
        var separator = platform == SkillProducerPathKeyPlatform.Windows ? '\\' : '/';
        var prefix = rootKey.EndsWith(separator) ? rootKey : rootKey + separator;
        return candidateKey[prefix.Length..].Split(separator);
    }

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
                "path-key-v1.golden.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Checked-in skill producer path key golden was not found.");
    }
}

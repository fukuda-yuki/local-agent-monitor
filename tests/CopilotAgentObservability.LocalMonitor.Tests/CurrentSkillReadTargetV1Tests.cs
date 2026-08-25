using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CurrentSkillReadTargetV1Tests : IDisposable
{
    private readonly TempFileHandleSource handleSource = new();

    public void Dispose() => handleSource.Dispose();

    [Fact]
    public void ValidTarget_ExposesRoleSegmentsAndRevision()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        var target = new CurrentSkillReadTargetV1(root, ["team", "SKILL.md"], "revision-1");

        Assert.Equal(DiscoveryRootKindV1.ProjectPath, target.RootRole);
        Assert.Equal(["team", "SKILL.md"], target.RelativeSegments);
        Assert.Equal("revision-1", target.ExpectedRevision);
        Assert.Same(root, target.RetainedRoot);
    }

    [Fact]
    public void RejectsDisposedRetainedRoot()
    {
        var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);
        root.Dispose();

        Assert.Throws<ArgumentException>(() => new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1"));
    }

    [Fact]
    public void RejectsEmptySegmentList()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        Assert.Throws<ArgumentException>(() => new CurrentSkillReadTargetV1(root, [], "revision-1"));
    }

    [Fact]
    public void RejectsMoreThan2048Segments()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);
        var segments = Enumerable.Range(0, CurrentSkillReadTargetV1.MaximumRelativeSegments)
            .Select(index => $"segment{index:0000}")
            .ToList();
        segments.Add("SKILL.md");

        Assert.Equal(CurrentSkillReadTargetV1.MaximumRelativeSegments + 1, segments.Count);
        Assert.Throws<ArgumentException>(() => new CurrentSkillReadTargetV1(root, segments, "revision-1"));
    }

    [Fact]
    public void AcceptsExactly2048Segments()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);
        var segments = Enumerable.Range(0, CurrentSkillReadTargetV1.MaximumRelativeSegments - 1)
            .Select(index => $"segment{index:0000}")
            .ToList();
        segments.Add("SKILL.md");
        Assert.Equal(CurrentSkillReadTargetV1.MaximumRelativeSegments, segments.Count);

        var target = new CurrentSkillReadTargetV1(root, segments, "revision-1");

        Assert.Equal(CurrentSkillReadTargetV1.MaximumRelativeSegments, target.RelativeSegments.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    public void RejectsReservedAndControlSegments(string segment)
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        Assert.Throws<ArgumentException>(() => new CurrentSkillReadTargetV1(root, [segment, "SKILL.md"], "revision-1"));
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('"')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    [InlineData(':')]
    [InlineData('/')]
    [InlineData('\\')]
    public void RejectsWindowsForbiddenCharactersInWindowsRoot(char forbidden)
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        Assert.Throws<ArgumentException>(
            () => new CurrentSkillReadTargetV1(root, [$"team{forbidden}x", "SKILL.md"], "revision-1"));
    }

    [Fact]
    public void RejectsWindowsSegmentLongerThan255CodeUnits()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);
        var longSegment = new string('a', 256);

        Assert.Throws<ArgumentException>(
            () => new CurrentSkillReadTargetV1(root, [longSegment, "SKILL.md"], "revision-1"));
    }

    [Fact]
    public void RejectsLinuxSegmentContainingSeparator()
    {
        using var root = CreateRetainedRoot("/srv/skills", SkillProducerPathKeyPlatform.Linux);

        Assert.Throws<ArgumentException>(
            () => new CurrentSkillReadTargetV1(root, ["team/x", "SKILL.md"], "revision-1"));
    }

    [Fact]
    public void RejectsLinuxSegmentLongerThan255Utf8Bytes()
    {
        using var root = CreateRetainedRoot("/srv/skills", SkillProducerPathKeyPlatform.Linux);
        // 128 characters × 2 UTF-8 bytes each = 256 bytes.
        var longSegment = new string('é', 128);

        Assert.Throws<ArgumentException>(
            () => new CurrentSkillReadTargetV1(root, [longSegment, "SKILL.md"], "revision-1"));
    }

    [Theory]
    [InlineData("skill.md")]
    [InlineData("SKILL.MD")]
    [InlineData("SKILL.md ")]
    [InlineData("SKILL.md.backup")]
    public void RejectsFinalSegmentThatIsNotExactlySkillMarkdown(string finalSegment)
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        Assert.Throws<ArgumentException>(
            () => new CurrentSkillReadTargetV1(root, ["team", finalSegment], "revision-1"));
    }

    [Fact]
    public void AcceptsSingleSkillSegmentDirectlyUnderRoot()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");

        Assert.Equal(["SKILL.md"], target.RelativeSegments);
    }

    [Fact]
    public void RejectsEmptyExpectedRevision()
    {
        using var root = CreateRetainedRoot("C:\\skills", SkillProducerPathKeyPlatform.Windows);

        Assert.Throws<ArgumentException>(() => new CurrentSkillReadTargetV1(root, ["SKILL.md"], ""));
    }

    private RetainedDiscoveryRootV1 CreateRetainedRoot(string rootPath, SkillProducerPathKeyPlatform platform)
    {
        if (!SkillProducerPathKeyV1.TryParse(rootPath, platform, out var pathKey, out var reason))
        {
            throw new InvalidOperationException($"Test root path failed to parse ({reason}).");
        }

        var identity = platform == SkillProducerPathKeyPlatform.Windows
            ? DiscoveryRootNativeIdentityV1.CreateWindows(1234UL, new byte[16])
            : DiscoveryRootNativeIdentityV1.CreateLinux(42, 8, 1, 2);

        return new RetainedDiscoveryRootV1(
            DiscoveryRootKindV1.ProjectPath,
            pathKey,
            identity,
            handleSource.OpenHandle());
    }

    private sealed class TempFileHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-target-{Guid.NewGuid():N}");

        private readonly string filePath;

        public TempFileHandleSource()
        {
            Directory.CreateDirectory(directoryPath);
            filePath = Path.Combine(directoryPath, "handle-source.bin");
            File.WriteAllBytes(filePath, [1, 2, 3]);
        }

        public SafeFileHandle OpenHandle() => File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
                // Test fixture cleanup is best-effort.
            }
        }
    }
}

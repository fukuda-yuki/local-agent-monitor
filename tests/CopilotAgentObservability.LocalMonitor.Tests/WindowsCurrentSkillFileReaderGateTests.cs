using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class WindowsCurrentSkillFileReaderGateTests : IDisposable
{
    private static readonly DateTimeOffset FixedReadAt = new(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);

    private readonly string rootPath =
        Path.Combine(Path.GetTempPath(), $"cao-skillread-{Guid.NewGuid():N}");

    private readonly WindowsDiscoveryRootOpenerV1 opener = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(rootPath, recursive: true);
        }
        catch (IOException)
        {
            // Test fixture cleanup is best-effort.
        }
    }

    [Fact]
    public void Success_ReturnsExactBytesDigestAndFixedReadAt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var skillPath = CreateNestedSkillFile(["team", "nested"], "# héllo → skill\n");
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["team", "nested", "SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        var expected = Encoding.UTF8.GetBytes("# héllo → skill\n");
        Assert.Equal(CurrentSkillNativeOutcomeV1.Success, result.Outcome);
        Assert.Equal(expected, result.Body);
        Assert.Equal(SHA256.HashData(expected), result.BodySha256);
        Assert.Equal(FixedReadAt, result.ReadAt);
    }

    [Fact]
    public void Success_PreservesUtf8BomBytesUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateNestedSkillFile([], "\uFEFFbom-body");
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Success, result.Outcome);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("bom-body")], result.Body);
    }

    [Fact]
    public void Missing_FinalSegment_ReturnsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Missing, result.Outcome);
    }

    [Fact]
    public void Missing_IntermediateSegment_ReturnsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["no-such-directory", "SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Missing, result.Outcome);
    }

    [Fact]
    public void FinalSegmentIsDirectory_ReturnsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(rootPath, "SKILL.md"));
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Unsafe, result.Outcome);
    }

    [Fact]
    public void IntermediateSegmentIsFile_ReturnsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "team"), "not a directory");
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["team", "SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Unsafe, result.Outcome);
    }

    [Fact]
    public void DirectorySymlinkSegment_ReturnsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var externalTarget = Path.Combine(Path.GetTempPath(), $"cao-skillread-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalTarget);
        File.WriteAllText(Path.Combine(externalTarget, "SKILL.md"), "external");
        Directory.CreateDirectory(rootPath);
        CreateDirectoryLinkOrSkip(Path.Combine(rootPath, "link"), externalTarget);

        try
        {
            using var root = RetainRoot(rootPath);
            var target = new CurrentSkillReadTargetV1(root, ["link", "SKILL.md"], "revision-1");
            var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

            var result = reader.Read(target, CancellationToken.None);

            Assert.Equal(CurrentSkillNativeOutcomeV1.Unsafe, result.Outcome);
        }
        finally
        {
            TryDeleteRecursive(externalTarget);
        }
    }

    [Fact]
    public void FileSymlinkFinalSegment_ReturnsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "real-target.txt"), "external content");
        CreateFileLinkOrSkip(Path.Combine(rootPath, "SKILL.md"), Path.Combine(rootPath, "real-target.txt"));
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Unsafe, result.Outcome);
    }

    [Fact]
    public void Race_AppendBetweenMetadataCaptureAndProofs_ReturnsRaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var skillPath = CreateNestedSkillFile([], "original-body");
        var hooks = new CurrentSkillFileReaderHooksV1
        {
            AfterFinalMetadataCaptured = _ =>
                File.AppendAllText(skillPath, "+appended", Encoding.UTF8),
        };
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt, hooks);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Raced, result.Outcome);
    }

    [Fact]
    public void Race_SameLengthRewriteAfterRead_ReturnsRaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var skillPath = CreateNestedSkillFile([], "AAAA-AAAA");
        var hooks = new CurrentSkillFileReaderHooksV1
        {
            AfterReadCompleted = _ =>
            {
                using var rewrite = new FileStream(
                    skillPath,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                rewrite.Write(Encoding.UTF8.GetBytes("BBBB-BBBB"));
            },
        };
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt, hooks);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Raced, result.Outcome);
    }

    [Fact]
    public void Race_RootDisposedAfterTargetConstruction_ReturnsRaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateNestedSkillFile([], "body");
        var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        root.Dispose();
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Raced, result.Outcome);
    }

    [Fact]
    public void SharingViolationHolder_ReturnsOtherNativeFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var skillPath = CreateNestedSkillFile([], "locked-body");
        using var exclusiveHolder = new FileStream(
            skillPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.OtherNativeFailure, result.Outcome);
    }

    [Fact]
    public void BodyAtExactlyMaximumBytes_ReturnsSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var body = new byte[WindowsCurrentSkillFileReaderV1.MaximumBodyBytes];
        Array.Fill(body, (byte)'a');
        CreateNestedSkillFileBytes([], body);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Success, result.Outcome);
        Assert.Equal(body.Length, result.Body!.Length);
    }

    [Fact]
    public void BodyOneByteOverMaximum_ReturnsOversized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var body = new byte[WindowsCurrentSkillFileReaderV1.MaximumBodyBytes + 1];
        Array.Fill(body, (byte)'a');
        CreateNestedSkillFileBytes([], body);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Oversized, result.Outcome);
    }

    [Fact]
    public void InvalidUtf8Body_ReturnsBinary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateNestedSkillFileBytes([], [0xFF, 0xFE, 0x41, 0x42]);
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Binary, result.Outcome);
    }

    [Fact]
    public void HandlesAreClosedInReverseAcquisitionOrder_AndNothingIsRetained()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateNestedSkillFile(["a", "b"], "nested-body");
        var opened = new List<IntPtr>();
        var closed = new List<IntPtr>();
        var hooks = new CurrentSkillFileReaderHooksV1
        {
            HandleOpened = handle => opened.Add(handle),
            HandleClosed = handle => closed.Add(handle),
        };
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["a", "b", "SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt, hooks);

        var result = reader.Read(target, CancellationToken.None);

        Assert.Equal(CurrentSkillNativeOutcomeV1.Success, result.Outcome);
        Assert.Equal(3, opened.Count);
        Assert.Equal(3, closed.Count);
        Assert.Equal(opened.Count, opened.Distinct().Count());
        Assert.Equal(opened.AsEnumerable().Reverse(), closed);
    }

    [Fact]
    public void CancelledToken_ThrowsBeforeAnyNativeWork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateNestedSkillFile([], "body");
        using var root = RetainRoot(rootPath);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.Read(target, cancellation.Token));
    }

    [Fact]
    public void LinuxPlatformRoot_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(SkillProducerPathKeyV1.TryParse(
            "/srv/skills",
            SkillProducerPathKeyPlatform.Linux,
            out var pathKey,
            out _));

        using var handleSource = File.OpenHandle(
            CreateNestedSkillFile([], "body"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var root = new RetainedDiscoveryRootV1(
            DiscoveryRootKindV1.SkillDirectory,
            pathKey,
            DiscoveryRootNativeIdentityV1.CreateLinux(42, 8, 1, 2),
            handleSource);
        var target = new CurrentSkillReadTargetV1(root, ["SKILL.md"], "revision-1");
        var reader = new WindowsCurrentSkillFileReaderV1(() => FixedReadAt);

        Assert.Throws<ArgumentException>(() => reader.Read(target, CancellationToken.None));
    }

    private string CreateNestedSkillFile(string[] intermediateSegments, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return CreateNestedSkillFileBytes(intermediateSegments, bytes);
    }

    private string CreateNestedSkillFileBytes(string[] intermediateSegments, byte[] content)
    {
        var directory = rootPath;
        Directory.CreateDirectory(directory);
        foreach (var segment in intermediateSegments)
        {
            directory = Path.Combine(directory, segment);
            Directory.CreateDirectory(directory);
        }

        var skillPath = Path.Combine(directory, "SKILL.md");
        File.WriteAllBytes(skillPath, content);
        return skillPath;
    }

    private RetainedDiscoveryRootV1 RetainRoot(string path)
    {
        var result = opener.TryOpenRetainedRoot(path, DiscoveryRootKindV1.SkillDirectory);
        Assert.True(result.IsSuccess, $"expected a retained root (failure: {result.Failure})");
        return result.Root!;
    }

    private static void CreateDirectoryLinkOrSkip(string path, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}");
        }
    }

    private static void CreateFileLinkOrSkip(string path, string target)
    {
        try
        {
            _ = File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create file reparse fixture: {exception.GetType().Name}");
        }
    }

    private static void TryDeleteRecursive(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Test fixture cleanup is best-effort.
        }
    }
}

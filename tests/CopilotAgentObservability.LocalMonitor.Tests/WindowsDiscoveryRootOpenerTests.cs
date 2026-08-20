using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class WindowsDiscoveryRootOpenerTests : IDisposable
{
    private readonly string rootPath =
        Path.Combine(Path.GetTempPath(), $"cao-skillroot-{Guid.NewGuid():N}");

    private readonly WindowsDiscoveryRootOpenerV1 opener = new();

    public void Dispose()
    {
        TryDeleteRecursive(rootPath);
    }

    [WindowsFact]
    public void ValidNestedDirectory_Succeeds_IdentityReproves_UntilDispose()
    {
        Directory.CreateDirectory(Path.Combine(rootPath, "skills", "team"));
        var configuredRoot = Path.Combine(rootPath, "skills", "team");

        var result = opener.TryOpenRetainedRoot(configuredRoot, DiscoveryRootKindV1.SkillDirectory);

        Assert.True(result.IsSuccess, $"expected success but got {result.Failure}");
        Assert.NotNull(result.Root);
        Assert.Null(result.Failure);
        Assert.Equal(DiscoveryRootKindV1.SkillDirectory, result.Root!.Kind);

        Assert.True(SkillProducerPathKeyV1.TryParse(
            configuredRoot,
            SkillProducerPathKeyPlatform.Windows,
            out var expectedKey,
            out _));
        Assert.Equal(expectedKey, result.Root.PathKey);

        Assert.True(opener.TryReproveRetainedRoot(result.Root));

        result.Root.Dispose();
        Assert.False(opener.TryReproveRetainedRoot(result.Root));
    }

    [WindowsFact]
    public void DriveRoot_WithZeroSegments_Succeeds()
    {
        var result = opener.TryOpenRetainedRoot(@"C:\", DiscoveryRootKindV1.ProjectPath);

        Assert.True(result.IsSuccess, $"expected success but got {result.Failure}");
        Assert.Equal(DiscoveryRootKindV1.ProjectPath, result.Root!.Kind);
        result.Root.Dispose();
    }

    [WindowsFact]
    public void MissingPath_FailsUnopenable()
    {
        var missing = Path.Combine(rootPath, $"no-such-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        var result = opener.TryOpenRetainedRoot(missing, DiscoveryRootKindV1.ProjectPath);

        Assert.False(result.IsSuccess);
        Assert.Equal(DiscoveryRootOpenFailureV1.Unopenable, result.Failure);
    }

    [WindowsFact]
    public void FileInsteadOfDirectory_FailsNotADirectory()
    {
        Directory.CreateDirectory(rootPath);
        var filePath = Path.Combine(rootPath, "not-a-directory.txt");
        File.WriteAllText(filePath, "body");

        var result = opener.TryOpenRetainedRoot(filePath, DiscoveryRootKindV1.ProjectPath);

        Assert.False(result.IsSuccess);
        Assert.Equal(DiscoveryRootOpenFailureV1.NotADirectory, result.Failure);
    }

    [WindowsFact]
    public void ReparsePointRoot_FailsReparseRoot()
    {
        var realDirectory = Path.Combine(rootPath, "real");
        Directory.CreateDirectory(realDirectory);
        var linkPath = Path.Combine(rootPath, "link");
        CreateDirectoryLinkOrSkip(linkPath, realDirectory);

        var result = opener.TryOpenRetainedRoot(linkPath, DiscoveryRootKindV1.ProjectPath);

        Assert.False(result.IsSuccess);
        Assert.Equal(DiscoveryRootOpenFailureV1.ReparseRoot, result.Failure);
    }

    [WindowsTheory]
    [InlineData("relative\\path")]
    [InlineData("")]
    [InlineData(@"C:\trailing\")]
    [InlineData(@"C:/forward")]
    public void InvalidSyntax_FailsInvalidSyntax(string configuredRoot)
    {
        var result = opener.TryOpenRetainedRoot(configuredRoot, DiscoveryRootKindV1.ProjectPath);

        Assert.False(result.IsSuccess);
        Assert.Equal(DiscoveryRootOpenFailureV1.InvalidSyntax, result.Failure);
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

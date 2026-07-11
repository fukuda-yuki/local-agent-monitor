using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ApplyPathPolicyTests
{
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("C:outside.txt")]
    [InlineData("\\outside.txt")]
    [InlineData("\\\\?\\C:\\outside.txt")]
    [InlineData("\\\\.\\C:\\outside.txt")]
    [InlineData("\\\\server\\share\\outside.txt")]
    [InlineData("file:///outside.txt")]
    [InlineData(".\\file.txt")]
    public void Resolve_rejects_non_relative_path(string value)
    {
        using var directory = new ApplyTestDirectory();
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);

        var error = Assert.Throws<ApplyPathException>(() => ApplyPathPolicy.Resolve(root, value));

        Assert.Equal("invalid_relative_path", error.Code);
    }

    [Fact]
    public void Resolve_rejects_missing_and_non_regular_target()
    {
        using var directory = new ApplyTestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "folder"));
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);

        Assert.Equal("target_not_regular_file", Assert.Throws<ApplyPathException>(() => ApplyPathPolicy.Resolve(root, "missing.txt")).Code);
        Assert.Equal("target_not_regular_file", Assert.Throws<ApplyPathException>(() => ApplyPathPolicy.Resolve(root, "folder")).Code);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ancestor")]
    [InlineData("target")]
    public void Resolve_rejects_actual_reparse_bearing_root_ancestor_and_target(string location)
    {
        using var directory = new ApplyTestDirectory();
        var external = Path.Combine(directory.Path, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "file.txt"), "content");

        string rootPath;
        string relativePath;
        if (location == "root")
        {
            rootPath = Path.Combine(directory.Path, "root-link");
            CreateDirectoryLinkOrSkip(rootPath, external);
            relativePath = "file.txt";
        }
        else
        {
            rootPath = Path.Combine(directory.Path, "root");
            Directory.CreateDirectory(rootPath);
            if (location == "ancestor")
            {
                CreateDirectoryLinkOrSkip(Path.Combine(rootPath, "linked"), external);
                relativePath = "linked/file.txt";
            }
            else
            {
                CreateFileLinkOrSkip(Path.Combine(rootPath, "file.txt"), Path.Combine(external, "file.txt"));
                relativePath = "file.txt";
            }
        }

        if (location == "root")
        {
            Assert.Equal("invalid_apply_root", Assert.Throws<ApplyPathException>(() => ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath)).Code);
            return;
        }

        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        Assert.Equal("unsafe_reparse_path", Assert.Throws<ApplyPathException>(() => ApplyPathPolicy.Resolve(root, relativePath)).Code);
    }

    private static void CreateDirectoryLinkOrSkip(string path, string target)
    {
        try { _ = Directory.CreateSymbolicLink(path, target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}");
        }
    }

    private static void CreateFileLinkOrSkip(string path, string target)
    {
        try { _ = File.CreateSymbolicLink(path, target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create file reparse fixture: {exception.GetType().Name}");
        }
    }

    private sealed class ApplyTestDirectory : IDisposable
    {
        public ApplyTestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-apply-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}

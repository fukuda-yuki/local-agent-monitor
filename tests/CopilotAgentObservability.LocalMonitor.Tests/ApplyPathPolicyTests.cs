using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ApplyPathPolicyTests
{
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\outside.txt")]
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

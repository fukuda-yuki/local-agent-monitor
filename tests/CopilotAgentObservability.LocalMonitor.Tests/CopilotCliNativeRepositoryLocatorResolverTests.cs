using CopilotAgentObservability.LocalMonitor.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CopilotCliNativeRepositoryLocatorResolverTests
{
    private const string NativeId = "5e11f25a-a93b-4629-8d5f-f4f0afa8d548";

    [Fact]
    public void ExactWorkspaceAndSingleCanonicalRemoteResolve()
    {
        using var temp = new MonitorTempDirectory();
        WriteWorkspace(temp.Path, NativeId, temp.Path);

        var result = CopilotCliNativeRepositoryLocatorResolver.Resolve(
            NativeId, temp.Path, cwd => cwd == temp.Path
                ? new(cwd, ["git@github.com:Example/Widget.git"])
                : null);

        Assert.Equal("github.com/example/widget", result?.CanonicalLocator);
    }

    [Fact]
    public void MismatchedWorkspaceIdOrDistinctRemotesDoNotResolve()
    {
        using var temp = new MonitorTempDirectory();
        WriteWorkspace(temp.Path, "11111111-1111-4111-8111-111111111111", temp.Path);
        Assert.Null(CopilotCliNativeRepositoryLocatorResolver.Resolve(NativeId, temp.Path, _ => new(temp.Path, ["https://github.com/Example/Widget"])));

        WriteWorkspace(temp.Path, NativeId, temp.Path);
        Assert.Null(CopilotCliNativeRepositoryLocatorResolver.Resolve(
            NativeId, temp.Path, _ => new(temp.Path, ["https://github.com/Example/One", "https://github.com/Example/Two"])));
    }

    [Fact]
    public void NoRemotesUsesCommonDirectoryAndGroupsWorktreesButNotClones()
    {
        using var first = new MonitorTempDirectory();
        using var second = new MonitorTempDirectory();
        WriteWorkspace(first.Path, NativeId, first.Path);
        WriteWorkspace(second.Path, NativeId, second.Path);
        var common = Path.Combine(first.Path, ".git");

        var primary = CopilotCliNativeRepositoryLocatorResolver.Resolve(NativeId, first.Path, _ => new(common, []));
        var worktree = CopilotCliNativeRepositoryLocatorResolver.Resolve(NativeId, second.Path, _ => new(common, []));
        var clone = CopilotCliNativeRepositoryLocatorResolver.Resolve(NativeId, second.Path, _ => new(Path.Combine(second.Path, ".git"), []));

        Assert.Equal("local_git_repository", primary?.Kind);
        Assert.Equal(primary?.CanonicalLocator, worktree?.CanonicalLocator);
        Assert.NotEqual(primary?.CanonicalLocator, clone?.CanonicalLocator);
        Assert.DoesNotContain(first.Path, primary?.CanonicalLocator, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteWorkspace(string profile, string id, string cwd)
    {
        var directory = Path.Combine(profile, ".copilot", "session-state", NativeId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "workspace.yaml"), $"id: {id}\ncwd: {cwd}\n");
    }
}

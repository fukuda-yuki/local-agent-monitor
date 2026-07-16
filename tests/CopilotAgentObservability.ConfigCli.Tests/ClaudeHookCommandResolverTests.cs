using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeHookCommandResolverTests
{
    [Fact]
    public void Resolve_ExactRepositoryCompiledLayout_UsesFixedProjectCommand()
    {
        const string root = "C:\\repo";
        var baseDirectory = Path.Combine(root, "src", "CopilotAgentObservability.ConfigCli", "bin", "Debug", "net10.0");
        var project = Path.Combine(root, "src", "CopilotAgentObservability.LocalMonitor", "CopilotAgentObservability.LocalMonitor.csproj");

        var command = ClaudeHookCommandResolver.Resolve(baseDirectory, path => path == project);

        Assert.Equal(ClaudeHookCommandMode.Repository, command.Mode);
        Assert.Equal("dotnet", command.Command);
        Assert.Equal(["run", "--no-build", "--project", project, "--"], command.ArgumentPrefix);
    }

    [Fact]
    public void Resolve_ExactReleaseLayout_UsesSiblingPackagedExecutable()
    {
        var baseDirectory = Path.Combine("C:\\release", "app", "config-cli");
        var executable = Path.Combine("C:\\release", "app", "CopilotAgentObservability.LocalMonitor.exe");

        var command = ClaudeHookCommandResolver.Resolve(baseDirectory, path => path == executable);

        Assert.Equal(ClaudeHookCommandMode.Release, command.Mode);
        Assert.Equal(executable, command.Command);
        Assert.Empty(command.ArgumentPrefix);
    }

    [Fact]
    public void Resolve_UnknownLayout_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClaudeHookCommandResolver.Resolve("C:\\unknown", _ => false));
    }
}

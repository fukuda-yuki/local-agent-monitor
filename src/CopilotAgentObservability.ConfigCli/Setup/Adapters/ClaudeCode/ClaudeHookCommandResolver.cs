namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal enum ClaudeHookCommandMode
{
    Repository,
    Release,
}

internal sealed record ClaudeHookCommand(
    string Command,
    IReadOnlyList<string> ArgumentPrefix,
    ClaudeHookCommandMode Mode);

internal interface IClaudeHookCommandProvider
{
    ClaudeHookCommand? Resolve();
}

internal sealed class ClaudeHookCommandProvider(
    string baseDirectory,
    Func<string, bool>? fileExists = null) : IClaudeHookCommandProvider
{
    public ClaudeHookCommand? Resolve()
    {
        try
        {
            return ClaudeHookCommandResolver.Resolve(baseDirectory, fileExists);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

internal sealed class FixedClaudeHookCommandProvider(ClaudeHookCommand? command) : IClaudeHookCommandProvider
{
    public ClaudeHookCommand? Resolve() => command;
}

internal static class ClaudeHookCommandResolver
{
    private const string MonitorExecutableName = "CopilotAgentObservability.LocalMonitor.exe";
    private static readonly string[] RepositoryProjectSegments =
    [
        "src",
        "CopilotAgentObservability.LocalMonitor",
        "CopilotAgentObservability.LocalMonitor.csproj",
    ];

    public static ClaudeHookCommand Resolve(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        fileExists ??= File.Exists;
        var fullBase = Path.GetFullPath(baseDirectory);

        var releaseExecutable = ReleaseExecutable(fullBase);
        var release = releaseExecutable is not null && fileExists(releaseExecutable);

        var repositoryProject = RepositoryProject(fullBase);
        var repository = repositoryProject is not null && fileExists(repositoryProject);
        if (release == repository)
        {
            throw new InvalidOperationException("claude_hook_command_layout_unavailable");
        }

        return release
            ? new ClaudeHookCommand(releaseExecutable!, [], ClaudeHookCommandMode.Release)
            : new ClaudeHookCommand(
                "dotnet",
                ["run", "--no-build", "--project", repositoryProject!, "--"],
                ClaudeHookCommandMode.Repository);
    }

    private static string? ReleaseExecutable(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        return directory.Name == "config-cli" && directory.Parent?.Name == "app"
            ? Path.Combine(directory.Parent.FullName, MonitorExecutableName)
            : null;
    }

    private static string? RepositoryProject(string baseDirectory)
    {
        var root = new DirectoryInfo(baseDirectory);
        for (var index = 0; index < 5; index++)
        {
            root = root.Parent!;
            if (root is null)
            {
                return null;
            }
        }

        return Path.Combine([root.FullName, .. RepositoryProjectSegments]);
    }
}

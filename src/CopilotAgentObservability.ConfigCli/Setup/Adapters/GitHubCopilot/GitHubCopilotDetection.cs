using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal sealed record ChannelObservation(bool Detected, string? Version);

internal sealed record GitHubCopilotObservations(
    SetupPlanningOs PlanningOs,
    ChannelObservation VsCodeStable,
    ChannelObservation VsCodeInsiders,
    ChannelObservation CopilotCli,
    bool StableHasNonDefaultProfiles,
    bool InsidersHasNonDefaultProfiles);

internal enum GitHubCopilotVsCodeChannel
{
    Stable,
    Insiders,
}

internal static partial class GitHubCopilotDetection
{
    private const int MaximumVersionLength = 128;

    public static GitHubCopilotObservations Observe(ISetupPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var stable = ObserveChannel(platform.ProcessRunner, "code", ["--version"]);
        var stableHasProfiles = stable.Detected && HasNonDefaultProfiles(platform, GitHubCopilotVsCodeChannel.Stable);
        var insiders = ObserveChannel(platform.ProcessRunner, "code-insiders", ["--version"]);
        var insidersHasProfiles = insiders.Detected && HasNonDefaultProfiles(platform, GitHubCopilotVsCodeChannel.Insiders);
        var cli = ObserveChannel(platform.ProcessRunner, "copilot", ["version"]);

        return new GitHubCopilotObservations(
            platform.OperatingSystem.Current,
            stable,
            insiders,
            cli,
            stableHasProfiles,
            insidersHasProfiles);
    }

    internal static string GetDefaultSettingsPath(
        ISetupPlatform platform,
        GitHubCopilotVsCodeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return Combine(
            platform.OperatingSystem.Current,
            GetUserDataDirectory(platform, channel),
            "settings.json");
    }

    private static ChannelObservation ObserveChannel(
        ISetupProcessRunner processRunner,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        SetupProcessObservation observation;
        try
        {
            observation = processRunner.Run(fileName, arguments);
        }
        catch (Exception)
        {
            return new ChannelObservation(false, null);
        }

        return observation.Outcome == SetupProcessOutcome.Completed
            ? new ChannelObservation(true, SanitizeVersion(observation.StandardOutput))
            : new ChannelObservation(false, null);
    }

    private static string? SanitizeVersion(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        foreach (Match match in SemanticVersion().Matches(output))
        {
            var versionGroup = match.Groups["version"];
            if (!HasTokenBoundary(output, versionGroup.Index - 1) ||
                !HasTokenBoundary(output, versionGroup.Index + versionGroup.Length))
            {
                continue;
            }

            var version = versionGroup.Value;
            return version.Length <= MaximumVersionLength ? version : null;
        }

        return null;
    }

    private static bool HasTokenBoundary(string text, int index) =>
        index < 0 ||
        index >= text.Length ||
        !IsSemanticVersionCharacter(text[index]);

    private static bool IsSemanticVersionCharacter(char value) =>
        value is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            '-' or
            '.' or
            '+';

    private static bool HasNonDefaultProfiles(
        ISetupPlatform platform,
        GitHubCopilotVsCodeChannel channel)
    {
        try
        {
            var profilesDirectory = Combine(
                platform.OperatingSystem.Current,
                GetUserDataDirectory(platform, channel),
                "profiles");
            return platform.FileSystem.HasDirectories(profilesDirectory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GetUserDataDirectory(
        ISetupPlatform platform,
        GitHubCopilotVsCodeChannel channel)
    {
        var operatingSystem = platform.OperatingSystem;
        var productDirectory = channel == GitHubCopilotVsCodeChannel.Stable
            ? "Code"
            : "Code - Insiders";
        return operatingSystem.Current switch
        {
            SetupPlanningOs.Windows or SetupPlanningOs.MacOs =>
                Combine(
                    operatingSystem.Current,
                    RequireKnownFolder(operatingSystem.ApplicationData),
                    productDirectory,
                    "User"),
            SetupPlanningOs.Linux =>
                Combine(
                    operatingSystem.Current,
                    RequireKnownFolder(operatingSystem.UserProfile),
                    ".config",
                    productDirectory,
                    "User"),
            _ => throw new ArgumentOutOfRangeException(nameof(operatingSystem.Current)),
        };
    }

    private static string RequireKnownFolder(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("setup_known_folder_unavailable")
            : path;

    private static string Combine(SetupPlanningOs operatingSystem, params string[] components)
    {
        var separator = operatingSystem == SetupPlanningOs.Windows ? '\\' : '/';
        var result = components[0].TrimEnd('\\', '/');
        foreach (var component in components.Skip(1))
        {
            result += separator + component.Trim('\\', '/');
        }

        return result;
    }

    [GeneratedRegex(
        @"(?<version>(?:0|[1-9][0-9]{0,18})\.(?:0|[1-9][0-9]{0,18})\.(?:0|[1-9][0-9]{0,18})(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersion();
}

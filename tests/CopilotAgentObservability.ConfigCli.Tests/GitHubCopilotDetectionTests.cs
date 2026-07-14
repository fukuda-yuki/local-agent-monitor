using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDetectionTests
{
    [Fact]
    public void Observe_AllProcessesAbsent_ReportsNothingInstalled()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(SetupPlanningOs.Windows, observations.PlanningOs);
        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeStable);
        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeInsiders);
        Assert.Equal(new ChannelObservation(false, null), observations.CopilotCli);
        Assert.False(observations.StableHasNonDefaultProfiles);
        Assert.False(observations.InsidersHasNonDefaultProfiles);
        Assert.Equal(
        [
            "process.run:code:--version",
            "process.run:code-insiders:--version",
            "process.run:copilot:version",
        ],
        platform.Operations);
    }

    [Fact]
    public void Observe_StableOnly_SanitizesTheFirstSemanticVersion()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.128.2\r\n0123456789abcdef\r\nx64\r\n");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.128.2"), observations.VsCodeStable);
        Assert.False(observations.VsCodeInsiders.Detected);
        Assert.False(observations.CopilotCli.Detected);
    }

    [Fact]
    public void Observe_BothVsCodeChannels_ReportsBothInStableThenInsidersOrder()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.128.0");
        Complete(platform, "code-insiders", ["--version"], "1.129.0-insider.1");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.128.0"), observations.VsCodeStable);
        Assert.Equal(new ChannelObservation(true, "1.129.0-insider.1"), observations.VsCodeInsiders);
        Assert.Equal("process.run:code:--version", platform.Operations[0]);
        Assert.StartsWith("process.run:code-insiders:--version", platform.Operations[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_CopilotCliOnly_AcceptsTheDocumentedVersionCommandOutput()
    {
        var platform = CreatePlatform(SetupPlanningOs.Linux);
        Complete(platform, "copilot", ["version"], "GitHub Copilot CLI 1.0.4\n");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, "1.0.4"), observations.CopilotCli);
        Assert.False(observations.VsCodeStable.Detected);
        Assert.False(observations.VsCodeInsiders.Detected);
    }

    [Fact]
    public void Observe_MalformedCompletedVersion_ReportsDetectedWithNullVersion()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "raw-path-marker C:\\private\\code.exe");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, null), observations.VsCodeStable);
        Assert.DoesNotContain("raw-path-marker", observations.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_NearSemanticVersion_ReportsNullRatherThanAValidSubstring()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], "1.2.3.4");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(true, null), observations.VsCodeStable);
    }

    [Fact]
    public void Observe_ProcessNotFound_ReportsNotDetected()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        platform.ScriptProcess(
            "code",
            ["--version"],
            new SetupProcessObservation(SetupProcessOutcome.NotFound, null, string.Empty));

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(new ChannelObservation(false, null), observations.VsCodeStable);
    }

    [Fact]
    public void Observe_ProcessTimeout_ReportsNotDetectedWithoutThrowing()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        platform.ScriptProcess(
            "copilot",
            ["version"],
            new SetupProcessObservation(SetupProcessOutcome.TimedOut, null, string.Empty));

        GitHubCopilotObservations? observations = null;
        var exception = Record.Exception(() => observations = GitHubCopilotDetection.Observe(platform));

        Assert.Null(exception);
        Assert.False(observations!.CopilotCli.Detected);
    }

    [Fact]
    public void Observe_NonDefaultProfiles_ReportsPerChannelBooleansWithoutOpeningProfileFiles()
    {
        var platform = CreatePlatform(SetupPlanningOs.Linux);
        Complete(platform, "code", ["--version"], "1.128.0");
        Complete(platform, "code-insiders", ["--version"], "1.129.0");
        platform.SeedDirectory("/home/setup-test/.config/Code/User/profiles/stable-profile");
        platform.SeedDirectory("/home/setup-test/.config/Code - Insiders/User/profiles/insiders-profile");
        platform.SeedFile("/home/setup-test/.config/Code/User/profiles/stable-profile/settings.json", [1, 2, 3]);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.True(observations.StableHasNonDefaultProfiles);
        Assert.True(observations.InsidersHasNonDefaultProfiles);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.read", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.Contains("settings.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupPlanningOs.Windows)]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void Observe_PlanningOsFake_CapturesTheSelectedOperatingSystem(SetupPlanningOs planningOs)
    {
        var platform = CreatePlatform(planningOs);

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.Equal(planningOs, observations.PlanningOs);
    }

    [Fact]
    public void Observe_OverlongSemanticVersion_ReportsNullRatherThanTruncatedOutput()
    {
        var platform = CreatePlatform(SetupPlanningOs.Windows);
        Complete(platform, "code", ["--version"], $"1.128.0-{new string('a', 121)}");

        var observations = GitHubCopilotDetection.Observe(platform);

        Assert.True(observations.VsCodeStable.Detected);
        Assert.Null(observations.VsCodeStable.Version);
    }

    [Theory]
    [InlineData(SetupPlanningOs.Windows, false, "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json")]
    [InlineData(SetupPlanningOs.Windows, true, "C:\\Users\\setup-test\\AppData\\Roaming\\Code - Insiders\\User\\settings.json")]
    [InlineData(SetupPlanningOs.MacOs, false, "/Users/setup-test/Library/Application Support/Code/User/settings.json")]
    [InlineData(SetupPlanningOs.MacOs, true, "/Users/setup-test/Library/Application Support/Code - Insiders/User/settings.json")]
    [InlineData(SetupPlanningOs.Linux, false, "/home/setup-test/.config/Code/User/settings.json")]
    [InlineData(SetupPlanningOs.Linux, true, "/home/setup-test/.config/Code - Insiders/User/settings.json")]
    public void GetDefaultSettingsPath_ChannelAndOperatingSystem_ReturnsTheDocumentedDefaultProfilePath(
        SetupPlanningOs planningOs,
        bool insiders,
        string expected)
    {
        var platform = CreatePlatform(planningOs);

        var path = GitHubCopilotDetection.GetDefaultSettingsPath(
            platform,
            insiders ? GitHubCopilotVsCodeChannel.Insiders : GitHubCopilotVsCodeChannel.Stable);

        Assert.Equal(expected, path);
    }

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            pathStyle: SetupPathStyle.Windows,
            planningOs: planningOs,
            applicationData: "C:\\Users\\setup-test\\AppData\\Roaming",
            userProfile: "C:\\Users\\setup-test"),
        SetupPlanningOs.MacOs => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            localApplicationData: "/Users/setup-test/Library/Application Support",
            pathStyle: SetupPathStyle.Unix,
            planningOs: planningOs,
            applicationData: "/Users/setup-test/Library/Application Support",
            userProfile: "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(
            DateTimeOffset.UnixEpoch,
            localApplicationData: "/home/setup-test/.local/share",
            pathStyle: SetupPathStyle.Unix,
            planningOs: planningOs,
            applicationData: "/home/setup-test/.config",
            userProfile: "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static void Complete(
        SetupTestPlatform platform,
        string fileName,
        IReadOnlyList<string> arguments,
        string output) =>
        platform.ScriptProcess(
            fileName,
            arguments,
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, output));
}

using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    [Theory]
    [InlineData(SetupPlanningOs.Windows)]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void AdapterPlan_AppSdkWithWslRoutingOption_ReturnsInvalidArgumentsBeforePlatformActivity(
        SetupPlanningOs planningOs)
    {
        var platform = PlatformFor(planningOs);
        var request = Request("app-sdk", includeContentCapture: false) with
        {
            AllowWsl2Routing = true,
        };

        var result = CreateAdapter(platform).Plan(request);

        var failure = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(result);
        Assert.Equal(SetupCodes.InvalidArguments, failure.Code);
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void AdapterPlan_VerifiedWslWithReleaseHookCommand_FailsClosedBeforeTargetActivity()
    {
        var platform = PlatformFor(SetupPlanningOs.Linux);
        platform.SeedProcessEnvironment("WSL_DISTRO_NAME", "Ubuntu");
        platform.ScriptProcess(
            "uname",
            ["-r"],
            new(SetupProcessOutcome.Completed, 0, "6.6.0-microsoft-standard-WSL2"));
        ScriptVersionAndReadiness(platform);
        var adapter = new ClaudeCodeSetupAdapter(
            platform,
            new ClaudeAgentSdkTargetPartition(),
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand(
                    "/release/app/CopilotAgentObservability.LocalMonitor.exe",
                    [],
                    ClaudeHookCommandMode.Release)),
            new ClaudeHigherPrecedenceObserver(
                platform,
                "/synthetic-repository",
                "/etc/claude-code/managed-settings.json"));
        var request = Request("cli", includeContentCapture: false) with
        {
            AllowWsl2Routing = true,
        };

        var result = adapter.Plan(request);

        var failure = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(result);
        Assert.Equal(SetupCodes.InternalError, failure.Code);
        Assert.Contains("process-environment.get:WSL_DISTRO_NAME", platform.Operations);
        Assert.Contains("process.run:uname:-r", platform.Operations);
        Assert.Contains(platform.Operations, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.StartsWith("file.", StringComparison.Ordinal) ||
            operation.StartsWith("managed-settings.", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterPlan_VerifiedWslWithRepositoryHookCommand_IsAccepted()
    {
        var platform = PlatformFor(SetupPlanningOs.Linux);
        platform.SeedProcessEnvironment("WSL_DISTRO_NAME", "Ubuntu");
        platform.ScriptProcess(
            "uname",
            ["-r"],
            new(SetupProcessOutcome.Completed, 0, "6.6.0-microsoft-standard-WSL2"));
        ScriptVersionAndReadiness(platform);
        var request = Request("cli", includeContentCapture: false) with
        {
            AllowWsl2Routing = true,
        };

        var result = CreateWslAdapter(platform).Plan(request);

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var desired = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(
            Assert.Single(success.Value.Records).DesiredState);
        Assert.All(desired.OwnedHooks, hook => Assert.Equal("dotnet", hook.Command));
    }

    [Fact]
    public void AdapterPlan_AllWithWslRoutingOption_UsesCliExecutionContextValidation()
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        var request = Request("all", includeContentCapture: false) with
        {
            AllowWsl2Routing = true,
        };

        var result = CreateAdapter(platform).Plan(request);

        var failure = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(result);
        Assert.Equal(SetupCodes.InvalidArguments, failure.Code);
        Assert.Equal(["process.run:claude:--version"], platform.Operations);
    }

    [Fact]
    public void AdapterPlan_WindowsNativeWithReleaseHookCommand_IsAccepted()
    {
        var platform = ReadyWindowsPlatform("{}\n");
        var adapter = new ClaudeCodeSetupAdapter(
            platform,
            new ClaudeAgentSdkTargetPartition(),
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand(
                    "C:\\release\\app\\CopilotAgentObservability.LocalMonitor.exe",
                    [],
                    ClaudeHookCommandMode.Release)),
            new ClaudeHigherPrecedenceObserver(
                platform,
                "C:\\synthetic-repository",
                "C:\\Program Files\\ClaudeCode\\managed-settings.json"));

        var result = adapter.Plan(Request("cli", includeContentCapture: false));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var desired = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(
            Assert.Single(success.Value.Records).DesiredState);
        Assert.All(
            desired.OwnedHooks,
            hook => Assert.Equal(
                "C:\\release\\app\\CopilotAgentObservability.LocalMonitor.exe",
                hook.Command));
    }

    private static SetupTestPlatform PlatformFor(SetupPlanningOs planningOs) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(AdapterTimestamp),
        SetupPlanningOs.MacOs => new SetupTestPlatform(
            AdapterTimestamp,
            "/Users/setup-test/Library/Application Support",
            SetupPathStyle.Unix,
            planningOs,
            "/Users/setup-test/Library/Application Support",
            "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(
            AdapterTimestamp,
            "/home/setup-test/.local/share",
            SetupPathStyle.Unix,
            planningOs,
            "/home/setup-test/.config",
            "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };
}

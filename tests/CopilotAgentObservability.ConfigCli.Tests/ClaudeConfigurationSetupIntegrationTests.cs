using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupPhysicalProcessCollection))]
public sealed class ClaudeConfigurationSetupIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void Plan_ClaudeCli_ProductionCompositionProjectsSetupV1()
    {
        var platform = new SetupTestPlatform(Timestamp);
        var parsed = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "claude-code",
            "--target",
            "cli",
            "--endpoint",
            "http://127.0.0.1:4320",
        ]);
        var options = Assert.IsType<SetupOptions>(parsed.Options);

        var result = SetupCompositionRoot.CreateSetupDispatch(platform)(options);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.Equal("claude-code", root.GetProperty("adapter").GetString());
        Assert.Equal(SetupCodes.TargetNotInstalled, root.GetProperty("code").GetString());
    }

    [Fact]
    public void Parse_ClaudeWslOptIn_AcceptsPublicFlag()
    {
        var parsed = SetupOptions.Parse(ClaudePlanArguments);

        Assert.Null(parsed.Code);
        var options = Assert.IsType<SetupOptions>(parsed.Options);
        var property = options.GetType().GetProperty(
            "AllowWsl2Routing",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.True(Assert.IsType<bool>(property.GetValue(options)));
    }

    [Fact]
    public async Task RepositorySetupWrapper_ForwardsClaudeWslOptInByteForByte()
    {
        var direct = await RunConfigCliAsync(DuplicateWslOptInActionArguments);
        var wrapper = await RunWrapperAsync(DuplicateWslOptInActionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
        using var document = JsonDocument.Parse(wrapper.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("invalid_arguments", root.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("adapter").ValueKind);
    }

    private static string[] ClaudePlanArguments => ["setup", .. ClaudeActionArguments];

    private static string[] ClaudeActionArguments =>
    [
        "plan",
        "--adapter",
        "claude-code",
        "--target",
        "cli",
        "--endpoint",
        "http://127.0.0.1:4320",
        "--allow-wsl2-routing",
    ];

    private static string[] DuplicateWslOptInActionArguments =>
    [
        .. ClaudeActionArguments,
        "--allow-wsl2-routing",
    ];

    private static Task<SetupPhysicalProcessResult> RunConfigCliAsync(IEnumerable<string> actionArguments)
    {
        var arguments = new List<string>
        {
            "run",
            "--verbosity",
            "quiet",
            "--project",
            ConfigCliProjectPath,
            "--",
            "setup",
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("dotnet", RepositoryRoot, arguments);
    }

    private static Task<SetupPhysicalProcessResult> RunWrapperAsync(IEnumerable<string> actionArguments)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-File",
            SetupScriptPath,
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("pwsh", RepositoryRoot, arguments);
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string ConfigCliProjectPath => Path.Combine(
        RepositoryRoot,
        "src",
        "CopilotAgentObservability.ConfigCli",
        "CopilotAgentObservability.ConfigCli.csproj");

    private static string SetupScriptPath => Path.Combine(
        RepositoryRoot,
        "scripts",
        "local-monitor",
        "setup.ps1");
}

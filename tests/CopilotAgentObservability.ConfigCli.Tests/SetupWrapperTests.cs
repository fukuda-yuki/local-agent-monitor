using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupPhysicalProcessCollection))]
public sealed class SetupWrapperTests
{
    [Fact]
    public async Task SetupWrapper_ForwardsPreDispatchStatusFailureByteForByte()
    {
        string[] actionArguments = ["status", "--adapter"];
        AssertRejectedBeforeDispatch(actionArguments);

        var direct = await RunConfigCliAsync(actionArguments);
        var wrapper = await RunWrapperAsync(actionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal("invalid_arguments", ReadResultCode(direct.StandardOutput));
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
    }

    [Fact]
    public async Task SetupWrapper_ForwardsFailureStderrAndExitCode()
    {
        string[] actionArguments = ["apply"];
        AssertRejectedBeforeDispatch(actionArguments);

        var direct = await RunConfigCliAsync(actionArguments);
        var wrapper = await RunWrapperAsync(actionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
        Assert.Equal("invalid_arguments", ReadResultCode(wrapper.StandardOutput));
    }

    [Fact]
    public async Task SetupWrapper_BareSetupProducesNoStdout()
    {
        var wrapper = await RunWrapperAsync();

        Assert.Equal(2, wrapper.ExitCode);
        Assert.Empty(wrapper.StandardOutput);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), wrapper.StandardError);
    }

    [Theory]
    [MemberData(nameof(AdditionalRecognizedActionArguments))]
    public async Task SetupWrapper_ForwardsPreDispatchPlanAndRollbackFailures(string[] actionArguments)
    {
        AssertRejectedBeforeDispatch(actionArguments);

        var direct = await RunConfigCliAsync(actionArguments);
        var wrapper = await RunWrapperAsync(actionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal("invalid_arguments", ReadResultCode(direct.StandardOutput));
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
    }

    public static TheoryData<string[]> AdditionalRecognizedActionArguments => new()
    {
        { ["plan", "--adapter", "github-copilot"] },
        { ["rollback", "--change-set"] },
    };

    private static Task<SetupPhysicalProcessResult> RunConfigCliAsync(params string[] actionArguments)
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

    private static Task<SetupPhysicalProcessResult> RunWrapperAsync(params string[] actionArguments)
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

    private static void AssertRejectedBeforeDispatch(string[] actionArguments)
    {
        var parseResult = SetupOptions.Parse(["setup", .. actionArguments]);

        Assert.Null(parseResult.Options);
        Assert.Equal(SetupCodes.InvalidArguments, parseResult.Code);
    }

    private static string ReadResultCode(byte[] output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("code").GetString()!;
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

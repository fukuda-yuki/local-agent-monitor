using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupPhysicalProcessCollection))]
public sealed class SetupWrapperTests
{
    [Fact]
    public async Task SetupWrapper_ForwardsProductionStatusStdoutByteForByte()
    {
        using var runtime = new TemporaryRuntimeRoot();

        var direct = await RunConfigCliAsync(runtime.Path, "status");
        var wrapper = await RunWrapperAsync(runtime.Path, "status");

        Assert.Equal(0, direct.ExitCode);
        Assert.Equal("status_ready", ReadResultCode(direct.StandardOutput));
        Assert.Empty(direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
    }

    [Fact]
    public async Task SetupWrapper_ForwardsFailureStderrAndExitCode()
    {
        using var runtime = new TemporaryRuntimeRoot();

        var direct = await RunConfigCliAsync(runtime.Path, "apply");
        var wrapper = await RunWrapperAsync(runtime.Path, "apply");

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
        using var runtime = new TemporaryRuntimeRoot();

        var wrapper = await RunWrapperAsync(runtime.Path);

        Assert.Equal(2, wrapper.ExitCode);
        Assert.Empty(wrapper.StandardOutput);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), wrapper.StandardError);
    }

    [Theory]
    [MemberData(nameof(AdditionalRecognizedActionArguments))]
    public async Task SetupWrapper_ForwardsPlanAndRollbackActionNames(string[] actionArguments)
    {
        using var runtime = new TemporaryRuntimeRoot();

        var direct = await RunConfigCliAsync(runtime.Path, actionArguments);
        var wrapper = await RunWrapperAsync(runtime.Path, actionArguments);

        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
    }

    public static TheoryData<string[]> AdditionalRecognizedActionArguments => new()
    {
        { ["plan", "--adapter", "github-copilot", "--target", "vscode"] },
        { ["rollback", "--change-set", "00000000-0000-7000-8000-000000000001"] },
    };

    private static Task<SetupPhysicalProcessResult> RunConfigCliAsync(string runtimeRoot, params string[] actionArguments)
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
        return SetupPhysicalProcessRunner.RunAsync("dotnet", RepositoryRoot, runtimeRoot, arguments);
    }

    private static Task<SetupPhysicalProcessResult> RunWrapperAsync(string runtimeRoot, params string[] actionArguments)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-File",
            SetupScriptPath,
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("pwsh", RepositoryRoot, runtimeRoot, arguments);
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

    private sealed class TemporaryRuntimeRoot : IDisposable
    {
        public TemporaryRuntimeRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-setup-wrapper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

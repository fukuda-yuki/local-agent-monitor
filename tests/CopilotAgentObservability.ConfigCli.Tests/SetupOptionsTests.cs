using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class SetupOptionsTests
{
    [Fact]
    public void Parse_Plan_NormalizesTheDocumentedOptions()
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            "all",
            "--endpoint",
            "HTTP://LOCALHOST:4320/",
            "--include-content-capture",
        ]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal(SetupCommand.Plan, options.Command);
        Assert.Equal("github-copilot", options.Adapter);
        Assert.Equal("all", options.Target);
        Assert.Equal("http://localhost:4320", options.Endpoint);
        Assert.True(options.IncludeContentCapture);
        Assert.Null(options.ChangeSetId);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4320", "http://127.0.0.1:4320")]
    [InlineData("http://localhost:4320/", "http://localhost:4320")]
    [InlineData("HTTP://[::1]:4320/", "http://[::1]:4320")]
    public void Parse_Plan_NormalizesAllowedLoopbackEndpoints(string input, string expected)
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            "vscode",
            "--endpoint",
            input,
        ]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal(expected, options.Endpoint);
    }

    [Fact]
    public void Parse_Plan_UsesTheDocumentedDefaultEndpoint()
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            "cli",
        ]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal("http://127.0.0.1:4320", options.Endpoint);
        Assert.False(options.IncludeContentCapture);
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("cli")]
    [InlineData("app-sdk")]
    [InlineData("all")]
    public void Parse_Plan_AcceptsEveryDocumentedTarget(string target)
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            target,
        ]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal(target, options.Target);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public void Parse_ChangeSetCommand_AcceptsCanonicalUuidV7(string command)
    {
        var changeSetId = "018f3b9a-0000-7000-8000-000000000001";

        var result = SetupOptions.Parse(["setup", command, "--change-set", changeSetId]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal(command == "apply" ? SetupCommand.Apply : SetupCommand.Rollback, options.Command);
        Assert.Equal(Guid.Parse(changeSetId), options.ChangeSetId);
        Assert.Null(options.Adapter);
        Assert.Null(options.Target);
        Assert.Null(options.Endpoint);
        Assert.False(options.IncludeContentCapture);
    }

    [Fact]
    public void Parse_Status_AcceptsItsOptionalAdapterFilter()
    {
        var result = SetupOptions.Parse(["setup", "status", "--adapter", "github-copilot"]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Equal(SetupCommand.Status, options.Command);
        Assert.Equal("github-copilot", options.Adapter);
        Assert.Null(options.Target);
        Assert.Null(options.Endpoint);
        Assert.Null(options.ChangeSetId);
    }

    [Fact]
    public void Parse_Status_AllowsNoAdapterFilter()
    {
        var result = SetupOptions.Parse(["setup", "status"]);

        var options = Assert.IsType<SetupOptions>(result.Options);
        Assert.Null(result.Code);
        Assert.Null(options.Adapter);
    }

    [Theory]
    [InlineData("plan", "--adapter")]
    [InlineData("plan", "--target")]
    [InlineData("plan", "--endpoint")]
    [InlineData("apply", "--change-set")]
    [InlineData("rollback", "--change-set")]
    [InlineData("status", "--adapter")]
    public void Parse_ReturnsInvalidArgumentsForMissingOptionValues(string command, string option)
    {
        var result = SetupOptions.Parse(["setup", command, option]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
    }

    [Theory]
    [InlineData("plan", "--adapter", "github-copilot", "--adapter", "github-copilot")]
    [InlineData("plan", "--target", "vscode", "--target", "vscode")]
    [InlineData("plan", "--endpoint", "http://127.0.0.1:4320", "--endpoint", "http://127.0.0.1:4321")]
    [InlineData("plan", "--include-content-capture", "--include-content-capture")]
    [InlineData("apply", "--change-set", "018f3b9a-0000-7000-8000-000000000001", "--change-set", "018f3b9a-0000-7000-8000-000000000002")]
    [InlineData("rollback", "--change-set", "018f3b9a-0000-7000-8000-000000000001", "--change-set", "018f3b9a-0000-7000-8000-000000000002")]
    [InlineData("status", "--adapter", "github-copilot", "--adapter", "github-copilot")]
    public void Parse_ReturnsInvalidArgumentsForDuplicateOptions(string command, params string[] options)
    {
        var result = SetupOptions.Parse(["setup", command, .. options]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
    }

    [Theory]
    [InlineData("plan", "--change-set")]
    [InlineData("apply", "--adapter")]
    [InlineData("rollback", "--target")]
    [InlineData("status", "--include-content-capture")]
    [InlineData("plan", "--unexpected")]
    public void Parse_ReturnsInvalidArgumentsForUnknownOrMutuallyInvalidOptions(string command, string option)
    {
        var result = SetupOptions.Parse(["setup", command, option, "untrusted-value"]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
    }

    [Theory]
    [InlineData("plan", "--adapter", "github-copilot")]
    [InlineData("plan", "--target", "vscode")]
    [InlineData("apply")]
    [InlineData("rollback")]
    public void Parse_ReturnsInvalidArgumentsForRequiredOptionsThatAreMissing(string command, params string[] options)
    {
        var result = SetupOptions.Parse(["setup", command, .. options]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
    }

    [Theory]
    [InlineData("plan", "--adapter", "other")]
    [InlineData("status", "--adapter", "other")]
    public void Parse_ReturnsUnsupportedAdapterWithoutEchoingTheValue(string command, string option, string value)
    {
        var result = SetupOptions.Parse(command == "plan"
            ? ["setup", command, option, value, "--target", "vscode"]
            : ["setup", command, option, value]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.UnsupportedAdapter, result.Code);
        Assert.DoesNotContain(value, result.Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("all-targets")]
    public void Parse_ReturnsUnsupportedTargetWithoutEchoingTheValue(string target)
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            target,
        ]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.DoesNotContain(target, result.Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost:4320")]
    [InlineData("http://example.test:4320")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:4320/v1/traces")]
    [InlineData("http://localhost:4320?token=secret")]
    [InlineData("http://user:secret@localhost:4320")]
    public void Parse_ReturnsInvalidArgumentsForNonCanonicalOrNonLoopbackEndpointWithoutEchoingIt(string endpoint)
    {
        var result = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "github-copilot",
            "--target",
            "vscode",
            "--endpoint",
            endpoint,
        ]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
        Assert.DoesNotContain(endpoint, result.Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("018F3B9A-0000-7000-8000-000000000001")]
    [InlineData("018f3b9a-0000-6000-8000-000000000001")]
    [InlineData("018f3b9a-0000-7000-7000-000000000001")]
    [InlineData("not-a-change-set")]
    public void Parse_ReturnsInvalidArgumentsForNonCanonicalOrNonV7ChangeSetIdWithoutEchoingIt(string changeSetId)
    {
        var result = SetupOptions.Parse(["setup", "apply", "--change-set", changeSetId]);

        Assert.Null(result.Options);
        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
        Assert.DoesNotContain(changeSetId, result.Code, StringComparison.Ordinal);
    }
}

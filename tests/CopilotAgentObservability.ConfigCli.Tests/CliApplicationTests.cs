using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class CliApplicationTests
{
    [Fact]
    public void Run_VsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("github.copilot.chat.otel.otlpEndpoint", output.ToString());
        Assert.Contains("http://localhost:3000/api/public/otel", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseVsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("http://localhost:3000/api/public/otel", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorVsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("http://localhost:4318", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", output.ToString());
        Assert.Contains("$env:OTEL_RESOURCE_ATTRIBUTES", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseCopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorCopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:4318\"", output.ToString());
        Assert.DoesNotContain("Authorization=Basic", output.ToString());
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", output.ToString());
        Assert.Contains("client.kind=vscode-copilot-chat", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseVsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorVsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:4318\"", output.ToString());
        Assert.DoesNotContain("Authorization=Basic", output.ToString());
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeFileSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-file-settings", "tmp/copilot-chat-otel.jsonl"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"github.copilot.chat.otel.exporterType\": \"file\"", output.ToString());
        Assert.Contains("\"github.copilot.chat.otel.outfile\": \"tmp/copilot-chat-otel.jsonl\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeFileSettings_ReturnsNonZeroWithoutOutfile()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-file-settings"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires exactly one output file path", error.ToString());
    }

    [Fact]
    public void Run_ValidateResourceAttributes_ReturnsNonZeroForMissingAttributes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["validate-resource-attributes", "client.kind=copilot-cli"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("missing required resource attribute", error.ToString());
    }
}

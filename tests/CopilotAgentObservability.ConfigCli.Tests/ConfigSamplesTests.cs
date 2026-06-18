using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class ConfigSamplesTests
{
    [Fact]
    public void CreateVsCodeSettingsJson_IncludesDefaultLangfuseSettings()
    {
        using var document = JsonDocument.Parse(ConfigSamples.CreateVsCodeSettingsJson());
        var root = document.RootElement;

        Assert.True(root.GetProperty("github.copilot.chat.otel.enabled").GetBoolean());
        Assert.Equal("otlp-http", root.GetProperty("github.copilot.chat.otel.exporterType").GetString());
        Assert.Equal("http://localhost:3000/api/public/otel", root.GetProperty("github.copilot.chat.otel.otlpEndpoint").GetString());
        Assert.True(root.GetProperty("github.copilot.chat.otel.captureContent").GetBoolean());
    }

    [Fact]
    public void CreateLangfuseVsCodeSettingsJson_IncludesLangfuseSettings()
    {
        using var document = JsonDocument.Parse(ConfigSamples.CreateLangfuseVsCodeSettingsJson());
        var root = document.RootElement;

        Assert.True(root.GetProperty("github.copilot.chat.otel.enabled").GetBoolean());
        Assert.Equal("otlp-http", root.GetProperty("github.copilot.chat.otel.exporterType").GetString());
        Assert.Equal("http://localhost:3000/api/public/otel", root.GetProperty("github.copilot.chat.otel.otlpEndpoint").GetString());
        Assert.True(root.GetProperty("github.copilot.chat.otel.captureContent").GetBoolean());
    }

    [Fact]
    public void CreateCollectorVsCodeSettingsJson_IncludesCollectorSettings()
    {
        using var document = JsonDocument.Parse(ConfigSamples.CreateCollectorVsCodeSettingsJson());
        var root = document.RootElement;

        Assert.True(root.GetProperty("github.copilot.chat.otel.enabled").GetBoolean());
        Assert.Equal("otlp-http", root.GetProperty("github.copilot.chat.otel.exporterType").GetString());
        Assert.Equal("http://localhost:4318", root.GetProperty("github.copilot.chat.otel.otlpEndpoint").GetString());
        Assert.True(root.GetProperty("github.copilot.chat.otel.captureContent").GetBoolean());
    }

    [Fact]
    public void CreateVsCodePowerShellScript_IncludesDefaultLangfuseEnvironmentVariables()
    {
        var script = ConfigSamples.CreateVsCodePowerShellScript();

        Assert.Contains("$publicKey = \"<public-key>\"", script);
        Assert.Contains("$secretKey = \"<secret-key>\"", script);
        Assert.Contains("[Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\")", script);
        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", script);
        Assert.Contains("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("user.id=example-user", script);
        Assert.Contains("user.email=user@example.com", script);
        Assert.Contains("team.id=platform", script);
        Assert.Contains("department=engineering", script);
        Assert.Contains("client.kind=vscode-copilot-chat", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateLangfuseVsCodePowerShellScript_IncludesLangfuseEnvironmentVariables()
    {
        var script = ConfigSamples.CreateLangfuseVsCodePowerShellScript();

        Assert.Contains("$publicKey = \"<public-key>\"", script);
        Assert.Contains("$secretKey = \"<secret-key>\"", script);
        Assert.Contains("[Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\")", script);
        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("client.kind=vscode-copilot-chat", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateCollectorVsCodePowerShellScript_IncludesCollectorEnvironmentVariables()
    {
        var script = ConfigSamples.CreateCollectorVsCodePowerShellScript();

        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:4318\"", script);
        Assert.Contains("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"", script);
        Assert.DoesNotContain("Authorization=Basic", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue", script);
        Assert.Contains("client.kind=vscode-copilot-chat", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateVsCodeFileSettingsJson_IncludesFileExporterSettings()
    {
        using var document = JsonDocument.Parse(ConfigSamples.CreateVsCodeFileSettingsJson("tmp/copilot-chat-otel.jsonl"));
        var root = document.RootElement;

        Assert.True(root.GetProperty("github.copilot.chat.otel.enabled").GetBoolean());
        Assert.Equal("file", root.GetProperty("github.copilot.chat.otel.exporterType").GetString());
        Assert.Equal("tmp/copilot-chat-otel.jsonl", root.GetProperty("github.copilot.chat.otel.outfile").GetString());
        Assert.True(root.GetProperty("github.copilot.chat.otel.captureContent").GetBoolean());
    }

    [Fact]
    public void CreateCopilotCliPowerShellScript_IncludesDefaultLangfuseEnvironmentVariables()
    {
        var script = ConfigSamples.CreateCopilotCliPowerShellScript();

        Assert.Contains("$publicKey = \"<public-key>\"", script);
        Assert.Contains("$secretKey = \"<secret-key>\"", script);
        Assert.Contains("[Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\")", script);
        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"", script);
        Assert.Contains("user.id=example-user", script);
        Assert.Contains("user.email=user@example.com", script);
        Assert.Contains("team.id=platform", script);
        Assert.Contains("department=engineering", script);
        Assert.Contains("client.kind=copilot-cli", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateLangfuseCopilotCliPowerShellScript_IncludesLangfuseEnvironmentVariables()
    {
        var script = ConfigSamples.CreateLangfuseCopilotCliPowerShellScript();

        Assert.Contains("$publicKey = \"<public-key>\"", script);
        Assert.Contains("$secretKey = \"<secret-key>\"", script);
        Assert.Contains("[Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\")", script);
        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", script);
        Assert.Contains("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"", script);
        Assert.Contains("client.kind=copilot-cli", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateCollectorCopilotCliPowerShellScript_IncludesCollectorEnvironmentVariables()
    {
        var script = ConfigSamples.CreateCollectorCopilotCliPowerShellScript();

        Assert.Contains("$env:COPILOT_OTEL_ENABLED=\"true\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:4318\"", script);
        Assert.Contains("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"", script);
        Assert.DoesNotContain("Authorization=Basic", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue", script);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue", script);
        Assert.Contains("client.kind=copilot-cli", script);
        Assert.Contains("experiment.id=baseline", script);
    }

    [Fact]
    public void CreateLangfuseCodexAppConfigToml_IncludesLangfuseOtelConfig()
    {
        var config = ConfigSamples.CreateLangfuseCodexAppConfigToml();

        Assert.Contains("[otel]", config);
        Assert.Contains("environment = \"dev\"", config);
        Assert.Contains("log_user_prompt = false", config);
        Assert.Contains("endpoint = \"http://localhost:3000/api/public/otel/v1/logs\"", config);
        Assert.Contains("endpoint = \"http://localhost:3000/api/public/otel/v1/metrics\"", config);
        Assert.Contains("endpoint = \"http://localhost:3000/api/public/otel/v1/traces\"", config);
        Assert.Contains("protocol = \"binary\"", config);
        Assert.Contains("Authorization = \"Basic <base64-public-secret>\"", config);
        Assert.Contains("\"x-langfuse-ingestion-version\" = \"4\"", config);
    }

    [Fact]
    public void CreateCollectorCodexAppConfigToml_IncludesCollectorOtelConfigWithoutLangfuseHeaders()
    {
        var config = ConfigSamples.CreateCollectorCodexAppConfigToml();

        Assert.Contains("[otel]", config);
        Assert.Contains("environment = \"dev\"", config);
        Assert.Contains("log_user_prompt = false", config);
        Assert.Contains("endpoint = \"http://localhost:4318/v1/logs\"", config);
        Assert.Contains("endpoint = \"http://localhost:4318/v1/metrics\"", config);
        Assert.Contains("endpoint = \"http://localhost:4318/v1/traces\"", config);
        Assert.Contains("protocol = \"binary\"", config);
        Assert.DoesNotContain("Authorization = \"Basic", config);
        Assert.DoesNotContain("x-langfuse-ingestion-version", config);
    }
}

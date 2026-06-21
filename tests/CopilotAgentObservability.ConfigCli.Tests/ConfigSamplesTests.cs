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

    [Theory]
    [InlineData(CollectionProfileOptions.RawOnly, "raw-only uses saved raw OTLP JSON")]
    [InlineData(CollectionProfileOptions.DockerDesktopLangfuse, "http://localhost:3000/api/public/otel")]
    [InlineData(CollectionProfileOptions.DockerDesktopCollectorLangfuse, "http://localhost:4318")]
    [InlineData(CollectionProfileOptions.Wsl2DockerLangfuse, "http://<windows-reachable-wsl2-host>:3000/api/public/otel")]
    [InlineData(CollectionProfileOptions.Wsl2DockerCollectorLangfuse, "http://<windows-reachable-wsl2-host>:4318")]
    [InlineData(CollectionProfileOptions.RemoteManagedLangfuse, "https://<langfuse-host>/api/public/otel")]
    [InlineData(CollectionProfileOptions.RemoteManagedCollector, "https://<collector-host>")]
    public void CreateProfileVsCodePowerShellScript_GeneratesProfileOutput(string profile, string expected)
    {
        var script = ConfigSamples.CreateProfileVsCodePowerShellScript(profile);

        Assert.Contains($"$env:CAO_COLLECTION_PROFILE=\"{profile}\"", script);
        Assert.Contains(expected, script);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.RawOnly, "raw-only uses saved raw OTLP JSON")]
    [InlineData(CollectionProfileOptions.DockerDesktopLangfuse, "http://localhost:3000/api/public/otel")]
    [InlineData(CollectionProfileOptions.DockerDesktopCollectorLangfuse, "http://localhost:4318")]
    [InlineData(CollectionProfileOptions.Wsl2DockerLangfuse, "http://<windows-reachable-wsl2-host>:3000/api/public/otel")]
    [InlineData(CollectionProfileOptions.Wsl2DockerCollectorLangfuse, "http://<windows-reachable-wsl2-host>:4318")]
    [InlineData(CollectionProfileOptions.RemoteManagedLangfuse, "https://<langfuse-host>/api/public/otel")]
    [InlineData(CollectionProfileOptions.RemoteManagedCollector, "https://<collector-host>")]
    public void CreateProfileCopilotCliPowerShellScript_GeneratesProfileOutput(string profile, string expected)
    {
        var script = ConfigSamples.CreateProfileCopilotCliPowerShellScript(profile);

        Assert.Contains($"$env:CAO_COLLECTION_PROFILE=\"{profile}\"", script);
        Assert.Contains(expected, script);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.RawOnly, "raw-only uses saved raw OTLP JSON")]
    [InlineData(CollectionProfileOptions.DockerDesktopLangfuse, "http://localhost:3000/api/public/otel/v1/traces")]
    [InlineData(CollectionProfileOptions.DockerDesktopCollectorLangfuse, "http://localhost:4318/v1/traces")]
    [InlineData(CollectionProfileOptions.Wsl2DockerLangfuse, "http://<windows-reachable-wsl2-host>:3000/api/public/otel/v1/traces")]
    [InlineData(CollectionProfileOptions.Wsl2DockerCollectorLangfuse, "http://<windows-reachable-wsl2-host>:4318/v1/traces")]
    [InlineData(CollectionProfileOptions.RemoteManagedLangfuse, "https://<langfuse-host>/api/public/otel/v1/traces")]
    [InlineData(CollectionProfileOptions.RemoteManagedCollector, "https://<collector-host>/v1/traces")]
    public void CreateProfileCodexAppConfigToml_GeneratesProfileOutput(string profile, string expected)
    {
        var config = ConfigSamples.CreateProfileCodexAppConfigToml(profile);

        Assert.Contains($"CAO_COLLECTION_PROFILE={profile}", config);
        Assert.Contains(expected, config);
    }

    [Fact]
    public void CreateProfilePowerShellScripts_ForRawOnlyClearLiveTelemetryEnvironmentVariables()
    {
        var vscodeScript = ConfigSamples.CreateProfileVsCodePowerShellScript(CollectionProfileOptions.RawOnly);
        var copilotCliScript = ConfigSamples.CreateProfileCopilotCliPowerShellScript(CollectionProfileOptions.RawOnly);

        Assert.Contains("Remove-Item Env:COPILOT_OTEL_ENABLED -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:COPILOT_OTEL_ENDPOINT -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:COPILOT_OTEL_CAPTURE_CONTENT -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_ENDPOINT -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue", vscodeScript);
        Assert.Contains("Remove-Item Env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT -ErrorAction SilentlyContinue", vscodeScript);

        Assert.Contains("Remove-Item Env:COPILOT_OTEL_ENABLED -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:COPILOT_OTEL_ENDPOINT -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:COPILOT_OTEL_CAPTURE_CONTENT -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_ENDPOINT -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue", copilotCliScript);
        Assert.Contains("Remove-Item Env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT -ErrorAction SilentlyContinue", copilotCliScript);
    }

    [Fact]
    public void CreateProfileCodexAppConfigToml_ForRawOnlyTellsUserToRemoveActiveOtelRouting()
    {
        var config = ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RawOnly);

        Assert.Contains("Remove or omit active [otel] routing entries from user-level ~/.codex/config.toml.", config);
        Assert.DoesNotContain("otlp-http", config);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.Wsl2DockerLangfuse)]
    [InlineData(CollectionProfileOptions.Wsl2DockerCollectorLangfuse)]
    public void CreateProfilePowerShellScripts_ForWsl2ProfilesDocumentWindowsReachableEndpoint(string profile)
    {
        var vscodeScript = ConfigSamples.CreateProfileVsCodePowerShellScript(profile);
        var copilotCliScript = ConfigSamples.CreateProfileCopilotCliPowerShellScript(profile);

        Assert.Contains("<windows-reachable-wsl2-host>", vscodeScript);
        Assert.Contains("Prefer localhost when WSL2 localhost forwarding exposes published container ports to Windows.", vscodeScript);
        Assert.Contains("machine-specific IP addresses out of repository files", vscodeScript);
        Assert.Contains("<windows-reachable-wsl2-host>", copilotCliScript);
        Assert.Contains("Prefer localhost when WSL2 localhost forwarding exposes published container ports to Windows.", copilotCliScript);
        Assert.Contains("machine-specific IP addresses out of repository files", copilotCliScript);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.Wsl2DockerLangfuse)]
    [InlineData(CollectionProfileOptions.Wsl2DockerCollectorLangfuse)]
    public void CreateProfileCodexAppConfigToml_ForWsl2ProfilesDocumentsWindowsReachableEndpoint(string profile)
    {
        var config = ConfigSamples.CreateProfileCodexAppConfigToml(profile);

        Assert.Contains("<windows-reachable-wsl2-host>", config);
        Assert.Contains("Prefer localhost when WSL2 localhost forwarding exposes published container ports to Windows.", config);
        Assert.Contains("machine-specific IP addresses out of repository files", config);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.RemoteManagedLangfuse)]
    [InlineData(CollectionProfileOptions.RemoteManagedCollector)]
    public void CreateProfilePowerShellScripts_ForRemoteManagedProfilesWarnConsentWorkflowIsNotImplemented(string profile)
    {
        var vscodeScript = ConfigSamples.CreateProfileVsCodePowerShellScript(profile);
        var copilotCliScript = ConfigSamples.CreateProfileCopilotCliPowerShellScript(profile);

        Assert.Contains("This repository does not implement a remote or shared endpoint user consent workflow.", vscodeScript);
        Assert.Contains("This repository does not implement a remote or shared endpoint user consent workflow.", copilotCliScript);
    }

    [Theory]
    [InlineData(CollectionProfileOptions.RemoteManagedLangfuse)]
    [InlineData(CollectionProfileOptions.RemoteManagedCollector)]
    public void CreateProfileCodexAppConfigToml_ForRemoteManagedProfilesWarnConsentWorkflowIsNotImplemented(string profile)
    {
        var config = ConfigSamples.CreateProfileCodexAppConfigToml(profile);

        Assert.Contains("This repository does not implement a remote or shared endpoint user consent workflow.", config);
    }

    [Fact]
    public void CreateProfileOutputs_ForRemoteManagedLangfuseUseCredentialPlaceholders()
    {
        var vscodeScript = ConfigSamples.CreateProfileVsCodePowerShellScript(CollectionProfileOptions.RemoteManagedLangfuse);
        var copilotCliScript = ConfigSamples.CreateProfileCopilotCliPowerShellScript(CollectionProfileOptions.RemoteManagedLangfuse);
        var codexConfig = ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RemoteManagedLangfuse);

        Assert.Contains("$publicKey = \"<public-key>\"", vscodeScript);
        Assert.Contains("$secretKey = \"<secret-key>\"", vscodeScript);
        Assert.Contains("Authorization=Basic $auth", vscodeScript);
        Assert.Contains("$publicKey = \"<public-key>\"", copilotCliScript);
        Assert.Contains("$secretKey = \"<secret-key>\"", copilotCliScript);
        Assert.Contains("Authorization=Basic $auth", copilotCliScript);
        Assert.Contains("Authorization = \"Basic <base64-public-secret>\"", codexConfig);
    }

    [Fact]
    public void CreateProfileOutputs_ForRemoteManagedCollectorDoNotEmitLangfuseCredentials()
    {
        var vscodeScript = ConfigSamples.CreateProfileVsCodePowerShellScript(CollectionProfileOptions.RemoteManagedCollector);
        var copilotCliScript = ConfigSamples.CreateProfileCopilotCliPowerShellScript(CollectionProfileOptions.RemoteManagedCollector);
        var codexConfig = ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RemoteManagedCollector);

        Assert.DoesNotContain("<public-key>", vscodeScript);
        Assert.DoesNotContain("<secret-key>", vscodeScript);
        Assert.DoesNotContain("Authorization=Basic", vscodeScript);
        Assert.DoesNotContain("<public-key>", copilotCliScript);
        Assert.DoesNotContain("<secret-key>", copilotCliScript);
        Assert.DoesNotContain("Authorization=Basic", copilotCliScript);
        Assert.DoesNotContain("Authorization = \"Basic", codexConfig);
    }
}

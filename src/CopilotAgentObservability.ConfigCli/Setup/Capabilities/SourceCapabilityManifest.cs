using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Capabilities;

internal enum GitHubCopilotSetupTarget
{
    VsCode,
    Cli,
    AppSdk,
}

internal sealed class SourceCapabilityManifest
{
    private readonly JsonElement canonicalJson;

    public SourceCapabilityManifest(string sourceSurface, JsonElement canonicalJson)
    {
        SourceSurface = sourceSurface;
        this.canonicalJson = canonicalJson.Clone();
    }

    public string SourceSurface { get; }

    public JsonElement CanonicalJson => canonicalJson.Clone();
}

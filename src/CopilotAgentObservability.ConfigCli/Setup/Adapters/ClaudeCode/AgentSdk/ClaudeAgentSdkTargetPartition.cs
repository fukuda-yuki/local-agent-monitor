using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;

internal sealed class ClaudeAgentSdkTargetPartition
{
    private const string GuidanceKind = "caller_managed_sample";

    public string TargetToken => "app-sdk";

    public IReadOnlyList<SetupChangeRecord> Plan(ISetupPlatform platform, SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(request);

        return Array.AsReadOnly(new[]
        {
            CreateRecord(
                platform,
                ClaudeAgentSdkGuidanceVariant.PythonLabel,
                GuidanceKind,
                "python",
                request.IncludeContentCapture),
            CreateRecord(
                platform,
                ClaudeAgentSdkGuidanceVariant.TypeScriptLabel,
                GuidanceKind,
                "typescript",
                request.IncludeContentCapture),
        });
    }

    private static SetupChangeRecord CreateRecord(
        ISetupPlatform platform,
        string label,
        string kind,
        string language,
        bool includeContentCapture)
    {
        var statusGuidance = new SetupStatusGuidance(kind, language);
        return new SetupChangeRecord(
            platform.Identifiers.CreateUuidV7(),
            SetupTargetKind.Guidance,
            label,
            label,
            new string('0', 64),
            ClaudeAgentSdkGuidanceVariant.DesiredState(label, includeContentCapture),
            [],
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                null,
                SetupOperation.NoOp,
                null,
                null,
                null,
                statusGuidance,
                []),
            ClaudeAgentSdkGuidanceVariant.CreateGuidance(label, kind, language, includeContentCapture));
    }
}

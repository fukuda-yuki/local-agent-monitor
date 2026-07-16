using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;

internal sealed class ClaudeAgentSdkTargetPartition
{
    private const string GuidanceKind = "caller_managed_sample";
    private const string PythonLabel = "claude-agent-sdk-python-guidance";
    private const string TypeScriptLabel = "claude-agent-sdk-typescript-guidance";
    private const string TracesEndpointPlaceholder = "<canonical-origin>/v1/traces";

    public string TargetToken => "app-sdk";

    public IReadOnlyList<SetupChangeRecord> Plan(ISetupPlatform platform, SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(request);

        return Array.AsReadOnly(new[]
        {
            CreateRecord(
                platform,
                PythonLabel,
                GuidanceKind,
                "python",
                CreatePythonSample(request.IncludeContentCapture)),
            CreateRecord(
                platform,
                TypeScriptLabel,
                GuidanceKind,
                "typescript",
                CreateTypeScriptSample(request.IncludeContentCapture)),
        });
    }

    private static SetupChangeRecord CreateRecord(
        ISetupPlatform platform,
        string label,
        string kind,
        string language,
        string sample)
    {
        var statusGuidance = new SetupStatusGuidance(kind, language);
        return new SetupChangeRecord(
            platform.Identifiers.CreateUuidV7(),
            SetupTargetKind.Guidance,
            label,
            label,
            new string('0', 64),
            new SetupInlineDesiredState(label),
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
            new SetupGuidance(kind, language, sample));
    }

    private static string CreatePythonSample(bool includeContentCapture)
    {
        var lines = new List<string>
        {
            "import os",
            "from claude_agent_sdk import ClaudeAgentOptions",
            string.Empty,
            "options = ClaudeAgentOptions(env={",
            "    **os.environ,",
            "    \"CLAUDE_CODE_ENABLE_TELEMETRY\": \"1\",",
            "    \"CLAUDE_CODE_ENHANCED_TELEMETRY_BETA\": \"1\",",
            "    \"OTEL_TRACES_EXPORTER\": \"otlp\",",
            "    \"OTEL_EXPORTER_OTLP_TRACES_PROTOCOL\": \"http/protobuf\",",
            $"    \"OTEL_EXPORTER_OTLP_TRACES_ENDPOINT\": \"{TracesEndpointPlaceholder}\",",
        };
        AddPythonContentGates(lines, includeContentCapture);
        lines.Add("})");
        lines.Add(string.Empty);
        lines.Add("# Flush telemetry before a short-lived process exits.");
        return string.Join('\n', lines);
    }

    private static string CreateTypeScriptSample(bool includeContentCapture)
    {
        var lines = new List<string>
        {
            "const options = {",
            "  env: {",
            "    ...process.env,",
            "    CLAUDE_CODE_ENABLE_TELEMETRY: \"1\",",
            "    CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: \"1\",",
            "    OTEL_TRACES_EXPORTER: \"otlp\",",
            "    OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: \"http/protobuf\",",
            $"    OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: \"{TracesEndpointPlaceholder}\",",
        };
        AddTypeScriptContentGates(lines, includeContentCapture);
        lines.Add("  },");
        lines.Add("};");
        lines.Add(string.Empty);
        lines.Add("// Pass options.env to the Agent SDK and flush telemetry before a short-lived process exits.");
        return string.Join('\n', lines);
    }

    private static void AddPythonContentGates(List<string> lines, bool includeContentCapture)
    {
        if (!includeContentCapture)
        {
            return;
        }

        lines.Add("    \"OTEL_LOG_USER_PROMPTS\": \"1\",");
        lines.Add("    \"OTEL_LOG_TOOL_DETAILS\": \"1\",");
        lines.Add("    \"OTEL_LOG_TOOL_CONTENT\": \"1\",");
    }

    private static void AddTypeScriptContentGates(List<string> lines, bool includeContentCapture)
    {
        if (!includeContentCapture)
        {
            return;
        }

        lines.Add("    OTEL_LOG_USER_PROMPTS: \"1\",");
        lines.Add("    OTEL_LOG_TOOL_DETAILS: \"1\",");
        lines.Add("    OTEL_LOG_TOOL_CONTENT: \"1\",");
    }
}

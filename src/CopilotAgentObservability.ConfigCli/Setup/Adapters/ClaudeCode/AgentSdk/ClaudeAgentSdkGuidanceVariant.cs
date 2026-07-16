using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;

internal static class ClaudeAgentSdkGuidanceVariant
{
    internal const string PythonLabel = "claude-agent-sdk-python-guidance";
    internal const string TypeScriptLabel = "claude-agent-sdk-typescript-guidance";
    private const string GuidanceKind = "caller_managed_sample";
    private const string ContentSuffix = "content-capture-v1";
    private const string TracesEndpointPlaceholder = "<canonical-origin>/v1/traces";

    internal static SetupInlineDesiredState DesiredState(string label, bool includeContentCapture) =>
        new(includeContentCapture ? $"{label}:{ContentSuffix}" : label);

    internal static bool Read(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget)
    {
        if (planTarget.TargetKind != SetupTargetKind.Guidance ||
            ledgerTarget.TargetKind != SetupTargetKind.Guidance ||
            planTarget.RecordId != ledgerTarget.RecordId ||
            planTarget.TargetLocation != ledgerTarget.TargetLabel ||
            ledgerTarget.StatusProjection.Guidance is not { Kind: GuidanceKind } guidance ||
            !IsExactLanguage(ledgerTarget.TargetLabel, guidance.Language) ||
            planTarget.DesiredState is not SetupInlineDesiredState desired)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        if (desired.Value == ledgerTarget.TargetLabel)
        {
            return false;
        }

        if (desired.Value == $"{ledgerTarget.TargetLabel}:{ContentSuffix}")
        {
            return true;
        }

        throw SetupPlanResult.InvalidOutput();
    }

    internal static bool ValidatePair(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet)
    {
        var planGuidance = plan.Targets.Where(target => target.TargetKind == SetupTargetKind.Guidance).ToArray();
        var ledgerGuidance = changeSet.Targets.Where(target => target.TargetKind == SetupTargetKind.Guidance).ToArray();
        if (planGuidance.Length == 0 && ledgerGuidance.Length == 0)
        {
            if (plan.Adapter == "claude-code" &&
                (plan.SelectedTarget != "cli" || changeSet.SelectedTarget != "cli"))
            {
                throw SetupPlanResult.InvalidOutput();
            }

            return false;
        }

        if (plan.Adapter != "claude-code" || changeSet.Adapter != "claude-code" ||
            plan.SelectedTarget != changeSet.SelectedTarget ||
            plan.SelectedTarget is not ("app-sdk" or "all") ||
            planGuidance.Length != 2 || ledgerGuidance.Length != 2 ||
            ledgerGuidance[0].TargetLabel != PythonLabel ||
            ledgerGuidance[1].TargetLabel != TypeScriptLabel)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var python = Read(planGuidance[0], ledgerGuidance[0]);
        var typescript = Read(planGuidance[1], ledgerGuidance[1]);
        if (python != typescript)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        if (plan.SelectedTarget == "all")
        {
            var settings = plan.Targets
                .Where(target => target.TargetKind != SetupTargetKind.Guidance)
                .Select(target => target.DesiredState)
                .OfType<SetupClaudeSettingsOwnedValuesDesiredState>()
                .SingleOrDefault();
            var settingsIncludeContent = settings?.OwnedEnv.Count == 8;
            if (settings is null || settingsIncludeContent != python)
            {
                throw SetupPlanResult.InvalidOutput();
            }
        }

        return python;
    }

    internal static SetupGuidance CreateGuidance(
        string label,
        string kind,
        string language,
        bool includeContentCapture)
    {
        if (kind != GuidanceKind || !IsExactLanguage(label, language))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        return new SetupGuidance(kind, language, CreateSample(label, includeContentCapture));
    }

    internal static bool IsValidSample(
        string label,
        string kind,
        string language,
        string sample) =>
        kind == GuidanceKind &&
        IsExactLanguage(label, language) &&
        (sample == CreateSample(label, includeContentCapture: false) ||
         sample == CreateSample(label, includeContentCapture: true));

    private static bool IsExactLanguage(string label, string language) =>
        (label, language) is
            (PythonLabel, "python") or
            (TypeScriptLabel, "typescript");

    private static string CreateSample(string label, bool includeContentCapture) => label switch
    {
        PythonLabel => CreatePythonSample(includeContentCapture),
        TypeScriptLabel => CreateTypeScriptSample(includeContentCapture),
        _ => throw SetupPlanResult.InvalidOutput(),
    };

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
        if (includeContentCapture)
        {
            lines.Add("    \"OTEL_LOG_USER_PROMPTS\": \"1\",");
            lines.Add("    \"OTEL_LOG_TOOL_DETAILS\": \"1\",");
            lines.Add("    \"OTEL_LOG_TOOL_CONTENT\": \"1\",");
        }

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
        if (includeContentCapture)
        {
            lines.Add("    OTEL_LOG_USER_PROMPTS: \"1\",");
            lines.Add("    OTEL_LOG_TOOL_DETAILS: \"1\",");
            lines.Add("    OTEL_LOG_TOOL_CONTENT: \"1\",");
        }

        lines.Add("  },");
        lines.Add("};");
        lines.Add(string.Empty);
        lines.Add("// Pass options.env to the Agent SDK and flush telemetry before a short-lived process exits.");
        return string.Join('\n', lines);
    }
}

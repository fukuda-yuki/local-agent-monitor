using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli;

internal static class Program
{
    public static int Main(string[] args)
    {
        return CliApplication.Run(args, Console.Out, Console.Error);
    }
}

internal static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(HelpText);
            return args.Length == 0 ? 1 : 0;
        }

        switch (args[0])
        {
            case "vscode-settings":
                output.WriteLine(ConfigSamples.CreateVsCodeSettingsJson());
                return 0;

            case "langfuse-vscode-settings":
                output.WriteLine(ConfigSamples.CreateLangfuseVsCodeSettingsJson());
                return 0;

            case "collector-vscode-settings":
                output.WriteLine(ConfigSamples.CreateCollectorVsCodeSettingsJson());
                return 0;

            case "vscode-env":
                output.WriteLine(ConfigSamples.CreateVsCodePowerShellScript());
                return 0;

            case "langfuse-vscode-env":
                output.WriteLine(ConfigSamples.CreateLangfuseVsCodePowerShellScript());
                return 0;

            case "collector-vscode-env":
                output.WriteLine(ConfigSamples.CreateCollectorVsCodePowerShellScript());
                return 0;

            case "vscode-file-settings":
                if (args.Length != 2)
                {
                    error.WriteLine("error: vscode-file-settings requires exactly one output file path.");
                    return 1;
                }

                output.WriteLine(ConfigSamples.CreateVsCodeFileSettingsJson(args[1]));
                return 0;

            case "copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateCopilotCliPowerShellScript());
                return 0;

            case "langfuse-copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateLangfuseCopilotCliPowerShellScript());
                return 0;

            case "collector-copilot-cli-env":
                output.WriteLine(ConfigSamples.CreateCollectorCopilotCliPowerShellScript());
                return 0;

            case "validate-resource-attributes":
                if (args.Length != 2)
                {
                    error.WriteLine("error: validate-resource-attributes requires exactly one OTEL_RESOURCE_ATTRIBUTES value.");
                    return 1;
                }

                var result = ResourceAttributeValidator.Validate(args[1]);
                foreach (var validationError in result.Errors)
                {
                    error.WriteLine($"error: {validationError}");
                }

                foreach (var warning in result.Warnings)
                {
                    error.WriteLine($"warning: {warning}");
                }

                if (!result.IsValid)
                {
                    return 1;
                }

                output.WriteLine("OTEL_RESOURCE_ATTRIBUTES is valid.");
                return 0;

            default:
                error.WriteLine($"error: unknown command '{args[0]}'.");
                error.WriteLine(HelpText);
                return 1;
        }
    }

    private const string HelpText = """
        Usage:
          config-cli vscode-settings
          config-cli langfuse-vscode-settings
          config-cli collector-vscode-settings
          config-cli vscode-env
          config-cli langfuse-vscode-env
          config-cli collector-vscode-env
          config-cli vscode-file-settings <outfile>
          config-cli copilot-cli-env
          config-cli langfuse-copilot-cli-env
          config-cli collector-copilot-cli-env
          config-cli validate-resource-attributes <OTEL_RESOURCE_ATTRIBUTES>
        """;
}

internal static class ConfigSamples
{
    public const string DefaultOtlpEndpoint = LangfuseOtlpEndpoint;
    public const string LangfuseOtlpEndpoint = "http://localhost:3000/api/public/otel";
    public const string LangfuseOtlpTracesEndpoint = "http://localhost:3000/api/public/otel/v1/traces";
    public const string CollectorOtlpHttpEndpoint = "http://localhost:4318";
    public const string VsCodeClientKind = "vscode-copilot-chat";
    public const string CopilotCliClientKind = "copilot-cli";
    public const string DefaultExperimentId = "baseline";
    private const string LangfuseIngestionVersionHeader = "x-langfuse-ingestion-version=4";
    private const string LangfusePublicKeyPlaceholder = "<public-key>";
    private const string LangfuseSecretKeyPlaceholder = "<secret-key>";

    public static string CreateVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = DefaultOtlpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateLangfuseVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = LangfuseOtlpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateCollectorVsCodeSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
            ["github.copilot.chat.otel.otlpEndpoint"] = CollectorOtlpHttpEndpoint,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateVsCodeFileSettingsJson(string outfile)
    {
        var settings = new Dictionary<string, object>
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.exporterType"] = "file",
            ["github.copilot.chat.otel.outfile"] = outfile,
            ["github.copilot.chat.otel.captureContent"] = true,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{DefaultOtlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateLangfuseVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{LangfuseOtlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCollectorVsCodePowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{CollectorOtlpHttpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{DefaultOtlpEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateLangfuseCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{LangfuseOtlpEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{LangfuseOtlpTracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    public static string CreateCollectorCopilotCliPowerShellScript()
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{CollectorOtlpHttpEndpoint}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static void AppendLangfuseAuthPrelude(StringBuilder builder)
    {
        builder.AppendLine($"$publicKey = \"{LangfusePublicKeyPlaceholder}\"");
        builder.AppendLine($"$secretKey = \"{LangfuseSecretKeyPlaceholder}\"");
        builder.AppendLine("$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\"))");
    }

    private static void AppendCollectorCleanup(StringBuilder builder)
    {
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue");
    }

    private static string CreateResourceAttributes(string clientKind)
    {
        return string.Join(
            ',',
            "user.id=example-user",
            "user.email=user@example.com",
            "team.id=platform",
            "department=engineering",
            $"client.kind={clientKind}",
            $"experiment.id={DefaultExperimentId}");
    }
}

internal static class ResourceAttributeValidator
{
    private static readonly string[] RequiredKeys =
    [
        "user.id",
        "user.email",
        "team.id",
        "department",
        "client.kind",
        "experiment.id",
    ];

    private static readonly HashSet<string> RecommendedClientKinds = new(StringComparer.Ordinal)
    {
        ConfigSamples.VsCodeClientKind,
        ConfigSamples.CopilotCliClientKind,
    };

    public static ResourceAttributeValidationResult Validate(string rawValue)
    {
        var parseResult = Parse(rawValue);
        var errors = new List<string>(parseResult.Errors);
        var warnings = new List<string>();
        var attributes = parseResult.Attributes;

        foreach (var requiredKey in RequiredKeys)
        {
            if (!attributes.ContainsKey(requiredKey))
            {
                errors.Add($"missing required resource attribute '{requiredKey}'.");
            }
        }

        if (attributes.TryGetValue("client.kind", out var clientKind)
            && !RecommendedClientKinds.Contains(clientKind))
        {
            warnings.Add($"client.kind '{clientKind}' is not a recommended value. Use 'vscode-copilot-chat' or 'copilot-cli'.");
        }

        if (attributes.TryGetValue("experiment.id", out var experimentId)
            && !string.Equals(experimentId, ConfigSamples.DefaultExperimentId, StringComparison.Ordinal))
        {
            warnings.Add($"experiment.id '{experimentId}' is not the initial recommended value 'baseline'.");
        }

        return new ResourceAttributeValidationResult(errors, warnings);
    }

    private static ResourceAttributeParseResult Parse(string rawValue)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            errors.Add("OTEL_RESOURCE_ATTRIBUTES is empty.");
            return new ResourceAttributeParseResult(attributes, errors);
        }

        var elements = rawValue.Split(',');
        for (var index = 0; index < elements.Length; index++)
        {
            var element = elements[index].Trim();
            var displayIndex = index + 1;

            if (element.Length == 0)
            {
                errors.Add($"resource attribute element {displayIndex} is empty.");
                continue;
            }

            var separatorIndex = element.IndexOf('=');
            if (separatorIndex < 0)
            {
                errors.Add($"resource attribute element {displayIndex} is not in key=value form.");
                continue;
            }

            var key = element[..separatorIndex].Trim();
            var value = element[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                errors.Add($"resource attribute element {displayIndex} has an empty key.");
                continue;
            }

            attributes[key] = value;
        }

        return new ResourceAttributeParseResult(attributes, errors);
    }

    private sealed record ResourceAttributeParseResult(
        IReadOnlyDictionary<string, string> Attributes,
        IReadOnlyList<string> Errors);
}

internal sealed record ResourceAttributeValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

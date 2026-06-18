namespace CopilotAgentObservability.ConfigCli;

internal static class ConfigSamples
{
    public const string DefaultOtlpEndpoint = LangfuseOtlpEndpoint;
    public const string LangfuseOtlpEndpoint = "http://localhost:3000/api/public/otel";
    public const string LangfuseOtlpTracesEndpoint = "http://localhost:3000/api/public/otel/v1/traces";
    public const string LangfuseOtlpLogsEndpoint = "http://localhost:3000/api/public/otel/v1/logs";
    public const string LangfuseOtlpMetricsEndpoint = "http://localhost:3000/api/public/otel/v1/metrics";
    public const string CollectorOtlpHttpEndpoint = "http://localhost:4318";
    public const string CollectorOtlpHttpLogsEndpoint = "http://localhost:4318/v1/logs";
    public const string CollectorOtlpHttpMetricsEndpoint = "http://localhost:4318/v1/metrics";
    public const string CollectorOtlpHttpTracesEndpoint = "http://localhost:4318/v1/traces";
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

    public static string CreateLangfuseCodexAppConfigToml()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[otel]");
        builder.AppendLine("environment = \"dev\"");
        builder.AppendLine("log_user_prompt = false");
        AppendCodexAppOtlpHttpExporter(builder, "exporter", LangfuseOtlpLogsEndpoint, includeLangfuseHeaders: true);
        AppendCodexAppOtlpHttpExporter(builder, "metrics_exporter", LangfuseOtlpMetricsEndpoint, includeLangfuseHeaders: true);
        AppendCodexAppOtlpHttpExporter(builder, "trace_exporter", LangfuseOtlpTracesEndpoint, includeLangfuseHeaders: true);
        return builder.ToString();
    }

    public static string CreateCollectorCodexAppConfigToml()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[otel]");
        builder.AppendLine("environment = \"dev\"");
        builder.AppendLine("log_user_prompt = false");
        AppendCodexAppOtlpHttpExporter(builder, "exporter", CollectorOtlpHttpLogsEndpoint, includeLangfuseHeaders: false);
        AppendCodexAppOtlpHttpExporter(builder, "metrics_exporter", CollectorOtlpHttpMetricsEndpoint, includeLangfuseHeaders: false);
        AppendCodexAppOtlpHttpExporter(builder, "trace_exporter", CollectorOtlpHttpTracesEndpoint, includeLangfuseHeaders: false);
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

    private static void AppendCodexAppOtlpHttpExporter(
        StringBuilder builder,
        string key,
        string endpoint,
        bool includeLangfuseHeaders)
    {
        if (includeLangfuseHeaders)
        {
            builder.AppendLine($"{key} = {{ otlp-http = {{ endpoint = \"{endpoint}\", protocol = \"binary\", headers = {{ Authorization = \"Basic <base64-public-secret>\", \"x-langfuse-ingestion-version\" = \"4\" }} }} }}");
            return;
        }

        builder.AppendLine($"{key} = {{ otlp-http = {{ endpoint = \"{endpoint}\", protocol = \"binary\" }} }}");
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

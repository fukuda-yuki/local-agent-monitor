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
    public const string Wsl2LangfuseOtlpEndpoint = "http://<windows-reachable-wsl2-host>:3000/api/public/otel";
    public const string Wsl2LangfuseOtlpTracesEndpoint = "http://<windows-reachable-wsl2-host>:3000/api/public/otel/v1/traces";
    public const string Wsl2LangfuseOtlpLogsEndpoint = "http://<windows-reachable-wsl2-host>:3000/api/public/otel/v1/logs";
    public const string Wsl2LangfuseOtlpMetricsEndpoint = "http://<windows-reachable-wsl2-host>:3000/api/public/otel/v1/metrics";
    public const string Wsl2CollectorOtlpHttpEndpoint = "http://<windows-reachable-wsl2-host>:4318";
    public const string Wsl2CollectorOtlpHttpLogsEndpoint = "http://<windows-reachable-wsl2-host>:4318/v1/logs";
    public const string Wsl2CollectorOtlpHttpMetricsEndpoint = "http://<windows-reachable-wsl2-host>:4318/v1/metrics";
    public const string Wsl2CollectorOtlpHttpTracesEndpoint = "http://<windows-reachable-wsl2-host>:4318/v1/traces";
    public const string RemoteLangfuseOtlpEndpoint = "https://<langfuse-host>/api/public/otel";
    public const string RemoteLangfuseOtlpTracesEndpoint = "https://<langfuse-host>/api/public/otel/v1/traces";
    public const string RemoteLangfuseOtlpLogsEndpoint = "https://<langfuse-host>/api/public/otel/v1/logs";
    public const string RemoteLangfuseOtlpMetricsEndpoint = "https://<langfuse-host>/api/public/otel/v1/metrics";
    public const string RemoteCollectorOtlpHttpEndpoint = "https://<collector-host>";
    public const string RemoteCollectorOtlpHttpLogsEndpoint = "https://<collector-host>/v1/logs";
    public const string RemoteCollectorOtlpHttpMetricsEndpoint = "https://<collector-host>/v1/metrics";
    public const string RemoteCollectorOtlpHttpTracesEndpoint = "https://<collector-host>/v1/traces";
    public const string RawLocalReceiverOtlpHttpEndpoint = "http://127.0.0.1:4319";
    public const string RawLocalReceiverOtlpHttpTracesEndpoint = "http://127.0.0.1:4319/v1/traces";
    public const string LocalMonitorOtlpHttpEndpoint = "http://127.0.0.1:4320";
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

    public static string CreateProfileVsCodePowerShellScript(string profile)
    {
        return CreateProfileVsCodePowerShellScript(profile, rawLocalReceiverEndpoint: null);
    }

    public static string CreateProfileVsCodePowerShellScript(string profile, string? rawLocalReceiverEndpoint)
    {
        return profile switch
        {
            CollectionProfileOptions.RawOnly => CreateRawOnlyProfileScript(profile, "raw-only uses saved raw OTLP JSON. No live VS Code receiver environment is required."),
            CollectionProfileOptions.DockerDesktopLangfuse => CreateLangfuseVsCodePowerShellScript(profile, LangfuseOtlpEndpoint, LangfuseOtlpTracesEndpoint),
            CollectionProfileOptions.DockerDesktopCollectorLangfuse => CreateCollectorVsCodePowerShellScript(profile, CollectorOtlpHttpEndpoint),
            CollectionProfileOptions.Wsl2DockerLangfuse => CreateWsl2EndpointWarning(CreateLangfuseVsCodePowerShellScript(profile, Wsl2LangfuseOtlpEndpoint, Wsl2LangfuseOtlpTracesEndpoint)),
            CollectionProfileOptions.Wsl2DockerCollectorLangfuse => CreateWsl2EndpointWarning(CreateCollectorVsCodePowerShellScript(profile, Wsl2CollectorOtlpHttpEndpoint)),
            CollectionProfileOptions.RemoteManagedLangfuse => CreateRemoteWarning(CreateLangfuseVsCodePowerShellScript(profile, RemoteLangfuseOtlpEndpoint, RemoteLangfuseOtlpTracesEndpoint)),
            CollectionProfileOptions.RemoteManagedCollector => CreateRemoteWarning(CreateCollectorVsCodePowerShellScript(profile, RemoteCollectorOtlpHttpEndpoint)),
            CollectionProfileOptions.RawLocalReceiver => CreateRawLocalReceiverVsCodePowerShellScript(profile, rawLocalReceiverEndpoint ?? RawLocalReceiverOtlpHttpEndpoint),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported collection profile."),
        };
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

    public static string CreateProfileCopilotCliPowerShellScript(string profile)
    {
        return profile switch
        {
            CollectionProfileOptions.RawOnly => CreateRawOnlyProfileScript(profile, "raw-only uses saved raw OTLP JSON. No live Copilot CLI receiver environment is required."),
            CollectionProfileOptions.DockerDesktopLangfuse => CreateLangfuseCopilotCliPowerShellScript(profile, LangfuseOtlpEndpoint, LangfuseOtlpTracesEndpoint),
            CollectionProfileOptions.DockerDesktopCollectorLangfuse => CreateCollectorCopilotCliPowerShellScript(profile, CollectorOtlpHttpEndpoint),
            CollectionProfileOptions.Wsl2DockerLangfuse => CreateWsl2EndpointWarning(CreateLangfuseCopilotCliPowerShellScript(profile, Wsl2LangfuseOtlpEndpoint, Wsl2LangfuseOtlpTracesEndpoint)),
            CollectionProfileOptions.Wsl2DockerCollectorLangfuse => CreateWsl2EndpointWarning(CreateCollectorCopilotCliPowerShellScript(profile, Wsl2CollectorOtlpHttpEndpoint)),
            CollectionProfileOptions.RemoteManagedLangfuse => CreateRemoteWarning(CreateLangfuseCopilotCliPowerShellScript(profile, RemoteLangfuseOtlpEndpoint, RemoteLangfuseOtlpTracesEndpoint)),
            CollectionProfileOptions.RemoteManagedCollector => CreateRemoteWarning(CreateCollectorCopilotCliPowerShellScript(profile, RemoteCollectorOtlpHttpEndpoint)),
            CollectionProfileOptions.RawLocalReceiver => CreateRawLocalReceiverCopilotCliPowerShellScript(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported collection profile."),
        };
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

    public static string CreateProfileCodexAppConfigToml(string profile)
    {
        return profile switch
        {
            CollectionProfileOptions.RawOnly => CreateRawOnlyCodexAppConfigToml(profile),
            CollectionProfileOptions.DockerDesktopLangfuse => CreateCodexAppConfigToml(profile, LangfuseOtlpLogsEndpoint, LangfuseOtlpMetricsEndpoint, LangfuseOtlpTracesEndpoint, includeLangfuseHeaders: true),
            CollectionProfileOptions.DockerDesktopCollectorLangfuse => CreateCodexAppConfigToml(profile, CollectorOtlpHttpLogsEndpoint, CollectorOtlpHttpMetricsEndpoint, CollectorOtlpHttpTracesEndpoint, includeLangfuseHeaders: false),
            CollectionProfileOptions.Wsl2DockerLangfuse => CreateWsl2EndpointWarning(CreateCodexAppConfigToml(profile, Wsl2LangfuseOtlpLogsEndpoint, Wsl2LangfuseOtlpMetricsEndpoint, Wsl2LangfuseOtlpTracesEndpoint, includeLangfuseHeaders: true)),
            CollectionProfileOptions.Wsl2DockerCollectorLangfuse => CreateWsl2EndpointWarning(CreateCodexAppConfigToml(profile, Wsl2CollectorOtlpHttpLogsEndpoint, Wsl2CollectorOtlpHttpMetricsEndpoint, Wsl2CollectorOtlpHttpTracesEndpoint, includeLangfuseHeaders: false)),
            CollectionProfileOptions.RemoteManagedLangfuse => CreateRemoteWarning(CreateCodexAppConfigToml(profile, RemoteLangfuseOtlpLogsEndpoint, RemoteLangfuseOtlpMetricsEndpoint, RemoteLangfuseOtlpTracesEndpoint, includeLangfuseHeaders: true)),
            CollectionProfileOptions.RemoteManagedCollector => CreateRemoteWarning(CreateCodexAppConfigToml(profile, RemoteCollectorOtlpHttpLogsEndpoint, RemoteCollectorOtlpHttpMetricsEndpoint, RemoteCollectorOtlpHttpTracesEndpoint, includeLangfuseHeaders: false)),
            CollectionProfileOptions.RawLocalReceiver => CreateRawLocalReceiverCodexAppConfigToml(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported collection profile."),
        };
    }

    private static string CreateLangfuseVsCodePowerShellScript(string profile, string otlpEndpoint, string tracesEndpoint)
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{tracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateCollectorVsCodePowerShellScript(string profile, string otlpEndpoint)
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateLangfuseCopilotCliPowerShellScript(string profile, string otlpEndpoint, string tracesEndpoint)
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendLangfuseAuthPrelude(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{tracesEndpoint}\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS=\"Authorization=Basic $auth,{LangfuseIngestionVersionHeader}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateCollectorCopilotCliPowerShellScript(string profile, string otlpEndpoint)
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendCollectorCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateRawLocalReceiverVsCodePowerShellScript(string profile, string otlpEndpoint)
    {
        var resourceAttributes = CreateResourceAttributes(VsCodeClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendRawLocalReceiverCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:COPILOT_OTEL_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine("$env:COPILOT_OTEL_CAPTURE_CONTENT=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{otlpEndpoint}\"");
        builder.AppendLine("$env:OTEL_EXPORTER_OTLP_PROTOCOL=\"http/protobuf\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateRawLocalReceiverCopilotCliPowerShellScript(string profile)
    {
        var resourceAttributes = CreateResourceAttributes(CopilotCliClientKind);

        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendRawLocalReceiverCleanup(builder);
        builder.AppendLine("$env:COPILOT_OTEL_ENABLED=\"true\"");
        builder.AppendLine($"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"{RawLocalReceiverOtlpHttpEndpoint}\"");
        builder.AppendLine("$env:OTEL_EXPORTER_OTLP_PROTOCOL=\"http/protobuf\"");
        builder.AppendLine("$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=\"true\"");
        builder.Append($"$env:OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttributes}\"");
        return builder.ToString();
    }

    private static string CreateCodexAppConfigToml(
        string profile,
        string logsEndpoint,
        string metricsEndpoint,
        string tracesEndpoint,
        bool includeLangfuseHeaders)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {CollectionProfileOptions.EnvironmentVariableName}={profile}");
        builder.AppendLine("[otel]");
        builder.AppendLine("environment = \"dev\"");
        builder.AppendLine("log_user_prompt = false");
        AppendCodexAppOtlpHttpExporter(builder, "exporter", logsEndpoint, includeLangfuseHeaders);
        AppendCodexAppOtlpHttpExporter(builder, "metrics_exporter", metricsEndpoint, includeLangfuseHeaders);
        AppendCodexAppOtlpHttpExporter(builder, "trace_exporter", tracesEndpoint, includeLangfuseHeaders);
        return builder.ToString();
    }

    private static void AppendLangfuseAuthPrelude(StringBuilder builder)
    {
        builder.AppendLine($"$publicKey = \"{LangfusePublicKeyPlaceholder}\"");
        builder.AppendLine($"$secretKey = \"{LangfuseSecretKeyPlaceholder}\"");
        builder.AppendLine("$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(\"${publicKey}:${secretKey}\"))");
    }

    private static void AppendProfileSelection(StringBuilder builder, string profile)
    {
        builder.AppendLine($"$env:{CollectionProfileOptions.EnvironmentVariableName}=\"{profile}\"");
    }

    private static string CreateRawOnlyProfileScript(string profile, string message)
    {
        var builder = new StringBuilder();
        AppendProfileSelection(builder, profile);
        AppendLiveTelemetryCleanup(builder);
        builder.AppendLine($"# {message}");
        builder.Append("# Use saved raw OTLP JSON with ingest-raw or normalize-raw.");
        return builder.ToString();
    }

    private static string CreateRawOnlyCodexAppConfigToml(string profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {CollectionProfileOptions.EnvironmentVariableName}={profile}");
        builder.AppendLine("# Remove or omit active [otel] routing entries from user-level ~/.codex/config.toml.");
        builder.AppendLine("# raw-only uses saved raw OTLP JSON. No Codex App OTel routing config is required.");
        builder.Append("# Use saved raw OTLP JSON with ingest-raw or normalize-raw.");
        return builder.ToString();
    }

    private static string CreateRemoteWarning(string content)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WARNING: confirm access control, retention, deletion process, masking / redaction, user notice or consent, identity handling, and credential handling before sending telemetry to a remote managed endpoint.");
        builder.AppendLine("# This repository does not implement a remote or shared endpoint user consent workflow.");
        builder.Append(content);
        return builder.ToString();
    }

    private static string CreateWsl2EndpointWarning(string content)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WSL2 Docker Engine: replace <windows-reachable-wsl2-host> with the host name or address that Windows clients can reach for the selected WSL2 distro.");
        builder.AppendLine("# Prefer localhost when WSL2 localhost forwarding exposes published container ports to Windows.");
        builder.AppendLine("# If localhost forwarding is unavailable, resolve the current WSL2 address during live validation and keep machine-specific IP addresses out of repository files.");
        builder.Append(content);
        return builder.ToString();
    }

    private static void AppendCollectorCleanup(StringBuilder builder)
    {
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue");
    }

    private static void AppendRawLocalReceiverCleanup(StringBuilder builder)
    {
        AppendCollectorCleanup(builder);
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_LOGS_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_LOGS_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_METRICS_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_METRICS_HEADERS -ErrorAction SilentlyContinue");
    }

    private static void AppendLiveTelemetryCleanup(StringBuilder builder)
    {
        builder.AppendLine("Remove-Item Env:COPILOT_OTEL_ENABLED -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:COPILOT_OTEL_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:COPILOT_OTEL_CAPTURE_CONTENT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_EXPORTER_OTLP_PROTOCOL -ErrorAction SilentlyContinue");
        builder.AppendLine("Remove-Item Env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT -ErrorAction SilentlyContinue");
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

    private static string CreateRawLocalReceiverCodexAppConfigToml(string profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {CollectionProfileOptions.EnvironmentVariableName}={profile}");
        builder.AppendLine("[otel]");
        builder.AppendLine("environment = \"dev\"");
        builder.AppendLine("log_user_prompt = false");
        AppendCodexAppOtlpHttpExporter(builder, "trace_exporter", RawLocalReceiverOtlpHttpTracesEndpoint, includeLangfuseHeaders: false);
        return builder.ToString();
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

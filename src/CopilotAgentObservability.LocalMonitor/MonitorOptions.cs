using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed record MonitorOptions(
    string DatabasePath,
    string Url,
    bool SanitizedOnly,
    int MaxRequestBodyBytes,
    int IngestionStallThresholdSeconds = MonitorOptions.DefaultIngestionStallThresholdSeconds,
    int ProjectionLagThresholdSeconds = MonitorOptions.DefaultProjectionLagThresholdSeconds,
    IReadOnlyList<ConfiguredApplyRoot>? ApplyRoots = null,
    IReadOnlyList<string>? PricingRegistryOverridePaths = null,
    IReadOnlyList<string>? SkillDiscoveryProjectPaths = null,
    IReadOnlyList<string>? SkillDiscoveryDirectories = null,
    bool RepositoryAiEnabled = MonitorOptions.DefaultExtendedAiEnabled,
    bool CompareAiEnabled = MonitorOptions.DefaultExtendedAiEnabled)
{
#if DEBUG
    public const bool DefaultExtendedAiEnabled = true;
#else
    public const bool DefaultExtendedAiEnabled = false;
#endif
    public const string MaxRequestBodyBytesEnvironmentVariable = "CAO_MONITOR_MAX_REQUEST_BODY_BYTES";
    public const int DefaultMaxRequestBodyBytes = 31_457_280;
    public const string IngestionStallThresholdSecondsEnvironmentVariable = "CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS";
    public const int DefaultIngestionStallThresholdSeconds = 10;
    public const string ProjectionLagThresholdSecondsEnvironmentVariable = "CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS";
    public const int DefaultProjectionLagThresholdSeconds = 60;

    public static MonitorOptionsParseResult Parse(
        string[] args,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        string databasePath = RawStoreDefaults.DefaultDatabasePath;
        string url = "http://127.0.0.1:4320";
        var databasePathSet = false;
        var urlSet = false;
        var portSet = false;
        var sanitizedOnly = false;
        bool? repositoryAiEnabled = null;
        bool? compareAiEnabled = null;
        int? maxRequestBodyBytes = null;
        int? ingestionStallThresholdSeconds = null;
        int? projectionLagThresholdSeconds = null;
        var applyRoots = new List<ConfiguredApplyRoot>();
        var pricingRegistryOverridePaths = new List<string>();
        var skillDiscoveryProjectPaths = new List<string>();
        var skillDiscoveryDirectories = new List<string>();
        var skillDiscoveryProjectPathValueFault = false;
        var skillDiscoveryDirectoryValueFault = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db":
                    if (databasePathSet)
                    {
                        return Failure("local-monitor accepts --db only once.");
                    }

                    if (!TryReadValue(args, index, out var dbValue))
                    {
                        return Failure("--db requires a value.");
                    }

                    databasePath = dbValue;
                    databasePathSet = true;
                    index++;
                    break;

                case "--url":
                    if (urlSet)
                    {
                        return Failure("local-monitor accepts --url only once.");
                    }

                    if (portSet)
                    {
                        return Failure("local-monitor accepts either --url or --port, not both.");
                    }

                    if (!TryReadValue(args, index, out var urlValue))
                    {
                        return Failure("--url requires a value.");
                    }

                    var urlValidationError = ValidateLoopbackHttpUrl(urlValue, "local-monitor");
                    if (urlValidationError is not null)
                    {
                        return Failure(urlValidationError);
                    }

                    url = urlValue;
                    urlSet = true;
                    index++;
                    break;

                case "--port":
                    if (portSet)
                    {
                        return Failure("local-monitor accepts --port only once.");
                    }

                    if (urlSet)
                    {
                        return Failure("local-monitor accepts either --url or --port, not both.");
                    }

                    if (!TryReadValue(args, index, out var portValue))
                    {
                        return Failure("--port requires a value.");
                    }

                    if (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                        || port < IPEndPoint.MinPort
                        || port > IPEndPoint.MaxPort)
                    {
                        return Failure("--port requires a TCP port from 0 to 65535.");
                    }

                    url = $"http://127.0.0.1:{port}";
                    portSet = true;
                    index++;
                    break;

                case "--sanitized-only":
                    sanitizedOnly = true;
                    break;

                case "--repository-ai-enabled":
                case "--compare-ai-enabled":
                    var option = args[index];
                    ref var enabled = ref (option == "--repository-ai-enabled" ? ref repositoryAiEnabled : ref compareAiEnabled);
                    if (enabled is not null)
                    {
                        return Failure($"local-monitor accepts {option} only once.");
                    }

                    if (!TryReadValue(args, index, out var enabledValue))
                    {
                        return Failure($"{option} requires a value.");
                    }

                    if (!string.Equals(enabledValue, "true", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(enabledValue, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure($"{option} requires true or false.");
                    }

                    enabled = string.Equals(enabledValue, "true", StringComparison.OrdinalIgnoreCase);
                    index++;
                    break;

                case "--max-request-body-bytes":
                    if (maxRequestBodyBytes is not null)
                    {
                        return Failure("local-monitor accepts --max-request-body-bytes only once.");
                    }

                    if (!TryReadValue(args, index, out var maxValue))
                    {
                        return Failure("--max-request-body-bytes requires a value.");
                    }

                    if (!TryParsePositiveInt(maxValue, out var parsedMax))
                    {
                        return Failure("--max-request-body-bytes requires a positive integer.");
                    }

                    maxRequestBodyBytes = parsedMax;
                    index++;
                    break;

                case "--ingestion-stall-threshold-seconds":
                    if (ingestionStallThresholdSeconds is not null)
                    {
                        return Failure("local-monitor accepts --ingestion-stall-threshold-seconds only once.");
                    }

                    if (!TryReadValue(args, index, out var stallValue))
                    {
                        return Failure("--ingestion-stall-threshold-seconds requires a value.");
                    }

                    if (!TryParsePositiveInt(stallValue, out var parsedStall))
                    {
                        return Failure("--ingestion-stall-threshold-seconds requires a positive integer.");
                    }

                    ingestionStallThresholdSeconds = parsedStall;
                    index++;
                    break;

                case "--projection-lag-threshold-seconds":
                    if (projectionLagThresholdSeconds is not null)
                    {
                        return Failure("local-monitor accepts --projection-lag-threshold-seconds only once.");
                    }

                    if (!TryReadValue(args, index, out var lagValue))
                    {
                        return Failure("--projection-lag-threshold-seconds requires a value.");
                    }

                    if (!TryParsePositiveInt(lagValue, out var parsedLag))
                    {
                        return Failure("--projection-lag-threshold-seconds requires a positive integer.");
                    }

                    projectionLagThresholdSeconds = parsedLag;
                    index++;
                    break;

                case "--apply-root":
                    if (!TryReadValue(args, index, out var applyRootValue)) return Failure("--apply-root requires kind=<absolute-directory>.");
                    var separator = applyRootValue.IndexOf('=');
                    if (separator <= 0 || !ConfiguredApplyRoot.TryParseKind(applyRootValue[..separator], out var kind)) return Failure("--apply-root requires user_config, skill, or repository.");
                    try
                    {
                        var root = ConfiguredApplyRoot.Create(kind, applyRootValue[(separator + 1)..]);
                        if (applyRoots.Any(existing => string.Equals(existing.CanonicalPath, root.CanonicalPath, StringComparison.OrdinalIgnoreCase))) return Failure("--apply-root does not allow duplicate roots.");
                        applyRoots.Add(root);
                    }
                    catch (ApplyPathException) { return Failure("--apply-root requires an existing non-reparse absolute directory."); }
                    index++;
                    break;

                case "--pricing-registry-override":
                    if (!TryReadValue(args, index, out var pricingRegistryOverridePath)
                        || pricingRegistryOverridePaths.Count == 8)
                    {
                        return Failure("pricing_catalog_unavailable");
                    }

                    pricingRegistryOverridePaths.Add(pricingRegistryOverridePath);
                    index++;
                    break;

                case "--skill-discovery-project-path":
                    if (TryReadValue(args, index, out var skillDiscoveryProjectPathValue))
                    {
                        index++;
                        if (skillDiscoveryProjectPathValue.Length == 0)
                        {
                            skillDiscoveryProjectPathValueFault = true;
                        }
                        else
                        {
                            skillDiscoveryProjectPaths.Add(skillDiscoveryProjectPathValue);
                        }
                    }
                    else
                    {
                        skillDiscoveryProjectPathValueFault = true;
                    }

                    break;

                case "--skill-discovery-directory":
                    if (TryReadValue(args, index, out var skillDiscoveryDirectoryValue))
                    {
                        index++;
                        if (skillDiscoveryDirectoryValue.Length == 0)
                        {
                            skillDiscoveryDirectoryValueFault = true;
                        }
                        else
                        {
                            skillDiscoveryDirectories.Add(skillDiscoveryDirectoryValue);
                        }
                    }
                    else
                    {
                        skillDiscoveryDirectoryValueFault = true;
                    }

                    break;

                default:
                    return Failure($"unknown local-monitor option '{args[index]}'.");
            }
        }

        // Every other option fails on its own first fault in argv order. These five Skill-discovery
        // faults instead resolve by a fixed priority evaluated once the scan is done, so the result is
        // independent of option/array order (docs/specifications/interfaces/skill-invocation-snapshot.md).
        if (skillDiscoveryProjectPathValueFault)
        {
            return Failure("--skill-discovery-project-path requires a value.");
        }

        if (skillDiscoveryDirectoryValueFault)
        {
            return Failure("--skill-discovery-directory requires a value.");
        }

        if (skillDiscoveryProjectPaths.Count > 16)
        {
            return Failure("local-monitor accepts at most 16 --skill-discovery-project-path values.");
        }

        if (skillDiscoveryDirectories.Count > 32)
        {
            return Failure("local-monitor accepts at most 32 --skill-discovery-directory values.");
        }

        if (sanitizedOnly && (skillDiscoveryProjectPaths.Count > 0 || skillDiscoveryDirectories.Count > 0))
        {
            return Failure("skill discovery options cannot be used with --sanitized-only.");
        }

        if (maxRequestBodyBytes is null)
        {
            var envValue = getEnvironmentVariable(MaxRequestBodyBytesEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (!TryParsePositiveInt(envValue, out var parsedMax))
                {
                    return Failure($"{MaxRequestBodyBytesEnvironmentVariable} requires a positive integer.");
                }

                maxRequestBodyBytes = parsedMax;
            }
        }

        if (ingestionStallThresholdSeconds is null)
        {
            var envValue = getEnvironmentVariable(IngestionStallThresholdSecondsEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (!TryParsePositiveInt(envValue, out var parsedStall))
                {
                    return Failure($"{IngestionStallThresholdSecondsEnvironmentVariable} requires a positive integer.");
                }

                ingestionStallThresholdSeconds = parsedStall;
            }
        }

        if (projectionLagThresholdSeconds is null)
        {
            var envValue = getEnvironmentVariable(ProjectionLagThresholdSecondsEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (!TryParsePositiveInt(envValue, out var parsedLag))
                {
                    return Failure($"{ProjectionLagThresholdSecondsEnvironmentVariable} requires a positive integer.");
                }

                projectionLagThresholdSeconds = parsedLag;
            }
        }

        return new MonitorOptionsParseResult(
            new MonitorOptions(
                databasePath,
                url,
                sanitizedOnly,
                maxRequestBodyBytes ?? DefaultMaxRequestBodyBytes,
                ingestionStallThresholdSeconds ?? DefaultIngestionStallThresholdSeconds,
                projectionLagThresholdSeconds ?? DefaultProjectionLagThresholdSeconds,
                applyRoots,
                pricingRegistryOverridePaths.AsReadOnly(),
                skillDiscoveryProjectPaths.AsReadOnly(),
                skillDiscoveryDirectories.AsReadOnly(),
                repositoryAiEnabled ?? DefaultExtendedAiEnabled,
                compareAiEnabled ?? DefaultExtendedAiEnabled),
            null);
    }

    internal static string? ValidateLoopbackHttpUrl(string candidateUrl, string context)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return $"{context} requires an http URL.";
        }

        if (!IsAllowedLoopbackHost(uri.Host))
        {
            return $"{context} only allows localhost, 127.0.0.1, or ::1.";
        }

        return null;
    }

    internal static bool IsAllowedLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.Ordinal)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || string.Equals(host, "[::1]", StringComparison.Ordinal);
    }

    private static bool TryParsePositiveInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0;
    }

    private static bool TryReadValue(string[] args, int index, [NotNullWhen(true)] out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[index + 1];
        return true;
    }

    private static MonitorOptionsParseResult Failure(string error)
    {
        return new MonitorOptionsParseResult(null, error);
    }
}

internal sealed record MonitorOptionsParseResult(
    MonitorOptions? Options,
    string? Error);

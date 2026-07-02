using CopilotAgentObservability.Telemetry;
using Microsoft.Extensions.Configuration;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed record CopilotAnalysisOptions(
    string DefaultProfile,
    string DefaultModel,
    IReadOnlyList<CopilotAnalysisProfileOption> Profiles,
    IReadOnlyList<CopilotAnalysisModelOption> Models,
    IReadOnlyList<string> ReasoningEfforts)
{
    private static readonly string[] DefaultReasoningEfforts = ["low", "medium", "high"];

    public static CopilotAnalysisOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("CopilotAnalysis");
        var profiles = BuildProfiles(section);
        var models = BuildModels(section);
        var defaultProfile = ReadNonEmpty(section["DefaultProfile"], "standard").ToLowerInvariant();
        if (!profiles.Any(profile => string.Equals(profile.Id, defaultProfile, StringComparison.Ordinal)))
        {
            defaultProfile = "standard";
        }

        var configuredDefaultModel = SanitizeCanvasOptionValue(
            ReadNonEmpty(section["DefaultModel"], ReadNonEmpty(section["Model"], "gpt-5")),
            "gpt-5");
        var defaultModel = models.Any(model => string.Equals(model.Id, configuredDefaultModel, StringComparison.Ordinal))
            ? configuredDefaultModel ?? models[0].Id
            : models[0].Id;
        var normalizedModels = models
            .Select(model => model with { IsDefault = string.Equals(model.Id, defaultModel, StringComparison.Ordinal) })
            .ToArray();

        return new CopilotAnalysisOptions(
            defaultProfile,
            defaultModel,
            profiles,
            normalizedModels,
            DefaultReasoningEfforts);
    }

    private static IReadOnlyList<CopilotAnalysisProfileOption> BuildProfiles(IConfigurationSection section)
    {
        var profilesSection = section.GetSection("Profiles");
        return
        [
            BuildProfile(profilesSection.GetSection("fast"), "fast", "Fast", 60, "low"),
            BuildProfile(profilesSection.GetSection("standard"), "standard", "Standard", 180, "medium"),
            BuildProfile(profilesSection.GetSection("deep"), "deep", "Deep", 600, "high"),
        ];
    }

    private static CopilotAnalysisProfileOption BuildProfile(
        IConfigurationSection section,
        string id,
        string displayName,
        int timeoutSeconds,
        string defaultReasoningEffort)
    {
        var configuredTimeout = section["TimeoutSeconds"];
        if (!int.TryParse(configuredTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTimeout)
            || parsedTimeout <= 0)
        {
            parsedTimeout = timeoutSeconds;
        }

        var reasoning = ReadNonEmpty(section["DefaultReasoningEffort"], defaultReasoningEffort).ToLowerInvariant();
        if (reasoning is not ("low" or "medium" or "high"))
        {
            reasoning = defaultReasoningEffort;
        }

        return new CopilotAnalysisProfileOption(
            id,
            ReadNonEmpty(section["DisplayName"], displayName),
            parsedTimeout,
            reasoning);
    }

    private static IReadOnlyList<CopilotAnalysisModelOption> BuildModels(IConfigurationSection section)
    {
        var modelsSection = section.GetSection("Models");
        var models = modelsSection.GetChildren()
            .Select(child =>
            {
                var id = SanitizeCanvasOptionValue(child.Key, fallback: null);
                if (id is null)
                {
                    return null;
                }

                var displayName = SanitizeCanvasOptionValue(
                    ReadNonEmpty(child["DisplayName"], id),
                    id) ?? id;
                var provider = SanitizeCanvasOptionValue(
                    ReadNonEmpty(child["Provider"], ReadNonEmpty(section["Provider:Type"], "copilot")),
                    "copilot") ?? "copilot";
                var supportsReasoning = bool.TryParse(child["SupportsReasoningEffort"], out var parsed)
                    ? parsed
                    : true;
                return new CopilotAnalysisModelOption(id, displayName, provider, supportsReasoning, IsDefault: false);
            })
            .OfType<CopilotAnalysisModelOption>()
            .ToArray();

        if (models.Length > 0)
        {
            return models;
        }

        var defaultModel = SanitizeCanvasOptionValue(
            ReadNonEmpty(section["DefaultModel"], ReadNonEmpty(section["Model"], "gpt-5")),
            "gpt-5") ?? "gpt-5";
        return
        [
            new CopilotAnalysisModelOption(
                defaultModel,
                defaultModel,
                SanitizeCanvasOptionValue(ReadNonEmpty(section["Provider:Type"], "copilot"), "copilot") ?? "copilot",
                SupportsReasoningEffort: true,
                IsDefault: true),
        ];
    }

    private static string ReadNonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? SanitizeCanvasOptionValue(string? value, string? fallback)
    {
        var candidate = MeasurementSanitizer.SanitizeFreeFormName(value);
        if (candidate is not null && !IsAbsoluteUri(candidate))
        {
            return candidate;
        }

        if (fallback is null)
        {
            return null;
        }

        var sanitizedFallback = MeasurementSanitizer.SanitizeFreeFormName(fallback);
        return sanitizedFallback is null || IsAbsoluteUri(sanitizedFallback) ? null : sanitizedFallback;
    }

    private static bool IsAbsoluteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && !string.IsNullOrWhiteSpace(uri.Scheme);
}

internal sealed record CopilotAnalysisProfileOption(
    string Id,
    string DisplayName,
    int TimeoutSeconds,
    string DefaultReasoningEffort);

internal sealed record CopilotAnalysisModelOption(
    string Id,
    string DisplayName,
    string Provider,
    bool SupportsReasoningEffort,
    bool IsDefault);

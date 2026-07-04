namespace CopilotAgentObservability.ConfigCli;

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

using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Capabilities;

internal static class SourceCapabilityManifestLoader
{
    private const string VsCodeSurface = "github-copilot-vscode";
    private const string CliSurface = "github-copilot-cli";
    private const string VsCodeResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-vscode.json";
    private const string CliResourceName = "CopilotAgentObservability.ConfigCli.Setup.Capabilities.Manifests.github-copilot-cli.json";

    public static SourceCapabilityManifest? LoadForTarget(GitHubCopilotSetupTarget target) => target switch
    {
        GitHubCopilotSetupTarget.VsCode => LoadForSurface(VsCodeSurface),
        GitHubCopilotSetupTarget.Cli => LoadForSurface(CliSurface),
        GitHubCopilotSetupTarget.AppSdk => null,
        _ => throw new InvalidDataException("Unsupported GitHub Copilot setup target."),
    };

    public static SourceCapabilityManifest LoadForSurface(string sourceSurface)
    {
        ArgumentNullException.ThrowIfNull(sourceSurface);

        return sourceSurface switch
        {
            VsCodeSurface => LoadEmbedded(VsCodeSurface, VsCodeResourceName),
            CliSurface => LoadEmbedded(CliSurface, CliResourceName),
            _ => throw new InvalidDataException("Unknown source capability manifest."),
        };
    }

    public static bool MatchesCanonical(SourceCapabilityManifest canonicalManifest, JsonElement candidate)
    {
        ArgumentNullException.ThrowIfNull(canonicalManifest);
        return SemanticallyEqual(canonicalManifest.CanonicalJson, candidate);
    }

    public static bool MatchesCanonical(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object ||
            !candidate.TryGetProperty("source_surface", out var sourceSurfaceElement) ||
            sourceSurfaceElement.ValueKind != JsonValueKind.String ||
            sourceSurfaceElement.GetString() is not { } sourceSurface)
        {
            return false;
        }

        try
        {
            return MatchesCanonical(LoadForSurface(sourceSurface), candidate);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static SourceCapabilityManifest LoadEmbedded(string expectedSurface, string resourceName)
    {
        using var stream = typeof(SourceCapabilityManifestLoader).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidDataException("Embedded source capability manifest is unavailable.");
        }

        try
        {
            using var document = JsonDocument.Parse(stream);
            var canonicalJson = document.RootElement.Clone();

            if (canonicalJson.ValueKind != JsonValueKind.Object ||
                !canonicalJson.TryGetProperty("source_surface", out var sourceSurface) ||
                sourceSurface.ValueKind != JsonValueKind.String ||
                !string.Equals(sourceSurface.GetString(), expectedSurface, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Embedded source capability manifest is invalid.");
            }

            return new SourceCapabilityManifest(expectedSurface, canonicalJson);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Embedded source capability manifest is invalid.");
        }
    }

    private static bool SemanticallyEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        return expected.ValueKind switch
        {
            JsonValueKind.Object => SemanticallyEqualObjects(expected, actual),
            JsonValueKind.Array => SemanticallyEqualArrays(expected, actual),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == actual.GetRawText(),
        };
    }

    private static bool SemanticallyEqualObjects(JsonElement expected, JsonElement actual)
    {
        var expectedProperties = expected.EnumerateObject().ToArray();
        var actualProperties = actual.EnumerateObject().ToArray();

        return expectedProperties.Length == actualProperties.Length &&
            expectedProperties.All(property => actual.TryGetProperty(property.Name, out var actualValue) && SemanticallyEqual(property.Value, actualValue));
    }

    private static bool SemanticallyEqualArrays(JsonElement expected, JsonElement actual)
    {
        return expected.GetArrayLength() == actual.GetArrayLength() &&
            expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => SemanticallyEqual(pair.First, pair.Second));
    }
}

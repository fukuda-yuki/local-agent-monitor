using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal sealed record ClaudeCodeVersionDetection(
    bool IsSupported,
    bool Detected,
    string? Version,
    string? FailureCode);

internal static class ClaudeCodeVersionDetector
{
    private const string VersionSuffix = " (Claude Code)";
    private const int MaximumVersionLength = 128;

    public static ClaudeCodeVersionDetection Detect(ISetupPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        SetupProcessObservation observation;
        try
        {
            observation = platform.ProcessRunner.Run("claude", ["--version"]);
        }
        catch (Exception)
        {
            return Unsupported(detected: true);
        }

        if (observation.Outcome == SetupProcessOutcome.NotFound)
        {
            return new ClaudeCodeVersionDetection(false, false, null, "target_not_installed");
        }

        if (observation.Outcome != SetupProcessOutcome.Completed || observation.ExitCode != 0)
        {
            return Unsupported(detected: true);
        }

        var version = ParseExactVersion(observation.StandardOutput);
        return version is not null && IsAtLeastMinimum(version)
            ? new ClaudeCodeVersionDetection(true, true, version, null)
            : Unsupported(detected: true);
    }

    private static ClaudeCodeVersionDetection Unsupported(bool detected) =>
        new(false, detected, null, "unsupported_version");

    private static string? ParseExactVersion(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var line = RemoveSingleFinalPlatformNewline(output);
        if (line is null ||
            line.Length <= VersionSuffix.Length ||
            !line.EndsWith(VersionSuffix, StringComparison.Ordinal) ||
            line.Contains('\r', StringComparison.Ordinal) ||
            line.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var version = line[..^VersionSuffix.Length];
        if (version.Length is 0 or > MaximumVersionLength)
        {
            return null;
        }

        var components = version.Split('.');
        return components.Length == 3 && components.All(IsCanonicalDecimal)
            ? version
            : null;
    }

    private static string? RemoveSingleFinalPlatformNewline(string output)
    {
        if (output.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return output[..^2];
        }

        return output.EndsWith('\n') ? output[..^1] : output;
    }

    private static bool IsCanonicalDecimal(string component)
    {
        if (component.Length == 0 || component.Length > MaximumVersionLength ||
            component.Length > 1 && component[0] == '0')
        {
            return false;
        }

        return component.All(value => value is >= '0' and <= '9');
    }

    private static bool IsAtLeastMinimum(string version)
    {
        var components = version.Split('.');
        return CompareDecimal(components[0], "2") switch
        {
            > 0 => true,
            < 0 => false,
            _ => CompareDecimal(components[1], "1") switch
            {
                > 0 => true,
                < 0 => false,
                _ => CompareDecimal(components[2], "207") >= 0,
            },
        };
    }

    private static int CompareDecimal(string left, string right) =>
        left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);
}

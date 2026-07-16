using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal enum ClaudeCodeExecutionContext
{
    WindowsNative,
    Wsl2Repository,
    UnsupportedNativeUnix,
}

internal sealed record ClaudeCodeExecutionContextDetection(
    ClaudeCodeExecutionContext Context,
    string? FailureCode);

internal static class ClaudeCodeExecutionContextDetector
{
    private const int MaximumKernelReleaseLength = 128;

    public static ClaudeCodeExecutionContextDetection Detect(
        ISetupPlatform platform,
        bool allowWsl2Routing)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (platform.OperatingSystem.Current == SetupPlanningOs.Windows)
        {
            return new ClaudeCodeExecutionContextDetection(
                ClaudeCodeExecutionContext.WindowsNative,
                allowWsl2Routing ? SetupCodes.InvalidArguments : null);
        }

        if (platform.OperatingSystem.Current == SetupPlanningOs.MacOs)
        {
            return Unsupported(allowWsl2Routing);
        }

        return IsVerifiedWsl2(platform)
            ? new ClaudeCodeExecutionContextDetection(
                ClaudeCodeExecutionContext.Wsl2Repository,
                allowWsl2Routing ? null : SetupCodes.Wsl2OptInRequired)
            : Unsupported(allowWsl2Routing);
    }

    private static ClaudeCodeExecutionContextDetection Unsupported(bool allowWsl2Routing) =>
        new(
            ClaudeCodeExecutionContext.UnsupportedNativeUnix,
            allowWsl2Routing ? SetupCodes.InvalidArguments : SetupCodes.UnsupportedTarget);

    private static bool IsVerifiedWsl2(ISetupPlatform platform)
    {
        string? distro;
        try
        {
            distro = platform.ProcessEnvironment.Get("WSL_DISTRO_NAME");
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(distro))
        {
            return false;
        }

        SetupProcessObservation kernel;
        try
        {
            kernel = platform.ProcessRunner.Run("uname", ["-r"]);
        }
        catch (Exception)
        {
            return false;
        }

        if (kernel.Outcome != SetupProcessOutcome.Completed || kernel.ExitCode != 0)
        {
            return false;
        }

        var release = RemoveSingleFinalPlatformNewline(kernel.StandardOutput);
        return release is { Length: > 0 and <= MaximumKernelReleaseLength } &&
            !release.Contains('\r', StringComparison.Ordinal) &&
            !release.Contains('\n', StringComparison.Ordinal) &&
            release.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RemoveSingleFinalPlatformNewline(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        if (output.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return output[..^2];
        }

        return output.EndsWith('\n') ? output[..^1] : output;
    }
}

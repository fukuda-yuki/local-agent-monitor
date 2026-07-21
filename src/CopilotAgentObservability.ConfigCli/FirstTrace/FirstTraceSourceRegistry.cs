using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.FirstTrace.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal enum FirstTraceSetupOwnership
{
    Managed,
    ManagedOnWindows,
    CallerManaged,
    ManagedCliAndCallerManagedAgentSdk,
}

internal enum FirstTraceSourceDetectionState
{
    Detected,
    NotDetected,
    Unavailable,
}

internal sealed record FirstTraceSourceRegistration(
    string SourceId,
    string DisplayLabel,
    string ExpectedDoctorAdapter,
    IReadOnlyList<string> AllowedInteractions,
    FirstTraceSetupOwnership SetupOwnership);

internal static class FirstTraceSourceRegistry
{
    private const string CopilotChatExtension = "github.copilot-chat";
    public static IReadOnlyList<FirstTraceSourceRegistration> Entries { get; } =
    [
        new("github-copilot-vscode", "GitHub Copilot in VS Code", "github-copilot-doctor", ["vscode-chat"], FirstTraceSetupOwnership.Managed),
        new("github-copilot-cli", "GitHub Copilot CLI", "github-copilot-doctor", ["cli"], FirstTraceSetupOwnership.ManagedOnWindows),
        new("github-copilot-app-sdk", "GitHub Copilot App/SDK", "github-copilot-doctor", ["app-sdk"], FirstTraceSetupOwnership.CallerManaged),
        new("claude-code", "Claude Code", "claude-code-otel", ["interactive-cli", "print", "agent-sdk"], FirstTraceSetupOwnership.ManagedCliAndCallerManagedAgentSdk),
    ];

    public static IReadOnlyList<IFirstTraceSourceAdapter> CreateAdapters(
        ISetupPlatform platform,
        Func<SetupOptions, SetupCommandResult>? setupDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        setupDispatch ??= SetupCompositionRoot.CreateSetupDispatch(platform);

        return
        [
            new GitHubCopilotFirstTraceAdapter(
                "github-copilot-vscode",
                setupDispatch,
                () => platform.Clock.UtcNow,
                platform),
            new GitHubCopilotFirstTraceAdapter(
                "github-copilot-cli",
                setupDispatch,
                () => platform.Clock.UtcNow,
                platform),
            new GitHubCopilotFirstTraceAdapter(
                "github-copilot-app-sdk",
                setupDispatch,
                () => platform.Clock.UtcNow,
                platform),
            new ClaudeCodeFirstTraceAdapter(platform),
        ];
    }

    public static IReadOnlyDictionary<string, FirstTraceSourceDetectionState> DetectSources(ISetupPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return new Dictionary<string, FirstTraceSourceDetectionState>(StringComparer.Ordinal)
        {
            ["github-copilot-vscode"] = DetectVsCode(platform),
            ["github-copilot-cli"] = DetectExecutable(platform, "copilot", ["version"]),
            ["github-copilot-app-sdk"] = FirstTraceSourceDetectionState.Unavailable,
            ["claude-code"] = DetectExecutable(platform, "claude", ["--version"]),
        };
    }

    private static FirstTraceSourceDetectionState DetectVsCode(ISetupPlatform platform)
    {
        var stable = ObserveExtensions(platform, "code");
        var insiders = ObserveExtensions(platform, "code-insiders");
        if (stable == ExtensionDetection.Present || insiders == ExtensionDetection.Present)
        {
            return FirstTraceSourceDetectionState.Detected;
        }
        return stable != ExtensionDetection.Unavailable && insiders != ExtensionDetection.Unavailable
            ? FirstTraceSourceDetectionState.NotDetected
            : FirstTraceSourceDetectionState.Unavailable;
    }

    private static ExtensionDetection ObserveExtensions(ISetupPlatform platform, string executable)
    {
        SetupProcessObservation observation;
        try
        {
            observation = platform.ProcessRunner.Run(executable, ["--list-extensions", "--show-versions"]);
        }
        catch
        {
            return ExtensionDetection.Unavailable;
        }
        if (observation.Outcome == SetupProcessOutcome.NotFound)
        {
            return ExtensionDetection.Absent;
        }
        if (observation.Outcome != SetupProcessOutcome.Completed || observation.ExitCode != 0)
        {
            return ExtensionDetection.Unavailable;
        }
        return observation.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('@', 2)[0].Trim())
            .Any(name => string.Equals(name, CopilotChatExtension, StringComparison.OrdinalIgnoreCase))
                ? ExtensionDetection.Present
                : ExtensionDetection.Absent;
    }

    private static FirstTraceSourceDetectionState DetectExecutable(
        ISetupPlatform platform,
        string executable,
        IReadOnlyList<string> arguments)
    {
        try
        {
            return platform.ProcessRunner.Run(executable, arguments).Outcome switch
            {
                SetupProcessOutcome.Completed => FirstTraceSourceDetectionState.Detected,
                SetupProcessOutcome.NotFound => FirstTraceSourceDetectionState.NotDetected,
                _ => FirstTraceSourceDetectionState.Unavailable,
            };
        }
        catch
        {
            return FirstTraceSourceDetectionState.Unavailable;
        }
    }

    private enum ExtensionDetection
    {
        Present,
        Absent,
        Unavailable,
    }
}

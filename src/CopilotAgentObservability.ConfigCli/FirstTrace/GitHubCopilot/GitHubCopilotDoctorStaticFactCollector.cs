using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.CopilotCli;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.VsCode;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.GitHubCopilot;

internal sealed class GitHubCopilotDoctorStaticFactCollector(ISetupPlatform platform)
{
    public DoctorFactSnapshot Collect(
        string target,
        string normalizedEndpoint,
        SetupCommandResult setup,
        DoctorFactSnapshot mapped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEndpoint);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(mapped);

        if (!TryGetCurrentTargets(setup, target, normalizedEndpoint, out var targets))
        {
            return mapped;
        }

        var source = CollectSource(target, targets);
        var endpoint = CollectEndpoint(normalizedEndpoint);
        return mapped with
        {
            InstallAndSourceVersion = new(
                endpoint.Classification == GitHubCopilotEndpointClassification.LocalMonitorLive
                    ? MonitorInstallStatus.Installed
                    : MonitorInstallStatus.Unknown,
                source.Version,
                source.Feature),
            ProcessReceiverAndPort = endpoint.Classification switch
            {
                GitHubCopilotEndpointClassification.LocalMonitorLive => new(
                    MonitorProcessStatus.Running,
                    ReceiverBindStatus.Bound,
                    PortOwnerStatus.Monitor),
                GitHubCopilotEndpointClassification.MonitorNotRunning => new(
                    MonitorProcessStatus.NotRunning,
                    ReceiverBindStatus.NotBound,
                    PortOwnerStatus.None),
                GitHubCopilotEndpointClassification.ForeignOwner => new(
                    MonitorProcessStatus.Unknown,
                    ReceiverBindStatus.Unknown,
                    PortOwnerStatus.Foreign),
                _ => new(
                    MonitorProcessStatus.Unknown,
                    ReceiverBindStatus.Unknown,
                    PortOwnerStatus.Unknown),
            },
            EndpointReachability = new(endpoint.Classification switch
            {
                GitHubCopilotEndpointClassification.LocalMonitorLive => ReachabilityStatus.Reachable,
                GitHubCopilotEndpointClassification.MonitorNotRunning or
                GitHubCopilotEndpointClassification.ForeignOwner => ReachabilityStatus.Unreachable,
                _ => ReachabilityStatus.Unknown,
            }),
        };
    }

    private SourceFacts CollectSource(string target, IReadOnlyList<SetupTargetResult> targets)
    {
        try
        {
            var observations = GitHubCopilotDetection.Observe(platform);
            return target switch
            {
                "cli" => CliSource(observations.CopilotCli),
                "vscode" => VsCodeSource(observations, targets),
                _ => SourceFacts.Unknown,
            };
        }
        catch
        {
            return SourceFacts.Unknown;
        }
    }

    private EndpointFacts CollectEndpoint(string normalizedEndpoint)
    {
        try
        {
            return new(GitHubCopilotEndpointProbe.Classify(platform, normalizedEndpoint));
        }
        catch
        {
            return EndpointFacts.Unknown;
        }
    }

    private static SourceFacts CliSource(ChannelObservation observation)
    {
        if (!observation.Detected)
        {
            return SourceFacts.Unknown;
        }

        return new(
            CopilotCliTargetPartition.IsSupportedVersion(observation.Version)
                ? SourceVersionStatus.Supported
                : observation.Version is null ? SourceVersionStatus.Unknown : SourceVersionStatus.Unsupported,
            SourceFeatureStatus.Available);
    }

    private SourceFacts VsCodeSource(
        GitHubCopilotObservations observations,
        IReadOnlyList<SetupTargetResult> targets)
    {
        var channels = targets.Select(target => target.TargetLabel switch
        {
            "vscode-stable-default-user-settings" => ("code", observations.VsCodeStable),
            "vscode-insiders-default-user-settings" => ("code-insiders", observations.VsCodeInsiders),
            _ => (string.Empty, new ChannelObservation(false, null)),
        }).ToArray();
        if (channels.Any(channel => !channel.Item2.Detected))
        {
            return SourceFacts.Unknown;
        }

        var versions = channels.Select(channel => channel.Item2.Version).ToArray();
        var version = versions.All(VsCodeTargetPartition.IsSupportedVersion)
            ? SourceVersionStatus.Supported
            : versions.Any(value => value is not null && !VsCodeTargetPartition.IsSupportedVersion(value))
                ? SourceVersionStatus.Unsupported
                : SourceVersionStatus.Unknown;
        var feature = channels.All(channel =>
            VsCodeTargetPartition.HasCopilotChatExtension(platform, channel.Item1))
                ? SourceFeatureStatus.Available
                : SourceFeatureStatus.Unavailable;
        return new(version, feature);
    }

    private static bool TryGetCurrentTargets(
        SetupCommandResult setup,
        string target,
        string normalizedEndpoint,
        out IReadOnlyList<SetupTargetResult> targets)
    {
        targets = [];
        if (!setup.Success || setup.Command != SetupCommand.Status ||
            !string.Equals(setup.Adapter, "github-copilot", StringComparison.Ordinal))
        {
            return false;
        }

        var matching = setup.ChangeSets.Where(changeSet =>
            string.Equals(changeSet.Adapter, "github-copilot", StringComparison.Ordinal) &&
            string.Equals(changeSet.SelectedTarget, target, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1 ||
            matching[0].State is not (SetupChangeSetState.Applied or SetupChangeSetState.NoChanges) ||
            matching[0].CurrentState != SetupCurrentState.Current ||
            matching[0].Targets.Count == 0 ||
            matching[0].Targets.Any(item =>
                item.CurrentState != SetupCurrentState.Current ||
                item.ReferenceState != SetupReferenceState.Desired ||
                !string.Equals(item.Endpoint, normalizedEndpoint, StringComparison.Ordinal)))
        {
            return false;
        }

        targets = matching[0].Targets;
        return true;
    }

    internal static bool TrySelectCurrentAuthority(
        SetupCommandResult setup,
        string target,
        string normalizedEndpoint,
        out SetupCommandResult selected)
    {
        selected = setup;
        if (!setup.Success || setup.Command != SetupCommand.Status ||
            !string.Equals(setup.Adapter, "github-copilot", StringComparison.Ordinal))
        {
            return false;
        }

        var matching = setup.ChangeSets.Where(changeSet =>
            string.Equals(changeSet.Adapter, "github-copilot", StringComparison.Ordinal) &&
            string.Equals(changeSet.SelectedTarget, target, StringComparison.Ordinal) &&
            changeSet.State is SetupChangeSetState.Applied or SetupChangeSetState.NoChanges &&
            changeSet.CurrentState == SetupCurrentState.Current &&
            changeSet.Targets.Count > 0 &&
            changeSet.Targets.All(item =>
                item.CurrentState == SetupCurrentState.Current &&
                item.ReferenceState == SetupReferenceState.Desired &&
                string.Equals(item.Endpoint, normalizedEndpoint, StringComparison.Ordinal))).ToArray();
        if (matching.Length != 1)
        {
            return false;
        }

        selected = setup with { ChangeSets = matching };
        return true;
    }

    private sealed record SourceFacts(SourceVersionStatus Version, SourceFeatureStatus Feature)
    {
        public static SourceFacts Unknown { get; } = new(SourceVersionStatus.Unknown, SourceFeatureStatus.Unknown);
    }

    private sealed record EndpointFacts(GitHubCopilotEndpointClassification? Classification)
    {
        public static EndpointFacts Unknown { get; } = new((GitHubCopilotEndpointClassification?)null);
    }
}

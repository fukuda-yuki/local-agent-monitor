using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal static class GitHubCopilotDoctorFactMapper
{
    private const string Adapter = "github-copilot";
    private const string ExpectedSourceAdapter = "github-copilot-doctor";
    private const string VsCodeTarget = "vscode";
    private const string CliTarget = "cli";
    private const string AppSdkTarget = "app-sdk";
    private const string VsCodeStableLabel = "vscode-stable-default-user-settings";
    private const string VsCodeInsidersLabel = "vscode-insiders-default-user-settings";
    private const string CliLabel = "copilot-cli-user-environment";
    private const string AppSdkLabel = "github-copilot-app-sdk-guidance";

    private static readonly string[] VsCodeRequiredSettings =
    [
        "github.copilot.chat.otel.enabled",
        "github.copilot.chat.otel.exporterType",
        "github.copilot.chat.otel.otlpEndpoint",
    ];

    private static readonly string[] CliRequiredSettings =
    [
        "COPILOT_OTEL_ENABLED",
        "COPILOT_OTEL_EXPORTER_TYPE",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
    ];

    internal static DoctorFactSnapshot FromSetup(
        SetupCommandResult setupResult,
        string selectedTarget,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(setupResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedTarget);

        var sourceSurface = selectedTarget switch
        {
            VsCodeTarget => "github-copilot-vscode",
            CliTarget => "github-copilot-cli",
            AppSdkTarget => "github-copilot-app-sdk",
            _ => throw new ArgumentException("The selected GitHub Copilot target is not supported by Doctor.", nameof(selectedTarget)),
        };

        if (!setupResult.Success ||
            !string.Equals(setupResult.Adapter, Adapter, StringComparison.Ordinal) ||
            setupResult.Command is not (SetupCommand.Plan or SetupCommand.Apply or SetupCommand.Rollback or SetupCommand.Status) ||
            setupResult.Targets is null ||
            setupResult.ChangeSets is null ||
            setupResult.Warnings is null)
        {
            throw new ArgumentException("The setup result is not a successful GitHub Copilot target result.", nameof(setupResult));
        }

        var selection = SelectTargets(setupResult, selectedTarget);
        var targets = selection.Targets;
        ValidateTargets(targets, selectedTarget);

        var isAppSdk = selectedTarget == AppSdkTarget;
        var isRollback = setupResult.Command == SetupCommand.Rollback;
        var isStatus = setupResult.Command == SetupCommand.Status;
        var endpointKnown = !isAppSdk && setupResult.Command is SetupCommand.Plan or SetupCommand.Apply &&
            targets.All(target => target.Endpoint is not null);
        var monitorNotRunning = setupResult.Warnings.Contains(SetupCodes.MonitorNotRunning, StringComparer.Ordinal);
        var endpoint = isAppSdk
            ? SettingDisposition.Unknown
            : GetSettingDisposition(setupResult.Command, targets, EndpointSetting(selectedTarget));
        var protocol = isAppSdk
            ? SettingDisposition.Unknown
            : Combine(ProtocolSettings(selectedTarget)
                .Select(setting => GetSettingDisposition(setupResult.Command, targets, setting)));
        var signal = isAppSdk
            ? SettingDisposition.Unknown
            : GetSettingDisposition(setupResult.Command, targets, SignalSetting(selectedTarget));
        var sourceDetected = targets.All(target => target.Detected);

        return new DoctorFactSnapshot(
            DoctorSchemaVersions.FactsV1,
            sourceSurface,
            ExpectedSourceAdapter,
            observedAt,
            null,
            [],
            new InstallAndSourceVersionFacts(
                endpointKnown && !monitorNotRunning ? MonitorInstallStatus.Installed : MonitorInstallStatus.Unknown,
                isAppSdk || isRollback || isStatus ? SourceVersionStatus.Unknown : SourceVersionStatus.Supported,
                isRollback || isStatus ? SourceFeatureStatus.Unknown :
                sourceDetected ? SourceFeatureStatus.Available : SourceFeatureStatus.Unavailable),
            new ProcessReceiverAndPortFacts(
                endpointKnown
                    ? monitorNotRunning ? MonitorProcessStatus.NotRunning : MonitorProcessStatus.Running
                    : MonitorProcessStatus.Unknown,
                endpointKnown
                    ? monitorNotRunning ? ReceiverBindStatus.NotBound : ReceiverBindStatus.Bound
                    : ReceiverBindStatus.Unknown,
                endpointKnown
                    ? monitorNotRunning ? PortOwnerStatus.None : PortOwnerStatus.Monitor
                    : PortOwnerStatus.Unknown),
            new SourceEffectiveConfigurationFacts(
                endpoint switch
                {
                    SettingDisposition.Match => EndpointAlignmentStatus.Match,
                    SettingDisposition.Mismatch => EndpointAlignmentStatus.Mismatch,
                    _ => EndpointAlignmentStatus.Unknown,
                }),
            new EndpointReachabilityFacts(
                endpointKnown
                    ? monitorNotRunning ? ReachabilityStatus.Unreachable : ReachabilityStatus.Reachable
                    : ReachabilityStatus.Unknown),
            new ProtocolAndSignalCompatibilityFacts(
                protocol switch
                {
                    SettingDisposition.Match => ProtocolStatus.HttpProtobuf,
                    SettingDisposition.Mismatch => ProtocolStatus.Mismatch,
                    _ => ProtocolStatus.Unknown,
                },
                signal switch
                {
                    SettingDisposition.Match => TraceSignalStatus.Enabled,
                    SettingDisposition.Mismatch => TraceSignalStatus.Disabled,
                    _ => TraceSignalStatus.Unknown,
                }),
            new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
            new LastIngestFacts(LastIngestOutcome.Unknown),
            new RawPersistenceFacts(RawPersistenceOutcome.Unknown),
            new ProjectionFacts(ProjectionOutcome.Unknown),
            new ExactSessionBindingFacts(ExactSessionBindingRequirement.Unknown, ExactSessionBindingOutcome.Unknown),
            new CompletenessAndContentFacts(
                DoctorCompleteness.Unknown,
                ContentCaptureStatus.Unknown,
                RawAccessStatus.Unknown),
            new RestartOrNewProcessFacts(RestartDisposition(
                setupResult.Command,
                selection.ChangeSetState,
                targets,
                isAppSdk)));
    }

    private static SelectedProjection SelectTargets(
        SetupCommandResult setupResult,
        string selectedTarget)
    {
        if (setupResult.Command != SetupCommand.Status)
        {
            if (setupResult.Targets.Count == 0 || setupResult.ChangeSets.Count != 0)
            {
                throw new ArgumentException("The setup result has an invalid direct target projection.", nameof(setupResult));
            }

            return new SelectedProjection(setupResult.Targets.ToArray(), null);
        }

        if (setupResult.Targets.Count != 0 || setupResult.ChangeSets.Any(changeSet => changeSet is null))
        {
            throw new ArgumentException("The status result has an invalid change-set projection.", nameof(setupResult));
        }

        var matching = setupResult.ChangeSets
            .Where(changeSet => string.Equals(changeSet.Adapter, Adapter, StringComparison.Ordinal) &&
                string.Equals(changeSet.SelectedTarget, selectedTarget, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length != 1 || matching[0].Targets is null || matching[0].Targets.Count == 0 ||
            matching[0].Targets.Any(target => target is null) ||
            matching[0].Targets.Any(target => target.TargetKind != SetupTargetKind.Guidance &&
                target.CurrentState is null or SetupCurrentState.NotApplicable) ||
            matching[0].CurrentState != AggregateCurrentState(matching[0].Targets))
        {
            throw new ArgumentException("The status result must contain exactly one valid matching change set.", nameof(setupResult));
        }

        return new SelectedProjection(matching[0].Targets.ToArray(), matching[0].State);
    }

    private static RestartRequirement RestartDisposition(
        SetupCommand command,
        SetupChangeSetState? changeSetState,
        IReadOnlyList<SetupTargetResult> targets,
        bool isAppSdk)
    {
        if (isAppSdk)
        {
            return RestartRequirement.Unknown;
        }

        if (targets.Any(target => target.RestartRequirement != SetupRestartRequirement.None))
        {
            return RestartRequirement.Required;
        }

        if (command != SetupCommand.Status)
        {
            return command == SetupCommand.Rollback
                ? RestartRequirement.Required
                : RestartRequirement.NotRequired;
        }

        if (targets.Any(target => target.CurrentState != SetupCurrentState.Current))
        {
            return RestartRequirement.Unknown;
        }

        if (changeSetState is (SetupChangeSetState.RolledBack or SetupChangeSetState.Restored) &&
            targets.All(target => target.ReferenceState == SetupReferenceState.Previous))
        {
            return RestartRequirement.Required;
        }

        if (changeSetState is (SetupChangeSetState.Applied or SetupChangeSetState.NoChanges) &&
            targets.All(target => target.ReferenceState == SetupReferenceState.Desired))
        {
            return RestartRequirement.NotRequired;
        }

        return RestartRequirement.Unknown;
    }

    private static SetupCurrentState AggregateCurrentState(IReadOnlyList<SetupTargetResult> targets)
    {
        var writableTargets = targets.Where(target => target.TargetKind != SetupTargetKind.Guidance).ToArray();
        if (writableTargets.Length == 0)
        {
            return SetupCurrentState.NotApplicable;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Diverged))
        {
            return SetupCurrentState.Diverged;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Stale))
        {
            return SetupCurrentState.Stale;
        }

        if (writableTargets.Any(target => target.CurrentState == SetupCurrentState.Unavailable))
        {
            return SetupCurrentState.Unavailable;
        }

        return writableTargets.All(target => target.CurrentState == SetupCurrentState.Current)
            ? SetupCurrentState.Current
            : SetupCurrentState.NotApplicable;
    }

    private static void ValidateTargets(IReadOnlyList<SetupTargetResult> targets, string selectedTarget)
    {
        if (targets.Any(target => target is null || target.Changes is null || target.Changes.Any(change => change is null)))
        {
            throw new ArgumentException("The setup result contains an invalid GitHub Copilot target.", nameof(targets));
        }

        var valid = selectedTarget switch
        {
            VsCodeTarget => targets.Count is 1 or 2 &&
                targets.All(target => target.TargetKind == SetupTargetKind.Json &&
                    target.TargetLabel is VsCodeStableLabel or VsCodeInsidersLabel &&
                    target.Detected && target.DetectedVersion is not null &&
                    HasExpectedSourceSurface(target, "github-copilot-vscode") &&
                    HasRequiredSettings(target, VsCodeRequiredSettings)) &&
                targets.Select(target => target.TargetLabel).Distinct(StringComparer.Ordinal).Count() == targets.Count,
            CliTarget => targets.Count == 1 &&
                targets[0] is { TargetKind: SetupTargetKind.Env, TargetLabel: CliLabel, Detected: true, DetectedVersion: not null } &&
                HasExpectedSourceSurface(targets[0], "github-copilot-cli") &&
                HasRequiredSettings(targets[0], CliRequiredSettings),
            AppSdkTarget => targets.Count == 1 &&
                targets[0] is
                {
                    TargetKind: SetupTargetKind.Guidance,
                    TargetLabel: AppSdkLabel,
                    Operation: SetupOperation.NoOp,
                    Endpoint: null,
                    Guidance.Kind: "caller_managed_sample",
                    Guidance.Language: "dotnet",
                } && targets[0].ExpectedResult is null && targets[0].Changes.Count == 0,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException("The setup result targets do not match the selected GitHub Copilot target.", nameof(targets));
        }
    }

    private static bool HasExpectedSourceSurface(SetupTargetResult target, string expectedSourceSurface)
    {
        if (target.ExpectedResult is not { ValueKind: System.Text.Json.JsonValueKind.Object } expectedResult ||
            !expectedResult.TryGetProperty("contract_version", out var contractVersion) ||
            !expectedResult.TryGetProperty("source_surface", out var sourceSurface) ||
            contractVersion.ValueKind != System.Text.Json.JsonValueKind.String ||
            sourceSurface.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(contractVersion.GetString(), "v1", StringComparison.Ordinal) &&
            string.Equals(sourceSurface.GetString(), expectedSourceSurface, StringComparison.Ordinal);
    }

    private static bool HasRequiredSettings(SetupTargetResult target, IReadOnlyList<string> requiredSettings) =>
        target.Changes.Count(change => requiredSettings.Contains(change.SettingKey, StringComparer.Ordinal)) == requiredSettings.Count &&
        requiredSettings.All(requiredSetting => target.Changes.Any(change =>
            string.Equals(change.SettingKey, requiredSetting, StringComparison.Ordinal)));

    private static SettingDisposition GetSettingDisposition(
        SetupCommand command,
        IReadOnlyList<SetupTargetResult> targets,
        string settingKey)
    {
        if (command == SetupCommand.Apply)
        {
            return SettingDisposition.Match;
        }

        var dispositions = targets.Select(target =>
        {
            var change = target.Changes.Single(item =>
                string.Equals(item.SettingKey, settingKey, StringComparison.Ordinal));
            return command switch
            {
                SetupCommand.Plan => change.Operation == SetupOperation.NoOp
                    ? SettingDisposition.Match
                    : change.Operation is SetupOperation.Add or SetupOperation.Replace or SetupOperation.Remove
                        ? SettingDisposition.Mismatch
                        : SettingDisposition.Unknown,
                SetupCommand.Rollback => PreviousStateDisposition(change.PreviousState),
                SetupCommand.Status => StatusDisposition(target, change),
                _ => SettingDisposition.Unknown,
            };
        });
        return Combine(dispositions);
    }

    private static SettingDisposition StatusDisposition(
        SetupTargetResult target,
        SetupMemberChangeResult change)
    {
        if (target.CurrentState != SetupCurrentState.Current)
        {
            return SettingDisposition.Unknown;
        }

        return target.ReferenceState switch
        {
            SetupReferenceState.Desired => SettingDisposition.Match,
            SetupReferenceState.Base or SetupReferenceState.Previous =>
                PreviousStateDisposition(change.PreviousState),
            _ => SettingDisposition.Unknown,
        };
    }

    private static SettingDisposition PreviousStateDisposition(string previousState) => previousState switch
    {
        "present_desired" or
        "managed" or
        "environment_override" or
        "process_absent_user_present_desired" or
        "process_present_desired_user_present_desired" or
        "process_present_different_user_present_desired" => SettingDisposition.Match,
        "absent" or
        "present_different" or
        "process_absent_user_absent" or
        "process_absent_user_present_different" or
        "process_present_desired_user_absent" or
        "process_present_desired_user_present_different" or
        "process_present_different_user_absent" or
        "process_present_different_user_present_different" => SettingDisposition.Mismatch,
        _ => SettingDisposition.Unknown,
    };

    private static SettingDisposition Combine(IEnumerable<SettingDisposition> dispositions)
    {
        var values = dispositions.ToArray();
        if (values.Any(value => value == SettingDisposition.Mismatch))
        {
            return SettingDisposition.Mismatch;
        }

        return values.Length != 0 && values.All(value => value == SettingDisposition.Match)
            ? SettingDisposition.Match
            : SettingDisposition.Unknown;
    }

    private static string EndpointSetting(string selectedTarget) => selectedTarget switch
    {
        VsCodeTarget => "github.copilot.chat.otel.otlpEndpoint",
        CliTarget => "OTEL_EXPORTER_OTLP_ENDPOINT",
        _ => throw new ArgumentOutOfRangeException(nameof(selectedTarget)),
    };

    private static IReadOnlyList<string> ProtocolSettings(string selectedTarget) => selectedTarget switch
    {
        VsCodeTarget => ["github.copilot.chat.otel.exporterType"],
        CliTarget => ["COPILOT_OTEL_EXPORTER_TYPE", "OTEL_EXPORTER_OTLP_PROTOCOL"],
        _ => throw new ArgumentOutOfRangeException(nameof(selectedTarget)),
    };

    private static string SignalSetting(string selectedTarget) => selectedTarget switch
    {
        VsCodeTarget => "github.copilot.chat.otel.enabled",
        CliTarget => "COPILOT_OTEL_ENABLED",
        _ => throw new ArgumentOutOfRangeException(nameof(selectedTarget)),
    };

    private enum SettingDisposition
    {
        Unknown,
        Match,
        Mismatch,
    }

    private sealed record SelectedProjection(
        IReadOnlyList<SetupTargetResult> Targets,
        SetupChangeSetState? ChangeSetState);
}

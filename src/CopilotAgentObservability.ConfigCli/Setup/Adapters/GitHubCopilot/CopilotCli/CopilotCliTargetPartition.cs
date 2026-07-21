using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.CopilotCli;

internal sealed class CopilotCliTargetPartition : IGitHubCopilotTargetPartition
{
    private const string TargetLabel = "copilot-cli-user-environment";
    private const string TraceProtocolOverride = "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL";
    private const string WindowsLocation = "current-user-windows";
    private const string MacOsLocation = "current-user-macos";
    private const string LinuxLocation = "current-user-linux";
    private static readonly MemberDefinition[] DefaultMembers =
    [
        new("COPILOT_OTEL_ENABLED", "true", "configured"),
        new("COPILOT_OTEL_EXPORTER_TYPE", "otlp-http", "configured"),
        new("OTEL_EXPORTER_OTLP_ENDPOINT", null, "configured_loopback"),
        new("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf", "configured"),
    ];

    public string TargetToken => "cli";

    public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var assessment = Assess(context);
        if (assessment.FailureCode is not null)
        {
            return new GitHubCopilotPartitionPlan(
                assessment.FailureCode,
                [],
                assessment.Warnings,
                assessment.NextActions);
        }

        return new GitHubCopilotPartitionPlan(
            null,
            [RenderRecord(context, assessment, context.Platform.Identifiers.CreateUuidV7())],
            assessment.Warnings,
            assessment.NextActions);
    }

    public SetupPlanResult<SetupRevalidation> Revalidate(
        GitHubCopilotPartitionContext context,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plannedChangeSet);

        if (!TryGetCliTargets(plan, plannedChangeSet, out var planTarget, out var ledgerTarget))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.RecoveryRequired);
        }

        if (!IsPlanningLocation(planTarget.TargetLocation))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.RecoveryRequired);
        }

        if (!string.Equals(planTarget.TargetLocation, WindowsLocation, StringComparison.Ordinal) ||
            context.Platform.OperatingSystem.Current != SetupPlanningOs.Windows)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget);
        }

        var assessment = Assess(context);
        if (assessment.FailureCode is not null)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(
                assessment.FailureCode,
                assessment.Warnings,
                assessment.NextActions);
        }

        var expected = RenderRecord(context, assessment, planTarget.RecordId);
        if (!MatchesPersistedRecord(expected, planTarget, ledgerTarget))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.RecoveryRequired);
        }

        return SetupPlanResult.Revalidated(assessment.Warnings, assessment.NextActions);
    }

    private static Assessment Assess(GitHubCopilotPartitionContext context)
    {
        var cli = context.Observations.CopilotCli;
        if (!cli.Detected)
        {
            return Assessment.Failure(SetupCodes.TargetNotInstalled, [SetupCodes.InstallCopilotCli]);
        }

        if (!IsSupportedVersion(cli.Version))
        {
            return Assessment.Failure(SetupCodes.UnsupportedVersion, [SetupCodes.UpgradeCopilotCli]);
        }

        var overrideState = ClassifyTraceProtocolOverride(context.Platform, context.Observations.PlanningOs);
        if (overrideState == TraceProtocolOverrideState.Conflicts)
        {
            return Assessment.Failure(
                SetupCodes.EnvironmentOverrideConflict,
                [SetupCodes.ReviewCliTraceProtocolOverride]);
        }

        var endpoint = context.Endpoint;
        if (endpoint == GitHubCopilotEndpointClassification.ForeignOwner)
        {
            return Assessment.Failure(SetupCodes.PortOwnedByForeignProcess, []);
        }

        var warnings = new List<string> { SetupCodes.ManagedPolicyUnverified };
        var actions = new List<string>();
        if (overrideState == TraceProtocolOverrideState.Matches)
        {
            warnings.Add(SetupCodes.CliTraceProtocolOverrideNotModified);
        }

        if (context.Request.IncludeContentCapture)
        {
            warnings.Add(SetupCodes.ContentCaptureSensitive);
            actions.Add(SetupCodes.ReviewContentCaptureWarning);
        }

        if (context.Observations.PlanningOs == SetupPlanningOs.Windows)
        {
            warnings.Add(SetupCodes.SharedUserEnvironmentAffectsOtherProcesses);
            actions.Add(SetupCodes.RestartTerminalSession);
        }

        if (endpoint == GitHubCopilotEndpointClassification.MonitorNotRunning)
        {
            warnings.Add(SetupCodes.MonitorNotRunning);
            actions.Add(SetupCodes.StartLocalMonitor);
        }

        return new Assessment(null, cli.Version, warnings, actions);
    }

    private static SetupChangeRecord RenderRecord(
        GitHubCopilotPartitionContext context,
        Assessment assessment,
        Guid recordId)
    {
        var definitions = context.Request.IncludeContentCapture
            ? DefaultMembers.Append(new MemberDefinition(
                "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT",
                "true",
                "configured")).ToArray()
            : DefaultMembers;
        var planningOs = context.Observations.PlanningOs;
        var processValues = ReadProcessValues(context.Platform, definitions);
        var userCapture = planningOs == SetupPlanningOs.Windows
            ? new UserEnvironmentSetupStep(context.Platform).Capture(definitions.Select(member => member.Name).ToArray())
            : null;
        var userValues = userCapture?.Members.ToDictionary(
            member => member.Name,
            member => member.Value,
            StringComparer.Ordinal);
        var members = new List<SetupPrivatePlanMember>(definitions.Length);
        var changes = new List<SetupMemberChangeResult>(definitions.Length);
        foreach (var definition in definitions)
        {
            var desiredValue = definition.DesiredValue ?? context.Request.Endpoint;
            var processCurrent = ToEnvironmentValue(processValues[definition.Name]);
            var userCurrent = planningOs == SetupPlanningOs.Windows
                ? userValues![definition.Name]
                : UserEnvironmentValue.Missing;
            var current = planningOs == SetupPlanningOs.Windows ? userCurrent : processCurrent;
            var memberOperation = current.Exists
                ? string.Equals(current.Value, desiredValue, StringComparison.Ordinal)
                    ? SetupOperation.NoOp
                    : SetupOperation.Replace
                : SetupOperation.Add;
            members.Add(new SetupPrivatePlanMember(definition.Name, memberOperation, desiredValue));
            changes.Add(new SetupMemberChangeResult(
                definition.Name,
                memberOperation,
                planningOs == SetupPlanningOs.Windows
                    ? DescribeWindowsState(processCurrent, userCurrent, desiredValue)
                    : DescribeEnvironmentState(processCurrent, desiredValue),
                definition.ConfiguredState,
                "none",
                false));
        }

        var changedOperations = members
            .Where(member => member.Operation != SetupOperation.NoOp)
            .Select(member => member.Operation)
            .Distinct()
            .ToArray();
        var operation = changedOperations.Length switch
        {
            0 => SetupOperation.NoOp,
            1 => changedOperations[0],
            _ => SetupOperation.Mixed,
        };
        return new SetupChangeRecord(
            recordId,
            SetupTargetKind.Env,
            PlanningLocation(planningOs),
            TargetLabel,
            userCapture?.AggregateHash ?? new string('0', 64),
            new SetupInlineDesiredState("environment-allowlist"),
            members,
            planningOs == SetupPlanningOs.Windows
                ? SetupRestartRequirement.RestartTerminalSession
                : SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                assessment.Version,
                operation,
                SetupEffectiveSource.Environment,
                context.Request.Endpoint,
                null,
                null,
                changes));
    }

    private static IReadOnlyDictionary<string, string?> ReadProcessValues(
        ISetupPlatform platform,
        IReadOnlyList<MemberDefinition> definitions)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            values.Add(definition.Name, platform.ProcessEnvironment.Get(definition.Name));
        }

        return values;
    }

    private static TraceProtocolOverrideState ClassifyTraceProtocolOverride(
        ISetupPlatform platform,
        SetupPlanningOs planningOs)
    {
        var processValue = platform.ProcessEnvironment.Get(TraceProtocolOverride);
        var userValue = planningOs == SetupPlanningOs.Windows
            ? platform.UserEnvironment.Get(TraceProtocolOverride)
            : null;
        if (IsConflictingOverride(processValue) || IsConflictingOverride(userValue))
        {
            return TraceProtocolOverrideState.Conflicts;
        }

        return string.Equals(processValue, "http/protobuf", StringComparison.Ordinal) ||
            string.Equals(userValue, "http/protobuf", StringComparison.Ordinal)
                ? TraceProtocolOverrideState.Matches
                : TraceProtocolOverrideState.Absent;
    }

    private static bool IsConflictingOverride(string? value) =>
        value is not null && !string.Equals(value, "http/protobuf", StringComparison.Ordinal);

    internal static bool IsSupportedVersion(string? version)
    {
        var match = version is null
            ? Match.Empty
            : Regex.Match(version, "^(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)");
        return match.Success &&
            int.TryParse(match.Groups["major"].Value, out var major) &&
            int.TryParse(match.Groups["minor"].Value, out var minor) &&
            int.TryParse(match.Groups["patch"].Value, out var patch) &&
            (major > 1 || major == 1 && (minor > 0 || minor == 0 && patch >= 4));
    }

    private static UserEnvironmentValue ToEnvironmentValue(string? value) =>
        value is null ? UserEnvironmentValue.Missing : UserEnvironmentValue.Present(value);

    private static string DescribeWindowsState(
        UserEnvironmentValue processCurrent,
        UserEnvironmentValue userCurrent,
        string desiredValue) =>
        $"process_{DescribeEnvironmentState(processCurrent, desiredValue)}_user_{DescribeEnvironmentState(userCurrent, desiredValue)}";

    private static string DescribeEnvironmentState(UserEnvironmentValue current, string desiredValue) =>
        !current.Exists
            ? "absent"
            : string.Equals(current.Value, desiredValue, StringComparison.Ordinal)
                ? "present_desired"
                : "present_different";

    private static string PlanningLocation(SetupPlanningOs planningOs) => planningOs switch
    {
        SetupPlanningOs.Windows => WindowsLocation,
        SetupPlanningOs.MacOs => MacOsLocation,
        SetupPlanningOs.Linux => LinuxLocation,
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static bool IsPlanningLocation(string value) =>
        value is WindowsLocation or MacOsLocation or LinuxLocation;

    private static bool TryGetCliTargets(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet,
        out SetupPrivatePlanTarget planTarget,
        out SetupLedgerTarget ledgerTarget)
    {
        planTarget = null!;
        ledgerTarget = null!;
        if (plan.Adapter != "github-copilot" || plannedChangeSet.Adapter != "github-copilot" ||
            plan.SelectedTarget != plannedChangeSet.SelectedTarget ||
            plan.SelectedTarget is not "cli" and not "all" ||
            plan.Targets is null || plannedChangeSet.Targets is null ||
            plan.SelectedTarget == "cli" && (plan.Targets.Count != 1 || plannedChangeSet.Targets.Count != 1))
        {
            return false;
        }

        var candidates = plan.Targets.SelectMany(candidatePlanTarget =>
            plannedChangeSet.Targets
                .Where(candidateLedgerTarget =>
                    candidatePlanTarget.RecordId == candidateLedgerTarget.RecordId &&
                    candidatePlanTarget.TargetKind == SetupTargetKind.Env &&
                    candidateLedgerTarget.TargetKind == SetupTargetKind.Env &&
                    candidateLedgerTarget.TargetLabel == TargetLabel &&
                    candidateLedgerTarget.OwningAdapter == "github-copilot" &&
                    candidatePlanTarget.Members is not null &&
                    candidateLedgerTarget.Members is not null &&
                    LedgerMembersEqual(candidatePlanTarget.Members, candidateLedgerTarget.Members))
                .Select(candidateLedgerTarget => (candidatePlanTarget, candidateLedgerTarget)))
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        planTarget = candidates[0].candidatePlanTarget;
        ledgerTarget = candidates[0].candidateLedgerTarget;
        return true;
    }

    private static bool MatchesPersistedRecord(
        SetupChangeRecord expected,
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget) =>
        expected.TargetKind == planTarget.TargetKind &&
        expected.TargetLocation == planTarget.TargetLocation &&
        expected.BaseStateHash == planTarget.BaseStateHash &&
        expected.DesiredState == planTarget.DesiredState &&
        MembersEqual(expected.Members, planTarget.Members) &&
        ledgerTarget.StatusProjection is not null &&
        ledgerTarget.RestartRequirement == expected.RestartRequirement &&
        ledgerTarget.StatusProjection.DetectedVersion == expected.StatusProjection.DetectedVersion &&
        ledgerTarget.StatusProjection.Operation == expected.StatusProjection.Operation &&
        ledgerTarget.StatusProjection.EffectiveSource == expected.StatusProjection.EffectiveSource &&
        ledgerTarget.StatusProjection.Endpoint == expected.StatusProjection.Endpoint &&
        LedgerMembersEqual(expected.Members, ledgerTarget.Members) &&
        ChangesEqual(expected.StatusProjection.Changes, ledgerTarget.StatusProjection.Changes);

    private static bool MembersEqual(
        IReadOnlyList<SetupPrivatePlanMember> left,
        IReadOnlyList<SetupPrivatePlanMember> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private static bool LedgerMembersEqual(
        IReadOnlyList<SetupPrivatePlanMember> expected,
        IReadOnlyList<SetupLedgerMember> persisted) =>
        expected.Count == persisted.Count && expected.Zip(persisted).All(pair =>
            pair.First.SettingKey == pair.Second.SettingKey &&
            pair.First.Operation == pair.Second.Operation);

    private static bool ChangesEqual(
        IReadOnlyList<SetupMemberChangeResult> left,
        IReadOnlyList<SetupMemberChangeResult> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private sealed record Assessment(
        string? FailureCode,
        string? Version,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> NextActions)
    {
        public static Assessment Failure(string code, IReadOnlyList<string> actions) =>
            new(code, null, [], actions);
    }

    private sealed record MemberDefinition(string Name, string? DesiredValue, string ConfiguredState);

    private enum TraceProtocolOverrideState
    {
        Absent,
        Matches,
        Conflicts,
    }
}

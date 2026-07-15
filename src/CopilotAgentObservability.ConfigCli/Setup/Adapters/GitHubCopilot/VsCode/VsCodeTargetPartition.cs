using System.Text;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Documents;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.VsCode;

internal sealed class VsCodeTargetPartition : IGitHubCopilotTargetPartition
{
    private const string StableExecutable = "code";
    private const string InsidersExecutable = "code-insiders";
    private const string CopilotChatExtension = "github.copilot-chat";
    private const string Enabled = "github.copilot.chat.otel.enabled";
    private const string ExporterType = "github.copilot.chat.otel.exporterType";
    private const string Endpoint = "github.copilot.chat.otel.otlpEndpoint";
    private const string CaptureContent = "github.copilot.chat.otel.captureContent";
    private const string StableLabel = "vscode-stable-default-user-settings";
    private const string InsidersLabel = "vscode-insiders-default-user-settings";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string TargetToken => "vscode";

    public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context)
    {
        var assessment = Assess(context, includeStatus: true);
        if (assessment.FailureCode is not null)
        {
            return Failure(assessment);
        }

        try
        {
            var records = assessment.Channels
                .Select(channel => CreateRecord(context, assessment, channel))
                .ToArray();
            var warnings = assessment.Warnings.ToList();
            var actions = assessment.NextActions.ToList();
            if (records.Any(record => record.RestartRequirement == SetupRestartRequirement.RestartVsCode))
            {
                actions.Add(SetupCodes.RestartVsCode);
            }

            return new GitHubCopilotPartitionPlan(null, records, warnings, actions);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or DecoderFallbackException)
        {
            return new GitHubCopilotPartitionPlan(SetupCodes.MalformedSettings, [], [], []);
        }
    }

    public SetupPlanResult<SetupRevalidation> Revalidate(
        GitHubCopilotPartitionContext context,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet)
    {
        var assessment = Assess(context, includeStatus: false);
        if (assessment.FailureCode is not null)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(
                assessment.FailureCode,
                assessment.Warnings,
                assessment.NextActions);
        }

        try
        {
            if (!MatchesPersistedState(context, assessment, plan, plannedChangeSet))
            {
                return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.RecoveryRequired);
            }

            return SetupPlanResult.Revalidated(assessment.Warnings, assessment.NextActions);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or DecoderFallbackException)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.RecoveryRequired);
        }
    }

    private static GitHubCopilotPartitionPlan Failure(Assessment assessment) =>
        new(assessment.FailureCode, [], assessment.Warnings, assessment.NextActions);

    private static Assessment Assess(GitHubCopilotPartitionContext context, bool includeStatus)
    {
        var observations = context.Observations;
        var channels = new List<Channel>();
        AddDetected(channels, GitHubCopilotVsCodeChannel.Stable, observations.VsCodeStable);
        AddDetected(channels, GitHubCopilotVsCodeChannel.Insiders, observations.VsCodeInsiders);
        if (channels.Count == 0)
        {
            return Assessment.Failure(SetupCodes.TargetNotInstalled, [], [SetupCodes.InstallVsCode]);
        }

        if (channels.Any(channel => !IsSupportedVersion(channel.Version)))
        {
            return Assessment.Failure(SetupCodes.UnsupportedVersion, [], [SetupCodes.UpgradeVsCode]);
        }

        if (channels.Any(channel => !HasCopilotChatExtension(context.Platform, channel.Executable)))
        {
            return Assessment.Failure(SetupCodes.TargetNotInstalled, [], [SetupCodes.InstallGitHubCopilotChatExtension]);
        }

        if (includeStatus)
        {
            foreach (var channel in channels)
            {
                channel.RestartRequirement = NeedsRestart(context.Platform, channel.Executable)
                    ? SetupRestartRequirement.RestartVsCode
                    : SetupRestartRequirement.None;
            }
        }

        var desiredPolicy = DesiredPolicyValues(context.Request);
        var policy = GitHubCopilotManagedPolicyResolver.Resolve(
            context.Platform,
            observations.PlanningOs,
            desiredPolicy);
        if (policy.MalformedSystems != GitHubCopilotManagedPolicyMalformedSystems.None)
        {
            return Assessment.Failure(SetupCodes.MalformedSettings, [], []);
        }

        if (policy.CopilotConstraints.Concat(policy.EnterprisePolicyConstraints)
            .Any(constraint => constraint.Comparison == ManagedConstraintComparison.DiffersFromDesired))
        {
            return Assessment.Failure(SetupCodes.ManagedPolicyConflict, [], []);
        }

        var environment = ReadEnvironment(context.Platform);
        var warnings = new List<string>();
        var actions = new List<string>();
        if (!policy.ServerTierVerifiable)
        {
            warnings.Add(SetupCodes.ManagedPolicyUnverified);
            actions.Add(SetupCodes.RunVsCodePolicyDiagnostics);
        }

        if (observations.StableHasNonDefaultProfiles || observations.InsidersHasNonDefaultProfiles)
        {
            warnings.Add(SetupCodes.VscodeNonDefaultProfilesNotModified);
        }

        var endpoint = context.Endpoint;
        if (endpoint == GitHubCopilotEndpointClassification.ForeignOwner)
        {
            return Assessment.Failure(SetupCodes.PortOwnedByForeignProcess, warnings, actions);
        }

        if (endpoint == GitHubCopilotEndpointClassification.MonitorNotRunning)
        {
            warnings.Add(SetupCodes.MonitorNotRunning);
            actions.Add(SetupCodes.StartLocalMonitor);
        }

        if (context.Request.IncludeContentCapture)
        {
            warnings.Add(SetupCodes.ContentCaptureSensitive);
            actions.Add(SetupCodes.ReviewContentCaptureWarning);
        }

        return new Assessment(null, channels, policy, environment, endpoint, warnings, actions);
    }

    private static void AddDetected(
        ICollection<Channel> channels,
        GitHubCopilotVsCodeChannel channel,
        ChannelObservation observation)
    {
        if (!observation.Detected)
        {
            return;
        }

        channels.Add(new Channel(
            channel,
            channel == GitHubCopilotVsCodeChannel.Stable ? StableExecutable : InsidersExecutable,
            channel == GitHubCopilotVsCodeChannel.Stable ? StableLabel : InsidersLabel,
            observation.Version,
            SetupRestartRequirement.None));
    }

    private static bool IsSupportedVersion(string? version)
    {
        var match = version is null ? Match.Empty : Regex.Match(version, "^(?<major>\\d+)\\.(?<minor>\\d+)\\.");
        return match.Success && int.TryParse(match.Groups["major"].Value, out var major) &&
            int.TryParse(match.Groups["minor"].Value, out var minor) &&
            (major > 1 || major == 1 && minor >= 128);
    }

    private static bool HasCopilotChatExtension(ISetupPlatform platform, string executable)
    {
        SetupProcessObservation observation;
        try
        {
            observation = platform.ProcessRunner.Run(executable, ["--list-extensions", "--show-versions"]);
        }
        catch (Exception)
        {
            return false;
        }

        return observation.Outcome == SetupProcessOutcome.Completed && observation.ExitCode == 0 &&
            observation.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('@', 2)[0].Trim())
                .Any(name => string.Equals(name, CopilotChatExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NeedsRestart(ISetupPlatform platform, string executable)
    {
        try
        {
            var observation = platform.ProcessRunner.Run(executable, ["--status"]);
            return observation.Outcome == SetupProcessOutcome.Completed && observation.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> DesiredPolicyValues(SetupPlanRequest request)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CopilotOtelEnabled"] = "true",
            ["CopilotOtelExporterType"] = "otlp-http",
            ["CopilotOtelEndpoint"] = request.Endpoint,
        };
        if (request.IncludeContentCapture)
        {
            desired["CopilotOtelCaptureContent"] = "true";
        }

        return desired;
    }

    private static EnvironmentOverrides ReadEnvironment(ISetupPlatform platform) => new(
        IsSet(platform, "COPILOT_OTEL_ENABLED"),
        IsSet(platform, "COPILOT_OTEL_PROTOCOL") || IsSet(platform, "OTEL_EXPORTER_OTLP_PROTOCOL"),
        IsSet(platform, "COPILOT_OTEL_ENDPOINT") || IsSet(platform, "OTEL_EXPORTER_OTLP_ENDPOINT"),
        IsSet(platform, "COPILOT_OTEL_CAPTURE_CONTENT"));

    private static bool IsSet(ISetupPlatform platform, string name)
    {
        try
        {
            return platform.UserEnvironment.Get(name) is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static SetupChangeRecord CreateRecord(
        GitHubCopilotPartitionContext context,
        Assessment assessment,
        Channel channel,
        Guid? recordId = null)
    {
        var location = GitHubCopilotDetection.GetDefaultSettingsPath(context.Platform, channel.Kind);
        var exists = context.Platform.FileSystem.FileExists(location);
        var bytes = exists ? context.Platform.FileSystem.ReadAllBytes(location) : [];
        var content = exists ? StrictUtf8.GetString(bytes) : "{}";
        var updates = new List<MemberUpdate>();
        content = UpdateBoolean(content, Enabled, true, assessment, environmentOverride: assessment.Environment.Enabled, updates);
        content = UpdateString(content, ExporterType, "otlp-http", "configured", assessment, environmentOverride: assessment.Environment.Protocol, updates);
        content = UpdateString(content, Endpoint, context.Request.Endpoint, "configured_loopback", assessment, environmentOverride: assessment.Environment.Endpoint, updates);
        if (context.Request.IncludeContentCapture)
        {
            content = UpdateBoolean(content, CaptureContent, true, assessment, environmentOverride: assessment.Environment.CaptureContent, updates);
        }

        var changes = updates.Select(update => update.Change).ToArray();
        var operation = changes.All(change => change.Operation == SetupOperation.NoOp)
            ? SetupOperation.NoOp
            : changes.Select(change => change.Operation).Distinct().Count() == 1
                ? changes[0].Operation
                : SetupOperation.Mixed;
        var effectiveSource = updates.Any(update => update.Managed)
            ? SetupEffectiveSource.ManagedPolicy
            : updates.Any(update => update.EnvironmentOverride)
                ? SetupEffectiveSource.Environment
                : updates.Any(update => update.Existed)
                    ? SetupEffectiveSource.UserSetting
                    : SetupEffectiveSource.Default;
        return new SetupChangeRecord(
            recordId ?? context.Platform.Identifiers.CreateUuidV7(),
            SetupTargetKind.Json,
            location,
            channel.Label,
            SetupHash.File(exists, bytes),
            content,
            updates.Select(update => new SetupPrivatePlanMember(update.Key, update.Change.Operation, update.DesiredValue)).ToArray(),
            channel.RestartRequirement,
            new SetupStatusProjection(
                true,
                channel.Version,
                operation,
                effectiveSource,
                context.Request.Endpoint,
                null,
                null,
                changes));
    }

    private static string UpdateBoolean(
        string content,
        string key,
        bool desired,
        Assessment assessment,
        bool environmentOverride,
        ICollection<MemberUpdate> updates)
    {
        var policyManaged = IsManaged(assessment.Policy, key);
        if (policyManaged || environmentOverride)
        {
            updates.Add(new MemberUpdate(
                key,
                desired ? "true" : "false",
                false,
                policyManaged,
                environmentOverride,
                new SetupMemberChangeResult(
                    key,
                    SetupOperation.NoOp,
                    policyManaged ? "managed" : "environment_override",
                    policyManaged ? "managed" : "environment_override",
                    policyManaged ? "none" : "environment_override",
                    policyManaged)));
            return content;
        }

        var document = JsoncSettingsDocument.Parse(content);
        if (document.TryGetBoolean(key, out var current))
        {
            var operation = current == desired ? SetupOperation.NoOp : SetupOperation.Replace;
            updates.Add(new MemberUpdate(key, desired ? "true" : "false", true, false, false,
                Change(key, operation, current == desired ? "present_desired" : "present_different", "configured", false)));
            return operation == SetupOperation.NoOp ? content : document.ReplaceBoolean(key, desired);
        }

        var updated = document.AddBoolean(key, desired);
        updates.Add(new MemberUpdate(key, desired ? "true" : "false", false, false, false,
            Change(key, SetupOperation.Add, "absent", "configured", false)));
        return updated;
    }

    private static string UpdateString(
        string content,
        string key,
        string desired,
        string configuredState,
        Assessment assessment,
        bool environmentOverride,
        ICollection<MemberUpdate> updates)
    {
        var policyManaged = IsManaged(assessment.Policy, key);
        if (policyManaged || environmentOverride)
        {
            updates.Add(new MemberUpdate(key, desired, false, policyManaged, environmentOverride,
                new SetupMemberChangeResult(
                    key,
                    SetupOperation.NoOp,
                    policyManaged ? "managed" : "environment_override",
                    policyManaged ? "managed" : "environment_override",
                    policyManaged ? "none" : "environment_override",
                    policyManaged)));
            return content;
        }

        var document = JsoncSettingsDocument.Parse(content);
        if (document.TryGetString(key, out var current))
        {
            var operation = string.Equals(current, desired, StringComparison.Ordinal) ? SetupOperation.NoOp : SetupOperation.Replace;
            updates.Add(new MemberUpdate(key, desired, true, false, false,
                Change(key, operation, operation == SetupOperation.NoOp ? "present_desired" : "present_different", configuredState, false)));
            return operation == SetupOperation.NoOp ? content : document.ReplaceString(key, desired);
        }

        var updated = document.AddString(key, desired);
        updates.Add(new MemberUpdate(key, desired, false, false, false,
            Change(key, SetupOperation.Add, "absent", configuredState, false)));
        return updated;
    }

    private static SetupMemberChangeResult Change(
        string key,
        SetupOperation operation,
        string previous,
        string next,
        bool managed) => new(key, operation, previous, next, "none", managed);

    private static bool IsManaged(GitHubCopilotManagedPolicyResolution policy, string settingKey) =>
        policy.CopilotConstraints.Concat(policy.EnterprisePolicyConstraints)
            .Any(constraint => constraint.Comparison == ManagedConstraintComparison.EqualToDesired &&
                string.Equals(MapPolicySetting(constraint.SettingKey), settingKey, StringComparison.Ordinal));

    private static string? MapPolicySetting(string key) => key switch
    {
        "CopilotOtelEnabled" => Enabled,
        "CopilotOtelExporterType" => ExporterType,
        "CopilotOtelEndpoint" => Endpoint,
        "CopilotOtelCaptureContent" => CaptureContent,
        _ => null,
    };

    private static bool MatchesPersistedState(
        GitHubCopilotPartitionContext context,
        Assessment assessment,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet ledger)
    {
        var labels = assessment.Channels.Select(channel => channel.Label).ToArray();
        var locations = assessment.Channels
            .Select(channel => GitHubCopilotDetection.GetDefaultSettingsPath(context.Platform, channel.Kind))
            .ToArray();
        var planTargets = plan.Targets.Where(target => locations.Contains(target.TargetLocation, StringComparer.Ordinal)).ToArray();
        var ledgerTargets = ledger.Targets.Where(target => labels.Contains(target.TargetLabel, StringComparer.Ordinal)).ToArray();
        if (planTargets.Length != labels.Length || ledgerTargets.Length != labels.Length)
        {
            return false;
        }

        foreach (var channel in assessment.Channels)
        {
            var location = GitHubCopilotDetection.GetDefaultSettingsPath(context.Platform, channel.Kind);
            var planTarget = planTargets.SingleOrDefault(target => target.TargetLocation == location);
            var ledgerTarget = ledgerTargets.SingleOrDefault(target => target.TargetLabel == channel.Label);
            if (planTarget is null || ledgerTarget is null || planTarget.TargetKind != SetupTargetKind.Json ||
                ledgerTarget.TargetKind != SetupTargetKind.Json || planTarget.RecordId != ledgerTarget.RecordId)
            {
                return false;
            }

            var exists = context.Platform.FileSystem.FileExists(location);
            var bytes = exists ? context.Platform.FileSystem.ReadAllBytes(location) : [];
            if (!string.Equals(planTarget.BaseStateHash, SetupHash.File(exists, bytes), StringComparison.Ordinal))
            {
                return false;
            }

            var record = CreateRecord(context, assessment, channel, planTarget.RecordId);
            if (!string.Equals(record.DesiredState, planTarget.DesiredState, StringComparison.Ordinal) ||
                !MembersEqual(record.Members, planTarget.Members) ||
                ledgerTarget.StatusProjection.EffectiveSource != record.StatusProjection.EffectiveSource ||
                ledgerTarget.StatusProjection.Endpoint != context.Request.Endpoint)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MembersEqual(
        IReadOnlyList<SetupPrivatePlanMember> left,
        IReadOnlyList<SetupPrivatePlanMember> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.SettingKey == pair.Second.SettingKey &&
            pair.First.Operation == pair.Second.Operation &&
            pair.First.DesiredValue == pair.Second.DesiredValue);

    private sealed class Channel(
        GitHubCopilotVsCodeChannel kind,
        string executable,
        string label,
        string? version,
        SetupRestartRequirement restartRequirement)
    {
        public GitHubCopilotVsCodeChannel Kind { get; } = kind;
        public string Executable { get; } = executable;
        public string Label { get; } = label;
        public string? Version { get; } = version;
        public SetupRestartRequirement RestartRequirement { get; set; } = restartRequirement;
    }

    private sealed record Assessment(
        string? FailureCode,
        IReadOnlyList<Channel> Channels,
        GitHubCopilotManagedPolicyResolution Policy,
        EnvironmentOverrides Environment,
        GitHubCopilotEndpointClassification Endpoint,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> NextActions)
    {
        public static Assessment Failure(string code, IReadOnlyList<string> warnings, IReadOnlyList<string> actions) =>
            new(code, [], EmptyPolicy, EnvironmentOverrides.None, GitHubCopilotEndpointClassification.LocalMonitorLive, warnings, actions);
    }

    private sealed record EnvironmentOverrides(bool Enabled, bool Protocol, bool Endpoint, bool CaptureContent)
    {
        public static EnvironmentOverrides None { get; } = new(false, false, false, false);
    }

    private sealed record MemberUpdate(
        string Key,
        string DesiredValue,
        bool Existed,
        bool Managed,
        bool EnvironmentOverride,
        SetupMemberChangeResult Change);

    private static readonly GitHubCopilotManagedPolicyResolution EmptyPolicy = new(
        GitHubCopilotManagedChannel.None,
        false,
        [],
        [],
        GitHubCopilotManagedPolicyMalformedSystems.None);
}

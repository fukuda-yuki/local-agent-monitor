using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.VsCode;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class VsCodeSetupAdapterTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private const string DefaultStablePath = "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-15T00:00:00Z");

    [Fact]
    public void TargetPartition_UsesTheFrozenVsCodeToken()
    {
        Assert.Equal("vscode", new VsCodeTargetPartition().TargetToken);
    }

    [Theory]
    [InlineData(SetupPlanningOs.Windows, "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json", "C:\\Users\\setup-test\\AppData\\Roaming\\Code - Insiders\\User\\settings.json")]
    [InlineData(SetupPlanningOs.MacOs, "/Users/setup-test/Library/Application Support/Code/User/settings.json", "/Users/setup-test/Library/Application Support/Code - Insiders/User/settings.json")]
    [InlineData(SetupPlanningOs.Linux, "/home/setup-test/.config/Code/User/settings.json", "/home/setup-test/.config/Code - Insiders/User/settings.json")]
    public void Plan_BothSupportedChannels_UsesDefaultProfilePathsAndStableThenInsidersOrder(
        SetupPlanningOs planningOs,
        string stablePath,
        string insidersPath)
    {
        var platform = CreatePlatform(planningOs);
        ScriptSupported(platform, "code", "1.128.0");
        ScriptSupported(platform, "code-insiders", "1.128.0");
        ScriptLiveEndpoint(platform);

        var plan = Plan(platform);

        Assert.Null(plan.FailureCode);
        Assert.Equal(
            ["vscode-stable-default-user-settings", "vscode-insiders-default-user-settings"],
            plan.Records.Select(record => record.TargetLabel));
        Assert.Equal([stablePath, insidersPath], plan.Records.Select(record => record.TargetLocation));
        Assert.All(plan.Records, record =>
        {
            Assert.Equal(SetupTargetKind.Json, record.TargetKind);
            Assert.Equal(
                [
                    "github.copilot.chat.otel.enabled",
                    "github.copilot.chat.otel.exporterType",
                    "github.copilot.chat.otel.otlpEndpoint",
                ],
                record.Members.Select(member => member.SettingKey));
            Assert.DoesNotContain("github.copilot.chat.otel.captureContent", record.Members.Select(member => member.SettingKey));
        });
    }

    [Theory]
    [InlineData("code", "vscode-stable-default-user-settings")]
    [InlineData("code-insiders", "vscode-insiders-default-user-settings")]
    public void Plan_OneSupportedChannel_ProducesOnlyItsPhysicalTarget(string executable, string targetLabel)
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, executable, "1.128.0");
        ScriptLiveEndpoint(platform);

        var plan = Plan(platform);

        Assert.Null(plan.FailureCode);
        Assert.Equal(targetLabel, Assert.Single(plan.Records).TargetLabel);
    }

    [Fact]
    public void Plan_DetectedChannelBelowTheFloor_FailsBeforeExtensionStatusOrEndpoint()
    {
        var platform = CreatePlatform();
        ScriptVersion(platform, "code", "1.127.0");

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.UnsupportedVersion, plan.FailureCode);
        Assert.Equal([SetupCodes.UpgradeVsCode], plan.NextActions);
        Assert.Empty(plan.Records);
        AssertNoOperation(platform, "--list-extensions --show-versions");
        AssertNoOperation(platform, "--status");
        AssertNoProbe(platform);
    }

    [Fact]
    public void Plan_NeitherChannelInstalled_UsesThePinnedCodeAndActionWithoutFurtherObservations()
    {
        var platform = CreatePlatform();

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.TargetNotInstalled, plan.FailureCode);
        Assert.Equal([SetupCodes.InstallVsCode], plan.NextActions);
        Assert.Empty(plan.Records);
        AssertNoOperation(platform, "--list-extensions --show-versions");
        AssertNoOperation(platform, "--status");
        AssertNoProbe(platform);
    }

    [Fact]
    public void Plan_InstalledChannelWithoutCopilotChatExtension_FailsWithoutStatusEndpointOrPartialPlan()
    {
        var platform = CreatePlatform();
        ScriptVersion(platform, "code", "1.128.0");
        platform.ScriptProcess(
            "code",
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "ms-dotnettools.csharp@1.0.0\n"));

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.TargetNotInstalled, plan.FailureCode);
        Assert.Equal([SetupCodes.InstallGitHubCopilotChatExtension], plan.NextActions);
        Assert.Empty(plan.Records);
        AssertNoOperation(platform, "--status");
        AssertNoProbe(platform);
        Assert.DoesNotContain(platform.Operations, operation => operation.Contains("--profile", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_EligibleChannels_RunsExactExtensionAndStatusCommandsInStableThenInsidersOrder()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "discarded"));
        ScriptSupported(platform, "code-insiders", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, 7, "discarded"));
        ScriptLiveEndpoint(platform);

        var plan = Plan(platform);

        Assert.Null(plan.FailureCode);
        Assert.Equal(
            [
                "process.run:code:--list-extensions --show-versions",
                "process.run:code-insiders:--list-extensions --show-versions",
                "process.run:code:--status",
                "process.run:code-insiders:--status",
            ],
            platform.Operations.Where(operation => operation.Contains("--list-extensions", StringComparison.Ordinal) || operation.Contains("--status", StringComparison.Ordinal)));
        Assert.Equal(SetupRestartRequirement.RestartVsCode, plan.Records[0].RestartRequirement);
        Assert.Equal(SetupRestartRequirement.None, plan.Records[1].RestartRequirement);
        Assert.Contains(SetupCodes.RestartVsCode, plan.NextActions);
        Assert.DoesNotContain(platform.Operations, operation => operation.Contains("--profile", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_NonDefaultProfilesAreOnlyObserved_AndNeverOpenedOrIncludedInThePlan()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedDirectory("C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\profiles\\non-default");

        var plan = Plan(platform);

        Assert.Null(plan.FailureCode);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified, SetupCodes.VscodeNonDefaultProfilesNotModified], plan.Warnings);
        Assert.DoesNotContain(plan.Records, record => record.TargetLocation.Contains("profiles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.StartsWith("file.read:", StringComparison.Ordinal) && operation.Contains("profiles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ManagedPolicyDifferenceFailsClosedBeforeEndpointOrSettingsRead()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            new SetupManagedObservation(SetupManagedOutcome.Present, "{\"CopilotOtelEnabled\":false}"u8.ToArray(), true));

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.ManagedPolicyConflict, plan.FailureCode);
        Assert.Empty(plan.Records);
        AssertNoProbe(platform);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.read:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy)]
    [InlineData(SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy)]
    public void Plan_DifferingCopilotOrEnterprisePolicyFailsClosed(SetupManagedLocation location)
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        platform.SeedManagedObservation(
            location,
            new SetupManagedObservation(SetupManagedOutcome.Present, "{\"CopilotOtelEndpoint\":\"http://127.0.0.1:9999\"}"u8.ToArray(), true));

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.ManagedPolicyConflict, plan.FailureCode);
        Assert.Empty(plan.Records);
        AssertNoProbe(platform);
    }

    [Fact]
    public void Plan_MalformedManagedSourceFailsClosed()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Present, "not-json"u8.ToArray(), true));

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.MalformedSettings, plan.FailureCode);
        Assert.Empty(plan.Records);
        AssertNoProbe(platform);
    }

    [Fact]
    public void Plan_EqualManagedPolicyIsReadOnlyAndDoesNotRewriteItsUserSetting()
    {
        const string settings = "{\n  \"github.copilot.chat.otel.enabled\": false\n}";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Present, "{\"CopilotOtelEnabled\":true}"u8.ToArray(), true));
        platform.SeedFile(DefaultStablePath, Encoding.UTF8.GetBytes(settings));

        var plan = Plan(platform);
        var record = Assert.Single(plan.Records);
        var enabled = Assert.Single(record.StatusProjection.Changes, change => change.SettingKey == "github.copilot.chat.otel.enabled");

        Assert.Null(plan.FailureCode);
        Assert.DoesNotContain(SetupCodes.ManagedPolicyUnverified, plan.Warnings);
        Assert.Equal(SetupOperation.NoOp, enabled.Operation);
        Assert.True(enabled.Managed);
        Assert.Equal(SetupEffectiveSource.ManagedPolicy, record.StatusProjection.EffectiveSource);
        Assert.Contains("\"github.copilot.chat.otel.enabled\": false", record.DesiredState, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_EnvironmentOverrideIsReportedReadOnlyWithoutReturningTheCliOnlyFailureCode()
    {
        const string marker = "ENVIRONMENT_OVERRIDE_MARKER";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedUserEnvironment("COPILOT_OTEL_ENDPOINT", marker);

        var plan = Plan(platform);
        var endpoint = Assert.Single(Assert.Single(plan.Records).StatusProjection.Changes,
            change => change.SettingKey == "github.copilot.chat.otel.otlpEndpoint");

        Assert.Null(plan.FailureCode);
        Assert.NotEqual(SetupCodes.EnvironmentOverrideConflict, plan.FailureCode);
        Assert.Equal(SetupOperation.NoOp, endpoint.Operation);
        Assert.Equal("environment_override", endpoint.Conflict);
        Assert.Equal(SetupEffectiveSource.Environment, Assert.Single(plan.Records).StatusProjection.EffectiveSource);
        Assert.DoesNotContain(marker, System.Text.Json.JsonSerializer.Serialize(Assert.Single(plan.Records).StatusProjection), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COPILOT_OTEL_ENABLED", "github.copilot.chat.otel.enabled")]
    [InlineData("COPILOT_OTEL_ENDPOINT", "github.copilot.chat.otel.otlpEndpoint")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "github.copilot.chat.otel.otlpEndpoint")]
    [InlineData("COPILOT_OTEL_PROTOCOL", "github.copilot.chat.otel.exporterType")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "github.copilot.chat.otel.exporterType")]
    [InlineData("COPILOT_OTEL_CAPTURE_CONTENT", "github.copilot.chat.otel.captureContent")]
    public void Plan_EachSharedEnvironmentMappingIsReportedWithoutDeletingIt(string variable, string settingKey)
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedUserEnvironment(variable, "ENVIRONMENT_MARKER");

        var plan = Plan(platform, includeContentCapture: variable == "COPILOT_OTEL_CAPTURE_CONTENT");
        var change = Assert.Single(Assert.Single(plan.Records).StatusProjection.Changes, change => change.SettingKey == settingKey);

        Assert.Equal(SetupOperation.NoOp, change.Operation);
        Assert.Equal("environment_override", change.Conflict);
        Assert.Equal("ENVIRONMENT_MARKER", platform.ReadUserEnvironment(variable));
    }

    [Fact]
    public void Plan_OnlyExplicitContentCaptureAddsTheSensitiveFourthMemberAndPreservesJsonc()
    {
        const string comment = "// keep this comment";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedFile(DefaultStablePath, Encoding.UTF8.GetBytes($"{{\n  {comment}\n  \"unrelated\": \"value\",\n}}"));

        var withoutCapture = Plan(platform);

        Assert.DoesNotContain("github.copilot.chat.otel.captureContent", Assert.Single(withoutCapture.Records).Members.Select(member => member.SettingKey));
        Assert.Contains(comment, Assert.Single(withoutCapture.Records).DesiredState, StringComparison.Ordinal);
        Assert.Contains("\"unrelated\": \"value\"", Assert.Single(withoutCapture.Records).DesiredState, StringComparison.Ordinal);

        var capturePlatform = CreatePlatform();
        ScriptSupported(capturePlatform, "code", "1.128.0");
        ScriptLiveEndpoint(capturePlatform);
        capturePlatform.SeedFile(DefaultStablePath, Encoding.UTF8.GetBytes("{}"));

        var withCapture = Plan(capturePlatform, includeContentCapture: true);

        Assert.Contains("github.copilot.chat.otel.captureContent", Assert.Single(withCapture.Records).Members.Select(member => member.SettingKey));
        Assert.Contains(SetupCodes.ContentCaptureSensitive, withCapture.Warnings);
        Assert.Contains(SetupCodes.ReviewContentCaptureWarning, withCapture.NextActions);
    }

    [Fact]
    public void Plan_ForeignEndpointFailsButRefusedEndpointRemainsUsableWithGuidance()
    {
        var foreign = CreatePlatform();
        ScriptSupported(foreign, "code", "1.128.0");
        foreign.ScriptHttpProbe(SetupHttpProbeObservation.TransportFailure);

        var rejected = Plan(foreign);

        Assert.Equal(SetupCodes.PortOwnedByForeignProcess, rejected.FailureCode);
        Assert.Empty(rejected.Records);

        var refused = CreatePlatform();
        ScriptSupported(refused, "code", "1.128.0");
        refused.ScriptHttpProbe(SetupHttpProbeObservation.Refused);

        var usable = Plan(refused);

        Assert.Null(usable.FailureCode);
        Assert.Contains(SetupCodes.MonitorNotRunning, usable.Warnings);
        Assert.Contains(SetupCodes.StartLocalMonitor, usable.NextActions);
    }

    [Theory]
    [InlineData(SetupProcessOutcome.Completed, 0, SetupRestartRequirement.RestartVsCode)]
    [InlineData(SetupProcessOutcome.Completed, null, SetupRestartRequirement.None)]
    [InlineData(SetupProcessOutcome.Completed, 1, SetupRestartRequirement.None)]
    [InlineData(SetupProcessOutcome.NotFound, null, SetupRestartRequirement.None)]
    [InlineData(SetupProcessOutcome.Failed, null, SetupRestartRequirement.None)]
    [InlineData(SetupProcessOutcome.TimedOut, null, SetupRestartRequirement.None)]
    public void Plan_StatusRestartRequirementRequiresCompletedZeroExitOnly(
        SetupProcessOutcome outcome,
        int? exitCode,
        SetupRestartRequirement expected)
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0", new SetupProcessObservation(outcome, exitCode, "STATUS_SECRET_MARKER"));
        ScriptLiveEndpoint(platform);

        var plan = Plan(platform);

        Assert.Equal(expected, Assert.Single(plan.Records).RestartRequirement);
        Assert.DoesNotContain("STATUS_SECRET_MARKER", System.Text.Json.JsonSerializer.Serialize(Assert.Single(plan.Records).StatusProjection), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Plan_TwoChannelsKeepIndependentRestartRequirementsAndDeduplicateTheTopLevelAction(
        bool stableRunning,
        bool insidersRunning)
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, stableRunning ? 0 : 1, "discard"));
        ScriptSupported(platform, "code-insiders", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, insidersRunning ? 0 : 1, "discard"));
        ScriptLiveEndpoint(platform);

        var plan = Plan(platform);

        Assert.Equal(stableRunning ? SetupRestartRequirement.RestartVsCode : SetupRestartRequirement.None, plan.Records[0].RestartRequirement);
        Assert.Equal(insidersRunning ? SetupRestartRequirement.RestartVsCode : SetupRestartRequirement.None, plan.Records[1].RestartRequirement);
        Assert.Equal(stableRunning || insidersRunning, plan.NextActions.Count(action => action == SetupCodes.RestartVsCode) == 1);
    }

    [Fact]
    public void Plan_ExistingSensitiveUnknownJsonNeverAppearsInThePublicProjection()
    {
        const string marker = "SETTINGS_SECRET_MARKER";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedFile(DefaultStablePath, Encoding.UTF8.GetBytes($"{{\"unknown\":\"{marker}\"}}"));

        var plan = Plan(platform);

        Assert.Null(plan.FailureCode);
        Assert.DoesNotContain(marker, System.Text.Json.JsonSerializer.Serialize(Assert.Single(plan.Records).StatusProjection), StringComparison.Ordinal);
        Assert.Contains(marker, Assert.Single(plan.Records).DesiredState, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_InvalidJsoncFailsWithTheFixedCodeWithoutLeakingItsRawContent()
    {
        const string marker = "INVALID_JSONC_SECRET_MARKER";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        platform.SeedFile(DefaultStablePath, Encoding.UTF8.GetBytes($"{{\"unknown\":\"{marker}\""));

        var plan = Plan(platform);
        var serializedFailure = System.Text.Json.JsonSerializer.Serialize(new
        {
            plan.FailureCode,
            plan.Warnings,
            plan.NextActions,
        });

        Assert.Equal(SetupCodes.MalformedSettings, plan.FailureCode);
        Assert.Empty(plan.Records);
        Assert.DoesNotContain(marker, serializedFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void Revalidate_RechecksCurrentInputsWithoutCallingStatusOrChangingTheRecordedRestartRequirement()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "discard"));
        ScriptLiveEndpoint(platform);
        var persisted = CreatePersisted(platform);
        var statusCalls = platform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);

        var result = Revalidate(platform, persisted.Plan, persisted.Ledger);

        Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        Assert.Equal(statusCalls, platform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));
        Assert.Equal(SetupRestartRequirement.RestartVsCode, Assert.Single(persisted.Ledger.Targets).RestartRequirement);
    }

    [Fact]
    public void Revalidate_ChangedDefaultProfileOrPersistedMemberRequiresRecovery()
    {
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        var persisted = CreatePersisted(platform);
        platform.SeedFile(DefaultStablePath, "{\"changed\":true}"u8.ToArray());
        ScriptSupported(platform, "code", "1.128.0");
        ScriptLiveEndpoint(platform);
        var statusCalls = platform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var result = Revalidate(platform, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result).Code);
        Assert.Equal(statusCalls, platform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));
    }

    [Fact]
    public void Revalidate_ChangedVersionExtensionPolicyAndEndpointReturnTheirCurrentFailureCodesWithoutStatus()
    {
        var version = CreatePlatform();
        ScriptSupported(version, "code", "1.128.0");
        ScriptLiveEndpoint(version);
        var persisted = CreatePersisted(version);
        ScriptVersion(version, "code", "1.127.0");
        var statusCalls = version.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var versionResult = Revalidate(version, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.UnsupportedVersion, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(versionResult).Code);
        Assert.Equal(statusCalls, version.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));

        var extension = CreatePlatform();
        ScriptSupported(extension, "code", "1.128.0");
        ScriptLiveEndpoint(extension);
        persisted = CreatePersisted(extension);
        ScriptVersion(extension, "code", "1.128.0");
        extension.ScriptProcess("code", ["--list-extensions", "--show-versions"], new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "other@1.0"));
        statusCalls = extension.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var extensionResult = Revalidate(extension, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.TargetNotInstalled, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(extensionResult).Code);
        Assert.Equal(statusCalls, extension.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));

        var policy = CreatePlatform();
        ScriptSupported(policy, "code", "1.128.0");
        ScriptLiveEndpoint(policy);
        persisted = CreatePersisted(policy);
        ScriptSupported(policy, "code", "1.128.0");
        policy.SeedManagedObservation(SetupManagedLocation.GitHubCopilotFileWindows,
            new SetupManagedObservation(SetupManagedOutcome.Present, "{\"CopilotOtelEnabled\":false}"u8.ToArray(), true));
        statusCalls = policy.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var policyResult = Revalidate(policy, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.ManagedPolicyConflict, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(policyResult).Code);
        Assert.Equal(statusCalls, policy.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));

        var endpoint = CreatePlatform();
        ScriptSupported(endpoint, "code", "1.128.0");
        ScriptLiveEndpoint(endpoint);
        persisted = CreatePersisted(endpoint);
        ScriptSupported(endpoint, "code", "1.128.0");
        endpoint.ScriptHttpProbe(SetupHttpProbeObservation.TransportFailure);
        statusCalls = endpoint.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var endpointResult = Revalidate(endpoint, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.PortOwnedByForeignProcess, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(endpointResult).Code);
        Assert.Equal(statusCalls, endpoint.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));
    }

    [Fact]
    public void Revalidate_PersistedMemberOrEffectiveSourceMismatchRequiresRecoveryWithoutStatus()
    {
        var memberPlatform = CreatePlatform();
        ScriptSupported(memberPlatform, "code", "1.128.0");
        ScriptLiveEndpoint(memberPlatform);
        var persisted = CreatePersisted(memberPlatform);
        var changedTarget = Assert.Single(persisted.Plan.Targets) with
        {
            Members =
            [
                new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.NoOp, "true"),
                .. Assert.Single(persisted.Plan.Targets).Members.Skip(1),
            ],
        };
        var changedPlan = persisted.Plan with { Targets = [changedTarget] };
        ScriptSupported(memberPlatform, "code", "1.128.0");
        ScriptLiveEndpoint(memberPlatform);
        var statusCalls = memberPlatform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var memberResult = Revalidate(memberPlatform, changedPlan, persisted.Ledger);

        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(memberResult).Code);
        Assert.Equal(statusCalls, memberPlatform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));

        var sourcePlatform = CreatePlatform();
        ScriptSupported(sourcePlatform, "code", "1.128.0");
        ScriptLiveEndpoint(sourcePlatform);
        persisted = CreatePersisted(sourcePlatform);
        sourcePlatform.SeedUserEnvironment("COPILOT_OTEL_ENDPOINT", "OVERRIDE_MARKER");
        ScriptSupported(sourcePlatform, "code", "1.128.0");
        ScriptLiveEndpoint(sourcePlatform);
        statusCalls = sourcePlatform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal));

        var sourceResult = Revalidate(sourcePlatform, persisted.Plan, persisted.Ledger);

        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(sourceResult).Code);
        Assert.Equal(statusCalls, sourcePlatform.Operations.Count(operation => operation.Contains("--status", StringComparison.Ordinal)));
    }

    [Fact]
    public void Plan_StatusStandardOutputNeverReachesThePersistedPlanOrLedger()
    {
        const string marker = "STATUS_OUTPUT_SECRET_MARKER";
        var platform = CreatePlatform();
        ScriptSupported(platform, "code", "1.128.0", new SetupProcessObservation(SetupProcessOutcome.Completed, 0, marker));
        ScriptLiveEndpoint(platform);

        var persisted = CreatePersisted(platform);

        Assert.DoesNotContain(marker, System.Text.Json.JsonSerializer.Serialize(persisted.Plan), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, System.Text.Json.JsonSerializer.Serialize(persisted.Ledger), StringComparison.Ordinal);
    }

    private static GitHubCopilotPartitionPlan Plan(SetupTestPlatform platform, bool includeContentCapture = false) =>
        new VsCodeTargetPartition().Plan(new GitHubCopilotPartitionContext(platform, CreateRequest(includeContentCapture)));

    private static SetupPlanResult<SetupRevalidation> Revalidate(
        SetupTestPlatform platform,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet ledger) =>
        new VsCodeTargetPartition().Revalidate(
            new GitHubCopilotPartitionContext(platform, CreateRequest(includeContentCapture: plan.Targets.Any(target => target.Members.Any(member => member.SettingKey == "github.copilot.chat.otel.captureContent")))),
            plan,
            ledger);

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet Ledger) CreatePersisted(SetupTestPlatform platform)
    {
        var partitionPlan = Plan(platform);
        Assert.Null(partitionPlan.FailureCode);
        var plan = new SetupPrivatePlan(
            1,
            Guid.Parse("00000000-0000-7000-8000-000000000010"),
            "github-copilot",
            "vscode",
            Timestamp,
            "1.2.3",
            partitionPlan.Records.Select(record => new SetupPrivatePlanTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLocation,
                record.BaseStateHash,
                record.DesiredState,
                record.Members)).ToArray());
        var ledger = new SetupLedgerChangeSet(
            plan.ChangeSetId,
            plan.Adapter,
            plan.SelectedTarget,
            plan.CreatedAt,
            plan.CreatedAt,
            plan.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            partitionPlan.Records.Select(record => new SetupLedgerTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLabel,
                plan.Adapter,
                record.Members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                record.BaseStateHash,
                null,
                null,
                null,
                SetupLedgerRollbackStatus.NotAvailable,
                record.RestartRequirement,
                record.StatusProjection,
                plan.ToolVersion)).ToArray());
        return (plan, ledger);
    }

    private static SetupPlanRequest CreateRequest(bool includeContentCapture) => new(
        "github-copilot",
        "vscode",
        Endpoint,
        includeContentCapture,
        Guid.Parse("00000000-0000-7000-8000-000000000010"),
        Timestamp,
        "1.2.3");

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs = SetupPlanningOs.Windows) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(Timestamp),
        SetupPlanningOs.MacOs => new SetupTestPlatform(Timestamp, "/Users/setup-test/Library/Application Support", SetupPathStyle.Unix, planningOs, "/Users/setup-test/Library/Application Support", "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(Timestamp, "/home/setup-test/.local/share", SetupPathStyle.Unix, planningOs, "/home/setup-test/.config", "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static void ScriptSupported(
        SetupTestPlatform platform,
        string executable,
        string version,
        SetupProcessObservation? status = null)
    {
        ScriptVersion(platform, executable, version);
        platform.ScriptProcess(
            executable,
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "GitHub.copilot-chat@0.26.0\n"));
        if (status is { } value)
        {
            platform.ScriptProcess(executable, ["--status"], value);
        }
    }

    private static void ScriptVersion(SetupTestPlatform platform, string executable, string version) =>
        platform.ScriptProcess(
            executable,
            ["--version"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, version + Environment.NewLine));

    private static void ScriptLiveEndpoint(SetupTestPlatform platform) => platform.ScriptHttpProbe(
        new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            17,
            "{\"status\":\"live\"}"u8.ToArray(),
            true));

    private static void AssertNoOperation(SetupTestPlatform platform, string fragment) =>
        Assert.DoesNotContain(platform.Operations, operation => operation.Contains(fragment, StringComparison.Ordinal));

    private static void AssertNoProbe(SetupTestPlatform platform) =>
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
}

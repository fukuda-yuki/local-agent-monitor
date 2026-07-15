using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.CopilotCli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class CopilotCliSetupAdapterTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private const string CliLabel = "copilot-cli-user-environment";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    private static readonly string[] DefaultMembers =
    [
        "COPILOT_OTEL_ENABLED",
        "COPILOT_OTEL_EXPORTER_TYPE",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
    ];

    [Fact]
    public void TargetPartition_UsesTheFrozenCliToken()
    {
        Assert.Equal("cli", new CopilotCliTargetPartition().TargetToken);
    }

    [Fact]
    public void Plan_WindowsProducesExactlyTheClosedEnvironmentAllowlist()
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);

        var plan = Plan(platform);
        var record = Assert.Single(plan.Records);

        Assert.Null(plan.FailureCode);
        Assert.Equal(SetupTargetKind.Env, record.TargetKind);
        Assert.Equal(CliLabel, record.TargetLabel);
        Assert.Equal(
            DefaultMembers,
            record.Members.Select(member => member.SettingKey));
        Assert.Equal(SetupRestartRequirement.RestartTerminalSession, record.RestartRequirement);
        Assert.Equal(SetupEffectiveSource.Environment, record.StatusProjection.EffectiveSource);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified, SetupCodes.SharedUserEnvironmentAffectsOtherProcesses], plan.Warnings);
        Assert.Equal([SetupCodes.RestartTerminalSession], plan.NextActions);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("managed.read:", StringComparison.Ordinal));
        AssertForbiddenEnvironmentKeys(record);
    }

    [Fact]
    public void Plan_DefaultDoesNotAddCaptureButExplicitOptInAddsOnlyTheFifthAllowedMember()
    {
        var withoutCapture = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(withoutCapture);
        var defaultPlan = Plan(withoutCapture);

        Assert.Equal(DefaultMembers, Assert.Single(defaultPlan.Records).Members.Select(member => member.SettingKey));

        var withCapture = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(withCapture);
        var capturePlan = Plan(withCapture, includeContentCapture: true);
        var record = Assert.Single(capturePlan.Records);

        Assert.Equal(
            DefaultMembers.Append("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"),
            record.Members.Select(member => member.SettingKey));
        Assert.Equal(
            [SetupCodes.ManagedPolicyUnverified, SetupCodes.ContentCaptureSensitive, SetupCodes.SharedUserEnvironmentAffectsOtherProcesses],
            capturePlan.Warnings);
        Assert.Equal(
            [SetupCodes.ReviewContentCaptureWarning, SetupCodes.RestartTerminalSession],
            capturePlan.NextActions);
        AssertForbiddenEnvironmentKeys(record);
    }

    [Theory]
    [InlineData(null, null, SetupOperation.Add, "process_absent_user_absent")]
    [InlineData(null, "true", SetupOperation.NoOp, "process_absent_user_present_desired")]
    [InlineData(null, "user-marker", SetupOperation.Replace, "process_absent_user_present_different")]
    [InlineData("true", null, SetupOperation.Add, "process_present_desired_user_absent")]
    [InlineData("true", "true", SetupOperation.NoOp, "process_present_desired_user_present_desired")]
    [InlineData("true", "user-marker", SetupOperation.Replace, "process_present_desired_user_present_different")]
    [InlineData("process-marker", null, SetupOperation.Add, "process_present_different_user_absent")]
    [InlineData("process-marker", "true", SetupOperation.NoOp, "process_present_different_user_present_desired")]
    [InlineData("process-marker", "user-marker", SetupOperation.Replace, "process_present_different_user_present_different")]
    public void Plan_WindowsReportsTheFullProcessUserStateMatrixButOnlyCurrentUserStateDeterminesTheMutation(
        string? processValue,
        string? userValue,
        SetupOperation expectedOperation,
        string expectedPreviousState)
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        if (processValue is not null)
        {
            platform.SeedProcessEnvironment("COPILOT_OTEL_ENABLED", processValue);
        }

        if (userValue is not null)
        {
            platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", userValue);
        }

        var plan = Plan(platform);
        var change = Assert.Single(Assert.Single(plan.Records).StatusProjection.Changes,
            member => member.SettingKey == "COPILOT_OTEL_ENABLED");

        Assert.Equal(expectedOperation, change.Operation);
        Assert.Equal(expectedPreviousState, change.PreviousState);
        Assert.Contains("process-environment.get:COPILOT_OTEL_ENABLED", platform.Operations);
        Assert.Contains("environment.get:COPILOT_OTEL_ENABLED", platform.Operations);
        Assert.Contains(SetupCodes.SharedUserEnvironmentAffectsOtherProcesses, plan.Warnings);
        Assert.Contains(SetupCodes.RestartTerminalSession, plan.NextActions);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http/protobuf", null)]
    [InlineData(null, "http/protobuf")]
    public void Plan_AbsentOrMatchingTraceOverrideIsNeverAddedToTheWriteAllowlist(
        string? processValue,
        string? userValue)
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        if (processValue is not null)
        {
            platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", processValue);
        }

        if (userValue is not null)
        {
            platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", userValue);
        }

        var plan = Plan(platform);
        var record = Assert.Single(plan.Records);

        Assert.Null(plan.FailureCode);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", record.Members.Select(member => member.SettingKey));
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", record.StatusProjection.Changes.Select(member => member.SettingKey));
        if (processValue is not null || userValue is not null)
        {
            Assert.Contains(SetupCodes.CliTraceProtocolOverrideNotModified, plan.Warnings);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_ConflictingTraceOverrideFailsBeforeARecordOrMutationArtifact(bool inCurrentProcess)
    {
        const string marker = "trace-override-secret-marker";
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        if (inCurrentProcess)
        {
            platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", marker);
        }
        else
        {
            platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", marker);
        }

        var plan = Plan(platform);

        Assert.Equal(SetupCodes.EnvironmentOverrideConflict, plan.FailureCode);
        Assert.Empty(plan.Records);
        Assert.Equal([SetupCodes.ReviewCliTraceProtocolOverride], plan.NextActions);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(plan), StringComparison.Ordinal);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_NotInstalledAndBelowFloorUseThePinnedCodeActionPairsBeforeEndpointOrEnvironmentReads()
    {
        var missing = CreatePlatform();
        var missingPlan = Plan(missing);

        Assert.Equal(SetupCodes.TargetNotInstalled, missingPlan.FailureCode);
        Assert.Equal([SetupCodes.InstallCopilotCli], missingPlan.NextActions);
        Assert.Empty(missingPlan.Records);
        Assert.DoesNotContain(missing.Operations, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(missing.Operations, operation => operation.Contains("environment.get:", StringComparison.Ordinal));

        var belowFloor = CreatePlatform();
        ScriptCliVersion(belowFloor, "1.0.3");
        var belowFloorPlan = Plan(belowFloor);

        Assert.Equal(SetupCodes.UnsupportedVersion, belowFloorPlan.FailureCode);
        Assert.Equal([SetupCodes.UpgradeCopilotCli], belowFloorPlan.NextActions);
        Assert.Empty(belowFloorPlan.Records);
        Assert.DoesNotContain(belowFloor.Operations, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_EndpointGatesForeignOwnerAndAllowsMissingMonitorWithGuidance()
    {
        var foreignOwner = CreatePlatform();
        ScriptCliVersion(foreignOwner, "1.0.4");
        foreignOwner.ScriptHttpProbe(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response, 200, 2, "{}"u8.ToArray(), true));

        var rejected = Plan(foreignOwner);

        Assert.Equal(SetupCodes.PortOwnedByForeignProcess, rejected.FailureCode);
        Assert.Empty(rejected.Records);

        var monitorAbsent = CreatePlatform();
        ScriptCliVersion(monitorAbsent, "1.0.4");
        monitorAbsent.ScriptHttpProbe(SetupHttpProbeObservation.Refused);

        var allowed = Plan(monitorAbsent);

        Assert.Null(allowed.FailureCode);
        Assert.Contains(SetupCodes.MonitorNotRunning, allowed.Warnings);
        Assert.Contains(SetupCodes.StartLocalMonitor, allowed.NextActions);
    }

    [Theory]
    [InlineData(SetupPlanningOs.MacOs, "current-user-macos")]
    [InlineData(SetupPlanningOs.Linux, "current-user-linux")]
    public void Plan_NonWindowsCreatesAnInspectableTaggedPlanButRevalidationRefusesWithoutArtifacts(
        SetupPlanningOs planningOs,
        string expectedPlanningTag)
    {
        var platform = CreatePlatform(planningOs);
        ScriptSupportedCliAndLiveEndpoint(platform);
        var planned = Plan(platform);
        var record = Assert.Single(planned.Records);
        var (privatePlan, ledger) = CreatePersistedPlan(record, planningOs);

        Assert.Null(planned.FailureCode);
        Assert.Equal(expectedPlanningTag, record.TargetLocation);
        Assert.Equal(SetupRestartRequirement.None, record.RestartRequirement);
        Assert.DoesNotContain(SetupCodes.SharedUserEnvironmentAffectsOtherProcesses, planned.Warnings);

        var revalidation = new CopilotCliTargetPartition().Revalidate(
            CreateContext(platform), privatePlan, ledger);

        var failure = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(revalidation);
        Assert.Equal(SetupCodes.UnsupportedTarget, failure.Code);
        Assert.Empty(failure.Warnings);
        Assert.Empty(failure.NextActions);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("environment.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.Contains(".zshrc", StringComparison.Ordinal) ||
            operation.Contains(".bashrc", StringComparison.Ordinal) ||
            operation.Contains(".profile", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidate_RejectsAnUnknownPrivatePlanningTagBeforeObservationsOrArtifacts()
    {
        var planPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(planPlatform);
        var record = Assert.Single(Plan(planPlatform).Records);
        var (privatePlan, ledger) = CreatePersistedPlan(record, SetupPlanningOs.Windows);
        var malformedPlan = privatePlan with
        {
            Targets = [privatePlan.Targets[0] with { TargetLocation = "current-user-unknown" }],
        };
        var revalidationPlatform = CreatePlatform();

        var result = new CopilotCliTargetPartition().Revalidate(
            CreateContext(revalidationPlatform), malformedPlan, ledger);

        var failure = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result);
        Assert.Equal(SetupCodes.RecoveryRequired, failure.Code);
        Assert.Empty(revalidationPlatform.Operations);
    }

    [Fact]
    public void PlanningTagIsPrivateSafeMetadataAndIsNotPartOfThePublicTargetProjection()
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        var record = Assert.Single(Plan(platform).Records);
        var (privatePlan, _) = CreatePersistedPlan(record, SetupPlanningOs.Windows);
        var privateBytes = SetupPlanStore.Serialize(privatePlan);

        Assert.Contains("current-user-windows", System.Text.Encoding.UTF8.GetString(privateBytes), StringComparison.Ordinal);
        Assert.Null(typeof(SetupPlanTarget).GetProperty("TargetLocation"));
    }

    [Fact]
    public void Revalidate_ConflictingTraceOverrideReturnsTheSameFailureBeforeAnyMutation()
    {
        var planPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(planPlatform);
        var record = Assert.Single(Plan(planPlatform).Records);
        var (privatePlan, ledger) = CreatePersistedPlan(record, SetupPlanningOs.Windows);

        var revalidationPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(revalidationPlatform);
        revalidationPlatform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "revalidation-secret-marker");

        var result = new CopilotCliTargetPartition().Revalidate(
            CreateContext(revalidationPlatform), privatePlan, ledger);

        var failure = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result);
        Assert.Equal(SetupCodes.EnvironmentOverrideConflict, failure.Code);
        Assert.Equal([SetupCodes.ReviewCliTraceProtocolOverride], failure.NextActions);
        Assert.DoesNotContain("revalidation-secret-marker", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(revalidationPlatform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidate_WindowsRechecksVersionAndEveryPlannedMemberBeforeMutation()
    {
        var planPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(planPlatform);
        var record = Assert.Single(Plan(planPlatform).Records);
        var (privatePlan, ledger) = CreatePersistedPlan(record, SetupPlanningOs.Windows);

        var versionDrift = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(versionDrift, "1.0.5");
        var versionResult = new CopilotCliTargetPartition().Revalidate(
            CreateContext(versionDrift), privatePlan, ledger);

        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(versionResult).Code);
        Assert.DoesNotContain(versionDrift.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));

        var memberDrift = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(memberDrift);
        memberDrift.SeedUserEnvironment("COPILOT_OTEL_ENABLED", "true");
        var memberResult = new CopilotCliTargetPartition().Revalidate(
            CreateContext(memberDrift), privatePlan, ledger);

        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(memberResult).Code);
        Assert.DoesNotContain(memberDrift.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidate_WindowsMatchingPlanReturnsNoMaterializationOrMutation()
    {
        var planPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(planPlatform);
        var record = Assert.Single(Plan(planPlatform).Records);
        var (privatePlan, ledger) = CreatePersistedPlan(record, SetupPlanningOs.Windows);
        var revalidationPlatform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(revalidationPlatform);

        var result = new CopilotCliTargetPartition().Revalidate(
            CreateContext(revalidationPlatform), privatePlan, ledger);

        var success = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        Assert.Empty(success.Value.MaterializedTargets);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified, SetupCodes.SharedUserEnvironmentAffectsOtherProcesses], success.Warnings);
        Assert.Equal([SetupCodes.RestartTerminalSession], success.NextActions);
        Assert.DoesNotContain(revalidationPlatform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ExistingEnvironmentMarkerIsRedactedFromRecordsAndDiagnostics()
    {
        const string marker = "existing-environment-secret-marker";
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", marker);
        platform.SeedProcessEnvironment("COPILOT_OTEL_ENABLED", marker);

        var plan = Plan(platform);
        var record = Assert.Single(plan.Records);
        var serialized = JsonSerializer.Serialize(new { plan, record });

        Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, record.StatusProjection.Changes.Select(change => change.PreviousState));
        Assert.DoesNotContain(marker, record.StatusProjection.Changes.Select(change => change.NewState));
    }

    private static GitHubCopilotPartitionPlan Plan(SetupTestPlatform platform, bool includeContentCapture = false) =>
        new CopilotCliTargetPartition().Plan(CreateContext(platform, includeContentCapture));

    private static GitHubCopilotPartitionContext CreateContext(
        ISetupPlatform platform,
        bool includeContentCapture = false) => new(
            platform,
            new SetupPlanRequest(
                "github-copilot",
                "cli",
                Endpoint,
                includeContentCapture,
                Guid.Parse("00000000-0000-7000-8000-000000000006"),
                Timestamp,
                "1.2.3"));

    private static void ScriptSupportedCliAndLiveEndpoint(SetupTestPlatform platform, string version = "1.0.4")
    {
        ScriptCliVersion(platform, version);
        platform.ScriptHttpProbe(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            17,
            "{\"status\":\"live\"}"u8.ToArray(),
            true));
    }

    private static void ScriptCliVersion(SetupTestPlatform platform, string version) =>
        platform.ScriptProcess(
            "copilot",
            ["version"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, $"GitHub Copilot CLI {version}\n"));

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs = SetupPlanningOs.Windows) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(Timestamp),
        SetupPlanningOs.MacOs => new SetupTestPlatform(Timestamp, "/Users/setup-test/Library/Application Support", SetupPathStyle.Unix, planningOs, "/Users/setup-test/Library/Application Support", "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(Timestamp, "/home/setup-test/.local/share", SetupPathStyle.Unix, planningOs, "/home/setup-test/.config", "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet Ledger) CreatePersistedPlan(
        SetupChangeRecord record,
        SetupPlanningOs planningOs)
    {
        var privatePlan = new SetupPrivatePlan(
            1,
            Guid.Parse("00000000-0000-7000-8000-000000000006"),
            "github-copilot",
            "cli",
            Timestamp,
            "1.2.3",
            [new SetupPrivatePlanTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLocation,
                record.BaseStateHash,
                record.DesiredState,
                record.Members)]);
        var ledger = new SetupLedgerChangeSet(
            privatePlan.ChangeSetId,
            privatePlan.Adapter,
            privatePlan.SelectedTarget,
            privatePlan.CreatedAt,
            privatePlan.CreatedAt,
            privatePlan.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            [new SetupLedgerTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLabel,
                "github-copilot",
                record.Members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                record.BaseStateHash,
                null,
                null,
                null,
                SetupLedgerRollbackStatus.NotAvailable,
                record.RestartRequirement,
                record.StatusProjection,
                privatePlan.ToolVersion)]);
        Assert.Equal(planningOs == SetupPlanningOs.Windows ? "current-user-windows" : record.TargetLocation, record.TargetLocation);
        return (privatePlan, ledger);
    }

    private static void AssertForbiddenEnvironmentKeys(SetupChangeRecord record)
    {
        var keys = record.Members.Select(member => member.SettingKey).ToArray();
        Assert.DoesNotContain("client.kind", keys);
        Assert.DoesNotContain("OTEL_SERVICE_NAME", keys);
        Assert.DoesNotContain("OTEL_RESOURCE_ATTRIBUTES", keys);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_HEADERS", keys);
        Assert.DoesNotContain("COPILOT_OTEL_SOURCE_NAME", keys);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", keys);
        Assert.DoesNotContain(keys, key => key.Contains("TOKEN", StringComparison.Ordinal) || key.Contains("SECRET", StringComparison.Ordinal) || key.Contains("CREDENTIAL", StringComparison.Ordinal));
    }
}

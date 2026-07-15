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
    private static readonly string AppSdkGuidanceSample = SetupContractValidator.RehydrateStatusGuidance(
        new SetupStatusGuidance("caller_managed_sample", "dotnet")).Sample;
    private static readonly (string Key, string Value)[] DefaultMembers =
    [
        ("COPILOT_OTEL_ENABLED", "true"),
        ("COPILOT_OTEL_EXPORTER_TYPE", "otlp-http"),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", Endpoint),
        ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
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
        AssertExactMembers(record, DefaultMembers);
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

        AssertExactMembers(Assert.Single(defaultPlan.Records), DefaultMembers);

        var withCapture = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(withCapture);
        var capturePlan = Plan(withCapture, includeContentCapture: true);
        var record = Assert.Single(capturePlan.Records);

        AssertExactMembers(
            record,
            DefaultMembers.Append(("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true")));
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
    public void Revalidate_AllPlanRoutesTheOneCanonicalCliRecordAlongsideValidCompanionTargets()
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform);
        ScriptSupportedCliAndLiveEndpoint(platform);
        var vscode = new CompanionPartition("vscode", new GitHubCopilotPartitionPlan(null, [CreateVsCodeRecord()], [], []));
        var appSdk = new CompanionPartition("app-sdk", new GitHubCopilotPartitionPlan(null, [CreateAppSdkRecord()], [], []));
        var adapter = new GitHubCopilotSetupAdapter(platform, [vscode, new CopilotCliTargetPartition(), appSdk]);

        var planned = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("all")));
        var (privatePlan, ledger) = CreatePersistedAggregatePlan(planned.Value);

        var result = adapter.Revalidate(privatePlan, ledger);

        var success = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        Assert.Empty(success.Value.MaterializedTargets);
        Assert.Equal(
            ["vscode-stable-default-user-settings", CliLabel, "github-copilot-app-sdk-guidance"],
            planned.Value.Records.Select(record => record.TargetLabel));
        Assert.Equal(1, vscode.RevalidateCalls);
        Assert.Equal(0, appSdk.RevalidateCalls);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Revalidate_AllPlanFailsClosedForMissingDuplicateOrMalformedCliRecords()
    {
        var platform = CreatePlatform();
        ScriptSupportedCliAndLiveEndpoint(platform, repetitions: 3);
        var (_, _, validPlan, validLedger) = CreateValidAllAggregate(platform);
        var cliIndex = validLedger.Targets
            .Select((target, index) => (target, index))
            .Single(item => item.target.TargetLabel == CliLabel)
            .index;

        var missingPlan = validPlan with { Targets = validPlan.Targets.Where((_, index) => index != cliIndex).ToArray() };
        var missingLedger = validLedger with { Targets = validLedger.Targets.Where((_, index) => index != cliIndex).ToArray() };

        var duplicatePlan = validPlan with { Targets = validPlan.Targets.Append(validPlan.Targets[cliIndex] with { RecordId = Guid.Parse("00000000-0000-7000-8000-000000000099") }).ToArray() };
        var duplicateLedger = validLedger with { Targets = validLedger.Targets.Append(validLedger.Targets[cliIndex] with { RecordId = Guid.Parse("00000000-0000-7000-8000-000000000099") }).ToArray() };

        var malformedPlan = validPlan with { Targets = validPlan.Targets.Select((target, index) => index == cliIndex ? target with { TargetKind = SetupTargetKind.File } : target).ToArray() };

        AssertRecoveryRequired(new CopilotCliTargetPartition().Revalidate(CreateContext(platform, selectedTarget: "all"), missingPlan, missingLedger));
        AssertRecoveryRequired(new CopilotCliTargetPartition().Revalidate(CreateContext(platform, selectedTarget: "all"), duplicatePlan, duplicateLedger));
        AssertRecoveryRequired(new CopilotCliTargetPartition().Revalidate(CreateContext(platform, selectedTarget: "all"), malformedPlan, validLedger));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
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
        bool includeContentCapture = false,
        string selectedTarget = "cli") => new(platform, CreateRequest(selectedTarget, includeContentCapture));

    private static SetupPlanRequest CreateRequest(string selectedTarget, bool includeContentCapture = false) => new(
        "github-copilot",
        selectedTarget,
        Endpoint,
        includeContentCapture,
        Guid.Parse("00000000-0000-7000-8000-000000000006"),
        Timestamp,
        "1.2.3");

    private static void ScriptSupportedCliAndLiveEndpoint(
        SetupTestPlatform platform,
        string version = "1.0.4",
        int repetitions = 1)
    {
        for (var index = 0; index < repetitions; index++)
        {
            ScriptCliVersion(platform, version);
            platform.ScriptHttpProbe(new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                17,
                "{\"status\":\"live\"}"u8.ToArray(),
                true));
        }
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

    private static (GitHubCopilotSetupAdapter Adapter, SetupChangePlan Plan, SetupPrivatePlan PrivatePlan, SetupLedgerChangeSet Ledger)
        CreateValidAllAggregate(SetupTestPlatform platform)
    {
        var vscode = new CompanionPartition("vscode", new GitHubCopilotPartitionPlan(null, [CreateVsCodeRecord()], [], []));
        var appSdk = new CompanionPartition("app-sdk", new GitHubCopilotPartitionPlan(null, [CreateAppSdkRecord()], [], []));
        var adapter = new GitHubCopilotSetupAdapter(platform, [vscode, new CopilotCliTargetPartition(), appSdk]);
        var plan = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("all"))).Value;
        var (privatePlan, ledger) = CreatePersistedAggregatePlan(plan);
        return (adapter, plan, privatePlan, ledger);
    }

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet Ledger) CreatePersistedAggregatePlan(SetupChangePlan plan)
    {
        var privatePlan = new SetupPrivatePlan(
            1,
            plan.ChangeSetId,
            plan.Adapter,
            plan.SelectedTarget,
            plan.CreatedAt,
            plan.ToolVersion,
            plan.Records.Select(record => new SetupPrivatePlanTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLocation,
                record.BaseStateHash,
                record.DesiredState,
                record.Members)).ToArray());
        var ledger = new SetupLedgerChangeSet(
            privatePlan.ChangeSetId,
            privatePlan.Adapter,
            privatePlan.SelectedTarget,
            privatePlan.CreatedAt,
            privatePlan.CreatedAt,
            privatePlan.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            plan.Records.Select(record => new SetupLedgerTarget(
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
                privatePlan.ToolVersion)).ToArray());
        return (privatePlan, ledger);
    }

    private static SetupChangeRecord CreateVsCodeRecord() => new(
        Guid.Parse("00000000-0000-7000-8000-000000000007"),
        SetupTargetKind.Json,
        "C:\\setup-test-app-data\\Code\\User\\settings.json",
        "vscode-stable-default-user-settings",
        new string('a', 64),
        new SetupJsoncOwnedValuesDesiredState(
            new string('b', 64),
            [
                new SetupJsoncOwnedValue("github.copilot.chat.otel.enabled", "boolean", true),
                new SetupJsoncOwnedValue("github.copilot.chat.otel.exporterType", "string", "otlp-http"),
                new SetupJsoncOwnedValue("github.copilot.chat.otel.otlpEndpoint", "string", Endpoint),
            ]),
        [
            new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.Replace, "true"),
            new SetupPrivatePlanMember("github.copilot.chat.otel.exporterType", SetupOperation.Replace, "otlp-http"),
            new SetupPrivatePlanMember("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, Endpoint),
        ],
        SetupRestartRequirement.RestartVsCode,
        new SetupStatusProjection(
            true,
            "1.128.0",
            SetupOperation.Replace,
            SetupEffectiveSource.UserSetting,
            Endpoint,
            null,
            null,
            [
                new SetupMemberChangeResult("github.copilot.chat.otel.enabled", SetupOperation.Replace, "present_different", "configured", "none", false),
                new SetupMemberChangeResult("github.copilot.chat.otel.exporterType", SetupOperation.Replace, "present_different", "configured", "none", false),
                new SetupMemberChangeResult("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false),
            ]));

    private static SetupChangeRecord CreateAppSdkRecord() => new(
        Guid.Parse("00000000-0000-7000-8000-000000000008"),
        SetupTargetKind.Guidance,
        "app-sdk-guidance",
        "github-copilot-app-sdk-guidance",
        new string('b', 64),
        new SetupInlineDesiredState("app-sdk-guidance"),
        [],
        SetupRestartRequirement.None,
        new SetupStatusProjection(
            false,
            null,
            SetupOperation.NoOp,
            null,
            null,
            null,
            new SetupStatusGuidance("caller_managed_sample", "dotnet"),
            []),
        new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample));

    private static void AssertExactMembers(
        SetupChangeRecord record,
        IEnumerable<(string Key, string Value)> expected)
    {
        var pairs = expected.ToArray();
        Assert.Equal(pairs.Select(pair => pair.Key), record.Members.Select(member => member.SettingKey));
        Assert.Equal(pairs.Select(pair => pair.Value), record.Members.Select(member => member.DesiredValue));
    }

    private static void AssertRecoveryRequired(SetupPlanResult<SetupRevalidation> result) =>
        Assert.Equal(SetupCodes.RecoveryRequired, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result).Code);

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

    private sealed class CompanionPartition(string targetToken, GitHubCopilotPartitionPlan plan) : IGitHubCopilotTargetPartition
    {
        public string TargetToken => targetToken;

        public int RevalidateCalls { get; private set; }

        public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context) => plan;

        public SetupPlanResult<SetupRevalidation> Revalidate(
            GitHubCopilotPartitionContext context,
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidateCalls++;
            return SetupPlanResult.Revalidated();
        }
    }
}

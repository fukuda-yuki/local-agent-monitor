using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotSetupAdapterTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-14T00:00:00Z");

    [Theory]
    [InlineData("vscode")]
    [InlineData("cli")]
    [InlineData("app-sdk")]
    public void Plan_SelectedTarget_RoutesOnlyToItsOwningPartition(string selectedTarget)
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        var result = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest(selectedTarget)));

        Assert.Equal(selectedTarget == "vscode" ? 1 : 0, vscode.PlanCalls);
        Assert.Equal(selectedTarget == "cli" ? 1 : 0, cli.PlanCalls);
        Assert.Equal(selectedTarget == "app-sdk" ? 1 : 0, appSdk.PlanCalls);
        Assert.Equal(selectedTarget, Assert.Single(result.Value.Records).TargetLabel switch
        {
            "vscode-stable-default-user-settings" => "vscode",
            "copilot-cli-user-environment" => "cli",
            "github-copilot-app-sdk-guidance" => "app-sdk",
            _ => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void Plan_All_ConcatenatesFixedPartitionOrderAndDeduplicatesDiagnostics()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition(
            "vscode",
            CreatePlan("vscode", 1, [SetupCodes.ManagedPolicyUnverified, SetupCodes.MonitorNotRunning], [SetupCodes.RunVsCodePolicyDiagnostics, SetupCodes.StartLocalMonitor]));
        var cli = new ScriptedPartition(
            "cli",
            CreatePlan("cli", 2, [SetupCodes.MonitorNotRunning, SetupCodes.SharedUserEnvironmentAffectsOtherProcesses], [SetupCodes.StartLocalMonitor, SetupCodes.RestartTerminalSession]));
        var appSdk = new ScriptedPartition(
            "app-sdk",
            CreatePlan("app-sdk", 3, [SetupCodes.ManagedPolicyUnverified], [SetupCodes.StartLocalMonitor]));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        var result = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("all")));

        Assert.Equal(
            ["vscode-stable-default-user-settings", "copilot-cli-user-environment", "github-copilot-app-sdk-guidance"],
            result.Value.Records.Select(record => record.TargetLabel));
        Assert.Equal(
            [SetupCodes.ManagedPolicyUnverified, SetupCodes.MonitorNotRunning, SetupCodes.SharedUserEnvironmentAffectsOtherProcesses],
            result.Warnings);
        Assert.Equal(
            [SetupCodes.RunVsCodePolicyDiagnostics, SetupCodes.StartLocalMonitor, SetupCodes.RestartTerminalSession],
            result.NextActions);
        Assert.Equal(1, vscode.PlanCalls);
        Assert.Equal(1, cli.PlanCalls);
        Assert.Equal(1, appSdk.PlanCalls);
        Assert.Same(vscode.PlanContexts.Single(), cli.PlanContexts.Single());
        Assert.Same(vscode.PlanContexts.Single(), appSdk.PlanContexts.Single());
    }

    [Fact]
    public void Plan_UnsupportedTarget_ReturnsArtifactFreeFailureWithoutPartitionOrLazyAccess()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        var result = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(adapter.Plan(CreateRequest("other")));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(0, vscode.PlanCalls + cli.PlanCalls + appSdk.PlanCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_AppSdk_DoesNotAccessSharedObservationsOrEndpoint()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        var result = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("app-sdk")));

        Assert.Equal("github-copilot-app-sdk-guidance", Assert.Single(result.Value.Records).TargetLabel);
        Assert.Equal(1, appSdk.PlanCalls);
        Assert.Equal(0, vscode.PlanCalls + cli.PlanCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_All_StopsAtFirstPartitionFailureWithoutPartialRecordsOrLaterCalls()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition(
            "cli",
            new GitHubCopilotPartitionPlan(
                SetupCodes.TargetNotInstalled,
                [],
                [SetupCodes.ManagedPolicyUnverified],
                [SetupCodes.InstallCopilotCli]));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        var result = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(adapter.Plan(CreateRequest("all")));

        Assert.Equal(SetupCodes.TargetNotInstalled, result.Code);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.InstallCopilotCli], result.NextActions);
        Assert.Equal(1, vscode.PlanCalls);
        Assert.Equal(1, cli.PlanCalls);
        Assert.Equal(0, appSdk.PlanCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_UnexpectedPartitionException_Propagates()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", _ => throw new InvalidOperationException("unexpected"));
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        var exception = Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("vscode")));

        Assert.Equal("unexpected", exception.Message);
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("cli")]
    public void Plan_SuccessfulWritableTarget_CachesOneSharedDetectionAndProbe(string selectedTarget)
    {
        var platform = CreatePlatform();
        ScriptLiveEndpoint(platform);
        var vscode = new ScriptedPartition("vscode", context => AccessSharedState(context, CreatePlan("vscode", 1)));
        var cli = new ScriptedPartition("cli", context => AccessSharedState(context, CreatePlan("cli", 2)));
        var adapter = CreateAdapter(platform, vscode, cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        _ = adapter.Plan(CreateRequest(selectedTarget));

        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public void Plan_All_SharesOneDetectionAndProbeAcrossWritablePartitions()
    {
        var platform = CreatePlatform();
        ScriptLiveEndpoint(platform);
        var vscode = new ScriptedPartition("vscode", context => AccessSharedState(context, CreatePlan("vscode", 1)));
        var cli = new ScriptedPartition("cli", context => AccessSharedState(context, CreatePlan("cli", 2)));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);

        _ = adapter.Plan(CreateRequest("all"));

        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public void Plan_EarlyPartitionFailure_UsesRequiredObservationsButNeverProbes()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition(
            "vscode",
            context =>
            {
                _ = context.Observations;
                return new GitHubCopilotPartitionPlan(SetupCodes.UnsupportedVersion, [], [], [SetupCodes.UpgradeVsCode]);
            });
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        var result = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(adapter.Plan(CreateRequest("vscode")));

        Assert.Equal(SetupCodes.UnsupportedVersion, result.Code);
        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(0, ProbeCallCount(platform));
    }

    [Fact]
    public void Plan_AttachesTheCanonicalManifestForEachTargetSurface()
    {
        var platform = CreatePlatform();
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        var plan = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("all"))).Value;
        var vscode = plan.Records.Single(record => record.TargetLabel == "vscode-stable-default-user-settings");
        var cli = plan.Records.Single(record => record.TargetLabel == "copilot-cli-user-environment");
        var appSdk = plan.Records.Single(record => record.TargetLabel == "github-copilot-app-sdk-guidance");

        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!,
            vscode.StatusProjection.ExpectedResult!.Value));
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!,
            cli.StatusProjection.ExpectedResult!.Value));
        Assert.Null(appSdk.StatusProjection.ExpectedResult);
    }

    [Fact]
    public void Revalidate_RoutesNonGuidanceRecordsToTheirOwningPartitionsAndDeduplicatesDiagnostics()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (_, _, _) => SetupPlanResult.Revalidated([SetupCodes.ManagedPolicyUnverified], [SetupCodes.RunVsCodePolicyDiagnostics]),
        };
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (_, _, _) => SetupPlanResult.Revalidated([SetupCodes.ManagedPolicyUnverified, SetupCodes.MonitorNotRunning], [SetupCodes.RunVsCodePolicyDiagnostics, SetupCodes.StartLocalMonitor]),
        };
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan("all", "vscode", "cli", "app-sdk");

        var result = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(1, vscode.RevalidateCalls);
        Assert.Equal(1, cli.RevalidateCalls);
        Assert.Equal(0, appSdk.RevalidateCalls);
        Assert.Same(vscode.RevalidateContexts.Single(), cli.RevalidateContexts.Single());
        Assert.Equal([SetupCodes.ManagedPolicyUnverified, SetupCodes.MonitorNotRunning], result.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics, SetupCodes.StartLocalMonitor], result.NextActions);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_SuccessfulWritablePartitions_CacheOneSharedDetectionAndProbe()
    {
        var platform = CreatePlatform();
        ScriptLiveEndpoint(platform);
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (context, _, _) => AccessSharedState(context, SetupPlanResult.Revalidated()),
        };
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (context, _, _) => AccessSharedState(context, SetupPlanResult.Revalidated()),
        };
        var adapter = CreateAdapter(platform, vscode, cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("all", "vscode", "cli");

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public void Revalidate_EarlyPartitionFailure_UsesRequiredObservationsButNeverProbes()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (context, _, _) =>
            {
                _ = context.Observations;
                return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedVersion, [], [SetupCodes.UpgradeVsCode]);
            },
        };
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("vscode", "vscode");

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedVersion, result.Code);
        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(0, ProbeCallCount(platform));
    }

    [Fact]
    public void Revalidate_GuidanceOnly_SkipsPartitionsAndLazyAccess()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan("app-sdk", "app-sdk");

        var result = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(0, vscode.RevalidateCalls + cli.RevalidateCalls + appSdk.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Theory]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void Revalidate_NonWindowsCliRefusal_HappensBeforeLazyAccess(SetupPlanningOs planningOs)
    {
        var platform = CreatePlatform(planningOs);
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (_, _, _) => SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget),
        };
        var adapter = CreateAdapter(platform, new ScriptedPartition("vscode", CreatePlan("vscode", 1)), cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("cli", "cli");

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal(1, cli.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_UnknownPersistedTargetLabel_FailsClosedWithoutPartitionOrLazyAccess()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan("vscode", "unknown");

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal(0, vscode.RevalidateCalls + cli.RevalidateCalls + appSdk.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Constructor_RequiresExactlyTheThreeFrozenTargetPartitions()
    {
        var platform = CreatePlatform();

        Assert.Throws<ArgumentException>(() => new GitHubCopilotSetupAdapter(
            platform,
            [new ScriptedPartition("vscode", CreatePlan("vscode", 1)), new ScriptedPartition("cli", CreatePlan("cli", 2))]));
    }

    [Fact]
    public void NonGatingSmoke_RealRegistryDispatcherAndSetupJsonCarryTheAggregateAdapterAndCanonicalManifest()
    {
        var platform = CreatePlatform();
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
        var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
        var dispatcher = new SetupCommandDispatcher(
            platform,
            paths,
            planStore,
            ledgerStore,
            new SetupTransactionJournalStore(platform, paths),
            new SetupAdapterRegistry([adapter]),
            "1.2.3");

        var result = dispatcher.Dispatch(new SetupOptions(SetupCommand.Plan, "github-copilot", "vscode", Endpoint, false, null));
        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.PlanReady, result.Code);
        Assert.Equal("github-copilot", serialized.RootElement.GetProperty("adapter").GetString());
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result")));
        Assert.DoesNotContain(
            typeof(GitHubCopilotSetupAdapter).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.ReturnType == typeof(SetupCommandResult));
    }

    private static GitHubCopilotSetupAdapter CreateAdapter(
        ISetupPlatform platform,
        ScriptedPartition vscode,
        ScriptedPartition cli,
        ScriptedPartition appSdk) => new(platform, [vscode, cli, appSdk]);

    private static SetupPlanRequest CreateRequest(string selectedTarget) => new(
        "github-copilot",
        selectedTarget,
        Endpoint,
        false,
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

    private static void ScriptLiveEndpoint(SetupTestPlatform platform) => platform.ScriptHttpProbe(
        new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            17,
            "{\"status\":\"live\"}"u8.ToArray(),
            true));

    private static int ProcessCallCount(SetupTestPlatform platform) => platform.Operations.Count(operation => operation.StartsWith("process.run:", StringComparison.Ordinal));

    private static int ProbeCallCount(SetupTestPlatform platform) => platform.Operations.Count(operation => operation.StartsWith("http.get:", StringComparison.Ordinal));

    private static void AssertNoDetectionOrProbe(SetupTestPlatform platform)
    {
        Assert.Equal(0, ProcessCallCount(platform));
        Assert.Equal(0, ProbeCallCount(platform));
    }

    private static GitHubCopilotPartitionPlan CreatePlan(
        string target,
        int recordNumber,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? nextActions = null) => new(
            null,
            [CreateRecord(target, recordNumber)],
            warnings ?? [],
            nextActions ?? []);

    private static GitHubCopilotPartitionPlan AccessSharedState(
        GitHubCopilotPartitionContext context,
        GitHubCopilotPartitionPlan plan)
    {
        _ = context.Observations;
        _ = context.Endpoint;
        return plan;
    }

    private static SetupPlanResult<SetupRevalidation> AccessSharedState(
        GitHubCopilotPartitionContext context,
        SetupPlanResult<SetupRevalidation> result)
    {
        _ = context.Observations;
        _ = context.Endpoint;
        return result;
    }

    private static SetupChangeRecord CreateRecord(string target, int recordNumber)
    {
        var (targetKind, targetLabel, detected, version, operation, source, restart, endpoint, members, guidance, changes) = target switch
        {
            "vscode" => (
                SetupTargetKind.Json,
                "vscode-stable-default-user-settings",
                true,
                "1.128.0",
                SetupOperation.Replace,
                (SetupEffectiveSource?)SetupEffectiveSource.UserSetting,
                SetupRestartRequirement.RestartVsCode,
                Endpoint,
                (IReadOnlyList<SetupPrivatePlanMember>)[new("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, Endpoint)],
                (SetupGuidance?)null,
                (IReadOnlyList<SetupMemberChangeResult>)[new("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]),
            "cli" => (
                SetupTargetKind.Env,
                "copilot-cli-user-environment",
                true,
                "1.0.4",
                SetupOperation.Replace,
                (SetupEffectiveSource?)SetupEffectiveSource.Environment,
                SetupRestartRequirement.RestartTerminalSession,
                Endpoint,
                (IReadOnlyList<SetupPrivatePlanMember>)[new("COPILOT_OTEL_EXPORTER_OTLP_ENDPOINT", SetupOperation.Replace, Endpoint)],
                (SetupGuidance?)null,
                (IReadOnlyList<SetupMemberChangeResult>)[new("COPILOT_OTEL_EXPORTER_OTLP_ENDPOINT", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]),
            "app-sdk" => (
                SetupTargetKind.Guidance,
                "github-copilot-app-sdk-guidance",
                false,
                (string?)null,
                SetupOperation.NoOp,
                (SetupEffectiveSource?)null,
                SetupRestartRequirement.None,
                (string?)null,
                (IReadOnlyList<SetupPrivatePlanMember>)[],
                new SetupGuidance("caller_managed_sample", "dotnet", string.Empty),
                (IReadOnlyList<SetupMemberChangeResult>)[]),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        return new SetupChangeRecord(
            Guid.Parse($"00000000-0000-7000-8000-{recordNumber:D12}"),
            targetKind,
            $"private://{targetLabel}",
            targetLabel,
            new string('a', 64),
            "configured",
            members,
            restart,
            new SetupStatusProjection(detected, version, operation, source, endpoint, null, guidance is null ? null : new SetupStatusGuidance(guidance.Kind, guidance.Language), changes),
            guidance);
    }

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet ChangeSet) CreatePersistedPlan(string selectedTarget, params string[] targets)
    {
        var records = targets.Select((target, index) => target == "unknown"
            ? CreateRecord("vscode", index + 1) with { TargetLabel = "unknown" }
            : CreateRecord(target, index + 1)).ToArray();
        var plan = new SetupPrivatePlan(
            1,
            Guid.Parse("00000000-0000-7000-8000-000000000010"),
            "github-copilot",
            selectedTarget,
            Timestamp,
            "1.2.3",
            records.Select(record => new SetupPrivatePlanTarget(record.RecordId, record.TargetKind, record.TargetLocation, record.BaseStateHash, record.DesiredState, record.Members)).ToArray());
        var changeSet = new SetupLedgerChangeSet(
            plan.ChangeSetId,
            plan.Adapter,
            plan.SelectedTarget,
            plan.CreatedAt,
            plan.CreatedAt,
            plan.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            records.Select(record => new SetupLedgerTarget(
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
        return (plan, changeSet);
    }

    private sealed class ScriptedPartition : IGitHubCopilotTargetPartition
    {
        private readonly Func<GitHubCopilotPartitionContext, GitHubCopilotPartitionPlan> planHandler;

        public ScriptedPartition(
            string targetToken,
            GitHubCopilotPartitionPlan plan)
            : this(targetToken, _ => plan)
        {
        }

        public ScriptedPartition(
            string targetToken,
            Func<GitHubCopilotPartitionContext, GitHubCopilotPartitionPlan> planHandler)
        {
            TargetToken = targetToken;
            this.planHandler = planHandler;
        }

        public string TargetToken { get; }

        public Func<GitHubCopilotPartitionContext, SetupPrivatePlan, SetupLedgerChangeSet, SetupPlanResult<SetupRevalidation>> RevalidateHandler { get; init; } =
            (_, _, _) => SetupPlanResult.Revalidated();

        public int PlanCalls { get; private set; }

        public int RevalidateCalls { get; private set; }

        public List<GitHubCopilotPartitionContext> PlanContexts { get; } = [];

        public List<GitHubCopilotPartitionContext> RevalidateContexts { get; } = [];

        public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context)
        {
            PlanCalls++;
            PlanContexts.Add(context);
            return planHandler(context);
        }

        public SetupPlanResult<SetupRevalidation> Revalidate(
            GitHubCopilotPartitionContext context,
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidateCalls++;
            RevalidateContexts.Add(context);
            return RevalidateHandler(context, plan, plannedChangeSet);
        }
    }
}

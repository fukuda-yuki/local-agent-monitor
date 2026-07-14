using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly string AppSdkGuidanceSample = SetupContractValidator.RehydrateStatusGuidance(
        new SetupStatusGuidance("caller_managed_sample", "dotnet")).Sample;

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
    public void Plan_AppSdk_EmitsThePinnedCallerManagedGuidanceSample()
    {
        var platform = CreatePlatform();
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        var plan = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(CreateRequest("app-sdk"))).Value;
        var guidance = Assert.IsType<SetupGuidance>(Assert.Single(plan.Records).Guidance);

        Assert.Equal(AppSdkGuidanceSample, guidance.Sample);
    }

    [Fact]
    public void Plan_RejectsAnAppSdkGuidanceSampleThatDiffersFromThePinnedContract()
    {
        var platform = CreatePlatform();
        var malformed = CreateRecord("app-sdk", 3) with
        {
            Guidance = new SetupGuidance("caller_managed_sample", "dotnet", "not-the-pinned-sample"),
        };
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", new GitHubCopilotPartitionPlan(null, [malformed], [], [])));

        Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("app-sdk")));

        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_RejectsAnAppSdkRestartRequirementBeforePublishingOutput()
    {
        var platform = CreatePlatform();
        var malformed = CreateRecord("app-sdk", 3) with { RestartRequirement = SetupRestartRequirement.RestartVsCode };
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", new GitHubCopilotPartitionPlan(null, [malformed], [], [])));

        Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("app-sdk")));

        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_RejectsPartitionRecordsThatDoNotMatchTheOwningTargetShape()
    {
        var platform = CreatePlatform();
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", new GitHubCopilotPartitionPlan(null, [CreateRecord("cli", 1)], [], [])),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("vscode")));

        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_RejectsWritableAppSdkAndEndpointMismatchesBeforePublishingThePlan()
    {
        var platform = CreatePlatform();
        var writableGuidance = CreateRecord("app-sdk", 3) with
        {
            TargetKind = SetupTargetKind.Json,
            StatusProjection = CreateRecord("app-sdk", 3).StatusProjection with
            {
                Endpoint = Endpoint,
            },
        };
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", CreatePlan("vscode", 1)),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", new GitHubCopilotPartitionPlan(null, [writableGuidance], [], [])));

        Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("app-sdk")));

        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Plan_RejectsWrongWritableKindAndEndpointBeforePublishingThePlan()
    {
        var platform = CreatePlatform();
        var malformed = CreateRecord("vscode", 1) with
        {
            TargetKind = SetupTargetKind.Env,
            StatusProjection = CreateRecord("vscode", 1).StatusProjection with { Endpoint = "http://127.0.0.1:4318" },
        };
        var adapter = CreateAdapter(
            platform,
            new ScriptedPartition("vscode", new GitHubCopilotPartitionPlan(null, [malformed], [], [])),
            new ScriptedPartition("cli", CreatePlan("cli", 2)),
            new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));

        Assert.Throws<InvalidOperationException>(() => adapter.Plan(CreateRequest("vscode")));

        AssertNoDetectionOrProbe(platform);
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

    [Theory]
    [InlineData("vscode")]
    [InlineData("cli")]
    public void Revalidate_SelectedWritableTarget_RoutesOnlyToItsOwningPartition(string selectedTarget)
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan(selectedTarget, selectedTarget);

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(selectedTarget == "vscode" ? 1 : 0, vscode.RevalidateCalls);
        Assert.Equal(selectedTarget == "cli" ? 1 : 0, cli.RevalidateCalls);
        Assert.Equal(0, appSdk.RevalidateCalls);
    }

    [Fact]
    public void Revalidate_All_StopsAtTheFirstFailureWithoutCallingLaterPartitions()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (_, _, _) => SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedVersion, [], [SetupCodes.UpgradeCopilotCli]),
        };
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan("all", "vscode", "cli", "app-sdk");

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedVersion, result.Code);
        Assert.Equal([SetupCodes.UpgradeCopilotCli], result.NextActions);
        Assert.Equal(1, vscode.RevalidateCalls);
        Assert.Equal(1, cli.RevalidateCalls);
        Assert.Equal(0, appSdk.RevalidateCalls);
    }

    [Fact]
    public void Revalidate_UnexpectedPartitionException_Propagates()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (_, _, _) => throw new InvalidOperationException("unexpected"),
        };
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("vscode", "vscode");

        var exception = Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

        Assert.Equal("unexpected", exception.Message);
    }

    [Fact]
    public void Revalidate_RejectsSelectedTargetAndPersistedLabelDisagreementBeforeDispatch()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan("vscode", "cli");

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal(0, vscode.RevalidateCalls + cli.RevalidateCalls + appSdk.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_RejectsDuplicatePersistedPhysicalLabelsBeforeDispatch()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlanFromRecords("vscode", [CreateRecord("vscode", 1), CreateRecord("vscode", 2)]);

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal(0, vscode.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_RejectsEndpointAndManifestMismatchesBeforeDispatch()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("vscode", "vscode");
        var persisted = changeSet.Targets.Single() with
        {
            StatusProjection = changeSet.Targets.Single().StatusProjection with { Endpoint = "http://127.0.0.1:4318" },
        };

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet with { Targets = [persisted] }));

        Assert.Equal(0, vscode.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_AcceptsTheTargetMatchedStrictHistoricalManifestWithoutCurrentEquality()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var record = CreateRecord("vscode", 1) with
        {
            StatusProjection = CreateRecord("vscode", 1).StatusProjection with
            {
                ExpectedResult = CreateHistoricalManifest(GitHubCopilotSetupTarget.VsCode),
            },
        };
        var (plan, changeSet) = CreatePersistedPlanFromRecords("vscode", [record]);

        var result = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(adapter.Revalidate(plan, changeSet));

        Assert.Empty(result.Warnings);
        Assert.Equal(1, vscode.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Theory]
    [InlineData("wrong-surface")]
    [InlineData("unsafe")]
    public void Revalidate_RejectsHistoricalManifestOutsideTheTargetMatchedSafeContract(string mismatch)
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var expectedResult = mismatch switch
        {
            "wrong-surface" => SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson,
            "unsafe" => CreateUnsafeHistoricalManifest(),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var record = CreateRecord("vscode", 1) with
        {
            StatusProjection = CreateRecord("vscode", 1).StatusProjection with { ExpectedResult = expectedResult },
        };
        var (plan, changeSet) = CreatePersistedPlanFromRecords("vscode", [record]);

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

        Assert.Equal(0, vscode.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_DerivesCaptureFlagFromCoherentPersistedTargetMembers()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (context, _, _) =>
            {
                Assert.True(context.Request.IncludeContentCapture);
                return SetupPlanResult.Revalidated();
            },
        };
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (context, _, _) =>
            {
                Assert.True(context.Request.IncludeContentCapture);
                return SetupPlanResult.Revalidated();
            },
        };
        var adapter = CreateAdapter(platform, vscode, cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlanFromRecords(
            "all",
            [CreateRecord("vscode", 1, true), CreateRecord("cli", 2, true), CreateRecord("app-sdk", 3)]);

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(1, vscode.RevalidateCalls);
        Assert.Equal(1, cli.RevalidateCalls);
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("cli")]
    public void Revalidate_SelectedWritableTarget_AccessesTheSharedLazyValuesOnceAndDispatchesOnlyItsPartition(string selectedTarget)
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
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var (plan, changeSet) = CreatePersistedPlan(selectedTarget, selectedTarget);

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(selectedTarget == "vscode" ? 1 : 0, vscode.RevalidateCalls);
        Assert.Equal(selectedTarget == "cli" ? 1 : 0, cli.RevalidateCalls);
        Assert.Equal(0, appSdk.RevalidateCalls);
        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public void Revalidate_DerivesTrueCaptureFromANoOpPersistedMember()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1))
        {
            RevalidateHandler = (context, _, _) =>
            {
                Assert.True(context.Request.IncludeContentCapture);
                return SetupPlanResult.Revalidated();
            },
        };
        var adapter = CreateAdapter(platform, vscode, new ScriptedPartition("cli", CreatePlan("cli", 2)), new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var record = WithCaptureMember(CreateRecord("vscode", 1, true), SetupOperation.NoOp, "true");
        var (plan, changeSet) = CreatePersistedPlanFromRecords("vscode", [record]);

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(1, vscode.RevalidateCalls);
    }

    [Fact]
    public void Revalidate_DerivesFalseCaptureWhenThePersistedMemberIsAbsent()
    {
        var platform = CreatePlatform();
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2))
        {
            RevalidateHandler = (context, _, _) =>
            {
                Assert.False(context.Request.IncludeContentCapture);
                return SetupPlanResult.Revalidated();
            },
        };
        var adapter = CreateAdapter(platform, new ScriptedPartition("vscode", CreatePlan("vscode", 1)), cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlan("cli", "cli");

        _ = adapter.Revalidate(plan, changeSet);

        Assert.Equal(1, cli.RevalidateCalls);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("remove")]
    [InlineData("false")]
    public void Revalidate_RejectsInvalidPersistedCaptureShapesBeforeDispatchOrLazyAccess(string shape)
    {
        var platform = CreatePlatform();
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var adapter = CreateAdapter(platform, new ScriptedPartition("vscode", CreatePlan("vscode", 1)), cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var record = shape switch
        {
            "extra" => AddUnexpectedMember(CreateRecord("cli", 2)),
            "remove" => WithCaptureMember(CreateRecord("cli", 2, true), SetupOperation.Remove, null),
            "false" => WithCaptureMember(CreateRecord("cli", 2, true), SetupOperation.Replace, "false"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var (plan, changeSet) = CreatePersistedPlanFromRecords("cli", [record]);

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

        Assert.Equal(0, cli.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_RejectsIncoherentCaptureMembersBeforeDispatch()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var adapter = CreateAdapter(platform, vscode, cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var (plan, changeSet) = CreatePersistedPlanFromRecords(
            "all",
            [CreateRecord("vscode", 1, true), CreateRecord("cli", 2), CreateRecord("app-sdk", 3)]);

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

        Assert.Equal(0, vscode.RevalidateCalls + cli.RevalidateCalls);
        AssertNoDetectionOrProbe(platform);
    }

    [Fact]
    public void Revalidate_RejectsPersistedWritableMemberValuesOutsideTheFrozenVocabulary()
    {
        var platform = CreatePlatform();
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var adapter = CreateAdapter(platform, new ScriptedPartition("vscode", CreatePlan("vscode", 1)), cli, new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3)));
        var record = CreateRecord("cli", 2) with
        {
            Members = CreateRecord("cli", 2).Members
                .Select(member => member.SettingKey == "OTEL_EXPORTER_OTLP_PROTOCOL"
                    ? member with { DesiredValue = "grpc" }
                    : member)
                .ToArray(),
        };
        var (plan, changeSet) = CreatePersistedPlanFromRecords("cli", [record]);

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

        Assert.Equal(0, cli.RevalidateCalls);
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
        var (plan, changeSet) = CreatePersistedPlan("all", "vscode", "cli", "app-sdk");

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

    [Fact]
    public void Revalidate_RejectsPersistedAppSdkRestartRequirementBeforeDispatchOrLazyAccess()
    {
        var platform = CreatePlatform();
        var vscode = new ScriptedPartition("vscode", CreatePlan("vscode", 1));
        var cli = new ScriptedPartition("cli", CreatePlan("cli", 2));
        var appSdk = new ScriptedPartition("app-sdk", CreatePlan("app-sdk", 3));
        var adapter = CreateAdapter(platform, vscode, cli, appSdk);
        var record = CreateRecord("app-sdk", 3) with { RestartRequirement = SetupRestartRequirement.RestartVsCode };
        var (plan, changeSet) = CreatePersistedPlanFromRecords("app-sdk", [record]);

        Assert.Throws<InvalidOperationException>(() => adapter.Revalidate(plan, changeSet));

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
    public void PartitionContext_RepeatedLazyAccessReturnsCachedValues()
    {
        var platform = CreatePlatform();
        ScriptLiveEndpoint(platform);
        var context = new GitHubCopilotPartitionContext(platform, CreateRequest("vscode"));

        var firstObservations = context.Observations;
        var secondObservations = context.Observations;
        var firstEndpoint = context.Endpoint;
        var secondEndpoint = context.Endpoint;

        Assert.Same(firstObservations, secondObservations);
        Assert.Equal(firstEndpoint, secondEndpoint);
        Assert.Equal(3, ProcessCallCount(platform));
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public async Task PartitionContext_ConcurrentEndpointAccessInvokesTheProbeOnce()
    {
        var platform = CreatePlatform();
        ScriptLiveEndpoint(platform);
        using var barrier = platform.AddBarrier($"http.get:{Endpoint}:/health/live:500:4096");
        using var accessBarrier = new Barrier(2);
        var context = new GitHubCopilotPartitionContext(platform, CreateRequest("vscode"));

        var first = Task.Run(() =>
        {
            accessBarrier.SignalAndWait();
            return context.Endpoint;
        });
        var second = Task.Run(() =>
        {
            accessBarrier.SignalAndWait();
            return context.Endpoint;
        });
        barrier.WaitUntilReached(CancellationToken.None);
        barrier.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal([GitHubCopilotEndpointClassification.LocalMonitorLive, GitHubCopilotEndpointClassification.LocalMonitorLive], results);
        Assert.Equal(1, ProbeCallCount(platform));
    }

    [Fact]
    public async Task PartitionContext_ConcurrentObservationAccessInvokesDetectionOnce()
    {
        var platform = CreatePlatform();
        using var barrier = platform.AddBarrier("process.run:code:--version");
        using var accessBarrier = new Barrier(2);
        var context = new GitHubCopilotPartitionContext(platform, CreateRequest("vscode"));

        var first = Task.Run(() =>
        {
            accessBarrier.SignalAndWait();
            return context.Observations;
        });
        var second = Task.Run(() =>
        {
            accessBarrier.SignalAndWait();
            return context.Observations;
        });
        barrier.WaitUntilReached(CancellationToken.None);
        barrier.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal(3, ProcessCallCount(platform));
    }

    [Fact]
    public void PartitionContext_CachesEndpointProbeExceptions()
    {
        var platform = CreatePlatform();
        platform.InjectFault($"http.get:{Endpoint}:/health/live:500:4096", new InvalidOperationException("probe"));
        var context = new GitHubCopilotPartitionContext(platform, CreateRequest("vscode"));

        var first = Assert.Throws<InvalidOperationException>(() => _ = context.Endpoint);
        var second = Assert.Throws<InvalidOperationException>(() => _ = context.Endpoint);

        Assert.Same(first, second);
        Assert.Equal(1, ProbeCallCount(platform));
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
    public void NonGatingSmoke_RealRegistryDispatcherAndSetupJsonCarryAllAggregateRecordsAndThePinnedGuidanceSample()
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

        var result = dispatcher.Dispatch(new SetupOptions(SetupCommand.Plan, "github-copilot", "all", Endpoint, false, null));
        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.PlanReady, result.Code);
        Assert.Equal("github-copilot", serialized.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(3, serialized.RootElement.GetProperty("targets").GetArrayLength());
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result")));
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(
            serialized.RootElement.GetProperty("targets")[1].GetProperty("expected_result")));
        Assert.Equal(
            AppSdkGuidanceSample,
            serialized.RootElement.GetProperty("targets")[2].GetProperty("guidance").GetProperty("sample").GetString());
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

    private static SetupChangeRecord CreateRecord(string target, int recordNumber, bool includeContentCapture = false)
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
                (IReadOnlyList<SetupPrivatePlanMember>)
                [
                    new("github.copilot.chat.otel.enabled", SetupOperation.Replace, "true"),
                    new("github.copilot.chat.otel.exporterType", SetupOperation.Replace, "otlp-http"),
                    new("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, Endpoint),
                ],
                (SetupGuidance?)null,
                (IReadOnlyList<SetupMemberChangeResult>)
                [
                    new("github.copilot.chat.otel.enabled", SetupOperation.Replace, "present_different", "configured", "none", false),
                    new("github.copilot.chat.otel.exporterType", SetupOperation.Replace, "present_different", "configured", "none", false),
                    new("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false),
                ]),
            "cli" => (
                SetupTargetKind.Env,
                "copilot-cli-user-environment",
                true,
                "1.0.4",
                SetupOperation.Replace,
                (SetupEffectiveSource?)SetupEffectiveSource.Environment,
                SetupRestartRequirement.RestartTerminalSession,
                Endpoint,
                (IReadOnlyList<SetupPrivatePlanMember>)
                [
                    new("COPILOT_OTEL_ENABLED", SetupOperation.Replace, "true"),
                    new("COPILOT_OTEL_EXPORTER_TYPE", SetupOperation.Replace, "otlp-http"),
                    new("OTEL_EXPORTER_OTLP_ENDPOINT", SetupOperation.Replace, Endpoint),
                    new("OTEL_EXPORTER_OTLP_PROTOCOL", SetupOperation.Replace, "http/protobuf"),
                ],
                (SetupGuidance?)null,
                (IReadOnlyList<SetupMemberChangeResult>)
                [
                    new("COPILOT_OTEL_ENABLED", SetupOperation.Replace, "present_different", "configured", "none", false),
                    new("COPILOT_OTEL_EXPORTER_TYPE", SetupOperation.Replace, "present_different", "configured", "none", false),
                    new("OTEL_EXPORTER_OTLP_ENDPOINT", SetupOperation.Replace, "present_different", "configured_loopback", "none", false),
                    new("OTEL_EXPORTER_OTLP_PROTOCOL", SetupOperation.Replace, "present_different", "configured", "none", false),
                ]),
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
                new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample),
                (IReadOnlyList<SetupMemberChangeResult>)[]),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        var record = new SetupChangeRecord(
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

        var expectedResult = target switch
        {
            "vscode" => SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!.CanonicalJson,
            "cli" => SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson,
            _ => (JsonElement?)null,
        };
        record = record with
        {
            StatusProjection = record.StatusProjection with { ExpectedResult = expectedResult },
        };

        return includeContentCapture ? target switch
        {
            "vscode" => record with
            {
                Members = record.Members.Concat([new SetupPrivatePlanMember("github.copilot.chat.otel.captureContent", SetupOperation.Replace, "true")]).ToArray(),
                StatusProjection = record.StatusProjection with
                {
                    Changes = record.StatusProjection.Changes.Concat([new SetupMemberChangeResult("github.copilot.chat.otel.captureContent", SetupOperation.Replace, "absent", "configured", "none", false)]).ToArray(),
                },
            },
            "cli" => record with
            {
                Members = record.Members.Concat([new SetupPrivatePlanMember("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", SetupOperation.Replace, "true")]).ToArray(),
                StatusProjection = record.StatusProjection with
                {
                    Changes = record.StatusProjection.Changes.Concat([new SetupMemberChangeResult("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", SetupOperation.Replace, "absent", "configured", "none", false)]).ToArray(),
                },
            },
            _ => record,
        } : record;
    }

    private static SetupChangeRecord WithCaptureMember(
        SetupChangeRecord record,
        SetupOperation operation,
        string? desiredValue)
    {
        var captureMember = record.TargetLabel == "vscode-stable-default-user-settings"
            ? "github.copilot.chat.otel.captureContent"
            : "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";
        return record with
        {
            Members = record.Members
                .Select(member => member.SettingKey == captureMember
                    ? member with { Operation = operation, DesiredValue = desiredValue }
                    : member)
                .ToArray(),
            StatusProjection = record.StatusProjection with
            {
                Operation = operation == SetupOperation.Remove
                    ? SetupOperation.Mixed
                    : record.StatusProjection.Operation,
                Changes = record.StatusProjection.Changes
                    .Select(change => change.SettingKey == captureMember
                        ? change with { Operation = operation }
                        : change)
                    .ToArray(),
            },
        };
    }

    private static SetupChangeRecord AddUnexpectedMember(SetupChangeRecord record) => record with
    {
        Members = record.Members.Concat([new SetupPrivatePlanMember("UNEXPECTED_CAPTURE_MEMBER", SetupOperation.Replace, "true")]).ToArray(),
        StatusProjection = record.StatusProjection with
        {
            Changes = record.StatusProjection.Changes.Concat([new SetupMemberChangeResult("UNEXPECTED_CAPTURE_MEMBER", SetupOperation.Replace, "absent", "configured", "none", false)]).ToArray(),
        },
    };

    private static JsonElement CreateHistoricalManifest(GitHubCopilotSetupTarget target)
    {
        var manifest = JsonNode.Parse(SourceCapabilityManifestLoader.LoadForTarget(target)!.CanonicalJson.GetRawText())!.AsObject();
        manifest["support_status"] = "planned";
        manifest["stability"] = "preview";
        using var document = JsonDocument.Parse(manifest.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateUnsafeHistoricalManifest()
    {
        var manifest = JsonNode.Parse(CreateHistoricalManifest(GitHubCopilotSetupTarget.VsCode).GetRawText())!.AsObject();
        manifest["unexpected_field"] = "unsafe";
        using var document = JsonDocument.Parse(manifest.ToJsonString());
        return document.RootElement.Clone();
    }

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet ChangeSet) CreatePersistedPlan(string selectedTarget, params string[] targets)
    {
        var records = targets.Select((target, index) => target == "unknown"
            ? CreateRecord("vscode", index + 1) with { TargetLabel = "unknown" }
            : CreateRecord(target, index + 1)).ToArray();
        return CreatePersistedPlanFromRecords(selectedTarget, records);
    }

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet ChangeSet) CreatePersistedPlanFromRecords(
        string selectedTarget,
        IReadOnlyList<SetupChangeRecord> records)
    {
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

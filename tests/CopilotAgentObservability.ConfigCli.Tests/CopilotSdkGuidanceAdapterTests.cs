using System.Text;
using System.Text.Json;
using System.Reflection;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.AppSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class CopilotSdkGuidanceAdapterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_PresentRepositoryPackage_ReportsOnlyTheSanitizedVersionAndPinnedNoOpGuidance()
    {
        var platform = new SetupTestPlatform(Timestamp);
        SeedPackageProject(platform, "1.0.4+build.7");

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.Equal(SetupTargetKind.Guidance, record.TargetKind);
        Assert.Equal("github-copilot-app-sdk-guidance", record.TargetLabel);
        Assert.True(record.StatusProjection.Detected);
        Assert.Equal("1.0.4+build.7", record.StatusProjection.DetectedVersion);
        Assert.Equal(SetupOperation.NoOp, record.StatusProjection.Operation);
        Assert.Null(record.StatusProjection.EffectiveSource);
        Assert.Null(record.StatusProjection.Endpoint);
        Assert.Null(record.StatusProjection.ExpectedResult);
        Assert.Equal(SetupRestartRequirement.None, record.RestartRequirement);
        Assert.Empty(record.Members);
        Assert.Empty(record.StatusProjection.Changes);
        var guidance = Assert.IsType<SetupGuidance>(record.Guidance);
        Assert.Equal("caller_managed_sample", guidance.Kind);
        Assert.Equal("dotnet", guidance.Language);
        Assert.Equal(PinnedGuidanceSample(), guidance.Sample);
        Assert.Equal(new SetupStatusGuidance(guidance.Kind, guidance.Language), record.StatusProjection.Guidance);
        Assert.Contains(platform.Operations, operation => operation.EndsWith(":1048576", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.read:", StringComparison.Ordinal));
        AssertNoMutationOrExternalObservation(platform);
    }

    [Theory]
    [InlineData("12345678901234567890.2.3", "12345678901234567890.2.3")]
    [InlineData("1.2.3\n", null)]
    [InlineData("1.2.3\u0001", null)]
    public void SanitizeVersion_UsesStrictWholeInputSemanticVersionValidation(string version, string? expected)
    {
        Assert.Equal(expected, InvokeSanitizer(version));
    }

    [Fact]
    public void Plan_AbsentRepositoryPackage_ReturnsUndetectedGuidanceWithoutVersion()
    {
        var platform = new SetupTestPlatform(Timestamp);

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.False(record.StatusProjection.Detected);
        Assert.Null(record.StatusProjection.DetectedVersion);
        Assert.Equal(SetupOperation.NoOp, record.StatusProjection.Operation);
        Assert.Equal(PinnedGuidanceSample(), Assert.IsType<SetupGuidance>(record.Guidance).Sample);
        AssertNoMutationOrExternalObservation(platform);
    }

    [Fact]
    public void Plan_NonSemanticPackageVersion_RemainsDetectedWithoutExposingTheRawVersion()
    {
        const string marker = "APP_SDK_VERSION_MARKER";
        var platform = new SetupTestPlatform(Timestamp);
        SeedPackageProject(platform, $"1.0.4-{marker}!");

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.True(record.StatusProjection.Detected);
        Assert.Null(record.StatusProjection.DetectedVersion);
        var exposed = JsonSerializer.Serialize(new { record.StatusProjection, record.Guidance });
        Assert.DoesNotContain(marker, exposed, StringComparison.Ordinal);
        AssertNoMutationOrExternalObservation(platform);
    }

    [Fact]
    public void Plan_DoesNotExposePackagePathOrProjectMarker()
    {
        const string marker = "APP_SDK_PACKAGE_PATH_MARKER";
        var platform = new SetupTestPlatform(Timestamp);
        platform.SeedFile(
            RepositoryProjectPath(platform),
            Encoding.UTF8.GetBytes($"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="GitHub.Copilot.SDK" Version="1.0.4" HintPath="C:\\{marker}\\GitHub.Copilot.SDK.dll" />
                  </ItemGroup>
                </Project>
                """));

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);
        var exposed = JsonSerializer.Serialize(new
        {
            record.TargetLocation,
            record.TargetLabel,
            record.BaseStateHash,
            record.DesiredState,
            record.StatusProjection,
            record.Guidance,
        });

        Assert.DoesNotContain(marker, exposed, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, record.TargetLocation, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, Assert.IsType<SetupGuidance>(record.Guidance).Sample, StringComparison.Ordinal);
        AssertNoMutationOrExternalObservation(platform);
    }

    [Fact]
    public void Plan_UsesTheUnixRepositoryProjectPath()
    {
        var platform = new SetupTestPlatform(
            Timestamp,
            "/home/setup-test/.local/share",
            SetupPathStyle.Unix,
            SetupPlanningOs.Linux,
            "/home/setup-test/.config",
            "/home/setup-test");
        SeedPackageProject(platform, "1.0.4");

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.True(record.StatusProjection.Detected);
        Assert.Contains(
            "file.read-bounded:src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj:1048576",
            platform.Operations);
        AssertNoMutationOrExternalObservation(platform);
    }

    [Fact]
    public void Plan_OversizeRepositoryProject_ReturnsSafeUndetectedGuidance()
    {
        var platform = new SetupTestPlatform(Timestamp);
        platform.SeedFile(
            RepositoryProjectPath(platform),
            Encoding.UTF8.GetBytes("<Project>" + new string('x', 1024 * 1024)));

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.False(record.StatusProjection.Detected);
        Assert.Null(record.StatusProjection.DetectedVersion);
        Assert.Contains(platform.Operations, operation => operation.EndsWith(":1048576", StringComparison.Ordinal));
        AssertNoMutationOrExternalObservation(platform);
    }

    [Theory]
    [InlineData("<Project>")]
    [InlineData("<!DOCTYPE Project [<!ENTITY blocked SYSTEM 'file:///APP_SDK_DTD_MARKER'>]><Project>&blocked;</Project>")]
    public void Plan_MalformedOrDtdRepositoryProject_ReturnsSafeUndetectedGuidance(string project)
    {
        var platform = new SetupTestPlatform(Timestamp);
        platform.SeedFile(RepositoryProjectPath(platform), Encoding.UTF8.GetBytes(project));

        var record = Assert.Single(new AppSdkTargetPartition().Plan(CreateContext(platform)).Records);

        Assert.False(record.StatusProjection.Detected);
        Assert.Null(record.StatusProjection.DetectedVersion);
        AssertNoMutationOrExternalObservation(platform);
    }

    [Fact]
    public void Revalidate_ReturnsNoDiagnosticsWithoutReadingOrWriting()
    {
        var platform = new SetupTestPlatform(Timestamp);
        var partition = new AppSdkTargetPartition();

        var result = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(partition.Revalidate(
            CreateContext(platform),
            new SetupPrivatePlan(
                1,
                Guid.Parse("00000000-0000-7000-8000-000000000041"),
                "github-copilot",
                "app-sdk",
                Timestamp,
                "1.2.3",
                []),
            new SetupLedgerChangeSet(
                Guid.Parse("00000000-0000-7000-8000-000000000041"),
                "github-copilot",
                "app-sdk",
                Timestamp,
                Timestamp,
                "1.2.3",
                null,
                SetupChangeSetState.Planned,
                [])));

        Assert.Empty(result.Value.MaterializedTargets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void Revalidate_AppSdkOnlyPlan_DoesNotRouteToTheGuidancePartition()
    {
        var platform = new SetupTestPlatform(Timestamp);
        SeedPackageProject(platform, "1.0.4");
        var appSdk = new CountingAppSdkPartition();
        var adapter = new GitHubCopilotSetupAdapter(
            platform,
            [new UnusedPartition("vscode"), new UnusedPartition("cli"), appSdk]);
        var planResult = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(adapter.Plan(
            new SetupPlanRequest(
                "github-copilot",
                "app-sdk",
                "http://127.0.0.1:4320",
                false,
                Guid.Parse("00000000-0000-7000-8000-000000000042"),
                Timestamp,
                "1.2.3")));
        var plan = planResult.Value;
        var projectedTarget = Assert.Single(planResult.Targets);
        Assert.False(projectedTarget.ProspectiveRollbackAvailable);
        Assert.Null(projectedTarget.ExpectedResult);
        var (privatePlan, ledger) = Persist(plan);
        var operationsBeforeRevalidation = platform.Operations.ToArray();

        var result = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(adapter.Revalidate(privatePlan, ledger));

        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(0, appSdk.RevalidateCalls);
        Assert.Equal(operationsBeforeRevalidation, platform.Operations);
    }

    private static GitHubCopilotPartitionContext CreateContext(SetupTestPlatform platform) => new(
        platform,
        new SetupPlanRequest(
            "github-copilot",
            "app-sdk",
            "http://127.0.0.1:4320",
            false,
            Guid.Parse("00000000-0000-7000-8000-000000000040"),
            Timestamp,
            "1.2.3"));

    private static string PinnedGuidanceSample() =>
        SetupContractValidator.RehydrateStatusGuidance(
            new SetupStatusGuidance("caller_managed_sample", "dotnet")).Sample;

    private static string? InvokeSanitizer(string version) =>
        (string?)typeof(AppSdkTargetPartition)
            .GetMethod("SanitizeVersion", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [version]);

    private static void SeedPackageProject(SetupTestPlatform platform, string version) =>
        platform.SeedFile(
            RepositoryProjectPath(platform),
            Encoding.UTF8.GetBytes($"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="GitHub.Copilot.SDK" Version="{version}" />
                  </ItemGroup>
                </Project>
                """));

    private static string RepositoryProjectPath(SetupTestPlatform platform) =>
        platform.PathStyle == SetupPathStyle.Windows
            ? "src\\CopilotAgentObservability.LocalMonitor\\CopilotAgentObservability.LocalMonitor.csproj"
            : "src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj";

    private static void AssertNoMutationOrExternalObservation(SetupTestPlatform platform)
    {
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.move", StringComparison.Ordinal) ||
            operation.StartsWith("file.replace", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal) ||
            operation.StartsWith("environment.", StringComparison.Ordinal) ||
            operation.StartsWith("process", StringComparison.Ordinal) ||
            operation.StartsWith("http.", StringComparison.Ordinal) ||
            operation.StartsWith("managed.", StringComparison.Ordinal));
    }

    private static (SetupPrivatePlan Plan, SetupLedgerChangeSet Ledger) Persist(SetupChangePlan plan)
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
                privatePlan.Adapter,
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

    private sealed class UnusedPartition(string targetToken) : IGitHubCopilotTargetPartition
    {
        public string TargetToken => targetToken;

        public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context) =>
            throw new InvalidOperationException("This partition must not be selected.");

        public SetupPlanResult<SetupRevalidation> Revalidate(
            GitHubCopilotPartitionContext context,
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) =>
            throw new InvalidOperationException("This partition must not be revalidated.");
    }

    private sealed class CountingAppSdkPartition : IGitHubCopilotTargetPartition
    {
        private readonly AppSdkTargetPartition inner = new();

        public int RevalidateCalls { get; private set; }

        public string TargetToken => inner.TargetToken;

        public GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context) => inner.Plan(context);

        public SetupPlanResult<SetupRevalidation> Revalidate(
            GitHubCopilotPartitionContext context,
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidateCalls++;
            return inner.Revalidate(context, plan, plannedChangeSet);
        }
    }
}

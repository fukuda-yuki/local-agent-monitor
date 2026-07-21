using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.AppSdk;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.CopilotCli;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.VsCode;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

[CollectionDefinition(nameof(SetupPhysicalProcessCollection), DisableParallelization = true)]
public sealed class SetupPhysicalProcessCollection;

[Collection(nameof(SetupPhysicalProcessCollection))]
public sealed class ConfigurationSetupIntegrationTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private const string StableSettingsPath = "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json";
    private const string InsidersSettingsPath = "C:\\Users\\setup-test\\AppData\\Roaming\\Code - Insiders\\User\\settings.json";
    private const string AppSdkProjectPath = "src\\CopilotAgentObservability.LocalMonitor\\CopilotAgentObservability.LocalMonitor.csproj";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    private static readonly string[] CliMembers =
    [
        "COPILOT_OTEL_ENABLED",
        "COPILOT_OTEL_EXPORTER_TYPE",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
    ];

    [Fact]
    public void Plan_All_UsesTheProductionCompositionAndPersistsTheOrderedManifestPairedChangeSet()
    {
        var platform = CreatePlatform();
        platform.SeedFile(StableSettingsPath, "{}"u8.ToArray());
        platform.SeedFile(InsidersSettingsPath, "{}"u8.ToArray());
        platform.SeedFile(
            AppSdkProjectPath,
            "<Project><ItemGroup><PackageReference Include=\"GitHub.Copilot.SDK\" Version=\"1.0.4\" /></ItemGroup></Project>"u8.ToArray());
        ScriptAllInstalled(platform);
        ScriptLiveEndpoint(platform);
        var harness = new IntegrationHarness(platform);

        var result = harness.Plan("all");

        Assert.Equal(SetupCodes.PlanReady, result.Code);
        Assert.Equal(
            [
                "vscode-stable-default-user-settings",
                "vscode-insiders-default-user-settings",
                "copilot-cli-user-environment",
                "github-copilot-app-sdk-guidance",
            ],
            result.Targets.Select(target => target.TargetLabel));
        var vsCodeManifest = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!;
        var cliManifest = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!;
        Assert.Equal(vsCodeManifest.SourceSurface, result.Targets[0].ExpectedResult!.Value.GetProperty("source_surface").GetString());
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(vsCodeManifest, result.Targets[0].ExpectedResult!.Value));
        Assert.Equal(vsCodeManifest.SourceSurface, result.Targets[1].ExpectedResult!.Value.GetProperty("source_surface").GetString());
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(vsCodeManifest, result.Targets[1].ExpectedResult!.Value));
        Assert.Equal(cliManifest.SourceSurface, result.Targets[2].ExpectedResult!.Value.GetProperty("source_surface").GetString());
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(cliManifest, result.Targets[2].ExpectedResult!.Value));
        Assert.Null(result.Targets[3].ExpectedResult);
        Assert.True(result.Targets[3].Detected);

        var changeSetId = ParseChangeSetId(result);
        var privatePlan = Assert.IsType<SetupPrivatePlan>(harness.PlanStore.Load(changeSetId));
        var ledgerRow = Assert.Single(harness.LedgerStore.Load().ChangeSets);
        Assert.Equal(changeSetId, privatePlan.ChangeSetId);
        Assert.Equal(changeSetId, ledgerRow.ChangeSetId);
        Assert.Equal(result.Targets.Select(target => target.RecordId), privatePlan.Targets.Select(target => target.RecordId.ToString("D")));
        Assert.Equal(result.Targets.Select(target => target.RecordId), ledgerRow.Targets.Select(target => target.RecordId.ToString("D")));
        Assert.All(result.Targets.Zip(ledgerRow.Targets), pair =>
        {
            if (pair.First.ExpectedResult is null)
            {
                Assert.Null(pair.Second.StatusProjection.ExpectedResult);
                return;
            }

            Assert.NotNull(pair.Second.StatusProjection.ExpectedResult);
            Assert.True(JsonElement.DeepEquals(
                pair.First.ExpectedResult.Value,
                pair.Second.StatusProjection.ExpectedResult.Value));
        });
    }

    [Fact]
    public void Apply_WindowsCli_UsesTheRealTransactionAndEstablishesRollbackOwnership()
    {
        var platform = CreatePlatform();
        ScriptCliOperations(platform, 2);
        ScriptLiveEndpoint(platform, 2);
        var harness = new IntegrationHarness(platform);

        var plan = harness.Plan("cli");
        var changeSetId = ParseChangeSetId(plan);
        var apply = harness.Apply(changeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, apply.Code);
        var target = Assert.Single(apply.Targets);
        Assert.True(target.RollbackAvailable);
        Assert.Equal(
            ["true", "otlp-http", Endpoint, "http/protobuf"],
            CliMembers.Select(platform.ReadUserEnvironment));
        var ledgerRow = Assert.Single(harness.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.Applied, ledgerRow.State);
        Assert.Equal(SetupCodes.ApplySucceeded, ledgerRow.OutcomeCode);
        var ledgerTarget = Assert.Single(ledgerRow.Targets);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, ledgerTarget.RollbackStatus);
        Assert.NotNull(ledgerTarget.AppliedStateHash);
        Assert.NotNull(ledgerTarget.BackupReference);
        Assert.True(platform.FileSystem.FileExists(harness.Paths.GetBackup(changeSetId, ledgerTarget.RecordId)));
        Assert.True(platform.FileSystem.FileExists(harness.Paths.GetTransactionJournal(changeSetId)));
    }

    [Fact]
    public void Rollback_WindowsCli_RestoresTheRealTransactionAndClearsRollbackAvailability()
    {
        var platform = CreatePlatform();
        ScriptCliOperations(platform, 2);
        ScriptLiveEndpoint(platform, 2);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("cli"));
        Assert.Equal(SetupCodes.ApplySucceeded, harness.Apply(changeSetId).Code);

        var rollback = harness.Rollback(changeSetId);

        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);
        Assert.All(rollback.Targets, target => Assert.False(target.RollbackAvailable));
        Assert.All(CliMembers, member => Assert.Null(platform.ReadUserEnvironment(member)));
        var ledgerRow = Assert.Single(harness.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.RolledBack, ledgerRow.State);
        Assert.Equal(SetupCodes.RollbackSucceeded, ledgerRow.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, Assert.Single(ledgerRow.Targets).RollbackStatus);
    }

    [Fact]
    public async Task Apply_VsCodeEditAtTheStaleBoundary_PreservesTheConcurrentBytesAndCreatesNoMutationArtifact()
    {
        var platform = CreatePlatform();
        SeedDirectoryChain(platform, Path.GetDirectoryName(StableSettingsPath)!);
        platform.SeedFile(StableSettingsPath, "{}"u8.ToArray());
        ScriptVsCodeStableOperations(platform, planAndApply: true);
        ScriptLiveEndpoint(platform, 2);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("vscode"));
        var recordId = Assert.Single(harness.PlanStore.Load(changeSetId)!.Targets).RecordId;
        var externalBytes = "{\"external\":true}"u8.ToArray();
        var operationStart = platform.Operations.Count;
        using var barrier = platform.AddBarrier($"file.read:{StableSettingsPath}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var applyTask = Task.Run(() => harness.Apply(changeSetId));
        barrier.WaitUntilReached(cancellation.Token);
        platform.SeedFile(StableSettingsPath, externalBytes);
        barrier.Release();
        var apply = await applyTask;

        Assert.Equal(SetupCodes.StalePlan, apply.Code);
        Assert.Equal(externalBytes, platform.ReadSeededFile(StableSettingsPath));
        Assert.False(platform.FileSystem.FileExists(harness.Paths.GetBackup(changeSetId, recordId)));
        Assert.False(platform.FileSystem.FileExists(harness.Paths.GetTransactionJournal(changeSetId)));
        Assert.DoesNotContain(
            platform.Operations.Skip(operationStart),
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith("->" + StableSettingsPath, StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations.Skip(operationStart), operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedNoOp_WindowsCli_PersistsInspectablePlansWithoutTargetWritesOrOwnership()
    {
        var platform = CreatePlatform();
        SeedDesiredCliEnvironment(platform);
        ScriptCliOperations(platform, 4);
        ScriptLiveEndpoint(platform, 4);
        var harness = new IntegrationHarness(platform);
        var operationStart = platform.Operations.Count;

        var firstPlan = harness.Plan("cli");
        var firstApply = harness.Apply(ParseChangeSetId(firstPlan));
        var secondPlan = harness.Plan("cli");
        var secondApply = harness.Apply(ParseChangeSetId(secondPlan));

        Assert.Equal(SetupCodes.NoChanges, firstPlan.Code);
        Assert.Equal(SetupCodes.NoChanges, firstApply.Code);
        Assert.Equal(SetupCodes.NoChanges, secondPlan.Code);
        Assert.Equal(SetupCodes.NoChanges, secondApply.Code);
        Assert.All(firstPlan.Targets.Concat(firstApply.Targets).Concat(secondPlan.Targets).Concat(secondApply.Targets), target => Assert.False(target.RollbackAvailable));
        Assert.DoesNotContain(platform.Operations.Skip(operationStart), operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", platform.Operations.Skip(operationStart));
        Assert.All(harness.LedgerStore.Load().ChangeSets, row =>
        {
            Assert.Equal(SetupChangeSetState.NoChanges, row.State);
            Assert.Null(Assert.Single(row.Targets).BackupReference);
        });
    }

    [Fact]
    public void Apply_RemovedAdapter_ReturnsTheExceptionalPairWithoutChangingDurableBytesOrStartingTargetActivity()
    {
        var platform = CreatePlatform();
        ScriptCliOperations(platform, 1);
        ScriptLiveEndpoint(platform);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("cli"));
        var planPath = harness.Paths.GetPlan(changeSetId);
        var planBytes = platform.ReadSeededFile(planPath);
        var ledgerBytes = platform.ReadSeededFile(harness.Paths.OwnershipLedger);
        var emptyDispatcher = new SetupCommandDispatcher(
            platform,
            harness.Paths,
            harness.PlanStore,
            harness.LedgerStore,
            harness.JournalStore,
            new SetupAdapterRegistry([]),
            "1.0.0");
        var operationStart = platform.Operations.Count;

        var result = DispatchSerialized(
            emptyDispatcher.Dispatch,
            new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId));

        Assert.Equal(SetupCodes.UnsupportedAdapter, result.Code);
        Assert.Equal("github-copilot", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal(planBytes, platform.ReadSeededFile(planPath));
        Assert.Equal(ledgerBytes, platform.ReadSeededFile(harness.Paths.OwnershipLedger));
        AssertNoTargetActivity(platform.Operations.Skip(operationStart));
        Assert.False(platform.FileSystem.FileExists(harness.Paths.GetTransactionJournal(changeSetId)));
        Assert.False(platform.FileSystem.FileExists(harness.Paths.GetBackup(changeSetId, Assert.Single(harness.LedgerStore.Load().ChangeSets).Targets[0].RecordId)));
    }

    [Fact]
    public void Apply_MacOsCliPlan_ReturnsUnsupportedTargetBeforeAnyShellProfileOrMutationActivity()
    {
        var platform = CreatePlatform(SetupPlanningOs.MacOs);
        ScriptCliOperations(platform, 1);
        ScriptLiveEndpoint(platform);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("cli"));
        var planPath = harness.Paths.GetPlan(changeSetId);
        var planBytes = platform.ReadSeededFile(planPath);
        var ledgerBytes = platform.ReadSeededFile(harness.Paths.OwnershipLedger);
        var recordId = Assert.Single(harness.PlanStore.Load(changeSetId)!.Targets).RecordId;
        var backupPath = harness.Paths.GetBackup(changeSetId, recordId);
        var operationStart = platform.Operations.Count;

        var result = harness.Apply(changeSetId);

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Empty(result.Targets);
        Assert.Equal(planBytes, platform.ReadSeededFile(planPath));
        Assert.Equal(ledgerBytes, platform.ReadSeededFile(harness.Paths.OwnershipLedger));
        var operations = platform.Operations.Skip(operationStart).ToArray();
        AssertNoTargetActivity(operations);
        AssertNoPrivateMutationActivity(
            operations,
            harness.Paths.Root,
            planPath,
            harness.Paths.OwnershipLedger);
        Assert.DoesNotContain(operations, operation =>
            operation.Contains(".zshrc", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains(".bashrc", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains(".profile", StringComparison.OrdinalIgnoreCase));
        Assert.False(platform.FileSystem.FileExists(harness.Paths.GetTransactionJournal(changeSetId)));
        Assert.False(platform.FileSystem.FileExists(backupPath));
    }

    [Fact]
    public void Plan_AfterInterruptedApply_ReportsRecoveryCorrelationAndTheExactRerunAction()
    {
        var platform = CreatePlatform();
        ScriptCliOperations(platform, 2);
        ScriptLiveEndpoint(platform, 2);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("cli"));
        platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}",
            new SetupApplyProducerCrashException());
        Assert.Equal(SetupCodes.InternalError, harness.Apply(changeSetId).Code);
        var operationStart = platform.Operations.Count;

        var recovery = harness.Plan("app-sdk");

        Assert.True(recovery.Success);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, recovery.Code);
        Assert.Null(recovery.ChangeSetId);
        Assert.Equal(changeSetId.ToString("D"), recovery.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, recovery.RecoveryOperation);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], recovery.NextActions);
        Assert.DoesNotContain(platform.Operations.Skip(operationStart), operation => operation.StartsWith("process.run:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations.Skip(operationStart), operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_UsesImmutableLedgerProjectionAndFreshCurrentStateWithoutAggregateDetectionOrProbe()
    {
        var platform = CreatePlatform();
        ScriptCliOperations(platform, 2);
        ScriptLiveEndpoint(platform, 2);
        var harness = new IntegrationHarness(platform);
        var changeSetId = ParseChangeSetId(harness.Plan("cli"));
        Assert.Equal(SetupCodes.ApplySucceeded, harness.Apply(changeSetId).Code);
        var operationStart = platform.Operations.Count;

        var currentStatus = harness.Status();

        var currentRow = Assert.Single(currentStatus.ChangeSets);
        var immutableTarget = Assert.Single(currentRow.Targets);
        Assert.Equal(SetupCodes.StatusReady, currentStatus.Code);
        Assert.Equal(SetupChangeSetState.Applied, currentRow.State);
        Assert.Equal(SetupCurrentState.Current, currentRow.CurrentState);
        Assert.True(currentRow.RollbackAvailable);
        Assert.Equal("copilot-cli-user-environment", immutableTarget.TargetLabel);
        Assert.True(immutableTarget.Detected);
        Assert.Equal("1.0.4", immutableTarget.DetectedVersion);
        Assert.Equal(SetupEffectiveSource.Environment, immutableTarget.EffectiveSource);
        Assert.True(SourceCapabilityManifestLoader.MatchesCanonical(immutableTarget.ExpectedResult!.Value));

        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4999");
        var staleStatus = harness.Status();
        var staleRow = Assert.Single(staleStatus.ChangeSets);
        var staleTarget = Assert.Single(staleRow.Targets);

        Assert.Equal(SetupCurrentState.Stale, staleRow.CurrentState);
        Assert.False(staleRow.RollbackAvailable);
        Assert.Equal(SetupReferenceState.Desired, staleTarget.ReferenceState);
        Assert.Equal(SetupCurrentState.Stale, staleTarget.CurrentState);
        Assert.Equal(immutableTarget.Detected, staleTarget.Detected);
        Assert.Equal(immutableTarget.DetectedVersion, staleTarget.DetectedVersion);
        Assert.Equal(immutableTarget.ExpectedResult!.Value.GetRawText(), staleTarget.ExpectedResult!.Value.GetRawText());
        var statusOperations = platform.Operations.Skip(operationStart).ToArray();
        Assert.DoesNotContain(statusOperations, operation => operation.StartsWith("process.run:", StringComparison.Ordinal));
        Assert.DoesNotContain(statusOperations, operation => operation.StartsWith("managed.read:", StringComparison.Ordinal));
        Assert.DoesNotContain(statusOperations, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
    }

    [Fact]
    public void AppSdkAndLinuxCli_NeverBecomeMutationAuthorities()
    {
        var appPlatform = CreatePlatform();
        appPlatform.SeedFile(
            AppSdkProjectPath,
            "<Project><ItemGroup><PackageReference Include=\"GitHub.Copilot.SDK\" Version=\"1.0.4\" /></ItemGroup></Project>"u8.ToArray());
        var appHarness = new IntegrationHarness(appPlatform);
        var appOperationStart = appPlatform.Operations.Count;
        var appPlan = appHarness.Plan("app-sdk");
        var appApply = appHarness.Apply(ParseChangeSetId(appPlan));

        Assert.Equal(SetupCodes.NoChanges, appPlan.Code);
        Assert.Equal(SetupCodes.NoChanges, appApply.Code);
        Assert.Equal(SetupTargetKind.Guidance, Assert.Single(appApply.Targets).TargetKind);
        AssertNoWritesOutsideRuntimeRoot(appPlatform.Operations.Skip(appOperationStart), appHarness.Paths.Root);

        var linuxPlatform = CreatePlatform(SetupPlanningOs.Linux);
        ScriptCliOperations(linuxPlatform, 1);
        ScriptLiveEndpoint(linuxPlatform);
        var linuxHarness = new IntegrationHarness(linuxPlatform);
        var linuxChangeSetId = ParseChangeSetId(linuxHarness.Plan("cli"));
        var linuxPlanPath = linuxHarness.Paths.GetPlan(linuxChangeSetId);
        var linuxPlanBytes = linuxPlatform.ReadSeededFile(linuxPlanPath);
        var linuxLedgerBytes = linuxPlatform.ReadSeededFile(linuxHarness.Paths.OwnershipLedger);
        var linuxRecordId = Assert.Single(linuxHarness.PlanStore.Load(linuxChangeSetId)!.Targets).RecordId;
        var linuxOperationStart = linuxPlatform.Operations.Count;
        var linuxApply = linuxHarness.Apply(linuxChangeSetId);

        Assert.Equal(SetupCodes.UnsupportedTarget, linuxApply.Code);
        Assert.Equal(linuxPlanBytes, linuxPlatform.ReadSeededFile(linuxPlanPath));
        Assert.Equal(linuxLedgerBytes, linuxPlatform.ReadSeededFile(linuxHarness.Paths.OwnershipLedger));
        var linuxOperations = linuxPlatform.Operations.Skip(linuxOperationStart).ToArray();
        AssertNoTargetActivity(linuxOperations);
        AssertNoPrivateMutationActivity(
            linuxOperations,
            linuxHarness.Paths.Root,
            linuxPlanPath,
            linuxHarness.Paths.OwnershipLedger);
        Assert.DoesNotContain(linuxOperations, operation =>
            operation.Contains(".bashrc", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains(".profile", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("/etc/", StringComparison.OrdinalIgnoreCase));
        Assert.False(linuxPlatform.FileSystem.FileExists(linuxHarness.Paths.GetTransactionJournal(linuxChangeSetId)));
        Assert.False(linuxPlatform.FileSystem.FileExists(linuxHarness.Paths.GetBackup(linuxChangeSetId, linuxRecordId)));
    }

    [Fact]
    public async Task PreDispatchStatusFailure_DirectCliAndPowerShellWrapperReturnByteIdenticalSetupV1()
    {
        string[] actionArguments = ["status", "--adapter"];
        var parseResult = SetupOptions.Parse(["setup", .. actionArguments]);
        Assert.Null(parseResult.Options);
        Assert.Equal(SetupCodes.InvalidArguments, parseResult.Code);

        var direct = await RunConfigCliAsync(actionArguments);
        var wrapper = await RunWrapperAsync(actionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal(2, wrapper.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        using var directJson = JsonDocument.Parse(direct.StandardOutput);
        Assert.Equal(SetupCodes.ContractVersion, directJson.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal("status", directJson.RootElement.GetProperty("command").GetString());
        Assert.Equal(SetupCodes.InvalidArguments, directJson.RootElement.GetProperty("code").GetString());
        Assert.False(directJson.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void PhysicalProcessTests_ShareOneDisabledParallelCollection()
    {
        var integrationCollection = Assert.Single(
            typeof(ConfigurationSetupIntegrationTests).CustomAttributes,
            attribute => attribute.AttributeType == typeof(CollectionAttribute));
        var wrapperCollection = Assert.Single(
            typeof(SetupWrapperTests).CustomAttributes,
            attribute => attribute.AttributeType == typeof(CollectionAttribute));
        var integrationName = Assert.IsType<string>(Assert.Single(integrationCollection.ConstructorArguments).Value);
        var wrapperName = Assert.IsType<string>(Assert.Single(wrapperCollection.ConstructorArguments).Value);

        Assert.Equal(integrationName, wrapperName);
        var definitionType = Assert.Single(
            typeof(ConfigurationSetupIntegrationTests).Assembly.GetTypes(),
            type => type.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                Assert.IsType<string>(Assert.Single(attribute.ConstructorArguments).Value) == integrationName));
        var definition = Assert.IsType<CollectionDefinitionAttribute>(
            definitionType.GetCustomAttribute(typeof(CollectionDefinitionAttribute)));
        Assert.True(definition.DisableParallelization);
    }

    [Fact]
    public async Task PhysicalProcessRunner_TimeoutReportsCompletedOwnedCleanupAndRepositorySafeEvidence()
    {
        string runtimePath;
        SetupPhysicalProcessCleanupResult? cleanup = null;
        var ownedProcessExitedBeforeDisposal = false;
        using (var evidenceDirectory = new TemporaryProcessEvidenceDirectory())
        {
            runtimePath = evidenceDirectory.Path;
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                SetupPhysicalProcessRunner.RunWithDeadlineAsync(
                    "pwsh",
                    RepositoryRoot,
                    ["-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null"],
                    TimeSpan.FromMilliseconds(100),
                    (process, result) =>
                    {
                        cleanup = result;
                        ownedProcessExitedBeforeDisposal = process.HasExited;
                    }));

            Assert.Equal(SetupPhysicalProcessRunner.TimeoutEvidence, exception.Message);
            Assert.DoesNotContain(evidenceDirectory.Path, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RepositoryRoot, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new SetupPhysicalProcessCleanupResult(true, true), cleanup);
            Assert.True(ownedProcessExitedBeforeDisposal);
        }

        Assert.False(Directory.Exists(runtimePath));
    }

    [Fact]
    public async Task PhysicalProcessRunner_CleanupDeadlineKeepsTimeoutSafeAndReportsIncompleteDrain()
    {
        string runtimePath;
        SetupPhysicalProcessCleanupResult? cleanup = null;
        var ownedProcessExitedBeforeDisposal = false;
        var incompleteDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var evidenceDirectory = new TemporaryProcessEvidenceDirectory())
        {
            runtimePath = evidenceDirectory.Path;
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                SetupPhysicalProcessRunner.RunWithDeadlineAsync(
                    "pwsh",
                    RepositoryRoot,
                    ["-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null"],
                    TimeSpan.FromMilliseconds(100),
                    (process, result) =>
                    {
                        cleanup = result;
                        ownedProcessExitedBeforeDisposal = process.HasExited;
                    },
                    cleanupDeadline: TimeSpan.FromSeconds(1),
                    drainEvidenceSelector: _ => incompleteDrain.Task));

            Assert.Equal(SetupPhysicalProcessRunner.TimeoutEvidence, exception.Message);
            Assert.DoesNotContain(evidenceDirectory.Path, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RepositoryRoot, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new SetupPhysicalProcessCleanupResult(true, false), cleanup);
            Assert.True(ownedProcessExitedBeforeDisposal);
            Assert.False(incompleteDrain.Task.IsCompleted);
        }

        Assert.False(Directory.Exists(runtimePath));
    }

    [Fact]
    public async Task PhysicalProcessRunner_CleanupEvidenceExceptionCannotReplaceFixedTimeout()
    {
        const string privateCleanupDetail = "C:\\private\\cleanup.log";

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            SetupPhysicalProcessRunner.RunWithDeadlineAsync(
                "pwsh",
                RepositoryRoot,
                ["-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null"],
                TimeSpan.FromMilliseconds(100),
                (_, _) => throw new InvalidOperationException(privateCleanupDetail)));

        Assert.Equal(SetupPhysicalProcessRunner.TimeoutEvidence, exception.Message);
        Assert.DoesNotContain(privateCleanupDetail, exception.Message, StringComparison.Ordinal);
    }

    private static Guid ParseChangeSetId(SetupCommandResult result) =>
        Guid.Parse(Assert.IsType<string>(result.ChangeSetId));

    private static SetupCommandResult DispatchSerialized(
        Func<SetupOptions, SetupCommandResult> dispatch,
        SetupOptions options)
    {
        var result = dispatch(options);
        using var json = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(SetupCodes.ContractVersion, json.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(result.Code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(result.Success, json.RootElement.GetProperty("success").GetBoolean());
        return result;
    }

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs = SetupPlanningOs.Windows) => planningOs switch
    {
        SetupPlanningOs.Windows => new SetupTestPlatform(Timestamp),
        SetupPlanningOs.MacOs => new SetupTestPlatform(
            Timestamp,
            "/Users/setup-test/Library/Application Support",
            SetupPathStyle.Unix,
            planningOs,
            "/Users/setup-test/Library/Application Support",
            "/Users/setup-test"),
        SetupPlanningOs.Linux => new SetupTestPlatform(
            Timestamp,
            "/home/setup-test/.local/share",
            SetupPathStyle.Unix,
            planningOs,
            "/home/setup-test/.config",
            "/home/setup-test"),
        _ => throw new ArgumentOutOfRangeException(nameof(planningOs)),
    };

    private static void ScriptAllInstalled(SetupTestPlatform platform)
    {
        ScriptVersion(platform, "code", ["--version"], "1.128.0");
        ScriptVersion(platform, "code-insiders", ["--version"], "1.128.0");
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptExtension(platform, "code");
        ScriptExtension(platform, "code-insiders");
    }

    private static void ScriptCliOperations(SetupTestPlatform platform, int count)
    {
        for (var index = 0; index < count; index++)
        {
            ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        }
    }

    private static void ScriptVsCodeStableOperations(SetupTestPlatform platform, bool planAndApply)
    {
        var count = planAndApply ? 2 : 1;
        for (var index = 0; index < count; index++)
        {
            ScriptVersion(platform, "code", ["--version"], "1.128.0");
            ScriptExtension(platform, "code");
        }
    }

    private static void ScriptVersion(
        SetupTestPlatform platform,
        string executable,
        IReadOnlyList<string> arguments,
        string version) =>
        platform.ScriptProcess(
            executable,
            arguments,
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, version + Environment.NewLine));

    private static void ScriptExtension(SetupTestPlatform platform, string executable) =>
        platform.ScriptProcess(
            executable,
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "GitHub.copilot-chat@0.26.0\n"));

    private static void ScriptLiveEndpoint(SetupTestPlatform platform, int count = 1)
    {
        for (var index = 0; index < count; index++)
        {
            platform.ScriptHttpProbe(new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                17,
                "{\"status\":\"live\"}"u8.ToArray(),
                true));
        }
    }

    private static void SeedDesiredCliEnvironment(SetupTestPlatform platform)
    {
        platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", "true");
        platform.SeedUserEnvironment("COPILOT_OTEL_EXPORTER_TYPE", "otlp-http");
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", Endpoint);
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
    }

    private static void SeedDirectoryChain(SetupTestPlatform platform, string directory)
    {
        var current = Path.GetPathRoot(directory)!;
        platform.SeedDirectory(current);
        foreach (var segment in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            platform.SeedDirectory(current);
        }
    }

    private static void AssertNoTargetActivity(IEnumerable<string> operations)
    {
        var snapshot = operations.ToArray();
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("process.run:", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("managed.read:", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("http.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("process-environment.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("environment.get:", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", snapshot);
    }

    private static void AssertNoWritesOutsideRuntimeRoot(IEnumerable<string> operations, string runtimeRoot)
    {
        var snapshot = operations.ToArray();
        var mutations = snapshot.Where(IsFileMutationActivity).ToArray();
        var directoryMutations = snapshot.Where(operation =>
            operation.StartsWith("directory.create:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(mutations);
        Assert.NotEmpty(directoryMutations);
        Assert.All(mutations, operation =>
        {
            var operands = operation[(operation.IndexOf(':') + 1)..]
                .Split("->", StringSplitOptions.None);
            Assert.All(operands, operand => Assert.StartsWith(runtimeRoot, operand, StringComparison.OrdinalIgnoreCase));
        });
        Assert.All(directoryMutations, operation =>
            Assert.StartsWith(
                runtimeRoot,
                operation["directory.create:".Length..],
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", snapshot);
    }

    private static void AssertNoPrivateMutationActivity(
        IReadOnlyCollection<string> operations,
        string runtimeRoot,
        string planPath,
        string ledgerPath)
    {
        Assert.Contains($"directory.create:{runtimeRoot}", operations);
        Assert.Contains($"file.read:{planPath}", operations);
        Assert.Contains(operations, operation =>
            operation.StartsWith($"file.read-bounded:{ledgerPath}:", StringComparison.Ordinal));
        Assert.DoesNotContain(operations, IsFileMutationActivity);
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("directory.create:", StringComparison.Ordinal) &&
            !string.Equals(operation, $"directory.create:{runtimeRoot}", StringComparison.Ordinal));
        Assert.DoesNotContain(operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", operations);
    }

    private static bool IsFileMutationActivity(string operation) =>
        operation.StartsWith("file.write:", StringComparison.Ordinal) ||
        operation.StartsWith("file.write-new:", StringComparison.Ordinal) ||
        operation.StartsWith("file.try-write-new-flushed:", StringComparison.Ordinal) ||
        operation.StartsWith("file.flush:", StringComparison.Ordinal) ||
        operation.StartsWith("file.replace:", StringComparison.Ordinal) ||
        operation.StartsWith("file.move:", StringComparison.Ordinal) ||
        operation.StartsWith("file.delete:", StringComparison.Ordinal);

    private static Task<SetupPhysicalProcessResult> RunConfigCliAsync(params string[] actionArguments)
    {
        var arguments = new List<string>
        {
            "run",
            "--verbosity",
            "quiet",
            "--project",
            ConfigCliProjectPath,
            "--",
            "setup",
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("dotnet", RepositoryRoot, arguments);
    }

    private static Task<SetupPhysicalProcessResult> RunWrapperAsync(params string[] actionArguments)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-File",
            SetupScriptPath,
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("pwsh", RepositoryRoot, arguments);
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string ConfigCliProjectPath => Path.Combine(
        RepositoryRoot,
        "src",
        "CopilotAgentObservability.ConfigCli",
        "CopilotAgentObservability.ConfigCli.csproj");

    private static string SetupScriptPath => Path.Combine(
        RepositoryRoot,
        "scripts",
        "local-monitor",
        "setup.ps1");

    private sealed class TemporaryProcessEvidenceDirectory : IDisposable
    {
        public TemporaryProcessEvidenceDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-setup-process-evidence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class IntegrationHarness
    {
        public IntegrationHarness(SetupTestPlatform platform)
        {
            Platform = platform;
            Paths = new SetupRuntimePaths(platform);
            PlanStore = new SetupPlanStore(platform, Paths);
            LedgerStore = new SetupLedgerStore(platform, Paths, PlanStore);
            JournalStore = new SetupTransactionJournalStore(platform, Paths);
            Dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        }

        public SetupTestPlatform Platform { get; }

        public SetupRuntimePaths Paths { get; }

        public SetupPlanStore PlanStore { get; }

        public SetupLedgerStore LedgerStore { get; }

        public SetupTransactionJournalStore JournalStore { get; }

        public Func<SetupOptions, SetupCommandResult> Dispatch { get; }

        public SetupCommandResult Plan(string target) => DispatchSerialized(
            Dispatch,
            new SetupOptions(SetupCommand.Plan, "github-copilot", target, Endpoint, false, null));

        public SetupCommandResult Apply(Guid changeSetId) => DispatchSerialized(
            Dispatch,
            new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId));

        public SetupCommandResult Rollback(Guid changeSetId) => DispatchSerialized(
            Dispatch,
            new SetupOptions(SetupCommand.Rollback, null, null, null, false, changeSetId));

        public SetupCommandResult Status() => DispatchSerialized(
            Dispatch,
            new SetupOptions(SetupCommand.Status, null, null, null, false, null));
    }
}

internal sealed record SetupPhysicalProcessResult(
    int ExitCode,
    byte[] StandardOutput,
    byte[] StandardError);

internal readonly record struct SetupPhysicalProcessCleanupResult(
    bool Exited,
    bool DrainsCompleted);

internal static class SetupPhysicalProcessRunner
{
    internal const string TimeoutEvidence = "Setup physical process exceeded its fixed execution deadline.";
    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(5);

    public static Task<SetupPhysicalProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments) =>
        RunWithDeadlineAsync(
            fileName,
            workingDirectory,
            arguments,
            ExecutionDeadline,
            cleanupObserved: null);

    internal static async Task<SetupPhysicalProcessResult> RunWithDeadlineAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan executionDeadline,
        Action<Process, SetupPhysicalProcessCleanupResult>? cleanupObserved,
        TimeSpan? cleanupDeadline = null,
        Func<Task, Task>? drainEvidenceSelector = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start setup physical process.");
        var outputTask = ReadBytesAsync(process.StandardOutput.BaseStream);
        var errorTask = ReadBytesAsync(process.StandardError.BaseStream);
        using var deadline = new CancellationTokenSource(executionDeadline);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await CleanupAndObserveSafelyAsync(
                process,
                outputTask,
                errorTask,
                cleanupDeadline ?? CleanupDeadline,
                drainEvidenceSelector,
                cleanupObserved);
            throw new TimeoutException(TimeoutEvidence);
        }
        catch
        {
            await CleanupAndObserveSafelyAsync(
                process,
                outputTask,
                errorTask,
                cleanupDeadline ?? CleanupDeadline,
                drainEvidenceSelector,
                cleanupObserved);
            throw;
        }

        return new SetupPhysicalProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static async Task CleanupAndObserveSafelyAsync(
        Process process,
        Task<byte[]> outputTask,
        Task<byte[]> errorTask,
        TimeSpan cleanupDeadline,
        Func<Task, Task>? drainEvidenceSelector,
        Action<Process, SetupPhysicalProcessCleanupResult>? cleanupObserved)
    {
        var result = default(SetupPhysicalProcessCleanupResult);
        try
        {
            result = await TerminateOwnedProcessTreeAsync(
                process,
                outputTask,
                errorTask,
                cleanupDeadline,
                drainEvidenceSelector);
        }
        catch
        {
        }

        try
        {
            cleanupObserved?.Invoke(process, result);
        }
        catch
        {
        }
    }

    private static async Task<SetupPhysicalProcessCleanupResult> TerminateOwnedProcessTreeAsync(
        Process process,
        Task<byte[]> outputTask,
        Task<byte[]> errorTask,
        TimeSpan cleanupDeadline,
        Func<Task, Task>? drainEvidenceSelector)
    {
        using var deadline = new CancellationTokenSource(cleanupDeadline);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            return new SetupPhysicalProcessCleanupResult(false, false);
        }

        Task drains = Task.WhenAll(outputTask, errorTask);
        try
        {
            var drainEvidence = drainEvidenceSelector?.Invoke(drains) ?? drains;
            await drainEvidence.WaitAsync(deadline.Token);
        }
        catch
        {
            return new SetupPhysicalProcessCleanupResult(true, false);
        }

        return new SetupPhysicalProcessCleanupResult(
            true,
            drains.IsCompletedSuccessfully);
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }
}

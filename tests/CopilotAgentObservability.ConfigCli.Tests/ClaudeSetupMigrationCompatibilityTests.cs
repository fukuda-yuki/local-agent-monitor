using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeSetupMigrationCompatibilityTests
{
    private static readonly Guid HistoricalChangeSetId =
        Guid.Parse("00000000-0000-7000-8000-000000000101");
    private static readonly Guid HistoricalRecordId =
        Guid.Parse("00000000-0000-7000-8000-000000000201");
    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void ActualV1Fixtures_ReopenProjectsUnavailableAndFreshCommandCompositionFailsClosedWithoutRewritingBytes()
    {
        var fixture = ActualV1Fixture.Create();
        var platform = fixture.Platform;
        var reopenedPlanStore = new SetupPlanStore(platform, fixture.Paths);
        var historicalRow = Assert.Single(new SetupLedgerStore(
            platform,
            fixture.Paths,
            reopenedPlanStore).Load().ChangeSets);
        var projection = new SetupStatusProjector(
            platform,
            fixture.Paths,
            reopenedPlanStore,
            new SetupTransactionJournalStore(platform, fixture.Paths)).Project(historicalRow);

        Assert.Equal(HistoricalChangeSetId.ToString("D"), projection.ChangeSetId);
        Assert.Equal(SetupChangeSetState.Applied, projection.State);
        Assert.Equal(SetupCurrentState.Unavailable, projection.CurrentState);
        Assert.False(projection.RollbackAvailable);

        var operationsBeforeStatus = platform.Operations.Count;
        var status = Dispatch(platform, new SetupOptions(
            SetupCommand.Status, "github-copilot", null, null, false, null));

        Assert.False(status.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, status.Code);
        Assert.Empty(status.ChangeSets);
        AssertNoHistoricalMutationActivity(platform, operationsBeforeStatus);

        var operationsBeforePlan = platform.Operations.Count;
        var plan = Dispatch(platform, new SetupOptions(
            SetupCommand.Plan,
            "claude-code",
            "cli",
            "http://127.0.0.1:4320",
            false,
            null));

        Assert.False(plan.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, plan.Code);
        Assert.Empty(plan.Targets);
        AssertNoHistoricalMutationActivity(platform, operationsBeforePlan);
        AssertNoClaudePlanActivity(platform, operationsBeforePlan);
        fixture.AssertHistoricalBytesUnchanged();
        Assert.Equivalent(
            historicalRow,
            Assert.Single(new SetupLedgerStore(
                platform,
                fixture.Paths,
                new SetupPlanStore(platform, fixture.Paths)).Load().ChangeSets),
            strict: true);

        var operationsBeforeApply = platform.Operations.Count;
        var apply = Dispatch(platform, new SetupOptions(
            SetupCommand.Apply, null, null, null, false, HistoricalChangeSetId));

        Assert.False(apply.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, apply.Code);
        AssertNoHistoricalMutationActivity(platform, operationsBeforeApply);

        var operationsBeforeRollback = platform.Operations.Count;
        var rollback = Dispatch(platform, new SetupOptions(
            SetupCommand.Rollback, null, null, null, false, HistoricalChangeSetId));

        Assert.False(rollback.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, rollback.Code);
        AssertNoHistoricalMutationActivity(platform, operationsBeforeRollback);
        fixture.AssertHistoricalBytesUnchanged();
        Assert.False(platform.FileSystem.FileExists(
            fixture.Paths.GetTransactionJournal(HistoricalChangeSetId)));
        Assert.False(platform.FileSystem.FileExists(
            fixture.Paths.GetBackup(HistoricalChangeSetId, HistoricalRecordId)));
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("ownership-ledger.v0", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("ownership-ledger.v2", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("private-plan.v0", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("private-plan.v2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CurrentV1ClaudePlan_AppendsThroughProductionStorageAndSurvivesLifecycleInSeparateFreshRoot()
    {
        var fixture = ActualV1Fixture.Create();
        var historicalPlatform = fixture.Platform;
        var initialStore = OpenLedgerStore(historicalPlatform, fixture.Paths);
        var historicalBefore = Assert.Single(initialStore.Load().ChangeSets);
        var historicalPlanBytes = historicalPlatform.ReadSeededFile(
            fixture.Paths.GetPlan(HistoricalChangeSetId));
        const string ClaudeSettingsPath = "C:\\Users\\setup-test\\.claude\\settings.json";
        var currentPlatform = new SetupTestPlatform(Timestamp);
        var currentPaths = new SetupRuntimePaths(currentPlatform);
        SeedDirectoryChain(currentPlatform, Path.GetDirectoryName(ClaudeSettingsPath)!);
        currentPlatform.SeedFile(ClaudeSettingsPath, "{}\n"u8.ToArray());
        ScriptReadyClaude(currentPlatform);
        ScriptReadyClaude(currentPlatform);

        var plan = DispatchClaude(currentPlatform, new SetupOptions(
            SetupCommand.Plan,
            "claude-code",
            "cli",
            "http://127.0.0.1:4320",
            false,
            null));

        Assert.True(plan.Success, plan.Code);
        Assert.Equal(SetupCodes.PlanReady, plan.Code);
        var claudeChangeSetId = Guid.Parse(plan.ChangeSetId!);
        var generatedPlanStore = new SetupPlanStore(currentPlatform, currentPaths);
        var generatedPlan = Assert.IsType<SetupPrivatePlan>(
            generatedPlanStore.Load(claudeChangeSetId));
        var generatedRow = OpenLedgerStore(currentPlatform, currentPaths).Load().ChangeSets.Single(
            changeSet => changeSet.ChangeSetId == claudeChangeSetId);
        var historicalPlanStore = new SetupPlanStore(historicalPlatform, fixture.Paths);
        var historicalLedgerStore = new SetupLedgerStore(
            historicalPlatform,
            fixture.Paths,
            historicalPlanStore);
        using (var acquisition = SetupLock.TryAcquire(historicalPlatform, fixture.Paths))
        {
            Assert.True(acquisition.Acquired);
            historicalLedgerStore.PersistPlannedChangeSet(
                acquisition.Lock!,
                generatedPlan,
                generatedRow);
        }

        var reopenedAfterPlan = OpenLedgerStore(historicalPlatform, fixture.Paths).Load();
        Assert.Equal(2, reopenedAfterPlan.ChangeSets.Count);
        Assert.Equivalent(
            historicalBefore,
            reopenedAfterPlan.ChangeSets.Single(changeSet =>
                changeSet.ChangeSetId == HistoricalChangeSetId),
            strict: true);
        Assert.Equal(
            historicalPlanBytes,
            historicalPlatform.ReadSeededFile(fixture.Paths.GetPlan(HistoricalChangeSetId)));
        Assert.Equivalent(
            generatedPlan,
            new SetupPlanStore(historicalPlatform, fixture.Paths).Load(claudeChangeSetId),
            strict: true);

        var apply = DispatchClaude(currentPlatform, new SetupOptions(
            SetupCommand.Apply, null, null, null, false, claudeChangeSetId));

        Assert.True(apply.Success, apply.Code);
        Assert.Equal(SetupCodes.ApplySucceeded, apply.Code);
        Assert.False("{}\n"u8.ToArray().SequenceEqual(
            currentPlatform.ReadSeededFile(ClaudeSettingsPath)));

        var statusAfterApply = DispatchClaude(currentPlatform, new SetupOptions(
            SetupCommand.Status, "claude-code", null, null, false, null));

        Assert.True(statusAfterApply.Success, statusAfterApply.Code);
        var applied = Assert.Single(statusAfterApply.ChangeSets);
        Assert.Equal(SetupChangeSetState.Applied, applied.State);
        Assert.True(applied.RollbackAvailable);

        var rollback = DispatchClaude(currentPlatform, new SetupOptions(
            SetupCommand.Rollback, null, null, null, false, claudeChangeSetId));

        Assert.True(rollback.Success, rollback.Code);
        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);
        Assert.Equal("{}\n"u8.ToArray(), currentPlatform.ReadSeededFile(ClaudeSettingsPath));

        var statusAfterRollback = DispatchClaude(currentPlatform, new SetupOptions(
            SetupCommand.Status, "claude-code", null, null, false, null));

        Assert.True(statusAfterRollback.Success, statusAfterRollback.Code);
        Assert.Equal(
            SetupChangeSetState.RolledBack,
            Assert.Single(statusAfterRollback.ChangeSets).State);
        var finalLedger = OpenLedgerStore(historicalPlatform, fixture.Paths).Load();
        Assert.Equivalent(
            historicalBefore,
            finalLedger.ChangeSets.Single(changeSet =>
                changeSet.ChangeSetId == HistoricalChangeSetId),
            strict: true);
        Assert.Equal(
            historicalPlanBytes,
            historicalPlatform.ReadSeededFile(fixture.Paths.GetPlan(HistoricalChangeSetId)));
    }

    private static SetupCommandResult Dispatch(SetupTestPlatform platform, SetupOptions options) =>
        SetupCompositionRoot.CreateSetupDispatch(platform)(options);

    private static SetupCommandResult DispatchClaude(
        SetupTestPlatform platform,
        SetupOptions options) =>
        SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(new ClaudeHookCommand(
                "dotnet",
                ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"],
                ClaudeHookCommandMode.Repository)))(options);

    private static SetupLedgerStore OpenLedgerStore(
        SetupTestPlatform platform,
        SetupRuntimePaths paths)
    {
        var planStore = new SetupPlanStore(platform, paths);
        return new SetupLedgerStore(platform, paths, planStore);
    }

    private static void ScriptReadyClaude(SetupTestPlatform platform)
    {
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(new(
            SetupHttpProbeOutcome.Response,
            200,
            null,
            Encoding.UTF8.GetBytes(
                "{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
            true));
    }

    private static void SeedDirectoryChain(SetupTestPlatform platform, string directory)
    {
        var current = Path.GetPathRoot(directory)!;
        platform.SeedDirectory(current);
        foreach (var segment in directory[current.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            platform.SeedDirectory(current);
        }
    }

    private static void AssertNoHistoricalMutationActivity(
        SetupTestPlatform platform,
        int operationsBefore)
    {
        var operations = platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.replace", StringComparison.Ordinal) ||
            operation.StartsWith("file.move", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.DoesNotContain(operations, operation =>
            operation.Contains("C:\\Synthetic\\settings.json", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoClaudePlanActivity(
        SetupTestPlatform platform,
        int operationsBefore)
    {
        var operations = platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("process.run:claude:", StringComparison.Ordinal) ||
            operation.StartsWith("http.get:", StringComparison.Ordinal) ||
            operation.StartsWith("managed.read:", StringComparison.Ordinal) ||
            operation.StartsWith("process-environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.Contains("\\.claude\\settings.json", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("managed-settings.json", StringComparison.OrdinalIgnoreCase) ||
            operation.Contains("SOFTWARE\\Policies\\ClaudeCode", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ActualV1Fixture
    {
        private const string OwnershipSha256 =
            "b4dc2c24af501bd3f56fdaf6d6c1031b034d0c984a503965cfb39566b419e457";
        private const string PrivatePlanSha256 =
            "bd2da01340392f9c13bd265f6c2abc307aaeeffac86d9b0f78192e45899f1dac";

        private ActualV1Fixture(
            SetupTestPlatform platform,
            SetupRuntimePaths paths,
            byte[] ownershipBytes,
            byte[] privatePlanBytes)
        {
            Platform = platform;
            Paths = paths;
            OwnershipBytes = ownershipBytes;
            PrivatePlanBytes = privatePlanBytes;
        }

        public SetupTestPlatform Platform { get; }

        public SetupRuntimePaths Paths { get; }

        private byte[] OwnershipBytes { get; }

        private byte[] PrivatePlanBytes { get; }

        public static ActualV1Fixture Create()
        {
            var ownershipBytes = ReadFixture("ownership-ledger.v1.json");
            var privatePlanBytes = ReadFixture("private-plan.v1.json");
            Assert.Equal(
                OwnershipSha256,
                Convert.ToHexString(SHA256.HashData(ownershipBytes)).ToLowerInvariant());
            Assert.Equal(
                PrivatePlanSha256,
                Convert.ToHexString(SHA256.HashData(privatePlanBytes)).ToLowerInvariant());

            var platform = new SetupTestPlatform(Timestamp);
            var paths = new SetupRuntimePaths(platform);
            paths.EnsureRoot();
            platform.FileSystem.WriteAllBytes(paths.OwnershipLedger, ownershipBytes);
            platform.FileSystem.WriteAllBytes(
                paths.GetPlan(HistoricalChangeSetId),
                privatePlanBytes);
            return new ActualV1Fixture(platform, paths, ownershipBytes, privatePlanBytes);
        }

        public void AssertHistoricalBytesUnchanged()
        {
            Assert.Equal(OwnershipBytes, Platform.ReadSeededFile(Paths.OwnershipLedger));
            Assert.Equal(PrivatePlanBytes, Platform.ReadSeededFile(Paths.GetPlan(HistoricalChangeSetId)));
        }

        private static byte[] ReadFixture(string name) => File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Setup",
            "v1",
            name));
    }
}

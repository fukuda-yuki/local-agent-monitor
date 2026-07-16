using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeSetupTransactionHardeningTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private const string SettingsPath = "C:\\Users\\setup-test\\.claude\\settings.json";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void Apply_StaleClaudeSettingsBeforePreflightCreatesNoMutationArtifacts()
    {
        var original = "{\"unrelated\":1}\n"u8.ToArray();
        var concurrent = "{\"unrelated\":2}\n"u8.ToArray();
        var fixture = ClaudeTransactionFixture.Create(original);
        var plan = fixture.Plan();
        var changeSetId = ParseChangeSetId(plan);
        var recordId = Assert.Single(fixture.PlanStore.Load(changeSetId)!.Targets).RecordId;
        fixture.Platform.SeedFile(SettingsPath, concurrent);
        ScriptVersionAndReadiness(fixture.Platform);
        var operationStart = fixture.Platform.Operations.Count;

        var apply = fixture.Apply(changeSetId);

        Assert.False(apply.Success);
        Assert.Equal(SetupCodes.StalePlan, apply.Code);
        Assert.Equal(concurrent, fixture.Platform.ReadSeededFile(SettingsPath));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(changeSetId, recordId)));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(changeSetId)));
        var row = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.Planned, row.State);
        Assert.Equal(SetupCodes.StalePlan, row.OutcomeCode);
        Assert.DoesNotContain(
            fixture.Platform.Operations.Skip(operationStart),
            operation => IsSettingsWrite(operation));
    }

    [Fact]
    public async Task Apply_ClaudeSettingsEditAtFinalGuardPreservesExternalBytesAsPartial()
    {
        var original = "{\"unrelated\":1}\n"u8.ToArray();
        var external = "{\"unrelated\":2}\n"u8.ToArray();
        var fixture = ClaudeTransactionFixture.Create(original);
        var changeSetId = ParseChangeSetId(fixture.Plan());
        ScriptVersionAndReadiness(fixture.Platform);
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var applying = Task.Run(() => fixture.Apply(changeSetId));
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedFile(SettingsPath, external);
        barrier.Release();
        var apply = await applying;

        Assert.False(apply.Success);
        Assert.Equal(SetupCodes.PartialApply, apply.Code);
        Assert.Equal(external, fixture.Platform.ReadSeededFile(SettingsPath));
        var journal = Assert.IsType<SetupTransactionJournal>(fixture.JournalStore.Load(changeSetId));
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, Assert.Single(Assert.Single(journal.Targets).Steps).Phase);
        var row = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.Partial, row.State);
        Assert.DoesNotContain(
            fixture.Platform.Operations,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith("->" + SettingsPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ClaudeFailureAfterMutationCompensatesToExactOriginalBytesAndHookOrder()
    {
        var original = OriginalSettingsWithOrderedHooks();
        var fixture = ClaudeTransactionFixture.Create(original);
        var changeSetId = ParseChangeSetId(fixture.Plan());
        ScriptVersionAndReadiness(fixture.Platform);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}",
            new IOException("PRIVATE_CLAUDE_FORWARD_FAILURE"));

        var apply = fixture.Apply(changeSetId);

        Assert.False(apply.Success);
        Assert.Equal(SetupCodes.InternalError, apply.Code);
        Assert.Equal(original, fixture.Platform.ReadSeededFile(SettingsPath));
        var journal = Assert.IsType<SetupTransactionJournal>(fixture.JournalStore.Load(changeSetId));
        Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, Assert.Single(Assert.Single(journal.Targets).Steps).Phase);
        var row = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.Restored, row.State);
        Assert.Equal(SetupCodes.InternalError, row.OutcomeCode);
        Assert.DoesNotContain("PRIVATE_CLAUDE_FORWARD_FAILURE", apply.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_AllHasOneWritableClaudeTargetAndTwoNonParticipatingGuidanceTargets()
    {
        var fixture = ClaudeTransactionFixture.Create("{}\n"u8.ToArray());

        var plan = fixture.Plan(target: "all");

        var privatePlan = Assert.IsType<SetupPrivatePlan>(fixture.PlanStore.Load(ParseChangeSetId(plan)));
        Assert.Equal(
            [SetupTargetKind.Json, SetupTargetKind.Guidance, SetupTargetKind.Guidance],
            privatePlan.Targets.Select(target => target.TargetKind));
        Assert.Single(privatePlan.Targets, target => target.TargetKind != SetupTargetKind.Guidance);
        Assert.All(
            privatePlan.Targets.Where(target => target.TargetKind == SetupTargetKind.Guidance),
            target => Assert.All(target.Members, member => Assert.Equal(SetupOperation.NoOp, member.Operation)));
    }

    [Fact]
    public void Rollback_ClaudeSettingsRestoresExactOriginalBytesAndHookOrder()
    {
        var original = OriginalSettingsWithOrderedHooks();
        var fixture = ClaudeTransactionFixture.Create(original);
        var changeSetId = ParseChangeSetId(fixture.Plan());
        ScriptVersionAndReadiness(fixture.Platform);
        Assert.Equal(SetupCodes.ApplySucceeded, fixture.Apply(changeSetId).Code);

        var rollback = fixture.Rollback(changeSetId);

        Assert.True(rollback.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);
        Assert.Equal(original, fixture.Platform.ReadSeededFile(SettingsPath));
        var row = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.RolledBack, row.State);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, Assert.Single(row.Targets).RollbackStatus);
    }

    [Fact]
    public void RepeatedClaudePlanApplyUsesNoChangesAndSameAppliedIdIsNotReapplied()
    {
        var fixture = ClaudeTransactionFixture.Create("{}\n"u8.ToArray());
        var firstChangeSetId = ParseChangeSetId(fixture.Plan());
        ScriptVersionAndReadiness(fixture.Platform);
        Assert.Equal(SetupCodes.ApplySucceeded, fixture.Apply(firstChangeSetId).Code);
        var operationsAfterFirstApply = fixture.Platform.Operations.Count;

        var repeatedSameId = fixture.Apply(firstChangeSetId);

        Assert.False(repeatedSameId.Success);
        Assert.Equal(SetupCodes.InvalidArguments, repeatedSameId.Code);
        Assert.DoesNotContain(
            fixture.Platform.Operations.Skip(operationsAfterFirstApply),
            operation => IsSettingsWrite(operation));

        ScriptVersionAndReadiness(fixture.Platform);
        var secondPlan = fixture.Plan();
        Assert.True(secondPlan.Success);
        Assert.Equal(SetupCodes.NoChanges, secondPlan.Code);
        var secondChangeSetId = ParseChangeSetId(secondPlan);
        ScriptVersionAndReadiness(fixture.Platform);
        var secondApply = fixture.Apply(secondChangeSetId);

        Assert.True(secondApply.Success);
        Assert.Equal(SetupCodes.NoChanges, secondApply.Code);
        Assert.DoesNotContain(
            fixture.Platform.Operations.Skip(operationsAfterFirstApply),
            operation => IsSettingsWrite(operation));
        var rows = fixture.LedgerStore.Load().ChangeSets;
        Assert.Equal(SetupChangeSetState.Applied, rows.Single(row => row.ChangeSetId == firstChangeSetId).State);
        var noChanges = rows.Single(row => row.ChangeSetId == secondChangeSetId);
        Assert.Equal(SetupChangeSetState.NoChanges, noChanges.State);
        Assert.Null(Assert.Single(noChanges.Targets).BackupReference);
    }

    [Fact]
    public async Task ConcurrentClaudeApplyReturnsOneSuccessAndOneSetupBusyWithoutRetry()
    {
        var fixture = ClaudeTransactionFixture.Create("{}\n"u8.ToArray());
        var changeSetId = ParseChangeSetId(fixture.Plan());
        ScriptVersionAndReadiness(fixture.Platform);
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operationStart = fixture.Platform.Operations.Count;

        var first = Task.Run(() => fixture.Apply(changeSetId));
        barrier.WaitUntilReached(cancellation.Token);
        var second = fixture.Apply(changeSetId);
        barrier.Release();
        var firstResult = await first;

        Assert.True(firstResult.Success);
        Assert.Equal(SetupCodes.ApplySucceeded, firstResult.Code);
        Assert.False(second.Success);
        Assert.Equal(SetupCodes.SetupBusy, second.Code);
        var applyOperations = fixture.Platform.Operations.Skip(operationStart).ToArray();
        Assert.Equal(2, applyOperations.Count(operation => operation == $"file.lock:{fixture.Paths.Lock}"));
        Assert.Equal(1, applyOperations.Count(operation => operation == "process.run:claude:--version"));
        Assert.Equal(1, applyOperations.Count(operation => operation.StartsWith("http.get:", StringComparison.Ordinal)));
        Assert.Equal(SetupChangeSetState.Applied, Assert.Single(fixture.LedgerStore.Load().ChangeSets).State);
    }

    private static byte[] OriginalSettingsWithOrderedHooks() => Encoding.UTF8.GetBytes(
        "{\n" +
        "  // preserve this comment\n" +
        "  \"hooks\": {\n" +
        "    \"Stop\": [{\"hooks\":[{\"type\":\"command\",\"command\":\"first\",\"timeout\":3}]}],\n" +
        "    \"SessionStart\": [{\"hooks\":[{\"type\":\"command\",\"command\":\"second\",\"timeout\":4}]}]\n" +
        "  },\n" +
        "  \"unrelated\": true\n" +
        "}\n");

    private static bool IsSettingsWrite(string operation) =>
        operation.Contains(SettingsPath, StringComparison.Ordinal) &&
        (operation.StartsWith("file.write", StringComparison.Ordinal) ||
         operation.StartsWith("file.flush", StringComparison.Ordinal) ||
         operation.StartsWith("file.replace", StringComparison.Ordinal) ||
         operation.StartsWith("file.move", StringComparison.Ordinal));

    private static Guid ParseChangeSetId(SetupCommandResult result)
    {
        Assert.NotNull(result.ChangeSetId);
        return Guid.Parse(result.ChangeSetId);
    }

    private static void ScriptVersionAndReadiness(SetupTestPlatform platform)
    {
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(new(
            CopilotAgentObservability.ConfigCli.Setup.Platform.SetupHttpProbeOutcome.Response,
            200,
            null,
            Encoding.UTF8.GetBytes("{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
            true));
    }

    private sealed class ClaudeTransactionFixture
    {
        private ClaudeTransactionFixture(byte[] initialSettings)
        {
            Platform = new SetupTestPlatform(Timestamp);
            SeedDirectoryChain(Platform, Path.GetDirectoryName(SettingsPath)!);
            Platform.SeedFile(SettingsPath, initialSettings);
            ScriptVersionAndReadiness(Platform);
            Paths = new SetupRuntimePaths(Platform);
            PlanStore = new SetupPlanStore(Platform, Paths);
            LedgerStore = new SetupLedgerStore(Platform, Paths, PlanStore);
            JournalStore = new SetupTransactionJournalStore(Platform, Paths);
            var adapter = new ClaudeCodeSetupAdapter(
                Platform,
                new ClaudeAgentSdkTargetPartition(),
                new FixedClaudeHookCommandProvider(new ClaudeHookCommand(
                    "dotnet",
                    ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"],
                    ClaudeHookCommandMode.Repository)),
                new ClaudeHigherPrecedenceObserver(
                    Platform,
                    "C:\\synthetic-repository",
                    "C:\\Program Files\\ClaudeCode\\managed-settings.json"));
            Dispatcher = new SetupCommandDispatcher(
                Platform,
                Paths,
                PlanStore,
                LedgerStore,
                JournalStore,
                new SetupAdapterRegistry([adapter]),
                "1.2.3");
        }

        public SetupTestPlatform Platform { get; }

        public SetupRuntimePaths Paths { get; }

        public SetupPlanStore PlanStore { get; }

        public SetupLedgerStore LedgerStore { get; }

        public SetupTransactionJournalStore JournalStore { get; }

        private SetupCommandDispatcher Dispatcher { get; }

        public static ClaudeTransactionFixture Create(byte[] initialSettings) => new(initialSettings);

        public SetupCommandResult Plan(bool includeContentCapture = false, string target = "cli") => Dispatcher.Dispatch(new SetupOptions(
            SetupCommand.Plan,
            "claude-code",
            target,
            Endpoint,
            includeContentCapture,
            null));

        public SetupCommandResult Apply(Guid changeSetId) => Dispatcher.Dispatch(new SetupOptions(
            SetupCommand.Apply,
            null,
            null,
            null,
            false,
            changeSetId));

        public SetupCommandResult Rollback(Guid changeSetId) => Dispatcher.Dispatch(new SetupOptions(
            SetupCommand.Rollback,
            null,
            null,
            null,
            false,
            changeSetId));

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
    }
}

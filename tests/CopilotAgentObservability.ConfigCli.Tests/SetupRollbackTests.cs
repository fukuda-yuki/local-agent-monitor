using System.Text;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupRollbackTests
{
    [Fact]
    public void Rollback_Restores_real_apply_artifacts_in_reverse_order_and_clears_ownership()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RequestedChangeSetId);
        Assert.Null(result.Recovery);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.All(journal.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.RestoreCompleted, step.Phase));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupCodes.RollbackSucceeded, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
        });
        var restores = fixture.Platform.Operations.Skip(baseline)
            .Where(operation => fixture.TargetPaths.Any(path =>
                operation.EndsWith("->" + path, StringComparison.Ordinal)))
            .ToArray();
        Assert.EndsWith("->" + fixture.TargetPaths[1], restores[0], StringComparison.Ordinal);
        Assert.EndsWith("->" + fixture.TargetPaths[0], restores[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_Stale_preflight_preserves_every_target_and_apply_journal()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackStale, result.Code);
        Assert.Equal("third-party", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("new-1", fixture.ReadText(fixture.TargetPaths[1]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.RollbackStale, durable.OutcomeCode);
        Assert.All(durable.Targets, target => Assert.Equal(SetupLedgerRollbackStatus.Pending, target.RollbackStatus));
    }

    [Fact]
    public void Rollback_Fault_after_supersession_leaves_dormant_pair_and_retry_reuses_it()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}",
            new IOException("private-supersession-boundary"));

        var interrupted = fixture.Rollback();

        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalOperation.Rollback, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));

        var retried = fixture.Rollback();

        Assert.True(retried.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, retried.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public void Rollback_Fault_after_ledger_before_restore_is_completed_by_next_mandatory_recovery()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-ledger-boundary"));

        var interrupted = fixture.Rollback();

        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));

        var recovered = fixture.Rollback();

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.NotNull(recovered.Recovery);
        Assert.Equal(fixture.ChangeSetId, recovered.RequestedChangeSetId);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery!.RecoveredChangeSetId);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public async Task Rollback_Edit_after_preflight_is_preserved_as_partial_while_safe_targets_continue()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var rollingBack = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
        boundary.Release();

        var result = await rollingBack;

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.PartialRollback, result.Code);
        Assert.Equal("third-party", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-1", fixture.ReadText(fixture.TargetPaths[1]));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, durable.State);
        Assert.Equal(SetupCodes.PartialRollback, durable.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, durable.Targets[0].RollbackStatus);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, durable.Targets[1].RollbackStatus);
    }

    [Fact]
    public async Task Rollback_Prior_state_after_preflight_is_accepted_idempotently()
    {
        var fixture = RollbackFixture.Create();
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var rollingBack = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("old-0"));
        boundary.Release();

        var result = await rollingBack;

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public void Rollback_Mandatory_recovery_short_circuits_and_preserves_requested_correlation()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-ledger-boundary"));
        Assert.Equal(SetupCodes.RecoveryRequired, fixture.Rollback().Code);
        var requested = Guid.Parse("00000000-0000-7000-8000-000000000999");

        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var result = fixture.Coordinator.Rollback(acquisition.Lock!, requested);

        Assert.True(result.Success);
        Assert.Equal(requested, result.RequestedChangeSetId);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.Recovery!.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Recovery.Operation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public void Rollback_Invalid_backup_fails_before_supersession_or_target_write(string variant)
    {
        var fixture = RollbackFixture.Create();
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordIds[0]);
        if (variant == "missing")
        {
            fixture.Platform.FileSystem.DeleteFile(backup);
        }
        else
        {
            fixture.Platform.SeedFile(backup, Encoding.UTF8.GetBytes("not-a-backup"));
        }
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Repeated_request_is_not_available_and_does_not_restore_again()
    {
        var fixture = RollbackFixture.Create();
        Assert.True(fixture.Rollback().Success);
        var baseline = fixture.Platform.Operations.Count;

        var repeated = fixture.Rollback();

        Assert.False(repeated.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, repeated.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Reparse_target_is_rejected_before_supersession()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.SeedPathMetadata(
            fixture.TargetPaths[0],
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.UnsafePath, result.Code);
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Rebound_plan_ledger_identity_fails_closed_before_supersession()
    {
        var fixture = RollbackFixture.Create();
        fixture.RebindLedgerToolVersion("2.0.0");
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
    }

    [Fact]
    public void Rollback_Restores_missing_previous_state_by_deleting_the_applied_file()
    {
        var fixture = RollbackFixture.Create(previousMissing: true);
        Assert.True(fixture.Platform.FileSystem.FileExists(fixture.TargetPaths[0]));

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.TargetPaths[0]));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public async Task Rollback_Same_lock_serializes_the_entire_operation_without_sleep_or_retry()
    {
        var fixture = RollbackFixture.Create();
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var secondStarted = new ManualResetEventSlim();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var first = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId);
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.IsCompleted);

        boundary.Release();
        var firstResult = await first;
        var secondResult = await second;

        Assert.True(firstResult.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, secondResult.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Theory]
    [InlineData("phase", false)]
    [InlineData("phase", true)]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("primitive", false)]
    [InlineData("primitive", true)]
    [InlineData("completion", false)]
    [InlineData("completion", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("ledger", false)]
    [InlineData("ledger", true)]
    public async Task Rollback_Current_execution_fault_matrix_never_returns_recovery_correlation(
        string boundary,
        bool afterEffect)
    {
        var fixture = RollbackFixture.Create();
        SetupRollbackExecutionResult direct;
        if (boundary == "ledger")
        {
            using var checkpoint = fixture.Platform.AddBarrier(
                $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
            var rollingBack = Task.Run(fixture.Rollback);
            checkpoint.WaitUntilReached(CancellationToken.None);
            fixture.InjectTerminalLedgerFault(afterEffect);
            checkpoint.Release();
            direct = await rollingBack;
        }
        else
        {
            fixture.InjectNormalRollbackFault(boundary, afterEffect);
            direct = fixture.Rollback();
            Assert.Contains(fixture.InjectedOperation!, fixture.Platform.Operations);
        }

        Assert.Null(direct.Recovery);
        Assert.Equal(afterEffect, direct.Success);
        Assert.DoesNotContain("PRIVATE", direct.Code, StringComparison.Ordinal);
        Assert.Equal(
            afterEffect
                ? SetupCodes.RollbackSucceeded
                : boundary == "primitive" ? SetupCodes.PartialRollback : SetupCodes.RecoveryRequired,
            direct.Code);

        var reopened = fixture.Rollback(fixture.ReopenCoordinator());
        if (afterEffect)
        {
            Assert.Equal(SetupCodes.RollbackNotAvailable, reopened.Code);
            Assert.Null(reopened.Recovery);
        }
        else
        {
            Assert.Equal(SetupCodes.InterruptedRollbackRecovered, reopened.Code);
            Assert.Equal(fixture.ChangeSetId, reopened.Recovery!.RecoveredChangeSetId);
            Assert.Equal(SetupRecoveryOperation.Rollback, reopened.Recovery.Operation);
        }
    }

    [Fact]
    public void Rollback_Mixed_applied_artifact_persists_not_available_without_mutation()
    {
        var fixture = RollbackFixture.Create(includeEnvironment: true);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.RollbackNotAvailable, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.NotNull(target.AppliedStateHash);
            Assert.NotNull(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Pending, target.RollbackStatus);
        });
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
    }

    private static bool IsTargetMutation(string operation, IReadOnlyList<string> targetPaths) =>
        targetPaths.Any(path =>
            operation.EndsWith("->" + path, StringComparison.Ordinal) ||
            string.Equals(operation, "file.delete:" + path, StringComparison.Ordinal));

    private sealed class RollbackFixture
    {
        private RollbackFixture(int fileCount, bool previousMissing, bool includeEnvironment)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 8, 9, 10, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000601");
            RecordIds = Enumerable.Range(0, fileCount)
                .Select(index => Guid.Parse($"00000000-0000-7000-8000-{602 + index:000000000000}"))
                .ToArray();
            TargetPaths = Enumerable.Range(0, fileCount)
                .Select(index => Path.Combine(Platform.LocalApplicationData, $"settings-{index}.json"))
                .ToArray();
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            for (var index = 0; index < fileCount; index++)
            {
                if (!previousMissing)
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes($"old-{index}"));
                }
            }

            var targets = Enumerable.Range(0, fileCount).Select(index => new SetupPrivatePlanTarget(
                RecordIds[index],
                SetupTargetKind.Json,
                TargetPaths[index],
                SetupHash.File(!previousMissing, previousMissing ? [] : Encoding.UTF8.GetBytes($"old-{index}")),
                $"new-{index}",
                [new SetupPrivatePlanMember($"setting-{index}", SetupOperation.Replace, $"new-{index}")])).ToList();
            if (includeEnvironment)
            {
                Platform.SeedUserEnvironment("ENV_A", "old-a");
                var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(["ENV_A"]);
                targets.Add(new SetupPrivatePlanTarget(
                    Guid.Parse("00000000-0000-7000-8000-000000000699"),
                    SetupTargetKind.Env,
                    "current-user",
                    environmentCapture.AggregateHash,
                    "environment-allowlist",
                    [new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a")]));
            }
            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                targets);
            var ledger = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                targets.Select((target, index) => new SetupLedgerTarget(
                    target.RecordId,
                    target.TargetKind,
                    target.TargetKind == SetupTargetKind.Env ? "user-environment" : $"settings-{index}",
                    "github-copilot",
                    target.TargetKind == SetupTargetKind.Env
                        ? [new SetupLedgerMember("ENV_A", SetupOperation.Replace)]
                        : [new SetupLedgerMember($"setting-{index}", SetupOperation.Replace)],
                    target.BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    target.TargetKind == SetupTargetKind.Env
                        ? SetupRestartRequirement.RestartTerminalSession
                        : SetupRestartRequirement.RestartVsCode,
                    "1.0.0")).ToArray());
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using (var acquisition = SetupLock.TryAcquire(Platform, Paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
                _ = new SetupApplyCoordinator(
                    Platform, Paths, planStore, ledgerStore, journalStore, new PassRevalidator())
                    .Apply(acquisition.Lock!, ChangeSetId);
            }

            Coordinator = new SetupRollbackCoordinator(Platform, Paths, planStore, ledgerStore, journalStore);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public IReadOnlyList<Guid> RecordIds { get; }
        public IReadOnlyList<string> TargetPaths { get; }
        public SetupRollbackCoordinator Coordinator { get; }
        public string? InjectedOperation { get; private set; }

        public static RollbackFixture Create(
            int fileCount = 1,
            bool previousMissing = false,
            bool includeEnvironment = false) =>
            new(fileCount, previousMissing, includeEnvironment);

        public SetupRollbackExecutionResult Rollback()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return Coordinator.Rollback(acquisition.Lock!, ChangeSetId);
        }

        public SetupRollbackExecutionResult Rollback(SetupRollbackCoordinator coordinator)
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return coordinator.Rollback(acquisition.Lock!, ChangeSetId);
        }

        public SetupRollbackCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRollbackCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public void InjectNormalRollbackFault(string boundary, bool afterEffect)
        {
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            var operation = boundary switch
            {
                "phase" => $"file.replace:{NextTemporaryPath(journalPath, 1)}->{journalPath}",
                "intent" => $"file.replace:{NextTemporaryPath(journalPath, 2)}->{journalPath}",
                "primitive" => $"file.replace:{NextTemporaryPath(TargetPaths[0], 3)}->{TargetPaths[0]}",
                "completion" => $"file.replace:{NextTemporaryPath(journalPath, 4)}->{journalPath}",
                "commit" => $"file.replace:{NextTemporaryPath(journalPath, 5)}->{journalPath}",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            };
            InjectedOperation = operation;
            if (afterEffect)
            {
                Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_NORMAL_ROLLBACK_AFTER"));
            }
            else
            {
                Platform.InjectFault(operation, new IOException("PRIVATE_NORMAL_ROLLBACK_BEFORE"));
            }
        }

        public void InjectTerminalLedgerFault(bool afterEffect)
        {
            var operation = $"file.replace:{Paths.OwnershipLedger}.tmp->{Paths.OwnershipLedger}";
            InjectedOperation = operation;
            if (afterEffect)
            {
                Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_NORMAL_LEDGER_AFTER"));
            }
            else
            {
                Platform.InjectFault(operation, new IOException("PRIVATE_NORMAL_LEDGER_BEFORE"));
            }
        }

        private string NextTemporaryPath(string destination, int offset)
        {
            var maximum = Platform.Operations
                .SelectMany(operation => Regex.Matches(operation,
                    @"\.cao-00000000-0000-7000-8000-(?<value>[0-9]{12})\.tmp"))
                .Select(match => long.Parse(match.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            return destination + ".cao-" +
                Guid.Parse($"00000000-0000-7000-8000-{maximum + offset + 1:D12}").ToString("D") + ".tmp";
        }

        public string ReadText(string path) => Encoding.UTF8.GetString(Platform.ReadSeededFile(path));

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets
                .Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
        }

        public void RebindLedgerToolVersion(string version)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var ledger = ledgerStore.LoadForRecovery();
            var rebound = ledger.ChangeSets[0] with
            {
                ToolVersion = version,
                Targets = ledger.ChangeSets[0].Targets
                    .Select(target => target with { ToolVersion = version })
                    .ToArray(),
            };
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.Save(acquisition.Lock!, ledger with { ChangeSets = [rebound] });
        }
    }

    private sealed class PassRevalidator : ISetupApplyRevalidator
    {
        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupRecoveryTests
{
    private static SetupStatusProjection CreateStatusProjection(
        IReadOnlyList<SetupLedgerMember> members,
        bool suppressManifest = false)
    {
        var operations = members.Select(member => member.Operation).Where(operation => operation != SetupOperation.NoOp).Distinct().ToArray();
        var aggregate = operations.Length switch { 0 => SetupOperation.NoOp, 1 => operations[0], _ => SetupOperation.Mixed };
        var expectedResult = !suppressManifest && members.All(member => member.SettingKey.StartsWith("ENV_", StringComparison.Ordinal))
            ? SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson
            : (JsonElement?)null;
        return new SetupStatusProjection(true, null, aggregate, null, null, expectedResult, null,
            members.Select(member => new SetupMemberChangeResult(member.SettingKey, member.Operation, "present", "configured", "none", false)).ToArray());
    }
    [Fact]
    public void RecoverNext_Ignores_normal_planned_change_set_without_journal()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        Assert.Null(result.Code);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.Operation);
        Assert.Null(result.EffectiveChangeSet);
    }

    [Fact]
    public void RecoverNext_Ignores_actual_apply_produced_stale_planned_attempt_after_reopen()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        var apply = new SetupApplyCoordinator(
            fixture.Platform,
            fixture.Paths,
            planStore,
            ledgerStore,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths),
            new CallbackApplyRevalidator(() =>
                fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("external"))));
        using (var applyLock = fixture.AcquireLock())
        {
            var exception = Assert.Throws<SetupApplyException>(() =>
                apply.Apply(applyLock, fixture.ChangeSetId));
            Assert.Equal(SetupCodes.StalePlan, exception.Code);
        }

        using var recoveryLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(recoveryLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        var stale = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Planned, stale.State);
        Assert.Equal(SetupCodes.StalePlan, stale.OutcomeCode);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
    }

    [Fact]
    public void RecoverNext_Reconciles_committed_apply_journal_before_stale_ledger()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.NotNull(result.EffectiveChangeSet);
        Assert.Equal(SetupChangeSetState.Applied, result.EffectiveChangeSet.State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.EffectiveChangeSet.OutcomeCode);
        var target = Assert.Single(result.EffectiveChangeSet.Targets);
        Assert.Equal(fixture.DesiredFileHash, target.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, target.RollbackStatus);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);

        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, durable.OutcomeCode);
        Assert.Equal(fixture.DesiredFileHash, Assert.Single(durable.Targets).AppliedStateHash);
    }

    [Fact]
    public void RecoverNext_Reconciles_actual_mixed_apply_producer_after_restart()
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterCommitBeforeLedger}",
            new IOException("private-apply-interruption"));
        using (var applyLock = fixture.AcquireLock())
        {
            var exception = Assert.Throws<SetupApplyException>(() =>
                fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId));
            Assert.Equal(SetupCodes.InternalError, exception.Code);
        }

        var restartedPlanStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var restartedLedgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, restartedPlanStore);
        var restartedJournalStore = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths);
        var producedPlan = Assert.IsType<SetupPrivatePlan>(restartedPlanStore.Load(fixture.ChangeSetId));
        var producedLedger = Assert.IsType<SetupOwnershipLedger>(restartedLedgerStore.LoadForRecovery());
        var producedJournal = Assert.IsType<SetupTransactionJournal>(
            restartedJournalStore.Load(fixture.ChangeSetId));
        Assert.Equal(SetupChangeSetState.Applying, Assert.Single(producedLedger.ChangeSets).State);
        Assert.Equal(SetupJournalPhase.Committed, producedJournal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, producedJournal.EnvironmentNotification);
        Assert.Equal([fixture.FileRecordId, fixture.EnvironmentRecordId],
            producedJournal.Targets.Select(target => target.RecordId));
        Assert.Null(Assert.Single(producedJournal.Targets[0].Steps).MemberKey);
        Assert.Equal(["ENV_A"], producedJournal.Targets[1].Steps.Select(step => step.MemberKey));
        Assert.All(producedJournal.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.MutationCompleted, step.Phase));
        var environmentPlan = Assert.Single(producedPlan.Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(["ENV_A", "ENV_B"], environmentPlan.Members.Select(member => member.SettingKey));
        var backup = new UserEnvironmentSetupStep(fixture.Platform).ReadBackup(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId),
            ["ENV_A", "ENV_B"]);
        Assert.Equal(environmentPlan.BaseStateHash, backup.AggregateHash);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
        var operationsBeforeRecovery = fixture.Platform.Operations.Count;

        var recoveryCoordinator = new SetupRecoveryCoordinator(
            fixture.Platform,
            fixture.Paths,
            restartedPlanStore,
            restartedLedgerStore,
            restartedJournalStore);
        using var recoveryLock = fixture.AcquireLock();
        var result = recoveryCoordinator.RecoverNext(recoveryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Applied, result.EffectiveChangeSet?.State);
        var appliedFile = Assert.Single(result.EffectiveChangeSet!.Targets,
            target => target.RecordId == fixture.FileRecordId);
        var appliedEnvironment = Assert.Single(result.EffectiveChangeSet.Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupHash.File(true, Encoding.UTF8.GetBytes("new")), appliedFile.AppliedStateHash);
        var fullEnvironmentCapture = new UserEnvironmentSetupStep(fixture.Platform)
            .Capture(["ENV_A", "ENV_B"]);
        Assert.Equal(fullEnvironmentCapture.AggregateHash, appliedEnvironment.AppliedStateHash);
        Assert.NotEqual(
            new UserEnvironmentSetupStep(fixture.Platform).Capture(["ENV_A"]).AggregateHash,
            appliedEnvironment.AppliedStateHash);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("outside-model-a", fixture.Platform.ReadUserEnvironment("UNRELATED"));
        var recoveredJournal = restartedJournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Committed, recoveredJournal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, recoveredJournal.EnvironmentNotification);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBeforeRecovery).ToArray();
        Assert.Equal(1, recoveryOperations.Count(operation => operation == "environment.notify"));
        Assert.DoesNotContain(recoveryOperations, operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoverNext_Actual_apply_before_environment_effect_recovers_applying_intent_after_fresh_store_restart()
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.Platform.InjectFault(
            "environment.set:ENV_A",
            new IOException("private-forward-before-effect"),
            () => fixture.InjectTwoJournalReadFaults());
        using (var applyLock = fixture.AcquireLock())
        {
            var exception = Assert.Throws<SetupApplyException>(() =>
                fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId));
            Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        }

        var restarted = AssertApplyingProducerEvidence(
            fixture,
            SetupJournalStepPhase.MutationStarted,
            "old-a");
        var operationsBeforeRecovery = fixture.Platform.Operations.Count;

        using var recoveryLock = fixture.AcquireLock();
        var result = restarted.Coordinator.RecoverNext(recoveryLock);

        AssertRecoveredProducerState(fixture, restarted, result);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBeforeRecovery).ToArray();
        Assert.DoesNotContain("environment.set:ENV_A", recoveryOperations);
        AssertRecoveryOperations(fixture, recoveryOperations);
    }

    [Fact]
    public void RecoverNext_Actual_apply_after_environment_effect_recovers_applying_intent_after_fresh_store_restart()
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_A",
            new IOException("private-forward-after-effect"),
            fixture.InjectTwoJournalReadFaults);
        using (var applyLock = fixture.AcquireLock())
        {
            var exception = Assert.Throws<SetupApplyException>(() =>
                fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId));
            Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        }

        var restarted = AssertApplyingProducerEvidence(
            fixture,
            SetupJournalStepPhase.MutationStarted,
            "desired-a");
        var operationsBeforeRecovery = fixture.Platform.Operations.Count;

        using var recoveryLock = fixture.AcquireLock();
        var result = restarted.Coordinator.RecoverNext(recoveryLock);

        AssertRecoveredProducerState(fixture, restarted, result);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBeforeRecovery).ToArray();
        Assert.Equal(1, recoveryOperations.Count(operation => operation == "environment.set:ENV_A"));
        AssertRecoveryOperations(fixture, recoveryOperations);
    }

    [Fact]
    public async Task RecoverNext_Actual_apply_compensation_restore_intent_recovers_after_fresh_store_restart()
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_A",
            new IOException("private-forward-after-effect"));
        SetupApplyException exception;
        using (var restoreCompletion = fixture.Platform.AddBarrier(
                   $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}"))
        using (var applyLock = fixture.AcquireLock())
        {
            var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
                fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId)));
            restoreCompletion.WaitUntilReached(CancellationToken.None);
            fixture.InjectNextJournalWriteFault();
            restoreCompletion.Release();
            exception = await applying;
        }

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        var restarted = AssertCompensatingProducerEvidence(fixture);
        var operationsBeforeRecovery = fixture.Platform.Operations.Count;

        using var recoveryLock = fixture.AcquireLock();
        var result = restarted.Coordinator.RecoverNext(recoveryLock);

        AssertRecoveredProducerState(fixture, restarted, result);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBeforeRecovery).ToArray();
        Assert.DoesNotContain("environment.set:ENV_A", recoveryOperations);
        AssertRecoveryOperations(fixture, recoveryOperations);
        var environmentClassification = Array.FindIndex(
            recoveryOperations,
            operation => operation == "environment.get:ENV_A");
        var fileRestore = Array.FindIndex(
            recoveryOperations,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        Assert.True(environmentClassification >= 0 && fileRestore > environmentClassification);
    }

    [Fact]
    public void RecoverNext_Actual_apply_produced_rollback_with_missing_noop_completes_after_ledger_interruption()
    {
        var fixture = new ApplyProducedRecoveryFixture(missingNoOp: true);
        using (var applyLock = fixture.AcquireLock())
        {
            var applied = fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId).Value;
            Assert.Equal(SetupChangeSetState.Applied, applied.State);
        }
        var appliedEnvironment = Assert.Single(
            fixture.LoadChangeSet().Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        var environmentStep = new UserEnvironmentSetupStep(fixture.Platform);
        var appliedCapture = environmentStep.Capture(["ENV_A", "ENV_B"]);
        var backup = environmentStep.ReadBackup(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId),
            ["ENV_A", "ENV_B"]);
        Assert.Equal(backup.AggregateHash, appliedEnvironment.PreviousStateHash);
        Assert.Equal(appliedCapture.AggregateHash, appliedEnvironment.AppliedStateHash);
        Assert.False(backup.Members[1].Value.Exists);
        Assert.Null(backup.Members[1].Value.Value);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-rollback-interruption"));
        using (var rollbackLock = fixture.AcquireLock())
        {
            var interrupted = fixture.CreateRollbackCoordinator().Rollback(
                rollbackLock,
                fixture.ChangeSetId);
            Assert.False(interrupted.Success);
            Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
            Assert.Null(interrupted.Recovery);
        }
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        var prepared = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, prepared.Operation);
        Assert.Equal(SetupJournalPhase.Prepared, prepared.Phase);
        var preparedEnvironment = prepared.Targets.Single(
            target => target.RecordId == fixture.EnvironmentRecordId);
        var preparedStep = Assert.Single(preparedEnvironment.Steps);
        Assert.Equal("ENV_A", preparedStep.MemberKey);
        Assert.Equal(backup.Members[0].Hash, preparedStep.PriorStateHash);
        Assert.Equal(
            environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
            preparedStep.DesiredStateHash);
        Assert.Equal(fixture.EnvironmentRecordId.ToString("D"), preparedStep.BackupReference);
        var baseline = fixture.Platform.Operations.Count;

        using var recoveryLock = fixture.AcquireLock();
        var recovered = fixture.CreateRecoveryCoordinator().RecoverNext(recoveryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Equal(fixture.ChangeSetId, recovered.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, recovered.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, recovered.EffectiveChangeSet?.State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        var recoveryOperations = fixture.Platform.Operations.Skip(baseline).ToArray();
        Assert.Contains("environment.get:ENV_B", recoveryOperations);
        Assert.DoesNotContain("environment.set:ENV_B", recoveryOperations);
        Assert.Equal(1, recoveryOperations.Count(operation => operation == "environment.notify"));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        var committedStep = Assert.Single(
            journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps);
        Assert.Equal("ENV_A", committedStep.MemberKey);
        Assert.Equal(preparedStep.PriorStateHash, committedStep.PriorStateHash);
        Assert.Equal(preparedStep.DesiredStateHash, committedStep.DesiredStateHash);
        Assert.Equal(preparedStep.BackupReference, committedStep.BackupReference);
    }

    [Fact]
    public void RecoverNext_Actual_apply_produced_rollback_preserves_missing_noop_drift_then_retries()
    {
        var fixture = new ApplyProducedRecoveryFixture(missingNoOp: true);
        using (var applyLock = fixture.AcquireLock())
        {
            _ = fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId);
        }
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-rollback-interruption"));
        using (var rollbackLock = fixture.AcquireLock())
        {
            var interrupted = fixture.CreateRollbackCoordinator().Rollback(
                rollbackLock,
                fixture.ChangeSetId);
            Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        }
        fixture.Platform.SeedUserEnvironment("ENV_B", "third-party");
        var baseline = fixture.Platform.Operations.Count;
        using (var recoveryLock = fixture.AcquireLock())
        {
            var failed = fixture.CreateRecoveryCoordinator().RecoverNext(recoveryLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(fixture.ChangeSetId, failed.RecoveredChangeSetId);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain("environment.set:ENV_B", fixture.Platform.Operations.Skip(baseline));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations.Skip(baseline));

        fixture.Platform.SeedUserEnvironment("ENV_B", null);
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.CreateRecoveryCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, retried.Code);
        Assert.Equal(fixture.ChangeSetId, retried.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, retried.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, retried.EffectiveChangeSet?.State);
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain("environment.set:ENV_B", fixture.Platform.Operations.Skip(baseline));
        Assert.Equal(1, fixture.Platform.Operations.Skip(baseline)
            .Count(operation => operation == "environment.notify"));
        var terminal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Committed, terminal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, terminal.EnvironmentNotification);
        Assert.Equal(["ENV_A"],
            terminal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId)
                .Steps.Select(step => step.MemberKey));
    }

    [Fact]
    public void RecoverNext_Ignores_exact_dormant_prepared_apply_without_mutating_or_rewriting_artifacts()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedApplyJournalAndBackup();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(recoveryOperations, operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.replace", StringComparison.Ordinal) ||
            operation.StartsWith("file.move", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Theory]
    [InlineData("missing-backup")]
    [InlineData("corrupt-backup")]
    [InlineData("stale-base")]
    [InlineData("reparse-backup")]
    public void RecoverNext_Fails_closed_for_nonexact_dormant_evidence_without_target_write(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedApplyJournalAndBackup();
        if (variant == "missing-backup")
        {
            fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId));
        }
        else if (variant == "corrupt-backup")
        {
            fixture.Platform.SeedFile(
                fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId),
                Encoding.UTF8.GetBytes("private-path-and-token-must-not-escape"));
        }
        else if (variant == "stale-base")
        {
            fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party"));
        }
        else
        {
            fixture.Platform.SeedPathMetadata(
                fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId),
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(recoveryOperations, operation =>
            operation.StartsWith($"file.write:{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation.StartsWith($"file.write-new:{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoverNext_Reconciles_only_oldest_candidate_then_uses_lowercase_uuid_ordinal_tie_order()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        var earlier = fixture.AddFileChangeSet(
            Guid.Parse("00000000-0000-7000-8000-000000000601"),
            Guid.Parse("00000000-0000-7000-8000-000000000602"),
            fixture.Plan.CreatedAt.AddMinutes(-1),
            "earlier");
        var lowerUuidTie = fixture.AddFileChangeSet(
            Guid.Parse("00000000-0000-7000-8000-000000000401"),
            Guid.Parse("00000000-0000-7000-8000-000000000402"),
            fixture.Plan.CreatedAt,
            "lower-tie");
        fixture.SeedCommittedApplyJournalAndApplyingLedger(fixture.PrimarySeed);
        fixture.SeedCommittedApplyJournalAndApplyingLedger(earlier);
        fixture.SeedCommittedApplyJournalAndApplyingLedger(lowerUuidTie);

        using (var setupLock = fixture.AcquireLock())
        {
            var first = fixture.ReopenCoordinator().RecoverNext(setupLock);
            Assert.Equal(earlier.ChangeSetId, first.RecoveredChangeSetId);
        }

        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(lowerUuidTie.ChangeSetId).State);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(fixture.ChangeSetId).State);

        using var secondLock = fixture.AcquireLock();
        var second = fixture.ReopenCoordinator().RecoverNext(secondLock);
        Assert.Equal(lowerUuidTie.ChangeSetId, second.RecoveredChangeSetId);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(fixture.ChangeSetId).State);
    }

    [Fact]
    public void RecoverNext_Oldest_failed_candidate_blocks_later_recoverable_candidate()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedApplyJournalAndBackup();
        fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId));
        var later = fixture.AddFileChangeSet(
            Guid.Parse("00000000-0000-7000-8000-000000000701"),
            Guid.Parse("00000000-0000-7000-8000-000000000702"),
            fixture.Plan.CreatedAt.AddMinutes(1),
            "later");
        fixture.SeedCommittedApplyJournalAndApplyingLedger(later);
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
    }

    [Fact]
    public void RecoverNext_Reconciles_restored_apply_journal_to_restored_ledger()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedRestoredApplyJournalAndCompensatingLedger();
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(SetupChangeSetState.Restored, result.EffectiveChangeSet?.State);
        var target = Assert.Single(result.EffectiveChangeSet!.Targets);
        Assert.Null(target.AppliedStateHash);
        Assert.Null(target.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Terminal_apply_verifies_full_environment_list_including_no_op_member(bool driftNoOpMember)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        if (driftNoOpMember)
        {
            fixture.Platform.SeedUserEnvironment("ENV_B", "third-party");
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(driftNoOpMember ? SetupRecoveryDisposition.Failed : SetupRecoveryDisposition.Recovered,
            result.Disposition);
        Assert.Equal(driftNoOpMember ? SetupCodes.InterruptedRecoveryFailed : SetupCodes.InterruptedApplyRecovered,
            result.Code);
        Assert.Equal(driftNoOpMember ? SetupChangeSetState.Partial : SetupChangeSetState.Applied,
            result.EffectiveChangeSet?.State);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(driftNoOpMember ? "third-party" : "stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(recoveryOperations, operation => operation.StartsWith("environment.set", StringComparison.Ordinal));
        Assert.Equal(!driftNoOpMember, recoveryOperations.Count(operation => operation == "environment.notify") == 1);
        var journal = fixture.LoadJournal();
        Assert.Equal(driftNoOpMember ? SetupEnvironmentNotification.Pending : SetupEnvironmentNotification.Completed,
            journal.EnvironmentNotification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Restored_apply_verifies_full_environment_previous_list(bool driftNoOpMember)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedRestoredApplyJournalAndCompensatingLedger();
        if (driftNoOpMember)
        {
            fixture.Platform.SeedUserEnvironment("ENV_B", "third-party");
        }

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(driftNoOpMember ? SetupRecoveryDisposition.Failed : SetupRecoveryDisposition.Recovered,
            result.Disposition);
        Assert.Equal(driftNoOpMember ? SetupChangeSetState.Partial : SetupChangeSetState.Restored,
            result.EffectiveChangeSet?.State);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(driftNoOpMember ? "third-party" : "stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(driftNoOpMember ? SetupEnvironmentNotification.Pending : SetupEnvironmentNotification.Completed,
            fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Rollback_notification_only_uses_rollback_correlation_without_target_read()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedRollbackJournalAndRolledBackLedger();
        fixture.Platform.SeedUserEnvironment("ENV_B", "post-rollback-drift");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal));
        Assert.Equal("post-rollback-drift", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Notification_only_does_not_reconcile_drifted_targets_and_marks_completion_last()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        fixture.Platform.SeedUserEnvironment("ENV_B", "post-commit-drift");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal("post-commit-drift", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(operations, operation => operation.StartsWith("environment.get", StringComparison.Ordinal));
        var ledgerReplace = Array.FindIndex(operations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        var notify = Array.IndexOf(operations, "environment.notify");
        var journalReplace = Array.FindIndex(operations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}", StringComparison.Ordinal));
        Assert.True(ledgerReplace >= 0 && ledgerReplace < notify && notify < journalReplace);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, Assert.Single(fixture.LoadChangeSet().Targets).OutcomeCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Notification_delivery_fault_keeps_terminal_lifecycle_pending_and_returns_partial_overlay(bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault("environment.notify", new IOException("sensitive-notify-after"));
        }
        else
        {
            fixture.Platform.InjectFault("environment.notify", new IOException("sensitive-notify-before"));
        }

        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, Assert.Single(durable.Targets).RollbackStatus);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);

        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupLedgerRollbackStatus.Pending,
            Assert.Single(fixture.LoadChangeSet().Targets).RollbackStatus);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Notification_only_missing_private_plan_fails_closed_without_target_read()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations.Skip(operationsBefore));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applied)]
    [InlineData(SetupChangeSetState.Restored)]
    [InlineData(SetupChangeSetState.RolledBack)]
    public void RecoverNext_Actual_apply_terminal_pending_notification_replays_from_artifacts_without_target_io(
        SetupChangeSetState terminalState)
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.ProduceTerminalPending(terminalState);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("post-terminal-file-drift"));
        fixture.Platform.SeedUserEnvironment("ENV_A", "post-terminal-environment-drift");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        var expectedOperation = terminalState == SetupChangeSetState.RolledBack
            ? SetupRecoveryOperation.Rollback
            : SetupRecoveryOperation.Apply;
        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(expectedOperation == SetupRecoveryOperation.Apply
            ? SetupCodes.InterruptedApplyRecovered
            : SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(expectedOperation, result.Operation);
        Assert.Equal(terminalState, result.EffectiveChangeSet?.State);
        Assert.Equal(terminalState, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Equal(1, operations.Count(operation => operation == "environment.notify"));
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
        Assert.Equal("post-terminal-file-drift",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("post-terminal-environment-drift", fixture.Platform.ReadUserEnvironment("ENV_A"));
    }

    public static TheoryData<string> TerminalPendingArtifactFailureCases => new()
    {
        "plan-missing",
        "plan-corrupt",
        "plan-rebound",
        "backup-missing",
        "backup-corrupt",
        "backup-rebound",
    };

    [Theory]
    [MemberData(nameof(TerminalPendingArtifactFailureCases))]
    public void RecoverNext_Actual_apply_terminal_pending_missing_corrupt_or_rebound_artifact_fails_closed(
        string variant)
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.ProduceTerminalPending(SetupChangeSetState.Applied);
        fixture.TamperNotificationArtifact(variant);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        AssertTerminalNotificationFailurePersistence(fixture, SetupChangeSetState.Applied);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
    }

    public static TheoryData<string> TerminalPendingHashMismatchCases => new()
    {
        "journal-prior-hash",
        "journal-desired-hash",
        "plan-base-hash",
        "backup-aggregate-hash",
        "ledger-previous-hash",
        "ledger-applied-hash",
    };

    [Theory]
    [MemberData(nameof(TerminalPendingHashMismatchCases))]
    public void RecoverNext_Actual_apply_terminal_pending_tampered_hash_fails_before_notification(string variant)
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.ProduceTerminalPending(SetupChangeSetState.Applied);
        fixture.TamperNotificationHash(variant);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        AssertTerminalNotificationFailurePersistence(fixture, SetupChangeSetState.Applied);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applied)]
    [InlineData(SetupChangeSetState.Restored)]
    [InlineData(SetupChangeSetState.RolledBack)]
    public void RecoverNext_Terminal_pending_lifecycles_share_environment_hash_gate(
        SetupChangeSetState terminalState)
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.ProduceTerminalPending(terminalState);
        fixture.TamperEnvironmentJournal("prior_state_hash", new string('a', 64));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        AssertTerminalNotificationFailurePersistence(fixture, terminalState);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Restored)]
    [InlineData(SetupChangeSetState.RolledBack)]
    public void RecoverNext_Cleared_ownership_terminal_pending_requires_canonical_backup_reference(
        SetupChangeSetState terminalState)
    {
        var fixture = new ApplyProducedRecoveryFixture();
        fixture.ProduceTerminalPending(terminalState);
        fixture.TamperEnvironmentJournal("backup_reference", "different-backup");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        AssertTerminalNotificationFailurePersistence(fixture, terminalState);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
    }

    [Fact]
    public void RecoverNext_Completed_notification_adds_no_plan_or_backup_requirement()
    {
        var fixture = new ApplyProducedRecoveryFixture();
        using (var applyLock = fixture.AcquireLock())
        {
            Assert.Equal(SetupChangeSetState.Applied,
                fixture.CreateApplyCoordinator().Apply(applyLock, fixture.ChangeSetId).Value.State);
        }
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        fixture.Platform.FileSystem.DeleteFile(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId));
        fixture.Platform.FileSystem.DeleteFile(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.CreateRecoveryCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        AssertNoMixedTargetIo(operations, fixture.TargetPath);
    }

    [Theory]
    [InlineData("ledger-save")]
    [InlineData("completion-marker")]
    public void RecoverNext_Notification_persistence_fault_returns_fixed_failure_and_leaves_marker_pending(string fault)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        var destination = fault == "ledger-save"
            ? fixture.Paths.OwnershipLedger
            : fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fault == "ledger-save"
            ? destination + ".tmp"
            : fixture.NextTemporaryPath(destination, 0);
        fixture.Platform.InjectFault(
            fault == "ledger-save" ? $"file.write:{temporary}" : $"file.write-new:{temporary}",
            new IOException("sensitive-persistence"));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.DoesNotContain("sensitive", result.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Equal(fault == "ledger-save" ? 0 : 1, operations.Count(operation => operation == "environment.notify"));
    }

    [Fact]
    public void RecoverNext_Proves_after_effect_completion_marker_and_returns_recovered()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(destination, 0);
        fixture.Platform.InjectAfterEffectFault(
            $"file.replace:{temporary}->{destination}", new IOException("sensitive-after-marker"));
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Theory]
    [InlineData("extra-no-op-step")]
    [InlineData("missing-changed-step")]
    public void RecoverNext_Rejects_terminal_journal_that_does_not_exactly_match_changed_environment_members(string journalShape)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger(journalShape);
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
    }

    [Theory]
    [InlineData("committed-third-party")]
    [InlineData("restored-third-party")]
    public void RecoverNext_Terminal_file_mismatch_is_preserved_and_persisted_partial(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        if (variant == "committed-third-party")
        {
            fixture.SeedCommittedApplyJournalAndApplyingLedger();
        }
        else
        {
            fixture.SeedRestoredApplyJournalAndCompensatingLedger();
        }

        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party"));
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal("third-party", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
    }

    [Theory]
    [InlineData("missing-plan")]
    [InlineData("plan-identity")]
    [InlineData("unsafe-path")]
    public void RecoverNext_Terminal_plan_or_path_failure_preserves_target_and_persists_partial(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        if (variant == "missing-plan")
        {
            fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        }
        else if (variant == "plan-identity")
        {
            fixture.Platform.SeedFile(
                fixture.Paths.GetPlan(fixture.ChangeSetId),
                SetupPlanStore.Serialize(fixture.Plan with { SelectedTarget = "cli" }));
        }
        else
        {
            fixture.Platform.SeedPathMetadata(
                fixture.TargetPath,
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)?.Phase);
    }

    [Fact]
    public void RecoverNext_Proves_after_effect_terminal_ledger_commit_instead_of_downgrading_truth()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.Platform.InjectAfterEffectFault(
            $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}",
            new IOException("sensitive-after-ledger"));
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupChangeSetState.Applied, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)?.Phase);
    }

    [Theory]
    [InlineData("missing-plan")]
    [InlineData("corrupt-journal")]
    [InlineData("unknown-journal-version")]
    [InlineData("reparse-plan")]
    [InlineData("reparse-journal")]
    public void RecoverNext_Missing_or_unreadable_private_artifact_returns_fixed_sanitized_failure(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedApplyJournalAndBackup();
        if (variant == "missing-plan")
        {
            fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        }
        else if (variant == "reparse-plan")
        {
            fixture.Platform.SeedPathMetadata(
                fixture.Paths.GetPlan(fixture.ChangeSetId),
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        else if (variant == "reparse-journal")
        {
            fixture.Platform.SeedPathMetadata(
                fixture.Paths.GetTransactionJournal(fixture.ChangeSetId),
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        else
        {
            var content = variant == "corrupt-journal"
                ? "{\"path\":\"C:\\\\private\\\\secret\",\"token\":\"credential-value\"}"
                : "{\"schema_version\":2,\"path\":\"C:\\\\private\\\\secret\"}";
            fixture.Platform.SeedFile(
                fixture.Paths.GetTransactionJournal(fixture.ChangeSetId), Encoding.UTF8.GetBytes(content));
        }

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.DoesNotContain("private", result.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", result.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Fact]
    public void RecoverNext_Active_file_apply_with_pending_prior_step_recovers_without_target_write()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedApplyingJournalAndLedger();
        var operationsBefore = fixture.Platform.Operations.Count;

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Restored, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Restored,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)?.Phase);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void RecoverNext_Prepared_file_apply_paired_with_applying_ledger_advances_to_compensation()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedJournalAndApplyingLedger();
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    public static TheoryData<string, string, bool, bool> ActiveFilePhaseStateCases
    {
        get
        {
            var data = new TheoryData<string, string, bool, bool>();
            foreach (var phase in Enum.GetValues<SetupJournalStepPhase>())
            {
                foreach (var state in new[] { "prior", "desired", "third", "missing", "unavailable" })
                {
                    var recovered = phase switch
                    {
                        SetupJournalStepPhase.Pending => state == "prior",
                        SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted or
                            SetupJournalStepPhase.RestoreStarted => state is "prior" or "desired",
                        SetupJournalStepPhase.RestoreCompleted => state == "prior",
                        _ => false,
                    };
                    var physicallyRestored = recovered && state == "desired" &&
                        phase != SetupJournalStepPhase.RestoreCompleted;
                    data.Add(phase.ToString(), state, recovered, physicallyRestored);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ActiveFilePhaseStateCases))]
    public void RecoverNext_Active_file_step_phase_and_current_state_matrix_is_fail_closed(
        string stepPhaseName,
        string currentState,
        bool expectedRecovered,
        bool expectedPhysicalRestore)
    {
        var stepPhase = Enum.Parse<SetupJournalStepPhase>(stepPhaseName);
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(stepPhase, currentState);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(expectedRecovered ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(expectedRecovered ? SetupCodes.InterruptedApplyRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(expectedRecovered ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
            result.EffectiveChangeSet?.State);
        Assert.Equal(expectedRecovered ? SetupJournalPhase.Restored : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        var expectedStepPhase = expectedRecovered && stepPhase != SetupJournalStepPhase.Pending
            ? SetupJournalStepPhase.RestoreCompleted
            : stepPhase;
        Assert.Equal(expectedStepPhase,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(expectedRecovered ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
            fixture.LoadChangeSet().State);
        var targetMutation = fixture.Platform.Operations.Skip(operationsBefore).Any(operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        Assert.Equal(expectedPhysicalRestore, targetMutation);
        if (currentState == "prior" || expectedPhysicalRestore)
        {
            Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        }
        else if (currentState == "desired")
        {
            Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        }
        else if (currentState == "third")
        {
            Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        }
        else if (currentState == "missing")
        {
            Assert.False(fixture.Platform.FileSystem.GetPathMetadata(fixture.TargetPath).Exists);
        }
        else
        {
            Assert.True((fixture.Platform.FileSystem.GetPathMetadata(fixture.TargetPath).Attributes &
                FileAttributes.ReparsePoint) != 0);
        }

        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RecoverNext_Active_file_targets_restore_in_strict_reverse_order(int targetCount)
    {
        var fixture = RecoveryFixture.CreateFileOnly(targetCount);
        fixture.SeedActiveFiles(Enumerable.Repeat("desired", targetCount).ToArray());
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var restoreIndexes = fixture.TargetPaths.Select(targetPath => Array.FindIndex(operations, operation =>
            operation.EndsWith($"->{targetPath}", StringComparison.Ordinal))).ToArray();
        Assert.All(restoreIndexes, index => Assert.True(index >= 0));
        for (var index = 1; index < restoreIndexes.Length; index++)
        {
            Assert.True(restoreIndexes[index - 1] > restoreIndexes[index]);
        }

        Assert.All(fixture.TargetPaths, targetPath =>
            Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(targetPath))));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void RecoverNext_Active_file_partial_retries_from_persisted_restore_intent_after_fault_removal()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}",
            new IOException("private-restore-fault"));

        using (var firstLock = fixture.AcquireLock())
        {
            var first = fixture.ReopenCoordinator().RecoverNext(firstLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, first.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, first.Code);
            Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
            Assert.Equal(SetupJournalStepPhase.RestoreStarted,
                Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
            Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        }

        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, retried.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void RecoverNext_Active_file_targets_restore_in_reverse_and_continue_after_third_party_state()
    {
        var fixture = RecoveryFixture.CreateFileOnly(3);
        fixture.SeedActiveFiles(["desired", "third", "desired"]);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[0])));
        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[1])));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[2])));
        var thirdPartyTarget = fixture.LoadChangeSet().Targets.Single(target =>
            target.RecordId == fixture.FileRecordIds[1]);
        Assert.Equal(SetupCodes.RollbackStale, thirdPartyTarget.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, thirdPartyTarget.RollbackStatus);
        var phases = fixture.LoadJournal().Targets
            .Select(target => Assert.Single(target.Steps).Phase)
            .ToArray();
        Assert.Equal(
            [SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.MutationCompleted,
             SetupJournalStepPhase.RestoreCompleted],
            phases);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var lastTargetRestore = Array.FindIndex(operations, operation =>
            operation.EndsWith($"->{fixture.TargetPaths[2]}", StringComparison.Ordinal));
        var firstTargetRestore = Array.FindIndex(operations, operation =>
            operation.EndsWith($"->{fixture.TargetPaths[0]}", StringComparison.Ordinal));
        Assert.True(lastTargetRestore >= 0 && firstTargetRestore > lastTargetRestore);
        Assert.DoesNotContain(operations, operation =>
            operation.EndsWith($"->{fixture.TargetPaths[1]}", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void RecoverNext_Unjournaled_no_op_file_failure_is_retryable_and_all_rows_are_normalized()
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpFile();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        fixture.Platform.SeedFile(fixture.TargetPaths[1], Encoding.UTF8.GetBytes("third"));

        using (var firstLock = fixture.AcquireLock())
        {
            var first = fixture.ReopenCoordinator().RecoverNext(firstLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, first.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, first.Code);
            Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
            var partial = fixture.LoadChangeSet();
            Assert.Equal(SetupChangeSetState.Partial, partial.State);
            var noOpPartial = partial.Targets.Single(target => target.RecordId == fixture.FileRecordIds[1]);
            Assert.Equal(SetupCodes.RollbackStale, noOpPartial.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, noOpPartial.RollbackStatus);
            Assert.Null(noOpPartial.AppliedStateHash);
            Assert.Null(noOpPartial.BackupReference);
        }

        fixture.Platform.SeedFile(fixture.TargetPaths[1], Encoding.UTF8.GetBytes("old"));
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, retried.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        var restored = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Restored, restored.State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, restored.OutcomeCode);
        Assert.All(restored.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        Assert.Equal(SetupCodes.InterruptedApplyRecovered,
            restored.Targets.Single(target => target.RecordId == fixture.FileRecordId).OutcomeCode);
        Assert.Null(restored.Targets.Single(target => target.RecordId == fixture.FileRecordIds[1]).OutcomeCode);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[1])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LedgerSave_Initial_file_lifecycle_rejects_unowned_failure_status(bool compensating)
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpFile();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        using (var seedLock = fixture.AcquireLock())
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = Assert.Single(ledger.ChangeSets);
            Assert.Throws<FormatException>(() => ledgerStore.Save(seedLock, ledger with
            {
                ChangeSets = [current with
                {
                    State = compensating ? SetupChangeSetState.Compensating : SetupChangeSetState.Applying,
                    Targets = current.Targets.Select(target => target.RecordId == fixture.FileRecordIds[1]
                        ? target with
                        {
                            OutcomeCode = SetupCodes.RollbackStale,
                            RollbackStatus = SetupLedgerRollbackStatus.Stale,
                        }
                        : target).ToArray(),
                }],
            }));
        }

        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable,
            fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.FileRecordIds[1]).RollbackStatus);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[1])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_file_restore_intent_write_is_proven_before_physical_restore(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"file.read:{fixture.TargetPath}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(journalPath, 0);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{journalPath}", new IOException("private-intent-after"));
        }
        else
        {
            fixture.Platform.InjectFault($"file.write-new:{temporary}", new IOException("private-intent-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(afterEffect ? SetupJournalPhase.Restored : SetupJournalPhase.Compensating,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Compensating,
            fixture.LoadChangeSet().State);
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_file_restore_primitive_is_classified_before_or_after_effect(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var temporary = fixture.NextTemporaryPath(fixture.TargetPath, 0);
        var replace = $"file.replace:{temporary}->{fixture.TargetPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(replace, new IOException("private-restore-after"));
        }
        else
        {
            fixture.Platform.InjectFault(replace, new IOException("private-restore-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(afterEffect ? SetupJournalPhase.Restored : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
            fixture.LoadChangeSet().State);
        var target = fixture.LoadChangeSet().Targets.Single(item => item.RecordId == fixture.FileRecordId);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.InternalError,
            target.OutcomeCode);
        Assert.Equal(afterEffect ? SetupLedgerRollbackStatus.NotAvailable : SetupLedgerRollbackStatus.Failed,
            target.RollbackStatus);
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Fact]
    public async Task RecoverNext_Active_file_restore_exception_with_desired_recapture_is_internal_failure()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var temporary = fixture.NextTemporaryPath(fixture.TargetPath, 0);
        fixture.Platform.InjectAfterEffectFault(
            $"file.replace:{temporary}->{fixture.TargetPath}",
            new IOException("private-restore-desired-after"),
            () => fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("new")));

        barrier.Release();
        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        var target = fixture.LoadChangeSet().Targets.Single(item => item.RecordId == fixture.FileRecordId);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal(SetupCodes.InternalError, target.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, target.RollbackStatus);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_file_restore_completion_write_is_proven_before_terminal_state(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(journalPath, 0);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{journalPath}", new IOException("private-completion-after"));
        }
        else
        {
            fixture.Platform.InjectFault($"file.write-new:{temporary}", new IOException("private-completion-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(afterEffect ? SetupJournalPhase.Restored : SetupJournalPhase.Compensating,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Compensating,
            fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Fact]
    public async Task RecoverNext_Active_file_final_verification_race_preserves_third_party_state_as_partial()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var completionBarrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        completionBarrier.WaitUntilReached(cancellation.Token);
        using var verificationBarrier = fixture.Platform.AddBarrier(
            $"file.read:{fixture.TargetPath}", cancellation.Token);
        completionBarrier.Release();
        verificationBarrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third"));
        verificationBarrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_file_terminal_journal_is_durably_ahead_of_ledger(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var completionBarrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        completionBarrier.WaitUntilReached(cancellation.Token);
        using var verificationBarrier = fixture.Platform.AddBarrier(
            $"file.read:{fixture.TargetPath}", cancellation.Token);
        completionBarrier.Release();
        verificationBarrier.WaitUntilReached(cancellation.Token);
        var ledgerReplace = $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(ledgerReplace, new IOException("private-ledger-after"));
        }
        else
        {
            fixture.Platform.InjectFault(ledgerReplace, new IOException("private-ledger-before"));
        }

        verificationBarrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Compensating,
            fixture.LoadChangeSet().State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));

        if (!afterEffect)
        {
            setupLock.Dispose();
            using var retryLock = fixture.AcquireLock();
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal(SetupCodes.InterruptedApplyRecovered, retried.Code);
            Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        }
    }

    [Theory]
    [InlineData("missing-backup")]
    [InlineData("corrupt-backup")]
    [InlineData("reparse-backup")]
    [InlineData("unsafe-target")]
    public void RecoverNext_Active_file_rejects_untrusted_backup_or_path_without_target_write(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId);
        if (variant == "missing-backup")
        {
            fixture.Platform.FileSystem.DeleteFile(backup);
        }
        else if (variant == "corrupt-backup")
        {
            fixture.Platform.SeedFile(backup, Encoding.UTF8.GetBytes("private-backup-corruption"));
        }
        else if (variant == "reparse-backup")
        {
            fixture.Platform.SeedPathMetadata(
                backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        else
        {
            fixture.Platform.SeedPathMetadata(
                fixture.TargetPath, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        Assert.DoesNotContain("private", result.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverNext_Active_file_backup_rebinding_after_read_fails_closed_without_target_write()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier($"file.read:{backup}", cancellation.Token);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedPathMetadata(
            backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Theory]
    [InlineData("plan-ledger-identity")]
    [InlineData("ledger-backup-reference")]
    [InlineData("journal-desired-hash")]
    public void RecoverNext_Active_file_requires_exact_plan_ledger_journal_identity(string mismatch)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        if (mismatch == "plan-ledger-identity")
        {
            fixture.Platform.SeedFile(
                fixture.Paths.GetPlan(fixture.ChangeSetId),
                SetupPlanStore.Serialize(fixture.Plan with { SelectedTarget = "cli" }));
        }
        else if (mismatch == "ledger-backup-reference")
        {
            var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
            var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
            using var mutationLock = fixture.AcquireLock();
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(mutationLock, ledger with
            {
                ChangeSets = [changeSet with
                {
                    Targets = [changeSet.Targets[0] with { BackupReference = "different-reference" }],
                }],
            });
        }
        else
        {
            var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
            var read = fixture.Platform.FileSystem.ReadAtMostBytes(
                journalPath, SetupTransactionJournalStore.MaximumJournalBytes);
            Assert.True(read.IsComplete);
            var json = Encoding.UTF8.GetString(read.Bytes).Replace(
                fixture.DesiredFileHash, new string('a', 64), StringComparison.Ordinal);
            fixture.Platform.SeedFile(journalPath, Encoding.UTF8.GetBytes(json));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Fact]
    public void RecoverNext_Active_file_unproven_journal_read_leaves_ledger_behind_and_requires_recovery()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        fixture.Platform.InjectFault(
            $"file.read-bounded:{journalPath}:{SetupTransactionJournalStore.MaximumJournalBytes}",
            new IOException("private-journal-read"));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(SetupJournalPhase.Applying, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet().State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        Assert.DoesNotContain("private", result.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoverNext_Exact_prepared_rollback_with_applied_ledger_is_dormant()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedRollbackJournalAndAppliedLedger();

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        Assert.Null(result.Code);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.Operation);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Theory]
    [InlineData("prepared", "pending", "applied", true)]
    [InlineData("rolling_back", "pending", "prior", false)]
    [InlineData("rolling_back", "restore_started", "applied", true)]
    [InlineData("rolling_back", "restore_started", "prior", false)]
    [InlineData("rolling_back", "restore_completed", "prior", false)]
    [InlineData("partial", "pending", "applied", true)]
    public void RecoverNext_Active_file_rollback_resumes_idempotently(
        string journalPhase,
        string stepPhase,
        string currentState,
        bool expectsPhysicalRestore)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback(journalPhase, stepPhase, currentState);
        var operationsBefore = fixture.Platform.Operations.Count;
        SetupRecoveryResult result;
        using (var setupLock = fixture.AcquireLock())
        {
            result = fixture.ReopenCoordinator().RecoverNext(setupLock);
        }

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        var target = Assert.Single(durable.Targets);
        Assert.Null(target.AppliedStateHash);
        Assert.Null(target.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Equal(expectsPhysicalRestore, operations.Any(operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}"));
        Assert.DoesNotContain("environment.notify", operations);
        var journalDestination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var journalCommit = Array.FindLastIndex(operations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{journalDestination}", StringComparison.Ordinal));
        var ledgerCommit = Array.FindLastIndex(operations, operation =>
            operation == $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}");
        Assert.True(journalCommit >= 0 && journalCommit < ledgerCommit);

        using var terminalLock = fixture.AcquireLock();
        var terminal = fixture.ReopenCoordinator().RecoverNext(terminalLock);
        Assert.Equal(SetupRecoveryDisposition.None, terminal.Disposition);
    }

    [Theory]
    [InlineData("third", SetupCodes.RollbackStale, "stale")]
    [InlineData("unavailable", SetupCodes.InternalError, "failed")]
    public void RecoverNext_Active_file_rollback_preserves_failed_target_and_continues_reverse_order(
        string failedState,
        string targetCode,
        string rollbackStatus)
    {
        var fixture = RecoveryFixture.CreateFileOnly(3);
        fixture.SeedActiveFileRollbacks(["applied", "applied", failedState]);
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
        }

        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        var partial = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, partial.State);
        Assert.Equal(targetCode, partial.Targets[2].OutcomeCode);
        Assert.Equal(
            rollbackStatus == "stale" ? SetupLedgerRollbackStatus.Stale : SetupLedgerRollbackStatus.Failed,
            partial.Targets[2].RollbackStatus);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[0])));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[1])));
        Assert.Equal(failedState == "third" ? "third" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[2])));
        var restores = fixture.Platform.Operations.Skip(operationsBefore)
            .Where(operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                fixture.TargetPaths.Any(path => operation.EndsWith($"->{path}", StringComparison.Ordinal)))
            .ToArray();
        Assert.EndsWith($"->{fixture.TargetPaths[1]}", restores[0], StringComparison.Ordinal);
        Assert.EndsWith($"->{fixture.TargetPaths[0]}", restores[1], StringComparison.Ordinal);
        var recoveryOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var partialJournal = Array.FindLastIndex(recoveryOperations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}", StringComparison.Ordinal));
        var partialLedger = Array.FindLastIndex(recoveryOperations, operation =>
            operation == $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}");
        Assert.True(partialJournal >= 0 && partialJournal < partialLedger);

        fixture.Platform.SeedPathMetadata(
            fixture.TargetPaths[2],
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal));
        fixture.Platform.SeedFile(fixture.TargetPaths[2], Encoding.UTF8.GetBytes("old"));
        using var retryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
    }

    [Fact]
    public void RecoverNext_Committed_file_rollback_reconciles_terminal_journal_before_ledger()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "prior");
        fixture.CommitRollbackJournalOnly();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Fact]
    public void RecoverNext_File_rollback_final_verification_includes_unjournaled_no_op_and_retries_after_external_resolution()
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpFile();
        fixture.SeedFileRollbackWithUnjournaledNoOp("third");
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
        }

        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        var partial = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, partial.State);
        Assert.Equal(SetupCodes.RollbackStale, partial.Targets[1].OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, partial.Targets[1].RollbackStatus);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[0])));
        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPaths[1])));

        fixture.Platform.SeedFile(fixture.TargetPaths[1], Encoding.UTF8.GetBytes("old"));
        using var retryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, durable.Targets[0].RollbackStatus);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, durable.Targets[1].RollbackStatus);
    }

    [Fact]
    public void RecoverNext_File_rollback_with_unjournaled_no_op_environment_uses_guard_without_environment_write()
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpEnvironment();
        fixture.SeedFileRollbackWithUnjournaledEnvironment();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Contains("environment.get:ENV_NOOP", fixture.Platform.Operations.Skip(operationsBefore));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded,
            durable.Targets.Single(target => target.TargetKind == SetupTargetKind.Json).RollbackStatus);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable,
            durable.Targets.Single(target => target.TargetKind == SetupTargetKind.Env).RollbackStatus);
    }

    [Theory]
    [InlineData("missing-backup")]
    [InlineData("corrupt-backup")]
    [InlineData("reparse-backup")]
    [InlineData("reparse-target")]
    [InlineData("ledger-backup-rebound")]
    [InlineData("journal-hash-rebound")]
    public void RecoverNext_File_rollback_rejects_nonexact_or_unsafe_evidence_without_overwrite(string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "applied");
        fixture.RebindRollbackEvidence(variant);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        Assert.DoesNotContain("private", result.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_File_rollback_restore_fault_recaptures_effect_before_classification(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "applied");
        var temporary = fixture.NextTemporaryPath(fixture.TargetPath, 1);
        var operation = $"file.replace:{temporary}->{fixture.TargetPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_RESTORE"));
        }
        else
        {
            fixture.Platform.InjectFault(operation, new IOException("PRIVATE_RESTORE"));
        }
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.True(fixture.Platform.Operations.Contains(operation),
            string.Join(Environment.NewLine, fixture.Platform.Operations));
        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(afterEffect ? SetupJournalPhase.Committed : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        if (!afterEffect)
        {
            var target = Assert.Single(fixture.LoadChangeSet().Targets);
            Assert.Equal(SetupCodes.InternalError, target.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.Failed, target.RollbackStatus);
        }
        Assert.DoesNotContain("PRIVATE", result.Code!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("completion", false)]
    [InlineData("completion", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    public void RecoverNext_File_rollback_journal_boundary_fault_requires_proof_before_advancing(
        string boundary,
        bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback(
            "rolling_back",
            boundary == "intent" ? "pending" : "restore_started",
            boundary == "intent" ? "applied" : "prior");
        if (boundary == "commit")
        {
            fixture.CompleteRollbackStepOnly();
        }

        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(journalPath, 0);
        var operation = $"file.replace:{temporary}->{journalPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_JOURNAL"));
        }
        else
        {
            fixture.Platform.InjectFault(operation, new IOException("PRIVATE_JOURNAL"));
        }
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Contains(operation, fixture.Platform.Operations);
        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.RecoveryRequired,
            result.Code);
        Assert.Equal(afterEffect ? SetupJournalPhase.Committed : SetupJournalPhase.RollingBack,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.RolledBack : SetupChangeSetState.RollingBack,
            fixture.LoadChangeSet().State);
        Assert.Equal(boundary == "intent" && !afterEffect ? "new" : "old",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_File_rollback_terminal_ledger_fault_reconciles_only_proven_write(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "prior");
        var operation = $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_LEDGER"));
        }
        else
        {
            fixture.Platform.InjectFault(operation, new IOException("PRIVATE_LEDGER"));
        }
        using (var setupLock = fixture.AcquireLock())
        {
            var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
                result.Disposition);
            Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.RecoveryRequired,
                result.Code);
        }

        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.RolledBack : SetupChangeSetState.RollingBack,
            fixture.LoadChangeSet().State);
        if (!afterEffect)
        {
            using var retryLock = fixture.AcquireLock();
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        }
    }

    [Fact]
    public void RecoverNext_Dormant_file_rollback_rejects_rebound_applied_hash()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedRollbackJournalAndAppliedLedger();
        fixture.RebindRollbackLedgerAppliedHash();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Fact]
    public void RecoverNext_File_rollback_restore_exception_with_third_party_recapture_is_stale()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "applied");
        var temporary = fixture.NextTemporaryPath(fixture.TargetPath, 1);
        fixture.Platform.InjectAfterEffectFault(
            $"file.replace:{temporary}->{fixture.TargetPath}",
            new IOException("PRIVATE_RESTORE"),
            () => fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third")));
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var target = Assert.Single(fixture.LoadChangeSet().Targets);
        Assert.Equal(SetupCodes.RollbackStale, target.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, target.RollbackStatus);
    }

    [Fact]
    public async Task RecoverNext_File_rollback_final_verification_preserves_race_after_step_completion()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "applied");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterCompletionBeforeCommit}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(
            () => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third"));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, Assert.Single(fixture.LoadChangeSet().Targets).RollbackStatus);
    }

    [Fact]
    public void RecoverNext_File_rollback_restores_missing_previous_state_by_deleting_applied_file()
    {
        var fixture = RecoveryFixture.CreateNewFile();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "applied");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.False(fixture.Platform.FileSystem.GetPathMetadata(fixture.TargetPath).Exists);
        Assert.Contains($"file.delete:{fixture.TargetPath}",
            fixture.Platform.Operations.Skip(operationsBefore));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void RecoverNext_File_rollback_preserves_unexpected_missing_current_state()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "pending", "missing");
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.False(fixture.Platform.FileSystem.GetPathMetadata(fixture.TargetPath).Exists);
        var target = Assert.Single(fixture.LoadChangeSet().Targets);
        Assert.Equal(SetupCodes.RollbackStale, target.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, target.RollbackStatus);
    }

    [Fact]
    public async Task RecoverNext_File_rollback_partial_retry_survives_commit_to_ledger_crash_window()
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpFile();
        fixture.SeedFileRollbackWithUnjournaledNoOp("third");
        using (var initialLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(initialLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
        }

        fixture.Platform.SeedFile(fixture.TargetPaths[1], Encoding.UTF8.GetBytes("old"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterCompletionBeforeCommit}", cancellation.Token);
        using (var setupLock = fixture.AcquireLock())
        {
            var recovery = Task.Run(
                () => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
            barrier.WaitUntilReached(cancellation.Token);
            fixture.Platform.InjectFault(
                $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}",
                new IOException("PRIVATE_LEDGER"));
            barrier.Release();
            var interrupted = await recovery;
            Assert.Equal(SetupRecoveryDisposition.Failed, interrupted.Disposition);
            Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        }

        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        var lagging = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RollingBack, lagging.State);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, lagging.Targets[0].RollbackStatus);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, lagging.Targets[1].RollbackStatus);

        using var retryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void RecoverNext_File_rollback_restarts_completed_restore_when_exact_applied_state_returns()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "applied");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var redoIntent = Array.FindIndex(operations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}", StringComparison.Ordinal));
        var restore = Array.FindIndex(operations, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        Assert.True(redoIntent >= 0 && redoIntent < restore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_File_rollback_completed_restore_redo_intent_requires_durable_proof(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "applied");
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(journalPath, 0);
        var operation = $"file.replace:{temporary}->{journalPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_REDO_INTENT"));
        }
        else
        {
            fixture.Platform.InjectFault(operation, new IOException("PRIVATE_REDO_INTENT"));
        }
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Contains(operation, fixture.Platform.Operations);
        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.RecoveryRequired,
            result.Code);
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(afterEffect ? SetupJournalPhase.Committed : SetupJournalPhase.RollingBack,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.RolledBack : SetupChangeSetState.RollingBack,
            fixture.LoadChangeSet().State);
        if (!afterEffect)
        {
            Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
                Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
            Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), item =>
                item == $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}" ||
                item.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
                item == $"file.delete:{fixture.TargetPath}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_File_rollback_completed_restore_redo_recaptures_primitive_effect(bool afterEffect)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "applied");
        var temporary = fixture.NextTemporaryPath(fixture.TargetPath, 1);
        var operation = $"file.replace:{temporary}->{fixture.TargetPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_REDO_RESTORE"));
        }
        else
        {
            fixture.Platform.InjectFault(operation, new IOException("PRIVATE_REDO_RESTORE"));
        }
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Contains(operation, fixture.Platform.Operations);
        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(afterEffect ? SetupJournalPhase.Committed : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupLedgerRollbackStatus.Succeeded : SetupLedgerRollbackStatus.Failed,
            Assert.Single(fixture.LoadChangeSet().Targets).RollbackStatus);
    }

    [Theory]
    [InlineData("third", SetupCodes.RollbackStale, "stale", "third", false)]
    [InlineData("unavailable", SetupCodes.InternalError, "failed", "new", true)]
    public void RecoverNext_File_rollback_restore_completed_preserves_third_party_or_unavailable_target(
        string currentState,
        string targetCode,
        string rollbackStatus,
        string expectedContent,
        bool expectedReparsePoint)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", currentState);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(journal.Targets).Steps).Phase);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        var target = Assert.Single(durable.Targets);
        Assert.Equal(fixture.DesiredFileHash, target.AppliedStateHash);
        Assert.Equal(fixture.FileRecordId.ToString("D"), target.BackupReference);
        Assert.Equal(targetCode, target.OutcomeCode);
        Assert.Equal(
            rollbackStatus == "stale" ? SetupLedgerRollbackStatus.Stale : SetupLedgerRollbackStatus.Failed,
            target.RollbackStatus);
        Assert.Equal(expectedContent,
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var metadata = fixture.Platform.FileSystem.GetPathMetadata(fixture.TargetPath);
        Assert.Equal(expectedReparsePoint,
            (metadata.Attributes & FileAttributes.ReparsePoint) != 0);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Theory]
    [InlineData("overall-outcome")]
    [InlineData("target-outcome")]
    [InlineData("rollback-status")]
    public void RecoverNext_Committed_file_rollback_rejects_noncanonical_recovery_produced_partial(
        string variant)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "prior");
        fixture.CommitRollbackJournalOnly();
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third"));
        using (var baselineLock = fixture.AcquireLock())
        {
            var baselineResult = fixture.ReopenCoordinator().RecoverNext(baselineLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, baselineResult.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, baselineResult.Code);
        }

        var baseline = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, baseline.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, baseline.OutcomeCode);
        var baselineTarget = Assert.Single(baseline.Targets);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, baselineTarget.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, baselineTarget.RollbackStatus);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("old"));
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        using (var mutationLock = fixture.AcquireLock())
        {
            var ledger = ledgerStore.LoadForRecovery();
            var malformed = variant switch
            {
                "overall-outcome" => baseline with { OutcomeCode = SetupCodes.ApplySucceeded },
                "target-outcome" => baseline with
                {
                    Targets = [baselineTarget with { OutcomeCode = SetupCodes.ApplySucceeded }],
                },
                "rollback-status" => baseline with
                {
                    Targets = [baselineTarget with { RollbackStatus = SetupLedgerRollbackStatus.Pending }],
                },
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
            ledgerStore.Save(mutationLock, ledger with { ChangeSets = [malformed] });
        }

        var persistedMutation = fixture.LoadChangeSet();
        var persistedMutationTarget = Assert.Single(persistedMutation.Targets);
        Assert.Equal(
            variant == "overall-outcome" ? SetupCodes.ApplySucceeded : SetupCodes.InterruptedRecoveryFailed,
            persistedMutation.OutcomeCode);
        Assert.Equal(
            variant == "target-outcome" ? SetupCodes.ApplySucceeded : SetupCodes.InterruptedRecoveryFailed,
            persistedMutationTarget.OutcomeCode);
        Assert.Equal(
            variant == "rollback-status" ? SetupLedgerRollbackStatus.Pending :
                SetupLedgerRollbackStatus.NotAvailable,
            persistedMutationTarget.RollbackStatus);

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.NotEqual(SetupChangeSetState.RolledBack, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        var durableTarget = Assert.Single(durable.Targets);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durableTarget.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, durableTarget.RollbackStatus);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Fact]
    public void RecoverNext_Committed_file_rollback_partial_remains_oldest_until_previous_state_is_restored()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedActiveFileRollback("rolling_back", "restore_completed", "prior");
        fixture.CommitRollbackJournalOnly();
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third"));
        var later = fixture.AddFileChangeSet(
            Guid.Parse("00000000-0000-7000-8000-000000000711"),
            Guid.Parse("00000000-0000-7000-8000-000000000712"),
            fixture.Plan.CreatedAt.AddMinutes(1),
            "later-committed-partial");
        fixture.SeedCommittedApplyJournalAndApplyingLedger(later);
        var operationsBefore = fixture.Platform.Operations.Count;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var setupLock = fixture.AcquireLock();
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(fixture.ChangeSetId, failed.RecoveredChangeSetId);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet(fixture.ChangeSetId).State);
            Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
        }

        Assert.Equal("third", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");

        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("old"));
        using var recoveryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(recoveryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Equal(fixture.ChangeSetId, recovered.RecoveredChangeSetId);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet(fixture.ChangeSetId).State);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Fact]
    public void RecoverNext_Exact_prepared_environment_rollback_with_applied_ledger_is_dormant()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedPreparedRollbackJournalAndAppliedLedger();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.None, result.Disposition);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Prepared_environment_rollback_rejects_no_op_journal_step_without_member_write()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedPreparedRollbackJournalAndAppliedLedger(includeNoOpStep: true);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LedgerSave_AllNoOpTargetWithForgedOwnershipFailsClosedBeforeRecovery(
        bool active,
        bool bindAppliedToBase)
    {
        var fixture = new EnvironmentRecoveryFixture();
        var operationsBefore = fixture.Platform.Operations.Count;

        var exception = Assert.Throws<FormatException>(() =>
            fixture.SeedForgedNoOpRollback(active, bindAppliedToBase));

        Assert.Null(exception.InnerException);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_pending_prior_completes_without_member_write_then_notifies()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "prior");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain("environment.set:ENV_A", fixture.Platform.Operations.Skip(operationsBefore));
        Assert.Equal(1, fixture.Platform.Operations.Skip(operationsBefore)
            .Count(operation => operation == "environment.notify"));
        var durableTarget = Assert.Single(fixture.LoadChangeSet().Targets);
        Assert.Null(durableTarget.AppliedStateHash);
        Assert.Null(durableTarget.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, durableTarget.RollbackStatus);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var journalDestination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var firstTerminalLedgerWrite = Array.FindIndex(operations, operation =>
            operation == $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}");
        var notification = Array.IndexOf(operations, "environment.notify");
        var journalCommit = Array.FindLastIndex(operations, firstTerminalLedgerWrite - 1, operation =>
            operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
            operation.EndsWith($"->{journalDestination}", StringComparison.Ordinal));
        Assert.True(journalCommit >= 0 && journalCommit < firstTerminalLedgerWrite &&
            firstTerminalLedgerWrite < notification);
    }

    public static TheoryData<string, string> ActiveEnvironmentRollbackPhaseStateCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var phase in new[]
                     {
                         SetupJournalStepPhase.Pending,
                         SetupJournalStepPhase.RestoreStarted,
                         SetupJournalStepPhase.RestoreCompleted,
                     })
            {
                foreach (var state in new[] { "prior", "applied", "third", "unavailable" })
                {
                    data.Add(phase.ToString(), state);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ActiveEnvironmentRollbackPhaseStateCases))]
    public void RecoverNext_Active_environment_rollback_phase_and_state_matrix_is_fail_closed(
        string stepPhaseName,
        string currentState)
    {
        var phase = Enum.Parse<SetupJournalStepPhase>(stepPhaseName);
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(phase, currentState);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        var recovered = currentState is "prior" or "applied";
        Assert.Equal(recovered ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(recovered ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(recovered ? SetupChangeSetState.RolledBack : SetupChangeSetState.Partial,
            result.EffectiveChangeSet?.State);
        Assert.Equal(recovered ? SetupJournalPhase.Committed : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(recovered ? SetupJournalStepPhase.RestoreCompleted : phase,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(recovered ? SetupEnvironmentNotification.Completed
                : phase == SetupJournalStepPhase.Pending
                    ? SetupEnvironmentNotification.NotRequired
                    : SetupEnvironmentNotification.Pending,
            fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal(currentState == "applied",
            fixture.Platform.Operations.Skip(operationsBefore).Contains("environment.set:ENV_A"));
        Assert.Equal(recovered ? "old-a" : currentState == "third" ? "third-a" : "old-a",
            fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(recovered ? 1 : 0,
            fixture.Platform.Operations.Skip(operationsBefore).Count(operation => operation == "environment.notify"));
    }

    [Theory]
    [InlineData("missing", "empty")]
    [InlineData("missing", "value")]
    [InlineData("empty", "missing")]
    [InlineData("empty", "value")]
    [InlineData("value", "missing")]
    [InlineData("value", "empty")]
    public void RecoverNext_Active_environment_rollback_distinguishes_missing_empty_and_value(
        string priorState,
        string desiredState)
    {
        var fixture = new EnvironmentRecoveryFixture(priorState, desiredState);
        fixture.SeedActiveRollback(SetupJournalStepPhase.RestoreStarted, "applied");
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(fixture.PriorValue, fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_restores_changed_members_in_reverse_order()
    {
        var fixture = new EnvironmentRecoveryFixture(secondMemberChanged: true);
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var restoreB = Array.IndexOf(operations, "environment.set:ENV_B");
        var restoreA = Array.IndexOf(operations, "environment.set:ENV_A");
        var notification = Array.IndexOf(operations, "environment.notify");
        Assert.True(restoreB >= 0 && restoreB < restoreA && restoreA < notification);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_preserves_third_party_member_then_retries_after_resolution()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "third");
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
            Assert.Equal("third-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
            Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        }

        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
        fixture.Platform.SeedUserEnvironment("ENV_A", "old-a");
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_never_restores_no_op_member_and_retries_full_guard()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        fixture.Platform.SeedUserEnvironment("ENV_B", "third-b");
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
            Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal("third-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        }

        Assert.DoesNotContain("environment.set:ENV_B", fixture.Platform.Operations.Skip(operationsBefore));
        fixture.Platform.SeedUserEnvironment("ENV_B", "stable-b");
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain("environment.set:ENV_B", fixture.Platform.Operations.Skip(operationsBefore));
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_rejects_full_backup_mismatch_before_member_write()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        fixture.ReplaceRollbackBackupWithCurrentState();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_rejects_applied_aggregate_mismatch_before_member_write()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        fixture.ReplaceRollbackAppliedHash(new string('a', 64));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Active_environment_rollback_rejects_ledger_base_mismatch_before_member_write()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        fixture.ReplaceRollbackPreviousHash(new string('b', 64));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Environment_rollback_completed_restore_proves_fresh_intent_before_member_write(
        bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.RestoreCompleted, "applied");
        var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(destination, 0);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{destination}", new IOException("private-env-redo-intent-after"));
        }
        else
        {
            fixture.Platform.InjectFault(
                $"file.write-new:{temporary}", new IOException("private-env-redo-intent-before"));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.RecoveryRequired,
            result.Code);
        Assert.Equal(afterEffect, fixture.Platform.Operations.Skip(operationsBefore)
            .Contains("environment.set:ENV_A"));
        Assert.Equal(afterEffect ? "old-a" : "desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(afterEffect ? SetupJournalPhase.Committed : SetupJournalPhase.RollingBack,
            fixture.LoadJournal().Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
    }

    [Fact]
    public void RecoverNext_Environment_rollback_notification_failure_retries_notification_only()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveRollback(SetupJournalStepPhase.Pending, "applied");
        fixture.Platform.InjectFault("environment.notify", new IOException("private-env-rollback-notification"));
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        var operationsBeforeRetry = fixture.Platform.Operations.Count;
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, retried.Code);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBeforeRetry), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoverNext_Active_environment_pending_prior_recovers_without_member_write_or_notification()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedApplyingJournalAndLedger();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Restored, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    public static TheoryData<string, string, bool, bool> ActiveEnvironmentPhaseStateCases
    {
        get
        {
            var data = new TheoryData<string, string, bool, bool>();
            foreach (var phase in Enum.GetValues<SetupJournalStepPhase>())
            {
                foreach (var state in new[] { "prior", "desired", "third", "unavailable" })
                {
                    var recovered = phase switch
                    {
                        SetupJournalStepPhase.Pending => state == "prior",
                        SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted or
                            SetupJournalStepPhase.RestoreStarted => state is "prior" or "desired",
                        SetupJournalStepPhase.RestoreCompleted => state == "prior",
                        _ => false,
                    };
                    var physicallyRestored = recovered && state == "desired" &&
                        phase != SetupJournalStepPhase.RestoreCompleted;
                    data.Add(phase.ToString(), state, recovered, physicallyRestored);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ActiveEnvironmentPhaseStateCases))]
    public void RecoverNext_Active_environment_phase_and_state_matrix_is_fail_closed(
        string stepPhaseName,
        string currentState,
        bool expectedRecovered,
        bool expectedPhysicalRestore)
    {
        var phase = Enum.Parse<SetupJournalStepPhase>(stepPhaseName);
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(phase, currentState);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(expectedRecovered ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(expectedRecovered ? SetupCodes.InterruptedApplyRecovered : SetupCodes.InterruptedRecoveryFailed,
            result.Code);
        Assert.Equal(expectedRecovered ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
            result.EffectiveChangeSet?.State);
        Assert.Equal(expectedRecovered ? SetupJournalPhase.Restored : SetupJournalPhase.Partial,
            fixture.LoadJournal().Phase);
        Assert.Equal(expectedRecovered && phase != SetupJournalStepPhase.Pending
                ? SetupEnvironmentNotification.Completed
                : phase == SetupJournalStepPhase.Pending
                    ? SetupEnvironmentNotification.NotRequired
                    : SetupEnvironmentNotification.Pending,
            fixture.LoadJournal().EnvironmentNotification);
        var expectedPhase = expectedRecovered && phase != SetupJournalStepPhase.Pending
            ? SetupJournalStepPhase.RestoreCompleted
            : phase;
        Assert.Equal(expectedPhase, Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(expectedPhysicalRestore,
            fixture.Platform.Operations.Skip(operationsBefore).Contains("environment.set:ENV_A"));
        Assert.Equal(expectedRecovered ? "old-a" : currentState switch
        {
            "prior" => "old-a",
            "desired" => "desired-a",
            "third" => "third-a",
            _ => "old-a",
        }, fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(expectedRecovered && phase != SetupJournalStepPhase.Pending ? 1 : 0,
            fixture.Platform.Operations.Skip(operationsBefore).Count(operation => operation == "environment.notify"));
    }

    [Theory]
    [InlineData("missing", "empty")]
    [InlineData("missing", "value")]
    [InlineData("empty", "missing")]
    [InlineData("empty", "value")]
    [InlineData("value", "missing")]
    [InlineData("value", "empty")]
    public void RecoverNext_Active_environment_distinguishes_missing_empty_and_value_states(
        string priorState,
        string desiredState)
    {
        foreach (var currentState in new[] { "prior", "desired", "third" })
        {
            var fixture = new EnvironmentRecoveryFixture(priorState, desiredState);
            fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, currentState);
            using var setupLock = fixture.AcquireLock();

            var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

            var recovered = currentState != "third";
            Assert.Equal(recovered ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
                result.Disposition);
            Assert.Equal(recovered ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
                result.EffectiveChangeSet?.State);
            Assert.Equal(recovered ? fixture.PriorValue : "third-a",
                fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal(recovered ? SetupEnvironmentNotification.Completed : SetupEnvironmentNotification.Pending,
                fixture.LoadJournal().EnvironmentNotification);
        }
    }

    [Fact]
    public void RecoverNext_Mixed_changed_targets_restore_in_reverse_target_and_member_order_then_notify()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedApply();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupChangeSetState.Restored, result.EffectiveChangeSet?.State);
        Assert.Equal("old-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var fileRestore = Array.FindIndex(operations, operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        var environmentBRestore = Array.IndexOf(operations, "environment.set:ENV_B");
        var environmentARestore = Array.IndexOf(operations, "environment.set:ENV_A");
        var notification = Array.IndexOf(operations, "environment.notify");
        Assert.True(fileRestore >= 0 && environmentBRestore > fileRestore &&
            environmentARestore > environmentBRestore && notification > environmentARestore);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Mixed_changed_targets_continue_reverse_recovery_around_third_party_member()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedApply();
        fixture.Platform.SeedUserEnvironment("ENV_B", "third-b");
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal("old-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Contains(operations, operation => operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        Assert.Contains("environment.set:ENV_A", operations);
        Assert.DoesNotContain("environment.set:ENV_B", operations);
        Assert.DoesNotContain("environment.notify", operations);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Mixed_rollback_restores_reverse_target_and_member_order_then_notifies_last()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, result.EffectiveChangeSet?.State);
        Assert.Equal("old-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var fileRestore = Array.FindIndex(operations, operation =>
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        var environmentBRestore = Array.IndexOf(operations, "environment.set:ENV_B");
        var environmentARestore = Array.IndexOf(operations, "environment.set:ENV_A");
        var terminalLedger = Array.FindLastIndex(operations, operation =>
            operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        var notification = Array.IndexOf(operations, "environment.notify");
        var terminalJournal = Array.FindLastIndex(
            operations,
            notification - 1,
            operation => operation.EndsWith(
                $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                StringComparison.Ordinal));
        Assert.True(fileRestore >= 0 && environmentBRestore > fileRestore &&
            environmentARestore > environmentBRestore && terminalJournal > environmentARestore &&
            terminalLedger > terminalJournal &&
            notification > terminalLedger);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.All(fixture.LoadChangeSet().Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Mixed_rollback_notification_delivery_ambiguity_replays_notification_only(
        bool afterEffect)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                "environment.notify", new IOException("PRIVATE_MIXED_ROLLBACK_NOTIFY_AFTER"));
        }
        else
        {
            fixture.Platform.InjectFault(
                "environment.notify", new IOException("PRIVATE_MIXED_ROLLBACK_NOTIFY_BEFORE"));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
            Assert.DoesNotContain("PRIVATE", failed.ToString(), StringComparison.Ordinal);
        }

        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, target.OutcomeCode);
        });
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
        var firstOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        var notification = Array.IndexOf(firstOperations, "environment.notify");
        var terminalJournal = Array.FindLastIndex(
            firstOperations,
            notification - 1,
            operation => operation.EndsWith(
                $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                StringComparison.Ordinal));
        var terminalLedger = Array.FindLastIndex(
            firstOperations,
            notification - 1,
            operation => operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        Assert.True(terminalJournal >= 0 && terminalLedger > terminalJournal && notification > terminalLedger);

        var operationsBeforeRetry = fixture.Platform.Operations.Count;
        using (var retryLock = fixture.AcquireLock())
        {
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal(SetupCodes.InterruptedRollbackRecovered, retried.Code);
            Assert.Equal(SetupRecoveryOperation.Rollback, retried.Operation);
            Assert.Equal(SetupChangeSetState.RolledBack, retried.EffectiveChangeSet?.State);
        }

        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        var retryOperations = fixture.Platform.Operations.Skip(operationsBeforeRetry).ToArray();
        Assert.Equal(1, retryOperations.Count(operation => operation == "environment.notify"));
        AssertNoMixedTargetIo(retryOperations, fixture.TargetPath);

        var operationsBeforeTerminal = fixture.Platform.Operations.Count;
        using var terminalLock = fixture.AcquireLock();
        var terminal = fixture.ReopenCoordinator().RecoverNext(terminalLock);
        Assert.Equal(SetupRecoveryDisposition.None, terminal.Disposition);
        var terminalOperations = fixture.Platform.Operations.Skip(operationsBeforeTerminal).ToArray();
        Assert.DoesNotContain("environment.notify", terminalOperations);
        AssertNoMixedTargetIo(terminalOperations, fixture.TargetPath);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public async Task RecoverNext_Mixed_rollback_notification_marker_ambiguity_is_replayable(
        string boundary,
        bool afterEffect)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var notificationBarrier = fixture.Platform.AddBarrier("environment.notify", cancellation.Token);
        SetupRecoveryResult first;
        string markerOperation;
        using (var setupLock = fixture.AcquireLock())
        {
            var recovery = Task.Run(
                () => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
            notificationBarrier.WaitUntilReached(cancellation.Token);

            Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
            Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
            Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
            var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
            var temporary = fixture.NextTemporaryPath(destination, 0);
            markerOperation = boundary switch
            {
                "write" => $"file.write-new:{temporary}",
                "flush" => $"file.flush:{temporary}",
                "replace" => $"file.replace:{temporary}->{destination}",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            };
            if (afterEffect)
            {
                fixture.Platform.InjectAfterEffectFault(
                    markerOperation, new IOException("PRIVATE_MIXED_ROLLBACK_MARKER_AFTER"));
            }
            else
            {
                fixture.Platform.InjectFault(
                    markerOperation, new IOException("PRIVATE_MIXED_ROLLBACK_MARKER_BEFORE"));
            }

            notificationBarrier.Release();
            first = await recovery;
        }

        var durableMarkerCompleted = boundary == "replace" && afterEffect;
        Assert.Contains(markerOperation, fixture.Platform.Operations);
        Assert.Equal(
            durableMarkerCompleted ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            first.Disposition);
        Assert.Equal(
            durableMarkerCompleted ? SetupCodes.InterruptedRollbackRecovered : SetupCodes.InterruptedRecoveryFailed,
            first.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, first.Operation);
        Assert.Equal(
            durableMarkerCompleted ? SetupChangeSetState.RolledBack : SetupChangeSetState.Partial,
            first.EffectiveChangeSet?.State);
        Assert.DoesNotContain("PRIVATE", first.ToString(), StringComparison.Ordinal);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.All(durable.Targets, target =>
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus));
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(
            durableMarkerCompleted ? SetupEnvironmentNotification.Completed : SetupEnvironmentNotification.Pending,
            fixture.LoadJournal().EnvironmentNotification);
        var firstOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Equal(1, firstOperations.Count(operation => operation == "environment.notify"));

        if (!durableMarkerCompleted)
        {
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
            var operationsBeforeRetry = fixture.Platform.Operations.Count;
            using var retryLock = fixture.AcquireLock();
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal(SetupCodes.InterruptedRollbackRecovered, retried.Code);
            var retryOperations = fixture.Platform.Operations.Skip(operationsBeforeRetry).ToArray();
            Assert.Equal(1, retryOperations.Count(operation => operation == "environment.notify"));
            AssertNoMixedTargetIo(retryOperations, fixture.TargetPath);
        }

        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        var operationsBeforeTerminal = fixture.Platform.Operations.Count;
        using var terminalLock = fixture.AcquireLock();
        var terminal = fixture.ReopenCoordinator().RecoverNext(terminalLock);
        Assert.Equal(SetupRecoveryDisposition.None, terminal.Disposition);
        var terminalOperations = fixture.Platform.Operations.Skip(operationsBeforeTerminal).ToArray();
        Assert.DoesNotContain("environment.notify", terminalOperations);
        AssertNoMixedTargetIo(terminalOperations, fixture.TargetPath);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("file")]
    public void RecoverNext_Mixed_rollback_continues_safe_restores_around_third_party_then_retries(
        string thirdPartyTarget)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        fixture.SetMixedRollbackThirdParty(thirdPartyTarget);
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        Assert.Equal(thirdPartyTarget == "file" ? "third-file" : "old-file",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(thirdPartyTarget == "environment" ? "third-b" : "old-b",
            fixture.Platform.ReadUserEnvironment("ENV_B"));
        var failedOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.Contains("environment.set:ENV_A", failedOperations);
        if (thirdPartyTarget == "environment")
        {
            Assert.Contains(failedOperations, operation =>
                operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
            Assert.DoesNotContain("environment.set:ENV_B", failedOperations);
        }
        else
        {
            Assert.Contains("environment.set:ENV_B", failedOperations);
            Assert.DoesNotContain(failedOperations, operation =>
                operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
        }

        Assert.DoesNotContain("environment.notify", failedOperations);
        var journalDestination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var partialJournalWrite = Array.FindLastIndex(failedOperations, operation =>
            operation.EndsWith($"->{journalDestination}", StringComparison.Ordinal));
        var partialLedgerWrite = Array.FindLastIndex(failedOperations, operation =>
            operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        Assert.True(partialJournalWrite >= 0 && partialJournalWrite < partialLedgerWrite);

        fixture.ResolveMixedRollbackThirdParty(thirdPartyTarget);
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public void RecoverNext_Mixed_rollback_never_restores_no_op_member_and_retries_full_guard()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-noop");
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        Assert.Equal("third-noop", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain("environment.set:ENV_NOOP", fixture.Platform.Operations.Skip(operationsBefore));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations.Skip(operationsBefore));
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "stable");
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain("environment.set:ENV_NOOP", fixture.Platform.Operations.Skip(operationsBefore));
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("applied")]
    [InlineData("base")]
    public void RecoverNext_Mixed_rollback_rejects_environment_aggregate_binding_mismatch_before_any_restore(
        string mismatch)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        fixture.RebindMixedRollbackEnvironmentEvidence(mismatch);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.Equal("new-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("desired-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Mixed_rollback_oldest_partial_blocks_later_active_row_until_resolution()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        fixture.SetMixedRollbackThirdParty("environment");
        var later = fixture.AddLaterActiveApply();

        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(fixture.ChangeSetId, failed.RecoveredChangeSetId);
            Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
            Assert.Equal("new-later", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(later.TargetPath)));
        }

        fixture.ResolveMixedRollbackThirdParty("environment");
        using var retryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(fixture.ChangeSetId, recovered.RecoveredChangeSetId);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
        Assert.Equal("new-later", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(later.TargetPath)));
    }

    [Fact]
    public void RecoverNext_Mixed_rollback_rejects_multiple_environment_physical_targets_before_mutation()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedActiveMixedRollback();
        fixture.AddSecondEnvironmentPhysicalTarget();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.RollingBack, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Committed_mixed_rollback_reconciles_rolling_back_ledger_without_target_restore()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.RollingBack);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, result.EffectiveChangeSet?.State);
        Assert.Equal("old-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("stable", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.All(durable.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
            Assert.Equal(SetupCodes.InterruptedRollbackRecovered, target.OutcomeCode);
        });
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        var environmentTarget = Assert.Single(journal.Targets,
            target => target.TargetKind == SetupTargetKind.Env);
        Assert.Equal(["ENV_A", "ENV_B"], environmentTarget.Steps.Select(step => step.MemberKey));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
        var ledgerWrite = Array.FindLastIndex(operations, operation =>
            operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        var notification = Array.IndexOf(operations, "environment.notify");
        var journalWrite = Array.FindLastIndex(operations, operation =>
            operation.EndsWith($"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}", StringComparison.Ordinal));
        Assert.True(ledgerWrite >= 0 && notification > ledgerWrite && journalWrite > notification);
    }

    [Fact]
    public void RecoverNext_Committed_mixed_rollback_rejects_applied_ledger_as_malformed_without_target_io()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.Applied);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == $"file.read:{fixture.TargetPath}" ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}" ||
            operation == "environment.notify");
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("file")]
    [InlineData("no-op")]
    [InlineData("environment-unavailable")]
    [InlineData("file-unavailable")]
    public void RecoverNext_Committed_mixed_rollback_preserves_drift_then_retries_exact_partial(
        string drift)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.RollingBack);
        fixture.SetCommittedMixedRollbackDrift(drift);
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, failed.Code);
            Assert.Equal(SetupRecoveryOperation.Rollback, failed.Operation);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        var partial = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, partial.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, partial.OutcomeCode);
        Assert.All(partial.Targets, target =>
        {
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, target.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        var failedOperations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(failedOperations, operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}" ||
            operation == "environment.notify");

        fixture.ResolveCommittedMixedRollbackDrift(drift);
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, retried.Code);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}");
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("applied")]
    [InlineData("base")]
    public void RecoverNext_Committed_mixed_rollback_rejects_environment_binding_mismatch_without_target_write(
        string mismatch)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.RollingBack);
        fixture.RebindMixedRollbackEnvironmentEvidence(mismatch);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}" ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Committed_mixed_partial_blocks_later_candidate_until_external_resolution()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.RollingBack);
        fixture.SetCommittedMixedRollbackDrift("environment");
        var later = fixture.AddLaterActiveApply();

        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(fixture.ChangeSetId, failed.RecoveredChangeSetId);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
            Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
            Assert.Equal("new-later", Encoding.UTF8.GetString(
                fixture.Platform.ReadSeededFile(later.TargetPath)));
        }

        fixture.ResolveCommittedMixedRollbackDrift("environment");
        using var retryLock = fixture.AcquireLock();
        var recovered = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Disposition);
        Assert.Equal(fixture.ChangeSetId, recovered.RecoveredChangeSetId);
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet(later.ChangeSetId).State);
        Assert.Equal("new-later", Encoding.UTF8.GetString(
            fixture.Platform.ReadSeededFile(later.TargetPath)));
    }

    [Fact]
    public void RecoverNext_Committed_mixed_rollback_rejects_multiple_environment_aggregates_without_mutation()
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState.RollingBack);
        fixture.AddSecondEnvironmentPhysicalTarget();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation == $"file.delete:{fixture.TargetPath}" ||
            operation == "environment.notify");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Active_environment_restore_write_is_classified_before_or_after_effect(bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                "environment.set:ENV_A", new IOException("private-env-restore-after"));
        }
        else
        {
            fixture.Platform.InjectFault(
                "environment.set:ENV_A", new IOException("private-env-restore-before"));
        }

        using (var setupLock = fixture.AcquireLock())
        {
            var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
                result.Disposition);
            Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Partial,
                result.EffectiveChangeSet?.State);
            Assert.Equal(afterEffect ? "old-a" : "desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal(afterEffect ? SetupEnvironmentNotification.Completed : SetupEnvironmentNotification.Pending,
                fixture.LoadJournal().EnvironmentNotification);
        }

        if (!afterEffect)
        {
            using var retryLock = fixture.AcquireLock();
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        }
    }

    [Fact]
    public async Task RecoverNext_Active_environment_full_allowlist_verification_race_is_partial_then_retryable()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier("environment.get:ENV_B", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedUserEnvironment("ENV_B", "third-b");
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);

        setupLock.Dispose();
        fixture.Platform.SeedUserEnvironment("ENV_B", "stable-b");
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
    }

    [Fact]
    public async Task RecoverNext_Active_environment_rebound_backup_fails_before_member_write()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"file.read-bounded:{backup}:2097152", cancellation.Token);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedPathMetadata(
            backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Active_environment_coherent_backup_from_different_base_fails_before_mutation()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        fixture.ReplaceActiveEvidenceWithCoherentBackupFromDifferentBase();
        var backup = new UserEnvironmentSetupStep(fixture.Platform).ReadBackup(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordId),
            ["ENV_A", "ENV_B"]);
        Assert.NotEqual(Assert.Single(fixture.Plan.Targets).BaseStateHash, backup.AggregateHash);
        Assert.Collection(
            backup.Members,
            member => Assert.Equal("ENV_A", member.Name),
            member => Assert.Equal("ENV_B", member.Name));
        Assert.Equal(
            ["ENV_A", "ENV_B"],
            Assert.Single(fixture.LoadJournal().Targets).Steps.Select(step => step.MemberKey));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        var failedJournal = fixture.LoadJournal();
        Assert.Equal(SetupJournalPhase.Partial, failedJournal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, failedJournal.EnvironmentNotification);
        Assert.All(Assert.Single(failedJournal.Targets).Steps,
            step => Assert.Equal(SetupJournalStepPhase.MutationCompleted, step.Phase));
        var failedChangeSet = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, failedChangeSet.State);
        Assert.Equal(Assert.Single(fixture.Plan.Targets).BaseStateHash,
            Assert.Single(failedChangeSet.Targets).PreviousStateHash);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public void RecoverNext_Active_environment_notification_failure_keeps_terminal_state_and_replays_only_notification()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        fixture.Platform.InjectFault("environment.notify", new IOException("private-active-notify"));
        using (var setupLock = fixture.AcquireLock())
        {
            var failed = fixture.ReopenCoordinator().RecoverNext(setupLock);
            Assert.Equal(SetupRecoveryDisposition.Failed, failed.Disposition);
            Assert.Equal(SetupChangeSetState.Partial, failed.EffectiveChangeSet?.State);
        }

        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverNext_Active_environment_notification_marker_failure_never_recompensates()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier("environment.notify", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(destination, 0);
        fixture.Platform.InjectFault(
            $"file.write-new:{temporary}", new IOException("private-active-marker"));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));

        setupLock.Dispose();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var retryLock = fixture.AcquireLock();
        Assert.Equal(SetupRecoveryDisposition.Recovered,
            fixture.ReopenCoordinator().RecoverNext(retryLock).Disposition);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_environment_restore_intent_is_proven_before_member_write(bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier("environment.get:ENV_A", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(destination, 0);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{destination}", new IOException("private-env-intent-after"));
        }
        else
        {
            fixture.Platform.InjectFault(
                $"file.write-new:{temporary}", new IOException("private-env-intent-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(afterEffect ? "old-a" : "desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(afterEffect ? SetupJournalPhase.Restored : SetupJournalPhase.Compensating,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupEnvironmentNotification.Completed : SetupEnvironmentNotification.Pending,
            fixture.LoadJournal().EnvironmentNotification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_environment_restore_completion_is_proven_before_terminal_state(bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var destination = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = fixture.NextTemporaryPath(destination, 0);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{destination}", new IOException("private-env-completion-after"));
        }
        else
        {
            fixture.Platform.InjectFault(
                $"file.write-new:{temporary}", new IOException("private-env-completion-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(afterEffect ? SetupJournalPhase.Restored : SetupJournalPhase.Compensating,
            fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupJournalStepPhase.RestoreCompleted : SetupJournalStepPhase.RestoreStarted,
            Assert.Single(Assert.Single(fixture.LoadJournal().Targets).Steps).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverNext_Active_environment_terminal_journal_can_be_durably_ahead_of_ledger(bool afterEffect)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedActiveApply(SetupJournalStepPhase.MutationCompleted, "desired");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier("environment.get:ENV_B", cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        var replace = $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(replace, new IOException("private-env-ledger-after"));
        }
        else
        {
            fixture.Platform.InjectFault(replace, new IOException("private-env-ledger-before"));
        }

        barrier.Release();
        var result = await recovery;

        Assert.Equal(afterEffect ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(afterEffect ? SetupCodes.InterruptedApplyRecovered : SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(afterEffect ? SetupChangeSetState.Restored : SetupChangeSetState.Compensating,
            fixture.LoadChangeSet().State);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));

        if (!afterEffect)
        {
            setupLock.Dispose();
            using var retryLock = fixture.AcquireLock();
            var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);
            Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
            Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
            Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        }
    }

    [Fact]
    public void RecoverNext_Mixed_file_and_no_op_environment_drift_retries_after_external_resolution()
    {
        var fixture = RecoveryFixture.CreateFileWithNoOpEnvironment();
        fixture.SeedActiveFileApply(SetupJournalStepPhase.MutationCompleted, "desired");
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "drifted");
        var operationsBefore = fixture.Platform.Operations.Count;
        using (var setupLock = fixture.AcquireLock())
        {
            var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

            Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
            Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
            Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
            Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
            var noOpTarget = fixture.LoadChangeSet().Targets.Single(target =>
                target.TargetKind == SetupTargetKind.Env);
            Assert.Equal(SetupCodes.RollbackStale, noOpTarget.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, noOpTarget.RollbackStatus);
        }

        Assert.Equal("drifted", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");

        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "stable");
        using var retryLock = fixture.AcquireLock();
        var retried = fixture.ReopenCoordinator().RecoverNext(retryLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, retried.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, retried.Code);
        Assert.Equal(SetupJournalPhase.Restored, fixture.LoadJournal().Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, fixture.LoadJournal().EnvironmentNotification);
        var restored = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Restored, restored.State);
        Assert.All(restored.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        Assert.Null(restored.Targets.Single(target => target.TargetKind == SetupTargetKind.Env).OutcomeCode);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverNext_Validates_environment_dormant_backup_without_writing(bool removeBackup)
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedPreparedApplyJournalAndBackup();
        if (removeBackup)
        {
            fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordId));
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(removeBackup ? SetupRecoveryDisposition.Failed : SetupRecoveryDisposition.None,
            result.Disposition);
        Assert.Equal(removeBackup ? SetupCodes.InterruptedRecoveryFailed : null, result.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) || operation == "environment.notify");
    }

    [Fact]
    public async Task RecoverNext_Dormant_backup_rebinding_fails_closed_deterministically()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedApplyJournalAndBackup();
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"file.read:{backup}",
            cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedPathMetadata(
            backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
    }

    [Fact]
    public void RecoverNext_Excludes_matching_terminal_journal_after_completion()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        using (var firstLock = fixture.AcquireLock())
        {
            Assert.Equal(SetupRecoveryDisposition.Recovered,
                fixture.ReopenCoordinator().RecoverNext(firstLock).Disposition);
        }

        var operationsBefore = fixture.Platform.Operations.Count;
        using var secondLock = fixture.AcquireLock();
        var second = fixture.ReopenCoordinator().RecoverNext(secondLock);

        Assert.Equal(SetupRecoveryDisposition.None, second.Disposition);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation == $"file.read:{fixture.TargetPath}" ||
            operation.StartsWith("environment.get", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoverNext_Matching_terminal_completion_does_not_require_private_plan()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        using (var firstLock = fixture.AcquireLock())
        {
            Assert.Equal(SetupRecoveryDisposition.Recovered,
                fixture.ReopenCoordinator().RecoverNext(firstLock).Disposition);
        }

        fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        using var secondLock = fixture.AcquireLock();

        Assert.Equal(SetupRecoveryDisposition.None,
            fixture.ReopenCoordinator().RecoverNext(secondLock).Disposition);
    }

    public static TheoryData<string, bool> TerminalImmutableMismatchCases => new()
    {
        { "change-set-id", false },
        { "change-set-id", true },
        { "record-id", false },
        { "record-id", true },
        { "target-kind", false },
        { "target-kind", true },
        { "target-order", false },
        { "target-order", true },
        { "target-count", false },
        { "target-count", true },
        { "member-key", false },
        { "member-key", true },
        { "member-order", false },
        { "member-order", true },
        { "member-count", false },
        { "member-count", true },
        { "backup-reference", false },
        { "backup-reference", true },
        { "rollback-status", false },
        { "rollback-status", true },
        { "file-prior-hash", false },
        { "file-prior-hash", true },
        { "file-desired-hash", false },
        { "file-desired-hash", true },
        { "operation", false },
        { "operation", true },
        { "lifecycle", false },
        { "lifecycle", true },
    };

    [Theory]
    [MemberData(nameof(TerminalImmutableMismatchCases))]
    public void RecoverNext_Rejects_planless_terminal_immutable_mismatch_without_notification_or_target_io(
        string mismatch,
        bool notificationCompleted)
    {
        var fixture = new TerminalEvidenceFixture();
        fixture.SeedMismatchedTerminal(mismatch, notificationCompleted);
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(mismatch == "operation" ? SetupRecoveryOperation.Rollback : SetupRecoveryOperation.Apply,
            result.Operation);
        Assert.Equal(SetupChangeSetState.Partial, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.EffectiveChangeSet?.OutcomeCode);
        Assert.Equal(mismatch == "lifecycle" ? SetupChangeSetState.Restored : SetupChangeSetState.Applied,
            fixture.LoadChangeSet().State);
        Assert.Equal(notificationCompleted
                ? SetupEnvironmentNotification.Completed
                : SetupEnvironmentNotification.Pending,
            fixture.ReadJournalNotification());
        var operations = fixture.Platform.Operations.Skip(operationsBefore).ToArray();
        Assert.DoesNotContain("environment.notify", operations);
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == $"file.read:{fixture.TargetPath}" ||
            operation.StartsWith($"file.write:{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation.StartsWith($"file.write-new:{fixture.TargetPath}", StringComparison.Ordinal) ||
            operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverNext_Dormant_environment_backup_rebinding_after_read_fails_closed()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedPreparedApplyJournalAndBackup();
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var barrier = fixture.Platform.AddBarrier(
            $"file.read-bounded:{backup}:2097152",
            cancellation.Token);
        using var setupLock = fixture.AcquireLock();
        var recovery = Task.Run(() => fixture.ReopenCoordinator().RecoverNext(setupLock), cancellation.Token);
        barrier.WaitUntilReached(cancellation.Token);
        fixture.Platform.SeedPathMetadata(
            backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();

        var result = await recovery;

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
    }

    [Fact]
    public void RecoverNext_Requires_live_matching_setup_lock()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        var disposed = fixture.AcquireLock();
        disposed.Dispose();

        var exception = Assert.Throws<SetupStorageException>(() =>
            fixture.ReopenCoordinator().RecoverNext(disposed));

        Assert.Equal(SetupStorageCodes.LockRequired, exception.Code);
    }

    private static RestartedProducerStores AssertApplyingProducerEvidence(
        ApplyProducedRecoveryFixture fixture,
        SetupJournalStepPhase environmentPhase,
        string expectedEnvironmentValue)
    {
        var restarted = OpenRestartedProducerStores(fixture);
        var plan = Assert.IsType<SetupPrivatePlan>(restarted.PlanStore.Load(fixture.ChangeSetId));
        var ledger = Assert.Single(restarted.LedgerStore.LoadForRecovery().ChangeSets);
        var journal = Assert.IsType<SetupTransactionJournal>(restarted.JournalStore.Load(fixture.ChangeSetId));

        Assert.Equal(fixture.ChangeSetId, plan.ChangeSetId);
        Assert.Equal(fixture.ChangeSetId, ledger.ChangeSetId);
        Assert.Equal(SetupChangeSetState.Applying, ledger.State);
        Assert.Equal(SetupJournalOperation.Apply, journal.Operation);
        Assert.Equal(SetupJournalPhase.Applying, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
        Assert.Equal([fixture.FileRecordId, fixture.EnvironmentRecordId],
            journal.Targets.Select(target => target.RecordId));

        var filePlan = Assert.Single(plan.Targets, target => target.RecordId == fixture.FileRecordId);
        var fileLedger = Assert.Single(ledger.Targets, target => target.RecordId == fixture.FileRecordId);
        var fileJournal = Assert.Single(journal.Targets, target => target.RecordId == fixture.FileRecordId);
        var fileStep = Assert.Single(fileJournal.Steps);
        Assert.Null(fileStep.MemberKey);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, fileStep.Phase);
        Assert.Equal(filePlan.BaseStateHash, fileStep.PriorStateHash);
        Assert.Equal(fixture.DesiredFileHash, fileStep.DesiredStateHash);
        Assert.Equal(fixture.FileRecordId.ToString("D"), fileStep.BackupReference);
        Assert.Equal(fixture.FileRecordId.ToString("D"), fileLedger.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, fileLedger.RollbackStatus);

        var environmentPlan = Assert.Single(
            plan.Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        var environmentLedger = Assert.Single(
            ledger.Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        var environmentJournal = Assert.Single(
            journal.Targets,
            target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(["ENV_A", "ENV_B"], environmentPlan.Members.Select(member => member.SettingKey));
        Assert.Equal([SetupOperation.Replace, SetupOperation.NoOp],
            environmentPlan.Members.Select(member => member.Operation));
        var environmentStep = Assert.Single(environmentJournal.Steps);
        Assert.Equal("ENV_A", environmentStep.MemberKey);
        Assert.Equal(environmentPhase, environmentStep.Phase);
        var environment = new UserEnvironmentSetupStep(fixture.Platform);
        Assert.Equal(
            environment.HashMember("ENV_A", UserEnvironmentValue.Present("old-a")),
            environmentStep.PriorStateHash);
        Assert.Equal(
            environment.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
            environmentStep.DesiredStateHash);
        Assert.Equal(fixture.EnvironmentRecordId.ToString("D"), environmentStep.BackupReference);
        Assert.Equal(fixture.EnvironmentRecordId.ToString("D"), environmentLedger.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, environmentLedger.RollbackStatus);

        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(expectedEnvironmentValue, fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("outside-model-a", fixture.Platform.ReadUserEnvironment("UNRELATED"));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
        return restarted;
    }

    private static RestartedProducerStores AssertCompensatingProducerEvidence(
        ApplyProducedRecoveryFixture fixture)
    {
        var restarted = OpenRestartedProducerStores(fixture);
        var plan = Assert.IsType<SetupPrivatePlan>(restarted.PlanStore.Load(fixture.ChangeSetId));
        var ledger = Assert.Single(restarted.LedgerStore.LoadForRecovery().ChangeSets);
        var journal = Assert.IsType<SetupTransactionJournal>(restarted.JournalStore.Load(fixture.ChangeSetId));

        Assert.Equal(fixture.ChangeSetId, plan.ChangeSetId);
        Assert.Equal(fixture.ChangeSetId, ledger.ChangeSetId);
        Assert.Equal(SetupChangeSetState.Compensating, ledger.State);
        Assert.Equal(SetupJournalOperation.Apply, journal.Operation);
        Assert.Equal(SetupJournalPhase.Compensating, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
        Assert.Equal([fixture.FileRecordId, fixture.EnvironmentRecordId],
            journal.Targets.Select(target => target.RecordId));

        var fileStep = Assert.Single(
            journal.Targets.Single(target => target.RecordId == fixture.FileRecordId).Steps);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, fileStep.Phase);
        Assert.Equal(fixture.DesiredFileHash, fileStep.DesiredStateHash);
        Assert.Equal(fixture.FileRecordId.ToString("D"), fileStep.BackupReference);
        var environmentStep = Assert.Single(
            journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps);
        Assert.Equal("ENV_A", environmentStep.MemberKey);
        Assert.Equal(SetupJournalStepPhase.RestoreStarted, environmentStep.Phase);
        var environment = new UserEnvironmentSetupStep(fixture.Platform);
        Assert.Equal(
            environment.HashMember("ENV_A", UserEnvironmentValue.Present("old-a")),
            environmentStep.PriorStateHash);
        Assert.Equal(
            environment.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
            environmentStep.DesiredStateHash);
        Assert.Equal(fixture.EnvironmentRecordId.ToString("D"), environmentStep.BackupReference);

        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("outside-model-a", fixture.Platform.ReadUserEnvironment("UNRELATED"));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
        return restarted;
    }

    private static RestartedProducerStores OpenRestartedProducerStores(ApplyProducedRecoveryFixture fixture)
    {
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        var journalStore = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths);
        return new RestartedProducerStores(
            planStore,
            ledgerStore,
            journalStore,
            new SetupRecoveryCoordinator(
                fixture.Platform,
                fixture.Paths,
                planStore,
                ledgerStore,
                journalStore));
    }

    private static void AssertRecoveredProducerState(
        ApplyProducedRecoveryFixture fixture,
        RestartedProducerStores restarted,
        SetupRecoveryResult result)
    {
        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal(SetupChangeSetState.Restored, result.EffectiveChangeSet?.State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.EffectiveChangeSet?.OutcomeCode);
        Assert.All(result.EffectiveChangeSet!.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupCodes.InterruptedApplyRecovered, target.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });

        var durable = Assert.Single(restarted.LedgerStore.LoadForRecovery().ChangeSets);
        Assert.Equal(SetupChangeSetState.Restored, durable.State);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupCodes.InterruptedApplyRecovered, target.OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        var journal = Assert.IsType<SetupTransactionJournal>(
            restarted.JournalStore.Load(fixture.ChangeSetId));
        Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        Assert.All(journal.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.RestoreCompleted, step.Phase));

        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("outside-model-a", fixture.Platform.ReadUserEnvironment("UNRELATED"));
    }

    private static void AssertRecoveryOperations(
        ApplyProducedRecoveryFixture fixture,
        IReadOnlyList<string> recoveryOperations)
    {
        var operations = recoveryOperations.ToArray();
        Assert.Equal(1, operations.Count(operation => operation == "environment.notify"));
        Assert.DoesNotContain("environment.set:ENV_B", recoveryOperations);
        Assert.DoesNotContain(recoveryOperations,
            operation => operation.Contains("UNRELATED", StringComparison.Ordinal));
        Assert.Equal(
            1,
            operations.Count(operation =>
                operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal)));
        var ledgerPersisted = Array.FindLastIndex(
            operations,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith($"->{fixture.Paths.OwnershipLedger}", StringComparison.Ordinal));
        var notified = Array.FindIndex(operations, operation => operation == "environment.notify");
        var notificationCompleted = Array.FindLastIndex(
            operations,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith(
                    $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                    StringComparison.Ordinal));
        Assert.True(ledgerPersisted >= 0 && notified > ledgerPersisted && notificationCompleted > notified);
    }

    private sealed record RestartedProducerStores(
        SetupPlanStore PlanStore,
        SetupLedgerStore LedgerStore,
        SetupTransactionJournalStore JournalStore,
        SetupRecoveryCoordinator Coordinator);

    private static void AssertNoMixedTargetIo(IReadOnlyList<string> operations, string fileTargetPath)
    {
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation.Contains(fileTargetPath, StringComparison.Ordinal));
    }

    private static void AssertTerminalNotificationFailurePersistence(
        ApplyProducedRecoveryFixture fixture,
        SetupChangeSetState terminalState)
    {
        var durable = fixture.LoadChangeSet();
        Assert.Equal(terminalState, durable.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
            Assert.Equal(SetupCodes.InterruptedRecoveryFailed, target.OutcomeCode));
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
    }

    private sealed class TerminalEvidenceFixture
    {
        private readonly Guid environmentRecordId =
            Guid.Parse("00000000-0000-7000-8000-000000000901");
        private readonly Guid fileRecordId =
            Guid.Parse("00000000-0000-7000-8000-000000000902");
        private readonly Guid mismatchedRecordId =
            Guid.Parse("00000000-0000-7000-8000-000000000903");
        private readonly Guid mismatchedChangeSetId =
            Guid.Parse("00000000-0000-7000-8000-000000000904");
        private readonly SetupLedgerChangeSet planned;
        private readonly string environmentPreviousHash;
        private readonly string environmentAppliedHash;
        private readonly string environmentAPriorHash;
        private readonly string environmentBPriorHash;
        private readonly string environmentADesiredHash;
        private readonly string environmentBDesiredHash;
        private readonly string filePreviousHash;
        private readonly string fileDesiredHash;

        public TerminalEvidenceFixture()
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 3, 4, 5, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000900");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "terminal-evidence.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            Platform.SeedUserEnvironment("ENV_NOOP", "stable");

            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var previousEnvironment = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_NOOP"]);
            environmentPreviousHash = previousEnvironment.AggregateHash;
            environmentAPriorHash = previousEnvironment.Members[0].Hash;
            environmentBPriorHash = previousEnvironment.Members[1].Hash;
            environmentADesiredHash = environmentStep.HashMember(
                "ENV_A", UserEnvironmentValue.Present("desired-a"));
            environmentBDesiredHash = environmentStep.HashMember(
                "ENV_B", UserEnvironmentValue.Present("desired-b"));
            filePreviousHash = SetupHash.File(true, Encoding.UTF8.GetBytes("old-file"));
            fileDesiredHash = SetupHash.File(true, Encoding.UTF8.GetBytes("new-file"));

            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            Platform.SeedUserEnvironment("ENV_B", "desired-b");
            environmentAppliedHash = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_NOOP"]).AggregateHash;
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new-file"));

            var createdAt = Platform.Clock.UtcNow;
            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "mixed",
                createdAt,
                "1.0.0",
                [
                    new SetupPrivatePlanTarget(
                        environmentRecordId,
                        SetupTargetKind.Env,
                        "current-user",
                        environmentPreviousHash,
                        "environment-allowlist",
                        [
                            new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a"),
                            new SetupPrivatePlanMember("ENV_B", SetupOperation.Replace, "desired-b"),
                            new SetupPrivatePlanMember("ENV_NOOP", SetupOperation.NoOp, "stable"),
                        ]),
                    new SetupPrivatePlanTarget(
                        fileRecordId,
                        SetupTargetKind.Json,
                        TargetPath,
                        filePreviousHash,
                        "new-file",
                        [new SetupPrivatePlanMember("file-setting", SetupOperation.Replace, "new-file")]),
                ]);
            planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "mixed",
                createdAt,
                createdAt,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                [
                    new SetupLedgerTarget(
                        environmentRecordId,
                        SetupTargetKind.Env,
                        "user-environment",
                        "github-copilot",
                        [
                            new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                            new SetupLedgerMember("ENV_B", SetupOperation.Replace),
                            new SetupLedgerMember("ENV_NOOP", SetupOperation.NoOp),
                        ],
                        environmentPreviousHash,
                        null,
                        null,
                        null,
                        SetupLedgerRollbackStatus.NotAvailable,
                        SetupRestartRequirement.RestartTerminalSession,
                        CreateStatusProjection(
                        [
                            new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                            new SetupLedgerMember("ENV_B", SetupOperation.Replace),
                            new SetupLedgerMember("ENV_NOOP", SetupOperation.NoOp),
                        ]),
                        "1.0.0"),
                    new SetupLedgerTarget(
                        fileRecordId,
                        SetupTargetKind.Json,
                        "settings",
                        "github-copilot",
                        [new SetupLedgerMember("file-setting", SetupOperation.Replace)],
                        filePreviousHash,
                        null,
                        null,
                        null,
                        SetupLedgerRollbackStatus.NotAvailable,
                        SetupRestartRequirement.RestartVsCode,
                        CreateStatusProjection([new SetupLedgerMember("file-setting", SetupOperation.Replace)]),
                        "1.0.0"),
                ]);
            var planStore = new SetupPlanStore(Platform, Paths);
            using var setupLock = AcquireLock();
            new SetupLedgerStore(Platform, Paths, planStore)
                .PersistPlannedChangeSet(setupLock, plan, planned);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public string TargetPath { get; }

        public SetupLock AcquireLock()
        {
            var result = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(result.Acquired);
            return result.Lock!;
        }

        public SetupRecoveryCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRecoveryCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupLedgerChangeSet LoadChangeSet()
        {
            return LoadChangeSet(ChangeSetId);
        }

        public SetupLedgerChangeSet LoadChangeSet(Guid changeSetId)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets
                .Single(changeSet => changeSet.ChangeSetId == changeSetId);
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public string NextTemporaryPath(string destination, int offset)
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

        public void SeedActiveMixedApply()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();

            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            var environmentBackup = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_NOOP"]);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
            var fileBackup = fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateOrValidateBackup(
                Paths.GetBackup(ChangeSetId, environmentRecordId), environmentBackup);
            fileStep.CreateOrValidateBackup(Paths.GetBackup(ChangeSetId, fileRecordId), fileBackup);

            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            Platform.SeedUserEnvironment("ENV_B", "desired-b");
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new-file"));
            journalStore.CreatePrepared(
                setupLock,
                ChangeSetId,
                SetupJournalOperation.Apply,
                [
                    new SetupJournalTarget(
                        environmentRecordId,
                        SetupTargetKind.Env,
                        [
                            new SetupJournalStep(
                                "ENV_A", environmentAPriorHash, environmentADesiredHash,
                                environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
                            new SetupJournalStep(
                                "ENV_B", environmentBPriorHash, environmentBDesiredHash,
                                environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
                        ]),
                    new SetupJournalTarget(
                        fileRecordId,
                        SetupTargetKind.Json,
                        [new SetupJournalStep(
                            null, filePreviousHash, fileDesiredHash,
                            fileRecordId.ToString("D"), SetupJournalStepPhase.Pending)]),
                ]);
            ledgerStore.Save(setupLock, new SetupOwnershipLedger(1,
                [planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets =
                    [
                        planned.Targets[0] with
                        {
                            BackupReference = environmentRecordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        },
                        planned.Targets[1] with
                        {
                            BackupReference = fileRecordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        },
                    ],
                }]));
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.Applying);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_A",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_A",
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_B",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_B",
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, fileRecordId, null,
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, fileRecordId, null,
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
        }

        public void SeedActiveMixedRollback()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();

            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            var environmentBackup = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_NOOP"]);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
            var fileBackup = fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateOrValidateBackup(
                Paths.GetBackup(ChangeSetId, environmentRecordId), environmentBackup);
            fileStep.CreateOrValidateBackup(Paths.GetBackup(ChangeSetId, fileRecordId), fileBackup);

            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            Platform.SeedUserEnvironment("ENV_B", "desired-b");
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new-file"));
            journalStore.CreatePrepared(
                setupLock,
                ChangeSetId,
                SetupJournalOperation.Rollback,
                [
                    new SetupJournalTarget(
                        environmentRecordId,
                        SetupTargetKind.Env,
                        [
                            new SetupJournalStep(
                                "ENV_A", environmentAPriorHash, environmentADesiredHash,
                                environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
                            new SetupJournalStep(
                                "ENV_B", environmentBPriorHash, environmentBDesiredHash,
                                environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
                        ]),
                    new SetupJournalTarget(
                        fileRecordId,
                        SetupTargetKind.Json,
                        [new SetupJournalStep(
                            null, filePreviousHash, fileDesiredHash,
                            fileRecordId.ToString("D"), SetupJournalStepPhase.Pending)]),
                ]);
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.RollingBack);
            ledgerStore.Save(setupLock, new SetupOwnershipLedger(1,
                [planned with
                {
                    State = SetupChangeSetState.RollingBack,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets =
                    [
                        planned.Targets[0] with
                        {
                            AppliedStateHash = environmentAppliedHash,
                            BackupReference = environmentRecordId.ToString("D"),
                            OutcomeCode = SetupCodes.ApplySucceeded,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        },
                        planned.Targets[1] with
                        {
                            AppliedStateHash = fileDesiredHash,
                            BackupReference = fileRecordId.ToString("D"),
                            OutcomeCode = SetupCodes.ApplySucceeded,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        },
                    ],
                }]));
        }

        public void SeedCommittedMixedRollbackJournalOnly(SetupChangeSetState ledgerState)
        {
            SeedActiveMixedRollback();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = AcquireLock();
            journalStore.MarkStepPhase(setupLock, ChangeSetId, fileRecordId, null,
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
            journalStore.MarkStepPhase(setupLock, ChangeSetId, fileRecordId, null,
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_B",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_B",
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_A",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            journalStore.MarkStepPhase(setupLock, ChangeSetId, environmentRecordId, "ENV_A",
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.Committed);
            if (ledgerState == SetupChangeSetState.RollingBack)
            {
                return;
            }

            Assert.Equal(SetupChangeSetState.Applied, ledgerState);
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? current with
                    {
                        State = ledgerState,
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        Targets = current.Targets.Select(target => target with
                        {
                            OutcomeCode = SetupCodes.ApplySucceeded,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }).ToArray(),
                    }
                    : changeSet).ToArray(),
            });
        }

        public void SetCommittedMixedRollbackDrift(string drift)
        {
            switch (drift)
            {
                case "environment":
                    Platform.SeedUserEnvironment("ENV_B", "third-b");
                    break;
                case "file":
                    Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("third-file"));
                    break;
                case "no-op":
                    Platform.SeedUserEnvironment("ENV_NOOP", "third-noop");
                    break;
                case "environment-unavailable":
                    Platform.InjectFault("environment.get:ENV_A", new IOException("private-env-unavailable"));
                    break;
                case "file-unavailable":
                    Platform.SeedPathMetadata(
                        TargetPath,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(drift));
            }
        }

        public void ResolveCommittedMixedRollbackDrift(string drift)
        {
            switch (drift)
            {
                case "environment":
                    Platform.SeedUserEnvironment("ENV_B", "old-b");
                    break;
                case "file":
                    Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
                    break;
                case "no-op":
                    Platform.SeedUserEnvironment("ENV_NOOP", "stable");
                    break;
                case "environment-unavailable":
                    break;
                case "file-unavailable":
                    Platform.SeedPathMetadata(
                        TargetPath,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal));
                    Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(drift));
            }
        }

        public void SetMixedRollbackThirdParty(string target)
        {
            if (target == "environment")
            {
                Platform.SeedUserEnvironment("ENV_B", "third-b");
                return;
            }

            if (target == "file")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("third-file"));
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(target));
        }

        public void ResolveMixedRollbackThirdParty(string target)
        {
            if (target == "environment")
            {
                Platform.SeedUserEnvironment("ENV_B", "old-b");
                return;
            }

            if (target == "file")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(target));
        }

        public void RebindMixedRollbackEnvironmentEvidence(string mismatch)
        {
            if (mismatch == "backup")
            {
                var environmentStep = new UserEnvironmentSetupStep(Platform);
                var backupPath = Paths.GetBackup(ChangeSetId, environmentRecordId);
                var currentA = Platform.ReadUserEnvironment("ENV_A");
                var currentB = Platform.ReadUserEnvironment("ENV_B");
                var currentNoOp = Platform.ReadUserEnvironment("ENV_NOOP");
                Platform.FileSystem.DeleteFile(backupPath);
                Platform.SeedUserEnvironment("ENV_A", "rebound-a");
                environmentStep.CreateBackup(
                    backupPath,
                    environmentStep.Capture(["ENV_A", "ENV_B", "ENV_NOOP"]));
                Platform.SeedUserEnvironment("ENV_A", currentA);
                Platform.SeedUserEnvironment("ENV_B", currentB);
                Platform.SeedUserEnvironment("ENV_NOOP", currentNoOp);
                return;
            }

            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = AcquireLock();
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? current with
                    {
                        Targets = current.Targets.Select(target => target.RecordId == environmentRecordId
                            ? mismatch switch
                            {
                                "applied" => target with { AppliedStateHash = new string('a', 64) },
                                "base" => target with { PreviousStateHash = new string('b', 64) },
                                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
                            }
                            : target).ToArray(),
                    }
                    : changeSet).ToArray(),
            });
        }

        public (Guid ChangeSetId, string TargetPath) AddLaterActiveApply()
        {
            var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000911");
            var recordId = Guid.Parse("00000000-0000-7000-8000-000000000912");
            var targetPath = Path.Combine(Platform.LocalApplicationData, "later-mixed.json");
            Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old-later"));
            var previousHash = SetupHash.File(true, Encoding.UTF8.GetBytes("old-later"));
            var desiredHash = SetupHash.File(true, Encoding.UTF8.GetBytes("new-later"));
            var createdAt = planned.CreatedAt.AddMinutes(1);
            var plan = new SetupPrivatePlan(
                1,
                changeSetId,
                "github-copilot",
                "mixed-later",
                createdAt,
                "1.0.0",
                [new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Json,
                    targetPath,
                    previousHash,
                    "new-later",
                    [new SetupPrivatePlanMember("later-setting", SetupOperation.Replace, "new-later")])]);
            var plannedLater = new SetupLedgerChangeSet(
                changeSetId,
                "github-copilot",
                "mixed-later",
                createdAt,
                createdAt,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                    recordId,
                    SetupTargetKind.Json,
                    "settings",
                    "github-copilot",
                    [new SetupLedgerMember("later-setting", SetupOperation.Replace)],
                    previousHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection([new SetupLedgerMember("later-setting", SetupOperation.Replace)]),
                    "1.0.0")]);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();
            ledgerStore.PersistPlannedChangeSet(setupLock, plan, plannedLater);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, changeSetId.ToString("D")));
            fileStep.CreateOrValidateBackup(
                Paths.GetBackup(changeSetId, recordId),
                fileStep.Capture(Path.GetDirectoryName(targetPath)!, targetPath));
            journalStore.CreatePrepared(
                setupLock,
                changeSetId,
                SetupJournalOperation.Apply,
                [new SetupJournalTarget(
                    recordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        previousHash,
                        desiredHash,
                        recordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == changeSetId
                    ? changeSet with
                    {
                        State = SetupChangeSetState.Applying,
                        Targets = [changeSet.Targets[0] with
                        {
                            BackupReference = recordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }],
                    }
                    : changeSet).ToArray(),
            });
            journalStore.MarkTransactionPhase(setupLock, changeSetId, SetupJournalPhase.Applying);
            journalStore.MarkStepPhase(
                setupLock,
                changeSetId,
                recordId,
                null,
                SetupJournalStepPhase.Pending,
                SetupJournalStepPhase.MutationStarted);
            Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("new-later"));
            journalStore.MarkStepPhase(
                setupLock,
                changeSetId,
                recordId,
                null,
                SetupJournalStepPhase.MutationStarted,
                SetupJournalStepPhase.MutationCompleted);
            return (changeSetId, targetPath);
        }

        public void AddSecondEnvironmentPhysicalTarget()
        {
            var recordId = Guid.Parse("00000000-0000-7000-8000-000000000913");
            Platform.SeedUserEnvironment("ENV_SECOND", "stable-second");
            var environmentHash = new UserEnvironmentSetupStep(Platform)
                .Capture(["ENV_SECOND"])
                .AggregateHash;
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = AcquireLock();
            var plan = planStore.Load(ChangeSetId)!;
            planStore.Delete(setupLock, ChangeSetId);
            planStore.Create(setupLock, plan with
            {
                Targets = plan.Targets.Append(new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Env,
                    "current-user-second",
                    environmentHash,
                    "environment-allowlist-second",
                    [new SetupPrivatePlanMember(
                        "ENV_SECOND", SetupOperation.NoOp, "stable-second")])).ToArray(),
            });
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? current with
                    {
                        Targets = current.Targets.Append(new SetupLedgerTarget(
                            recordId,
                            SetupTargetKind.Env,
                            "user-environment-second",
                            "github-copilot",
                            [new SetupLedgerMember("ENV_SECOND", SetupOperation.NoOp)],
                            environmentHash,
                            null,
                            null,
                            null,
                            SetupLedgerRollbackStatus.NotAvailable,
                            SetupRestartRequirement.RestartTerminalSession,
                            CreateStatusProjection([new SetupLedgerMember("ENV_SECOND", SetupOperation.NoOp)], suppressManifest: true),
                            "1.0.0")).ToArray(),
                    }
                    : changeSet).ToArray(),
            });
        }

        public SetupEnvironmentNotification ReadJournalNotification()
        {
            var read = Platform.FileSystem.ReadAtMostBytes(
                Paths.GetTransactionJournal(ChangeSetId),
                SetupTransactionJournalStore.MaximumJournalBytes);
            Assert.True(read.IsComplete);
            using var document = JsonDocument.Parse(read.Bytes);
            return document.RootElement.GetProperty("environment_notification").GetString() switch
            {
                "pending" => SetupEnvironmentNotification.Pending,
                "completed" => SetupEnvironmentNotification.Completed,
                _ => SetupEnvironmentNotification.NotRequired,
            };
        }

        public void SeedMismatchedTerminal(string mismatch, bool notificationCompleted)
        {
            var environmentSteps = new[]
            {
                new SetupJournalStep(
                    "ENV_A", environmentAPriorHash, environmentADesiredHash,
                    environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
                new SetupJournalStep(
                    "ENV_B", environmentBPriorHash, environmentBDesiredHash,
                    environmentRecordId.ToString("D"), SetupJournalStepPhase.Pending),
            };
            var environmentTarget = new SetupJournalTarget(
                environmentRecordId, SetupTargetKind.Env, environmentSteps);
            var fileTarget = new SetupJournalTarget(
                fileRecordId,
                SetupTargetKind.Json,
                [new SetupJournalStep(
                    null, filePreviousHash, fileDesiredHash,
                    fileRecordId.ToString("D"), SetupJournalStepPhase.Pending)]);
            IReadOnlyList<SetupJournalTarget> journalTargets = [environmentTarget, fileTarget];
            var operation = SetupJournalOperation.Apply;
            var journalChangeSetId = ChangeSetId;
            var applied = planned with
            {
                State = SetupChangeSetState.Applied,
                OutcomeCode = SetupCodes.ApplySucceeded,
                Targets =
                [
                    planned.Targets[0] with
                    {
                        AppliedStateHash = environmentAppliedHash,
                        BackupReference = environmentRecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    },
                    planned.Targets[1] with
                    {
                        AppliedStateHash = fileDesiredHash,
                        BackupReference = fileRecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    },
                ],
            };

            switch (mismatch)
            {
                case "change-set-id":
                    journalChangeSetId = mismatchedChangeSetId;
                    break;
                case "record-id":
                    journalTargets =
                    [environmentTarget with { RecordId = mismatchedRecordId }, fileTarget];
                    break;
                case "target-kind":
                    journalTargets =
                    [environmentTarget, fileTarget with { TargetKind = SetupTargetKind.Toml }];
                    break;
                case "target-order":
                    journalTargets = [fileTarget, environmentTarget];
                    break;
                case "target-count":
                    journalTargets = [environmentTarget];
                    break;
                case "member-key":
                    journalTargets =
                    [environmentTarget with
                    {
                        Steps =
                        [environmentSteps[0] with { MemberKey = "ENV_C" }, environmentSteps[1]],
                    }, fileTarget];
                    break;
                case "member-order":
                    journalTargets =
                    [environmentTarget with { Steps = [environmentSteps[1], environmentSteps[0]] }, fileTarget];
                    break;
                case "member-count":
                    journalTargets =
                    [environmentTarget with { Steps = [environmentSteps[0]] }, fileTarget];
                    break;
                case "backup-reference":
                    journalTargets =
                    [environmentTarget with
                    {
                        Steps = environmentSteps.Select(step => step with
                        {
                            BackupReference = "different-backup",
                        }).ToArray(),
                    }, fileTarget];
                    break;
                case "rollback-status":
                    applied = applied with
                    {
                        Targets =
                        [applied.Targets[0], applied.Targets[1] with
                        {
                            RollbackStatus = SetupLedgerRollbackStatus.Succeeded,
                        }],
                    };
                    break;
                case "file-prior-hash":
                    journalTargets =
                    [environmentTarget, fileTarget with
                    {
                        Steps = [fileTarget.Steps[0] with { PriorStateHash = new string('1', 64) }],
                    }];
                    break;
                case "file-desired-hash":
                    journalTargets =
                    [environmentTarget, fileTarget with
                    {
                        Steps = [fileTarget.Steps[0] with { DesiredStateHash = new string('2', 64) }],
                    }];
                    break;
                case "operation":
                    operation = SetupJournalOperation.Rollback;
                    break;
                case "lifecycle":
                    applied = applied with
                    {
                        State = SetupChangeSetState.Restored,
                        Targets = applied.Targets.Select(target => target with
                        {
                            AppliedStateHash = null,
                            BackupReference = null,
                            RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                        }).ToArray(),
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mismatch));
            }

            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = AcquireLock();
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock, ledger with { ChangeSets = [applied] });
            journalStore.CreatePrepared(setupLock, journalChangeSetId, operation, journalTargets);
            if (operation == SetupJournalOperation.Apply)
            {
                journalStore.MarkTransactionPhase(setupLock, journalChangeSetId, SetupJournalPhase.Applying);
                foreach (var target in journalTargets)
                {
                    foreach (var step in target.Steps)
                    {
                        journalStore.MarkStepPhase(setupLock, journalChangeSetId, target.RecordId, step.MemberKey,
                            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
                        journalStore.MarkStepPhase(setupLock, journalChangeSetId, target.RecordId, step.MemberKey,
                            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
                    }
                }

                journalStore.MarkTransactionPhase(setupLock, journalChangeSetId, SetupJournalPhase.Committed);
            }
            else
            {
                journalStore.MarkTransactionPhase(setupLock, journalChangeSetId, SetupJournalPhase.RollingBack);
                foreach (var target in journalTargets)
                {
                    foreach (var step in target.Steps)
                    {
                        journalStore.MarkStepPhase(setupLock, journalChangeSetId, target.RecordId, step.MemberKey,
                            SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
                        journalStore.MarkStepPhase(setupLock, journalChangeSetId, target.RecordId, step.MemberKey,
                            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
                    }
                }

                journalStore.MarkTransactionPhase(setupLock, journalChangeSetId, SetupJournalPhase.Committed);
            }

            if (notificationCompleted)
            {
                journalStore.MarkEnvironmentNotificationCompleted(setupLock, journalChangeSetId);
            }

            if (journalChangeSetId != ChangeSetId)
            {
                var read = Platform.FileSystem.ReadAtMostBytes(
                    Paths.GetTransactionJournal(journalChangeSetId),
                    SetupTransactionJournalStore.MaximumJournalBytes);
                Assert.True(read.IsComplete);
                Platform.SeedFile(
                    Paths.GetTransactionJournal(ChangeSetId),
                    read.Bytes);
                Platform.FileSystem.DeleteFile(Paths.GetTransactionJournal(journalChangeSetId));
            }

            Platform.FileSystem.DeleteFile(Paths.GetPlan(ChangeSetId));
        }
    }

    private sealed class RecoveryFixture
    {
        private RecoveryFixture(
            int fileTargetCount = 1,
            bool lastFileNoOp = false,
            bool includeEnvironmentNoOp = false,
            bool initialFileExists = true)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000501");
            FileRecordIds = Enumerable.Range(0, fileTargetCount)
                .Select(index => Guid.Parse($"00000000-0000-7000-8000-{502 + index:D12}"))
                .ToArray();
            TargetPaths = Enumerable.Range(0, fileTargetCount)
                .Select(index => Path.Combine(
                    Platform.LocalApplicationData,
                    index == 0 ? "settings.json" : $"settings-{index}.json"))
                .ToArray();
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            if (initialFileExists)
            {
                foreach (var targetPath in TargetPaths)
                {
                    Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old"));
                }
            }

            if (includeEnvironmentNoOp)
            {
                Platform.SeedUserEnvironment("ENV_NOOP", "stable");
            }

            var filePlanTargets = FileRecordIds.Select((recordId, index) =>
            {
                var noOp = lastFileNoOp && index == FileRecordIds.Count - 1;
                return new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Json,
                    TargetPaths[index],
                    SetupHash.File(initialFileExists, initialFileExists ? Encoding.UTF8.GetBytes("old") : []),
                    noOp ? "old" : "new",
                    [new SetupPrivatePlanMember(
                        index == 0 ? "setting" : $"setting-{index}",
                        noOp ? SetupOperation.NoOp : SetupOperation.Replace,
                        noOp ? "old" : "new")]);
            }).ToList();
            var environmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000590");
            var environmentHash = includeEnvironmentNoOp
                ? new UserEnvironmentSetupStep(Platform).Capture(["ENV_NOOP"]).AggregateHash
                : null;
            if (includeEnvironmentNoOp)
            {
                filePlanTargets.Add(new SetupPrivatePlanTarget(
                    environmentRecordId,
                    SetupTargetKind.Env,
                    "environment-allowlist",
                    environmentHash!,
                    "environment-allowlist",
                    [new SetupPrivatePlanMember("ENV_NOOP", SetupOperation.NoOp, "stable")]));
            }

            Plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                filePlanTargets);
            var ledgerTargets = FileRecordIds.Select((recordId, index) =>
            {
                var noOp = lastFileNoOp && index == FileRecordIds.Count - 1;
                return new SetupLedgerTarget(
                    recordId,
                    SetupTargetKind.Json,
                    index == 0 ? "settings" : $"settings-{index}",
                    "github-copilot",
                    [new SetupLedgerMember(
                        index == 0 ? "setting" : $"setting-{index}",
                        noOp ? SetupOperation.NoOp : SetupOperation.Replace)],
                    Plan.Targets[index].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection(
                    [
                        new SetupLedgerMember(index == 0 ? "setting" : $"setting-{index}", noOp ? SetupOperation.NoOp : SetupOperation.Replace),
                    ]),
                    "1.0.0");
            }).ToList();
            if (includeEnvironmentNoOp)
            {
                ledgerTargets.Add(new SetupLedgerTarget(
                    environmentRecordId,
                    SetupTargetKind.Env,
                    "user-environment",
                    "github-copilot",
                    [new SetupLedgerMember("ENV_NOOP", SetupOperation.NoOp)],
                    environmentHash!,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    CreateStatusProjection([new SetupLedgerMember("ENV_NOOP", SetupOperation.NoOp)]),
                    "1.0.0"));
            }
            Planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                ledgerTargets);

            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.PersistPlannedChangeSet(setupLock.Lock!, Plan, Planned);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public IReadOnlyList<Guid> FileRecordIds { get; }
        public IReadOnlyList<string> TargetPaths { get; }
        public Guid FileRecordId => FileRecordIds[0];
        public string TargetPath => TargetPaths[0];
        public SetupPrivatePlan Plan { get; }
        public SetupLedgerChangeSet Planned { get; }
        public string DesiredFileHash => SetupHash.File(true, Encoding.UTF8.GetBytes("new"));
        public FileChangeSetSeed PrimarySeed => new(Plan, Planned, TargetPath, FileRecordId);

        public static RecoveryFixture CreateFileOnly() => new();

        public static RecoveryFixture CreateFileOnly(int targetCount) => new(targetCount);

        public static RecoveryFixture CreateNewFile() => new(initialFileExists: false);

        public static RecoveryFixture CreateFileWithNoOpFile() => new(2, lastFileNoOp: true);

        public static RecoveryFixture CreateFileWithNoOpEnvironment() => new(includeEnvironmentNoOp: true);

        public SetupLock AcquireLock()
        {
            var result = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(result.Acquired);
            return result.Lock!;
        }

        public SetupRecoveryCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRecoveryCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return Assert.Single(new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets);
        }

        public SetupLedgerChangeSet LoadChangeSet(Guid changeSetId)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets
                .Single(changeSet => changeSet.ChangeSetId == changeSetId);
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public void SeedCommittedApplyJournalAndApplyingLedger()
            => SeedCommittedApplyJournalAndApplyingLedger(PrimarySeed);

        public void SeedCommittedApplyJournalAndApplyingLedger(FileChangeSetSeed seed)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);

            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, seed.ChangeSetId.ToString("D")));
            fileStep.CreateBackup(Paths.GetBackup(seed.ChangeSetId, seed.RecordId),
                fileStep.Capture(Path.GetDirectoryName(seed.TargetPath)!, seed.TargetPath));
            journalStore.CreatePrepared(
                setupLock.Lock!,
                seed.ChangeSetId,
                SetupJournalOperation.Apply,
                [new SetupJournalTarget(
                    seed.RecordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        seed.Plan.Targets[0].BaseStateHash,
                        seed.DesiredHash,
                        seed.RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == seed.ChangeSetId
                ? seed.Planned with
                    {
                        State = SetupChangeSetState.Applying,
                        Targets = [seed.Planned.Targets[0] with
                        {
                            BackupReference = seed.RecordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }],
                    }
                : changeSet).ToArray();
            ledgerStore.Save(setupLock.Lock!, ledger with { ChangeSets = changeSets });
            journalStore.MarkTransactionPhase(setupLock.Lock!, seed.ChangeSetId, SetupJournalPhase.Applying);
            journalStore.MarkStepPhase(
                setupLock.Lock!, seed.ChangeSetId, seed.RecordId, null,
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            Platform.SeedFile(seed.TargetPath, Encoding.UTF8.GetBytes("new"));
            journalStore.MarkStepPhase(
                setupLock.Lock!, seed.ChangeSetId, seed.RecordId, null,
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, seed.ChangeSetId, SetupJournalPhase.Committed);
        }

        public void SeedPreparedApplyJournalAndBackup()
        {
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            fileStep.CreateBackup(Paths.GetBackup(ChangeSetId, FileRecordId),
                fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath));
            journalStore.CreatePrepared(
                setupLock.Lock!,
                ChangeSetId,
                SetupJournalOperation.Apply,
                [new SetupJournalTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        Plan.Targets[0].BaseStateHash,
                        DesiredFileHash,
                        FileRecordId.ToString("D"),
                    SetupJournalStepPhase.Pending)])]);
        }

        public FileChangeSetSeed AddFileChangeSet(
            Guid changeSetId,
            Guid recordId,
            DateTimeOffset createdAt,
            string fileName)
        {
            var targetPath = Path.Combine(Platform.LocalApplicationData, fileName + ".json");
            Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old"));
            var plan = new SetupPrivatePlan(
                1,
                changeSetId,
                "github-copilot",
                "vscode",
                createdAt,
                "1.0.0",
                [new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Json,
                    targetPath,
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                    "new",
                    [new SetupPrivatePlanMember("setting", SetupOperation.Replace, "new")])]);
            var planned = new SetupLedgerChangeSet(
                changeSetId,
                "github-copilot",
                "vscode",
                createdAt,
                createdAt,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                    recordId,
                    SetupTargetKind.Json,
                    "settings",
                    "github-copilot",
                    [new SetupLedgerMember("setting", SetupOperation.Replace)],
                    plan.Targets[0].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection([new SetupLedgerMember("setting", SetupOperation.Replace)]),
                    "1.0.0")]);
            var planStore = new SetupPlanStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            new SetupLedgerStore(Platform, Paths, planStore).PersistPlannedChangeSet(setupLock.Lock!, plan, planned);
            return new FileChangeSetSeed(plan, planned, targetPath, recordId);
        }

        public void SeedRestoredApplyJournalAndCompensatingLedger()
        {
            SeedCommittedApplyJournalAndApplyingLedger();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            var applying = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [applying with
                {
                    State = SetupChangeSetState.Compensating,
                    Targets = [applying.Targets[0] with { AppliedStateHash = DesiredFileHash }],
                }],
            });

            // Rebuild the coherent interrupted compensation state from its applying journal.
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            Platform.FileSystem.DeleteFile(journalPath);
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Apply,
                [new SetupJournalTarget(FileRecordId, SetupTargetKind.Json,
                    [new SetupJournalStep(null, Plan.Targets[0].BaseStateHash, DesiredFileHash,
                        FileRecordId.ToString("D"), SetupJournalStepPhase.Pending)])]);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Compensating);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Restored);
        }

        public void SeedApplyingJournalAndLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = Planned.Targets.Select(target => target.RecordId == FileRecordId
                        ? target with
                        {
                            BackupReference = FileRecordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }
                        : target).ToArray(),
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
        }

        public void SeedPreparedJournalAndApplyingLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = FileRecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
        }

        public void SeedActiveFileApply(SetupJournalStepPhase stepPhase, string currentState)
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = Planned.Targets.Select(target => target.RecordId == FileRecordId
                        ? target with
                        {
                            BackupReference = FileRecordId.ToString("D"),
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }
                        : target).ToArray(),
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            if (stepPhase != SetupJournalStepPhase.Pending)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            }

            if (stepPhase is SetupJournalStepPhase.MutationCompleted or SetupJournalStepPhase.RestoreStarted or
                SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            }

            if (stepPhase is SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Compensating);
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted);
                var current = Assert.Single(ledgerStore.LoadForRecovery().ChangeSets);
                ledgerStore.Save(setupLock.Lock!, new SetupOwnershipLedger(1,
                    [current with { State = SetupChangeSetState.Compensating }]));
            }

            if (stepPhase == SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            }

            if (currentState == "prior")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            }
            else if (currentState == "desired")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new"));
            }
            else if (currentState == "third")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("third"));
            }
            else if (currentState == "missing")
            {
                Platform.FileSystem.DeleteFile(TargetPath);
            }
            else if (currentState == "unavailable")
            {
                Platform.SeedPathMetadata(
                    TargetPath,
                    new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(currentState));
            }
        }

        public void SeedActiveFiles(IReadOnlyList<string> currentStates)
        {
            Assert.Equal(Plan.Targets.Count, currentStates.Count);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            var journalTargets = new List<SetupJournalTarget>();
            for (var index = 0; index < Plan.Targets.Count; index++)
            {
                var target = Plan.Targets[index];
                fileStep.CreateBackup(
                    Paths.GetBackup(ChangeSetId, target.RecordId),
                    fileStep.Capture(Path.GetDirectoryName(target.TargetLocation)!, target.TargetLocation));
                journalTargets.Add(new SetupJournalTarget(
                    target.RecordId,
                    target.TargetKind,
                    [new SetupJournalStep(
                        null,
                        target.BaseStateHash,
                        SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState)),
                        target.RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)]));
            }

            journalStore.CreatePrepared(
                setupLock.Lock!, ChangeSetId, SetupJournalOperation.Apply, journalTargets);
            var ledger = ledgerStore.LoadForRecovery();
            var current = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [current with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = current.Targets.Select(target => target with
                    {
                        BackupReference = target.RecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }).ToArray(),
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            foreach (var target in journalTargets)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, target.RecordId, null,
                    SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, target.RecordId, null,
                    SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            }

            for (var index = 0; index < currentStates.Count; index++)
            {
                Platform.SeedFile(
                    TargetPaths[index],
                    Encoding.UTF8.GetBytes(currentStates[index] == "desired" ? "new" : currentStates[index]));
            }
        }

        public void SeedPreparedRollbackJournalAndAppliedLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            Platform.FileSystem.DeleteFile(Paths.GetTransactionJournal(ChangeSetId));
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Rollback,
                [new SetupJournalTarget(FileRecordId, SetupTargetKind.Json,
                    [new SetupJournalStep(null, Plan.Targets[0].BaseStateHash, DesiredFileHash,
                        FileRecordId.ToString("D"), SetupJournalStepPhase.Pending)])]);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new"));
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = [Planned.Targets[0] with
                    {
                        AppliedStateHash = DesiredFileHash,
                        BackupReference = FileRecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
        }

        public void SeedActiveFileRollback(string journalPhase, string stepPhase, string currentState)
        {
            SeedPreparedRollbackJournalAndAppliedLedger();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = AcquireLock();
            var ledger = ledgerStore.LoadForRecovery();
            var applied = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = [applied with
                {
                    State = journalPhase == "partial"
                        ? SetupChangeSetState.Partial
                        : SetupChangeSetState.RollingBack,
                }],
            });
            if (journalPhase != "prepared")
            {
                journalStore.MarkTransactionPhase(
                    setupLock, ChangeSetId, SetupJournalPhase.RollingBack);
            }

            if (stepPhase != "pending")
            {
                journalStore.MarkStepPhase(
                    setupLock, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            }

            if (stepPhase == "restore_completed")
            {
                journalStore.MarkStepPhase(
                    setupLock, ChangeSetId, FileRecordId, null,
                    SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            }

            if (journalPhase == "partial")
            {
                journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.Partial);
            }

            if (currentState == "applied")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new"));
            }
            else if (currentState == "prior")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            }
            else if (currentState == "third")
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("third"));
            }
            else if (currentState == "missing")
            {
                Platform.FileSystem.DeleteFile(TargetPath);
            }
            else if (currentState == "unavailable")
            {
                Platform.SeedPathMetadata(
                    TargetPath,
                    new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(currentState));
            }
        }

        public void SeedActiveFileRollbacks(IReadOnlyList<string> currentStates)
        {
            Assert.Equal(Plan.Targets.Count, currentStates.Count);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            var journalTargets = new List<SetupJournalTarget>();
            for (var index = 0; index < Plan.Targets.Count; index++)
            {
                var target = Plan.Targets[index];
                fileStep.CreateBackup(
                    Paths.GetBackup(ChangeSetId, target.RecordId),
                    fileStep.Capture(Path.GetDirectoryName(target.TargetLocation)!, target.TargetLocation));
                journalTargets.Add(new SetupJournalTarget(
                    target.RecordId,
                    target.TargetKind,
                    [new SetupJournalStep(
                        null,
                        target.BaseStateHash,
                        SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState)),
                        target.RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)]));
            }

            journalStore.CreatePrepared(
                setupLock, ChangeSetId, SetupJournalOperation.Rollback, journalTargets);
            var ledger = ledgerStore.LoadForRecovery();
            var current = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = [current with
                {
                    State = SetupChangeSetState.RollingBack,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = current.Targets.Select((target, index) => target with
                    {
                        AppliedStateHash = journalTargets[index].Steps[0].DesiredStateHash,
                        BackupReference = target.RecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }).ToArray(),
                }],
            });
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.RollingBack);
            for (var index = 0; index < currentStates.Count; index++)
            {
                if (currentStates[index] == "applied")
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes("new"));
                }
                else if (currentStates[index] == "prior")
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes("old"));
                }
                else if (currentStates[index] == "third")
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes("third"));
                }
                else if (currentStates[index] == "unavailable")
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes("new"));
                    Platform.SeedPathMetadata(
                        TargetPaths[index],
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(currentStates));
                }
            }
        }

        public void CommitRollbackJournalOnly()
        {
            using var setupLock = AcquireLock();
            new SetupTransactionJournalStore(Platform, Paths).MarkTransactionPhase(
                setupLock, ChangeSetId, SetupJournalPhase.Committed);
        }

        public void CompleteRollbackStepOnly()
        {
            using var setupLock = AcquireLock();
            new SetupTransactionJournalStore(Platform, Paths).MarkStepPhase(
                setupLock,
                ChangeSetId,
                FileRecordId,
                null,
                SetupJournalStepPhase.RestoreStarted,
                SetupJournalStepPhase.RestoreCompleted);
        }

        public void SeedFileRollbackWithUnjournaledNoOp(string noOpCurrentState)
        {
            Assert.Equal(2, Plan.Targets.Count);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            fileStep.CreateBackup(
                Paths.GetBackup(ChangeSetId, FileRecordId),
                fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath));
            journalStore.CreatePrepared(
                setupLock,
                ChangeSetId,
                SetupJournalOperation.Rollback,
                [new SetupJournalTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        Plan.Targets[0].BaseStateHash,
                        DesiredFileHash,
                        FileRecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
            var ledger = ledgerStore.LoadForRecovery();
            var planned = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = [planned with
                {
                    State = SetupChangeSetState.RollingBack,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets =
                    [
                        planned.Targets[0] with
                        {
                            AppliedStateHash = DesiredFileHash,
                            BackupReference = FileRecordId.ToString("D"),
                            OutcomeCode = SetupCodes.ApplySucceeded,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        },
                        planned.Targets[1],
                    ],
                }],
            });
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.RollingBack);
            Platform.SeedFile(TargetPaths[0], Encoding.UTF8.GetBytes("new"));
            Platform.SeedFile(
                TargetPaths[1],
                Encoding.UTF8.GetBytes(noOpCurrentState == "prior" ? "old" : "third"));
        }

        public void SeedFileRollbackWithUnjournaledEnvironment()
        {
            Assert.Contains(Plan.Targets, target => target.TargetKind == SetupTargetKind.Env);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var fileStep = new AtomicFileSetupStep(Platform);
            using var setupLock = AcquireLock();
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            fileStep.CreateBackup(
                Paths.GetBackup(ChangeSetId, FileRecordId),
                fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath));
            journalStore.CreatePrepared(
                setupLock,
                ChangeSetId,
                SetupJournalOperation.Rollback,
                [new SetupJournalTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        Plan.Targets[0].BaseStateHash,
                        DesiredFileHash,
                        FileRecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
            var ledger = ledgerStore.LoadForRecovery();
            var planned = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = [planned with
                {
                    State = SetupChangeSetState.RollingBack,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = planned.Targets.Select(target => target.RecordId == FileRecordId
                        ? target with
                        {
                            AppliedStateHash = DesiredFileHash,
                            BackupReference = FileRecordId.ToString("D"),
                            OutcomeCode = SetupCodes.ApplySucceeded,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }
                        : target).ToArray(),
                }],
            });
            journalStore.MarkTransactionPhase(setupLock, ChangeSetId, SetupJournalPhase.RollingBack);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("new"));
        }

        public void RebindRollbackEvidence(string variant)
        {
            var backupPath = Paths.GetBackup(ChangeSetId, FileRecordId);
            if (variant == "missing-backup")
            {
                Platform.FileSystem.DeleteFile(backupPath);
            }
            else if (variant == "corrupt-backup")
            {
                Platform.SeedFile(backupPath, Encoding.UTF8.GetBytes("PRIVATE_CORRUPT_BACKUP"));
            }
            else if (variant == "reparse-backup")
            {
                Platform.SeedPathMetadata(
                    backupPath,
                    new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
            }
            else if (variant == "reparse-target")
            {
                Platform.SeedPathMetadata(
                    TargetPath,
                    new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
            }
            else if (variant == "ledger-backup-rebound")
            {
                var planStore = new SetupPlanStore(Platform, Paths);
                var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
                using var setupLock = AcquireLock();
                var ledger = ledgerStore.LoadForRecovery();
                var changeSet = Assert.Single(ledger.ChangeSets);
                ledgerStore.Save(setupLock, ledger with
                {
                    ChangeSets = [changeSet with
                    {
                        Targets = [changeSet.Targets[0] with { BackupReference = "different-reference" }],
                    }],
                });
            }
            else if (variant == "journal-hash-rebound")
            {
                var path = Paths.GetTransactionJournal(ChangeSetId);
                var read = Platform.FileSystem.ReadAtMostBytes(
                    path, SetupTransactionJournalStore.MaximumJournalBytes);
                Assert.True(read.IsComplete);
                var json = Encoding.UTF8.GetString(read.Bytes).Replace(
                    DesiredFileHash, new string('a', 64), StringComparison.Ordinal);
                Platform.SeedFile(path, Encoding.UTF8.GetBytes(json));
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void RebindRollbackLedgerAppliedHash()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = AcquireLock();
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock, ledger with
            {
                ChangeSets = [changeSet with
                {
                    Targets = [changeSet.Targets[0] with { AppliedStateHash = new string('b', 64) }],
                }],
            });
        }

        public string NextTemporaryPath(string destination, int offset)
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

        public sealed record FileChangeSetSeed(
            SetupPrivatePlan Plan,
            SetupLedgerChangeSet Planned,
            string TargetPath,
            Guid RecordId)
        {
            public Guid ChangeSetId => Plan.ChangeSetId;
            public string DesiredHash => SetupHash.File(true, Encoding.UTF8.GetBytes("new"));
        }
    }

    private sealed class ApplyProducedRecoveryFixture
    {
        public ApplyProducedRecoveryFixture(bool missingNoOp = false)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 4, 5, 6, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000901");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000902");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000903");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "producer-settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            if (!missingNoOp)
            {
                Platform.SeedUserEnvironment("ENV_B", "stable-b");
            }
            Platform.SeedUserEnvironment("UNRELATED", "outside-model-a");
            var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(["ENV_A", "ENV_B"]);
            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                "1.0.0",
                [new SetupPrivatePlanTarget(
                     FileRecordId,
                     SetupTargetKind.Json,
                     TargetPath,
                     SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                     "new",
                     [new SetupPrivatePlanMember("setting", SetupOperation.Replace, "new")]),
                 new SetupPrivatePlanTarget(
                     EnvironmentRecordId,
                     SetupTargetKind.Env,
                     "current-user",
                     environmentCapture.AggregateHash,
                     "environment-allowlist",
                     [new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a"),
                      new SetupPrivatePlanMember(
                          "ENV_B",
                          SetupOperation.NoOp,
                          missingNoOp ? null : "stable-b")])]);
            var planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                     FileRecordId,
                     SetupTargetKind.Json,
                     "settings",
                     "github-copilot",
                     [new SetupLedgerMember("setting", SetupOperation.Replace)],
                     SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                     null,
                     null,
                     null,
                     SetupLedgerRollbackStatus.NotAvailable,
                     SetupRestartRequirement.RestartTerminalSession,
                     CreateStatusProjection([new SetupLedgerMember("setting", SetupOperation.Replace)]),
                     "1.0.0"),
                 new SetupLedgerTarget(
                     EnvironmentRecordId,
                     SetupTargetKind.Env,
                     "user-environment",
                     "github-copilot",
                     [new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                      new SetupLedgerMember("ENV_B", SetupOperation.NoOp)],
                     environmentCapture.AggregateHash,
                     null,
                     null,
                     null,
                     SetupLedgerRollbackStatus.NotAvailable,
                     SetupRestartRequirement.RestartTerminalSession,
                     CreateStatusProjection(
                     [
                         new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                         new SetupLedgerMember("ENV_B", SetupOperation.NoOp),
                     ]),
                     "1.0.0")]);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            ledgerStore.PersistPlannedChangeSet(setupLock.Lock!, plan, planned);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid FileRecordId { get; }
        public Guid EnvironmentRecordId { get; }
        public string TargetPath { get; }
        public string DesiredFileHash => SetupHash.File(true, Encoding.UTF8.GetBytes("new"));

        public SetupLock AcquireLock()
        {
            var result = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(result.Acquired);
            return result.Lock!;
        }

        public SetupApplyCoordinator CreateApplyCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupApplyCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths),
                new NoOpApplyRevalidator());
        }

        public SetupRollbackCoordinator CreateRollbackCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRollbackCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupRecoveryCoordinator CreateRecoveryCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRecoveryCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return Assert.Single(
                new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets);
        }

        public void ProduceTerminalPending(SetupChangeSetState terminalState)
        {
            switch (terminalState)
            {
                case SetupChangeSetState.Applied:
                    Platform.InjectFault(
                        "environment.notify",
                        new IOException("private-apply-notification"));
                    using (var applyLock = AcquireLock())
                    {
                        _ = Assert.Throws<SetupApplyException>(() =>
                            CreateApplyCoordinator().Apply(applyLock, ChangeSetId));
                    }

                    break;
                case SetupChangeSetState.Restored:
                    Platform.InjectFault(
                        $"checkpoint:{SetupFaultPoint.AfterCompletionBeforeCommit}",
                        new IOException("private-forward-failure"));
                    Platform.InjectFault(
                        "environment.notify",
                        new IOException("private-restore-notification"));
                    using (var applyLock = AcquireLock())
                    {
                        _ = Assert.Throws<SetupApplyException>(() =>
                            CreateApplyCoordinator().Apply(applyLock, ChangeSetId));
                    }

                    break;
                case SetupChangeSetState.RolledBack:
                    using (var applyLock = AcquireLock())
                    {
                        Assert.Equal(SetupChangeSetState.Applied,
                            CreateApplyCoordinator().Apply(applyLock, ChangeSetId).Value.State);
                    }

                    Platform.InjectFault(
                        "environment.notify",
                        new IOException("private-rollback-notification"));
                    using (var rollbackLock = AcquireLock())
                    {
                        var rollback = CreateRollbackCoordinator().Rollback(rollbackLock, ChangeSetId);
                        Assert.False(rollback.Success);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(terminalState));
            }

            Assert.Equal(terminalState, LoadChangeSet().State);
            Assert.Equal(SetupEnvironmentNotification.Pending, LoadJournal().EnvironmentNotification);
            Assert.Equal(
                terminalState == SetupChangeSetState.Restored
                    ? SetupJournalPhase.Restored
                    : SetupJournalPhase.Committed,
                LoadJournal().Phase);
        }

        public void TamperNotificationArtifact(string variant)
        {
            var planPath = Paths.GetPlan(ChangeSetId);
            var backupPath = Paths.GetBackup(ChangeSetId, EnvironmentRecordId);
            switch (variant)
            {
                case "plan-missing":
                    Platform.FileSystem.DeleteFile(planPath);
                    break;
                case "plan-corrupt":
                    Platform.SeedFile(planPath, Encoding.UTF8.GetBytes("{not-json"));
                    break;
                case "plan-rebound":
                    Platform.SeedPathMetadata(
                        planPath,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                case "backup-missing":
                    Platform.FileSystem.DeleteFile(backupPath);
                    break;
                case "backup-corrupt":
                    Platform.SeedFile(backupPath, Encoding.UTF8.GetBytes("not-an-environment-backup"));
                    break;
                case "backup-rebound":
                    Platform.SeedPathMetadata(
                        backupPath,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void TamperNotificationHash(string variant)
        {
            var replacementHash = variant switch
            {
                "journal-prior-hash" => new string('a', 64),
                "journal-desired-hash" => new string('b', 64),
                "plan-base-hash" => new string('c', 64),
                "backup-aggregate-hash" => new string('d', 64),
                "ledger-previous-hash" => new string('e', 64),
                "ledger-applied-hash" => new string('f', 64),
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };

            switch (variant)
            {
                case "journal-prior-hash":
                    TamperEnvironmentJournal("prior_state_hash", replacementHash);
                    return;
                case "journal-desired-hash":
                    TamperEnvironmentJournal("desired_state_hash", replacementHash);
                    return;
                case "plan-base-hash":
                {
                    var planStore = new SetupPlanStore(Platform, Paths);
                    var plan = Assert.IsType<SetupPrivatePlan>(planStore.Load(ChangeSetId));
                    using var setupLock = AcquireLock();
                    planStore.Delete(setupLock, ChangeSetId);
                    planStore.Create(setupLock, plan with
                    {
                        Targets = plan.Targets.Select(target => target.RecordId == EnvironmentRecordId
                            ? target with { BaseStateHash = replacementHash }
                            : target).ToArray(),
                    });
                    return;
                }
                case "backup-aggregate-hash":
                {
                    Platform.SeedUserEnvironment("ENV_A", "different-backup-state");
                    var environmentStep = new UserEnvironmentSetupStep(Platform);
                    var replacement = environmentStep.Capture(["ENV_A", "ENV_B"]);
                    Platform.SeedUserEnvironment("ENV_A", "desired-a");
                    var actualPath = Paths.GetBackup(ChangeSetId, EnvironmentRecordId);
                    var replacementPath = actualPath + ".replacement";
                    environmentStep.CreateBackup(replacementPath, replacement);
                    Platform.SeedFile(actualPath, Platform.ReadSeededFile(replacementPath));
                    Platform.FileSystem.DeleteFile(replacementPath);
                    return;
                }
                case "ledger-previous-hash":
                case "ledger-applied-hash":
                {
                    var planStore = new SetupPlanStore(Platform, Paths);
                    var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
                    using var setupLock = AcquireLock();
                    var ledger = ledgerStore.LoadForRecovery();
                    var changeSet = Assert.Single(ledger.ChangeSets);
                    ledgerStore.Save(setupLock, ledger with
                    {
                        ChangeSets = [changeSet with
                        {
                            Targets = changeSet.Targets.Select(target => target.RecordId == EnvironmentRecordId
                                ? variant == "ledger-previous-hash"
                                    ? target with { PreviousStateHash = replacementHash }
                                    : target with { AppliedStateHash = replacementHash }
                                : target).ToArray(),
                        }],
                    });
                    return;
                }
            }
        }

        public void TamperEnvironmentJournal(string propertyName, string replacement)
        {
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            var environmentStep = Assert.Single(
                LoadJournal().Targets.Single(target => target.RecordId == EnvironmentRecordId).Steps);
            var original = propertyName switch
            {
                "prior_state_hash" => environmentStep.PriorStateHash,
                "desired_state_hash" => environmentStep.DesiredStateHash,
                "backup_reference" => environmentStep.BackupReference,
                _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
            };
            var json = Encoding.UTF8.GetString(Platform.ReadSeededFile(journalPath));
            var pattern = new Regex(
                $"(\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"){Regex.Escape(original)}(\")",
                RegexOptions.CultureInvariant);
            Assert.Single(pattern.Matches(json).Cast<Match>());
            var tampered = pattern.Replace(
                json,
                match => match.Groups[1].Value + replacement + match.Groups[2].Value,
                1);
            Platform.SeedFile(journalPath, Encoding.UTF8.GetBytes(tampered));
        }

        public void InjectTwoJournalReadFaults()
        {
            var operation = $"file.read-bounded:{Paths.GetTransactionJournal(ChangeSetId)}:" +
                SetupTransactionJournalStore.MaximumJournalBytes;
            Platform.InjectFault(operation, new IOException("private-journal-read-one"));
            Platform.InjectFault(operation, new IOException("private-journal-read-two"));
        }

        public void InjectNextJournalWriteFault()
        {
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            Platform.InjectFault(
                $"file.write-new:{NextTemporaryPath(journalPath)}",
                new IOException("private-journal-write"));
        }

        private string NextTemporaryPath(string destination)
        {
            var maximum = Platform.Operations
                .SelectMany(operation => Regex.Matches(operation,
                    @"\.cao-00000000-0000-7000-8000-(?<value>[0-9]{12})\.tmp"))
                .Select(match => long.Parse(match.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            return destination + ".cao-" +
                Guid.Parse($"00000000-0000-7000-8000-{maximum + 1:D12}").ToString("D") + ".tmp";
        }
    }

    private sealed class NoOpApplyRevalidator : ISetupApplyRevalidator
    {
        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }

    private sealed class CallbackApplyRevalidator(Action callback) : ISetupApplyRevalidator
    {
        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            callback();
            return SetupPlanResult.Revalidated();
        }
    }

    private sealed class EnvironmentRecoveryFixture
    {
        private readonly string priorState;
        private readonly string desiredState;
        private readonly bool secondMemberChanged;

        public EnvironmentRecoveryFixture(
            string priorState = "value",
            string desiredState = "value",
            bool secondMemberChanged = false)
        {
            this.priorState = priorState;
            this.desiredState = desiredState;
            this.secondMemberChanged = secondMemberChanged;
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 2, 3, 4, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000801");
            RecordId = Guid.Parse("00000000-0000-7000-8000-000000000802");
            Platform.SeedUserEnvironment("ENV_A", StateValue(priorState, prior: true));
            Platform.SeedUserEnvironment("ENV_B", secondMemberChanged ? "old-b" : "stable-b");
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var capture = environmentStep.Capture(["ENV_A", "ENV_B"]);
            Plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                "1.0.0",
                [new SetupPrivatePlanTarget(
                    RecordId,
                    SetupTargetKind.Env,
                    "current-user",
                    capture.AggregateHash,
                    "environment-allowlist",
                    [new SetupPrivatePlanMember(
                         "ENV_A",
                         desiredState == "missing" ? SetupOperation.Remove : SetupOperation.Replace,
                         StateValue(desiredState, prior: false)),
                     new SetupPrivatePlanMember(
                         "ENV_B",
                         secondMemberChanged ? SetupOperation.Replace : SetupOperation.NoOp,
                         secondMemberChanged ? "desired-b" : "stable-b")])]);
            Planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                    RecordId,
                    SetupTargetKind.Env,
                    "user-environment",
                    "github-copilot",
                    [new SetupLedgerMember(
                         "ENV_A",
                         desiredState == "missing" ? SetupOperation.Remove : SetupOperation.Replace),
                     new SetupLedgerMember(
                         "ENV_B",
                         secondMemberChanged ? SetupOperation.Replace : SetupOperation.NoOp)],
                    capture.AggregateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    CreateStatusProjection(
                    [
                        new SetupLedgerMember("ENV_A", desiredState == "missing" ? SetupOperation.Remove : SetupOperation.Replace),
                        new SetupLedgerMember("ENV_B", secondMemberChanged ? SetupOperation.Replace : SetupOperation.NoOp),
                    ]),
                    "1.0.0")]);
            var planStore = new SetupPlanStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            new SetupLedgerStore(Platform, Paths, planStore)
                .PersistPlannedChangeSet(setupLock.Lock!, Plan, Planned);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid RecordId { get; }
        public SetupPrivatePlan Plan { get; }
        public SetupLedgerChangeSet Planned { get; }
        public string? PriorValue => StateValue(priorState, prior: true);

        public SetupLock AcquireLock()
        {
            var result = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(result.Acquired);
            return result.Lock!;
        }

        public SetupRecoveryCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRecoveryCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return Assert.Single(new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets);
        }

        public void SeedCommittedApplyJournalAndApplyingLedger(string journalShape = "exact")
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var capture = environmentStep.Capture(["ENV_A", "ENV_B"]);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateBackup(Paths.GetBackup(ChangeSetId, RecordId), capture);
            var changedStep = new SetupJournalStep(
                "ENV_A",
                capture.Members[0].Hash,
                environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
                RecordId.ToString("D"),
                SetupJournalStepPhase.Pending);
            var noOpStep = new SetupJournalStep(
                "ENV_B",
                capture.Members[1].Hash,
                capture.Members[1].Hash,
                RecordId.ToString("D"),
                SetupJournalStepPhase.Pending);
            IReadOnlyList<SetupJournalStep> journalSteps = journalShape switch
            {
                "extra-no-op-step" => [changedStep, noOpStep],
                "missing-changed-step" => [noOpStep],
                _ => [changedStep],
            };
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Apply,
                [new SetupJournalTarget(RecordId, SetupTargetKind.Env, journalSteps)]);
            ledgerStore.Save(setupLock.Lock!, new SetupOwnershipLedger(1,
                [Planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = RecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }]));
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            foreach (var journalStep in journalSteps)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, journalStep.MemberKey,
                    SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            }
            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            foreach (var journalStep in journalSteps)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, journalStep.MemberKey,
                    SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            }
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Committed);
        }

        public void MarkLedgerAppliedWithoutCompletingNotification()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = Assert.Single(ledger.ChangeSets);
            var appliedHash = new UserEnvironmentSetupStep(Platform).Capture(["ENV_A", "ENV_B"]).AggregateHash;
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [changeSet with
                {
                    State = SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = [changeSet.Targets[0] with
                    {
                        AppliedStateHash = appliedHash,
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
        }

        public void SeedPreparedApplyJournalAndBackup()
        {
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var capture = environmentStep.Capture(["ENV_A", "ENV_B"]);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateBackup(Paths.GetBackup(ChangeSetId, RecordId), capture);
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Apply,
                [new SetupJournalTarget(RecordId, SetupTargetKind.Env,
                    [new SetupJournalStep(
                        "ENV_A",
                        capture.Members[0].Hash,
                        environmentStep.HashMember("ENV_A", DesiredValue),
                        RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
        }

        public void SeedPreparedRollbackJournalAndAppliedLedger(bool includeNoOpStep = false)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var previous = environmentStep.Capture(["ENV_A", "ENV_B"]);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateBackup(Paths.GetBackup(ChangeSetId, RecordId), previous);
            Platform.SeedUserEnvironment("ENV_A", StateValue(desiredState, prior: false));
            if (secondMemberChanged)
            {
                Platform.SeedUserEnvironment("ENV_B", "desired-b");
            }

            var applied = environmentStep.Capture(["ENV_A", "ENV_B"]);
            var steps = new List<SetupJournalStep>
            {
                new(
                    "ENV_A",
                    previous.Members[0].Hash,
                    applied.Members[0].Hash,
                    RecordId.ToString("D"),
                    SetupJournalStepPhase.Pending),
            };
            if (secondMemberChanged)
            {
                steps.Add(new SetupJournalStep(
                    "ENV_B",
                    previous.Members[1].Hash,
                    applied.Members[1].Hash,
                    RecordId.ToString("D"),
                    SetupJournalStepPhase.Pending));
            }
            else if (includeNoOpStep)
            {
                steps.Add(new SetupJournalStep(
                    "ENV_B",
                    previous.Members[1].Hash,
                    previous.Members[1].Hash,
                    RecordId.ToString("D"),
                    SetupJournalStepPhase.Pending));
            }

            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Rollback,
                [new SetupJournalTarget(RecordId, SetupTargetKind.Env,
                    steps)]);
            ledgerStore.Save(setupLock.Lock!, new SetupOwnershipLedger(1,
                [Planned with
                {
                    State = SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = [Planned.Targets[0] with
                    {
                        AppliedStateHash = applied.AggregateHash,
                        BackupReference = RecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }]));
        }

        public void SeedActiveRollback(SetupJournalStepPhase phase, string currentState)
        {
            SeedPreparedRollbackJournalAndAppliedLedger();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.RollingBack);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Assert.Single(ledger.ChangeSets) with
                {
                    State = SetupChangeSetState.RollingBack,
                }],
            });
            if (phase is SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted)
            {
                foreach (var memberKey in secondMemberChanged ? new[] { "ENV_A", "ENV_B" } : ["ENV_A"])
                {
                    journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, memberKey,
                        SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
                }
            }

            if (phase == SetupJournalStepPhase.RestoreCompleted)
            {
                foreach (var memberKey in secondMemberChanged ? new[] { "ENV_A", "ENV_B" } : ["ENV_A"])
                {
                    journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, memberKey,
                        SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
                }
            }

            Platform.SeedUserEnvironment("ENV_A", currentState switch
            {
                "prior" => StateValue(priorState, prior: true),
                "applied" => StateValue(desiredState, prior: false),
                "third" => "third-a",
                "unavailable" => StateValue(priorState, prior: true),
                _ => throw new ArgumentOutOfRangeException(nameof(currentState)),
            });
            if (secondMemberChanged)
            {
                Platform.SeedUserEnvironment("ENV_B", currentState switch
                {
                    "prior" => "old-b",
                    "applied" => "desired-b",
                    "third" => "third-b",
                    "unavailable" => "old-b",
                    _ => throw new ArgumentOutOfRangeException(nameof(currentState)),
                });
            }

            if (currentState == "unavailable")
            {
                Platform.InjectFault("environment.get:ENV_A", new IOException("private-env-rollback-read"));
            }
        }

        public void SeedForgedNoOpRollback(bool active, bool bindAppliedToBase = true)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var previous = environmentStep.Capture(["ENV_A", "ENV_B"]);
            var forgedPlan = Plan with
            {
                Targets = [Plan.Targets[0] with
                {
                    Members =
                    [
                        Plan.Targets[0].Members[0] with { Operation = SetupOperation.NoOp },
                        Plan.Targets[0].Members[1],
                    ],
                }],
            };
            planStore.Delete(setupLock.Lock!, ChangeSetId);
            planStore.Create(setupLock.Lock!, forgedPlan);
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            environmentStep.CreateBackup(Paths.GetBackup(ChangeSetId, RecordId), previous);
            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            var desired = environmentStep.Capture(["ENV_A", "ENV_B"]);
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Rollback,
                [new SetupJournalTarget(RecordId, SetupTargetKind.Env,
                    [new SetupJournalStep(
                        "ENV_A",
                        previous.Members[0].Hash,
                        desired.Members[0].Hash,
                        RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
            var forgedTarget = Planned.Targets[0] with
            {
                Members =
                [
                    Planned.Targets[0].Members[0] with { Operation = SetupOperation.NoOp },
                    Planned.Targets[0].Members[1],
                ],
                StatusProjection = CreateStatusProjection(
                [
                    Planned.Targets[0].Members[0] with { Operation = SetupOperation.NoOp },
                    Planned.Targets[0].Members[1],
                ]),
                AppliedStateHash = bindAppliedToBase ? previous.AggregateHash : desired.AggregateHash,
                BackupReference = RecordId.ToString("D"),
                OutcomeCode = SetupCodes.ApplySucceeded,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
            };
            ledgerStore.Save(setupLock.Lock!, new SetupOwnershipLedger(1,
                [Planned with
                {
                    State = active ? SetupChangeSetState.RollingBack : SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    Targets = [forgedTarget],
                }]));
            if (active)
            {
                journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.RollingBack);
            }
        }

        public void ReplaceRollbackBackupWithCurrentState()
        {
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var backupPath = Paths.GetBackup(ChangeSetId, RecordId);
            Platform.FileSystem.DeleteFile(backupPath);
            environmentStep.CreateBackup(backupPath, environmentStep.Capture(["ENV_A", "ENV_B"]));
        }

        public void ReplaceRollbackAppliedHash(string appliedHash)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [changeSet with
                {
                    Targets = [Assert.Single(changeSet.Targets) with { AppliedStateHash = appliedHash }],
                }],
            });
        }

        public void ReplaceRollbackPreviousHash(string previousHash)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = Assert.Single(ledger.ChangeSets);
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [changeSet with
                {
                    Targets = [Assert.Single(changeSet.Targets) with { PreviousStateHash = previousHash }],
                }],
            });
        }

        public void SeedApplyingJournalAndLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Applying,
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = RecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
        }

        public void SeedActiveApply(SetupJournalStepPhase phase, string currentState)
        {
            SeedApplyingJournalAndLedger();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            if (phase != SetupJournalStepPhase.Pending)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                    SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            }

            if (phase is SetupJournalStepPhase.MutationCompleted or SetupJournalStepPhase.RestoreStarted or
                SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                    SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            }

            if (phase is SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Compensating);
                var ledger = ledgerStore.LoadForRecovery();
                ledgerStore.Save(setupLock.Lock!, ledger with
                {
                    ChangeSets = [Assert.Single(ledger.ChangeSets) with
                    {
                        State = SetupChangeSetState.Compensating,
                    }],
                });
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                    SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted);
            }

            if (phase == SetupJournalStepPhase.RestoreCompleted)
            {
                journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                    SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            }

            Platform.SeedUserEnvironment("ENV_A", currentState switch
            {
                "prior" => StateValue(priorState, prior: true),
                "desired" => StateValue(desiredState, prior: false),
                "third" => "third-a",
                "unavailable" => StateValue(priorState, prior: true),
                _ => throw new ArgumentOutOfRangeException(nameof(currentState)),
            });
            if (currentState == "unavailable")
            {
                Platform.InjectFault("environment.get:ENV_A", new IOException("private-env-read"));
            }
        }

        public void ReplaceActiveEvidenceWithCoherentBackupFromDifferentBase()
        {
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            Platform.SeedUserEnvironment("ENV_A", "tampered-a");
            Platform.SeedUserEnvironment("ENV_B", "tampered-b");
            var tampered = environmentStep.Capture(["ENV_A", "ENV_B"]);
            var backupPath = Paths.GetBackup(ChangeSetId, RecordId);
            Platform.FileSystem.DeleteFile(backupPath);
            environmentStep.CreateBackup(backupPath, tampered);
            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            Platform.SeedUserEnvironment("ENV_B", "stable-b");

            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            Platform.FileSystem.DeleteFile(Paths.GetTransactionJournal(ChangeSetId));
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            journalStore.CreatePrepared(
                setupLock.Lock!,
                ChangeSetId,
                SetupJournalOperation.Apply,
                [new SetupJournalTarget(
                    RecordId,
                    SetupTargetKind.Env,
                    [new SetupJournalStep(
                         "ENV_A",
                         tampered.Members[0].Hash,
                         environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
                         RecordId.ToString("D"),
                         SetupJournalStepPhase.Pending),
                     new SetupJournalStep(
                         "ENV_B",
                         tampered.Members[1].Hash,
                         environmentStep.HashMember("ENV_B", UserEnvironmentValue.Present("stable-b")),
                         RecordId.ToString("D"),
                         SetupJournalStepPhase.Pending)])]);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            foreach (var memberKey in new[] { "ENV_A", "ENV_B" })
            {
                journalStore.MarkStepPhase(
                    setupLock.Lock!,
                    ChangeSetId,
                    RecordId,
                    memberKey,
                    SetupJournalStepPhase.Pending,
                    SetupJournalStepPhase.MutationStarted);
                journalStore.MarkStepPhase(
                    setupLock.Lock!,
                    ChangeSetId,
                    RecordId,
                    memberKey,
                    SetupJournalStepPhase.MutationStarted,
                    SetupJournalStepPhase.MutationCompleted);
            }
        }

        public void SeedRestoredApplyJournalAndCompensatingLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.Compensating,
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = RecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            Platform.SeedUserEnvironment("ENV_A", "desired-a");
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Compensating);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted);
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Restored);
        }

        public void SeedCommittedRollbackJournalAndRolledBackLedger()
        {
            SeedPreparedApplyJournalAndBackup();
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            Assert.True(setupLock.Acquired);
            Platform.FileSystem.DeleteFile(Paths.GetTransactionJournal(ChangeSetId));
            var previous = environmentStep.Capture(["ENV_A", "ENV_B"]);
            var desiredA = environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a"));
            journalStore.CreatePrepared(setupLock.Lock!, ChangeSetId, SetupJournalOperation.Rollback,
                [new SetupJournalTarget(RecordId, SetupTargetKind.Env,
                    [new SetupJournalStep("ENV_A", previous.Members[0].Hash, desiredA,
                        RecordId.ToString("D"), SetupJournalStepPhase.Pending)])]);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.RollingBack);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            journalStore.MarkStepPhase(setupLock.Lock!, ChangeSetId, RecordId, "ENV_A",
                SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Committed);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [Planned with
                {
                    State = SetupChangeSetState.RolledBack,
                    OutcomeCode = SetupCodes.RollbackSucceeded,
                    Targets = [Planned.Targets[0] with
                    {
                        AppliedStateHash = null,
                        BackupReference = null,
                        OutcomeCode = SetupCodes.RollbackSucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                    }],
                }],
            });
        }

        public string NextTemporaryPath(string destination, int offset)
        {
            var maximum = Platform.Operations
                .SelectMany(operation => Regex.Matches(operation,
                    @"\.cao-00000000-0000-7000-8000-(?<value>[0-9]{12})\.tmp"))
                .Select(match => long.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            return destination + ".cao-" +
                Guid.Parse($"00000000-0000-7000-8000-{maximum + offset + 1:D12}").ToString("D") + ".tmp";
        }

        private UserEnvironmentValue DesiredValue => desiredState == "missing"
            ? UserEnvironmentValue.Missing
            : UserEnvironmentValue.Present(StateValue(desiredState, prior: false)!);

        private static string? StateValue(string state, bool prior) => state switch
        {
            "missing" => null,
            "empty" => string.Empty,
            "value" => prior ? "old-a" : "desired-a",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}

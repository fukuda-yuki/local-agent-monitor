using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupRecoveryTests
{
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
    public void RecoverNext_Notification_only_does_not_require_private_plan_or_target_read()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedCommittedApplyJournalAndApplyingLedger();
        fixture.MarkLedgerAppliedWithoutCompletingNotification();
        fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetPlan(fixture.ChangeSetId));
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.get", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("applying", SetupRecoveryOperation.Apply)]
    [InlineData("partial", SetupRecoveryOperation.Apply)]
    [InlineData("rollback", SetupRecoveryOperation.Rollback)]
    public void RecoverNext_Active_and_rollback_handoffs_are_explicit_fixed_failures(
        string variant,
        SetupRecoveryOperation expectedOperation)
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        if (variant == "applying")
        {
            fixture.SeedApplyingJournalAndLedger();
        }
        else if (variant == "partial")
        {
            fixture.SeedPreparedApplyJournalAndBackup();
            fixture.Platform.FileSystem.DeleteFile(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId));
            using var firstLock = fixture.AcquireLock();
            _ = fixture.ReopenCoordinator().RecoverNext(firstLock);
        }
        else
        {
            fixture.SeedPreparedRollbackJournalAndAppliedLedger();
        }

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(expectedOperation, result.Operation);
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
            var planStore = new SetupPlanStore(Platform, Paths);
            return Assert.Single(new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets);
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

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
        private RecoveryFixture()
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000501");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000502");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));

            Plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                [new SetupPrivatePlanTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    TargetPath,
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                    "new",
                    [new SetupPrivatePlanMember("setting", SetupOperation.Replace, "new")])]);
            Planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
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
                    Plan.Targets[0].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    "1.0.0")]);

            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.PersistPlannedChangeSet(setupLock.Lock!, Plan, Planned);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid FileRecordId { get; }
        public string TargetPath { get; }
        public SetupPrivatePlan Plan { get; }
        public SetupLedgerChangeSet Planned { get; }
        public string DesiredFileHash => SetupHash.File(true, Encoding.UTF8.GetBytes("new"));
        public FileChangeSetSeed PrimarySeed => new(Plan, Planned, TargetPath, FileRecordId);

        public static RecoveryFixture CreateFileOnly() => new();

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
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = FileRecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
                }],
            });
            journalStore.MarkTransactionPhase(setupLock.Lock!, ChangeSetId, SetupJournalPhase.Applying);
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

    private sealed class EnvironmentRecoveryFixture
    {
        public EnvironmentRecoveryFixture()
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 2, 3, 4, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000801");
            RecordId = Guid.Parse("00000000-0000-7000-8000-000000000802");
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "stable-b");
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
                    [new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a"),
                     new SetupPrivatePlanMember("ENV_B", SetupOperation.NoOp, "stable-b")])]);
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
                    [new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                     new SetupLedgerMember("ENV_B", SetupOperation.NoOp)],
                    capture.AggregateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
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
                        environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
                        RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)])]);
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
    }
}

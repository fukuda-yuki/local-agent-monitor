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
        Assert.Equal(afterEffect ? "old" : "new",
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
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
    public void RecoverNext_Rollback_handoff_remains_an_explicit_fixed_failure()
    {
        var fixture = RecoveryFixture.CreateFileOnly();
        fixture.SeedPreparedRollbackJournalAndAppliedLedger();

        using var setupLock = fixture.AcquireLock();
        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Operation);
    }

    [Fact]
    public void RecoverNext_Active_environment_apply_remains_an_explicit_fixed_handoff()
    {
        var fixture = new EnvironmentRecoveryFixture();
        fixture.SeedApplyingJournalAndLedger();
        var operationsBefore = fixture.Platform.Operations.Count;
        using var setupLock = fixture.AcquireLock();

        var result = fixture.ReopenCoordinator().RecoverNext(setupLock);

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.Operation);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("stable-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("environment.set", StringComparison.Ordinal) ||
            operation == "environment.notify");
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
        private RecoveryFixture(int fileTargetCount = 1)
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
            foreach (var targetPath in TargetPaths)
            {
                Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old"));
            }

            Plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                FileRecordIds.Select((recordId, index) => new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Json,
                    TargetPaths[index],
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                    "new",
                    [new SetupPrivatePlanMember(index == 0 ? "setting" : $"setting-{index}", SetupOperation.Replace, "new")])).ToArray());
            Planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                FileRecordIds.Select((recordId, index) => new SetupLedgerTarget(
                    recordId,
                    SetupTargetKind.Json,
                    index == 0 ? "settings" : $"settings-{index}",
                    "github-copilot",
                    [new SetupLedgerMember(index == 0 ? "setting" : $"setting-{index}", SetupOperation.Replace)],
                    Plan.Targets[index].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    "1.0.0")).ToArray());

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
                    Targets = [Planned.Targets[0] with
                    {
                        BackupReference = FileRecordId.ToString("D"),
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }],
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

using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed record SetupRollbackExecutionResult(
    Guid RequestedChangeSetId,
    bool Success,
    string Code,
    SetupLedgerChangeSet? ChangeSet,
    SetupRecoveryResult? Recovery);

internal sealed class SetupRollbackCoordinator
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupTransactionJournalStore journalStore;
    private readonly SetupRecoveryCoordinator recoveryCoordinator;
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupRollbackCoordinator(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore)
    {
        this.platform = platform;
        this.paths = paths;
        this.planStore = planStore;
        this.ledgerStore = ledgerStore;
        this.journalStore = journalStore;
        recoveryCoordinator = new SetupRecoveryCoordinator(platform, paths, planStore, ledgerStore, journalStore);
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public SetupRollbackExecutionResult Rollback(SetupLock setupLock, Guid changeSetId)
    {
        ArgumentNullException.ThrowIfNull(setupLock);
        return setupLock.ExecuteWhileHeld(platform, paths, () => RollbackCore(setupLock, changeSetId));
    }

    private SetupRollbackExecutionResult RollbackCore(SetupLock setupLock, Guid changeSetId)
    {
        var priorRecovery = recoveryCoordinator.RecoverNext(setupLock);
        if (priorRecovery.Disposition != SetupRecoveryDisposition.None)
        {
            return new SetupRollbackExecutionResult(
                changeSetId,
                priorRecovery.Disposition == SetupRecoveryDisposition.Recovered,
                priorRecovery.Code ?? SetupCodes.InternalError,
                null,
                priorRecovery);
        }

        SetupPrivatePlan? plan;
        SetupOwnershipLedger ledger;
        SetupTransactionJournal? applyJournal;
        SetupLedgerChangeSet changeSet;
        try
        {
            plan = planStore.Load(changeSetId);
            ledger = ledgerStore.LoadForRecovery();
            changeSet = ledger.ChangeSets.Single(item => item.ChangeSetId == changeSetId);
            applyJournal = journalStore.Load(changeSetId);
        }
        catch (InvalidOperationException)
        {
            return Result(changeSetId, false, SetupCodes.InvalidArguments);
        }
        catch (Exception)
        {
            return Result(changeSetId, false, SetupCodes.RecoveryRequired);
        }

        if (plan is null)
        {
            return Result(changeSetId, false, SetupCodes.RecoveryRequired);
        }

        if (changeSet.State != SetupChangeSetState.Applied)
        {
            return PersistAttemptOutcome(setupLock, ledger, changeSet, SetupCodes.RollbackNotAvailable);
        }

        var physicalTargets = plan.Targets
            .Where(target => target.TargetKind != SetupTargetKind.Guidance)
            .ToArray();
        if (physicalTargets.Count(target => target.TargetKind == SetupTargetKind.Env) > 1)
        {
            return PersistAttemptOutcome(
                setupLock, ledger, changeSet, SetupCodes.RollbackNotAvailable);
        }

        IReadOnlyList<SetupJournalTarget> rollbackTargets;
        try
        {
            rollbackTargets = ValidatePreflight(plan, changeSet, applyJournal);
        }
        catch (SetupRollbackStaleException)
        {
            return PersistAttemptOutcome(setupLock, ledger, changeSet, SetupCodes.RollbackStale);
        }
        catch (SetupFileStepException exception)
        {
            return Result(changeSetId, false,
                exception.Code == SetupCodes.UnsafePath ? SetupCodes.UnsafePath : SetupCodes.InternalError,
                changeSet);
        }
        catch (SetupEnvironmentStepException)
        {
            return Result(changeSetId, false, SetupCodes.InternalError, changeSet);
        }
        catch (Exception)
        {
            return Result(changeSetId, false, SetupCodes.RecoveryRequired, changeSet);
        }

        try
        {
            journalStore.SupersedeWithPreparedRollback(setupLock, changeSetId, rollbackTargets);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterJournalPreparedBeforeLedger);
            var rollingBack = changeSet with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = null,
                State = SetupChangeSetState.RollingBack,
                Targets = changeSet.Targets.Select(target => target.AppliedStateHash is not null
                    ? target with { OutcomeCode = null, RollbackStatus = SetupLedgerRollbackStatus.Pending }
                    : target with { OutcomeCode = null, RollbackStatus = SetupLedgerRollbackStatus.NotAvailable }).ToArray(),
            };
            SaveChangeSet(setupLock, ledger, rollingBack);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent);
        }
        catch (Exception)
        {
            return Result(changeSetId, false, SetupCodes.RecoveryRequired, changeSet);
        }

        var execution = recoveryCoordinator.CompleteRequestedRollback(setupLock, changeSetId);
        if (execution.Disposition == SetupRecoveryDisposition.Recovered &&
            execution.Operation == SetupRecoveryOperation.Rollback &&
            execution.RecoveredChangeSetId == changeSetId &&
            execution.EffectiveChangeSet is not null)
        {
            return Result(changeSetId, true, SetupCodes.RollbackSucceeded, execution.EffectiveChangeSet);
        }

        if (execution.Disposition == SetupRecoveryDisposition.Failed &&
            execution.Code == SetupCodes.PartialRollback &&
            execution.RecoveredChangeSetId == changeSetId &&
            execution.EffectiveChangeSet?.State == SetupChangeSetState.Partial)
        {
            return Result(changeSetId, false, SetupCodes.PartialRollback, execution.EffectiveChangeSet);
        }

        return Result(
            changeSetId,
            false,
            execution.Code ?? SetupCodes.RecoveryRequired,
            execution.EffectiveChangeSet);
    }

    private IReadOnlyList<SetupJournalTarget> ValidatePreflight(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal? journal)
    {
        SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        if (journal is null || journal.ChangeSetId != changeSet.ChangeSetId)
        {
            throw new FormatException();
        }

        var terminalApply = journal.Operation == SetupJournalOperation.Apply &&
            journal.Phase == SetupJournalPhase.Committed;
        var dormantRollback = journal.Operation == SetupJournalOperation.Rollback &&
            journal.Phase == SetupJournalPhase.Prepared;
        if (!terminalApply && !dormantRollback)
        {
            throw new FormatException();
        }

        var hasOwnedEnvironmentTarget = journal.Targets.Any(
            target => target.TargetKind == SetupTargetKind.Env);
        var expectedNotification = terminalApply && hasOwnedEnvironmentTarget
            ? SetupEnvironmentNotification.Completed
            : SetupEnvironmentNotification.NotRequired;
        if (journal.EnvironmentNotification != expectedNotification)
        {
            throw new FormatException();
        }

        var changedTargets = changeSet.Targets
            .Where(target => target.TargetKind != SetupTargetKind.Guidance &&
                target.Members.Any(member => member.Operation != SetupOperation.NoOp))
            .ToArray();
        if (changedTargets.Length == 0 || changedTargets.Length != journal.Targets.Count ||
            changedTargets.Where((target, index) =>
                target.RecordId != journal.Targets[index].RecordId ||
                target.TargetKind != journal.Targets[index].TargetKind).Any() ||
            changeSet.Targets.Any(target => changedTargets.Contains(target)
                ? target.AppliedStateHash is null ||
                  target.BackupReference is null ||
                  target.RollbackStatus != SetupLedgerRollbackStatus.Pending
                : !HasNoOwnership(target)))
        {
            throw new FormatException();
        }

        for (var index = 0; index < plan.Targets.Count; index++)
        {
            var planTarget = plan.Targets[index];
            var ledgerTarget = changeSet.Targets[index];
            if (planTarget.TargetKind == SetupTargetKind.Guidance)
            {
                if (!HasNoOwnership(ledgerTarget))
                {
                    throw new FormatException();
                }

                continue;
            }

            if (planTarget.TargetKind == SetupTargetKind.Env)
            {
                if (HasNoOwnership(ledgerTarget))
                {
                    ValidateUnownedEnvironmentPreflight(planTarget, ledgerTarget, journal);
                }
                else
                {
                    ValidateEnvironmentPreflight(
                        plan.ChangeSetId,
                        planTarget,
                        ledgerTarget,
                        journal,
                        terminalApply);
                }
                continue;
            }

            var current = fileStep.Capture(GetAllowedRoot(planTarget.TargetLocation), planTarget.TargetLocation);
            if (ledgerTarget.AppliedStateHash is null)
            {
                if (!HasNoOwnership(ledgerTarget) || !string.Equals(current.Hash, planTarget.BaseStateHash, StringComparison.Ordinal))
                {
                    throw new SetupRollbackStaleException();
                }

                continue;
            }

            var journalTarget = journal.Targets.SingleOrDefault(target => target.RecordId == planTarget.RecordId);
            if (journalTarget is null ||
                journalTarget.TargetKind != planTarget.TargetKind ||
                journalTarget.Steps.Count != 1 ||
                journalTarget.Steps[0].MemberKey is not null ||
                journalTarget.Steps[0].Phase != (terminalApply
                    ? SetupJournalStepPhase.MutationCompleted
                    : SetupJournalStepPhase.Pending) ||
                !string.Equals(journalTarget.Steps[0].PriorStateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
                !string.Equals(journalTarget.Steps[0].DesiredStateHash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal) ||
                !string.Equals(journalTarget.Steps[0].DesiredStateHash,
                    SetupHash.File(true, Encoding.UTF8.GetBytes(planTarget.DesiredState)), StringComparison.Ordinal) ||
                !string.Equals(journalTarget.Steps[0].BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
                !string.Equals(ledgerTarget.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
                ledgerTarget.RollbackStatus != SetupLedgerRollbackStatus.Pending)
            {
                throw new FormatException();
            }

            fileStep.ValidateBackup(paths.GetBackup(changeSet.ChangeSetId, planTarget.RecordId), planTarget.BaseStateHash);
            if (!string.Equals(current.Hash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal))
            {
                throw new SetupRollbackStaleException();
            }
        }

        return journal.Targets.Select(target => target with
        {
            Steps = target.Steps.Select(step => step with { Phase = SetupJournalStepPhase.Pending }).ToArray(),
        }).ToArray();
    }

    private void ValidateUnownedEnvironmentPreflight(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupTransactionJournal journal)
    {
        if (planTarget.Members.Any(member => member.Operation != SetupOperation.NoOp) ||
            journal.Targets.Any(target => target.RecordId == planTarget.RecordId))
        {
            throw new FormatException();
        }

        var names = planTarget.Members.Select(member => member.SettingKey).ToArray();
        var current = environmentStep.Capture(names);
        for (var index = 0; index < planTarget.Members.Count; index++)
        {
            var member = planTarget.Members[index];
            var desiredHash = environmentStep.HashMember(
                member.SettingKey,
                DesiredEnvironmentValue(member));
            if (!string.Equals(current.Members[index].Hash, desiredHash, StringComparison.Ordinal))
            {
                throw new SetupRollbackStaleException();
            }
        }

        if (!string.Equals(current.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
            !string.Equals(current.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal))
        {
            throw new FormatException();
        }
    }

    private void ValidateEnvironmentPreflight(
        Guid changeSetId,
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupTransactionJournal journal,
        bool terminalApply)
    {
        if (ledgerTarget.AppliedStateHash is null ||
            !string.Equals(ledgerTarget.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
            ledgerTarget.RollbackStatus != SetupLedgerRollbackStatus.Pending)
        {
            throw new FormatException();
        }

        var names = planTarget.Members.Select(member => member.SettingKey).ToArray();
        var backup = environmentStep.ReadBackup(paths.GetBackup(changeSetId, planTarget.RecordId), names);
        if (!string.Equals(backup.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
            !string.Equals(backup.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal))
        {
            throw new FormatException();
        }

        var current = environmentStep.Capture(names);
        if (!string.Equals(current.AggregateHash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal))
        {
            throw new SetupRollbackStaleException();
        }

        var journalTarget = journal.Targets.SingleOrDefault(target => target.RecordId == planTarget.RecordId);
        var changedMembers = planTarget.Members
            .Where(member => member.Operation != SetupOperation.NoOp)
            .ToArray();
        if (journalTarget is null ||
            journalTarget.TargetKind != SetupTargetKind.Env ||
            journalTarget.Steps.Count != changedMembers.Length)
        {
            throw new FormatException();
        }

        var stepIndex = 0;
        for (var memberIndex = 0; memberIndex < planTarget.Members.Count; memberIndex++)
        {
            var member = planTarget.Members[memberIndex];
            var desiredHash = environmentStep.HashMember(
                member.SettingKey,
                DesiredEnvironmentValue(member));
            if (!string.Equals(current.Members[memberIndex].Hash, desiredHash, StringComparison.Ordinal))
            {
                throw new SetupRollbackStaleException();
            }

            if (member.Operation == SetupOperation.NoOp)
            {
                if (!string.Equals(backup.Members[memberIndex].Hash, desiredHash, StringComparison.Ordinal))
                {
                    throw new FormatException();
                }

                continue;
            }

            var step = journalTarget.Steps[stepIndex++];
            if (!string.Equals(step.MemberKey, member.SettingKey, StringComparison.Ordinal) ||
                !string.Equals(step.PriorStateHash, backup.Members[memberIndex].Hash, StringComparison.Ordinal) ||
                !string.Equals(step.DesiredStateHash, desiredHash, StringComparison.Ordinal) ||
                !string.Equals(step.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
                step.Phase != (terminalApply
                    ? SetupJournalStepPhase.MutationCompleted
                    : SetupJournalStepPhase.Pending))
            {
                throw new FormatException();
            }
        }
    }

    private SetupRollbackExecutionResult PersistAttemptOutcome(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        string code)
    {
        var attempted = changeSet with { UpdatedAt = platform.Clock.UtcNow, OutcomeCode = code };
        try
        {
            SaveChangeSet(setupLock, ledger, attempted);
            return Result(changeSet.ChangeSetId, false, code, attempted);
        }
        catch (Exception)
        {
            return Result(changeSet.ChangeSetId, false, SetupCodes.InternalError, changeSet);
        }
    }

    private void SaveChangeSet(SetupLock setupLock, SetupOwnershipLedger ledger, SetupLedgerChangeSet changeSet)
    {
        var changeSets = ledger.ChangeSets.ToArray();
        var index = Array.FindIndex(changeSets, item => item.ChangeSetId == changeSet.ChangeSetId);
        if (index < 0)
        {
            throw new InvalidOperationException();
        }

        changeSets[index] = changeSet;
        ledgerStore.Save(setupLock, ledger with { ChangeSets = changeSets });
    }

    private static bool HasNoOwnership(SetupLedgerTarget target) =>
        target.AppliedStateHash is null &&
        target.BackupReference is null &&
        target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable;

    private static string GetAllowedRoot(string targetPath) =>
        Path.GetDirectoryName(targetPath) ?? throw new FormatException();

    private static UserEnvironmentValue DesiredEnvironmentValue(SetupPrivatePlanMember member) =>
        member.Operation == SetupOperation.Remove
            ? UserEnvironmentValue.Missing
            : UserEnvironmentValue.Present(member.DesiredValue!);

    private static SetupRollbackExecutionResult Result(
        Guid changeSetId,
        bool success,
        string code,
        SetupLedgerChangeSet? changeSet = null) =>
        new(changeSetId, success, code, changeSet, null);

    private sealed class SetupRollbackStaleException : Exception;
}

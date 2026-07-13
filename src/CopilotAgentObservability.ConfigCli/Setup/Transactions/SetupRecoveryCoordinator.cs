using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal enum SetupRecoveryDisposition
{
    None,
    Recovered,
    Failed,
}

internal sealed record SetupRecoveryResult(
    SetupRecoveryDisposition Disposition,
    string? Code,
    Guid? RecoveredChangeSetId,
    SetupRecoveryOperation? Operation,
    SetupLedgerChangeSet? EffectiveChangeSet);

internal sealed class SetupRecoveryCoordinator
{
    private static readonly byte[] FileBackupMagic = Encoding.ASCII.GetBytes("CAOSETUP1");
    private static readonly byte[] EnvironmentAggregateHashDomain =
        Encoding.ASCII.GetBytes("CAO-USER-ENV-AGGREGATE\0");
    private static readonly Encoding EnvironmentCanonicalEncoding =
        new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private enum ActiveFileClassification
    {
        Prior,
        Desired,
        ThirdParty,
        Unavailable,
    }

    private enum ActiveFileStepOutcome
    {
        Completed,
        Stale,
        Unavailable,
        JournalUnproven,
    }

    private enum ActiveFileFailureKind
    {
        Stale,
        Unavailable,
    }

    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupTransactionJournalStore journalStore;
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupRecoveryCoordinator(
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
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public SetupRecoveryResult RecoverNext(SetupLock setupLock)
    {
        ArgumentNullException.ThrowIfNull(setupLock);
        return setupLock.ExecuteWhileHeld(platform, paths, () => RecoverNextCore(setupLock, null));
    }

    internal SetupRecoveryResult CompleteRequestedRollback(SetupLock setupLock, Guid changeSetId)
    {
        ArgumentNullException.ThrowIfNull(setupLock);
        return setupLock.ExecuteWhileHeld(
            platform, paths, () => RecoverNextCore(setupLock, changeSetId));
    }

    private SetupRecoveryResult RecoverNextCore(SetupLock setupLock, Guid? normalRollbackChangeSetId)
    {
        SetupOwnershipLedger ledger;
        try
        {
            ledger = ledgerStore.LoadForRecovery();
        }
        catch (SetupStorageException exception)
        {
            return Failed(exception.Code, null, null, null);
        }

        foreach (var changeSet in ledger.ChangeSets
                     .OrderBy(item => item.CreatedAt)
                     .ThenBy(item => item.ChangeSetId.ToString("D"), StringComparer.Ordinal))
        {
            SetupTransactionJournal? journal;
            try
            {
                journal = LoadJournalForRecovery(changeSet.ChangeSetId);
            }
            catch (SetupStorageException)
            {
                if (IsTerminalLedgerState(changeSet.State))
                {
                    return PersistFailure(
                        setupLock,
                        ledger,
                        changeSet,
                        OperationFor(changeSet)!.Value,
                        preserveTerminalLifecycle: true);
                }

                return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId, OperationFor(changeSet),
                    OverlayFailure(changeSet));
            }

            if (journal is not null && IsTerminalJournal(journal) &&
                !TerminalEvidenceMatches(changeSet, journal))
            {
                return PersistFailure(
                    setupLock,
                    ledger,
                    changeSet,
                    OperationFor(journal, changeSet)!.Value,
                    preserveTerminalLifecycle: IsTerminalLedgerState(changeSet.State));
            }

            if (journal is not null && IsMatchingTerminal(changeSet, journal))
            {
                if (journal.EnvironmentNotification == SetupEnvironmentNotification.Pending)
                {
                    return RecoverNotificationOnly(setupLock, ledger, changeSet, journal);
                }

                continue;
            }

            if (changeSet.State == SetupChangeSetState.NoChanges && journal is null)
            {
                continue;
            }

            SetupPrivatePlan? plan;
            try
            {
                plan = LoadPlanForRecovery(changeSet.ChangeSetId);
            }
            catch (SetupStorageException exception)
            {
                if (IsTerminalApplyJournal(journal))
                {
                    return PersistFailure(setupLock, ledger, changeSet, SetupRecoveryOperation.Apply);
                }

                return Failed(exception.Code, changeSet.ChangeSetId, OperationFor(journal, changeSet),
                    OverlayFailure(changeSet));
            }

            if (changeSet.State == SetupChangeSetState.Planned && journal is null && plan is not null)
            {
                continue;
            }

            if (plan is null)
            {
                if (IsTerminalApplyJournal(journal))
                {
                    return PersistFailure(setupLock, ledger, changeSet, SetupRecoveryOperation.Apply);
                }

                return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                    OperationFor(journal, changeSet), OverlayFailure(changeSet));
            }

            if (journal is null)
            {
                return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId, OperationFor(changeSet),
                    OverlayFailure(changeSet));
            }

            if (changeSet.State == SetupChangeSetState.Planned &&
                journal.Operation == SetupJournalOperation.Apply &&
                journal.Phase == SetupJournalPhase.Prepared)
            {
                try
                {
                    ValidateDormantPreparedApply(plan, changeSet, journal);
                    continue;
                }
                catch (Exception)
                {
                    return PersistFailure(setupLock, ledger, changeSet, SetupRecoveryOperation.Apply);
                }
            }

            if (journal.Operation == SetupJournalOperation.Rollback &&
                journal.Phase == SetupJournalPhase.Prepared &&
                changeSet.State == SetupChangeSetState.Applied)
            {
                if (plan.Targets.Any(target => target.TargetKind == SetupTargetKind.Env) &&
                    !HasSingleEnvironmentPhysicalTarget(plan))
                {
                    return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                        SetupRecoveryOperation.Rollback, OverlayFailure(changeSet));
                }

                try
                {
                    ValidateDormantPreparedRollback(plan, changeSet, journal);
                    continue;
                }
                catch (Exception)
                {
                    return PersistActiveFileRollbackFailure(
                        setupLock,
                        changeSet,
                        journal,
                        new Dictionary<Guid, ActiveFileFailureKind>());
                }
            }

            if (journal.Operation == SetupJournalOperation.Rollback)
            {
                if (plan.Targets.Any(target => target.TargetKind == SetupTargetKind.Env) &&
                    !HasSingleEnvironmentPhysicalTarget(plan))
                {
                    return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                        SetupRecoveryOperation.Rollback, OverlayFailure(changeSet));
                }

                if (IsActiveRollback(journal))
                {
                    return RecoverActiveFileRollback(
                        setupLock,
                        changeSet,
                        plan,
                        journal,
                        normalRollbackChangeSetId == changeSet.ChangeSetId);
                }

                if (journal.Phase == SetupJournalPhase.Committed &&
                    changeSet.State != SetupChangeSetState.RolledBack)
                {
                    return ReconcileCommittedFileRollback(setupLock, changeSet, plan, journal);
                }
            }

            if (IsActiveApply(journal))
            {
                return RecoverActiveApply(setupLock, changeSet, plan, journal);
            }

            if (journal.Operation == SetupJournalOperation.Apply &&
                journal.Phase == SetupJournalPhase.Committed &&
                changeSet.State != SetupChangeSetState.Applied)
            {
                return ReconcileCommittedApply(setupLock, ledger, changeSet, plan, journal);
            }

            if (journal.Operation == SetupJournalOperation.Apply &&
                journal.Phase == SetupJournalPhase.Restored &&
                changeSet.State != SetupChangeSetState.Restored)
            {
                return ReconcileRestoredApply(setupLock, ledger, changeSet, plan, journal);
            }

            return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                OperationFor(journal, changeSet), OverlayFailure(changeSet));
        }

        return new SetupRecoveryResult(SetupRecoveryDisposition.None, null, null, null, null);
    }

    private void ValidateDormantPreparedRollback(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        if (journal.ChangeSetId != changeSet.ChangeSetId ||
            journal.Operation != SetupJournalOperation.Rollback ||
            journal.Phase != SetupJournalPhase.Prepared ||
            journal.EnvironmentNotification != SetupEnvironmentNotification.NotRequired ||
            journal.Targets.SelectMany(target => target.Steps)
                .Any(step => step.Phase != SetupJournalStepPhase.Pending))
        {
            throw new FormatException();
        }

        var terminalHashes = VerifyTerminalState(plan, changeSet, journal, desired: true);
        foreach (var target in changeSet.Targets)
        {
            var journalTarget = journal.Targets.SingleOrDefault(item => item.RecordId == target.RecordId);
            if (journalTarget is not null)
            {
                if (!string.Equals(
                        target.AppliedStateHash,
                        terminalHashes[target.RecordId],
                        StringComparison.Ordinal) ||
                    !string.Equals(target.BackupReference, target.RecordId.ToString("D"), StringComparison.Ordinal) ||
                    target.RollbackStatus != SetupLedgerRollbackStatus.Pending)
                {
                    throw new FormatException();
                }
            }
            else if (!HasNoRollbackOwnership(target))
            {
                throw new FormatException();
            }
        }
    }

    private SetupRecoveryResult RecoverActiveFileRollback(
        SetupLock setupLock,
        SetupLedgerChangeSet changeSet,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal,
        bool normalRollback)
    {
        if (!ActiveFileRollbackLifecycleMatches(changeSet.State, journal.Phase))
        {
            return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                SetupRecoveryOperation.Rollback, OverlayFailure(changeSet));
        }

        try
        {
            ValidateActiveFileRollbackEvidence(plan, changeSet, journal);
        }
        catch (Exception)
        {
            return PersistActiveFileRollbackFailure(
                setupLock,
                changeSet,
                journal,
                new Dictionary<Guid, ActiveFileFailureKind>(),
                normalRollback);
        }

        if (!TryEnterActiveFileRollback(setupLock, changeSet.ChangeSetId, journal.Phase) ||
            !TryPersistRollingBackLedger(setupLock, changeSet.ChangeSetId))
        {
            return ActiveFileRollbackRecoveryRequired(changeSet);
        }

        var failures = new Dictionary<Guid, ActiveFileFailureKind>();
        for (var targetIndex = journal.Targets.Count - 1; targetIndex >= 0; targetIndex--)
        {
            var journalTarget = journal.Targets[targetIndex];
            var planTarget = plan.Targets.Single(target => target.RecordId == journalTarget.RecordId);
            for (var stepIndex = journalTarget.Steps.Count - 1; stepIndex >= 0; stepIndex--)
            {
                SetupJournalStep step;
                try
                {
                    step = LoadActiveStep(
                        changeSet.ChangeSetId,
                        journalTarget.RecordId,
                        journalTarget.Steps[stepIndex].MemberKey);
                }
                catch (Exception)
                {
                    return ActiveFileRollbackRecoveryRequired(changeSet);
                }

                var outcome = RecoverActiveRollbackStep(
                    setupLock,
                    changeSet.ChangeSetId,
                    planTarget,
                    step);
                if (outcome == ActiveFileStepOutcome.JournalUnproven)
                {
                    return ActiveFileRollbackRecoveryRequired(changeSet);
                }

                if (outcome == ActiveFileStepOutcome.Stale)
                {
                    failures[journalTarget.RecordId] = ActiveFileFailureKind.Stale;
                }
                else if (outcome == ActiveFileStepOutcome.Unavailable)
                {
                    failures[journalTarget.RecordId] = ActiveFileFailureKind.Unavailable;
                }
            }
        }

        try
        {
            platform.Execution.Checkpoint(SetupFaultPoint.AfterCompletionBeforeCommit);
        }
        catch (Exception)
        {
        }

        foreach (var target in plan.Targets.Where(target => target.TargetKind != SetupTargetKind.Guidance))
        {
            var classification = ClassifyAggregatePrior(target);
            if (classification != ActiveFileClassification.Prior)
            {
                failures[target.RecordId] = classification == ActiveFileClassification.ThirdParty
                    ? ActiveFileFailureKind.Stale
                    : ActiveFileFailureKind.Unavailable;
            }
        }

        if (failures.Count > 0)
        {
            return PersistActiveFileRollbackFailure(
                setupLock, changeSet, journal, failures, normalRollback);
        }

        if (!TryMarkJournalPhaseOrConfirm(
                setupLock,
                changeSet.ChangeSetId,
                SetupJournalPhase.RollingBack,
                SetupJournalPhase.Committed))
        {
            return ActiveFileRollbackRecoveryRequired(changeSet);
        }

        return PersistRecoveredFileRollback(setupLock, changeSet, plan, journal, normalRollback);
    }

    private void ValidateActiveFileRollbackEvidence(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        if (journal.ChangeSetId != changeSet.ChangeSetId ||
            journal.Operation != SetupJournalOperation.Rollback ||
            journal.EnvironmentNotification != ExpectedActiveNotification(journal))
        {
            throw new FormatException();
        }

        ValidateJournalTargets(plan, changeSet, journal);
        var journalTargets = journal.Targets.ToDictionary(target => target.RecordId);
        foreach (var target in changeSet.Targets)
        {
            if (journalTargets.TryGetValue(target.RecordId, out var journalTarget))
            {
                var interruptedTerminalRetry =
                    changeSet.State == SetupChangeSetState.Partial &&
                    string.Equals(changeSet.OutcomeCode, SetupCodes.InterruptedRecoveryFailed, StringComparison.Ordinal) &&
                    string.Equals(target.OutcomeCode, SetupCodes.InterruptedRecoveryFailed, StringComparison.Ordinal) &&
                    target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable;
                var rollbackStatusMatches = changeSet.State == SetupChangeSetState.Partial
                    ? target.RollbackStatus is SetupLedgerRollbackStatus.Pending or
                        SetupLedgerRollbackStatus.Stale or SetupLedgerRollbackStatus.Failed || interruptedTerminalRetry
                    : target.RollbackStatus == SetupLedgerRollbackStatus.Pending;
                var planTarget = plan.Targets.Single(item => item.RecordId == target.RecordId);
                var appliedHashMatches = target.TargetKind == SetupTargetKind.Env
                    ? string.Equals(
                        target.AppliedStateHash,
                        ExpectedEnvironmentAppliedHash(plan.ChangeSetId, planTarget),
                        StringComparison.Ordinal)
                    : journalTarget.Steps.Count == 1 && string.Equals(
                        target.AppliedStateHash,
                        journalTarget.Steps[0].DesiredStateHash,
                        StringComparison.Ordinal);
                if (!appliedHashMatches ||
                    !string.Equals(target.BackupReference, target.RecordId.ToString("D"), StringComparison.Ordinal) ||
                    !rollbackStatusMatches)
                {
                    throw new FormatException();
                }
            }
            else if (!HasNoRollbackOwnership(target) &&
                     !(changeSet.State == SetupChangeSetState.Partial &&
                       target.AppliedStateHash is null &&
                       target.BackupReference is null &&
                       ((target.RollbackStatus == SetupLedgerRollbackStatus.Stale &&
                         string.Equals(target.OutcomeCode, SetupCodes.RollbackStale, StringComparison.Ordinal)) ||
                        (target.RollbackStatus == SetupLedgerRollbackStatus.Failed &&
                         string.Equals(target.OutcomeCode, SetupCodes.InternalError, StringComparison.Ordinal)))))
            {
                throw new FormatException();
            }
        }
    }

    private bool TryEnterActiveFileRollback(
        SetupLock setupLock,
        Guid changeSetId,
        SetupJournalPhase initialPhase)
    {
        if (initialPhase == SetupJournalPhase.Prepared)
        {
            return TryMarkJournalPhaseOrConfirm(
                setupLock, changeSetId, SetupJournalPhase.Prepared, SetupJournalPhase.RollingBack);
        }

        if (initialPhase == SetupJournalPhase.Partial)
        {
            return TryMarkJournalPhaseOrConfirm(
                setupLock, changeSetId, SetupJournalPhase.Partial, SetupJournalPhase.RollingBack);
        }

        return initialPhase == SetupJournalPhase.RollingBack;
    }

    private bool TryPersistRollingBackLedger(SetupLock setupLock, Guid changeSetId)
    {
        try
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(item => item.ChangeSetId == changeSetId);
            if (current.State == SetupChangeSetState.RollingBack)
            {
                return true;
            }

            if (current.State is not (SetupChangeSetState.Applied or SetupChangeSetState.Partial))
            {
                return false;
            }

            SaveChangeSetOrConfirm(setupLock, ledger, current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                State = SetupChangeSetState.RollingBack,
                Targets = current.Targets.Select(target =>
                    target.AppliedStateHash is not null && target.BackupReference is not null
                        ? target with
                        {
                            OutcomeCode = null,
                            RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        }
                        : target.AppliedStateHash is null && target.BackupReference is null
                            ? target with
                            {
                                OutcomeCode = null,
                                RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                            }
                            : target).ToArray(),
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ActiveFileStepOutcome RecoverActiveRollbackStep(
        SetupLock setupLock,
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        SetupJournalStep step)
    {
        var classification = ClassifyActiveStep(target, step);
        if (step.Phase == SetupJournalStepPhase.RestoreCompleted)
        {
            if (classification == ActiveFileClassification.Prior)
            {
                return ActiveFileStepOutcome.Completed;
            }

            if (classification is ActiveFileClassification.ThirdParty or ActiveFileClassification.Unavailable)
            {
                return ClassificationFailure(classification);
            }

            if (!TryMarkStepPhaseOrConfirm(
                    setupLock,
                    changeSetId,
                    target.RecordId,
                    step.MemberKey,
                    SetupJournalStepPhase.RestoreCompleted,
                    SetupJournalStepPhase.RestoreStarted))
            {
                return ActiveFileStepOutcome.JournalUnproven;
            }
        }
        else if (step.Phase == SetupJournalStepPhase.Pending)
        {
            if (classification is ActiveFileClassification.ThirdParty or ActiveFileClassification.Unavailable)
            {
                return ClassificationFailure(classification);
            }

            if (!TryMarkStepPhaseOrConfirm(
                    setupLock,
                    changeSetId,
                    target.RecordId,
                    step.MemberKey,
                    SetupJournalStepPhase.Pending,
                    SetupJournalStepPhase.RestoreStarted))
            {
                return ActiveFileStepOutcome.JournalUnproven;
            }
        }
        else if (step.Phase != SetupJournalStepPhase.RestoreStarted)
        {
            return ActiveFileStepOutcome.Unavailable;
        }

        if (classification is ActiveFileClassification.ThirdParty or ActiveFileClassification.Unavailable)
        {
            return ClassificationFailure(classification);
        }

        if (classification == ActiveFileClassification.Desired)
        {
            try
            {
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreIntentBeforeRestore);
                RestoreActiveStep(changeSetId, target, step);
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreBeforeCompletion);
            }
            catch (Exception)
            {
                var afterFailure = ClassifyActiveStep(target, step);
                if (afterFailure != ActiveFileClassification.Prior)
                {
                    return ClassificationFailure(afterFailure);
                }
            }
        }

        return TryMarkStepPhaseOrConfirm(
            setupLock,
            changeSetId,
            target.RecordId,
            step.MemberKey,
            SetupJournalStepPhase.RestoreStarted,
            SetupJournalStepPhase.RestoreCompleted)
                ? ActiveFileStepOutcome.Completed
                : ActiveFileStepOutcome.JournalUnproven;
    }

    private SetupRecoveryResult PersistActiveFileRollbackFailure(
        SetupLock setupLock,
        SetupLedgerChangeSet original,
        SetupTransactionJournal journal,
        IReadOnlyDictionary<Guid, ActiveFileFailureKind> failures,
        bool normalRollback = false)
    {
        if (!TryEnterActiveFileRollback(setupLock, original.ChangeSetId, journal.Phase) ||
            !TryPersistRollingBackLedger(setupLock, original.ChangeSetId) ||
            !TryMarkJournalPhaseOrConfirm(
                setupLock,
                original.ChangeSetId,
                SetupJournalPhase.RollingBack,
                SetupJournalPhase.Partial))
        {
            return ActiveFileRollbackRecoveryRequired(original);
        }

        try
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(item => item.ChangeSetId == original.ChangeSetId);
            var outcomeCode = normalRollback
                ? SetupCodes.PartialRollback
                : SetupCodes.InterruptedRecoveryFailed;
            var partial = current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = outcomeCode,
                State = SetupChangeSetState.Partial,
                Targets = current.Targets.Select(target => failures.TryGetValue(target.RecordId, out var failure)
                    ? target with
                    {
                        OutcomeCode = failure == ActiveFileFailureKind.Stale
                            ? SetupCodes.RollbackStale
                            : SetupCodes.InternalError,
                        RollbackStatus = failure == ActiveFileFailureKind.Stale
                            ? SetupLedgerRollbackStatus.Stale
                            : SetupLedgerRollbackStatus.Failed,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, ledger, partial);
            return Failed(
                outcomeCode,
                original.ChangeSetId,
                SetupRecoveryOperation.Rollback,
                partial);
        }
        catch (Exception)
        {
            return ActiveFileRollbackRecoveryRequired(original);
        }
    }

    private SetupRecoveryResult ReconcileCommittedFileRollback(
        SetupLock setupLock,
        SetupLedgerChangeSet changeSet,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal)
    {
        try
        {
            ValidateActiveFileRollbackEvidence(plan, changeSet, journal);
            _ = VerifyTerminalState(plan, changeSet, journal, desired: false);
            return PersistRecoveredFileRollback(setupLock, changeSet, plan, journal, normalRollback: false);
        }
        catch (Exception)
        {
            return PersistFailure(
                setupLock,
                ledgerStore.LoadForRecovery(),
                changeSet,
                SetupRecoveryOperation.Rollback);
        }
    }

    private SetupRecoveryResult PersistRecoveredFileRollback(
        SetupLock setupLock,
        SetupLedgerChangeSet original,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal,
        bool normalRollback)
    {
        try
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(item => item.ChangeSetId == original.ChangeSetId);
            var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
            var physicalRecordIds = plan.Targets
                .Where(target => target.TargetKind != SetupTargetKind.Guidance)
                .Select(target => target.RecordId)
                .ToHashSet();
            var outcomeCode = normalRollback
                ? SetupCodes.RollbackSucceeded
                : SetupCodes.InterruptedRollbackRecovered;
            var recovered = current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = outcomeCode,
                State = SetupChangeSetState.RolledBack,
                Targets = current.Targets.Select(target => physicalRecordIds.Contains(target.RecordId)
                    ? target with
                    {
                        AppliedStateHash = null,
                        BackupReference = null,
                        OutcomeCode = journalRecordIds.Contains(target.RecordId)
                            ? outcomeCode
                            : null,
                        RollbackStatus = journalRecordIds.Contains(target.RecordId)
                            ? SetupLedgerRollbackStatus.Succeeded
                            : SetupLedgerRollbackStatus.NotAvailable,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, ledger, recovered);
            var durableJournal = LoadJournalForRecovery(original.ChangeSetId);
            if (durableJournal?.EnvironmentNotification == SetupEnvironmentNotification.Pending)
            {
                return CompleteNotification(setupLock, ledger, recovered, durableJournal);
            }

            return Recovered(recovered, SetupRecoveryOperation.Rollback);
        }
        catch (Exception)
        {
            return ActiveFileRollbackRecoveryRequired(original);
        }
    }

    private static SetupRecoveryResult ActiveFileRollbackRecoveryRequired(SetupLedgerChangeSet changeSet) =>
        Failed(
            SetupCodes.RecoveryRequired,
            changeSet.ChangeSetId,
            SetupRecoveryOperation.Rollback,
            OverlayFailure(changeSet));

    private SetupRecoveryResult RecoverActiveApply(
        SetupLock setupLock,
        SetupLedgerChangeSet changeSet,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal)
    {
        if (!ActiveFileLifecycleMatches(changeSet.State, journal.Phase))
        {
            return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId,
                SetupRecoveryOperation.Apply, OverlayFailure(changeSet));
        }

        try
        {
            ValidateActiveFileApplyEvidence(plan, changeSet, journal);
        }
        catch (Exception)
        {
            return PersistActiveFileFailure(
                setupLock,
                changeSet,
                journal,
                new Dictionary<Guid, ActiveFileFailureKind>());
        }

        if (!TryEnterActiveFileCompensation(setupLock, changeSet.ChangeSetId, journal.Phase) ||
            !TryPersistCompensatingLedger(setupLock, changeSet.ChangeSetId))
        {
            return ActiveFileRecoveryRequired(changeSet);
        }

        var failures = new Dictionary<Guid, ActiveFileFailureKind>();
        for (var targetIndex = journal.Targets.Count - 1; targetIndex >= 0; targetIndex--)
        {
            var journalTarget = journal.Targets[targetIndex];
            var planTarget = plan.Targets.Single(target => target.RecordId == journalTarget.RecordId);
            for (var stepIndex = journalTarget.Steps.Count - 1; stepIndex >= 0; stepIndex--)
            {
                SetupJournalStep step;
                try
                {
                    step = LoadActiveStep(
                        changeSet.ChangeSetId,
                        journalTarget.RecordId,
                        journalTarget.Steps[stepIndex].MemberKey);
                }
                catch (Exception)
                {
                    return ActiveFileRecoveryRequired(changeSet);
                }

                var outcome = RecoverActiveStep(
                    setupLock,
                    changeSet.ChangeSetId,
                    planTarget,
                    step);
                if (outcome == ActiveFileStepOutcome.JournalUnproven)
                {
                    return ActiveFileRecoveryRequired(changeSet);
                }

                if (outcome == ActiveFileStepOutcome.Stale)
                {
                    failures[journalTarget.RecordId] = ActiveFileFailureKind.Stale;
                }
                else if (outcome == ActiveFileStepOutcome.Unavailable)
                {
                    failures[journalTarget.RecordId] = ActiveFileFailureKind.Unavailable;
                }
            }
        }

        foreach (var target in plan.Targets.Where(target => target.TargetKind != SetupTargetKind.Guidance))
        {
            var classification = ClassifyAggregatePrior(target);
            if (classification != ActiveFileClassification.Prior)
            {
                failures[target.RecordId] = classification == ActiveFileClassification.ThirdParty
                    ? ActiveFileFailureKind.Stale
                    : ActiveFileFailureKind.Unavailable;
            }
        }

        if (failures.Count > 0)
        {
            return PersistActiveFileFailure(setupLock, changeSet, journal, failures);
        }

        if (!TryMarkJournalPhaseOrConfirm(
                setupLock,
                changeSet.ChangeSetId,
                SetupJournalPhase.Compensating,
                SetupJournalPhase.Restored))
        {
            return ActiveFileRecoveryRequired(changeSet);
        }

        SetupLedgerChangeSet restored;
        SetupOwnershipLedger recoveryLedger;
        try
        {
            recoveryLedger = ledgerStore.LoadForRecovery();
            var current = recoveryLedger.ChangeSets.Single(item => item.ChangeSetId == changeSet.ChangeSetId);
            var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
            var physicalRecordIds = plan.Targets
                .Where(target => target.TargetKind != SetupTargetKind.Guidance)
                .Select(target => target.RecordId)
                .ToHashSet();
            restored = current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.InterruptedApplyRecovered,
                State = SetupChangeSetState.Restored,
                Targets = current.Targets.Select(target => physicalRecordIds.Contains(target.RecordId)
                    ? target with
                    {
                        AppliedStateHash = null,
                        BackupReference = null,
                        OutcomeCode = journalRecordIds.Contains(target.RecordId)
                            ? SetupCodes.InterruptedApplyRecovered
                            : null,
                        RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, recoveryLedger, restored);
        }
        catch (Exception)
        {
            return ActiveFileRecoveryRequired(changeSet);
        }

        SetupTransactionJournal terminal;
        try
        {
            terminal = LoadJournalForRecovery(changeSet.ChangeSetId) ?? throw new FormatException();
        }
        catch (Exception)
        {
            return ActiveFileRecoveryRequired(changeSet);
        }

        return terminal.EnvironmentNotification == SetupEnvironmentNotification.Pending
            ? CompleteNotification(
                setupLock,
                recoveryLedger,
                restored,
                terminal)
            : Recovered(restored, SetupRecoveryOperation.Apply);
    }

    private void ValidateActiveFileApplyEvidence(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        if (journal.ChangeSetId != changeSet.ChangeSetId ||
            journal.Operation != SetupJournalOperation.Apply ||
            journal.EnvironmentNotification != ExpectedActiveNotification(journal))
        {
            throw new FormatException();
        }

        ValidateJournalTargets(plan, changeSet, journal);
        var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
        foreach (var target in changeSet.Targets)
        {
            var changed = journalRecordIds.Contains(target.RecordId);
            if (changed)
            {
                var rollbackStatusMatches = changeSet.State == SetupChangeSetState.Partial
                    ? target.RollbackStatus is SetupLedgerRollbackStatus.Pending or
                        SetupLedgerRollbackStatus.Stale or SetupLedgerRollbackStatus.Failed
                    : target.RollbackStatus == SetupLedgerRollbackStatus.Pending;
                if (!string.Equals(target.BackupReference, target.RecordId.ToString("D"), StringComparison.Ordinal) ||
                    target.AppliedStateHash is not null ||
                    !rollbackStatusMatches)
                {
                    throw new FormatException();
                }
            }
            else if (!HasNoRollbackOwnership(target) &&
                     !(changeSet.State == SetupChangeSetState.Partial &&
                       journal.Phase == SetupJournalPhase.Partial &&
                       target.AppliedStateHash is null &&
                       target.BackupReference is null &&
                       ((target.RollbackStatus == SetupLedgerRollbackStatus.Stale &&
                         string.Equals(target.OutcomeCode, SetupCodes.RollbackStale, StringComparison.Ordinal)) ||
                        (target.RollbackStatus == SetupLedgerRollbackStatus.Failed &&
                         string.Equals(target.OutcomeCode, SetupCodes.InternalError, StringComparison.Ordinal)))))
            {
                throw new FormatException();
            }
        }
    }

    private bool TryEnterActiveFileCompensation(
        SetupLock setupLock,
        Guid changeSetId,
        SetupJournalPhase initialPhase)
    {
        var phase = initialPhase;
        if (phase == SetupJournalPhase.Prepared)
        {
            if (!TryMarkJournalPhaseOrConfirm(
                    setupLock, changeSetId, SetupJournalPhase.Prepared, SetupJournalPhase.Applying))
            {
                return false;
            }

            phase = SetupJournalPhase.Applying;
        }

        if (phase is SetupJournalPhase.Applying or SetupJournalPhase.Partial)
        {
            return TryMarkJournalPhaseOrConfirm(
                setupLock, changeSetId, phase, SetupJournalPhase.Compensating);
        }

        return phase == SetupJournalPhase.Compensating;
    }

    private bool TryPersistCompensatingLedger(SetupLock setupLock, Guid changeSetId)
    {
        try
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(item => item.ChangeSetId == changeSetId);
            if (current.State == SetupChangeSetState.Compensating)
            {
                return true;
            }

            if (current.State is not (SetupChangeSetState.Applying or SetupChangeSetState.Partial))
            {
                return false;
            }

            var compensating = current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                State = SetupChangeSetState.Compensating,
            };
            SaveChangeSetOrConfirm(setupLock, ledger, compensating);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ActiveFileStepOutcome RecoverActiveStep(
        SetupLock setupLock,
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        SetupJournalStep step)
    {
        var classification = ClassifyActiveStep(target, step);
        if (step.Phase == SetupJournalStepPhase.Pending)
        {
            return classification == ActiveFileClassification.Prior
                ? ActiveFileStepOutcome.Completed
                : ClassificationFailure(classification);
        }

        if (step.Phase == SetupJournalStepPhase.RestoreCompleted)
        {
            return classification == ActiveFileClassification.Prior
                ? ActiveFileStepOutcome.Completed
                : ClassificationFailure(classification);
        }

        if (classification is ActiveFileClassification.ThirdParty or ActiveFileClassification.Unavailable)
        {
            return ClassificationFailure(classification);
        }

        if (step.Phase is SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted)
        {
            if (!TryMarkStepPhaseOrConfirm(
                    setupLock,
                    changeSetId,
                    target.RecordId,
                    step.MemberKey,
                    step.Phase,
                    SetupJournalStepPhase.RestoreStarted))
            {
                return ActiveFileStepOutcome.JournalUnproven;
            }
        }

        if (classification == ActiveFileClassification.Desired)
        {
            try
            {
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreIntentBeforeRestore);
                RestoreActiveStep(changeSetId, target, step);
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreBeforeCompletion);
            }
            catch (Exception)
            {
                var afterFailure = ClassifyActiveStep(target, step);
                if (afterFailure != ActiveFileClassification.Prior)
                {
                    return ClassificationFailure(afterFailure);
                }
            }
        }

        return TryMarkStepPhaseOrConfirm(
            setupLock,
            changeSetId,
            target.RecordId,
            step.MemberKey,
            SetupJournalStepPhase.RestoreStarted,
            SetupJournalStepPhase.RestoreCompleted)
                ? ActiveFileStepOutcome.Completed
                : ActiveFileStepOutcome.JournalUnproven;
    }

    private bool TryMarkJournalPhaseOrConfirm(
        SetupLock setupLock,
        Guid changeSetId,
        SetupJournalPhase expected,
        SetupJournalPhase next)
    {
        try
        {
            var current = LoadJournalForRecovery(changeSetId);
            if (current?.Phase == next)
            {
                return true;
            }

            if (current?.Phase != expected)
            {
                return false;
            }

            journalStore.MarkTransactionPhase(setupLock, changeSetId, next);
            return LoadJournalForRecovery(changeSetId)?.Phase == next;
        }
        catch (Exception)
        {
            try
            {
                return LoadJournalForRecovery(changeSetId)?.Phase == next;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private bool TryMarkStepPhaseOrConfirm(
        SetupLock setupLock,
        Guid changeSetId,
        Guid recordId,
        string? memberKey,
        SetupJournalStepPhase expected,
        SetupJournalStepPhase next)
    {
        try
        {
            var current = LoadActiveStep(changeSetId, recordId, memberKey);
            if (current.Phase == next)
            {
                return true;
            }

            if (current.Phase != expected)
            {
                return false;
            }

            journalStore.MarkStepPhase(setupLock, changeSetId, recordId, memberKey, expected, next);
            return LoadActiveStep(changeSetId, recordId, memberKey).Phase == next;
        }
        catch (Exception)
        {
            try
            {
                return LoadActiveStep(changeSetId, recordId, memberKey).Phase == next;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private SetupJournalStep LoadActiveStep(Guid changeSetId, Guid recordId, string? memberKey)
    {
        var journal = LoadJournalForRecovery(changeSetId) ?? throw new FormatException();
        var target = journal.Targets.Single(item => item.RecordId == recordId);
        return target.Steps.Single(step =>
            string.Equals(step.MemberKey, memberKey, StringComparison.Ordinal));
    }

    private ActiveFileClassification ClassifyActiveStep(
        SetupPrivatePlanTarget target,
        SetupJournalStep step)
    {
        try
        {
            var current = target.TargetKind == SetupTargetKind.Env
                ? environmentStep.Capture([step.MemberKey!]).Members[0].Hash
                : fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation).Hash;
            if (string.Equals(current, step.PriorStateHash, StringComparison.Ordinal))
            {
                return ActiveFileClassification.Prior;
            }

            return string.Equals(current, step.DesiredStateHash, StringComparison.Ordinal)
                ? ActiveFileClassification.Desired
                : ActiveFileClassification.ThirdParty;
        }
        catch (Exception)
        {
            return ActiveFileClassification.Unavailable;
        }
    }

    private ActiveFileClassification ClassifyAggregatePrior(SetupPrivatePlanTarget target)
    {
        try
        {
            if (target.TargetKind == SetupTargetKind.Env)
            {
                var capture = environmentStep.Capture(
                    target.Members.Select(member => member.SettingKey).ToArray());
                if (string.Equals(capture.AggregateHash, target.BaseStateHash, StringComparison.Ordinal))
                {
                    return ActiveFileClassification.Prior;
                }

                return target.Members.Where((member, index) => !string.Equals(
                        capture.Members[index].Hash,
                        environmentStep.HashMember(member.SettingKey, DesiredEnvironmentValue(member)),
                        StringComparison.Ordinal)).Any()
                    ? ActiveFileClassification.ThirdParty
                    : ActiveFileClassification.Desired;
            }

            var current = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation).Hash;
            if (string.Equals(current, target.BaseStateHash, StringComparison.Ordinal))
            {
                return ActiveFileClassification.Prior;
            }

            return string.Equals(
                current,
                SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState)),
                StringComparison.Ordinal)
                    ? ActiveFileClassification.Desired
                    : ActiveFileClassification.ThirdParty;
        }
        catch (Exception)
        {
            return ActiveFileClassification.Unavailable;
        }
    }

    private void RestoreActiveStep(
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        SetupJournalStep step)
    {
        var backupPath = paths.GetBackup(changeSetId, target.RecordId);
        if (target.TargetKind == SetupTargetKind.Env)
        {
            environmentStep.RestoreMember(
                step.MemberKey!,
                backupPath,
                step.DesiredStateHash,
                step.PriorStateHash);
            return;
        }

        fileStep.Restore(
            GetAllowedRoot(target.TargetLocation),
            target.TargetLocation,
            backupPath,
            step.DesiredStateHash,
            step.PriorStateHash);
    }

    private SetupRecoveryResult PersistActiveFileFailure(
        SetupLock setupLock,
        SetupLedgerChangeSet original,
        SetupTransactionJournal journal,
        IReadOnlyDictionary<Guid, ActiveFileFailureKind> failures)
    {
        if (!TryEnterActiveFileCompensation(setupLock, original.ChangeSetId, journal.Phase) ||
            !TryPersistCompensatingLedger(setupLock, original.ChangeSetId) ||
            !TryMarkJournalPhaseOrConfirm(
                setupLock,
                original.ChangeSetId,
                SetupJournalPhase.Compensating,
                SetupJournalPhase.Partial))
        {
            return ActiveFileRecoveryRequired(original);
        }

        try
        {
            var ledger = ledgerStore.LoadForRecovery();
            var current = ledger.ChangeSets.Single(item => item.ChangeSetId == original.ChangeSetId);
            var partial = current with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
                State = SetupChangeSetState.Partial,
                Targets = current.Targets.Select(target => failures.TryGetValue(target.RecordId, out var failure)
                    ? target with
                    {
                        OutcomeCode = failure == ActiveFileFailureKind.Stale
                            ? SetupCodes.RollbackStale
                            : SetupCodes.InternalError,
                        RollbackStatus = failure == ActiveFileFailureKind.Stale
                            ? SetupLedgerRollbackStatus.Stale
                            : SetupLedgerRollbackStatus.Failed,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, ledger, partial);
            return Failed(
                SetupCodes.InterruptedRecoveryFailed,
                original.ChangeSetId,
                SetupRecoveryOperation.Apply,
                partial);
        }
        catch (Exception)
        {
            return ActiveFileRecoveryRequired(original);
        }
    }

    private static SetupRecoveryResult ActiveFileRecoveryRequired(SetupLedgerChangeSet changeSet) =>
        Failed(
            SetupCodes.RecoveryRequired,
            changeSet.ChangeSetId,
            SetupRecoveryOperation.Apply,
            OverlayFailure(changeSet));

    private static ActiveFileStepOutcome ClassificationFailure(ActiveFileClassification classification) =>
        classification == ActiveFileClassification.ThirdParty
            ? ActiveFileStepOutcome.Stale
            : ActiveFileStepOutcome.Unavailable;

    private static bool IsActiveApply(SetupTransactionJournal journal) =>
        journal.Operation == SetupJournalOperation.Apply &&
        journal.Phase is (SetupJournalPhase.Prepared or SetupJournalPhase.Applying or
            SetupJournalPhase.Compensating or SetupJournalPhase.Partial);

    private static bool IsActiveRollback(SetupTransactionJournal journal) =>
        journal.Operation == SetupJournalOperation.Rollback &&
        journal.Phase is SetupJournalPhase.Prepared or SetupJournalPhase.RollingBack or SetupJournalPhase.Partial;

    private static SetupEnvironmentNotification ExpectedActiveNotification(
        SetupTransactionJournal journal) => journal.Targets.Any(target =>
            target.TargetKind == SetupTargetKind.Env &&
            target.Steps.Any(step => step.Phase != SetupJournalStepPhase.Pending))
                ? SetupEnvironmentNotification.Pending
                : SetupEnvironmentNotification.NotRequired;

    private static bool ActiveFileLifecycleMatches(
        SetupChangeSetState state,
        SetupJournalPhase phase) => phase switch
    {
        SetupJournalPhase.Prepared => state == SetupChangeSetState.Applying,
        SetupJournalPhase.Applying => state == SetupChangeSetState.Applying,
        SetupJournalPhase.Compensating => state is SetupChangeSetState.Applying or
            SetupChangeSetState.Compensating or SetupChangeSetState.Partial,
        SetupJournalPhase.Partial => state is SetupChangeSetState.Compensating or SetupChangeSetState.Partial,
        _ => false,
    };

    private static bool ActiveFileRollbackLifecycleMatches(
        SetupChangeSetState state,
        SetupJournalPhase phase) => phase switch
    {
        SetupJournalPhase.Prepared => state == SetupChangeSetState.RollingBack,
        SetupJournalPhase.RollingBack => state is SetupChangeSetState.RollingBack or SetupChangeSetState.Partial,
        SetupJournalPhase.Partial => state is SetupChangeSetState.RollingBack or SetupChangeSetState.Partial,
        _ => false,
    };

    private SetupPrivatePlan? LoadPlanForRecovery(Guid changeSetId)
    {
        var source = paths.GetPlan(changeSetId);
        try
        {
            var metadata = platform.FileSystem.GetPathMetadata(source);
            if (!metadata.Exists)
            {
                return null;
            }

            RequireRegular(metadata);
            var plan = planStore.Load(changeSetId) ?? throw new FormatException();
            RequireRegular(platform.FileSystem.GetPathMetadata(source));
            return plan;
        }
        catch (SetupStorageException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }
    }

    private SetupTransactionJournal? LoadJournalForRecovery(Guid changeSetId)
    {
        var source = paths.GetTransactionJournal(changeSetId);
        try
        {
            var metadata = platform.FileSystem.GetPathMetadata(source);
            if (!metadata.Exists)
            {
                return null;
            }

            RequireRegular(metadata);
            var journal = journalStore.Load(changeSetId) ?? throw new FormatException();
            RequireRegular(platform.FileSystem.GetPathMetadata(source));
            return journal;
        }
        catch (SetupStorageException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupStorageException(SetupJournalStorageCodes.Corrupt);
        }
    }

    private void ValidateDormantPreparedApply(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        if (journal.ChangeSetId != plan.ChangeSetId ||
            journal.EnvironmentNotification != SetupEnvironmentNotification.NotRequired)
        {
            throw new FormatException();
        }

        var expectedTargets = new List<SetupJournalTarget>();
        foreach (var target in plan.Targets)
        {
            if (target.TargetKind == SetupTargetKind.Guidance)
            {
                continue;
            }

            if (target.TargetKind == SetupTargetKind.Env)
            {
                var names = target.Members.Select(member => member.SettingKey).ToArray();
                var capture = environmentStep.Capture(names);
                RequireEqual(capture.AggregateHash, target.BaseStateHash);
                var expectedSteps = new List<SetupJournalStep>();
                for (var index = 0; index < target.Members.Count; index++)
                {
                    var desiredHash = environmentStep.HashMember(
                        names[index], DesiredEnvironmentValue(target.Members[index]));
                    if (!string.Equals(capture.Members[index].Hash, desiredHash, StringComparison.Ordinal))
                    {
                        expectedSteps.Add(new SetupJournalStep(
                            names[index],
                            capture.Members[index].Hash,
                            desiredHash,
                            target.RecordId.ToString("D"),
                            SetupJournalStepPhase.Pending));
                    }
                }

                if (expectedSteps.Count > 0)
                {
                    ValidateEnvironmentBackup(plan.ChangeSetId, target, capture);
                    expectedTargets.Add(new SetupJournalTarget(target.RecordId, target.TargetKind, expectedSteps));
                }

                continue;
            }

            var fileCapture = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation);
            RequireEqual(fileCapture.Hash, target.BaseStateHash);
            var desiredFileHash = SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState));
            if (!string.Equals(fileCapture.Hash, desiredFileHash, StringComparison.Ordinal))
            {
                ValidateFileBackup(paths.GetBackup(plan.ChangeSetId, target.RecordId), fileCapture.Hash);
                expectedTargets.Add(new SetupJournalTarget(
                    target.RecordId,
                    target.TargetKind,
                    [new SetupJournalStep(
                        null,
                        fileCapture.Hash,
                        desiredFileHash,
                        target.RecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)]));
            }
        }

        RequireJournalTargetsEqual(journal.Targets, expectedTargets);
    }

    private void ValidateEnvironmentBackup(
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        UserEnvironmentCapture expected)
    {
        var names = target.Members.Select(member => member.SettingKey).ToArray();
        var backup = environmentStep.ReadBackup(paths.GetBackup(changeSetId, target.RecordId), names);
        RequireEqual(backup.AggregateHash, expected.AggregateHash);
        for (var index = 0; index < names.Length; index++)
        {
            RequireEqual(backup.Members[index].Hash, expected.Members[index].Hash);
        }
    }

    private static void RequireJournalTargetsEqual(
        IReadOnlyList<SetupJournalTarget> actual,
        IReadOnlyList<SetupJournalTarget> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new FormatException();
        }

        for (var targetIndex = 0; targetIndex < actual.Count; targetIndex++)
        {
            var actualTarget = actual[targetIndex];
            var expectedTarget = expected[targetIndex];
            if (actualTarget.RecordId != expectedTarget.RecordId ||
                actualTarget.TargetKind != expectedTarget.TargetKind ||
                actualTarget.Steps.Count != expectedTarget.Steps.Count)
            {
                throw new FormatException();
            }

            for (var stepIndex = 0; stepIndex < actualTarget.Steps.Count; stepIndex++)
            {
                var actualStep = actualTarget.Steps[stepIndex];
                var expectedStep = expectedTarget.Steps[stepIndex];
                if (actualStep.Phase != SetupJournalStepPhase.Pending ||
                    !string.Equals(actualStep.MemberKey, expectedStep.MemberKey, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.PriorStateHash, expectedStep.PriorStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.DesiredStateHash, expectedStep.DesiredStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.BackupReference, expectedStep.BackupReference, StringComparison.Ordinal))
                {
                    throw new FormatException();
                }
            }
        }
    }

    private SetupRecoveryResult ReconcileCommittedApply(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal)
    {
        try
        {
            SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
            var appliedHashes = VerifyTerminalState(plan, changeSet, journal, desired: true);
            var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
            var recovered = changeSet with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.InterruptedApplyRecovered,
                State = SetupChangeSetState.Applied,
                Targets = changeSet.Targets.Select(target => journalRecordIds.Contains(target.RecordId)
                    ? target with
                    {
                        AppliedStateHash = appliedHashes[target.RecordId],
                        OutcomeCode = SetupCodes.InterruptedApplyRecovered,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, ledger, recovered);
            if (journal.EnvironmentNotification == SetupEnvironmentNotification.Pending)
            {
                return CompleteNotification(setupLock, ledger, recovered, journal);
            }

            return Recovered(recovered, SetupRecoveryOperation.Apply);
        }
        catch (Exception)
        {
            return PersistFailure(setupLock, ledger, changeSet, SetupRecoveryOperation.Apply);
        }
    }

    private SetupRecoveryResult ReconcileRestoredApply(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        SetupPrivatePlan plan,
        SetupTransactionJournal journal)
    {
        try
        {
            SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
            _ = VerifyTerminalState(plan, changeSet, journal, desired: false);
            var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
            var recovered = changeSet with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.InterruptedApplyRecovered,
                State = SetupChangeSetState.Restored,
                Targets = changeSet.Targets.Select(target => journalRecordIds.Contains(target.RecordId)
                    ? target with
                    {
                        AppliedStateHash = null,
                        BackupReference = null,
                        OutcomeCode = SetupCodes.InterruptedApplyRecovered,
                        RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                    }
                    : target).ToArray(),
            };
            SaveChangeSetOrConfirm(setupLock, ledger, recovered);
            if (journal.EnvironmentNotification == SetupEnvironmentNotification.Pending)
            {
                return CompleteNotification(setupLock, ledger, recovered, journal);
            }

            return Recovered(recovered, SetupRecoveryOperation.Apply);
        }
        catch (Exception)
        {
            return PersistFailure(setupLock, ledger, changeSet, SetupRecoveryOperation.Apply);
        }
    }

    private IReadOnlyDictionary<Guid, string> VerifyTerminalState(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal,
        bool desired)
    {
        var hashes = new Dictionary<Guid, string>();
        foreach (var target in plan.Targets)
        {
            if (target.TargetKind == SetupTargetKind.Guidance)
            {
                continue;
            }

            var ledgerTarget = changeSet.Targets.Single(item => item.RecordId == target.RecordId);
            if (target.TargetKind == SetupTargetKind.Env)
            {
                var names = target.Members.Select(member => member.SettingKey).ToArray();
                var capture = environmentStep.Capture(names);
                var previous = desired
                    ? null
                    : environmentStep.ReadBackup(paths.GetBackup(plan.ChangeSetId, target.RecordId), names);
                for (var index = 0; index < target.Members.Count; index++)
                {
                    var expected = desired
                        ? environmentStep.HashMember(names[index], DesiredEnvironmentValue(target.Members[index]))
                        : previous!.Members[index].Hash;
                    RequireEqual(capture.Members[index].Hash, expected);
                }

                hashes[target.RecordId] = capture.AggregateHash;
                continue;
            }

            var captureFile = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation);
            var expectedHash = desired
                ? SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState))
                : ledgerTarget.PreviousStateHash;
            RequireEqual(captureFile.Hash, expectedHash);
            hashes[target.RecordId] = captureFile.Hash;
        }

        ValidateJournalTargets(plan, changeSet, journal);
        return hashes;
    }

    private void ValidateJournalTargets(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        var expectedTargets = new List<SetupJournalTarget>();
        for (var targetIndex = 0; targetIndex < plan.Targets.Count; targetIndex++)
        {
            var target = plan.Targets[targetIndex];
            var ledgerTarget = changeSet.Targets[targetIndex];
            if (target.TargetKind == SetupTargetKind.Guidance)
            {
                continue;
            }

            var expectedBackupReference = target.RecordId.ToString("D");
            if (!string.Equals(ledgerTarget.BackupReference, expectedBackupReference, StringComparison.Ordinal))
            {
                if (ledgerTarget.BackupReference is null)
                {
                    continue;
                }

                throw new FormatException();
            }

            var backupPath = paths.GetBackup(plan.ChangeSetId, target.RecordId);
            if (target.TargetKind == SetupTargetKind.Env)
            {
                var names = target.Members.Select(member => member.SettingKey).ToArray();
                var backup = environmentStep.ReadBackup(backupPath, names);
                RequireEqual(backup.AggregateHash, target.BaseStateHash);
                var expectedSteps = new List<SetupJournalStep>();
                for (var memberIndex = 0; memberIndex < target.Members.Count; memberIndex++)
                {
                    var member = target.Members[memberIndex];
                    var desiredHash = environmentStep.HashMember(
                        names[memberIndex], DesiredEnvironmentValue(member));
                    if (member.Operation == SetupOperation.NoOp)
                    {
                        RequireEqual(backup.Members[memberIndex].Hash, desiredHash);
                        continue;
                    }

                    if (!string.Equals(backup.Members[memberIndex].Hash, desiredHash, StringComparison.Ordinal))
                    {
                        expectedSteps.Add(new SetupJournalStep(
                            names[memberIndex],
                            backup.Members[memberIndex].Hash,
                            desiredHash,
                            expectedBackupReference,
                            SetupJournalStepPhase.Pending));
                    }
                }

                if (expectedSteps.Count == 0)
                {
                    throw new FormatException();
                }

                expectedTargets.Add(new SetupJournalTarget(target.RecordId, target.TargetKind, expectedSteps));
                continue;
            }

            ValidateFileBackup(backupPath, target.BaseStateHash);
            expectedTargets.Add(new SetupJournalTarget(
                target.RecordId,
                target.TargetKind,
                [new SetupJournalStep(
                    null,
                    target.BaseStateHash,
                    SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState)),
                    expectedBackupReference,
                    SetupJournalStepPhase.Pending)]));
        }

        RequireJournalTargetsEqualIgnoringPhase(journal.Targets, expectedTargets);
    }

    private static void RequireJournalTargetsEqualIgnoringPhase(
        IReadOnlyList<SetupJournalTarget> actual,
        IReadOnlyList<SetupJournalTarget> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new FormatException();
        }

        for (var targetIndex = 0; targetIndex < actual.Count; targetIndex++)
        {
            var actualTarget = actual[targetIndex];
            var expectedTarget = expected[targetIndex];
            if (actualTarget.RecordId != expectedTarget.RecordId ||
                actualTarget.TargetKind != expectedTarget.TargetKind ||
                actualTarget.Steps.Count != expectedTarget.Steps.Count)
            {
                throw new FormatException();
            }

            for (var stepIndex = 0; stepIndex < actualTarget.Steps.Count; stepIndex++)
            {
                var actualStep = actualTarget.Steps[stepIndex];
                var expectedStep = expectedTarget.Steps[stepIndex];
                if (!string.Equals(actualStep.MemberKey, expectedStep.MemberKey, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.PriorStateHash, expectedStep.PriorStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.DesiredStateHash, expectedStep.DesiredStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.BackupReference, expectedStep.BackupReference, StringComparison.Ordinal))
                {
                    throw new FormatException();
                }
            }
        }
    }

    private void ValidateFileBackup(string backupPath, string expectedHash)
    {
        var before = platform.FileSystem.GetPathMetadata(backupPath);
        RequireRegular(before);
        var bytes = platform.FileSystem.ReadAllBytes(backupPath);
        RequireRegular(platform.FileSystem.GetPathMetadata(backupPath));
        var headerLength = FileBackupMagic.Length + 9;
        if (bytes.Length < headerLength || !bytes.AsSpan(0, FileBackupMagic.Length).SequenceEqual(FileBackupMagic))
        {
            throw new FormatException();
        }

        var state = bytes[FileBackupMagic.Length];
        var length = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(FileBackupMagic.Length + 1, 8));
        if (state > 1 || length > int.MaxValue || bytes.Length != headerLength + (int)length || state == 0 && length != 0)
        {
            throw new FormatException();
        }

        RequireEqual(SetupHash.File(state == 1, bytes.AsSpan(headerLength).ToArray()), expectedHash);
    }

    private SetupRecoveryResult RecoverNotificationOnly(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        var operation = OperationFor(journal, changeSet)!.Value;
        var code = operation == SetupRecoveryOperation.Apply
            ? SetupCodes.InterruptedApplyRecovered
            : SetupCodes.InterruptedRollbackRecovered;
        var journalRecordIds = journal.Targets.Select(target => target.RecordId).ToHashSet();
        var recovered = changeSet with
        {
            UpdatedAt = platform.Clock.UtcNow,
            OutcomeCode = code,
            Targets = changeSet.Targets.Select(target => journalRecordIds.Contains(target.RecordId)
                ? target with { OutcomeCode = code }
                : target).ToArray(),
        };
        try
        {
            SaveChangeSetOrConfirm(setupLock, ledger, recovered);
            return CompleteNotification(setupLock, ledger, recovered, journal);
        }
        catch (Exception)
        {
            return PersistFailure(setupLock, ledger, recovered, operation, preserveTerminalLifecycle: true);
        }
    }

    private SetupRecoveryResult CompleteNotification(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet recovered,
        SetupTransactionJournal journal)
    {
        var operation = OperationFor(journal, recovered)!.Value;
        try
        {
            environmentStep.NotifyFinalState();
            journalStore.MarkEnvironmentNotificationCompleted(setupLock, recovered.ChangeSetId);
            return Recovered(recovered, operation);
        }
        catch (Exception)
        {
            try
            {
                var durableJournal = journalStore.Load(recovered.ChangeSetId);
                if (durableJournal?.EnvironmentNotification == SetupEnvironmentNotification.Completed)
                {
                    return Recovered(recovered, operation);
                }
            }
            catch (Exception)
            {
            }

            return PersistFailure(setupLock, ledger, recovered, operation, preserveTerminalLifecycle: true);
        }
    }

    private SetupRecoveryResult PersistFailure(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        SetupRecoveryOperation operation,
        bool preserveTerminalLifecycle = false)
    {
        var durable = preserveTerminalLifecycle
            ? changeSet with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
                Targets = changeSet.Targets.Select(target => target with
                {
                    OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
                }).ToArray(),
            }
            : OverlayFailure(changeSet) with { UpdatedAt = platform.Clock.UtcNow };
        var effective = preserveTerminalLifecycle ? OverlayFailure(durable) : durable;
        try
        {
            SaveChangeSet(setupLock, ledgerStore.LoadForRecovery(), durable);
        }
        catch (Exception)
        {
        }

        return Failed(SetupCodes.InterruptedRecoveryFailed, changeSet.ChangeSetId, operation, effective);
    }

    private void SaveChangeSet(SetupLock setupLock, SetupOwnershipLedger ledger, SetupLedgerChangeSet changeSet)
    {
        var changeSets = ledger.ChangeSets.ToArray();
        var index = Array.FindIndex(changeSets, item => item.ChangeSetId == changeSet.ChangeSetId);
        if (index < 0)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }

        changeSets[index] = changeSet;
        ledgerStore.Save(setupLock, ledger with { ChangeSets = changeSets });
    }

    private void SaveChangeSetOrConfirm(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet)
    {
        try
        {
            SaveChangeSet(setupLock, ledger, changeSet);
        }
        catch (Exception)
        {
            try
            {
                var durable = ledgerStore.LoadForRecovery().ChangeSets
                    .Single(item => item.ChangeSetId == changeSet.ChangeSetId);
                var expectedBytes = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [changeSet]));
                var durableBytes = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [durable]));
                if (expectedBytes.AsSpan().SequenceEqual(durableBytes))
                {
                    return;
                }
            }
            catch (Exception)
            {
            }

            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    private static SetupLedgerChangeSet OverlayFailure(
        SetupLedgerChangeSet changeSet,
        bool preserveTerminalLifecycle = false) => changeSet with
    {
        OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        State = preserveTerminalLifecycle ? changeSet.State : SetupChangeSetState.Partial,
        Targets = changeSet.Targets.Select(target => target with
        {
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
            RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
        }).ToArray(),
    };

    private static bool TerminalEvidenceMatches(
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        if (journal.ChangeSetId != changeSet.ChangeSetId ||
            !TerminalLifecycleMatches(changeSet.State, journal.Operation, journal.Phase))
        {
            return false;
        }

        var expectedTargets = changeSet.Targets
            .Where(target => target.TargetKind != SetupTargetKind.Guidance &&
                target.Members.Any(member => member.Operation != SetupOperation.NoOp))
            .ToArray();
        if (expectedTargets.Length != journal.Targets.Count ||
            changeSet.Targets.Any(target =>
                (target.TargetKind == SetupTargetKind.Guidance ||
                 target.Members.All(member => member.Operation == SetupOperation.NoOp)) &&
                !HasNoRollbackOwnership(target)))
        {
            return false;
        }

        for (var targetIndex = 0; targetIndex < expectedTargets.Length; targetIndex++)
        {
            var ledgerTarget = expectedTargets[targetIndex];
            var journalTarget = journal.Targets[targetIndex];
            if (ledgerTarget.RecordId != journalTarget.RecordId ||
                ledgerTarget.TargetKind != journalTarget.TargetKind ||
                !RollbackOwnershipMatches(changeSet.State, ledgerTarget) ||
                !TargetEvidenceMatches(ledgerTarget, journalTarget))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TargetEvidenceMatches(
        SetupLedgerTarget ledgerTarget,
        SetupJournalTarget journalTarget)
    {
        // Restored rows and cleared rollback rows no longer retain a backup reference to compare.
        if (ledgerTarget.BackupReference is not null &&
            journalTarget.Steps.Any(step => !string.Equals(
                step.BackupReference,
                ledgerTarget.BackupReference,
                StringComparison.Ordinal)))
        {
            return false;
        }

        if (ledgerTarget.TargetKind == SetupTargetKind.Env)
        {
            var changedMembers = ledgerTarget.Members
                .Where(member => member.Operation != SetupOperation.NoOp)
                .ToArray();
            if (changedMembers.Length != journalTarget.Steps.Count)
            {
                return false;
            }

            for (var stepIndex = 0; stepIndex < changedMembers.Length; stepIndex++)
            {
                if (!string.Equals(
                    changedMembers[stepIndex].SettingKey,
                    journalTarget.Steps[stepIndex].MemberKey,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // Ledger environment hashes cover the full allowlist, while journal hashes are per changed member.
            return true;
        }

        if (journalTarget.Steps.Count != 1 || journalTarget.Steps[0].MemberKey is not null)
        {
            return false;
        }

        var fileStep = journalTarget.Steps[0];
        return string.Equals(fileStep.PriorStateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal) &&
            (ledgerTarget.AppliedStateHash is null ||
             string.Equals(fileStep.DesiredStateHash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal));
    }

    private static bool RollbackOwnershipMatches(
        SetupChangeSetState state,
        SetupLedgerTarget target) => state switch
    {
        SetupChangeSetState.Applying =>
            target.AppliedStateHash is null &&
            target.BackupReference is not null &&
            target.RollbackStatus == SetupLedgerRollbackStatus.Pending,
        SetupChangeSetState.Compensating =>
            target.BackupReference is not null &&
            target.RollbackStatus == SetupLedgerRollbackStatus.Pending,
        SetupChangeSetState.Applied =>
            target.AppliedStateHash is not null &&
            target.BackupReference is not null &&
            target.RollbackStatus == SetupLedgerRollbackStatus.Pending,
        SetupChangeSetState.Restored => HasNoRollbackOwnership(target),
        SetupChangeSetState.RollingBack =>
            target.AppliedStateHash is not null &&
            target.BackupReference is not null &&
            target.RollbackStatus == SetupLedgerRollbackStatus.Pending,
        SetupChangeSetState.Partial =>
            target.AppliedStateHash is not null &&
            target.BackupReference is not null &&
            string.Equals(target.OutcomeCode, SetupCodes.InterruptedRecoveryFailed, StringComparison.Ordinal) &&
            target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable,
        SetupChangeSetState.RolledBack =>
            HasNoRollbackOwnership(target) ||
            target.AppliedStateHash is null &&
            target.BackupReference is null &&
            target.RollbackStatus == SetupLedgerRollbackStatus.Succeeded,
        _ => false,
    };

    private static bool HasNoRollbackOwnership(SetupLedgerTarget target) =>
        target.AppliedStateHash is null &&
        target.BackupReference is null &&
        target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable;

    private static bool TerminalLifecycleMatches(
        SetupChangeSetState state,
        SetupJournalOperation operation,
        SetupJournalPhase phase) => (operation, phase) switch
    {
        (SetupJournalOperation.Apply, SetupJournalPhase.Committed) =>
            state is SetupChangeSetState.Applying or SetupChangeSetState.Applied,
        (SetupJournalOperation.Apply, SetupJournalPhase.Restored) =>
            state is SetupChangeSetState.Compensating or SetupChangeSetState.Restored,
        (SetupJournalOperation.Rollback, SetupJournalPhase.Committed) =>
            state is SetupChangeSetState.RollingBack or SetupChangeSetState.Partial or SetupChangeSetState.RolledBack,
        _ => false,
    };

    private static bool IsTerminalLedgerState(SetupChangeSetState state) =>
        state is SetupChangeSetState.Applied or SetupChangeSetState.Restored or SetupChangeSetState.RolledBack;

    private static bool IsTerminalJournal(SetupTransactionJournal journal) =>
        journal.Phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored;

    private static bool IsMatchingTerminal(SetupLedgerChangeSet changeSet, SetupTransactionJournal journal) =>
        journal.Operation switch
        {
            SetupJournalOperation.Apply when journal.Phase == SetupJournalPhase.Committed =>
                changeSet.State == SetupChangeSetState.Applied,
            SetupJournalOperation.Apply when journal.Phase == SetupJournalPhase.Restored =>
                changeSet.State == SetupChangeSetState.Restored,
            SetupJournalOperation.Rollback when journal.Phase == SetupJournalPhase.Committed =>
                changeSet.State == SetupChangeSetState.RolledBack,
            _ => false,
        };

    private static bool IsTerminalApplyJournal(SetupTransactionJournal? journal) =>
        journal?.Operation == SetupJournalOperation.Apply &&
        journal.Phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored;

    private static SetupRecoveryOperation? OperationFor(SetupLedgerChangeSet changeSet) => changeSet.State switch
    {
        SetupChangeSetState.RollingBack or SetupChangeSetState.RolledBack => SetupRecoveryOperation.Rollback,
        SetupChangeSetState.NoChanges or SetupChangeSetState.Planned => null,
        _ => SetupRecoveryOperation.Apply,
    };

    private static SetupRecoveryOperation? OperationFor(
        SetupTransactionJournal? journal,
        SetupLedgerChangeSet changeSet) => journal?.Operation switch
    {
        SetupJournalOperation.Apply => SetupRecoveryOperation.Apply,
        SetupJournalOperation.Rollback => SetupRecoveryOperation.Rollback,
        _ => OperationFor(changeSet),
    };

    private static SetupRecoveryResult Recovered(
        SetupLedgerChangeSet changeSet,
        SetupRecoveryOperation operation) => new(
        SetupRecoveryDisposition.Recovered,
        changeSet.OutcomeCode == SetupCodes.RollbackSucceeded
            ? SetupCodes.RollbackSucceeded
            : operation == SetupRecoveryOperation.Apply
            ? SetupCodes.InterruptedApplyRecovered
            : SetupCodes.InterruptedRollbackRecovered,
        changeSet.ChangeSetId,
        operation,
        changeSet);

    private static SetupRecoveryResult Failed(
        string code,
        Guid? changeSetId,
        SetupRecoveryOperation? operation,
        SetupLedgerChangeSet? effectiveChangeSet) => new(
        SetupRecoveryDisposition.Failed,
        code,
        changeSetId,
        operation,
        effectiveChangeSet);

    private static UserEnvironmentValue DesiredEnvironmentValue(SetupPrivatePlanMember member) =>
        member.Operation == SetupOperation.Remove
            ? UserEnvironmentValue.Missing
            : UserEnvironmentValue.Present(member.DesiredValue!);

    private static bool HasSingleEnvironmentPhysicalTarget(SetupPrivatePlan plan)
    {
        return plan.Targets.Count(target => target.TargetKind == SetupTargetKind.Env) == 1;
    }

    private string ExpectedEnvironmentAppliedHash(Guid changeSetId, SetupPrivatePlanTarget target)
    {
        var names = target.Members.Select(member => member.SettingKey).ToArray();
        var backup = environmentStep.ReadBackup(paths.GetBackup(changeSetId, target.RecordId), names);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(EnvironmentAggregateHashDomain);
        AppendEnvironmentUInt32(hash, checked((uint)target.Members.Count));
        for (var index = 0; index < target.Members.Count; index++)
        {
            var member = target.Members[index];
            var value = member.Operation == SetupOperation.NoOp
                ? backup.Members[index].Value
                : DesiredEnvironmentValue(member);
            AppendEnvironmentString(hash, member.SettingKey);
            hash.AppendData([value.Exists ? (byte)1 : (byte)0]);
            if (value.Exists)
            {
                AppendEnvironmentString(hash, value.Value!);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendEnvironmentString(IncrementalHash hash, string value)
    {
        var bytes = EnvironmentCanonicalEncoding.GetBytes(value);
        AppendEnvironmentUInt32(hash, checked((uint)bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendEnvironmentUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string GetAllowedRoot(string targetPath) =>
        Path.GetDirectoryName(targetPath) ?? throw new FormatException();

    private static void RequireRegular(SetupPathMetadata metadata)
    {
        if (!metadata.Exists || metadata.Kind != SetupPathKind.File ||
            (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FormatException();
        }
    }

    private static void RequireEqual(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new FormatException();
        }
    }
}

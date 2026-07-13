using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed class SetupApplyCoordinator
{
    private static readonly HashSet<string> ApplyPreflightCodes = new(StringComparer.Ordinal)
    {
        SetupCodes.TargetNotInstalled,
        SetupCodes.UnsupportedVersion,
        SetupCodes.ManagedPolicyConflict,
        SetupCodes.MalformedSettings,
        SetupCodes.PermissionDenied,
        SetupCodes.UnsafePath,
        SetupCodes.StalePlan,
        SetupCodes.RecoveryRequired,
        SetupCodes.PortOwnedByForeignProcess,
        SetupCodes.InternalError,
    };

    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupTransactionJournalStore journalStore;
    private readonly ISetupApplyRevalidator revalidator;
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupApplyCoordinator(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore,
        ISetupApplyRevalidator revalidator)
    {
        this.platform = platform;
        this.paths = paths;
        this.planStore = planStore;
        this.ledgerStore = ledgerStore;
        this.journalStore = journalStore;
        this.revalidator = revalidator ?? throw new ArgumentNullException(nameof(revalidator));
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public SetupLedgerChangeSet Apply(SetupLock setupLock, Guid changeSetId)
    {
        ArgumentNullException.ThrowIfNull(setupLock);
        return setupLock.ExecuteWhileHeld(platform, paths, () => ApplyCore(setupLock, changeSetId));
    }

    private SetupLedgerChangeSet ApplyCore(SetupLock setupLock, Guid changeSetId)
    {
        var compensationEligible = false;
        try
        {
            var plan = planStore.Load(changeSetId) ?? throw new SetupApplyException(SetupCodes.RecoveryRequired);
            var ledger = ledgerStore.Load();
            var changeSetIndex = FindPlannedChangeSet(ledger, changeSetId);
            var planned = ledger.ChangeSets[changeSetIndex];
            SetupStorageValidation.ValidatePlanAndLedger(plan, planned);
            if (!ImmutablePlanAndLedgerMatch(plan, planned))
            {
                throw new SetupApplyException(SetupCodes.RecoveryRequired);
            }

            RunRevalidation(plan, planned);
            var captures = CaptureAndValidateBases(plan);
            var changedCaptures = captures.Where(capture => capture.HasChanges).ToArray();
            if (changedCaptures.Length == 0)
            {
                var noChanges = CreateNoChangesChangeSet(planned);
                SaveChangedSet(setupLock, ledger, changeSetIndex, noChanges);
                return noChanges;
            }

            var journalTargets = CreateJournalTargets(changedCaptures);
            var preparedJournalExists = platform.FileSystem
                .GetPathMetadata(paths.GetTransactionJournal(changeSetId))
                .Exists;
            if (preparedJournalExists)
            {
                journalStore.OpenOrCreatePrepared(
                    setupLock, changeSetId, SetupJournalOperation.Apply, journalTargets);
                CreateOrValidateBackups(plan, changedCaptures, requireExisting: true);
            }
            else
            {
                CreateOrValidateBackups(plan, changedCaptures, requireExisting: false);
                journalStore.OpenOrCreatePrepared(
                    setupLock, changeSetId, SetupJournalOperation.Apply, journalTargets);
            }

            compensationEligible = true;
            platform.Execution.Checkpoint(SetupFaultPoint.AfterJournalPreparedBeforeLedger);

            var applying = CreateApplyingChangeSet(planned, changedCaptures);
            SaveChangedSet(setupLock, ledger, changeSetIndex, applying);
            journalStore.MarkTransactionPhase(setupLock, changeSetId, SetupJournalPhase.Applying);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent);

            ApplyForwardSteps(setupLock, plan, changedCaptures);
            var appliedHashes = VerifyDesiredStates(plan, changedCaptures);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterCompletionBeforeCommit);
            journalStore.MarkTransactionPhase(setupLock, changeSetId, SetupJournalPhase.Committed);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterCommitBeforeLedger);

            var applied = CreateAppliedChangeSet(applying, appliedHashes);
            SaveChangedSet(setupLock, ledger, changeSetIndex, applied);
            var terminalJournal = journalStore.Load(changeSetId) ?? throw new SetupApplyException(SetupCodes.InternalError);
            if (terminalJournal.EnvironmentNotification == SetupEnvironmentNotification.Pending)
            {
                environmentStep.NotifyFinalState();
                journalStore.MarkEnvironmentNotificationCompleted(setupLock, changeSetId);
            }

            return applied;
        }
        catch (Exception exception)
        {
            var outcomeCode = MapApplyFailure(exception);
            if (!compensationEligible && outcomeCode == SetupCodes.StalePlan &&
                !TryPersistStalePlanOutcome(setupLock, changeSetId))
            {
                throw new SetupApplyException(SetupCodes.InternalError);
            }

            if (compensationEligible)
            {
                var compensation = Compensate(setupLock, changeSetId, outcomeCode);
                if (compensation == CompensationOutcome.Partial)
                {
                    throw new SetupApplyException(SetupCodes.PartialApply);
                }

                if (compensation == CompensationOutcome.RecoveryRequired)
                {
                    throw new SetupApplyException(SetupCodes.RecoveryRequired);
                }
            }

            throw new SetupApplyException(outcomeCode);
        }
    }

    private bool TryPersistStalePlanOutcome(SetupLock setupLock, Guid changeSetId)
    {
        try
        {
            var ledger = ledgerStore.Load();
            var changeSetIndex = FindPlannedChangeSet(ledger, changeSetId);
            var stale = ledger.ChangeSets[changeSetIndex] with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = SetupCodes.StalePlan,
            };
            SaveChangedSet(setupLock, ledger, changeSetIndex, stale);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private CompensationOutcome Compensate(SetupLock setupLock, Guid changeSetId, string forwardOutcome)
    {
        if (!TryLoadJournalAfterFailure(changeSetId, out var initialJournal))
        {
            return CompensationOutcome.RecoveryRequired;
        }

        if (initialJournal is null)
        {
            return IsLedgerStillPlanned(changeSetId)
                ? CompensationOutcome.OriginalFailure
                : CompensationOutcome.RecoveryRequired;
        }

        if (initialJournal.Operation != SetupJournalOperation.Apply)
        {
            return CompensationOutcome.RecoveryRequired;
        }

        if (initialJournal.Phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored)
        {
            return CompensationOutcome.OriginalFailure;
        }

        if (initialJournal.Phase == SetupJournalPhase.Partial)
        {
            return CompensationOutcome.Partial;
        }

        try
        {
            var plan = planStore.Load(changeSetId);
            if (plan is null || !EnsureJournalCompensating(setupLock, changeSetId, initialJournal))
            {
                return CompensationOutcome.RecoveryRequired;
            }

            if (!EnsureLedgerCompensating(setupLock, changeSetId))
            {
                return PersistPartial(
                    setupLock,
                    changeSetId,
                    new CompensationFailure(null, SetupCodes.InternalError, SetupLedgerRollbackStatus.Failed))
                    ? CompensationOutcome.Partial
                    : CompensationOutcome.RecoveryRequired;
            }

            if (!TryLoadJournalOnce(changeSetId, out var journal) || journal is null ||
                journal.Phase != SetupJournalPhase.Compensating)
            {
                return CompensationOutcome.RecoveryRequired;
            }

            var failures = new Dictionary<Guid, CompensationFailure>();
            for (var targetIndex = journal.Targets.Count - 1; targetIndex >= 0; targetIndex--)
            {
                var target = journal.Targets[targetIndex];
                var planTarget = plan.Targets.Single(candidate => candidate.RecordId == target.RecordId);
                for (var stepIndex = target.Steps.Count - 1; stepIndex >= 0; stepIndex--)
                {
                    var step = ReloadStep(changeSetId, target.RecordId, target.Steps[stepIndex].MemberKey);
                    if (step.Phase == SetupJournalStepPhase.Pending)
                    {
                        continue;
                    }

                    if (step.Phase == SetupJournalStepPhase.RestoreCompleted)
                    {
                        continue;
                    }

                    var failure = CompensateStep(setupLock, changeSetId, planTarget, step);
                    if (failure is not null)
                    {
                        if (failure.RequiresRecovery)
                        {
                            return CompensationOutcome.RecoveryRequired;
                        }

                        if (!failure.SafeToPreserveAndContinue || failure.RecordId is null)
                        {
                            if (failure.RecordId is not null)
                            {
                                RecordEffectiveFailure(failures, failure);
                                return PersistPartial(setupLock, changeSetId, failures.Values)
                                    ? CompensationOutcome.Partial
                                    : CompensationOutcome.RecoveryRequired;
                            }

                            var terminalFailures = failures.Values.Append(failure).ToArray();
                            return PersistPartial(setupLock, changeSetId, terminalFailures)
                                ? CompensationOutcome.Partial
                                : CompensationOutcome.RecoveryRequired;
                        }

                        RecordEffectiveFailure(failures, failure);
                    }
                }
            }

            VerifyAllPrevious(plan, journal, failures);
            if (failures.Count > 0)
            {
                return PersistPartial(setupLock, changeSetId, failures.Values)
                    ? CompensationOutcome.Partial
                    : CompensationOutcome.RecoveryRequired;
            }

            if (!TryMarkJournalPhase(setupLock, changeSetId, SetupJournalPhase.Restored))
            {
                return PersistPartial(
                    setupLock,
                    changeSetId,
                    new CompensationFailure(null, SetupCodes.InternalError, SetupLedgerRollbackStatus.Failed))
                    ? CompensationOutcome.Partial
                    : CompensationOutcome.RecoveryRequired;
            }

            if (!PersistRestoredLedger(setupLock, changeSetId, forwardOutcome))
            {
                return CompensationOutcome.RecoveryRequired;
            }

            if (!TryLoadJournalOnce(changeSetId, out var terminal) || terminal is null ||
                terminal.Phase != SetupJournalPhase.Restored)
            {
                return CompensationOutcome.RecoveryRequired;
            }

            if (terminal.EnvironmentNotification == SetupEnvironmentNotification.Pending)
            {
                try
                {
                    environmentStep.NotifyFinalState();
                    journalStore.MarkEnvironmentNotificationCompleted(setupLock, changeSetId);
                }
                catch (Exception)
                {
                    // The restored ledger and pending notification are recovery evidence.
                }
            }

            return CompensationOutcome.Restored;
        }
        catch (Exception)
        {
            return PersistPartial(
                setupLock,
                changeSetId,
                new CompensationFailure(null, SetupCodes.InternalError, SetupLedgerRollbackStatus.Failed))
                ? CompensationOutcome.Partial
                : CompensationOutcome.RecoveryRequired;
        }
    }

    private bool EnsureJournalCompensating(
        SetupLock setupLock,
        Guid changeSetId,
        SetupTransactionJournal initial)
    {
        var current = initial;

        if (current.Phase == SetupJournalPhase.Prepared &&
            !TryMarkJournalPhase(setupLock, changeSetId, SetupJournalPhase.Applying))
        {
            return false;
        }

        if (!TryLoadJournalOnce(changeSetId, out current) || current is null)
        {
            return false;
        }

        return current.Phase == SetupJournalPhase.Compensating ||
            current.Phase == SetupJournalPhase.Applying &&
            TryMarkJournalPhase(setupLock, changeSetId, SetupJournalPhase.Compensating);
    }

    private bool TryMarkJournalPhase(SetupLock setupLock, Guid changeSetId, SetupJournalPhase phase)
    {
        try
        {
            journalStore.MarkTransactionPhase(setupLock, changeSetId, phase);
            return true;
        }
        catch (Exception)
        {
            return TryLoadJournalOnce(changeSetId, out var current) && current?.Phase == phase;
        }
    }

    private bool EnsureLedgerCompensating(SetupLock setupLock, Guid changeSetId)
    {
        var ledger = ledgerStore.Load();
        var index = FindChangeSet(ledger, changeSetId);
        var current = ledger.ChangeSets[index];
        if (current.State == SetupChangeSetState.Compensating)
        {
            return true;
        }

        if (current.State is not (SetupChangeSetState.Planned or SetupChangeSetState.Applying))
        {
            return false;
        }

        var journalRecordIds = journalStore.Load(changeSetId)!.Targets.Select(target => target.RecordId).ToHashSet();
        var compensating = current with
        {
            UpdatedAt = platform.Clock.UtcNow,
            State = SetupChangeSetState.Compensating,
            Targets = current.Targets.Select(target => journalRecordIds.Contains(target.RecordId)
                ? target with
                {
                    BackupReference = target.RecordId.ToString("D"),
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                }
                : target).ToArray(),
        };
        try
        {
            SaveChangedSet(setupLock, ledger, index, compensating);
            return true;
        }
        catch (Exception)
        {
            try
            {
                return ledgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == changeSetId).State ==
                    SetupChangeSetState.Compensating;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private CompensationFailure? CompensateStep(
        SetupLock setupLock,
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        SetupJournalStep step)
    {
        var classification = Classify(target, step);
        if (classification == CompensationClassification.ThirdParty)
        {
            return new CompensationFailure(
                target.RecordId,
                SetupCodes.RollbackStale,
                SetupLedgerRollbackStatus.Stale,
                SafeToPreserveAndContinue: true);
        }

        if (classification == CompensationClassification.Error)
        {
            return new CompensationFailure(
                target.RecordId,
                SetupCodes.InternalError,
                SetupLedgerRollbackStatus.Failed,
                SafeToPreserveAndContinue: true);
        }

        if (step.Phase is SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted)
        {
            if (!TryMarkStepPhase(
                    setupLock,
                    changeSetId,
                    target.RecordId,
                    step.MemberKey,
                    step.Phase,
                    SetupJournalStepPhase.RestoreStarted))
            {
                return new CompensationFailure(
                    target.RecordId,
                    SetupCodes.InternalError,
                    SetupLedgerRollbackStatus.Failed,
                    RequiresRecovery: true);
            }
        }

        if (classification == CompensationClassification.Desired)
        {
            try
            {
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreIntentBeforeRestore);
            }
            catch (Exception)
            {
                return ClassifyRestoreFailure(
                    setupLock,
                    changeSetId,
                    target,
                    step,
                    safeToPreserveAndContinue: false);
            }

            try
            {
                RestoreStep(changeSetId, target, step);
            }
            catch (Exception)
            {
                return ClassifyRestoreFailure(
                    setupLock,
                    changeSetId,
                    target,
                    step,
                    safeToPreserveAndContinue: true);
            }

            try
            {
                platform.Execution.Checkpoint(SetupFaultPoint.AfterRestoreBeforeCompletion);
            }
            catch (Exception)
            {
                return ClassifyRestoreFailure(
                    setupLock,
                    changeSetId,
                    target,
                    step,
                    safeToPreserveAndContinue: false);
            }
        }

        return TryCompleteRestore(setupLock, changeSetId, target.RecordId, step.MemberKey)
            ? null
            : new CompensationFailure(
                target.RecordId,
                SetupCodes.InternalError,
                SetupLedgerRollbackStatus.Failed,
                RequiresRecovery: true);
    }

    private CompensationFailure? ClassifyRestoreFailure(
        SetupLock setupLock,
        Guid changeSetId,
        SetupPrivatePlanTarget target,
        SetupJournalStep step,
        bool safeToPreserveAndContinue)
    {
        var afterFailure = Classify(target, step);
        if (afterFailure == CompensationClassification.Prior)
        {
            return TryCompleteRestore(setupLock, changeSetId, target.RecordId, step.MemberKey)
                ? null
                : new CompensationFailure(
                    target.RecordId,
                    SetupCodes.InternalError,
                    SetupLedgerRollbackStatus.Failed,
                    RequiresRecovery: true);
        }

        return afterFailure == CompensationClassification.ThirdParty
            ? new CompensationFailure(
                target.RecordId,
                SetupCodes.RollbackStale,
                SetupLedgerRollbackStatus.Stale,
                SafeToPreserveAndContinue: safeToPreserveAndContinue)
            : new CompensationFailure(
                target.RecordId,
                SetupCodes.InternalError,
                SetupLedgerRollbackStatus.Failed,
                SafeToPreserveAndContinue: safeToPreserveAndContinue);
    }

    private bool TryMarkStepPhase(
        SetupLock setupLock,
        Guid changeSetId,
        Guid recordId,
        string? memberKey,
        SetupJournalStepPhase expected,
        SetupJournalStepPhase next)
    {
        try
        {
            journalStore.MarkStepPhase(setupLock, changeSetId, recordId, memberKey, expected, next);
            return true;
        }
        catch (Exception)
        {
            try
            {
                return ReloadStep(changeSetId, recordId, memberKey).Phase == next;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private bool TryCompleteRestore(SetupLock setupLock, Guid changeSetId, Guid recordId, string? memberKey)
    {
        try
        {
            journalStore.MarkStepPhase(
                setupLock,
                changeSetId,
                recordId,
                memberKey,
                SetupJournalStepPhase.RestoreStarted,
                SetupJournalStepPhase.RestoreCompleted);
            return true;
        }
        catch (Exception)
        {
            try
            {
                return ReloadStep(changeSetId, recordId, memberKey).Phase ==
                    SetupJournalStepPhase.RestoreCompleted;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private void RestoreStep(Guid changeSetId, SetupPrivatePlanTarget target, SetupJournalStep step)
    {
        var backupPath = paths.GetBackup(changeSetId, target.RecordId);
        if (target.TargetKind == SetupTargetKind.Env)
        {
            environmentStep.RestoreMember(
                step.MemberKey!, backupPath, step.DesiredStateHash, step.PriorStateHash);
        }
        else
        {
            fileStep.Restore(
                GetAllowedRoot(target.TargetLocation),
                target.TargetLocation,
                backupPath,
                step.DesiredStateHash,
                step.PriorStateHash);
        }
    }

    private CompensationClassification Classify(SetupPrivatePlanTarget target, SetupJournalStep step)
    {
        try
        {
            string currentHash;
            if (target.TargetKind == SetupTargetKind.Env)
            {
                currentHash = environmentStep.Capture([step.MemberKey!]).Members[0].Hash;
            }
            else
            {
                currentHash = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation).Hash;
            }

            if (string.Equals(currentHash, step.PriorStateHash, StringComparison.Ordinal))
            {
                return CompensationClassification.Prior;
            }

            return string.Equals(currentHash, step.DesiredStateHash, StringComparison.Ordinal)
                ? CompensationClassification.Desired
                : CompensationClassification.ThirdParty;
        }
        catch (Exception)
        {
            return CompensationClassification.Error;
        }
    }

    private void VerifyAllPrevious(
        SetupPrivatePlan plan,
        SetupTransactionJournal journal,
        IDictionary<Guid, CompensationFailure> failures)
    {
        foreach (var journalTarget in journal.Targets)
        {
            var target = plan.Targets.Single(candidate => candidate.RecordId == journalTarget.RecordId);
            try
            {
                var matches = target.TargetKind == SetupTargetKind.Env
                    ? string.Equals(
                        environmentStep.Capture(target.Members.Select(member => member.SettingKey).ToArray()).AggregateHash,
                        target.BaseStateHash,
                        StringComparison.Ordinal)
                    : string.Equals(
                        fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation).Hash,
                        target.BaseStateHash,
                        StringComparison.Ordinal);
                if (!matches)
                {
                    RecordEffectiveFailure(failures, new CompensationFailure(
                        target.RecordId,
                        SetupCodes.RollbackStale,
                        SetupLedgerRollbackStatus.Stale));
                }
            }
            catch (Exception)
            {
                RecordEffectiveFailure(failures, new CompensationFailure(
                    target.RecordId,
                    SetupCodes.InternalError,
                    SetupLedgerRollbackStatus.Failed));
            }
        }
    }

    private static void RecordEffectiveFailure(
        IDictionary<Guid, CompensationFailure> failures,
        CompensationFailure candidate)
    {
        var recordId = candidate.RecordId!.Value;
        if (!failures.TryGetValue(recordId, out var current) ||
            FailureStrength(candidate) >= FailureStrength(current))
        {
            failures[recordId] = candidate;
        }
    }

    private static int FailureStrength(CompensationFailure failure) => failure.RollbackStatus switch
    {
        SetupLedgerRollbackStatus.Failed => 1,
        SetupLedgerRollbackStatus.Stale => 0,
        _ => -1,
    };

    private bool PersistPartial(SetupLock setupLock, Guid changeSetId, CompensationFailure failure)
    {
        return PersistPartial(setupLock, changeSetId, [failure]);
    }

    private bool PersistPartial(
        SetupLock setupLock,
        Guid changeSetId,
        IReadOnlyCollection<CompensationFailure> failures)
    {
        SetupTransactionJournal? journal;
        if (!TryLoadJournalOnce(changeSetId, out journal) || journal is null)
        {
            return false;
        }

        if (journal.Phase == SetupJournalPhase.Compensating)
        {
            if (!TryMarkJournalPhase(setupLock, changeSetId, SetupJournalPhase.Partial))
            {
                return false;
            }
        }
        else if (journal.Phase != SetupJournalPhase.Partial)
        {
            return false;
        }

        return PersistPartialLedger(
            setupLock,
            changeSetId,
            failures);
    }

    private bool PersistPartialLedger(
        SetupLock setupLock,
        Guid changeSetId,
        IReadOnlyCollection<CompensationFailure> failures)
    {
        SetupOwnershipLedger ledger;
        try
        {
            ledger = ledgerStore.Load();
        }
        catch (Exception)
        {
            return false;
        }

        var index = FindChangeSet(ledger, changeSetId);
        var current = ledger.ChangeSets[index];
        if (current.State is SetupChangeSetState.Applied or SetupChangeSetState.Restored or
            SetupChangeSetState.NoChanges or SetupChangeSetState.RolledBack)
        {
            return false;
        }

        var partial = current with
        {
            UpdatedAt = platform.Clock.UtcNow,
            OutcomeCode = SetupCodes.PartialApply,
            State = SetupChangeSetState.Partial,
            Targets = current.Targets.Select(target =>
            {
                var failure = failures.FirstOrDefault(candidate =>
                    candidate.RecordId == target.RecordId) ??
                    failures.FirstOrDefault(candidate => candidate.RecordId is null);
                return failure is null
                    ? target
                    : target with
                    {
                        OutcomeCode = failure.Code,
                        RollbackStatus = failure.RollbackStatus,
                    };
            }).ToArray(),
        };
        try
        {
            SaveChangedSet(setupLock, ledger, index, partial);
            return true;
        }
        catch (Exception)
        {
            try
            {
                return ledgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == changeSetId).State ==
                    SetupChangeSetState.Partial;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private bool PersistRestoredLedger(SetupLock setupLock, Guid changeSetId, string forwardOutcome)
    {
        var ledger = ledgerStore.Load();
        var index = FindChangeSet(ledger, changeSetId);
        var current = ledger.ChangeSets[index];
        var changedRecordIds = journalStore.Load(changeSetId)!.Targets
            .Select(target => target.RecordId)
            .ToHashSet();
        var restored = current with
        {
            UpdatedAt = platform.Clock.UtcNow,
            OutcomeCode = forwardOutcome,
            State = SetupChangeSetState.Restored,
            Targets = current.Targets.Select(target => changedRecordIds.Contains(target.RecordId)
                ? target with
                {
                    AppliedStateHash = null,
                    BackupReference = null,
                    OutcomeCode = forwardOutcome,
                    RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                }
                : target).ToArray(),
        };
        try
        {
            SaveChangedSet(setupLock, ledger, index, restored);
            return true;
        }
        catch (Exception)
        {
            try
            {
                return ledgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == changeSetId).State ==
                    SetupChangeSetState.Restored;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private SetupJournalStep ReloadStep(Guid changeSetId, Guid recordId, string? memberKey) =>
        journalStore.Load(changeSetId)!.Targets.Single(target => target.RecordId == recordId).Steps
            .Single(step => string.Equals(step.MemberKey, memberKey, StringComparison.Ordinal));

    private bool TryLoadJournalAfterFailure(Guid changeSetId, out SetupTransactionJournal? journal)
    {
        if (TryLoadJournalOnce(changeSetId, out journal))
        {
            return true;
        }

        return TryLoadJournalOnce(changeSetId, out journal);
    }

    private bool TryLoadJournalOnce(Guid changeSetId, out SetupTransactionJournal? journal)
    {
        try
        {
            journal = journalStore.Load(changeSetId);
            return true;
        }
        catch (Exception)
        {
            journal = null;
            return false;
        }
    }

    private bool IsLedgerStillPlanned(Guid changeSetId)
    {
        try
        {
            return ledgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == changeSetId).State ==
                SetupChangeSetState.Planned;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int FindChangeSet(SetupOwnershipLedger ledger, Guid changeSetId)
    {
        for (var index = 0; index < ledger.ChangeSets.Count; index++)
        {
            if (ledger.ChangeSets[index].ChangeSetId == changeSetId)
            {
                return index;
            }
        }

        throw new SetupApplyException(SetupCodes.InvalidArguments);
    }

    private static string MapApplyFailure(Exception exception) => exception switch
    {
        SetupApplyException applyException when SetupCodes.ResultCodes.Contains(applyException.Code, StringComparer.Ordinal) =>
            applyException.Code,
        SetupStorageException storageException => MapStorageCode(storageException.Code),
        SetupFileStepException fileException => MapMutationCode(fileException.Code),
        SetupEnvironmentStepException environmentException => MapMutationCode(environmentException.Code),
        _ => SetupCodes.InternalError,
    };

    private void RunRevalidation(SetupPrivatePlan plan, SetupLedgerChangeSet planned)
    {
        try
        {
            revalidator.Revalidate(plan, planned);
        }
        catch (SetupApplyException exception)
        {
            throw ApplyPreflightCodes.Contains(exception.Code)
                ? exception
                : new SetupApplyException(SetupCodes.InternalError);
        }
    }

    private IReadOnlyList<TargetCapture> CaptureAndValidateBases(SetupPrivatePlan plan)
    {
        var captures = new List<TargetCapture>();
        foreach (var target in plan.Targets)
        {
            switch (target.TargetKind)
            {
                case SetupTargetKind.Env:
                {
                    var capture = environmentStep.Capture(target.Members.Select(member => member.SettingKey).ToArray());
                    if (!string.Equals(capture.AggregateHash, target.BaseStateHash, StringComparison.Ordinal))
                    {
                        throw new SetupApplyException(SetupCodes.StalePlan);
                    }

                    ValidateEnvironmentOperationCoherence(target, capture);
                    var changedMembers = target.Members
                        .Select((member, index) => (member, index))
                        .Where(item => item.member.Operation != SetupOperation.NoOp)
                        .Select(item => item.index)
                        .ToArray();
                    captures.Add(new TargetCapture(target, null, capture, false, changedMembers));
                    break;
                }
                case SetupTargetKind.Guidance:
                    break;
                default:
                {
                    AtomicFileCapture capture;
                    try
                    {
                        capture = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation);
                    }
                    catch (SetupFileStepException exception)
                    {
                        throw new SetupApplyException(MapPreflightStepCode(exception.Code));
                    }

                    if (!string.Equals(capture.Hash, target.BaseStateHash, StringComparison.Ordinal))
                    {
                        throw new SetupApplyException(SetupCodes.StalePlan);
                    }

                    var desiredHash = SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState));
                    var fileChanged = !string.Equals(capture.Hash, desiredHash, StringComparison.Ordinal);
                    var membersDeclareChange = target.Members.Any(member => member.Operation != SetupOperation.NoOp);
                    if (fileChanged != membersDeclareChange)
                    {
                        throw new SetupApplyException(SetupCodes.RecoveryRequired);
                    }

                    captures.Add(new TargetCapture(
                        target,
                        capture,
                        null,
                        membersDeclareChange,
                        []));
                    break;
                }
            }
        }

        return captures;
    }

    private static void ValidateEnvironmentOperationCoherence(
        SetupPrivatePlanTarget target,
        UserEnvironmentCapture capture)
    {
        for (var index = 0; index < target.Members.Count; index++)
        {
            var member = target.Members[index];
            var expected = EnvironmentOperation(
                capture.Members[index].Value,
                SetupEnvironmentPlanValue.Desired(member));
            if (member.Operation != expected)
            {
                throw new SetupApplyException(SetupCodes.RecoveryRequired);
            }
        }
    }

    private static SetupOperation EnvironmentOperation(
        UserEnvironmentValue current,
        UserEnvironmentValue desired)
    {
        if (!current.Exists)
        {
            return desired.Exists ? SetupOperation.Add : SetupOperation.NoOp;
        }

        if (!desired.Exists)
        {
            return SetupOperation.Remove;
        }

        return string.Equals(current.Value, desired.Value, StringComparison.Ordinal)
            ? SetupOperation.NoOp
            : SetupOperation.Replace;
    }

    private SetupLedgerChangeSet CreateNoChangesChangeSet(SetupLedgerChangeSet planned) => planned with
    {
        UpdatedAt = platform.Clock.UtcNow,
        OutcomeCode = SetupCodes.NoChanges,
        State = SetupChangeSetState.NoChanges,
        Targets = planned.Targets.Select(target => target with { OutcomeCode = SetupCodes.NoChanges }).ToArray(),
    };

    private void CreateOrValidateBackups(
        SetupPrivatePlan plan,
        IReadOnlyList<TargetCapture> captures,
        bool requireExisting)
    {
        if (requireExisting)
        {
            foreach (var capture in captures)
            {
                var backupPath = paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId);
                if (!platform.FileSystem.GetPathMetadata(backupPath).Exists)
                {
                    throw new SetupApplyException(SetupCodes.RecoveryRequired);
                }

                CreateOrValidateBackup(capture, backupPath);
            }

            return;
        }

        var existing = new List<TargetCapture>();
        var missing = new List<TargetCapture>();
        foreach (var capture in captures)
        {
            var backupPath = paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId);
            if (platform.FileSystem.GetPathMetadata(backupPath).Exists)
            {
                existing.Add(capture);
            }
            else
            {
                missing.Add(capture);
            }
        }

        foreach (var capture in existing)
        {
            CreateOrValidateBackup(
                capture,
                paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId));
        }

        if (missing.Count > 0)
        {
            platform.FileSystem.CreateDirectory(paths.Backups);
            platform.FileSystem.CreateDirectory(Path.Combine(paths.Backups, plan.ChangeSetId.ToString("D")));
            foreach (var capture in missing)
            {
                CreateOrValidateBackup(
                    capture,
                    paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId));
            }
        }

        foreach (var capture in captures)
        {
            CreateOrValidateBackup(
                capture,
                paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId));
        }
    }

    private void CreateOrValidateBackup(TargetCapture capture, string backupPath)
    {
        if (capture.File is not null)
        {
            fileStep.CreateOrValidateBackup(backupPath, capture.File);
        }
        else
        {
            environmentStep.CreateOrValidateBackup(backupPath, capture.Environment!);
        }
    }

    private IReadOnlyList<SetupJournalTarget> CreateJournalTargets(
        IReadOnlyList<TargetCapture> captures) => captures.Select(capture =>
    {
        var backupReference = capture.Target.RecordId.ToString("D");
        IReadOnlyList<SetupJournalStep> steps;
        if (capture.File is not null)
        {
            steps = [new SetupJournalStep(
                null,
                capture.File.Hash,
                SetupHash.File(true, Encoding.UTF8.GetBytes(capture.Target.DesiredState)),
                backupReference,
                SetupJournalStepPhase.Pending)];
        }
        else
        {
            steps = capture.ChangedMemberIndices.Select(index =>
            {
                var member = capture.Target.Members[index];
                return new SetupJournalStep(
                    member.SettingKey,
                    capture.Environment!.Members[index].Hash,
                    environmentStep.HashMember(member.SettingKey, SetupEnvironmentPlanValue.Desired(member)),
                    backupReference,
                    SetupJournalStepPhase.Pending);
            }).ToArray();
        }

        return new SetupJournalTarget(capture.Target.RecordId, capture.Target.TargetKind, steps);
    }).ToArray();

    private SetupLedgerChangeSet CreateApplyingChangeSet(
        SetupLedgerChangeSet planned,
        IReadOnlyList<TargetCapture> changedCaptures)
    {
        var changedRecordIds = changedCaptures.Select(capture => capture.Target.RecordId).ToHashSet();
        return planned with
        {
            UpdatedAt = platform.Clock.UtcNow,
            State = SetupChangeSetState.Applying,
            Targets = planned.Targets.Select(target => changedRecordIds.Contains(target.RecordId)
                ? target with
                {
                    BackupReference = target.RecordId.ToString("D"),
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                }
                : target).ToArray(),
        };
    }

    private void ApplyForwardSteps(
        SetupLock setupLock,
        SetupPrivatePlan plan,
        IReadOnlyList<TargetCapture> captures)
    {
        foreach (var capture in captures)
        {
            if (capture.File is not null)
            {
                MarkMutationStarted(setupLock, plan.ChangeSetId, capture.Target.RecordId, null);
                fileStep.Apply(
                    GetAllowedRoot(capture.Target.TargetLocation),
                    capture.Target.TargetLocation,
                    capture.File.Hash,
                    Encoding.UTF8.GetBytes(capture.Target.DesiredState));
                MarkMutationCompleted(setupLock, plan.ChangeSetId, capture.Target.RecordId, null);
                continue;
            }

            foreach (var index in capture.ChangedMemberIndices)
            {
                var member = capture.Target.Members[index];
                MarkMutationStarted(setupLock, plan.ChangeSetId, capture.Target.RecordId, member.SettingKey);
                environmentStep.ApplyMember(
                    member.SettingKey,
                    capture.Environment!.Members[index].Hash,
                    SetupEnvironmentPlanValue.Desired(member));
                MarkMutationCompleted(setupLock, plan.ChangeSetId, capture.Target.RecordId, member.SettingKey);
            }
        }
    }

    private IReadOnlyDictionary<Guid, string> VerifyDesiredStates(
        SetupPrivatePlan plan,
        IReadOnlyList<TargetCapture> changedCaptures)
    {
        var changedRecordIds = changedCaptures.Select(capture => capture.Target.RecordId).ToHashSet();
        var hashes = new Dictionary<Guid, string>();
        foreach (var target in plan.Targets)
        {
            switch (target.TargetKind)
            {
                case SetupTargetKind.Guidance:
                    continue;
                case SetupTargetKind.Env:
                {
                    var capture = environmentStep.Capture(target.Members.Select(member => member.SettingKey).ToArray());
                    for (var index = 0; index < target.Members.Count; index++)
                    {
                        var member = target.Members[index];
                        var desiredHash = environmentStep.HashMember(
                            member.SettingKey,
                            SetupEnvironmentPlanValue.Desired(member));
                        if (!string.Equals(capture.Members[index].Hash, desiredHash, StringComparison.Ordinal))
                        {
                            throw new SetupApplyException(SetupCodes.InternalError);
                        }
                    }

                    if (changedRecordIds.Contains(target.RecordId))
                    {
                        hashes.Add(target.RecordId, capture.AggregateHash);
                    }
                    break;
                }
                default:
                {
                    var capture = fileStep.Capture(GetAllowedRoot(target.TargetLocation), target.TargetLocation);
                    var desiredHash = SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState));
                    if (!string.Equals(capture.Hash, desiredHash, StringComparison.Ordinal))
                    {
                        throw new SetupApplyException(SetupCodes.InternalError);
                    }

                    if (changedRecordIds.Contains(target.RecordId))
                    {
                        hashes.Add(target.RecordId, capture.Hash);
                    }
                    break;
                }
            }
        }

        return hashes;
    }

    private SetupLedgerChangeSet CreateAppliedChangeSet(
        SetupLedgerChangeSet applying,
        IReadOnlyDictionary<Guid, string> appliedHashes) => applying with
    {
        UpdatedAt = platform.Clock.UtcNow,
        OutcomeCode = SetupCodes.ApplySucceeded,
        State = SetupChangeSetState.Applied,
        Targets = applying.Targets.Select(target => appliedHashes.TryGetValue(target.RecordId, out var hash)
            ? target with
            {
                AppliedStateHash = hash,
                OutcomeCode = SetupCodes.ApplySucceeded,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
            }
            : target).ToArray(),
    };

    private void MarkMutationStarted(SetupLock setupLock, Guid changeSetId, Guid recordId, string? memberKey)
    {
        journalStore.MarkStepPhase(setupLock, changeSetId, recordId, memberKey,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        platform.Execution.Checkpoint(SetupFaultPoint.AfterMutationIntentBeforeMutation);
    }

    private void MarkMutationCompleted(SetupLock setupLock, Guid changeSetId, Guid recordId, string? memberKey)
    {
        platform.Execution.Checkpoint(SetupFaultPoint.AfterMutationBeforeCompletion);
        journalStore.MarkStepPhase(setupLock, changeSetId, recordId, memberKey,
            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
    }

    private void SaveChangedSet(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        int changeSetIndex,
        SetupLedgerChangeSet changeSet)
    {
        var changeSets = ledger.ChangeSets.ToArray();
        changeSets[changeSetIndex] = changeSet;
        ledgerStore.Save(setupLock, ledger with { ChangeSets = changeSets });
    }

    private static int FindPlannedChangeSet(SetupOwnershipLedger ledger, Guid changeSetId)
    {
        var index = -1;
        for (var candidate = 0; candidate < ledger.ChangeSets.Count; candidate++)
        {
            if (ledger.ChangeSets[candidate].ChangeSetId == changeSetId)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0 || ledger.ChangeSets[index].State != SetupChangeSetState.Planned)
        {
            throw new SetupApplyException(SetupCodes.InvalidArguments);
        }

        return index;
    }

    private static bool ImmutablePlanAndLedgerMatch(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
    {
        if (plan.ChangeSetId != changeSet.ChangeSetId ||
            !string.Equals(plan.Adapter, changeSet.Adapter, StringComparison.Ordinal) ||
            !string.Equals(plan.SelectedTarget, changeSet.SelectedTarget, StringComparison.Ordinal) ||
            plan.CreatedAt != changeSet.CreatedAt ||
            plan.CreatedAt != changeSet.UpdatedAt ||
            !string.Equals(plan.ToolVersion, changeSet.ToolVersion, StringComparison.Ordinal) ||
            plan.Targets.Count != changeSet.Targets.Count)
        {
            return false;
        }

        for (var targetIndex = 0; targetIndex < plan.Targets.Count; targetIndex++)
        {
            var planTarget = plan.Targets[targetIndex];
            var ledgerTarget = changeSet.Targets[targetIndex];
            if (planTarget.RecordId != ledgerTarget.RecordId ||
                planTarget.TargetKind != ledgerTarget.TargetKind ||
                !string.Equals(planTarget.BaseStateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal) ||
                planTarget.Members.Count != ledgerTarget.Members.Count)
            {
                return false;
            }

            for (var memberIndex = 0; memberIndex < planTarget.Members.Count; memberIndex++)
            {
                var planMember = planTarget.Members[memberIndex];
                var ledgerMember = ledgerTarget.Members[memberIndex];
                if (!string.Equals(planMember.SettingKey, ledgerMember.SettingKey, StringComparison.Ordinal) ||
                    planMember.Operation != ledgerMember.Operation)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string GetAllowedRoot(string targetPath) =>
        Path.GetDirectoryName(targetPath) ?? throw new SetupApplyException(SetupCodes.UnsafePath);

    private static string MapStorageCode(string code) => code switch
    {
        SetupCodes.LedgerCorrupt => SetupCodes.LedgerCorrupt,
        SetupCodes.LedgerVersionUnsupported => SetupCodes.LedgerVersionUnsupported,
        SetupCodes.RecoveryRequired => SetupCodes.RecoveryRequired,
        SetupStorageCodes.PlanLedgerMismatch => SetupCodes.RecoveryRequired,
        _ => SetupCodes.InternalError,
    };

    private static string MapPreflightStepCode(string code) => code switch
    {
        SetupCodes.UnsafePath => SetupCodes.UnsafePath,
        SetupCodes.StalePlan => SetupCodes.StalePlan,
        _ => SetupCodes.InternalError,
    };

    private static string MapMutationCode(string _) => SetupCodes.InternalError;

    private sealed record TargetCapture(
        SetupPrivatePlanTarget Target,
        AtomicFileCapture? File,
        UserEnvironmentCapture? Environment,
        bool FileChanged,
        IReadOnlyList<int> ChangedMemberIndices)
    {
        public bool HasChanges => File is not null ? FileChanged : ChangedMemberIndices.Count > 0;
    }

    private enum CompensationClassification
    {
        Prior,
        Desired,
        ThirdParty,
        Error,
    }

    private enum CompensationOutcome
    {
        OriginalFailure,
        Restored,
        Partial,
        RecoveryRequired,
    }

    private sealed record CompensationFailure(
        Guid? RecordId,
        string Code,
        SetupLedgerRollbackStatus RollbackStatus,
        bool SafeToPreserveAndContinue = false,
        bool RequiresRecovery = false);
}

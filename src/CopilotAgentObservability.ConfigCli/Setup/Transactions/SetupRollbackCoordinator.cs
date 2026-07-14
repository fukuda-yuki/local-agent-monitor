using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed record SetupRollbackExecutionResult
{
    public SetupRollbackExecutionResult(
        Guid requestedChangeSetId,
        bool success,
        string code,
        SetupLedgerChangeSet? changeSet,
        SetupRecoveryResult? recovery)
    {
        if (changeSet is not null && changeSet.ChangeSetId != requestedChangeSetId)
        {
            throw new ArgumentException(
                "The trusted change set must match the requested change set.",
                nameof(changeSet));
        }

        if (changeSet is not null && recovery is not null)
        {
            throw new ArgumentException(
                "A recovery result cannot expose a trusted requested change set.",
                nameof(recovery));
        }

        RequestedChangeSetId = requestedChangeSetId;
        Success = success;
        Code = code;
        ChangeSet = changeSet;
        Recovery = recovery;
    }

    public Guid RequestedChangeSetId { get; }

    public bool Success { get; }

    public string Code { get; }

    public SetupLedgerChangeSet? ChangeSet { get; }

    public SetupRecoveryResult? Recovery { get; }
}

internal sealed class SetupRollbackCoordinator
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupTransactionJournalStore journalStore;
    private readonly SetupRecoveryCoordinator recoveryCoordinator;
    private readonly SetupRollbackPreflightObserver preflightObserver;

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
        preflightObserver = new SetupRollbackPreflightObserver(platform, paths);
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

        var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, changeSet, applyJournal);
        var preflight = preparation.Result;
        if (preparation.Evidence is not null)
        {
            preflight = SetupRollbackPreflightEvaluator.Evaluate(
                preparation.Evidence,
                preflightObserver.Capture(preparation.Evidence));
        }

        if (preflight is null || !preflight.IsAvailable)
        {
            return HandlePreflightFailure(
                setupLock,
                ledger,
                changeSet,
                preflight ?? new SetupRollbackPreflightResult(
                    SetupRollbackPreflightClassification.RecoveryRequired,
                    SetupCodes.RecoveryRequired,
                    []),
                preparation.TrustedChangeSet);
        }

        var trustedChangeSet = preparation.TrustedChangeSet
            ?? throw new InvalidOperationException();
        var rollbackTargets = preflight.RollbackTargets;

        try
        {
            journalStore.SupersedeWithPreparedRollback(setupLock, changeSetId, rollbackTargets);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterJournalPreparedBeforeLedger);
            var rollingBack = trustedChangeSet with
            {
                UpdatedAt = platform.Clock.UtcNow,
                OutcomeCode = null,
                State = SetupChangeSetState.RollingBack,
                Targets = trustedChangeSet.Targets.Select(target => target.AppliedStateHash is not null
                    ? target with { OutcomeCode = null, RollbackStatus = SetupLedgerRollbackStatus.Pending }
                    : target with { OutcomeCode = null, RollbackStatus = SetupLedgerRollbackStatus.NotAvailable }).ToArray(),
            };
            SaveChangeSet(setupLock, ledger, rollingBack);
            platform.Execution.Checkpoint(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent);
        }
        catch (Exception)
        {
            return Result(changeSetId, false, SetupCodes.RecoveryRequired, trustedChangeSet);
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
            execution.EffectiveChangeSet ?? trustedChangeSet);
    }

    private SetupRollbackExecutionResult HandlePreflightFailure(
        SetupLock setupLock,
        SetupOwnershipLedger ledger,
        SetupLedgerChangeSet changeSet,
        SetupRollbackPreflightResult preflight,
        SetupLedgerChangeSet? trustedChangeSet)
    {
        var code = preflight.Code ?? SetupCodes.RecoveryRequired;
        if (preflight.Classification is SetupRollbackPreflightClassification.NotAvailable or
            SetupRollbackPreflightClassification.Stale)
        {
            if (trustedChangeSet is null)
            {
                return Result(changeSet.ChangeSetId, false, code);
            }

            return PersistAttemptOutcome(setupLock, ledger, trustedChangeSet, code);
        }

        return Result(changeSet.ChangeSetId, false, code, trustedChangeSet);
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

    private static SetupRollbackExecutionResult Result(
        Guid changeSetId,
        bool success,
        string code,
        SetupLedgerChangeSet? changeSet = null) =>
        new(changeSetId, success, code, changeSet, null);
}

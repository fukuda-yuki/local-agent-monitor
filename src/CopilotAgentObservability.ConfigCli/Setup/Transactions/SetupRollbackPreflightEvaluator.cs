using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal enum SetupRollbackPreflightClassification
{
    Available,
    NotAvailable,
    Stale,
    RecoveryRequired,
    UnsafePath,
    InternalError,
}

internal sealed record SetupRollbackPreflightResult(
    SetupRollbackPreflightClassification Classification,
    string? Code,
    IReadOnlyList<SetupJournalTarget> RollbackTargets)
{
    public bool IsAvailable => Classification == SetupRollbackPreflightClassification.Available;
}

internal sealed record SetupRollbackPreflightPreparation(
    SetupRollbackPreflightEvidence? Evidence,
    SetupRollbackPreflightResult? Result,
    SetupLedgerChangeSet? TrustedChangeSet);

internal sealed record SetupRollbackPreflightEvidence(
    SetupPrivatePlan Plan,
    SetupLedgerChangeSet ChangeSet,
    SetupTransactionJournal Journal,
    bool TerminalApply,
    IReadOnlyList<SetupJournalTarget> RollbackTargets);

internal sealed record SetupRollbackTargetObservation(
    Guid RecordId,
    AtomicFileCapture? FileCurrent,
    AtomicFileCapture? FileBackup,
    UserEnvironmentCapture? EnvironmentCurrent,
    UserEnvironmentCapture? EnvironmentBackup,
    string? FailureCode);

internal sealed record SetupRollbackPreflightObservations(
    IReadOnlyList<SetupRollbackTargetObservation> Targets);

internal static class SetupRollbackPreflightEvaluator
{
    public static SetupRollbackPreflightPreparation Prepare(
        SetupPrivatePlan? plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal? journal)
    {
        if (plan is null)
        {
            return Failure(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        try
        {
            SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
        }
        catch (Exception)
        {
            return Failure(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        if (changeSet.State != SetupChangeSetState.Applied)
        {
            return Failure(
                SetupRollbackPreflightClassification.NotAvailable,
                SetupCodes.RollbackNotAvailable,
                changeSet);
        }

        if (plan.Targets.Count(target => target.TargetKind == SetupTargetKind.Env) > 1)
        {
            return Failure(
                SetupRollbackPreflightClassification.NotAvailable,
                SetupCodes.RollbackNotAvailable,
                changeSet);
        }

        try
        {
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

            var rollbackTargets = journal.Targets.Select(target => target with
            {
                Steps = target.Steps.Select(step => step with { Phase = SetupJournalStepPhase.Pending }).ToArray(),
            }).ToArray();
            return new SetupRollbackPreflightPreparation(
                new SetupRollbackPreflightEvidence(plan, changeSet, journal, terminalApply, rollbackTargets),
                null,
                changeSet);
        }
        catch (Exception)
        {
            return Failure(
                SetupRollbackPreflightClassification.RecoveryRequired,
                SetupCodes.RecoveryRequired,
                changeSet);
        }
    }

    public static SetupRollbackPreflightResult Evaluate(
        SetupRollbackPreflightEvidence evidence,
        SetupRollbackPreflightObservations observations)
    {
        try
        {
            var observedTargets = observations.Targets.ToDictionary(target => target.RecordId);
            for (var index = 0; index < evidence.Plan.Targets.Count; index++)
            {
                var planTarget = evidence.Plan.Targets[index];
                var ledgerTarget = evidence.ChangeSet.Targets[index];
                if (planTarget.TargetKind == SetupTargetKind.Guidance)
                {
                    continue;
                }

                if (!observedTargets.TryGetValue(planTarget.RecordId, out var observation))
                {
                    return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
                }

                if (observation.FailureCode is not null)
                {
                    return observation.FailureCode switch
                    {
                        SetupCodes.UnsafePath => Result(SetupRollbackPreflightClassification.UnsafePath, SetupCodes.UnsafePath),
                        SetupCodes.RecoveryRequired => Result(
                            SetupRollbackPreflightClassification.RecoveryRequired,
                            SetupCodes.RecoveryRequired),
                        _ => Result(SetupRollbackPreflightClassification.InternalError, SetupCodes.InternalError),
                    };
                }

                if (planTarget.TargetKind == SetupTargetKind.Env)
                {
                    var environmentResult = EvaluateEnvironment(evidence, planTarget, ledgerTarget, observation);
                    if (environmentResult is not null)
                    {
                        return environmentResult;
                    }
                }
                else
                {
                    var fileResult = EvaluateFile(evidence, planTarget, ledgerTarget, observation);
                    if (fileResult is not null)
                    {
                        return fileResult;
                    }
                }
            }

            return new SetupRollbackPreflightResult(
                SetupRollbackPreflightClassification.Available,
                null,
                evidence.RollbackTargets);
        }
        catch (Exception)
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }
    }

    public static bool IsTargetAvailable(
        SetupRollbackPreflightEvidence evidence,
        SetupRollbackPreflightObservations observations,
        Guid recordId)
    {
        try
        {
            var index = evidence.Plan.Targets.Select((target, index) => (target, index))
                .Single(item => item.target.RecordId == recordId).index;
            var planTarget = evidence.Plan.Targets[index];
            var ledgerTarget = evidence.ChangeSet.Targets[index];
            if (planTarget.TargetKind == SetupTargetKind.Guidance || HasNoOwnership(ledgerTarget))
            {
                return false;
            }

            var observation = observations.Targets.Single(target => target.RecordId == recordId);
            if (observation.FailureCode is not null)
            {
                return false;
            }

            var failure = planTarget.TargetKind == SetupTargetKind.Env
                ? EvaluateEnvironment(evidence, planTarget, ledgerTarget, observation)
                : EvaluateFile(evidence, planTarget, ledgerTarget, observation);
            return failure is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SetupRollbackPreflightResult? EvaluateFile(
        SetupRollbackPreflightEvidence evidence,
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupRollbackTargetObservation observation)
    {
        if (observation.FileCurrent is null)
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        if (ledgerTarget.AppliedStateHash is null)
        {
            return string.Equals(observation.FileCurrent.Hash, planTarget.BaseStateHash, StringComparison.Ordinal)
                ? null
                : Result(SetupRollbackPreflightClassification.Stale, SetupCodes.RollbackStale);
        }

        var journalTarget = evidence.Journal.Targets.SingleOrDefault(
            target => target.RecordId == planTarget.RecordId);
        if (journalTarget is null ||
            journalTarget.TargetKind != planTarget.TargetKind ||
            journalTarget.Steps.Count != 1 ||
            journalTarget.Steps[0].MemberKey is not null ||
            journalTarget.Steps[0].Phase != (evidence.TerminalApply
                ? SetupJournalStepPhase.MutationCompleted
                : SetupJournalStepPhase.Pending) ||
            !string.Equals(journalTarget.Steps[0].PriorStateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
            !string.Equals(journalTarget.Steps[0].DesiredStateHash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal) ||
            !string.Equals(journalTarget.Steps[0].DesiredStateHash,
                SetupDesiredStateHash.File(planTarget.DesiredState), StringComparison.Ordinal) ||
            !string.Equals(journalTarget.Steps[0].BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(ledgerTarget.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
            ledgerTarget.RollbackStatus != SetupLedgerRollbackStatus.Pending ||
            observation.FileBackup is null ||
            !string.Equals(observation.FileBackup.Hash, planTarget.BaseStateHash, StringComparison.Ordinal))
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        return string.Equals(observation.FileCurrent.Hash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal)
            ? null
            : Result(SetupRollbackPreflightClassification.Stale, SetupCodes.RollbackStale);
    }

    private static SetupRollbackPreflightResult? EvaluateEnvironment(
        SetupRollbackPreflightEvidence evidence,
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupRollbackTargetObservation observation)
    {
        if (observation.EnvironmentCurrent is null)
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        if (HasNoOwnership(ledgerTarget))
        {
            if (planTarget.Members.Any(member => member.Operation != SetupOperation.NoOp) ||
                evidence.Journal.Targets.Any(target => target.RecordId == planTarget.RecordId))
            {
                return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
            }

            for (var index = 0; index < planTarget.Members.Count; index++)
            {
                var desiredHash = UserEnvironmentSetupStep.HashPlannedMember(planTarget.Members[index]);
                if (!string.Equals(observation.EnvironmentCurrent.Members[index].Hash, desiredHash, StringComparison.Ordinal))
                {
                    return Result(SetupRollbackPreflightClassification.Stale, SetupCodes.RollbackStale);
                }
            }

            return string.Equals(observation.EnvironmentCurrent.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) &&
                string.Equals(observation.EnvironmentCurrent.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal)
                ? null
                : Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        if (ledgerTarget.AppliedStateHash is null ||
            !string.Equals(ledgerTarget.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
            ledgerTarget.RollbackStatus != SetupLedgerRollbackStatus.Pending ||
            observation.EnvironmentBackup is null ||
            !string.Equals(observation.EnvironmentBackup.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
            !string.Equals(observation.EnvironmentBackup.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal))
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        if (!string.Equals(observation.EnvironmentCurrent.AggregateHash, ledgerTarget.AppliedStateHash, StringComparison.Ordinal))
        {
            return Result(SetupRollbackPreflightClassification.Stale, SetupCodes.RollbackStale);
        }

        var journalTarget = evidence.Journal.Targets.SingleOrDefault(target => target.RecordId == planTarget.RecordId);
        var changedMembers = planTarget.Members.Where(member => member.Operation != SetupOperation.NoOp).ToArray();
        if (journalTarget is null ||
            journalTarget.TargetKind != SetupTargetKind.Env ||
            journalTarget.Steps.Count != changedMembers.Length)
        {
            return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
        }

        var stepIndex = 0;
        for (var memberIndex = 0; memberIndex < planTarget.Members.Count; memberIndex++)
        {
            var member = planTarget.Members[memberIndex];
            var desiredHash = UserEnvironmentSetupStep.HashPlannedMember(member);
            if (!string.Equals(observation.EnvironmentCurrent.Members[memberIndex].Hash, desiredHash, StringComparison.Ordinal))
            {
                return Result(SetupRollbackPreflightClassification.Stale, SetupCodes.RollbackStale);
            }

            if (member.Operation == SetupOperation.NoOp)
            {
                if (!string.Equals(observation.EnvironmentBackup.Members[memberIndex].Hash, desiredHash, StringComparison.Ordinal))
                {
                    return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
                }

                continue;
            }

            var step = journalTarget.Steps[stepIndex++];
            if (!string.Equals(step.MemberKey, member.SettingKey, StringComparison.Ordinal) ||
                !string.Equals(step.PriorStateHash, observation.EnvironmentBackup.Members[memberIndex].Hash, StringComparison.Ordinal) ||
                !string.Equals(step.DesiredStateHash, desiredHash, StringComparison.Ordinal) ||
                !string.Equals(step.BackupReference, planTarget.RecordId.ToString("D"), StringComparison.Ordinal) ||
                step.Phase != (evidence.TerminalApply
                    ? SetupJournalStepPhase.MutationCompleted
                    : SetupJournalStepPhase.Pending))
            {
                return Result(SetupRollbackPreflightClassification.RecoveryRequired, SetupCodes.RecoveryRequired);
            }
        }

        return null;
    }

    private static SetupRollbackPreflightPreparation Failure(
        SetupRollbackPreflightClassification classification,
        string code,
        SetupLedgerChangeSet? trustedChangeSet = null) =>
        new(null, Result(classification, code), trustedChangeSet);

    private static SetupRollbackPreflightResult Result(
        SetupRollbackPreflightClassification classification,
        string code) =>
        new(classification, code, []);

    private static bool HasNoOwnership(SetupLedgerTarget target) =>
        target.AppliedStateHash is null &&
        target.BackupReference is null &&
        target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable;
}

internal sealed class SetupRollbackPreflightObserver
{
    private readonly SetupRuntimePaths paths;
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupRollbackPreflightObserver(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        this.paths = paths;
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public SetupRollbackPreflightObservations Capture(SetupRollbackPreflightEvidence evidence)
    {
        var observations = new List<SetupRollbackTargetObservation>();
        for (var index = 0; index < evidence.Plan.Targets.Count; index++)
        {
            var planTarget = evidence.Plan.Targets[index];
            var ledgerTarget = evidence.ChangeSet.Targets[index];
            if (planTarget.TargetKind == SetupTargetKind.Guidance)
            {
                continue;
            }

            AtomicFileCapture? fileCurrent = null;
            UserEnvironmentCapture? environmentCurrent = null;
            try
            {
                if (planTarget.TargetKind == SetupTargetKind.Env)
                {
                    var names = planTarget.Members.Select(member => member.SettingKey).ToArray();
                    environmentCurrent = environmentStep.Capture(names);
                    var backup = ledgerTarget.AppliedStateHash is null
                        ? null
                        : environmentStep.ReadBackup(
                            paths.GetBackup(evidence.ChangeSet.ChangeSetId, planTarget.RecordId),
                            names);
                    observations.Add(new SetupRollbackTargetObservation(
                        planTarget.RecordId, null, null, environmentCurrent, backup, null));
                }
                else
                {
                    fileCurrent = fileStep.Capture(GetAllowedRoot(planTarget.TargetLocation), planTarget.TargetLocation);
                    var backup = ledgerTarget.AppliedStateHash is null
                        ? null
                        : fileStep.ReadBackup(
                            paths.GetBackup(evidence.ChangeSet.ChangeSetId, planTarget.RecordId),
                            planTarget.BaseStateHash);
                    observations.Add(new SetupRollbackTargetObservation(
                        planTarget.RecordId, fileCurrent, backup, null, null, null));
                }
            }
            catch (SetupFileStepException exception)
            {
                observations.Add(new SetupRollbackTargetObservation(
                    planTarget.RecordId, fileCurrent, null, environmentCurrent, null,
                    exception.Code == SetupCodes.UnsafePath ? SetupCodes.UnsafePath : SetupCodes.InternalError));
                break;
            }
            catch (SetupEnvironmentStepException)
            {
                observations.Add(new SetupRollbackTargetObservation(
                    planTarget.RecordId, fileCurrent, null, environmentCurrent, null, SetupCodes.InternalError));
                break;
            }
            catch (Exception)
            {
                observations.Add(new SetupRollbackTargetObservation(
                    planTarget.RecordId, fileCurrent, null, environmentCurrent, null, SetupCodes.RecoveryRequired));
                break;
            }
        }

        return new SetupRollbackPreflightObservations(observations);
    }

    private static string GetAllowedRoot(string targetPath) =>
        Path.GetDirectoryName(targetPath) ?? throw new FormatException();
}

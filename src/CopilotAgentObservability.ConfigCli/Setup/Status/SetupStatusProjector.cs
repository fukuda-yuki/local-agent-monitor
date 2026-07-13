using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Status;

internal sealed class SetupStatusProjector
{
    private readonly SetupPlanStore planStore;
    private readonly SetupTransactionJournalStore journalStore;
    private readonly SetupStatusTargetObserver targetObserver;
    private readonly SetupRollbackPreflightObserver rollbackObserver;

    public SetupStatusProjector(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupTransactionJournalStore journalStore)
    {
        this.planStore = planStore;
        this.journalStore = journalStore;
        targetObserver = new SetupStatusTargetObserver(platform);
        rollbackObserver = new SetupRollbackPreflightObserver(platform, paths);
    }

    public SetupChangeSetStatusResult Project(SetupLedgerChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var terminal = IsTerminal(changeSet.State);
        SetupPrivatePlan? plan;
        SetupTransactionJournal? journal;
        try
        {
            plan = planStore.Load(changeSet.ChangeSetId);
            if (plan is null)
            {
                if (!terminal)
                {
                    throw new SetupStorageException(SetupCodes.RecoveryRequired);
                }

                return Unavailable(changeSet);
            }

            SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
            journal = journalStore.Load(changeSet.ChangeSetId);
            if (RequiresJournal(changeSet.State) && journal is null)
            {
                if (!terminal)
                {
                    throw new SetupStorageException(SetupCodes.RecoveryRequired);
                }

                return Unavailable(changeSet);
            }
        }
        catch (SetupStorageException) when (terminal)
        {
            return Unavailable(changeSet);
        }
        catch (SetupStorageException) when (!terminal)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }
        catch (Exception exception) when (terminal && exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return Unavailable(changeSet);
        }
        catch (Exception exception) when (!terminal && exception is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }

        var observations = ObserveTargets(plan!, changeSet);
        var rollback = EvaluateRollback(plan!, changeSet, journal);
        var targets = new SetupTargetResult[changeSet.Targets.Count];
        for (var index = 0; index < targets.Length; index++)
        {
            var ledgerTarget = changeSet.Targets[index];
            targets[index] = ledgerTarget.TargetKind == SetupTargetKind.Guidance
                ? CreateGuidanceTarget(ledgerTarget)
                : CreateWritableTarget(
                    changeSet.State,
                    ledgerTarget,
                    observations[index],
                    rollback.TargetAvailability.TryGetValue(ledgerTarget.RecordId, out var available) && available);
        }

        var writableTargets = targets.Where(target => target.TargetKind != SetupTargetKind.Guidance).ToArray();
        return new SetupChangeSetStatusResult(
            changeSet.ChangeSetId.ToString("D"),
            changeSet.Adapter,
            changeSet.SelectedTarget,
            SetupStorageJson.FormatTimestamp(changeSet.CreatedAt),
            SetupStorageJson.FormatTimestamp(changeSet.UpdatedAt),
            changeSet.State,
            changeSet.OutcomeCode,
            AggregateCurrentState(writableTargets),
            rollback.ChangeSetAvailable,
            targets);
    }

    private IReadOnlyList<SetupStatusTargetObservation> ObserveTargets(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet)
    {
        var observations = new SetupStatusTargetObservation[plan.Targets.Count];
        for (var index = 0; index < observations.Length; index++)
        {
            var planTarget = plan.Targets[index];
            observations[index] = planTarget.TargetKind == SetupTargetKind.Guidance
                ? SetupStatusTargetObservation.Guidance
                : targetObserver.Capture(planTarget, changeSet.Targets[index]);
        }

        return observations;
    }

    private SetupStatusRollbackResult EvaluateRollback(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal? journal)
    {
        var availability = changeSet.Targets.ToDictionary(target => target.RecordId, _ => false);
        var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, changeSet, journal);
        if (preparation.Evidence is not { } evidence)
        {
            return new SetupStatusRollbackResult(false, availability);
        }

        var observations = rollbackObserver.Capture(evidence);
        var result = SetupRollbackPreflightEvaluator.Evaluate(evidence, observations);
        foreach (var target in changeSet.Targets.Where(target =>
                     target.TargetKind != SetupTargetKind.Guidance &&
                     target.Members.Any(member => member.Operation != SetupOperation.NoOp)))
        {
            availability[target.RecordId] = SetupRollbackPreflightEvaluator.IsTargetAvailable(
                evidence,
                observations,
                target.RecordId);
        }

        return new SetupStatusRollbackResult(result.IsAvailable, availability);
    }

    private static SetupTargetResult CreateGuidanceTarget(SetupLedgerTarget target)
    {
        var projection = target.StatusProjection;
        return new SetupTargetResult(
            target.RecordId.ToString("D"),
            target.TargetKind,
            target.TargetLabel,
            projection.Detected,
            projection.DetectedVersion,
            projection.Operation,
            projection.EffectiveSource,
            SetupReferenceState.None,
            SetupCurrentState.NotApplicable,
            target.RestartRequirement,
            false,
            projection.Endpoint,
            projection.ExpectedResult,
            SetupContractValidator.RehydrateStatusGuidance(projection.Guidance!),
            projection.Changes);
    }

    private static SetupTargetResult CreateWritableTarget(
        SetupChangeSetState state,
        SetupLedgerTarget ledgerTarget,
        SetupStatusTargetObservation observation,
        bool rollbackAvailable)
    {
        var projectedState = ProjectState(state, observation);
        var projection = ledgerTarget.StatusProjection;
        return new SetupTargetResult(
            ledgerTarget.RecordId.ToString("D"),
            ledgerTarget.TargetKind,
            ledgerTarget.TargetLabel,
            projection.Detected,
            projection.DetectedVersion,
            projection.Operation,
            projection.EffectiveSource,
            projectedState.ReferenceState,
            projectedState.CurrentState,
            ledgerTarget.RestartRequirement,
            rollbackAvailable,
            projection.Endpoint,
            projection.ExpectedResult,
            null,
            projection.Changes);
    }

    private static SetupStatusProjectedState ProjectState(
        SetupChangeSetState state,
        SetupStatusTargetObservation observation)
    {
        if (observation.Classification == SetupStatusTargetClassification.Unavailable)
        {
            return new SetupStatusProjectedState(SetupReferenceState.None, SetupCurrentState.Unavailable);
        }

        return state switch
        {
            SetupChangeSetState.Planned => CompareStable(
                SetupReferenceState.Base,
                observation.MatchesPrevious),
            SetupChangeSetState.NoChanges => CompareStable(
                SetupReferenceState.Desired,
                observation.MatchesDesired),
            SetupChangeSetState.Applied => CompareStable(
                SetupReferenceState.Desired,
                observation.MatchesDesired),
            SetupChangeSetState.Restored or SetupChangeSetState.RolledBack => CompareStable(
                SetupReferenceState.Previous,
                observation.MatchesPrevious),
            SetupChangeSetState.Partial or SetupChangeSetState.Applying or
                SetupChangeSetState.Compensating or SetupChangeSetState.RollingBack =>
                ProjectDynamic(state, observation.Classification),
            _ => throw new InvalidOperationException(),
        };
    }

    private static SetupStatusProjectedState CompareStable(SetupReferenceState reference, bool matches) =>
        new(reference, matches ? SetupCurrentState.Current : SetupCurrentState.Stale);

    private static SetupStatusProjectedState ProjectDynamic(
        SetupChangeSetState state,
        SetupStatusTargetClassification classification) =>
        classification switch
        {
            SetupStatusTargetClassification.Desired => new(SetupReferenceState.Desired, SetupCurrentState.Current),
            SetupStatusTargetClassification.Previous => new(
                state == SetupChangeSetState.Applying ? SetupReferenceState.Base : SetupReferenceState.Previous,
                SetupCurrentState.Current),
            SetupStatusTargetClassification.Diverged => new(SetupReferenceState.None, SetupCurrentState.Diverged),
            _ => new(SetupReferenceState.None, SetupCurrentState.Unavailable),
        };

    private static SetupChangeSetStatusResult Unavailable(SetupLedgerChangeSet changeSet)
    {
        var targets = changeSet.Targets.Select(target => target.TargetKind == SetupTargetKind.Guidance
            ? CreateGuidanceTarget(target)
            : CreateUnavailableTarget(target)).ToArray();
        return new SetupChangeSetStatusResult(
            changeSet.ChangeSetId.ToString("D"),
            changeSet.Adapter,
            changeSet.SelectedTarget,
            SetupStorageJson.FormatTimestamp(changeSet.CreatedAt),
            SetupStorageJson.FormatTimestamp(changeSet.UpdatedAt),
            changeSet.State,
            changeSet.OutcomeCode,
            targets.All(target => target.TargetKind == SetupTargetKind.Guidance)
                ? SetupCurrentState.NotApplicable
                : SetupCurrentState.Unavailable,
            false,
            targets);
    }

    private static SetupTargetResult CreateUnavailableTarget(SetupLedgerTarget target)
    {
        var projection = target.StatusProjection;
        return new SetupTargetResult(
            target.RecordId.ToString("D"),
            target.TargetKind,
            target.TargetLabel,
            projection.Detected,
            projection.DetectedVersion,
            projection.Operation,
            projection.EffectiveSource,
            SetupReferenceState.None,
            SetupCurrentState.Unavailable,
            target.RestartRequirement,
            false,
            projection.Endpoint,
            projection.ExpectedResult,
            null,
            projection.Changes);
    }

    private static SetupCurrentState AggregateCurrentState(IReadOnlyList<SetupTargetResult> targets)
    {
        if (targets.Count == 0)
        {
            return SetupCurrentState.NotApplicable;
        }

        if (targets.Any(target => target.CurrentState == SetupCurrentState.Diverged))
        {
            return SetupCurrentState.Diverged;
        }

        if (targets.Any(target => target.CurrentState == SetupCurrentState.Stale))
        {
            return SetupCurrentState.Stale;
        }

        return targets.Any(target => target.CurrentState == SetupCurrentState.Unavailable)
            ? SetupCurrentState.Unavailable
            : SetupCurrentState.Current;
    }

    private static bool IsTerminal(SetupChangeSetState state) => state is
        SetupChangeSetState.Applied or SetupChangeSetState.NoChanges or
        SetupChangeSetState.Restored or SetupChangeSetState.RolledBack;

    private static bool RequiresJournal(SetupChangeSetState state) => state is not
        SetupChangeSetState.Planned and not SetupChangeSetState.NoChanges;

    private sealed record SetupStatusRollbackResult(
        bool ChangeSetAvailable,
        IReadOnlyDictionary<Guid, bool> TargetAvailability);

    private sealed record SetupStatusProjectedState(
        SetupReferenceState ReferenceState,
        SetupCurrentState CurrentState);
}

internal enum SetupStatusTargetClassification
{
    Previous,
    Desired,
    Diverged,
    Unavailable,
}

internal sealed record SetupStatusTargetObservation(
    SetupStatusTargetClassification Classification,
    bool MatchesPrevious,
    bool MatchesDesired)
{
    public static SetupStatusTargetObservation Guidance { get; } =
        new(SetupStatusTargetClassification.Unavailable, false, false);
}

internal sealed class SetupStatusTargetObserver
{
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupStatusTargetObserver(ISetupPlatform platform)
    {
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public SetupStatusTargetObservation Capture(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget)
    {
        try
        {
            return planTarget.TargetKind == SetupTargetKind.Env
                ? CaptureEnvironment(planTarget, ledgerTarget)
                : CaptureFile(planTarget);
        }
        catch (Exception)
        {
            return new SetupStatusTargetObservation(
                SetupStatusTargetClassification.Unavailable,
                false,
                false);
        }
    }

    private SetupStatusTargetObservation CaptureFile(SetupPrivatePlanTarget target)
    {
        var current = fileStep.Capture(
            Path.GetDirectoryName(target.TargetLocation) ?? throw new FormatException(),
            target.TargetLocation).Hash;
        var desired = SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState));
        var matchesPrevious = string.Equals(current, target.BaseStateHash, StringComparison.Ordinal);
        var matchesDesired = string.Equals(current, desired, StringComparison.Ordinal);
        return new SetupStatusTargetObservation(
            matchesDesired
                ? SetupStatusTargetClassification.Desired
                : matchesPrevious
                    ? SetupStatusTargetClassification.Previous
                    : SetupStatusTargetClassification.Diverged,
            matchesPrevious,
            matchesDesired);
    }

    private SetupStatusTargetObservation CaptureEnvironment(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget)
    {
        var capture = environmentStep.Capture(planTarget.Members.Select(member => member.SettingKey).ToArray());
        var matchesPrevious = string.Equals(capture.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) &&
            string.Equals(capture.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal);
        var desiredHashes = planTarget.Members.Select(UserEnvironmentSetupStep.HashPlannedMember).ToArray();
        var matchesDesired = capture.Members.Where((member, index) =>
            !string.Equals(member.Hash, desiredHashes[index], StringComparison.Ordinal)).Any() == false;
        if (matchesDesired)
        {
            return new SetupStatusTargetObservation(
                SetupStatusTargetClassification.Desired,
                matchesPrevious,
                true);
        }

        if (matchesPrevious)
        {
            return new SetupStatusTargetObservation(
                SetupStatusTargetClassification.Previous,
                true,
                false);
        }

        return new SetupStatusTargetObservation(
            SetupStatusTargetClassification.Diverged,
            false,
            false);
    }
}

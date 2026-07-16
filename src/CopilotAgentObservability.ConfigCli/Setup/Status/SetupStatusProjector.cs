using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
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
    private readonly SetupStatusActiveEvidenceValidator activeEvidenceValidator;

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
        activeEvidenceValidator = new SetupStatusActiveEvidenceValidator(platform, paths);
    }

    public SetupChangeSetStatusResult Project(SetupLedgerChangeSet changeSet) =>
        ProjectCore(changeSet, changeSet, failedRecovery: false);

    internal SetupChangeSetStatusResult ProjectFailedRecovery(
        SetupLedgerChangeSet evidenceChangeSet,
        SetupLedgerChangeSet effectiveChangeSet)
    {
        SetupFailedRecoveryOverlayValidator.Validate(
            evidenceChangeSet,
            effectiveChangeSet,
            requireTerminalEvidence: true);
        return ProjectCore(evidenceChangeSet, effectiveChangeSet, failedRecovery: true);
    }

    private SetupChangeSetStatusResult ProjectCore(
        SetupLedgerChangeSet evidenceChangeSet,
        SetupLedgerChangeSet projectedChangeSet,
        bool failedRecovery)
    {
        ArgumentNullException.ThrowIfNull(evidenceChangeSet);
        ArgumentNullException.ThrowIfNull(projectedChangeSet);
        var terminal = IsTerminal(evidenceChangeSet.State);
        SetupPrivatePlan? plan;
        SetupTransactionJournal? journal;
        var guidanceIncludesContent = false;
        var journalDisposition = SetupStatusJournalDisposition.None;
        IReadOnlyDictionary<Guid, SetupReferenceState>? activeReferences = null;
        try
        {
            plan = planStore.Load(evidenceChangeSet.ChangeSetId);
            if (plan is null)
            {
                if (!terminal)
                {
                    throw new SetupStorageException(SetupCodes.RecoveryRequired);
                }

                return Unavailable(projectedChangeSet);
            }

            SetupTransactionEvidence.RequireImmutableIdentity(plan, evidenceChangeSet);
            SetupTransactionEvidence.RequireImmutableIdentity(plan, projectedChangeSet);
            if (plan.Adapter == "claude-code" || evidenceChangeSet.Adapter == "claude-code")
            {
                guidanceIncludesContent = ClaudeAgentSdkGuidanceVariant.ValidatePair(plan, evidenceChangeSet);
            }
            journal = journalStore.Load(evidenceChangeSet.ChangeSetId);
            journalDisposition = RequireJournalLifecycle(evidenceChangeSet.State, journal);
            if (journal is not null &&
                (journalDisposition is SetupStatusJournalDisposition.Active or SetupStatusJournalDisposition.Dormant ||
                    !terminal))
            {
                activeReferences = activeEvidenceValidator.Validate(plan, evidenceChangeSet, journal);
            }
        }
        catch (SetupStorageException) when (terminal)
        {
            return Unavailable(projectedChangeSet);
        }
        catch (SetupStorageException) when (!terminal)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }
        catch (Exception exception) when (terminal && exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return Unavailable(projectedChangeSet);
        }
        catch (Exception exception) when (!terminal && exception is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }
        catch (Exception exception) when (terminal && exception is SetupFileStepException or SetupEnvironmentStepException)
        {
            return Unavailable(projectedChangeSet);
        }
        catch (Exception exception) when (!terminal && exception is SetupFileStepException or SetupEnvironmentStepException)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }

        var rollback = failedRecovery
            ? MaskRollback(
                projectedChangeSet,
                EvaluateRollback(plan!, evidenceChangeSet, journal, journalDisposition))
            : EvaluateRollback(plan!, projectedChangeSet, journal, journalDisposition);
        var observationChangeSet = failedRecovery ? evidenceChangeSet : projectedChangeSet;
        var observations = rollback.Observations is null
            ? ObserveTargets(plan!, observationChangeSet)
            : ObserveTargets(plan!, observationChangeSet, rollback.Observations);
        var targets = new SetupTargetResult[projectedChangeSet.Targets.Count];
        for (var index = 0; index < targets.Length; index++)
        {
            var ledgerTarget = projectedChangeSet.Targets[index];
            targets[index] = ledgerTarget.TargetKind == SetupTargetKind.Guidance
                ? CreateGuidanceTarget(ledgerTarget, guidanceIncludesContent)
                : CreateWritableTarget(
                    projectedChangeSet.State,
                    ledgerTarget,
                    observations[index],
                    activeReferences is not null && activeReferences.TryGetValue(
                        ledgerTarget.RecordId,
                        out var activeReference)
                            ? activeReference
                            : null,
                    rollback.TargetAvailability.TryGetValue(ledgerTarget.RecordId, out var available) && available);
        }

        var writableTargets = targets.Where(target => target.TargetKind != SetupTargetKind.Guidance).ToArray();
        return new SetupChangeSetStatusResult(
            projectedChangeSet.ChangeSetId.ToString("D"),
            projectedChangeSet.Adapter,
            projectedChangeSet.SelectedTarget,
            SetupStorageJson.FormatTimestamp(projectedChangeSet.CreatedAt),
            SetupStorageJson.FormatTimestamp(projectedChangeSet.UpdatedAt),
            projectedChangeSet.State,
            projectedChangeSet.OutcomeCode,
            AggregateCurrentState(writableTargets),
            rollback.ChangeSetAvailable,
            targets);
    }

    private static SetupStatusRollbackResult MaskRollback(
        SetupLedgerChangeSet changeSet,
        SetupStatusRollbackResult observed) => new(
        false,
        changeSet.Targets.ToDictionary(target => target.RecordId, _ => false),
        observed.Observations);

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

    private IReadOnlyList<SetupStatusTargetObservation> ObserveTargets(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupRollbackPreflightObservations rollbackObservations)
    {
        var observed = rollbackObservations.Targets.ToDictionary(target => target.RecordId);
        return plan.Targets.Select((target, index) => target.TargetKind == SetupTargetKind.Guidance
            ? SetupStatusTargetObservation.Guidance
            : observed.TryGetValue(target.RecordId, out var observation)
                ? targetObserver.FromRollback(target, changeSet.Targets[index], observation)
                : SetupStatusTargetObservation.Unavailable).ToArray();
    }

    private SetupStatusRollbackResult EvaluateRollback(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal? journal,
        SetupStatusJournalDisposition journalDisposition)
    {
        var availability = changeSet.Targets.ToDictionary(target => target.RecordId, _ => false);
        if (changeSet.State != SetupChangeSetState.Applied ||
            journalDisposition != SetupStatusJournalDisposition.Completed ||
            journal is not
            {
                Operation: SetupJournalOperation.Apply,
                Phase: SetupJournalPhase.Committed,
            })
        {
            return new SetupStatusRollbackResult(false, availability, null);
        }

        var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, changeSet, journal);
        if (preparation.Evidence is not { } evidence)
        {
            return new SetupStatusRollbackResult(false, availability, null);
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

        return new SetupStatusRollbackResult(result.IsAvailable, availability, observations);
    }

    private static SetupTargetResult CreateGuidanceTarget(
        SetupLedgerTarget target,
        bool? includeContentCapture)
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
            includeContentCapture.HasValue
                ? SetupContractValidator.RehydrateStatusGuidance(
                    projection.Guidance!,
                    target.TargetLabel,
                    includeContentCapture.Value)
                : new SetupGuidance(
                    projection.Guidance!.Kind,
                    projection.Guidance.Language,
                    null!),
            projection.Changes);
    }

    private static SetupTargetResult CreateWritableTarget(
        SetupChangeSetState state,
        SetupLedgerTarget ledgerTarget,
        SetupStatusTargetObservation observation,
        SetupReferenceState? activeReference,
        bool rollbackAvailable)
    {
        var projectedState = ProjectState(state, observation, activeReference);
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
        SetupStatusTargetObservation observation,
        SetupReferenceState? activeReference)
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
                ProjectDynamic(state, observation, activeReference),
            _ => throw new InvalidOperationException(),
        };
    }

    private static SetupStatusProjectedState CompareStable(SetupReferenceState reference, bool matches) =>
        new(reference, matches ? SetupCurrentState.Current : SetupCurrentState.Stale);

    private static SetupStatusProjectedState ProjectDynamic(
        SetupChangeSetState state,
        SetupStatusTargetObservation observation,
        SetupReferenceState? priorReference)
    {
        if (observation.MatchesPrevious && observation.MatchesDesired)
        {
            return new SetupStatusProjectedState(EqualStateReference(state), SetupCurrentState.Current);
        }

        return observation.Classification switch
        {
            SetupStatusTargetClassification.Desired => new(
                SetupReferenceState.Desired,
                SetupCurrentState.Current),
            SetupStatusTargetClassification.Previous => new(
                priorReference ?? PreviousStateReference(state),
                SetupCurrentState.Current),
            SetupStatusTargetClassification.Diverged => new(SetupReferenceState.None, SetupCurrentState.Diverged),
            _ => new(SetupReferenceState.None, SetupCurrentState.Unavailable),
        };
    }

    private static SetupReferenceState EqualStateReference(SetupChangeSetState state) => state switch
    {
        SetupChangeSetState.Applying => SetupReferenceState.Base,
        SetupChangeSetState.Compensating or SetupChangeSetState.RollingBack => SetupReferenceState.Previous,
        SetupChangeSetState.Partial => SetupReferenceState.Desired,
        _ => throw new InvalidOperationException(),
    };

    private static SetupReferenceState PreviousStateReference(SetupChangeSetState state) => state switch
    {
        SetupChangeSetState.Applying => SetupReferenceState.Base,
        SetupChangeSetState.Compensating or SetupChangeSetState.RollingBack or SetupChangeSetState.Partial =>
            SetupReferenceState.Previous,
        _ => throw new InvalidOperationException(),
    };

    private static SetupChangeSetStatusResult Unavailable(SetupLedgerChangeSet changeSet)
    {
        var targets = changeSet.Targets.Select(target => target.TargetKind == SetupTargetKind.Guidance
            ? CreateGuidanceTarget(target, includeContentCapture: null)
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

    private static SetupStatusJournalDisposition RequireJournalLifecycle(
        SetupChangeSetState state,
        SetupTransactionJournal? journal)
    {
        if (journal is null)
        {
            if (state is SetupChangeSetState.Planned or SetupChangeSetState.NoChanges)
            {
                return SetupStatusJournalDisposition.None;
            }

            throw new FormatException();
        }

        var disposition = (state, journal.Operation, journal.Phase) switch
        {
            (SetupChangeSetState.Planned, SetupJournalOperation.Apply, SetupJournalPhase.Prepared) =>
                SetupStatusJournalDisposition.Dormant,
            (SetupChangeSetState.Applying, SetupJournalOperation.Apply,
                SetupJournalPhase.Prepared or SetupJournalPhase.Applying or SetupJournalPhase.Compensating) =>
                SetupStatusJournalDisposition.Active,
            (SetupChangeSetState.Applying, SetupJournalOperation.Apply, SetupJournalPhase.Committed) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.Compensating, SetupJournalOperation.Apply,
                SetupJournalPhase.Compensating or SetupJournalPhase.Partial) =>
                SetupStatusJournalDisposition.Active,
            (SetupChangeSetState.Compensating, SetupJournalOperation.Apply, SetupJournalPhase.Restored) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.Applied, SetupJournalOperation.Apply, SetupJournalPhase.Committed) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.Applied, SetupJournalOperation.Rollback, SetupJournalPhase.Prepared) =>
                SetupStatusJournalDisposition.Dormant,
            (SetupChangeSetState.Restored, SetupJournalOperation.Apply, SetupJournalPhase.Restored) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.RollingBack, SetupJournalOperation.Rollback,
                SetupJournalPhase.Prepared or SetupJournalPhase.RollingBack or SetupJournalPhase.Partial) =>
                SetupStatusJournalDisposition.Active,
            (SetupChangeSetState.RollingBack, SetupJournalOperation.Rollback, SetupJournalPhase.Committed) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.Partial, SetupJournalOperation.Apply,
                SetupJournalPhase.Compensating or SetupJournalPhase.Partial) =>
                SetupStatusJournalDisposition.Active,
            (SetupChangeSetState.Partial, SetupJournalOperation.Rollback,
                SetupJournalPhase.RollingBack or SetupJournalPhase.Partial) =>
                SetupStatusJournalDisposition.Active,
            (SetupChangeSetState.Partial, SetupJournalOperation.Rollback, SetupJournalPhase.Committed) =>
                SetupStatusJournalDisposition.Completed,
            (SetupChangeSetState.RolledBack, SetupJournalOperation.Rollback, SetupJournalPhase.Committed) =>
                SetupStatusJournalDisposition.Completed,
            _ => throw new FormatException(),
        };
        return disposition;
    }

    private sealed record SetupStatusRollbackResult(
        bool ChangeSetAvailable,
        IReadOnlyDictionary<Guid, bool> TargetAvailability,
        SetupRollbackPreflightObservations? Observations);

    private sealed record SetupStatusProjectedState(
        SetupReferenceState ReferenceState,
        SetupCurrentState CurrentState);

    private enum SetupStatusJournalDisposition
    {
        None,
        Dormant,
        Active,
        Completed,
    }
}

internal sealed class SetupStatusActiveEvidenceValidator
{
    private static readonly byte[] EnvironmentAggregateHashDomain =
        Encoding.ASCII.GetBytes("CAO-USER-ENV-AGGREGATE\0");
    private static readonly Encoding EnvironmentCanonicalEncoding =
        new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private readonly SetupRuntimePaths paths;
    private readonly AtomicFileSetupStep fileStep;
    private readonly UserEnvironmentSetupStep environmentStep;

    public SetupStatusActiveEvidenceValidator(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        this.paths = paths;
        fileStep = new AtomicFileSetupStep(platform);
        environmentStep = new UserEnvironmentSetupStep(platform);
    }

    public IReadOnlyDictionary<Guid, SetupReferenceState> Validate(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet,
        SetupTransactionJournal journal)
    {
        if (journal.ChangeSetId != changeSet.ChangeSetId ||
            journal.EnvironmentNotification != ExpectedNotification(journal))
        {
            throw new FormatException();
        }

        var expectedTargets = plan.Targets
            .Select((target, index) => (Plan: target, Ledger: changeSet.Targets[index]))
            .Where(item => item.Plan.TargetKind != SetupTargetKind.Guidance &&
                item.Plan.Members.Any(member => member.Operation != SetupOperation.NoOp))
            .ToArray();
        if (expectedTargets.Length != journal.Targets.Count)
        {
            throw new FormatException();
        }

        var references = new Dictionary<Guid, SetupReferenceState>();
        for (var index = 0; index < expectedTargets.Length; index++)
        {
            var (planTarget, ledgerTarget) = expectedTargets[index];
            var journalTarget = journal.Targets[index];
            if (journalTarget.RecordId != planTarget.RecordId ||
                journalTarget.TargetKind != planTarget.TargetKind ||
                !OwnershipMatches(
                    changeSet.State,
                    journal.Operation,
                    ledgerTarget,
                    planTarget.RecordId.ToString("D")))
            {
                throw new FormatException();
            }

            ValidateTarget(plan.ChangeSetId, planTarget, ledgerTarget, journalTarget, journal.Operation);
            references.Add(
                planTarget.RecordId,
                journal.Operation == SetupJournalOperation.Apply &&
                journal.Phase is SetupJournalPhase.Prepared or SetupJournalPhase.Applying
                    ? SetupReferenceState.Base
                    : SetupReferenceState.Previous);
        }

        var participating = expectedTargets.Select(item => item.Plan.RecordId).ToHashSet();
        if (changeSet.Targets.Where(target => !participating.Contains(target.RecordId)).Any(target =>
                target.AppliedStateHash is not null ||
                target.BackupReference is not null ||
                target.RollbackStatus != SetupLedgerRollbackStatus.NotAvailable))
        {
            throw new FormatException();
        }

        return references;
    }

    private void ValidateTarget(
        Guid changeSetId,
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupJournalTarget journalTarget,
        SetupJournalOperation operation)
    {
        var backupReference = planTarget.RecordId.ToString("D");
        if (planTarget.TargetKind == SetupTargetKind.Env)
        {
            var names = planTarget.Members.Select(member => member.SettingKey).ToArray();
            var backup = environmentStep.ReadBackup(paths.GetBackup(changeSetId, planTarget.RecordId), names);
            if (!string.Equals(backup.AggregateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
                !string.Equals(backup.AggregateHash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            var changed = planTarget.Members.Select((member, index) => (Member: member, Index: index))
                .Where(item => item.Member.Operation != SetupOperation.NoOp)
                .ToArray();
            if (changed.Length != journalTarget.Steps.Count)
            {
                throw new FormatException();
            }

            for (var index = 0; index < changed.Length; index++)
            {
                var item = changed[index];
                var step = journalTarget.Steps[index];
                if (!string.Equals(step.MemberKey, item.Member.SettingKey, StringComparison.Ordinal) ||
                    !string.Equals(step.PriorStateHash, backup.Members[item.Index].Hash, StringComparison.Ordinal) ||
                    !string.Equals(step.DesiredStateHash,
                        UserEnvironmentSetupStep.HashPlannedMember(item.Member), StringComparison.Ordinal) ||
                    !string.Equals(step.BackupReference, backupReference, StringComparison.Ordinal))
                {
                    throw new FormatException();
                }
            }

            if (operation == SetupJournalOperation.Rollback && !string.Equals(
                    ledgerTarget.AppliedStateHash,
                    ExpectedEnvironmentAppliedHash(planTarget, backup),
                    StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return;
        }

        var fileBackup = fileStep.ReadBackup(
            paths.GetBackup(changeSetId, planTarget.RecordId),
            planTarget.BaseStateHash);
        var expectedDesired = SetupDesiredStateHash.File(planTarget.DesiredState);
        if (!string.Equals(fileBackup.Hash, ledgerTarget.PreviousStateHash, StringComparison.Ordinal) ||
            journalTarget.Steps.Count != 1 ||
            journalTarget.Steps[0].MemberKey is not null ||
            !string.Equals(journalTarget.Steps[0].PriorStateHash, planTarget.BaseStateHash, StringComparison.Ordinal) ||
            !string.Equals(journalTarget.Steps[0].DesiredStateHash, expectedDesired, StringComparison.Ordinal) ||
            !string.Equals(journalTarget.Steps[0].BackupReference, backupReference, StringComparison.Ordinal) ||
            operation == SetupJournalOperation.Rollback &&
            !string.Equals(ledgerTarget.AppliedStateHash, expectedDesired, StringComparison.Ordinal))
        {
            throw new FormatException();
        }
    }

    private static bool OwnershipMatches(
        SetupChangeSetState state,
        SetupJournalOperation operation,
        SetupLedgerTarget target,
        string expectedBackupReference)
    {
        if (state == SetupChangeSetState.Planned)
        {
            return target.AppliedStateHash is null &&
                target.BackupReference is null &&
                target.OutcomeCode is null &&
                target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable;
        }

        var requiresAppliedHash = state is SetupChangeSetState.Applied or SetupChangeSetState.RollingBack ||
            state == SetupChangeSetState.Partial && operation == SetupJournalOperation.Rollback;
        if (requiresAppliedHash != (target.AppliedStateHash is not null) ||
            !string.Equals(target.BackupReference, expectedBackupReference, StringComparison.Ordinal))
        {
            return false;
        }

        if (state != SetupChangeSetState.Partial)
        {
            var expectedOutcome = state == SetupChangeSetState.Applied
                ? SetupCodes.ApplySucceeded
                : null;
            return string.Equals(target.OutcomeCode, expectedOutcome, StringComparison.Ordinal) &&
                target.RollbackStatus == SetupLedgerRollbackStatus.Pending;
        }

        return target.RollbackStatus switch
        {
            SetupLedgerRollbackStatus.Pending => target.OutcomeCode is null || string.Equals(
                target.OutcomeCode, SetupCodes.ApplySucceeded, StringComparison.Ordinal),
            SetupLedgerRollbackStatus.Stale => string.Equals(
                target.OutcomeCode, SetupCodes.RollbackStale, StringComparison.Ordinal),
            SetupLedgerRollbackStatus.Failed => string.Equals(
                target.OutcomeCode, SetupCodes.InternalError, StringComparison.Ordinal),
            SetupLedgerRollbackStatus.NotAvailable => string.Equals(
                target.OutcomeCode, SetupCodes.InterruptedRecoveryFailed, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static SetupEnvironmentNotification ExpectedNotification(SetupTransactionJournal journal) =>
        journal.Targets.Any(target => target.TargetKind == SetupTargetKind.Env &&
            target.Steps.Any(step => step.Phase != SetupJournalStepPhase.Pending))
            ? SetupEnvironmentNotification.Pending
            : SetupEnvironmentNotification.NotRequired;

    private static string ExpectedEnvironmentAppliedHash(
        SetupPrivatePlanTarget target,
        UserEnvironmentCapture backup)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(EnvironmentAggregateHashDomain);
        AppendUInt32(hash, checked((uint)target.Members.Count));
        for (var index = 0; index < target.Members.Count; index++)
        {
            var member = target.Members[index];
            var value = member.Operation == SetupOperation.NoOp
                ? backup.Members[index].Value
                : SetupEnvironmentPlanValue.Desired(member);
            AppendString(hash, member.SettingKey);
            hash.AppendData([value.Exists ? (byte)1 : (byte)0]);
            if (value.Exists)
            {
                AppendString(hash, value.Value!);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = EnvironmentCanonicalEncoding.GetBytes(value);
        AppendUInt32(hash, checked((uint)bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
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

    public static SetupStatusTargetObservation Unavailable { get; } =
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

    public SetupStatusTargetObservation FromRollback(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        SetupRollbackTargetObservation observation)
    {
        return planTarget.TargetKind == SetupTargetKind.Env
            ? CaptureEnvironment(planTarget, ledgerTarget, observation.EnvironmentCurrent)
            : CaptureFile(planTarget, observation.FileCurrent);
    }

    private SetupStatusTargetObservation CaptureFile(SetupPrivatePlanTarget target) =>
        CaptureFile(
            target,
            fileStep.Capture(
                Path.GetDirectoryName(target.TargetLocation) ?? throw new FormatException(),
                target.TargetLocation));

    private static SetupStatusTargetObservation CaptureFile(
        SetupPrivatePlanTarget target,
        AtomicFileCapture? capture)
    {
        if (capture is null)
        {
            return SetupStatusTargetObservation.Unavailable;
        }

        var current = capture.Hash;
        var desired = SetupDesiredStateHash.File(target.DesiredState);
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
        SetupLedgerTarget ledgerTarget) => CaptureEnvironment(
            planTarget,
            ledgerTarget,
            environmentStep.Capture(planTarget.Members.Select(member => member.SettingKey).ToArray()));

    private static SetupStatusTargetObservation CaptureEnvironment(
        SetupPrivatePlanTarget planTarget,
        SetupLedgerTarget ledgerTarget,
        UserEnvironmentCapture? capture)
    {
        if (capture is null)
        {
            return SetupStatusTargetObservation.Unavailable;
        }
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

internal static class SetupFailedRecoveryOverlayValidator
{
    public static void Validate(
        SetupLedgerChangeSet evidence,
        SetupLedgerChangeSet effective,
        bool requireTerminalEvidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(effective);
        if (requireTerminalEvidence && evidence.State is not SetupChangeSetState.Applied and
                not SetupChangeSetState.Restored and
                not SetupChangeSetState.RolledBack ||
            effective.State != SetupChangeSetState.Partial ||
            effective.OutcomeCode != SetupCodes.InterruptedRecoveryFailed ||
            effective.ChangeSetId != evidence.ChangeSetId ||
            effective.Adapter != evidence.Adapter ||
            effective.SelectedTarget != evidence.SelectedTarget ||
            effective.CreatedAt != evidence.CreatedAt ||
            effective.UpdatedAt < evidence.UpdatedAt ||
            effective.ToolVersion != evidence.ToolVersion ||
            effective.Targets.Count != evidence.Targets.Count)
        {
            throw new FormatException();
        }

        for (var index = 0; index < evidence.Targets.Count; index++)
        {
            var durableTarget = evidence.Targets[index];
            var effectiveTarget = effective.Targets[index];
            if (effectiveTarget.RecordId != durableTarget.RecordId ||
                effectiveTarget.TargetKind != durableTarget.TargetKind ||
                effectiveTarget.TargetLabel != durableTarget.TargetLabel ||
                effectiveTarget.OwningAdapter != durableTarget.OwningAdapter ||
                !effectiveTarget.Members.SequenceEqual(durableTarget.Members) ||
                effectiveTarget.PreviousStateHash != durableTarget.PreviousStateHash ||
                effectiveTarget.AppliedStateHash != durableTarget.AppliedStateHash ||
                effectiveTarget.BackupReference != durableTarget.BackupReference ||
                effectiveTarget.OutcomeCode != SetupCodes.InterruptedRecoveryFailed ||
                effectiveTarget.RollbackStatus != SetupLedgerRollbackStatus.NotAvailable ||
                effectiveTarget.RestartRequirement != durableTarget.RestartRequirement ||
                !StatusProjectionMatches(effectiveTarget.StatusProjection, durableTarget.StatusProjection) ||
                effectiveTarget.ToolVersion != durableTarget.ToolVersion)
            {
                throw new FormatException();
            }
        }
    }

    private static bool StatusProjectionMatches(SetupStatusProjection left, SetupStatusProjection right) =>
        left.Detected == right.Detected &&
        left.DetectedVersion == right.DetectedVersion &&
        left.Operation == right.Operation &&
        left.EffectiveSource == right.EffectiveSource &&
        left.Endpoint == right.Endpoint &&
        ExpectedResultMatches(left.ExpectedResult, right.ExpectedResult) &&
        GuidanceMatches(left.Guidance, right.Guidance) &&
        left.Changes.SequenceEqual(right.Changes);

    private static bool ExpectedResultMatches(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || JsonElement.DeepEquals(left.Value, right!.Value));

    private static bool GuidanceMatches(SetupStatusGuidance? left, SetupStatusGuidance? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        left.Kind == right.Kind &&
        left.Language == right.Language;
}

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
        setupLock.AssertHeld(platform, paths);
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

            CreateBackups(plan, changedCaptures);

            var journalTargets = CreateJournalTargets(changedCaptures);
            journalStore.CreatePrepared(setupLock, changeSetId, SetupJournalOperation.Apply, journalTargets);
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
        catch (SetupApplyException exception)
        {
            if (SetupCodes.ResultCodes.Contains(exception.Code, StringComparer.Ordinal))
            {
                throw;
            }

            throw new SetupApplyException(SetupCodes.InternalError);
        }
        catch (SetupStorageException exception)
        {
            throw new SetupApplyException(MapStorageCode(exception.Code));
        }
        catch (SetupFileStepException exception)
        {
            throw new SetupApplyException(MapMutationCode(exception.Code));
        }
        catch (SetupEnvironmentStepException exception)
        {
            throw new SetupApplyException(MapMutationCode(exception.Code));
        }
        catch (Exception)
        {
            throw new SetupApplyException(SetupCodes.InternalError);
        }
    }

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

                    var changedMembers = target.Members
                        .Select((member, index) => (member, index))
                        .Where(item => !string.Equals(
                            capture.Members[item.index].Hash,
                            environmentStep.HashMember(item.member.SettingKey, DesiredEnvironmentValue(item.member)),
                            StringComparison.Ordinal))
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
                    captures.Add(new TargetCapture(
                        target,
                        capture,
                        null,
                        !string.Equals(capture.Hash, desiredHash, StringComparison.Ordinal),
                        []));
                    break;
                }
            }
        }

        return captures;
    }

    private SetupLedgerChangeSet CreateNoChangesChangeSet(SetupLedgerChangeSet planned) => planned with
    {
        UpdatedAt = platform.Clock.UtcNow,
        OutcomeCode = SetupCodes.NoChanges,
        State = SetupChangeSetState.NoChanges,
        Targets = planned.Targets.Select(target => target with { OutcomeCode = SetupCodes.NoChanges }).ToArray(),
    };

    private void CreateBackups(SetupPrivatePlan plan, IReadOnlyList<TargetCapture> captures)
    {
        platform.FileSystem.CreateDirectory(paths.Backups);
        platform.FileSystem.CreateDirectory(Path.Combine(paths.Backups, plan.ChangeSetId.ToString("D")));
        foreach (var capture in captures)
        {
            var backupPath = paths.GetBackup(plan.ChangeSetId, capture.Target.RecordId);
            if (capture.File is not null)
            {
                fileStep.CreateBackup(backupPath, capture.File);
            }
            else
            {
                environmentStep.CreateBackup(backupPath, capture.Environment!);
            }
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
                    environmentStep.HashMember(member.SettingKey, DesiredEnvironmentValue(member)),
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
        var capturesByRecordId = changedCaptures.ToDictionary(capture => capture.Target.RecordId);
        return planned with
        {
            UpdatedAt = platform.Clock.UtcNow,
            State = SetupChangeSetState.Applying,
            Targets = planned.Targets.Select(target => capturesByRecordId.TryGetValue(target.RecordId, out var capture)
                ? target with
                {
                    Members = capture.Environment is null
                        ? target.Members
                        : capture.ChangedMemberIndices.Select(index => target.Members[index]).ToArray(),
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
                    DesiredEnvironmentValue(member));
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
                        var desiredHash = environmentStep.HashMember(member.SettingKey, DesiredEnvironmentValue(member));
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

    private static UserEnvironmentValue DesiredEnvironmentValue(SetupPrivatePlanMember member) =>
        member.Operation == SetupOperation.Remove
            ? UserEnvironmentValue.Missing
            : UserEnvironmentValue.Present(member.DesiredValue!);

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
}

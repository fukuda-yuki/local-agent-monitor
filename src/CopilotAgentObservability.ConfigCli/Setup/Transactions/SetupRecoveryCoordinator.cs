using System.Buffers.Binary;
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
        return setupLock.ExecuteWhileHeld(platform, paths, () => RecoverNextCore(setupLock));
    }

    private SetupRecoveryResult RecoverNextCore(SetupLock setupLock)
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
                return Failed(SetupCodes.RecoveryRequired, changeSet.ChangeSetId, OperationFor(changeSet),
                    OverlayFailure(changeSet));
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
        RequireImmutableIdentity(plan, changeSet);
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
            RequireImmutableIdentity(plan, changeSet);
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
            RequireImmutableIdentity(plan, changeSet);
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
                var expectedSteps = new List<SetupJournalStep>();
                for (var memberIndex = 0; memberIndex < target.Members.Count; memberIndex++)
                {
                    var desiredHash = environmentStep.HashMember(
                        names[memberIndex], DesiredEnvironmentValue(target.Members[memberIndex]));
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

    private static void RequireImmutableIdentity(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
    {
        if (plan.ChangeSetId != changeSet.ChangeSetId ||
            !string.Equals(plan.Adapter, changeSet.Adapter, StringComparison.Ordinal) ||
            !string.Equals(plan.SelectedTarget, changeSet.SelectedTarget, StringComparison.Ordinal) ||
            plan.CreatedAt != changeSet.CreatedAt ||
            !string.Equals(plan.ToolVersion, changeSet.ToolVersion, StringComparison.Ordinal) ||
            plan.Targets.Count != changeSet.Targets.Count)
        {
            throw new FormatException();
        }

        for (var targetIndex = 0; targetIndex < plan.Targets.Count; targetIndex++)
        {
            var expected = plan.Targets[targetIndex];
            var actual = changeSet.Targets[targetIndex];
            if (expected.RecordId != actual.RecordId ||
                expected.TargetKind != actual.TargetKind ||
                !string.Equals(expected.BaseStateHash, actual.PreviousStateHash, StringComparison.Ordinal) ||
                expected.Members.Count != actual.Members.Count)
            {
                throw new FormatException();
            }

            for (var memberIndex = 0; memberIndex < expected.Members.Count; memberIndex++)
            {
                if (!string.Equals(expected.Members[memberIndex].SettingKey,
                        actual.Members[memberIndex].SettingKey, StringComparison.Ordinal) ||
                    expected.Members[memberIndex].Operation != actual.Members[memberIndex].Operation)
                {
                    throw new FormatException();
                }
            }
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
        operation == SetupRecoveryOperation.Apply
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

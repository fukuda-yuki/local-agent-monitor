using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupCompensationTests
{
    private const string TaggedPreviousMarker = "PRIVATE_TAGGED_COMPENSATION_MARKER";

    private static SetupStatusProjection CreateStatusProjection(IReadOnlyList<SetupLedgerMember> members)
    {
        var operations = members.Select(member => member.Operation).Where(operation => operation != SetupOperation.NoOp).Distinct().ToArray();
        var aggregate = operations.Length switch { 0 => SetupOperation.NoOp, 1 => operations[0], _ => SetupOperation.Mixed };
        var expectedResult = members.All(member => member.SettingKey.StartsWith("ENV_", StringComparison.Ordinal))
            ? SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson
            : (JsonElement?)null;
        return new SetupStatusProjection(true, null, aggregate, null, null, expectedResult, null,
            members.Select(member => new SetupMemberChangeResult(member.SettingKey, member.Operation, "present", "configured", "none", false)).ToArray());
    }

    [Theory]
    [InlineData(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent)]
    [InlineData(SetupFaultPoint.AfterMutationIntentBeforeMutation)]
    [InlineData(SetupFaultPoint.AfterMutationBeforeCompletion)]
    [InlineData(SetupFaultPoint.AfterCompletionBeforeCommit)]
    public void Apply_TaggedFaultBoundaryCompensatesFromBackupWithoutRematerializing(string faultPoint)
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false, tagged: true);
        fixture.Platform.InjectFault($"checkpoint:{faultPoint}", new IOException("private-fault"));
        SetupApplyException exception;
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            exception = Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        }

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Contains(TaggedPreviousMarker,
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)),
            StringComparison.Ordinal);
        Assert.Contains(TaggedPreviousMarker,
            Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(
                fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId))),
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.MaterializingRevalidator!.Calls);
        var recovery = fixture.RecoverAfterReopen();
        Assert.Equal(SetupRecoveryDisposition.None, recovery.Disposition);
        Assert.Equal(1, fixture.MaterializingRevalidator.Calls);
        fixture.AssertTaggedMarkerAbsentFromSafeEvidence(
            JsonSerializer.Serialize(recovery),
            exception.Message);
    }

    [Theory]
    [InlineData("prior", true)]
    [InlineData("desired", true)]
    [InlineData("third-party", false)]
    public void RecoverNext_TaggedInterruptedCompensationClassifiesHashAndBackupWithoutRematerializing(
        string currentState,
        bool expectedRecovered)
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false, tagged: true);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterCompletionBeforeCommit}",
            new IOException("private-forward"));
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}",
            new IOException("private-compensation"));
        SetupApplyException forwardException;
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            forwardException = Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        }
        fixture.SeedTaggedCurrentState(currentState);
        var revalidationCalls = fixture.MaterializingRevalidator!.Calls;

        var result = fixture.RecoverAfterReopen();

        Assert.Equal(
            expectedRecovered ? SetupRecoveryDisposition.Recovered : SetupRecoveryDisposition.Failed,
            result.Disposition);
        Assert.Equal(revalidationCalls, fixture.MaterializingRevalidator.Calls);
        var current = fixture.Platform.ReadSeededFile(fixture.TargetPath);
        if (currentState == "third-party")
        {
            Assert.Equal(Encoding.UTF8.GetBytes("third-party"), current);
        }
        else
        {
            Assert.Equal(fixture.PriorBytes, current);
        }
        fixture.AssertTaggedMarkerAbsentFromSafeEvidence(
            JsonSerializer.Serialize(result),
            forwardException.Message);
    }

    [Fact]
    public void RecoverNext_TaggedInterruptedRollbackRestoresBackupWithoutRematerializing()
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false, tagged: true);
        using (var applyLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            Assert.Equal(SetupChangeSetState.Applied,
                fixture.Coordinator.Apply(applyLock.Lock!, fixture.ChangeSetId).Value.State);
        }
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}",
            new IOException("private-rollback"));
        SetupRollbackExecutionResult rollback;
        using (var rollbackLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            rollback = new SetupRollbackCoordinator(
                fixture.Platform,
                fixture.Paths,
                fixture.PlanStore,
                fixture.LedgerStore,
                fixture.JournalStore).Rollback(rollbackLock.Lock!, fixture.ChangeSetId);
            Assert.False(rollback.Success);
        }
        var revalidationCalls = fixture.MaterializingRevalidator!.Calls;

        var result = fixture.RecoverAfterReopen();

        Assert.Equal(SetupRecoveryDisposition.Recovered, result.Disposition);
        Assert.Equal(revalidationCalls, fixture.MaterializingRevalidator.Calls);
        Assert.Equal(fixture.PriorBytes, fixture.Platform.ReadSeededFile(fixture.TargetPath));
        fixture.AssertTaggedMarkerAbsentFromSafeEvidence(
            string.Join("\n", JsonSerializer.Serialize(rollback), JsonSerializer.Serialize(result)),
            rollback.Code);
    }
    [Fact]
    public async Task Apply_DisposeRequestedAtEnvironmentRestoreKeepsExclusiveLockUntilRecoveryEvidenceIsReturned()
    {
        var fixture = CompensationFixture.Create();
        var restoreBarrierReady = new TaskCompletionSource<SetupTestBarrier>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => restoreBarrierReady.SetResult(fixture.Platform.AddBarrier("environment.set:ENV_B")));
        using var disposeRequested = new ManualResetEventSlim();
        var exclusive = new TrackingExclusiveLock();
        var setupLock = SetupLock.CreateForTesting(exclusive, fixture.Platform, fixture.Paths, disposeRequested.Set);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(setupLock, fixture.ChangeSetId)));
        using var restore = await restoreBarrierReady.Task;
        restore.WaitUntilReached(CancellationToken.None);

        var disposing = Task.Run(setupLock.Dispose);
        Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(exclusive.IsDisposed);
        restore.Release();

        var exception = await applying;
        await disposing;

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.True(exclusive.IsDisposed);
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupChangeSetState.Compensating, fixture.LoadChangeSet().State);
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Compensating, journal.Phase);
        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps
                .Single(step => step.MemberKey == "ENV_B").Phase);
    }

    [Theory]
    [InlineData("ENV_A", false)]
    [InlineData("ENV_A", true)]
    [InlineData("ENV_B", false)]
    [InlineData("ENV_B", true)]
    [InlineData("ENV_C", false)]
    [InlineData("ENV_C", true)]
    public void Apply_EnvironmentForwardFaultBeforeOrAfterEffectRestoresThePriorAggregate(
        string member,
        bool afterEffect)
    {
        var fixture = CompensationFixture.Create();
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault($"environment.set:{member}", new IOException("PRIVATE_VALUE"));
        }
        else
        {
            fixture.Platform.InjectFault($"environment.set:{member}", new IOException("PRIVATE_VALUE"));
        }
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("PRIVATE_VALUE", exception.Message, StringComparison.Ordinal);
        AssertFullyRestored(fixture);
    }

    [Fact]
    public void Apply_OneShotJournalReadFailureAfterForwardPrimitiveFailureStillCompensates()
    {
        var fixture = CompensationFixture.Create();
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => fixture.Platform.InjectFault(
                $"file.read-bounded:{journalPath}:{SetupTransactionJournalStore.MaximumJournalBytes}",
                new IOException("JOURNAL_PRIVATE")));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        AssertFullyRestored(fixture);
        Assert.DoesNotContain("JOURNAL_PRIVATE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UnprovenJournalReadAfterForwardPrimitiveFailureRequiresRecovery()
    {
        var fixture = CompensationFixture.Create();
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var readOperation = $"file.read-bounded:{journalPath}:{SetupTransactionJournalStore.MaximumJournalBytes}";
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.InjectFault(readOperation, new IOException("JOURNAL_PRIVATE_ONE"));
                fixture.Platform.InjectFault(readOperation, new IOException("JOURNAL_PRIVATE_TWO"));
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupJournalPhase.Applying, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet().State);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain("JOURNAL_PRIVATE", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupFaultPoint.AfterRestoreIntentBeforeRestore, false)]
    [InlineData(SetupFaultPoint.AfterRestoreBeforeCompletion, true)]
    public void Apply_RestoreCheckpointFaultClassifiesPhysicalEffectBeforeContinuing(
        string faultPoint,
        bool effectOccurred)
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault("environment.set:ENV_B", new IOException("FORWARD_PRIVATE"));
        fixture.Platform.InjectFault($"checkpoint:{faultPoint}", new IOException("RESTORE_PRIVATE"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        if (effectOccurred)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            AssertFullyRestored(fixture);
        }
        else
        {
            Assert.Equal(SetupCodes.PartialApply, exception.Code);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
            Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
            Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        }
    }

    [Fact]
    public void Apply_ForwardFailureRestoresTouchedStepsInReverseAndLeavesPendingUntouched()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectFault("environment.set:ENV_B", new IOException("PRIVATE_VALUE"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
        Assert.Equal(
            [
                SetupJournalStepPhase.RestoreCompleted,
                SetupJournalStepPhase.RestoreCompleted,
                SetupJournalStepPhase.RestoreCompleted,
                SetupJournalStepPhase.Pending,
            ],
            journal.Targets.SelectMany(target => target.Steps).Select(step => step.Phase));
        var ledger = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Restored, ledger.State);
        Assert.Equal(SetupCodes.InternalError, ledger.OutcomeCode);
        Assert.All(ledger.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });

        var operations = fixture.Platform.Operations;
        var envRestore = LastIndexOf(operations, "environment.set:ENV_A");
        var fileRestore = LastIndexContaining(operations, $"->{fixture.TargetPath}");
        Assert.True(envRestore >= 0 && fileRestore > envRestore);
        Assert.Equal(1, operations.Count(operation => operation == "environment.set:ENV_B"));
        Assert.DoesNotContain("environment.set:ENV_C", operations);
        Assert.Equal(1, operations.Count(operation => operation == "environment.notify"));
        Assert.True(
            LastIndexContaining(operations, $"->{fixture.Paths.OwnershipLedger}") <
            LastIndexOf(operations, "environment.notify"));
    }

    [Fact]
    public void Apply_FullCompensationDoesNotAssignFailureOrOwnershipToNoOpPhysicalTarget()
    {
        var fixture = CompensationFixture.Create(fileNoChange: true);
        fixture.Platform.InjectFault("environment.set:ENV_B", new IOException("PRIVATE_VALUE"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        var file = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Null(file.OutcomeCode);
        Assert.Null(file.AppliedStateHash);
        Assert.Null(file.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, file.RollbackStatus);
        Assert.Equal([fixture.EnvironmentRecordId], fixture.JournalStore.Load(fixture.ChangeSetId)!.Targets.Select(target => target.RecordId));
    }

    [Fact]
    public void Apply_ThirdPartyEnvironmentMemberIsPreservedWhileEarlierOwnedStepsAreRestored()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_C",
            new IOException("PRIVATE_VALUE"),
            () => fixture.Platform.SeedUserEnvironment("ENV_B", "third-party"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_B").Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_C").Phase);
        var changeSet = fixture.LoadChangeSet();
        var file = changeSet.Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Null(file.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, file.RollbackStatus);
        var environment = changeSet.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
        var operations = fixture.Platform.Operations;
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_C") <
            LastIndexOf(operations, "environment.set:ENV_A"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_A") <
            LastIndexContaining(operations, $"->{fixture.TargetPath}"));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_UnavailableEnvironmentMemberDoesNotBlockEarlierOwnedRestores()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_C",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.InjectFault("environment.get:ENV_B", new IOException("CAPTURE_PRIVATE_ONE"));
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_B").Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_C").Phase);
        var changeSet = fixture.LoadChangeSet();
        var file = changeSet.Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Null(file.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, file.RollbackStatus);
        var environment = changeSet.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.InternalError, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, environment.RollbackStatus);
        var operations = fixture.Platform.Operations;
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_C") <
            LastIndexOf(operations, "environment.set:ENV_A"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_A") <
            LastIndexContaining(operations, $"->{fixture.TargetPath}"));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_MixedFileAndEnvironmentFailuresAggregateWhileNoOpMemberRemainsUnowned()
    {
        var fixture = CompensationFixture.Create(environmentCNoChange: true);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party-file"));
                fixture.Platform.SeedUserEnvironment("ENV_A", "third-party-a");
                fixture.Platform.SeedUserEnvironment("ENV_C", "third-party-c");
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("third-party-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("third-party-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        Assert.Equal("third-party-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain("environment.set:ENV_C", fixture.Platform.Operations);
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(["ENV_A", "ENV_B"], environmentSteps.Select(step => step.MemberKey));
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, environmentSteps[0].Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps[1].Phase);
        var changeSet = fixture.LoadChangeSet();
        var file = changeSet.Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Equal(SetupCodes.RollbackStale, file.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, file.RollbackStatus);
        var environment = changeSet.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public async Task Apply_AccumulatedSafeFailureIsPersistedWithLaterHardRestoreFailure()
    {
        var fixture = CompensationFixture.Create();
        var fileClassificationReady = new TaskCompletionSource<SetupTestBarrier>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_C",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.SeedUserEnvironment("ENV_B", "third-party");
                fileClassificationReady.SetResult(
                    fixture.Platform.AddBarrier($"file.metadata:{fixture.TargetPath}"));
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        using var fileClassification = await fileClassificationReady.Task;
        fileClassification.WaitUntilReached(CancellationToken.None);
        using var fileRestore = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}");
        fileClassification.Release();
        fileRestore.WaitUntilReached(CancellationToken.None);
        var restoreTemporary = NextTemporaryPath(fixture.Platform.Operations, fixture.TargetPath);
        fixture.Platform.InjectFault(
            $"file.replace:{restoreTemporary}->{fixture.TargetPath}",
            new IOException("RESTORE_PRIVATE"));
        fileRestore.Release();

        var exception = await applying;

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreStarted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_B").Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, environmentSteps.Single(step => step.MemberKey == "ENV_C").Phase);
        var changeSet = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, changeSet.State);
        var file = changeSet.Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Equal(SetupCodes.InternalError, file.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, file.RollbackStatus);
        var environment = changeSet.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
        var operations = fixture.Platform.Operations;
        Assert.True(
            LastIndexContaining(operations, $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}") <
            LastIndexContaining(operations, $"->{fixture.Paths.OwnershipLedger}"));
        Assert.DoesNotContain("environment.notify", operations);
    }

    [Fact]
    public void Apply_RestorePrimitiveBeforeEffectFailurePreservesDesiredStateAndContinuesEarlierTarget()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => fixture.Platform.InjectFault("environment.set:ENV_A", new IOException("RESTORE_PRIVATE")));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        Assert.Equal(
            SetupJournalStepPhase.RestoreCompleted,
            environmentSteps.Single(step => step.MemberKey == "ENV_B").Phase);
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.InternalError, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, environment.RollbackStatus);
        var operations = fixture.Platform.Operations;
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_B") <
            LastIndexOf(operations, "environment.set:ENV_A"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_A") <
            LastIndexContaining(operations, $"->{fixture.TargetPath}"));
        Assert.DoesNotContain("environment.notify", operations);
    }

    [Fact]
    public void Apply_RestorePrimitiveUnavailableRecapturePreservesPriorStateAndContinuesEarlierTarget()
    {
        var fixture = CompensationFixture.Create();
        var restoreSetOperationIndex = -1;
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => fixture.Platform.InjectAfterEffectFault(
                "environment.set:ENV_A",
                new IOException("RESTORE_PRIVATE"),
                () =>
                {
                    restoreSetOperationIndex = fixture.Platform.Operations.Count - 1;
                    fixture.Platform.InjectFault("environment.get:ENV_A", new IOException("CAPTURE_PRIVATE"));
                }));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var operations = fixture.Platform.Operations;
        Assert.True(restoreSetOperationIndex >= 0);
        Assert.Equal(2, operations.Count(operation => operation == "environment.set:ENV_A"));
        Assert.Contains(
            operations.Skip(restoreSetOperationIndex + 1),
            operation => operation == "environment.get:ENV_A");
        Assert.True(
            LastIndexContaining(operations, $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}") <
            LastIndexContaining(operations, $"->{fixture.Paths.OwnershipLedger}"));
        Assert.Equal("old-a", fixture.Platform.UserEnvironment.Get("ENV_A"));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.InternalError, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, environment.RollbackStatus);
        Assert.DoesNotContain("environment.notify", operations);
    }

    [Fact]
    public void Apply_RestorePrimitiveAfterEffectFailureRecapturesPriorWithoutRetryAndContinues()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => fixture.Platform.InjectAfterEffectFault(
                "environment.set:ENV_A",
                new IOException("RESTORE_PRIVATE")));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        AssertFullyRestored(fixture);
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(
            SetupJournalStepPhase.RestoreCompleted,
            environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        var operations = fixture.Platform.Operations;
        Assert.Equal(2, operations.Count(operation => operation == "environment.set:ENV_A"));
        Assert.Equal(1, operations.Count(operation => operation == "environment.notify"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_B") <
            LastIndexOf(operations, "environment.set:ENV_A"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_A") <
            LastIndexContaining(operations, $"->{fixture.TargetPath}"));
        Assert.True(
            LastIndexContaining(operations, $"->{fixture.Paths.OwnershipLedger}") <
            LastIndexOf(operations, "environment.notify"));
    }

    [Fact]
    public void Apply_RestorePrimitiveAfterEffectThirdPartyRaceIsPreservedAndPartial()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => fixture.Platform.InjectAfterEffectFault(
                "environment.set:ENV_A",
                new IOException("RESTORE_PRIVATE"),
                () => fixture.Platform.SeedUserEnvironment("ENV_A", "third-party")));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_MultipleRestorePrimitiveFailuresContinueInReverseAndRetainStrongestTargetFailure()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_C",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.InjectFault("environment.set:ENV_C", new IOException("RESTORE_PRIVATE_C"));
                fixture.Platform.InjectAfterEffectFault(
                    "environment.set:ENV_B",
                    new IOException("RESTORE_PRIVATE_B"),
                    () => fixture.Platform.SeedUserEnvironment("ENV_B", "third-party"));
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("desired-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
        var environmentSteps = journal.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Steps;
        Assert.Equal(
            SetupJournalStepPhase.RestoreCompleted,
            environmentSteps.Single(step => step.MemberKey == "ENV_A").Phase);
        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            environmentSteps.Single(step => step.MemberKey == "ENV_B").Phase);
        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            environmentSteps.Single(step => step.MemberKey == "ENV_C").Phase);
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.InternalError, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, environment.RollbackStatus);
        var operations = fixture.Platform.Operations;
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_C") <
            LastIndexOf(operations, "environment.set:ENV_B"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_B") <
            LastIndexOf(operations, "environment.set:ENV_A"));
        Assert.True(
            LastIndexOf(operations, "environment.set:ENV_A") <
            LastIndexContaining(operations, $"->{fixture.TargetPath}"));
        Assert.DoesNotContain("environment.notify", operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_FileRestorePrimitiveFaultClassifiesBeforeOrAfterEffect(bool afterEffect)
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false);
        using var forwardBarrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}");
        using var restoreBarrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        forwardBarrier.WaitUntilReached(CancellationToken.None);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        fixture.Platform.InjectFault(
            $"file.write-new:{NextTemporaryPath(fixture.Platform.Operations, journalPath)}",
            new IOException("FORWARD_PRIVATE"));
        forwardBarrier.Release();

        restoreBarrier.WaitUntilReached(CancellationToken.None);
        var restoreTemporary = NextTemporaryPath(fixture.Platform.Operations, fixture.TargetPath);
        var restoreOperation = $"file.replace:{restoreTemporary}->{fixture.TargetPath}";
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(restoreOperation, new IOException("RESTORE_PRIVATE"));
        }
        else
        {
            fixture.Platform.InjectFault(restoreOperation, new IOException("RESTORE_PRIVATE"));
        }
        restoreBarrier.Release();

        var exception = await applying;
        if (afterEffect)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
            var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
            Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
            Assert.Equal(SetupJournalStepPhase.RestoreCompleted, journal.Targets[0].Steps[0].Phase);
            Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        }
        else
        {
            Assert.Equal(SetupCodes.PartialApply, exception.Code);
            Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
            var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
            Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
            Assert.Equal(SetupJournalStepPhase.RestoreStarted, journal.Targets[0].Steps[0].Phase);
            var changeSet = fixture.LoadChangeSet();
            Assert.Equal(SetupChangeSetState.Partial, changeSet.State);
            Assert.Equal(SetupCodes.InternalError, changeSet.Targets[0].OutcomeCode);
            Assert.Equal(SetupLedgerRollbackStatus.Failed, changeSet.Targets[0].RollbackStatus);
        }

        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_EnvironmentRestoreIntentJournalFaultRequiresDurableIntentBeforeRestore(bool afterEffect)
    {
        var fixture = CompensationFixture.Create();
        var classificationReady = new TaskCompletionSource<SetupTestBarrier>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () => classificationReady.SetResult(fixture.Platform.AddBarrier("environment.get:ENV_B")));

        SetupApplyException exception;
        string journalFaultOperation;
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
            using var classification = await classificationReady.Task;
            classification.WaitUntilReached(CancellationToken.None);
            journalFaultOperation = InjectNextJournalWriteFault(fixture, afterEffect);
            classification.Release();
            exception = await applying;
        }

        if (afterEffect)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            AssertFullyRestored(fixture);
            Assert.True(
                LastIndexOf(fixture.Platform.Operations, journalFaultOperation) <
                LastIndexOf(fixture.Platform.Operations, "environment.set:ENV_B"));
            return;
        }

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.set:ENV_B"));
        AssertCompensatingStep(fixture, fixture.EnvironmentRecordId, "ENV_B", SetupJournalStepPhase.MutationStarted);

        AssertRecoveredAfterReopen(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_FileRestoreIntentJournalFaultRequiresDurableIntentBeforeRestore(bool afterEffect)
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false);
        using var forwardReady = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterMutationIntentBeforeMutation}");
        var classificationReady = new TaskCompletionSource<SetupTestBarrier>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SetupApplyException exception;
        string journalFaultOperation;
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
            forwardReady.WaitUntilReached(CancellationToken.None);
            var forwardTemporary = NextTemporaryPath(fixture.Platform.Operations, fixture.TargetPath);
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{forwardTemporary}->{fixture.TargetPath}",
                new IOException("FORWARD_PRIVATE"),
                () => classificationReady.SetResult(
                    fixture.Platform.AddBarrier($"file.read:{fixture.TargetPath}")));
            forwardReady.Release();

            using var classification = await classificationReady.Task;
            classification.WaitUntilReached(CancellationToken.None);
            journalFaultOperation = InjectNextJournalWriteFault(fixture, afterEffect);
            classification.Release();
            exception = await applying;
        }

        if (afterEffect)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            AssertFullyRestored(fixture);
            Assert.True(
                LastIndexOf(fixture.Platform.Operations, journalFaultOperation) <
                LastIndexContaining(fixture.Platform.Operations, $"->{fixture.TargetPath}"));
            return;
        }

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(
            1,
            fixture.Platform.Operations.Count(operation =>
                operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith($"->{fixture.TargetPath}", StringComparison.Ordinal)));
        AssertCompensatingStep(fixture, fixture.FileRecordId, null, SetupJournalStepPhase.MutationStarted);

        AssertRecoveredAfterReopen(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_EnvironmentRestoreCompletionJournalFaultRequiresDurableCompletionBeforeContinuing(
        bool afterEffect)
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault("environment.set:ENV_B", new IOException("FORWARD_PRIVATE"));
        SetupApplyException exception;
        string journalFaultOperation;
        using (var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}"))
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
            barrier.WaitUntilReached(CancellationToken.None);
            journalFaultOperation = InjectNextJournalWriteFault(fixture, afterEffect);
            barrier.Release();
            exception = await applying;
        }

        if (afterEffect)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            AssertFullyRestored(fixture);
            var operations = fixture.Platform.Operations;
            Assert.True(
                LastIndexOf(operations, "environment.set:ENV_B") <
                LastIndexOf(operations, journalFaultOperation));
            Assert.True(
                LastIndexOf(operations, journalFaultOperation) <
                LastIndexOf(operations, "environment.set:ENV_A"));
            return;
        }

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        AssertCompensatingStep(fixture, fixture.EnvironmentRecordId, "ENV_B", SetupJournalStepPhase.RestoreStarted);

        AssertRecoveredAfterReopen(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_FileRestoreCompletionJournalFaultRequiresDurableCompletionBeforeContinuing(bool afterEffect)
    {
        var fixture = CompensationFixture.Create(includeEnvironment: false);
        using var forwardReady = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterMutationIntentBeforeMutation}");

        SetupApplyException exception;
        string journalFaultOperation;
        using (var completionReady = fixture.Platform.AddBarrier(
                   $"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}"))
        using (var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
                fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
            forwardReady.WaitUntilReached(CancellationToken.None);
            var forwardTemporary = NextTemporaryPath(fixture.Platform.Operations, fixture.TargetPath);
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{forwardTemporary}->{fixture.TargetPath}",
                new IOException("FORWARD_PRIVATE"));
            forwardReady.Release();

            completionReady.WaitUntilReached(CancellationToken.None);
            journalFaultOperation = InjectNextJournalWriteFault(fixture, afterEffect);
            completionReady.Release();
            exception = await applying;
        }

        if (afterEffect)
        {
            Assert.Equal(SetupCodes.InternalError, exception.Code);
            AssertFullyRestored(fixture);
            Assert.True(
                LastIndexContaining(fixture.Platform.Operations, $"->{fixture.TargetPath}") <
                LastIndexOf(fixture.Platform.Operations, journalFaultOperation));
            Assert.True(
                LastIndexOf(fixture.Platform.Operations, journalFaultOperation) <
                LastIndexContaining(fixture.Platform.Operations, $"->{fixture.Paths.OwnershipLedger}"));
            return;
        }

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        AssertCompensatingStep(fixture, fixture.FileRecordId, null, SetupJournalStepPhase.RestoreStarted);

        AssertRecoveredAfterReopen(fixture);
    }

    [Fact]
    public async Task Apply_RestoredLedgerPreReplaceFaultLeavesJournalAheadForRecoveryWithoutRepeatingRestores()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault("environment.set:ENV_B", new IOException("FORWARD_PRIVATE"));
        using var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.Platform.InjectFault(
            $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}",
            new IOException("PRIVATE_LEDGER"));
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        AssertFullyPriorValues(fixture);
        Assert.Equal(SetupJournalPhase.Restored, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupChangeSetState.Compensating, fixture.LoadChangeSet().State);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public async Task Apply_UnprovenJournalPartialNeverAdvancesLedgerToPartial()
    {
        var fixture = CompensationFixture.Create();
        var barrierReady = new TaskCompletionSource<SetupTestBarrier>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                fixture.Platform.SeedUserEnvironment("ENV_A", "third-party");
                barrierReady.SetResult(fixture.Platform.AddBarrier("environment.get:ENV_A"));
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        using var barrier = await barrierReady.Task;
        barrier.WaitUntilReached(CancellationToken.None);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        fixture.Platform.InjectFault(
            $"file.write-new:{NextTemporaryPath(fixture.Platform.Operations, journalPath)}",
            new IOException("PRIVATE_JOURNAL"));
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupJournalPhase.Compensating, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupChangeSetState.Compensating, fixture.LoadChangeSet().State);
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RestoredNotificationAmbiguityLeavesRestoredLedgerAndPendingEvidence(bool afterEffect)
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("FORWARD_PRIVATE"),
            () =>
            {
                if (afterEffect)
                {
                    fixture.Platform.InjectAfterEffectFault("environment.notify", new IOException("NOTIFY_PRIVATE"));
                }
                else
                {
                    fixture.Platform.InjectFault("environment.notify", new IOException("NOTIFY_PRIVATE"));
                }
            });
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));
        AssertFullyPriorValues(fixture);
    }

    private static void AssertFullyRestored(CompensationFixture fixture)
    {
        AssertFullyPriorValues(fixture);
        var ledger = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Restored, ledger.State);
        Assert.All(ledger.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Restored, journal.Phase);
    }

    private static void AssertFullyPriorValues(CompensationFixture fixture)
    {
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("old-c", fixture.Platform.ReadUserEnvironment("ENV_C"));
    }

    private static void AssertCompensatingStep(
        CompensationFixture fixture,
        Guid recordId,
        string? memberKey,
        SetupJournalStepPhase expectedPhase)
    {
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Compensating, journal.Phase);
        Assert.Equal(
            expectedPhase,
            journal.Targets.Single(target => target.RecordId == recordId).Steps
                .Single(step => string.Equals(step.MemberKey, memberKey, StringComparison.Ordinal)).Phase);
        Assert.Equal(SetupChangeSetState.Compensating, fixture.LoadChangeSet().State);
    }

    private static void AssertRecoveredAfterReopen(CompensationFixture fixture)
    {
        var recovery = fixture.RecoverAfterReopen();

        Assert.Equal(SetupRecoveryDisposition.Recovered, recovery.Disposition);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, recovery.Code);
        Assert.Equal(fixture.ChangeSetId, recovery.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, recovery.Operation);
        AssertFullyPriorValues(fixture);
        Assert.Equal(SetupJournalPhase.Restored, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
    }

    private static string InjectNextJournalWriteFault(CompensationFixture fixture, bool afterEffect)
    {
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = NextTemporaryPath(fixture.Platform.Operations, journalPath);
        if (afterEffect)
        {
            var operation = $"file.replace:{temporary}->{journalPath}";
            fixture.Platform.InjectAfterEffectFault(
                operation,
                new IOException("PRIVATE_JOURNAL"));
            return operation;
        }

        var beforeEffectOperation = $"file.write-new:{temporary}";
        fixture.Platform.InjectFault(beforeEffectOperation, new IOException("PRIVATE_JOURNAL"));
        return beforeEffectOperation;
    }

    private static string NextTemporaryPath(IReadOnlyList<string> operations, string destination)
    {
        const string marker = ".cao-00000000-0000-7000-8000-";
        var maximum = operations
            .Where(operation => operation.Contains(marker, StringComparison.Ordinal))
            .Select(operation => operation[(operation.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])
            .Select(suffix => suffix[..12])
            .Select(long.Parse)
            .DefaultIfEmpty()
            .Max();
        return destination + marker + (maximum + 1).ToString("D12") + ".tmp";
    }

    private static int LastIndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastIndexContaining(IReadOnlyList<string> values, string value)
    {
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (values[index].Contains(value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class CompensationFixture
    {
        private CompensationFixture(
            bool fileNoChange,
            bool includeEnvironment,
            bool environmentCNoChange,
            bool tagged)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000201");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000202");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000203");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            var priorBytes = tagged
                ? Encoding.UTF8.GetBytes(
                    "{\n  // " + TaggedPreviousMarker + "\n  \"unrelated\": 1,\n  \"setting\": \"old\",\n}\n")
                : Encoding.UTF8.GetBytes("old");
            PriorBytes = priorBytes.ToArray();
            var desiredBytes = tagged
                ? Encoding.UTF8.GetBytes(
                    "{\n  // " + TaggedPreviousMarker + "\n  \"unrelated\": 1,\n  \"setting\": \"new\",\n}\n")
                : Encoding.UTF8.GetBytes(fileNoChange ? "old" : "new");
            Platform.SeedFile(TargetPath, priorBytes);
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            Platform.SeedUserEnvironment("ENV_C", "old-c");
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var environmentCapture = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_C"]);
            var fileDesired = fileNoChange ? "old" : "new";
            var fileOperation = fileNoChange ? SetupOperation.NoOp : SetupOperation.Replace;
            SetupPrivateDesiredState desiredState = tagged
                ? new SetupJsoncOwnedValuesDesiredState(
                    SetupHash.File(true, desiredBytes),
                    [new SetupJsoncOwnedValue("setting", "string", fileDesired)])
                : new SetupInlineDesiredState(fileDesired);
            var planTargets = new List<SetupPrivatePlanTarget>
            {
                new(
                    FileRecordId,
                    SetupTargetKind.Json,
                    TargetPath,
                    SetupHash.File(true, priorBytes),
                    desiredState,
                    [new SetupPrivatePlanMember("setting", fileOperation, fileDesired)]),
            };
            if (includeEnvironment)
            {
                planTargets.Add(new SetupPrivatePlanTarget(
                    EnvironmentRecordId,
                    SetupTargetKind.Env,
                    "current-user",
                    environmentCapture.AggregateHash,
                    new SetupInlineDesiredState("environment-allowlist"),
                    [
                        new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a"),
                        new SetupPrivatePlanMember("ENV_B", SetupOperation.Remove, null),
                        new SetupPrivatePlanMember(
                            "ENV_C",
                            environmentCNoChange ? SetupOperation.NoOp : SetupOperation.Replace,
                            environmentCNoChange ? "old-c" : "desired-c"),
                    ]));
            }

            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                planTargets);
            var fileProjection = CreateStatusProjection([new SetupLedgerMember("setting", fileOperation)]);
            if (tagged)
            {
                fileProjection = fileProjection with
                {
                    ExpectedResult = SourceCapabilityManifestLoader
                        .LoadForSurface("github-copilot-vscode")
                        .CanonicalJson,
                };
            }

            var ledgerTargets = new List<SetupLedgerTarget>
            {
                new(
                    FileRecordId,
                    SetupTargetKind.Json,
                    tagged ? "vscode-stable-default-user-settings" : "settings",
                    "github-copilot",
                    [new SetupLedgerMember("setting", fileOperation)],
                    plan.Targets[0].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    fileProjection,
                    "1.0.0"),
            };
            if (includeEnvironment)
            {
                ledgerTargets.Add(new SetupLedgerTarget(
                    EnvironmentRecordId,
                    SetupTargetKind.Env,
                    "copilot-cli-user-environment",
                    "github-copilot",
                    [
                        new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                        new SetupLedgerMember("ENV_B", SetupOperation.Remove),
                        new SetupLedgerMember(
                            "ENV_C",
                            environmentCNoChange ? SetupOperation.NoOp : SetupOperation.Replace),
                    ],
                    plan.Targets[1].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    CreateStatusProjection(
                    [
                        new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                        new SetupLedgerMember("ENV_B", SetupOperation.Remove),
                        new SetupLedgerMember("ENV_C", environmentCNoChange ? SetupOperation.NoOp : SetupOperation.Replace),
                    ]),
                    "1.0.0"));
            }

            var ledgerChangeSet = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                ledgerTargets);
            PlanStore = new SetupPlanStore(Platform, Paths);
            LedgerStore = new SetupLedgerStore(Platform, Paths, PlanStore);
            JournalStore = new SetupTransactionJournalStore(Platform, Paths);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            LedgerStore.PersistPlannedChangeSet(setupLock.Lock!, plan, ledgerChangeSet);
            MaterializingRevalidator = tagged
                ? new RecordingMaterializingRevalidator(FileRecordId, desiredBytes)
                : null;
            Coordinator = new SetupApplyCoordinator(
                Platform,
                Paths,
                PlanStore,
                LedgerStore,
                JournalStore,
                (ISetupApplyRevalidator?)MaterializingRevalidator ?? new NoOpRevalidator());
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid FileRecordId { get; }
        public Guid EnvironmentRecordId { get; }
        public string TargetPath { get; }
        public byte[] PriorBytes { get; }
        public SetupPlanStore PlanStore { get; }
        public SetupLedgerStore LedgerStore { get; }
        public SetupTransactionJournalStore JournalStore { get; }
        public SetupApplyCoordinator Coordinator { get; }
        public RecordingMaterializingRevalidator? MaterializingRevalidator { get; }

        public static CompensationFixture Create(
            bool fileNoChange = false,
            bool includeEnvironment = true,
            bool environmentCNoChange = false,
            bool tagged = false) =>
            new(fileNoChange, includeEnvironment, environmentCNoChange, tagged);

        public SetupLedgerChangeSet LoadChangeSet() =>
            LedgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);

        public SetupRecoveryResult RecoverAfterReopen()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return new SetupRecoveryCoordinator(Platform, Paths, PlanStore, LedgerStore, JournalStore)
                .RecoverNext(acquisition.Lock!);
        }

        public void SeedTaggedCurrentState(string currentState)
        {
            var bytes = currentState switch
            {
                "prior" => PriorBytes,
                "desired" => MaterializingRevalidator!.DesiredBytes,
                "third-party" => Encoding.UTF8.GetBytes("third-party"),
                _ => throw new ArgumentOutOfRangeException(nameof(currentState)),
            };
            Platform.SeedFile(TargetPath, bytes);
        }

        public void AssertTaggedMarkerAbsentFromSafeEvidence(
            string resultEvidence,
            string? errorEvidence)
        {
            var safeEvidence = new List<string>
            {
                Encoding.UTF8.GetString(Platform.ReadSeededFile(Paths.GetPlan(ChangeSetId))),
                Encoding.UTF8.GetString(Platform.ReadSeededFile(Paths.OwnershipLedger)),
                string.Join("\n", Platform.Operations),
                resultEvidence,
                errorEvidence ?? string.Empty,
                File.ReadAllText(Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "Setup",
                    "v1",
                    "private-plan.v1.json")),
                File.ReadAllText(Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "Setup",
                    "v1",
                    "ownership-ledger.v1.json")),
            };
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            if (Platform.FileSystem.FileExists(journalPath))
            {
                safeEvidence.Add(Encoding.UTF8.GetString(Platform.ReadSeededFile(journalPath)));
            }

            Assert.All(safeEvidence, evidence =>
                Assert.DoesNotContain(TaggedPreviousMarker, evidence, StringComparison.Ordinal));
        }
    }

    private sealed class NoOpRevalidator : ISetupApplyRevalidator
    {
        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }

    private sealed class RecordingMaterializingRevalidator(Guid recordId, byte[] desiredBytes)
        : ISetupApplyRevalidator
    {
        private readonly byte[] desiredBytes = desiredBytes.ToArray();

        public int Calls { get; private set; }

        public byte[] DesiredBytes => desiredBytes.ToArray();

        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            Calls++;
            return SetupPlanResult.Revalidated(
            [
                new SetupMaterializedTarget(
                    recordId,
                    desiredBytes,
                    SetupHash.File(true, desiredBytes)),
            ]);
        }
    }

    private sealed class TrackingExclusiveLock : ISetupExclusiveFileLock
    {
        private int disposed;

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }
}

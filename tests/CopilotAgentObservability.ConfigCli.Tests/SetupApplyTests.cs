using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupApplyTests
{
    [Fact]
    public async Task Apply_SameTokenConcurrentCommandWaitsForTheActiveCommandToExit()
    {
        var fixture = ApplyFixture.Create();
        using var mutation = fixture.Platform.AddBarrier("environment.set:ENV_A");
        using var secondStarted = new ManualResetEventSlim();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var first = Task.Run(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        mutation.WaitUntilReached(CancellationToken.None);
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return Assert.Throws<SetupApplyException>(
                () => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.IsCompleted);

        mutation.Release();
        var applied = await first;
        var rejected = await second;

        Assert.Equal(SetupCodes.ApplySucceeded, applied.OutcomeCode);
        Assert.Equal(SetupCodes.InvalidArguments, rejected.Code);
        Assert.Equal(1, fixture.Revalidator.Calls);
    }

    [Fact]
    public void Apply_ForeignPlatformLockRejectsBeforeRevalidationOrArtifacts()
    {
        var fixture = ApplyFixture.Create();
        var foreignPlatform = new SetupTestPlatform(
            fixture.Platform.Clock.UtcNow,
            fixture.Platform.LocalApplicationData,
            fixture.Platform.PathStyle);
        using var foreignLock = SetupLock.CreateForTesting(
            new TrackingExclusiveLock(),
            foreignPlatform,
            new SetupRuntimePaths(foreignPlatform));

        var exception = Assert.Throws<SetupStorageException>(() =>
            fixture.Coordinator.Apply(foreignLock, fixture.ChangeSetId));

        Assert.Equal(SetupStorageCodes.LockRequired, exception.Code);
        Assert.Equal(0, fixture.Revalidator.Calls);
        AssertNoTransactionArtifacts(fixture);
    }

    [Fact]
    public async Task Apply_DisposeRequestedAtEnvironmentWriteKeepsExclusiveLockUntilRecoveryEvidenceIsReturned()
    {
        var fixture = ApplyFixture.Create();
        using var mutation = fixture.Platform.AddBarrier("environment.set:ENV_A");
        using var disposeRequested = new ManualResetEventSlim();
        var exclusive = new TrackingExclusiveLock();
        var setupLock = SetupLock.CreateForTesting(exclusive, fixture.Platform, fixture.Paths, disposeRequested.Set);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(setupLock, fixture.ChangeSetId)));
        mutation.WaitUntilReached(CancellationToken.None);

        var disposing = Task.Run(setupLock.Dispose);
        Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(exclusive.IsDisposed);
        Assert.Null(exclusive.TryAcquire());
        mutation.Release();

        var exception = await applying;
        await disposing;

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.True(exclusive.IsDisposed);
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet().State);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Applying, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, journal.Targets[1].Steps[0].Phase);
        using var reacquired = exclusive.TryAcquire();
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task Apply_DisposeRequestedAtFileReplaceKeepsExclusiveLockUntilRecoveryEvidenceIsReturned()
    {
        var fixture = ApplyFixture.Create(includeEnvironment: false);
        using var beforeMutation = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterMutationIntentBeforeMutation}");
        using var disposeRequested = new ManualResetEventSlim();
        var exclusive = new TrackingExclusiveLock();
        var setupLock = SetupLock.CreateForTesting(exclusive, fixture.Platform, fixture.Paths, disposeRequested.Set);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(setupLock, fixture.ChangeSetId)));
        beforeMutation.WaitUntilReached(CancellationToken.None);
        var temporary = NextTemporaryPath(fixture.Platform.Operations, fixture.TargetPath);
        using var replace = fixture.Platform.AddBarrier($"file.replace:{temporary}->{fixture.TargetPath}");
        beforeMutation.Release();
        replace.WaitUntilReached(CancellationToken.None);

        var disposing = Task.Run(setupLock.Dispose);
        Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(exclusive.IsDisposed);
        replace.Release();

        var exception = await applying;
        await disposing;

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.True(exclusive.IsDisposed);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupChangeSetState.Applying, fixture.LoadChangeSet().State);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Applying, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, journal.Targets[0].Steps[0].Phase);
    }

    [Fact]
    public async Task Apply_DisposeRequestedAtNotificationKeepsCommittedPendingRecoveryEvidence()
    {
        var fixture = ApplyFixture.Create();
        using var notification = fixture.Platform.AddBarrier("environment.notify");
        using var disposeRequested = new ManualResetEventSlim();
        var exclusive = new TrackingExclusiveLock();
        var setupLock = SetupLock.CreateForTesting(exclusive, fixture.Platform, fixture.Paths, disposeRequested.Set);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(setupLock, fixture.ChangeSetId)));
        notification.WaitUntilReached(CancellationToken.None);

        var disposing = Task.Run(setupLock.Dispose);
        Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(exclusive.IsDisposed);
        notification.Release();

        var exception = await applying;
        await disposing;

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.True(exclusive.IsDisposed);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
    }

    [Fact]
    public void Apply_RequiresLiveLockBeforeRevalidationOrArtifacts()
    {
        var fixture = ApplyFixture.Create();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var releasedLock = acquisition.Lock!;
        acquisition.Dispose();

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Coordinator.Apply(releasedLock, fixture.ChangeSetId));

        Assert.Equal(SetupStorageCodes.LockRequired, exception.Code);
        Assert.Equal(0, fixture.Revalidator.Calls);
        AssertNoTransactionArtifacts(fixture);
    }

    [Fact]
    public void Apply_RunsAllAdapterRevalidationBeforeCaptureAndLeavesNoArtifactsOnRejection()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) =>
        {
            AssertNoTransactionArtifacts(fixture);
            fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("changed"));
            throw new SetupApplyException(SetupCodes.UnsupportedVersion);
        };
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.UnsupportedVersion, exception.Code);
        Assert.Equal(1, fixture.Revalidator.Calls);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Apply_RejectsChangedBaseAfterAdapterRevalidationBeforeCreatingArtifacts()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) =>
            fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("changed"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Apply_RejectsChangedEnvironmentAllowlistAfterAdapterRevalidationBeforeCreatingArtifacts()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) => fixture.Platform.SeedUserEnvironment("ENV_A", "changed");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Theory]
    [InlineData(SetupFaultPoint.AfterJournalPreparedBeforeLedger, SetupChangeSetState.Restored, "Restored", "Pending")]
    [InlineData(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent, SetupChangeSetState.Restored, "Restored", "Pending")]
    [InlineData(SetupFaultPoint.AfterMutationIntentBeforeMutation, SetupChangeSetState.Restored, "Restored", "RestoreCompleted")]
    [InlineData(SetupFaultPoint.AfterMutationBeforeCompletion, SetupChangeSetState.Restored, "Restored", "RestoreCompleted")]
    [InlineData(SetupFaultPoint.AfterCompletionBeforeCommit, SetupChangeSetState.Restored, "Restored", "RestoreCompleted")]
    [InlineData(SetupFaultPoint.AfterCommitBeforeLedger, SetupChangeSetState.Applying, "Committed", "MutationCompleted")]
    public void Apply_FaultsCompensateBeforeCommitAndLeaveCommittedRecoveryEvidenceAfterCommit(
        string faultPoint,
        SetupChangeSetState expectedLedgerState,
        string expectedJournalPhase,
        string expectedFirstStepPhase)
    {
        var fixture = ApplyFixture.Create();
        fixture.Platform.InjectFault($"checkpoint:{faultPoint}", new IOException("private raw detail"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(expectedLedgerState, fixture.LoadChangeSet().State);
        var reopenedJournal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(expectedJournalPhase, reopenedJournal.Phase.ToString());
        Assert.Equal(expectedFirstStepPhase, reopenedJournal.Targets[0].Steps[0].Phase.ToString());
        Assert.DoesNotContain("private raw detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PersistsOneFileIntentAndOneIntentPerEnvironmentMemberInPlanOrder()
    {
        var fixture = ApplyFixture.Create();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal([fixture.FileRecordId, fixture.EnvironmentRecordId], journal.Targets.Select(target => target.RecordId));
        Assert.Single(journal.Targets[0].Steps);
        Assert.Null(journal.Targets[0].Steps[0].MemberKey);
        Assert.Equal(["ENV_A", "ENV_B"], journal.Targets[1].Steps.Select(step => step.MemberKey));
        Assert.All(journal.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.MutationCompleted, step.Phase));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.True(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId)));
        Assert.True(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId)));
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));
        Assert.True(IndexOf(fixture.Platform.Operations, $"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}", 0)
            < IndexOf(fixture.Platform.Operations, "environment.set:ENV_A", 0));
        Assert.True(IndexOf(fixture.Platform.Operations, "environment.set:ENV_A", 0)
            < IndexOf(fixture.Platform.Operations, "environment.set:ENV_B", 0));
    }

    [Fact]
    public void Apply_VerifiesEveryDesiredStateThenCommitsJournalBeforeAppliedLedgerAndNotifiesLast()
    {
        var fixture = ApplyFixture.Create();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applied = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupChangeSetState.Applied, applied.State);
        Assert.Equal(SetupCodes.ApplySucceeded, applied.OutcomeCode);
        Assert.All(applied.Targets, target =>
        {
            Assert.NotNull(target.AppliedStateHash);
            Assert.NotNull(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Pending, target.RollbackStatus);
        });
        Assert.Equal(SetupJournalPhase.Committed,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!.EnvironmentNotification);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));

        var operations = fixture.Platform.Operations;
        var commitCheckpoint = IndexOf(operations, $"checkpoint:{SetupFaultPoint.AfterCommitBeforeLedger}", 0);
        var notify = IndexOf(operations, "environment.notify", 0);
        var firstBackupWrite = operations
            .Select((operation, index) => (operation, index))
            .First(item => item.operation.StartsWith($"file.write-new:{fixture.Paths.Backups}", StringComparison.Ordinal)).index;
        Assert.True(operations.Take(firstBackupWrite).Count(operation => operation == "environment.get:ENV_B") >= 2);
        Assert.True(commitCheckpoint >= 0 && commitCheckpoint < notify);
        Assert.DoesNotContain(operations.Skip(notify + 1), operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_FirstEnvironmentIntentDurablyMarksNotificationPendingBeforeApiWrite()
    {
        var fixture = ApplyFixture.Create();
        using var barrier = fixture.Platform.AddBarrier("environment.set:ENV_A");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        barrier.WaitUntilReached(CancellationToken.None);
        var journalAtApiBoundary = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupEnvironmentNotification.Pending, journalAtApiBoundary.EnvironmentNotification);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, journalAtApiBoundary.Targets[1].Steps[0].Phase);
        barrier.Release();

        var result = await applying;
        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
    }

    [Fact]
    public async Task Apply_FinalDesiredRacePreservesThirdPartyStateAndRecordsPartialCompensation()
    {
        var fixture = ApplyFixture.Create();
        using var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party"));
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
        Assert.Equal("third-party", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
    }

    [Fact]
    public async Task Apply_ImmediatePreWriteRaceIsPreservedAsPartialWithoutLaterWrites()
    {
        var fixture = ApplyFixture.Create();
        using var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party"));
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("third-party", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, journal.Targets[0].Steps[0].Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations, operation => operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_NotificationFailureLeavesAppliedLedgerAndPendingJournalForRecovery(bool afterEffect)
    {
        var fixture = ApplyFixture.Create();
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault("environment.notify", new IOException("private notification detail"));
        }
        else
        {
            fixture.Platform.InjectFault("environment.notify", new IOException("private notification detail"));
        }
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("private notification detail", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, journal.EnvironmentNotification);
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedVersion)]
    [InlineData(SetupCodes.ManagedPolicyConflict)]
    [InlineData(SetupCodes.PortOwnedByForeignProcess)]
    [InlineData(SetupCodes.UnsafePath)]
    [InlineData(SetupCodes.TargetNotInstalled)]
    [InlineData(SetupCodes.MalformedSettings)]
    [InlineData(SetupCodes.PermissionDenied)]
    [InlineData(SetupCodes.StalePlan)]
    [InlineData(SetupCodes.InternalError)]
    public void Apply_AllAdapterPreflightFailuresAreFixedAndArtifactFree(string code)
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) => throw new SetupApplyException(code);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(code, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Apply_MapsNonContractRevalidatorFailureWithoutEchoingIt()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) => throw new SetupApplyException("private-path-and-value");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.DoesNotContain("private-path-and-value", exception.Message, StringComparison.Ordinal);
        AssertNoTransactionArtifacts(fixture);
    }

    [Fact]
    public void Constructor_RejectsMissingRealRevalidator()
    {
        var fixture = ApplyFixture.Create();
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);

        Assert.Throws<ArgumentNullException>(() => new SetupApplyCoordinator(
            fixture.Platform,
            fixture.Paths,
            planStore,
            new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore),
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths),
            null!));
    }

    [Fact]
    public void Apply_RejectsImmutablePlanLedgerMismatchBeforeRevalidationOrArtifacts()
    {
        var fixture = ApplyFixture.Create();
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var ledger = ledgerStore.Load();
        var changedTarget = ledger.ChangeSets[0].Targets[0] with
        {
            Members = [new SetupLedgerMember("setting", SetupOperation.Remove)],
        };
        ledgerStore.Save(acquisition.Lock!, ledger with
        {
            ChangeSets = [ledger.ChangeSets[0] with { Targets = [changedTarget, ledger.ChangeSets[0].Targets[1]] }],
        });

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(0, fixture.Revalidator.Calls);
        AssertNoTransactionArtifacts(fixture);
    }

    [Fact]
    public void Apply_AllDesiredTargetsAlreadyCurrentPersistsNoChangesWithoutTransactionArtifacts()
    {
        var fixture = ApplyFixture.Create(noChanges: true);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupChangeSetState.NoChanges, result.State);
        Assert.Equal(SetupCodes.NoChanges, result.OutcomeCode);
        Assert.All(result.Targets, target =>
        {
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        AssertNoTransactionArtifacts(fixture);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_MixedTransactionDoesNotBackUpJournalOrClaimOwnershipForNoOpTarget()
    {
        var fixture = ApplyFixture.Create(fileNoChange: true);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        var file = result.Targets.Single(target => target.RecordId == fixture.FileRecordId);
        Assert.Null(file.BackupReference);
        Assert.Null(file.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, file.RollbackStatus);
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId)));
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal([fixture.EnvironmentRecordId], journal.Targets.Select(target => target.RecordId));
    }

    [Fact]
    public void Apply_MixedEnvironmentTargetJournalsAndWritesOnlyActuallyChangedMembers()
    {
        var fixture = ApplyFixture.Create(environmentANoChange: true);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        var environmentJournal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths)
            .Load(fixture.ChangeSetId)!.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(["ENV_B"], environmentJournal.Steps.Select(step => step.MemberKey));
        Assert.Equal(
            ["ENV_A", "ENV_B"],
            result.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId).Members.Select(member => member.SettingKey));
        var environmentStep = new UserEnvironmentSetupStep(fixture.Platform);
        var backup = environmentStep.ReadBackup(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId),
            ["ENV_A", "ENV_B"]);
        Assert.Equal(["ENV_A", "ENV_B"], backup.Members.Select(member => member.Name));
        var environmentLedger = result.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(backup.AggregateHash, environmentLedger.PreviousStateHash);
        var fullApplied = environmentStep.Capture(["ENV_A", "ENV_B"]);
        var changedSubset = environmentStep.Capture(["ENV_B"]);
        Assert.Equal(fullApplied.AggregateHash, environmentLedger.AppliedStateHash);
        Assert.NotEqual(changedSubset.AggregateHash, environmentLedger.AppliedStateHash);
        Assert.DoesNotContain("environment.set:ENV_A", fixture.Platform.Operations);
        Assert.Contains("environment.set:ENV_B", fixture.Platform.Operations);
        Assert.Equal("untouched", fixture.Platform.ReadUserEnvironment("UNRELATED"));
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));
    }

    [Fact]
    public async Task Apply_NoOpEnvironmentMemberExternalEditFailsFullAllowlistCommitGuard()
    {
        var fixture = ApplyFixture.Create(fileNoChange: true, environmentANoChange: true);
        using var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterMutationBeforeCompletion}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(
            () => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedUserEnvironment("ENV_A", "third-party");
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(["ENV_B"], journal.Targets.Single().Steps.Select(step => step.MemberKey));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_NoOpEnvironmentTargetInMixedTransactionDoesNotNotifyOrClaimOwnership()
    {
        var fixture = ApplyFixture.Create(environmentNoChange: true);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        var environment = result.Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Null(environment.BackupReference);
        Assert.Null(environment.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environment.RollbackStatus);
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId)));
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal([fixture.FileRecordId], journal.Targets.Select(target => target.RecordId));
        Assert.Equal(SetupEnvironmentNotification.NotRequired, journal.EnvironmentNotification);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_FileOnlySuccessNeverMakesEnvironmentNotificationRequired()
    {
        var fixture = ApplyFixture.Create(includeEnvironment: false);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupEnvironmentNotification.NotRequired, journal.EnvironmentNotification);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_RealCapturePathFailureBeforeArtifactsPreservesSafeCode()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) => fixture.Platform.SeedPathMetadata(
            fixture.TargetPath,
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.UnsafePath, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    [Theory]
    [InlineData(SetupCodes.ApplySucceeded)]
    [InlineData(SetupCodes.PlanReady)]
    [InlineData(SetupCodes.NoChanges)]
    [InlineData(SetupCodes.RollbackSucceeded)]
    [InlineData(SetupCodes.StatusReady)]
    [InlineData(SetupCodes.InterruptedApplyRecovered)]
    [InlineData(SetupCodes.InterruptedRollbackRecovered)]
    [InlineData(SetupCodes.InvalidArguments)]
    [InlineData(SetupCodes.UnsupportedAdapter)]
    [InlineData(SetupCodes.UnsupportedTarget)]
    [InlineData(SetupCodes.RollbackStale)]
    [InlineData(SetupCodes.RollbackNotAvailable)]
    [InlineData(SetupCodes.PartialApply)]
    [InlineData(SetupCodes.PartialRollback)]
    [InlineData(SetupCodes.SetupBusy)]
    [InlineData(SetupCodes.RecoveryRequired)]
    [InlineData(SetupCodes.InterruptedRecoveryFailed)]
    [InlineData(SetupCodes.LedgerCorrupt)]
    [InlineData(SetupCodes.LedgerVersionUnsupported)]
    public void Apply_MapsKnownNonPreflightRevalidatorCodesToInternalError(string wrongCode)
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) => throw new SetupApplyException(wrongCode);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        AssertNoTransactionArtifacts(fixture);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value, int start)
    {
        for (var index = start; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static void AssertNoTransactionArtifacts(ApplyFixture fixture)
    {
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId)));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId)));
    }

    private sealed class ApplyFixture
    {
        private ApplyFixture(
            bool noChanges,
            bool fileNoChange,
            bool environmentANoChange,
            bool environmentNoChange,
            bool includeEnvironment)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000101");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000102");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000103");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            Platform.SeedUserEnvironment("UNRELATED", "untouched");
            var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(["ENV_A", "ENV_B"]);
            var fileDesired = noChanges || fileNoChange ? "old" : "new";
            var fileOperation = noChanges || fileNoChange ? SetupOperation.NoOp : SetupOperation.Replace;
            var environmentAOperation = noChanges || environmentANoChange || environmentNoChange ? SetupOperation.NoOp : SetupOperation.Replace;
            var environmentBOperation = noChanges || environmentNoChange ? SetupOperation.NoOp : SetupOperation.Remove;
            var environmentBDesired = noChanges || environmentNoChange ? "old-b" : null;
            var planTargets = new List<SetupPrivatePlanTarget>
            {
                new(FileRecordId, SetupTargetKind.Json, TargetPath,
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old")), fileDesired,
                    [new SetupPrivatePlanMember("setting", fileOperation, fileDesired)]),
            };
            if (includeEnvironment)
            {
                planTargets.Add(new SetupPrivatePlanTarget(EnvironmentRecordId, SetupTargetKind.Env, "current-user",
                    environmentCapture.AggregateHash, "environment-allowlist",
                    [new SetupPrivatePlanMember("ENV_A", environmentAOperation, noChanges || environmentANoChange || environmentNoChange ? "old-a" : "desired-a"),
                     new SetupPrivatePlanMember("ENV_B", environmentBOperation, environmentBDesired)]));
            }

            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                planTargets);
            var ledgerTargets = new List<SetupLedgerTarget>
            {
                new(FileRecordId, SetupTargetKind.Json, "settings", "github-copilot",
                    [new SetupLedgerMember("setting", fileOperation)], plan.Targets[0].BaseStateHash,
                    null, null, null, SetupLedgerRollbackStatus.NotAvailable, SetupRestartRequirement.RestartVsCode, "1.0.0"),
            };
            if (includeEnvironment)
            {
                ledgerTargets.Add(new SetupLedgerTarget(EnvironmentRecordId, SetupTargetKind.Env, "user-environment", "github-copilot",
                    [new SetupLedgerMember("ENV_A", environmentAOperation), new SetupLedgerMember("ENV_B", environmentBOperation)],
                    plan.Targets[1].BaseStateHash, null, null, null, SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession, "1.0.0"));
            }
            var ledgerChangeSet = new SetupLedgerChangeSet(
                ChangeSetId, "github-copilot", "vscode", Platform.Clock.UtcNow, Platform.Clock.UtcNow,
                "1.0.0", null, SetupChangeSetState.Planned,
                ledgerTargets);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.PersistPlannedChangeSet(setupLock.Lock!, plan, ledgerChangeSet);
            Revalidator = new RecordingRevalidator();
            Coordinator = new SetupApplyCoordinator(
                Platform, Paths, planStore, ledgerStore,
                new SetupTransactionJournalStore(Platform, Paths), Revalidator);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid FileRecordId { get; }
        public Guid EnvironmentRecordId { get; }
        public string TargetPath { get; }
        public RecordingRevalidator Revalidator { get; }
        public SetupApplyCoordinator Coordinator { get; }

        public static ApplyFixture Create(
            bool noChanges = false,
            bool fileNoChange = false,
            bool environmentANoChange = false,
            bool environmentNoChange = false,
            bool includeEnvironment = true) => new(
                noChanges,
                fileNoChange,
                environmentANoChange,
                environmentNoChange,
                includeEnvironment);

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
        }
    }

    private sealed class RecordingRevalidator : ISetupApplyRevalidator
    {
        public int Calls { get; private set; }

        public Action<SetupPrivatePlan, SetupLedgerChangeSet>? OnRevalidate { get; set; }

        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
            Calls++;
            OnRevalidate?.Invoke(plan, plannedChangeSet);
        }
    }

    private sealed class TrackingExclusiveLock : ISetupExclusiveFileLock
    {
        private int held = 1;

        public bool IsDisposed => Volatile.Read(ref held) == 0;

        public ISetupExclusiveFileLock? TryAcquire() =>
            Interlocked.CompareExchange(ref held, 1, 0) == 0 ? this : null;

        public void Dispose() => Interlocked.Exchange(ref held, 0);
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
}

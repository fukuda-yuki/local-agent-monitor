using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupCompensationTests
{
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
    public void Apply_ThirdPartyValueBeforeClassificationIsPreservedAndStopsEarlierRestores()
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault(
            "environment.set:ENV_B",
            new IOException("PRIVATE_VALUE"),
            () => fixture.Platform.SeedUserEnvironment("ENV_A", "third-party"));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var journal = fixture.JournalStore.Load(fixture.ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Partial, journal.Phase);
        Assert.Equal(SetupJournalStepPhase.MutationCompleted, journal.Targets[0].Steps[0].Phase);
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_RestorePrimitiveBeforeEffectFailureLeavesDesiredStateAndStops()
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
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.InternalError, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Failed, environment.RollbackStatus);
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
        Assert.Equal(2, fixture.Platform.Operations.Count(operation => operation == "environment.set:ENV_A"));
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
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var environment = fixture.LoadChangeSet().Targets.Single(target => target.RecordId == fixture.EnvironmentRecordId);
        Assert.Equal(SetupCodes.RollbackStale, environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, environment.RollbackStatus);
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
            Assert.Equal(SetupJournalPhase.Restored, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
            Assert.Equal(SetupChangeSetState.Restored, fixture.LoadChangeSet().State);
        }
        else
        {
            Assert.Equal(SetupCodes.PartialApply, exception.Code);
            Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
            Assert.Equal(SetupJournalPhase.Partial, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
            Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
        }

        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RestoreCompletionWriteFaultStopsWithoutRestoringEarlierSteps(bool afterEffect)
    {
        var fixture = CompensationFixture.Create();
        fixture.Platform.InjectAfterEffectFault("environment.set:ENV_B", new IOException("FORWARD_PRIVATE"));
        using var barrier = fixture.Platform.AddBarrier($"checkpoint:{SetupFaultPoint.AfterRestoreBeforeCompletion}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var temporary = NextTemporaryPath(fixture.Platform.Operations, journalPath);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                $"file.replace:{temporary}->{journalPath}",
                new IOException("PRIVATE_JOURNAL"));
        }
        else
        {
            fixture.Platform.InjectFault($"file.write-new:{temporary}", new IOException("PRIVATE_JOURNAL"));
        }
        barrier.Release();

        var exception = await applying;
        Assert.Equal(SetupCodes.PartialApply, exception.Code);
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(SetupJournalPhase.Partial, fixture.JournalStore.Load(fixture.ChangeSetId)!.Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);
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
        private CompensationFixture(bool fileNoChange, bool includeEnvironment)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000201");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000202");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000203");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            Platform.SeedUserEnvironment("ENV_A", "old-a");
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            Platform.SeedUserEnvironment("ENV_C", "old-c");
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var environmentCapture = environmentStep.Capture(["ENV_A", "ENV_B", "ENV_C"]);
            var fileDesired = fileNoChange ? "old" : "new";
            var fileOperation = fileNoChange ? SetupOperation.NoOp : SetupOperation.Replace;
            var planTargets = new List<SetupPrivatePlanTarget>
            {
                new(
                    FileRecordId,
                    SetupTargetKind.Json,
                    TargetPath,
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old")),
                    fileDesired,
                    [new SetupPrivatePlanMember("setting", fileOperation, fileDesired)]),
            };
            if (includeEnvironment)
            {
                planTargets.Add(new SetupPrivatePlanTarget(
                    EnvironmentRecordId,
                    SetupTargetKind.Env,
                    "current-user",
                    environmentCapture.AggregateHash,
                    "environment-allowlist",
                    [
                        new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "desired-a"),
                        new SetupPrivatePlanMember("ENV_B", SetupOperation.Remove, null),
                        new SetupPrivatePlanMember("ENV_C", SetupOperation.Replace, "desired-c"),
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
            var ledgerTargets = new List<SetupLedgerTarget>
            {
                new(
                    FileRecordId,
                    SetupTargetKind.Json,
                    "settings",
                    "github-copilot",
                    [new SetupLedgerMember("setting", fileOperation)],
                    plan.Targets[0].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    "1.0.0"),
            };
            if (includeEnvironment)
            {
                ledgerTargets.Add(new SetupLedgerTarget(
                    EnvironmentRecordId,
                    SetupTargetKind.Env,
                    "user-environment",
                    "github-copilot",
                    [
                        new SetupLedgerMember("ENV_A", SetupOperation.Replace),
                        new SetupLedgerMember("ENV_B", SetupOperation.Remove),
                        new SetupLedgerMember("ENV_C", SetupOperation.Replace),
                    ],
                    plan.Targets[1].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
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
            Coordinator = new SetupApplyCoordinator(
                Platform,
                Paths,
                PlanStore,
                LedgerStore,
                JournalStore,
                new NoOpRevalidator());
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public Guid FileRecordId { get; }
        public Guid EnvironmentRecordId { get; }
        public string TargetPath { get; }
        public SetupPlanStore PlanStore { get; }
        public SetupLedgerStore LedgerStore { get; }
        public SetupTransactionJournalStore JournalStore { get; }
        public SetupApplyCoordinator Coordinator { get; }

        public static CompensationFixture Create(bool fileNoChange = false, bool includeEnvironment = true) =>
            new(fileNoChange, includeEnvironment);

        public SetupLedgerChangeSet LoadChangeSet() =>
            LedgerStore.Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
    }

    private sealed class NoOpRevalidator : ISetupApplyRevalidator
    {
        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
        }
    }

    private sealed class TrackingExclusiveLock : ISetupExclusiveFileLock
    {
        private int disposed;

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }
}

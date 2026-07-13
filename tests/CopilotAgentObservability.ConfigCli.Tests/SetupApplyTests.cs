using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupApplyTests
{
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
    public void Apply_AdapterOperationMismatchRequiresRecoveryBeforeArtifactsOrWrites()
    {
        var fixture = ApplyFixture.Create();
        fixture.Revalidator.OnRevalidate = (_, _) =>
            throw new SetupApplyException(SetupCodes.RecoveryRequired);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(1, fixture.Revalidator.Calls);
        AssertApplyInputsUnchanged(fixture);
        AssertNoTransactionArtifacts(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
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
    [InlineData(true, "old", SetupOperation.NoOp, "desired")]
    [InlineData(true, "old", SetupOperation.Add, "desired")]
    [InlineData(true, "old", SetupOperation.Add, "old")]
    [InlineData(false, null, SetupOperation.Replace, "desired")]
    [InlineData(true, "old", SetupOperation.Replace, "old")]
    [InlineData(false, null, SetupOperation.Remove, null)]
    [InlineData(true, "old", SetupOperation.Mixed, "desired")]
    public void Apply_RejectsEnvironmentOperationThatDoesNotDescribeCapturedToDesiredTransitionBeforeArtifacts(
        bool priorExists,
        string? priorValue,
        SetupOperation operation,
        string? desiredValue)
    {
        var fixture = ApplyFixture.Create(environmentOperationCase:
            new EnvironmentOperationCase(priorExists, priorValue, operation, desiredValue));
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(1, fixture.Revalidator.Calls);
        AssertNoTransactionArtifacts(fixture);
        var planned = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Planned, planned.State);
        Assert.All(planned.Targets, target =>
        {
            Assert.Null(target.BackupReference);
            Assert.Null(target.AppliedStateHash);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData(true, "same", SetupOperation.NoOp, "same", SetupCodes.NoChanges)]
    [InlineData(false, null, SetupOperation.Add, "desired", SetupCodes.ApplySucceeded)]
    [InlineData(true, "old", SetupOperation.Replace, "desired", SetupCodes.ApplySucceeded)]
    [InlineData(true, "old", SetupOperation.Remove, null, SetupCodes.ApplySucceeded)]
    [InlineData(true, "", SetupOperation.NoOp, "", SetupCodes.NoChanges)]
    [InlineData(false, null, SetupOperation.Add, "", SetupCodes.ApplySucceeded)]
    [InlineData(true, "", SetupOperation.Replace, "desired", SetupCodes.ApplySucceeded)]
    [InlineData(true, "", SetupOperation.Remove, null, SetupCodes.ApplySucceeded)]
    public void Apply_AcceptsExactEnvironmentOperationTransitionIncludingMissingAndEmpty(
        bool priorExists,
        string? priorValue,
        SetupOperation operation,
        string? desiredValue,
        string expectedCode)
    {
        var fixture = ApplyFixture.Create(environmentOperationCase:
            new EnvironmentOperationCase(priorExists, priorValue, operation, desiredValue));
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(expectedCode, result.OutcomeCode);
        if (operation == SetupOperation.NoOp)
        {
            AssertNoTransactionArtifacts(fixture);
            Assert.DoesNotContain("environment.set:ENV_A", fixture.Platform.Operations);
            Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
        }
        else
        {
            var journal = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
            Assert.Equal(["ENV_A"], Assert.Single(journal.Targets).Steps.Select(step => step.MemberKey));
            Assert.Equal(desiredValue, fixture.Platform.ReadUserEnvironment("ENV_A"));
            Assert.Equal(1, fixture.Platform.Operations.Count(item => item == "environment.notify"));
        }
    }

    [Fact]
    public void Apply_MissingToMissingNoOpEnvironmentOnlyPersistsNoChangesWithoutArtifacts()
    {
        var fixture = CanonicalEnvironmentApplyFixture.Create(includeChangedFile: false, includeChangedEnvironment: false);

        var result = fixture.Apply();

        Assert.Equal(SetupCodes.NoChanges, result.OutcomeCode);
        Assert.Equal(SetupChangeSetState.NoChanges, result.State);
        var environment = Assert.Single(result.Targets);
        Assert.Equal(SetupTargetKind.Env, environment.TargetKind);
        Assert.Equal(SetupOperation.NoOp, Assert.Single(environment.Members).Operation);
        Assert.Null(environment.BackupReference);
        Assert.Null(environment.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environment.RollbackStatus);
        fixture.AssertNoTransactionArtifacts();
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_ChangedFileWithMissingEnvironmentNoOpMutatesAndOwnsOnlyFile()
    {
        var fixture = CanonicalEnvironmentApplyFixture.Create(includeChangedFile: true, includeChangedEnvironment: false);

        var result = fixture.Apply();

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        Assert.Equal("new-file", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        var environment = result.Targets.Single(target => target.TargetKind == SetupTargetKind.Env);
        Assert.Null(environment.BackupReference);
        Assert.Null(environment.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environment.RollbackStatus);
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.EnvironmentBackupPath));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupEnvironmentNotification.NotRequired, journal.EnvironmentNotification);
        Assert.Equal([fixture.FileRecordId], journal.Targets.Select(target => target.RecordId));
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Fact]
    public void Apply_ChangedEnvironmentWithMissingNoOpUsesFullAggregateAndChangedOnlyMutationEvidence()
    {
        var fixture = CanonicalEnvironmentApplyFixture.Create(includeChangedFile: false, includeChangedEnvironment: true);
        var environmentStep = new UserEnvironmentSetupStep(fixture.Platform);
        var previous = environmentStep.Capture(fixture.EnvironmentMemberNames);

        var result = fixture.Apply();

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        var backup = environmentStep.ReadBackup(fixture.EnvironmentBackupPath, fixture.EnvironmentMemberNames);
        var applied = environmentStep.Capture(fixture.EnvironmentMemberNames);
        var changedSubset = environmentStep.Capture(["ENV_CHANGED"]);
        var environment = Assert.Single(result.Targets);
        Assert.Equal(previous.AggregateHash, backup.AggregateHash);
        Assert.Equal(backup.AggregateHash, environment.PreviousStateHash);
        Assert.Equal(applied.AggregateHash, environment.AppliedStateHash);
        Assert.NotEqual(changedSubset.AggregateHash, environment.AppliedStateHash);
        var journal = fixture.LoadJournal();
        Assert.Equal(["ENV_CHANGED"], Assert.Single(journal.Targets).Steps.Select(step => step.MemberKey));
        Assert.Equal("private-desired-marker", fixture.Platform.ReadUserEnvironment("ENV_CHANGED"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_MISSING"));
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.set:ENV_CHANGED"));
        Assert.DoesNotContain("environment.set:ENV_MISSING", fixture.Platform.Operations);
        Assert.Equal(1, fixture.Platform.Operations.Count(operation => operation == "environment.notify"));
        var repositorySafeLedger = Encoding.UTF8.GetString(
            fixture.Platform.ReadSeededFile(fixture.Paths.OwnershipLedger));
        Assert.DoesNotContain("private-desired-marker", repositorySafeLedger, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MissingNoOpBecomesPresentBeforeCaptureFailsStaleWithoutArtifactsOrEcho()
    {
        var fixture = CanonicalEnvironmentApplyFixture.Create(includeChangedFile: false, includeChangedEnvironment: true);
        fixture.Revalidator.OnRevalidate = (_, _) =>
            fixture.Platform.SeedUserEnvironment("ENV_MISSING", "private-external-marker");

        var exception = Assert.Throws<SetupApplyException>(() => fixture.Apply());

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        Assert.DoesNotContain("private-external-marker", exception.Message, StringComparison.Ordinal);
        Assert.Equal("private-external-marker", fixture.Platform.ReadUserEnvironment("ENV_MISSING"));
        Assert.Equal("old-env", fixture.Platform.ReadUserEnvironment("ENV_CHANGED"));
        var planned = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Planned, planned.State);
        Assert.All(planned.Targets, target =>
        {
            Assert.Null(target.BackupReference);
            Assert.Null(target.AppliedStateHash);
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        });
        fixture.AssertNoTransactionArtifacts();
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations);
    }

    [Theory]
    [InlineData(false, SetupOperation.NoOp)]
    [InlineData(true, SetupOperation.Add)]
    [InlineData(true, SetupOperation.Replace)]
    [InlineData(true, SetupOperation.Remove)]
    [InlineData(true, SetupOperation.Mixed)]
    public void Apply_RejectsFileAggregateWhoseChangedMembershipDisagreesWithWholeDesiredBytes(
        bool desiredEqualsCaptured,
        SetupOperation operation)
    {
        var fixture = ApplyFixture.Create(
            fileNoChange: desiredEqualsCaptured,
            includeEnvironment: false);
        fixture.RewriteMemberOperation(fixture.FileRecordId, "setting", operation);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData(SetupOperation.Add)]
    [InlineData(SetupOperation.Replace)]
    [InlineData(SetupOperation.Remove)]
    [InlineData(SetupOperation.Mixed)]
    public void Apply_OpaqueFileAggregateDoesNotInferLogicalNonNoOpMemberOperation(SetupOperation operation)
    {
        var fixture = ApplyFixture.Create(includeEnvironment: false);
        fixture.RewriteMemberOperation(fixture.FileRecordId, "setting", operation);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal(operation, Assert.Single(Assert.Single(result.Targets).Members).Operation);
    }

    [Fact]
    public void Apply_OpaqueFileWithNoOpAndNonNoOpMembersUsesOnePhysicalJournalStep()
    {
        var fixture = ApplyFixture.Create(includeEnvironment: false);
        fixture.RewriteFileMemberOperations(SetupOperation.NoOp, SetupOperation.Replace);
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        Assert.Equal(
            [SetupOperation.NoOp, SetupOperation.Replace],
            Assert.Single(result.Targets).Members.Select(member => member.Operation));
        var target = Assert.Single(new SetupTransactionJournalStore(fixture.Platform, fixture.Paths)
            .Load(fixture.ChangeSetId)!.Targets);
        var step = Assert.Single(target.Steps);
        Assert.Null(step.MemberKey);
    }

    [Fact]
    public void Apply_OpaqueFileWithAnyNonNoOpMemberAndSameBytesRequiresRecoveryWithoutArtifacts()
    {
        var fixture = ApplyFixture.Create(fileNoChange: true, includeEnvironment: false);
        fixture.RewriteFileMemberOperations(SetupOperation.NoOp, SetupOperation.Replace);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        AssertNoTransactionArtifacts(fixture);
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void Apply_MalformedOperationCannotProduceOwnershipForRecoveryOrRollbackConsumers()
    {
        var fixture = ApplyFixture.Create(environmentOperationCase:
            new EnvironmentOperationCase(true, "old", SetupOperation.NoOp, "desired"));
        var planStore = new SetupPlanStore(fixture.Platform, fixture.Paths);
        var ledgerStore = new SetupLedgerStore(fixture.Platform, fixture.Paths, planStore);
        var journalStore = new SetupTransactionJournalStore(fixture.Platform, fixture.Paths);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.Coordinator.Apply(acquisition.Lock!, fixture.ChangeSetId));
        var recovery = new SetupRecoveryCoordinator(
            fixture.Platform, fixture.Paths, planStore, ledgerStore, journalStore)
            .RecoverNext(acquisition.Lock!);
        var rollback = new SetupRollbackCoordinator(
            fixture.Platform, fixture.Paths, planStore, ledgerStore, journalStore)
            .Rollback(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupRecoveryDisposition.None, recovery.Disposition);
        Assert.False(rollback.Success);
        Assert.Equal(SetupChangeSetState.Planned, rollback.ChangeSet!.State);
        AssertNoTransactionArtifacts(fixture);
        var target = fixture.LoadChangeSet().Targets.Single(item => item.RecordId == fixture.EnvironmentRecordId);
        Assert.Null(target.BackupReference);
        Assert.Null(target.AppliedStateHash);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void Apply_ReusesFirstExactOrphanBackupAndCreatesOnlyLaterMissingBackup()
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        var fileBackup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId);
        var exactFileBackup = fixture.Platform.ReadSeededFile(fileBackup);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        Assert.Equal(exactFileBackup, fixture.Platform.ReadSeededFile(fileBackup));
        Assert.True(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId)));
        var environmentBackup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId);
        Assert.Equal(2, fixture.Platform.Operations.Skip(operationCount).Count(operation =>
            operation == $"file.read-bounded:{fileBackup}:{exactFileBackup.Length}"));
        Assert.Equal(2, fixture.Platform.Operations.Skip(operationCount).Count(operation =>
            operation == $"file.read-bounded:{environmentBackup}:{2 * 1024 * 1024}"));
        Assert.DoesNotContain(
            fixture.Platform.Operations.Skip(operationCount),
            operation => IsWriteOperation(operation) && operation.Contains(fileBackup, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "malformed")]
    [InlineData(false, "reparse")]
    [InlineData(true, "malformed")]
    [InlineData(true, "reparse")]
    public void Apply_FreshArtifactsValidateEveryExistingBackupBeforeCreatingAnyMissing(
        bool reverseTargetOrder,
        string invalidKind)
    {
        var fixture = ApplyFixture.Create(reverseTargetOrder: reverseTargetOrder);
        var missingRecordId = reverseTargetOrder ? fixture.EnvironmentRecordId : fixture.FileRecordId;
        var invalidRecordId = reverseTargetOrder ? fixture.FileRecordId : fixture.EnvironmentRecordId;
        var missingPath = fixture.Paths.GetBackup(fixture.ChangeSetId, missingRecordId);
        var invalidPath = fixture.Paths.GetBackup(fixture.ChangeSetId, invalidRecordId);
        fixture.SeedExactBackup(invalidRecordId);
        if (invalidKind == "malformed")
        {
            fixture.Platform.SeedFile(
                invalidPath,
                fixture.Platform.ReadSeededFile(invalidPath).Append((byte)0xff).ToArray());
        }
        else
        {
            fixture.Platform.SeedPathMetadata(
                invalidPath,
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        var invalidBytes = fixture.Platform.ReadSeededFile(invalidPath);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.False(fixture.Platform.FileSystem.FileExists(missingPath));
        Assert.Equal(invalidBytes, fixture.Platform.ReadSeededFile(invalidPath));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_FreshArtifactsValidateReboundExistingBackupBeforeCreatingAnyMissing(
        bool reverseTargetOrder)
    {
        var fixture = ApplyFixture.Create(reverseTargetOrder: reverseTargetOrder);
        var missingRecordId = reverseTargetOrder ? fixture.EnvironmentRecordId : fixture.FileRecordId;
        var invalidRecordId = reverseTargetOrder ? fixture.FileRecordId : fixture.EnvironmentRecordId;
        var missingPath = fixture.Paths.GetBackup(fixture.ChangeSetId, missingRecordId);
        var invalidPath = fixture.Paths.GetBackup(fixture.ChangeSetId, invalidRecordId);
        fixture.SeedExactBackup(invalidRecordId);
        var invalidBytes = fixture.Platform.ReadSeededFile(invalidPath);
        var maximumBytes = reverseTargetOrder ? invalidBytes.Length : 2 * 1024 * 1024;
        using var barrier = fixture.Platform.AddBarrier($"file.read-bounded:{invalidPath}:{maximumBytes}");
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var applying = Task.Run(() => Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId)));
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedPathMetadata(
            invalidPath,
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();
        var exception = await applying;

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.False(fixture.Platform.FileSystem.FileExists(missingPath));
        Assert.Equal(invalidBytes, fixture.Platform.ReadSeededFile(invalidPath));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void Apply_ReopensExactBackupsAndPreparedJournalThenContinuesWithoutArtifactWriteBeforeLedger()
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        fixture.SeedExactBackup(fixture.EnvironmentRecordId);
        fixture.SeedPreparedJournal(fixture.ExpectedJournalTargets());
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId);

        Assert.Equal(SetupCodes.ApplySucceeded, result.OutcomeCode);
        Assert.Equal(SetupChangeSetState.Applied, result.State);
        var operations = fixture.Platform.Operations.Skip(operationCount).ToArray();
        var ledgerWrite = Array.FindIndex(
            operations,
            operation => IsWriteOperation(operation) &&
                operation.Contains(fixture.Paths.OwnershipLedger, StringComparison.Ordinal));
        Assert.True(ledgerWrite >= 0);
        Assert.DoesNotContain(operations.Take(ledgerWrite), IsWriteOperation);
        Assert.Equal(1, operations.Count(operation => operation == "environment.notify"));
        Assert.Equal(SetupJournalPhase.Committed,
            new SetupTransactionJournalStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!.Phase);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("target-order")]
    [InlineData("target-kind")]
    [InlineData("member-key")]
    [InlineData("prior-hash")]
    [InlineData("desired-hash")]
    [InlineData("backup-reference")]
    [InlineData("step-order")]
    public void Apply_NonExactPreparedJournalFailsClosedWithoutAnyWrite(string mismatch)
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        fixture.SeedExactBackup(fixture.EnvironmentRecordId);
        var targets = fixture.ExpectedJournalTargets().ToArray();
        var operation = SetupJournalOperation.Apply;
        switch (mismatch)
        {
            case "operation": operation = SetupJournalOperation.Rollback; break;
            case "target-order": targets = targets.Reverse().ToArray(); break;
            case "target-kind": targets[0] = targets[0] with { TargetKind = SetupTargetKind.Toml }; break;
            case "member-key": targets[1] = ReplaceStep(targets[1], 0, step => step with { MemberKey = "ENV_C" }); break;
            case "prior-hash": targets[0] = ReplaceStep(targets[0], 0, step => step with { PriorStateHash = Hash('a') }); break;
            case "desired-hash": targets[0] = ReplaceStep(targets[0], 0, step => step with { DesiredStateHash = Hash('b') }); break;
            case "backup-reference": targets[0] = ReplaceStep(targets[0], 0, step => step with { BackupReference = "other-backup" }); break;
            case "step-order": targets[1] = targets[1] with { Steps = targets[1].Steps.Reverse().ToArray() }; break;
        }
        fixture.SeedPreparedJournal(targets, operation);
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var exactJournal = fixture.Platform.ReadSeededFile(journalPath);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(exactJournal, fixture.Platform.ReadSeededFile(journalPath));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("notification")]
    [InlineData("phase")]
    public void Apply_UnsupportedOrNonDormantPreparedJournalFailsClosedWithoutAnyWrite(string mismatch)
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        fixture.SeedExactBackup(fixture.EnvironmentRecordId);
        fixture.SeedPreparedJournal(fixture.ExpectedJournalTargets());
        if (mismatch == "phase")
        {
            fixture.MarkPreparedJournalApplying();
        }
        else
        {
            fixture.RewriteJournal(json => mismatch == "unsupported-version"
                ? json.Replace("\"schema_version\": 1", "\"schema_version\": 2", StringComparison.Ordinal)
                : json.Replace("\"environment_notification\": \"not_required\"", "\"environment_notification\": \"pending\"", StringComparison.Ordinal));
        }
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var exactJournal = fixture.Platform.ReadSeededFile(journalPath);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(exactJournal, fixture.Platform.ReadSeededFile(journalPath));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData("file-mismatch")]
    [InlineData("environment-mismatch")]
    [InlineData("file-rebound")]
    [InlineData("environment-rebound")]
    public void Apply_InvalidBackupReferencedByExactPreparedJournalFailsClosedWithoutAnyWrite(string mismatch)
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        fixture.SeedExactBackup(fixture.EnvironmentRecordId);
        fixture.SeedPreparedJournal(fixture.ExpectedJournalTargets());
        var recordId = mismatch.StartsWith("file", StringComparison.Ordinal)
            ? fixture.FileRecordId
            : fixture.EnvironmentRecordId;
        var backupPath = fixture.Paths.GetBackup(fixture.ChangeSetId, recordId);
        if (mismatch.EndsWith("mismatch", StringComparison.Ordinal))
        {
            fixture.Platform.SeedFile(
                backupPath,
                fixture.Platform.ReadSeededFile(backupPath).Append((byte)0xff).ToArray());
        }
        else
        {
            fixture.Platform.SeedPathMetadata(
                backupPath,
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var exactJournal = fixture.Platform.ReadSeededFile(journalPath);
        var exactBackup = fixture.Platform.ReadSeededFile(backupPath);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, exception.Code);
        Assert.Equal(exactJournal, fixture.Platform.ReadSeededFile(journalPath));
        Assert.Equal(exactBackup, fixture.Platform.ReadSeededFile(backupPath));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void Apply_MissingBackupReferencedByExactPreparedJournalRequiresRecoveryWithoutAnyWrite()
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.EnvironmentRecordId);
        fixture.SeedPreparedJournal(fixture.ExpectedJournalTargets());
        var journalPath = fixture.Paths.GetTransactionJournal(fixture.ChangeSetId);
        var exactJournal = fixture.Platform.ReadSeededFile(journalPath);
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId)));
        Assert.Equal(exactJournal, fixture.Platform.ReadSeededFile(journalPath));
        AssertApplyInputsUnchanged(fixture);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void Apply_StalePlanPreservesExactOrphanBackupWithoutCreatingJournal()
    {
        var fixture = ApplyFixture.Create();
        fixture.SeedExactBackup(fixture.FileRecordId);
        var backupPath = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId);
        var exactBackup = fixture.Platform.ReadSeededFile(backupPath);
        fixture.Revalidator.OnRevalidate = (_, _) =>
            fixture.Platform.SeedFile(fixture.TargetPath, Encoding.UTF8.GetBytes("third-party"));
        var operationCount = fixture.Platform.Operations.Count;
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var exception = Assert.Throws<SetupApplyException>(() =>
            fixture.ReopenCoordinator().Apply(acquisition.Lock!, fixture.ChangeSetId));

        Assert.Equal(SetupCodes.StalePlan, exception.Code);
        Assert.Equal(exactBackup, fixture.Platform.ReadSeededFile(backupPath));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(operationCount), IsWriteOperation);
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
            .First(item => item.operation.StartsWith(
                $"file.try-write-new-flushed:{fixture.Paths.Backups}", StringComparison.Ordinal)).index;
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
            StatusProjection = CreateStatusProjection([new SetupLedgerMember("setting", SetupOperation.Remove)]),
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

    private static SetupJournalTarget ReplaceStep(
        SetupJournalTarget target,
        int index,
        Func<SetupJournalStep, SetupJournalStep> replace)
    {
        var steps = target.Steps.ToArray();
        steps[index] = replace(steps[index]);
        return target with { Steps = steps };
    }

    private static string Hash(char value) => new(value, 64);

    private static bool IsWriteOperation(string operation) =>
        operation.StartsWith("file.write:", StringComparison.Ordinal) ||
        operation.StartsWith("file.write-new:", StringComparison.Ordinal) ||
        operation.StartsWith("file.try-write-new-flushed:", StringComparison.Ordinal) ||
        operation.StartsWith("file.flush:", StringComparison.Ordinal) ||
        operation.StartsWith("file.replace:", StringComparison.Ordinal) ||
        operation.StartsWith("file.move:", StringComparison.Ordinal) ||
        operation.StartsWith("file.delete:", StringComparison.Ordinal) ||
        operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
        operation == "environment.notify";

    private static void AssertApplyInputsUnchanged(ApplyFixture fixture)
    {
        Assert.Equal("old", Encoding.UTF8.GetString(fixture.Platform.ReadSeededFile(fixture.TargetPath)));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupChangeSetState.Planned, fixture.LoadChangeSet().State);
    }

    private static void AssertNoTransactionArtifacts(ApplyFixture fixture)
    {
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.FileRecordId)));
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.EnvironmentRecordId)));
    }

    private sealed record EnvironmentOperationCase(
        bool PriorExists,
        string? PriorValue,
        SetupOperation Operation,
        string? DesiredValue);

    private sealed class CanonicalEnvironmentApplyFixture
    {
        private CanonicalEnvironmentApplyFixture(bool includeChangedFile, bool includeChangedEnvironment)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 14, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000121");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000122");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000123");
            TargetPath = Path.Combine(Platform.LocalApplicationData, "canonical-settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);

            var planTargets = new List<SetupPrivatePlanTarget>();
            var ledgerTargets = new List<SetupLedgerTarget>();
            if (includeChangedFile)
            {
                Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old-file"));
                planTargets.Add(new SetupPrivatePlanTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    TargetPath,
                    SetupHash.File(true, Encoding.UTF8.GetBytes("old-file")),
                    "new-file",
                    [new SetupPrivatePlanMember("setting", SetupOperation.Replace, "new-file")]));
                ledgerTargets.Add(new SetupLedgerTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    "settings",
                    "github-copilot",
                    [new SetupLedgerMember("setting", SetupOperation.Replace)],
                    planTargets[^1].BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection([new SetupLedgerMember("setting", SetupOperation.Replace)]),
                    "1.0.0"));
            }

            var environmentMembers = new List<SetupPrivatePlanMember>();
            if (includeChangedEnvironment)
            {
                Platform.SeedUserEnvironment("ENV_CHANGED", "old-env");
                environmentMembers.Add(new SetupPrivatePlanMember(
                    "ENV_CHANGED", SetupOperation.Replace, "private-desired-marker"));
            }

            environmentMembers.Add(new SetupPrivatePlanMember(
                "ENV_MISSING", SetupOperation.NoOp, null));
            EnvironmentMemberNames = environmentMembers.Select(member => member.SettingKey).ToArray();
            var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(EnvironmentMemberNames);
            planTargets.Add(new SetupPrivatePlanTarget(
                EnvironmentRecordId,
                SetupTargetKind.Env,
                "current-user",
                environmentCapture.AggregateHash,
                "environment-allowlist",
                environmentMembers));
            ledgerTargets.Add(new SetupLedgerTarget(
                EnvironmentRecordId,
                SetupTargetKind.Env,
                "user-environment",
                "github-copilot",
                environmentMembers.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                environmentCapture.AggregateHash,
                null,
                null,
                null,
                SetupLedgerRollbackStatus.NotAvailable,
                SetupRestartRequirement.RestartTerminalSession,
                CreateStatusProjection(environmentMembers.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray()),
                "1.0.0"));

            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                "1.0.0",
                planTargets);
            var planned = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "cli",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                ledgerTargets);
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var setupLock = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.PersistPlannedChangeSet(setupLock.Lock!, plan, planned);
            Revalidator = new RecordingRevalidator();
            Coordinator = new SetupApplyCoordinator(
                Platform,
                Paths,
                planStore,
                ledgerStore,
                new SetupTransactionJournalStore(Platform, Paths),
                Revalidator);
        }

        public SetupTestPlatform Platform { get; }

        public SetupRuntimePaths Paths { get; }

        public Guid ChangeSetId { get; }

        public Guid FileRecordId { get; }

        public Guid EnvironmentRecordId { get; }

        public string TargetPath { get; }

        public IReadOnlyList<string> EnvironmentMemberNames { get; }

        public RecordingRevalidator Revalidator { get; }

        public SetupApplyCoordinator Coordinator { get; }

        public string EnvironmentBackupPath => Paths.GetBackup(ChangeSetId, EnvironmentRecordId);

        public static CanonicalEnvironmentApplyFixture Create(
            bool includeChangedFile,
            bool includeChangedEnvironment) => new(includeChangedFile, includeChangedEnvironment);

        public SetupLedgerChangeSet Apply()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return Coordinator.Apply(acquisition.Lock!, ChangeSetId);
        }

        public SetupLedgerChangeSet LoadChangeSet() =>
            new SetupLedgerStore(Platform, Paths, new SetupPlanStore(Platform, Paths))
                .Load().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public void AssertNoTransactionArtifacts()
        {
            Assert.False(Platform.FileSystem.FileExists(Paths.GetTransactionJournal(ChangeSetId)));
            Assert.False(Platform.FileSystem.FileExists(Paths.GetBackup(ChangeSetId, FileRecordId)));
            Assert.False(Platform.FileSystem.FileExists(EnvironmentBackupPath));
        }
    }

    private sealed class ApplyFixture
    {
        private ApplyFixture(
            bool noChanges,
            bool fileNoChange,
            bool environmentANoChange,
            bool environmentNoChange,
            bool includeEnvironment,
            bool reverseTargetOrder,
            EnvironmentOperationCase? environmentOperationCase)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000101");
            FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000102");
            EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000103");
            ReverseTargetOrder = reverseTargetOrder;
            TargetPath = Path.Combine(Platform.LocalApplicationData, "settings.json");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes("old"));
            if (environmentOperationCase is null || environmentOperationCase.PriorExists)
            {
                Platform.SeedUserEnvironment("ENV_A", environmentOperationCase?.PriorValue ?? "old-a");
            }
            Platform.SeedUserEnvironment("ENV_B", "old-b");
            Platform.SeedUserEnvironment("UNRELATED", "untouched");
            var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(["ENV_A", "ENV_B"]);
            var isolateEnvironmentOperation = environmentOperationCase is not null;
            var fileDesired = noChanges || fileNoChange || isolateEnvironmentOperation ? "old" : "new";
            var fileOperation = noChanges || fileNoChange || isolateEnvironmentOperation
                ? SetupOperation.NoOp
                : SetupOperation.Replace;
            var environmentAOperation = environmentOperationCase?.Operation ??
                (noChanges || environmentANoChange || environmentNoChange ? SetupOperation.NoOp : SetupOperation.Replace);
            var environmentADesired = environmentOperationCase is null
                ? (noChanges || environmentANoChange || environmentNoChange ? "old-a" : "desired-a")
                : environmentOperationCase.DesiredValue;
            var environmentBOperation = noChanges || environmentNoChange || isolateEnvironmentOperation
                ? SetupOperation.NoOp
                : SetupOperation.Remove;
            var environmentBDesired = noChanges || environmentNoChange || isolateEnvironmentOperation ? "old-b" : null;
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
                    [new SetupPrivatePlanMember("ENV_A", environmentAOperation, environmentADesired),
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
                    null, null, null, SetupLedgerRollbackStatus.NotAvailable, SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection([new SetupLedgerMember("setting", fileOperation)]), "1.0.0"),
            };
            if (includeEnvironment)
            {
                ledgerTargets.Add(new SetupLedgerTarget(EnvironmentRecordId, SetupTargetKind.Env, "user-environment", "github-copilot",
                    [new SetupLedgerMember("ENV_A", environmentAOperation), new SetupLedgerMember("ENV_B", environmentBOperation)],
                    plan.Targets[1].BaseStateHash, null, null, null, SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    CreateStatusProjection([new SetupLedgerMember("ENV_A", environmentAOperation), new SetupLedgerMember("ENV_B", environmentBOperation)]),
                    "1.0.0"));
            }
            if (reverseTargetOrder)
            {
                plan = plan with { Targets = plan.Targets.Reverse().ToArray() };
                ledgerTargets.Reverse();
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
        public bool ReverseTargetOrder { get; }
        public string TargetPath { get; }
        public RecordingRevalidator Revalidator { get; }
        public SetupApplyCoordinator Coordinator { get; }

        public SetupApplyCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupApplyCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths),
                Revalidator);
        }

        public void SeedExactBackup(Guid recordId)
        {
            Platform.SeedDirectory(Paths.Backups);
            Platform.SeedDirectory(Path.Combine(Paths.Backups, ChangeSetId.ToString("D")));
            var backupPath = Paths.GetBackup(ChangeSetId, recordId);
            if (recordId == FileRecordId)
            {
                var step = new AtomicFileSetupStep(Platform);
                step.CreateOrValidateBackup(
                    backupPath,
                    step.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath));
                return;
            }

            var environmentStep = new UserEnvironmentSetupStep(Platform);
            environmentStep.CreateOrValidateBackup(
                backupPath,
                environmentStep.Capture(["ENV_A", "ENV_B"]));
        }

        public IReadOnlyList<SetupJournalTarget> ExpectedJournalTargets()
        {
            var fileStep = new AtomicFileSetupStep(Platform);
            var fileCapture = fileStep.Capture(Path.GetDirectoryName(TargetPath)!, TargetPath);
            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var environmentCapture = environmentStep.Capture(["ENV_A", "ENV_B"]);
            IReadOnlyList<SetupJournalTarget> targets =
            [
                new SetupJournalTarget(
                    FileRecordId,
                    SetupTargetKind.Json,
                    [new SetupJournalStep(
                        null,
                        fileCapture.Hash,
                        SetupHash.File(true, Encoding.UTF8.GetBytes("new")),
                        FileRecordId.ToString("D"),
                        SetupJournalStepPhase.Pending)]),
                new SetupJournalTarget(
                    EnvironmentRecordId,
                    SetupTargetKind.Env,
                    [
                        new SetupJournalStep(
                            "ENV_A",
                            environmentCapture.Members[0].Hash,
                            environmentStep.HashMember("ENV_A", UserEnvironmentValue.Present("desired-a")),
                            EnvironmentRecordId.ToString("D"),
                            SetupJournalStepPhase.Pending),
                        new SetupJournalStep(
                            "ENV_B",
                            environmentCapture.Members[1].Hash,
                            environmentStep.HashMember("ENV_B", UserEnvironmentValue.Missing),
                            EnvironmentRecordId.ToString("D"),
                            SetupJournalStepPhase.Pending),
                    ])
            ];
            return ReverseTargetOrder ? targets.Reverse().ToArray() : targets;
        }

        public void SeedPreparedJournal(
            IReadOnlyList<SetupJournalTarget> targets,
            SetupJournalOperation operation = SetupJournalOperation.Apply)
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            new SetupTransactionJournalStore(Platform, Paths).OpenOrCreatePrepared(
                acquisition.Lock!, ChangeSetId, operation, targets);
        }

        public void MarkPreparedJournalApplying()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            new SetupTransactionJournalStore(Platform, Paths).MarkTransactionPhase(
                acquisition.Lock!, ChangeSetId, SetupJournalPhase.Applying);
        }

        public void RewriteJournal(Func<string, string> rewrite)
        {
            var path = Paths.GetTransactionJournal(ChangeSetId);
            Platform.SeedFile(path, Encoding.UTF8.GetBytes(rewrite(Encoding.UTF8.GetString(Platform.ReadSeededFile(path)))));
        }

        public static ApplyFixture Create(
            bool noChanges = false,
            bool fileNoChange = false,
            bool environmentANoChange = false,
            bool environmentNoChange = false,
            bool includeEnvironment = true,
            bool reverseTargetOrder = false,
            EnvironmentOperationCase? environmentOperationCase = null) => new(
                noChanges,
                fileNoChange,
                environmentANoChange,
                environmentNoChange,
                includeEnvironment,
                reverseTargetOrder,
                environmentOperationCase);

        public void RewriteMemberOperation(Guid recordId, string settingKey, SetupOperation operation)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var plan = planStore.Load(ChangeSetId)!;
            var planTargets = plan.Targets.Select(target => target.RecordId != recordId
                ? target
                : target with
                {
                    Members = target.Members.Select(member => member.SettingKey == settingKey
                        ? member with { Operation = operation }
                        : member).ToArray(),
                }).ToArray();
            Platform.SeedFile(Paths.GetPlan(ChangeSetId), SetupPlanStore.Serialize(plan with { Targets = planTargets }));

            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var ledger = ledgerStore.Load();
            var changeSet = ledger.ChangeSets.Single(item => item.ChangeSetId == ChangeSetId);
            var ledgerTargets = changeSet.Targets.Select(target => target.RecordId != recordId
                ? target
                : target with
                {
                    Members = target.Members.Select(member => member.SettingKey == settingKey
                        ? member with { Operation = operation }
                        : member).ToArray(),
                    StatusProjection = CreateStatusProjection(target.Members.Select(member => member.SettingKey == settingKey
                        ? member with { Operation = operation }
                        : member).ToArray()),
                }).ToArray();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(item => item.ChangeSetId == ChangeSetId
                    ? item with { Targets = ledgerTargets }
                    : item).ToArray(),
            });
        }

        public void RewriteFileMemberOperations(params SetupOperation[] operations)
        {
            var members = operations.Select((operation, index) =>
                new SetupPrivatePlanMember($"setting-{index}", operation, "desired")).ToArray();
            var planStore = new SetupPlanStore(Platform, Paths);
            var plan = planStore.Load(ChangeSetId)!;
            var planTargets = plan.Targets.Select(target => target.RecordId == FileRecordId
                ? target with { Members = members }
                : target).ToArray();
            Platform.SeedFile(Paths.GetPlan(ChangeSetId), SetupPlanStore.Serialize(plan with { Targets = planTargets }));

            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var ledger = ledgerStore.Load();
            var ledgerTargets = ledger.ChangeSets.Single(item => item.ChangeSetId == ChangeSetId).Targets
                .Select(target => target.RecordId == FileRecordId
                    ? target with
                    {
                        Members = members.Select(member =>
                            new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                        StatusProjection = CreateStatusProjection(members.Select(member =>
                            new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray()),
                    }
                    : target).ToArray();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(item => item.ChangeSetId == ChangeSetId
                    ? item with { Targets = ledgerTargets }
                    : item).ToArray(),
            });
        }

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

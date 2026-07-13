using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupStatusServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_EmptyLedgerUsesProductionRecoveryAndReturnsSerializableStatus()
    {
        var fixture = StatusServiceFixture.Create();

        var result = fixture.CreateProductionService().Status(null);

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.StatusReady, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.ChangeSets);
        Assert.False(result.Truncated);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation.StartsWith("environment.", StringComparison.Ordinal) ||
            operation.StartsWith("file.replace:", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_ProductionServiceCompletesRecoveryReadBeforeStatusReadAndProjection()
    {
        var row = Row(11, SetupChangeSetState.NoChanges, 1);
        var fixture = StatusServiceFixture.Create([row]);

        var result = fixture.CreateProductionService().Status(null);

        var projected = Assert.Single(result.ChangeSets);
        Assert.Equal(row.ChangeSetId.ToString("D"), projected.ChangeSetId);
        Assert.Equal(2, fixture.Platform.Operations.Count(operation =>
            operation == $"file.read-bounded:{fixture.Paths.OwnershipLedger}:{SetupLedgerStore.MaximumLedgerBytes}"));
        var secondLedgerRead = fixture.Platform.Operations
            .Select((operation, index) => (operation, index))
            .Where(item => item.operation == $"file.read-bounded:{fixture.Paths.OwnershipLedger}:{SetupLedgerStore.MaximumLedgerBytes}")
            .Select(item => item.index)
            .Last();
        var projectionPlanRead = fixture.Platform.Operations
            .Select((operation, index) => (operation, index))
            .Single(item => item.operation == $"file.exists:{fixture.Paths.GetPlan(row.ChangeSetId)}")
            .index;
        Assert.True(secondLedgerRead < projectionPlanRead);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupRecoveryOperation.Apply, SetupCodes.InterruptedApplyRecovered, SetupChangeSetState.Applied)]
    [InlineData(SetupRecoveryOperation.Rollback, SetupCodes.InterruptedRollbackRecovered, SetupChangeSetState.RolledBack)]
    public void Status_RecoveredChangeSetUsesEffectiveOverlayAndProjectsItExactlyOnceWhileLockIsHeld(
        SetupRecoveryOperation operation,
        string code,
        SetupChangeSetState effectiveState)
    {
        var recoveredId = Id(101);
        var durable = Row(101, SetupChangeSetState.Applied, 1, outcomeCode: SetupCodes.ApplySucceeded);
        var effective = durable with
        {
            UpdatedAt = Timestamp.AddMinutes(5),
            State = effectiveState,
            OutcomeCode = code,
        };
        var fixture = StatusServiceFixture.Create([durable, Row(102, SetupChangeSetState.Applied, 2)]);
        var projectedIds = new List<Guid>();
        var recoveryObserved = false;
        var service = fixture.CreateService(
            setupLock =>
            {
                recoveryObserved = true;
                return new SetupRecoveryResult(SetupRecoveryDisposition.Recovered, code, recoveredId, operation, effective);
            },
            changeSet =>
            {
                Assert.True(recoveryObserved);
                using var contended = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
                Assert.False(contended.Acquired);
                projectedIds.Add(changeSet.ChangeSetId);
                return Project(changeSet);
            });

        var result = service.Status(null);

        Assert.True(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(operation, result.RecoveryOperation);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], result.NextActions);
        Assert.Equal([recoveredId, Id(102)], projectedIds);
        Assert.Equal(code, result.ChangeSets[0].OutcomeCode);
        Assert.Equal(effectiveState, result.ChangeSets[0].State);
        Assert.Equal(SetupStorageJson.FormatTimestamp(effective.UpdatedAt), result.ChangeSets[0].UpdatedAt);
        Assert.Single(projectedIds, id => id == recoveredId);
        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));
        using var reacquired = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public void Status_FailedRecoveryUsesCanonicalTerminalEffectiveOverlayWithoutChangingDurableLedger()
    {
        var recoveredId = Id(201);
        var durable = Row(201, SetupChangeSetState.Applied, 1, outcomeCode: SetupCodes.ApplySucceeded);
        var terminalEffective = durable with
        {
            UpdatedAt = Timestamp.AddMinutes(1),
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        };
        var fixture = StatusServiceFixture.Create([durable]);
        var projectedStates = new List<SetupChangeSetState>();
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                recoveredId,
                SetupRecoveryOperation.Apply,
                terminalEffective),
            changeSet =>
            {
                return Project(changeSet);
            },
            (evidence, effective) =>
            {
                projectedStates.Add(evidence.State);
                return Project(effective);
            });

        var result = service.Status(null);

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.RecoveryOperation);
        var projected = Assert.Single(result.ChangeSets);
        Assert.Equal(SetupChangeSetState.Partial, projected.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, projected.OutcomeCode);
        Assert.False(projected.RollbackAvailable);
        Assert.Equal([SetupChangeSetState.Applied], projectedStates);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadLedger().ChangeSets.Single().State);
        Assert.Equal(SetupCodes.ApplySucceeded, fixture.LoadLedger().ChangeSets.Single().OutcomeCode);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal("partial", document.RootElement.GetProperty("change_sets")[0].GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applied, SetupCodes.InterruptedRecoveryFailed, SetupCodes.InterruptedRecoveryFailed, true, null)]
    [InlineData(SetupChangeSetState.Partial, SetupCodes.ApplySucceeded, SetupCodes.InterruptedRecoveryFailed, true, null)]
    [InlineData(SetupChangeSetState.Partial, SetupCodes.InterruptedRecoveryFailed, SetupCodes.ApplySucceeded, true, null)]
    [InlineData(SetupChangeSetState.Partial, SetupCodes.InterruptedRecoveryFailed, SetupCodes.InterruptedRecoveryFailed, false, null)]
    [InlineData(SetupChangeSetState.Partial, SetupCodes.InterruptedRecoveryFailed, SetupCodes.InterruptedRecoveryFailed, false, "other-adapter")]
    public void Status_MalformedFailedRecoveryEffectiveOverlayReturnsInternalErrorWithoutProjection(
        SetupChangeSetState state,
        string outcomeCode,
        string targetOutcomeCode,
        bool rollbackStatusIsCanonical,
        string? adapterFilter)
    {
        var durable = Row(211, SetupChangeSetState.Applied, 1) with
        {
            Targets = [Target(211, SetupCodes.ApplySucceeded, SetupLedgerRollbackStatus.Pending)],
        };
        var effective = durable with
        {
            State = state,
            OutcomeCode = outcomeCode,
            Targets = [Target(
                211,
                targetOutcomeCode,
                rollbackStatusIsCanonical
                    ? SetupLedgerRollbackStatus.NotAvailable
                    : SetupLedgerRollbackStatus.Pending)],
        };
        var fixture = StatusServiceFixture.Create([durable]);
        var projectionCalled = false;
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                durable.ChangeSetId,
                SetupRecoveryOperation.Apply,
                effective),
            changeSet =>
            {
                projectionCalled = true;
                return Project(changeSet);
            },
            (_, changeSet) =>
            {
                projectionCalled = true;
                return Project(changeSet);
            });

        var result = service.Status(adapterFilter);

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Empty(result.ChangeSets);
        Assert.False(projectionCalled);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void Status_RecoveredUnrelatedAdapterAppliesExactFilterAfterOverlay()
    {
        var recovered = Row(301, SetupChangeSetState.Applied, 1, "github-copilot");
        var selected = Row(302, SetupChangeSetState.Applied, 2, "other-adapter");
        var fixture = StatusServiceFixture.Create([recovered, selected]);
        var projectedIds = new List<Guid>();
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Recovered,
                SetupCodes.InterruptedApplyRecovered,
                recovered.ChangeSetId,
                SetupRecoveryOperation.Apply,
                recovered with { OutcomeCode = SetupCodes.InterruptedApplyRecovered }),
            changeSet =>
            {
                projectedIds.Add(changeSet.ChangeSetId);
                return Project(changeSet);
            });

        var result = service.Status("other-adapter");

        Assert.Equal("other-adapter", result.Adapter);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        var projected = Assert.Single(result.ChangeSets);
        Assert.Equal(selected.ChangeSetId.ToString("D"), projected.ChangeSetId);
        Assert.Equal([selected.ChangeSetId], projectedIds);
        Assert.False(result.Truncated);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void Status_FailedRecoveryForUnrelatedAdapterPreservesCorrelationAndFiltersOverlayOut()
    {
        var recovered = Row(311, SetupChangeSetState.Applied, 1, "github-copilot");
        var effective = recovered with
        {
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        };
        var fixture = StatusServiceFixture.Create([recovered]);
        var normalProjectionCalls = 0;
        var failedProjectionCalls = 0;
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                recovered.ChangeSetId,
                SetupRecoveryOperation.Apply,
                effective),
            changeSet =>
            {
                normalProjectionCalls++;
                return Project(changeSet);
            },
            (evidence, projection) =>
            {
                failedProjectionCalls++;
                return Project(projection);
            });

        var result = service.Status("other-adapter");

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(recovered.ChangeSetId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.RecoveryOperation);
        Assert.Equal("other-adapter", result.Adapter);
        Assert.Empty(result.ChangeSets);
        Assert.Equal(0, normalProjectionCalls);
        Assert.Equal(0, failedProjectionCalls);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Empty(document.RootElement.GetProperty("change_sets").EnumerateArray());
    }

    [Fact]
    public void Status_OverlaidRecoveryRowIsPrioritizedBeforeHardCapAndProjectedOnce()
    {
        var rows = Enumerable.Range(1, 101)
            .Select(index => Row(index, SetupChangeSetState.Applied, index))
            .ToArray();
        var recovered = rows[0] with
        {
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        };
        var fixture = StatusServiceFixture.Create(rows);
        var projectedIds = new List<Guid>();
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                recovered.ChangeSetId,
                SetupRecoveryOperation.Apply,
                recovered),
            changeSet =>
            {
                projectedIds.Add(changeSet.ChangeSetId);
                return Project(changeSet);
            });

        var result = service.Status(null);

        Assert.Equal(100, result.ChangeSets.Count);
        Assert.True(result.Truncated);
        Assert.Equal(recovered.ChangeSetId.ToString("D"), result.ChangeSets[0].ChangeSetId);
        Assert.Equal(SetupChangeSetState.Partial, result.ChangeSets[0].State);
        Assert.Single(projectedIds, id => id == recovered.ChangeSetId);
        Assert.DoesNotContain(Id(1), projectedIds.Skip(1));
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void Status_RecoveryRequiredReturnsNoProjectionOrRejectedCorrelation()
    {
        var fixture = StatusServiceFixture.Create([Row(401, SetupChangeSetState.Applied, 1)]);
        var projected = false;
        var service = fixture.CreateService(
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                Id(401),
                SetupRecoveryOperation.Apply,
                null),
            changeSet =>
            {
                projected = true;
                return Project(changeSet);
            });

        var result = service.Status("github-copilot");

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal("github-copilot", result.Adapter);
        Assert.Empty(result.ChangeSets);
        Assert.False(projected);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void Status_LockContentionReturnsSetupBusyWithoutRunningRecoveryOrProjection()
    {
        var fixture = StatusServiceFixture.Create([Row(501, SetupChangeSetState.Applied, 1)]);
        var recoveryCalled = false;
        var projectionCalled = false;
        var service = fixture.CreateService(
            _ =>
            {
                recoveryCalled = true;
                return NoRecovery;
            },
            changeSet =>
            {
                projectionCalled = true;
                return Project(changeSet);
            });
        using var held = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        Assert.True(held.Acquired);

        var result = service.Status("github-copilot");

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.SetupBusy, result.Code);
        Assert.Equal("github-copilot", result.Adapter);
        Assert.Empty(result.ChangeSets);
        Assert.False(recoveryCalled);
        Assert.False(projectionCalled);
        SetupContractValidator.Validate(result);
    }

    private static SetupRecoveryResult NoRecovery { get; } = new(
        SetupRecoveryDisposition.None,
        null,
        null,
        null,
        null);

    private static Guid Id(int value) =>
        Guid.ParseExact($"00000000-0000-7000-8000-{value:D12}", "D");

    private static SetupLedgerChangeSet Row(
        int id,
        SetupChangeSetState state,
        int seconds,
        string adapter = "github-copilot",
        string? outcomeCode = null) => new(
        Id(id),
        adapter,
        "all",
        Timestamp,
        Timestamp.AddSeconds(seconds),
        "1.0.0",
        outcomeCode,
        state,
        []);

    private static SetupLedgerTarget Target(
        int id,
        string outcomeCode,
        SetupLedgerRollbackStatus rollbackStatus) => new(
        Id(id),
        SetupTargetKind.Json,
        $"target-{id}",
        "github-copilot",
        [new SetupLedgerMember("setting.key", SetupOperation.Replace)],
        new string('a', 64),
        new string('b', 64),
        "backup-ref",
        outcomeCode,
        rollbackStatus,
        SetupRestartRequirement.None,
        new SetupStatusProjection(
            true,
            "1.0.0",
            SetupOperation.Replace,
            SetupEffectiveSource.UserSetting,
            "http://127.0.0.1:4320",
            null,
            null,
            [new SetupMemberChangeResult(
                "setting.key",
                SetupOperation.Replace,
                "present_different",
                "configured_loopback",
                "none",
                false)]),
        "1.0.0");

    private static SetupChangeSetStatusResult Project(SetupLedgerChangeSet changeSet) => new(
        changeSet.ChangeSetId.ToString("D"),
        changeSet.Adapter,
        changeSet.SelectedTarget,
        SetupStorageJson.FormatTimestamp(changeSet.CreatedAt),
        SetupStorageJson.FormatTimestamp(changeSet.UpdatedAt),
        changeSet.State,
        changeSet.OutcomeCode,
        SetupCurrentState.NotApplicable,
        false,
        []);

    private sealed class StatusServiceFixture(
        SetupTestPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore)
    {
        public SetupTestPlatform Platform { get; } = platform;

        public SetupRuntimePaths Paths { get; } = paths;

        public static StatusServiceFixture Create(IReadOnlyList<SetupLedgerChangeSet>? rows = null)
        {
            var platform = new SetupTestPlatform(Timestamp);
            var paths = new SetupRuntimePaths(platform);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var journalStore = new SetupTransactionJournalStore(platform, paths);
            if (rows is not null)
            {
                platform.SeedFile(
                    paths.OwnershipLedger,
                    SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, rows)));
            }

            return new StatusServiceFixture(platform, paths, planStore, ledgerStore, journalStore);
        }

        public SetupStatusService CreateProductionService() =>
            new(Platform, Paths, planStore, ledgerStore, journalStore);

        public SetupStatusService CreateService(
            Func<SetupLock, SetupRecoveryResult> recover,
            Func<SetupLedgerChangeSet, SetupChangeSetStatusResult> project,
            Func<SetupLedgerChangeSet, SetupLedgerChangeSet, SetupChangeSetStatusResult>? projectFailedRecovery = null) =>
            new(
                Platform,
                Paths,
                ledgerStore,
                recover,
                project,
                projectFailedRecovery ?? ((_, effective) => project(effective)));

        public SetupOwnershipLedger LoadLedger() => ledgerStore.LoadForRecovery();
    }
}

using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupCommandDispatcherTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid RecordId = Guid.Parse("00000000-0000-7000-8000-000000000101");

    [Fact]
    public void DispatchPlan_ChangedTargetPersistsArtifactsAndReturnsValidatedPlanReady()
    {
        var recoveryCalls = 0;
        var stages = new List<string>();
        var adapter = new RecordingAdapter("test-adapter", request =>
        {
            stages.Add("plan");
            return SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)]),
                [SetupCodes.MonitorNotRunning],
                [SetupCodes.StartLocalMonitor]);
        });
        var fixture = DispatcherFixture.Create(
            adapter,
            _ =>
            {
                recoveryCalls++;
                stages.Add("recover");
                return NoRecovery();
            });

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.PlanReady, result.Code);
        Assert.Equal("00000000-0000-7000-8000-000000000001", result.ChangeSetId);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Equal([SetupCodes.MonitorNotRunning], result.Warnings);
        Assert.Equal([SetupCodes.StartLocalMonitor], result.NextActions);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(1, adapter.PlanCalls);
        Assert.Equal("sample", adapter.LastRequest!.SelectedTarget);
        Assert.Equal(Timestamp, adapter.LastRequest.CreatedAt);
        Assert.Equal("1.2.3", adapter.LastRequest.ToolVersion);
        Assert.Equal(["recover", "plan"], stages);
        Assert.Single(fixture.Platform.Operations, operation => operation == $"file.lock:{fixture.Paths.Lock}");
        var target = Assert.Single(result.Targets);
        Assert.Equal(RecordId.ToString("D"), target.RecordId);
        Assert.Equal("first-target", target.TargetLabel);
        Assert.True(target.RollbackAvailable);
        Assert.Null(target.ReferenceState);
        Assert.Null(target.CurrentState);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupTargetResult>)result.Targets)[0] = target);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupMemberChangeResult>)target.Changes)[0] = target.Changes[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.NextActions)[0] = "changed");
        Assert.Equal(result.ChangeSetId, fixture.PlanStore.Load(Guid.Parse(result.ChangeSetId!))!.ChangeSetId.ToString("D"));
        var persisted = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(result.ChangeSetId, persisted.ChangeSetId.ToString("D"));
        var planPath = fixture.Paths.GetPlan(Guid.Parse(result.ChangeSetId!));
        var operations = fixture.Platform.Operations;
        var planPersisted = operations
            .Select((operation, index) => (operation, index))
            .Single(item => item.operation == $"file.move:{planPath}.tmp->{planPath}")
            .index;
        var ledgerPersisted = operations
            .Select((operation, index) => (operation, index))
            .Single(item => item.operation ==
                $"file.move:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}")
            .index;
        Assert.True(planPersisted < ledgerPersisted);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("all-no-op")]
    [InlineData("guidance")]
    public void DispatchPlan_NoWritableChangesPersistsPlannedArtifactsAndReturnsNoChanges(string variant)
    {
        var records = variant == "guidance"
            ? new[] { CreateGuidanceRecord(RecordId) }
            : [CreateRecord(RecordId, "first-target", SetupOperation.NoOp)];
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(request, records)));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.NoChanges, result.Code);
        Assert.NotNull(result.ChangeSetId);
        Assert.All(result.Targets, target => Assert.False(target.RollbackAvailable));
        Assert.Equal(SetupChangeSetState.Planned, Assert.Single(fixture.LedgerStore.Load().ChangeSets).State);
        Assert.NotNull(fixture.PlanStore.Load(Guid.Parse(result.ChangeSetId!)));
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal("no_changes", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void DispatchPlan_PreservesAdapterTargetOrderAcrossResultAndPersistedArtifacts()
    {
        var secondRecordId = Guid.Parse("00000000-0000-7000-8000-000000000102");
        var adapter = new RecordingAdapter("test-adapter", request => SetupPlanResult.Planned(CreatePlan(request,
        [
            CreateRecord(secondRecordId, "second-target", SetupOperation.Replace),
            CreateRecord(RecordId, "first-target", SetupOperation.NoOp),
        ])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal([secondRecordId.ToString("D"), RecordId.ToString("D")], result.Targets.Select(target => target.RecordId));
        var changeSetId = Guid.Parse(result.ChangeSetId!);
        Assert.Equal([secondRecordId, RecordId], fixture.PlanStore.Load(changeSetId)!.Targets.Select(target => target.RecordId));
        Assert.Equal([secondRecordId, RecordId], Assert.Single(fixture.LedgerStore.Load().ChangeSets).Targets.Select(target => target.RecordId));
        Assert.True(result.Targets[0].RollbackAvailable);
        Assert.False(result.Targets[1].RollbackAvailable);
    }

    [Fact]
    public void DispatchPlan_SanitizedAdapterFailureReturnsImmutableDiagnosticsWithoutArtifacts()
    {
        var adapter = new RecordingAdapter("test-adapter", _ => SetupPlanResult.Failure<SetupChangePlan>(
            SetupCodes.UnsupportedTarget,
            [SetupCodes.ManagedPolicyUnverified],
            [SetupCodes.RunVsCodePolicyDiagnostics]));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Empty(result.Targets);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics], result.NextActions);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.NextActions)[0] = "changed");
        Assert.Empty(fixture.LedgerStore.Load().ChangeSets);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetPlan(Guid.Parse("00000000-0000-7000-8000-000000000001"))));
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal("unsupported_target", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void DispatchPlan_WhenLockIsBusyStopsBeforeRecoveryAndAdapterResolution()
    {
        var recoveryCalls = 0;
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(
            adapter,
            _ =>
            {
                recoveryCalls++;
                return NoRecovery();
            });
        using var held = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.SetupBusy, result.Code);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Null(result.ChangeSetId);
        Assert.Equal(0, recoveryCalls);
        Assert.Equal(0, adapter.PlanCalls);
        Assert.Empty(fixture.LedgerStore.Load().ChangeSets);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupRecoveryOperation.Apply, SetupCodes.InterruptedApplyRecovered)]
    [InlineData(SetupRecoveryOperation.Rollback, SetupCodes.InterruptedRollbackRecovered)]
    public void DispatchPlan_WhenRecoveryCompletesReturnsCorrelationAndDoesNotConsumeRequestedPlanId(
        SetupRecoveryOperation operation,
        string code)
    {
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000901");
        var recoveries = new Queue<SetupRecoveryResult>(
        [
            new SetupRecoveryResult(SetupRecoveryDisposition.Recovered, code, recoveredId, operation, null),
            NoRecovery(),
        ]);
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => recoveries.Dequeue());

        var recovered = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.True(recovered.Success);
        Assert.Equal(code, recovered.Code);
        Assert.Null(recovered.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), recovered.RecoveredChangeSetId);
        Assert.Equal(operation, recovered.RecoveryOperation);
        Assert.Equal("test-adapter", recovered.Adapter);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], recovered.NextActions);
        Assert.Equal(0, adapter.PlanCalls);
        Assert.Empty(fixture.LedgerStore.Load().ChangeSets);
        using var document = JsonDocument.Parse(SetupJson.Serialize(recovered));

        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal("00000000-0000-7000-8000-000000000001", planned.ChangeSetId);
        Assert.Equal(1, adapter.PlanCalls);
    }

    [Fact]
    public void DispatchPlan_WhenRecoveryFailsReturnsCanonicalFailureCorrelationAndStopsPlanning()
    {
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000902");
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => new SetupRecoveryResult(
            SetupRecoveryDisposition.Failed,
            SetupCodes.InterruptedRecoveryFailed,
            recoveredId,
            SetupRecoveryOperation.Apply,
            null));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.RecoveryOperation);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Empty(result.NextActions);
        Assert.Equal(0, adapter.PlanCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.RecoveryRequired)]
    [InlineData(SetupCodes.LedgerCorrupt)]
    [InlineData(SetupCodes.LedgerVersionUnsupported)]
    public void DispatchPlan_WhenRecoveryReturnsUncorrelatedStorageFailurePreservesOnlyFixedCode(string code)
    {
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => new SetupRecoveryResult(
            SetupRecoveryDisposition.Failed,
            code,
            null,
            null,
            null));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal(0, adapter.PlanCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchPlan_WhenRecoveryReturnsMalformedUncorrelatedEvidenceFailsClosed()
    {
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => new SetupRecoveryResult(
            SetupRecoveryDisposition.Failed,
            SetupCodes.LedgerCorrupt,
            null,
            null,
            CreateLedgerRow()));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(0, adapter.PlanCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void ParsedDigitLeadingUnknownAdapterSerializesUnsupportedAdapterOnlyAfterRecovery()
    {
        var recovered = false;
        var parsed = SetupOptions.Parse(["setup", "plan", "--adapter", "1", "--target", "arbitrary-target"]);
        var fixture = DispatcherFixture.Create(
            [],
            _ =>
            {
                recovered = true;
                return NoRecovery();
            });

        var result = fixture.Dispatcher.Dispatch(parsed.Options!);
        var json = SetupJson.Serialize(result);

        Assert.True(recovered);
        Assert.Null(parsed.Code);
        Assert.False(result.Success);
        Assert.Equal(SetupCodes.UnsupportedAdapter, result.Code);
        Assert.Equal("1", result.Adapter);
        Assert.Null(result.ChangeSetId);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("plan", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("unsupported_adapter", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("change_set_id").ValueKind);
    }

    [Fact]
    public void ParsedArbitraryTargetReachesKnownAdapterUnchangedButNeverAppearsInFailureJson()
    {
        const string rawTarget = "../../PRIVATE_TARGET=value with spaces";
        var recovered = false;
        var parsed = SetupOptions.Parse(
            ["setup", "plan", "--adapter", "known-adapter", "--target", rawTarget]);
        var adapter = new RecordingAdapter("known-adapter", request =>
        {
            Assert.True(recovered);
            Assert.Equal(rawTarget, request.SelectedTarget);
            return SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.UnsupportedTarget);
        });
        var fixture = DispatcherFixture.Create(
            adapter,
            _ =>
            {
                recovered = true;
                return NoRecovery();
            });

        var result = fixture.Dispatcher.Dispatch(parsed.Options!);
        var json = SetupJson.Serialize(result);

        Assert.Null(parsed.Code);
        Assert.Equal(1, adapter.PlanCalls);
        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.DoesNotContain(rawTarget, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_TARGET", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchPlan_NormalPersistenceFailureCleansOrphanAndReturnsFixedInternalError()
    {
        const string rawMarker = "PRIVATE_STORAGE_FAILURE";
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        fixture.Platform.InjectFault(
            "checkpoint:after-plan-persisted-before-ledger",
            new IOException(rawMarker));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Empty(fixture.LedgerStore.Load().ChangeSets);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetPlan(Guid.Parse("00000000-0000-7000-8000-000000000001"))));
        Assert.DoesNotContain(rawMarker, SetupJson.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchPlan_AmbiguousLedgerCommitPreservesReferencedPlanAndMapsFailureSafely()
    {
        const string rawMarker = "PRIVATE_AMBIGUOUS_COMMIT";
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        fixture.Platform.InjectAfterEffectFault(
            $"file.move:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}",
            new IOException(rawMarker));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal(SetupCodes.InternalError, result.Code);
        var persisted = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.NotNull(fixture.PlanStore.Load(persisted.ChangeSetId));
        Assert.DoesNotContain(rawMarker, SetupJson.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("adapter")]
    [InlineData("unknown-diagnostic")]
    public void DispatchPlan_UnexpectedFailuresMapToInternalErrorWithoutRawText(string source)
    {
        const string rawMarker = "PRIVATE_UNEXPECTED_FAILURE";
        var adapter = new RecordingAdapter("test-adapter", _ => source switch
        {
            "adapter" => throw new InvalidOperationException(rawMarker),
            "unknown-diagnostic" => SetupPlanResult.Failure<SetupChangePlan>(
                "future_failure",
                ["future_warning"],
                ["future_action"]),
            _ => SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.UnsupportedTarget),
        });
        var fixture = DispatcherFixture.Create(
            adapter,
            _ => source == "recovery"
                ? throw new InvalidOperationException(rawMarker)
                : NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());
        var json = SetupJson.Serialize(result);

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.DoesNotContain(rawMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("future_", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupCommand.Apply)]
    [InlineData(SetupCommand.Rollback)]
    [InlineData(SetupCommand.Status)]
    public void Dispatch_UnimplementedCommandUsesValidatorValidFixedGuardWithoutLocking(SetupCommand command)
    {
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => throw new InvalidOperationException("recovery must not run"));
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000701");
        var options = command switch
        {
            SetupCommand.Apply or SetupCommand.Rollback => new SetupOptions(command, null, null, null, false, changeSetId),
            _ => new SetupOptions(command, "test-adapter", null, null, false, null),
        };

        var result = fixture.Dispatcher.Dispatch(options);

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(command, result.Command);
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation == $"file.lock:{fixture.Paths.Lock}");
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchPlan_ProductionConstructorRunsMandatoryRecoveryBeforeAdapter()
    {
        var adapterCalled = false;
        var adapter = new RecordingAdapter("test-adapter", request =>
        {
            adapterCalled = true;
            return SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)]));
        });
        var fixture = DispatcherFixture.CreateProduction(adapter);

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.True(adapterCalled);
        Assert.Equal(SetupCodes.PlanReady, result.Code);
        Assert.Single(fixture.Platform.Operations, operation => operation == $"file.lock:{fixture.Paths.Lock}");
        Assert.Single(fixture.LedgerStore.Load().ChangeSets);
    }

    private static SetupOptions CreatePlanOptions(
        string adapter = "test-adapter",
        string target = "sample") => new(
        SetupCommand.Plan,
        adapter,
        target,
        "http://127.0.0.1:4320",
        false,
        null);

    private static SetupRecoveryResult NoRecovery() => new(
        SetupRecoveryDisposition.None,
        null,
        null,
        null,
        null);

    private static SetupChangePlan CreatePlan(
        SetupPlanRequest request,
        IReadOnlyList<SetupChangeRecord> records) => new(
        request.ChangeSetId,
        request.Adapter,
        request.SelectedTarget,
        request.CreatedAt,
        request.ToolVersion,
        records);

    private static SetupChangeRecord CreateRecord(
        Guid recordId,
        string targetLabel,
        SetupOperation operation)
    {
        var change = new SetupMemberChangeResult(
            "setting_1",
            operation,
            operation == SetupOperation.NoOp ? "present_same" : "present_different",
            "configured",
            "none",
            false);
        return new SetupChangeRecord(
            recordId,
            SetupTargetKind.File,
            "private://" + targetLabel,
            targetLabel,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "configured",
            [new SetupPrivatePlanMember(change.SettingKey, change.Operation, "configured")],
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                "1.0.0",
                operation,
                SetupEffectiveSource.UserSetting,
                null,
                null,
                null,
                [change]));
    }

    private static SetupChangeRecord CreateGuidanceRecord(Guid recordId)
    {
        var statusGuidance = new SetupStatusGuidance("caller_managed_sample", "dotnet");
        return new SetupChangeRecord(
            recordId,
            SetupTargetKind.Guidance,
            "private://app-sdk-guidance",
            "app-sdk-guidance",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "configured",
            [],
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                false,
                null,
                SetupOperation.NoOp,
                null,
                null,
                null,
                statusGuidance,
                []),
            SetupContractValidator.RehydrateStatusGuidance(statusGuidance));
    }

    private static SetupLedgerChangeSet CreateLedgerRow() => new(
        Guid.Parse("00000000-0000-7000-8000-000000000999"),
        "test-adapter",
        "sample",
        Timestamp,
        Timestamp,
        "1.2.3",
        null,
        SetupChangeSetState.Planned,
        []);

    private sealed class RecordingAdapter(
        string adapterId,
        Func<SetupPlanRequest, SetupPlanResult<SetupChangePlan>> plan) : ISetupAdapter
    {
        public string AdapterId { get; } = adapterId;

        public int PlanCalls { get; private set; }

        public SetupPlanRequest? LastRequest { get; private set; }

        public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request)
        {
            PlanCalls++;
            LastRequest = request;
            return plan(request);
        }

        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan privatePlan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }

    private sealed record DispatcherFixture(
        SetupTestPlatform Platform,
        SetupRuntimePaths Paths,
        SetupPlanStore PlanStore,
        SetupLedgerStore LedgerStore,
        SetupCommandDispatcher Dispatcher)
    {
        public static DispatcherFixture Create(
            ISetupAdapter adapter,
            Func<SetupLock, SetupRecoveryResult> recover) => Create([adapter], recover);

        public static DispatcherFixture Create(
            IEnumerable<ISetupAdapter> adapters,
            Func<SetupLock, SetupRecoveryResult> recover)
        {
            var platform = new SetupTestPlatform(Timestamp);
            var paths = new SetupRuntimePaths(platform);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var dispatcher = new SetupCommandDispatcher(
                platform,
                paths,
                ledgerStore,
                new SetupAdapterRegistry(adapters),
                "1.2.3",
                recover);
            return new DispatcherFixture(platform, paths, planStore, ledgerStore, dispatcher);
        }

        public static DispatcherFixture CreateProduction(ISetupAdapter adapter)
        {
            var platform = new SetupTestPlatform(Timestamp);
            var paths = new SetupRuntimePaths(platform);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var dispatcher = new SetupCommandDispatcher(
                platform,
                paths,
                planStore,
                ledgerStore,
                new SetupTransactionJournalStore(platform, paths),
                new SetupAdapterRegistry([adapter]),
                "1.2.3");
            return new DispatcherFixture(platform, paths, planStore, ledgerStore, dispatcher);
        }
    }
}

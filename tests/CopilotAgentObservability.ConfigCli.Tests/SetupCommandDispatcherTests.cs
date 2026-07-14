using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
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
    public void DispatchPlan_SuccessWithUnknownDiagnosticsFailsValidationBeforePersistence()
    {
        var adapter = new RecordingAdapter("test-adapter", request => SetupPlanResult.Planned(
            CreatePlan(request, [CreateRecord(RecordId, "first-target", SetupOperation.Replace)]),
            ["future_warning"],
            ["future_action"]));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        AssertNoPlannedArtifacts(fixture);
        Assert.DoesNotContain("future_", SetupJson.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchPlan_SuccessWithInvalidPublicTargetFailsValidationBeforePersistenceWithoutRawText()
    {
        const string rawMarker = "PRIVATE_INVALID_SAMPLE";
        var adapter = new RecordingAdapter("test-adapter", request => SetupPlanResult.Planned(
            CreatePlan(request, [CreateInvalidGuidanceRecord(RecordId, rawMarker)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.Equal(SetupCodes.InternalError, result.Code);
        AssertNoPlannedArtifacts(fixture);
        Assert.DoesNotContain(rawMarker, SetupJson.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchPlan_HistoricalManifestFromAdapterFailsExactCurrentValidationWithoutArtifacts()
    {
        using var historical = CreateHistoricalVsCodeManifest();
        var adapter = new RecordingAdapter("test-adapter", request =>
        {
            var record = CreateManifestRecord(request);
            return SetupPlanResult.Planned(CreatePlan(request,
            [record with
            {
                StatusProjection = record.StatusProjection with
                {
                    ExpectedResult = historical.RootElement.Clone(),
                },
            }]));
        });
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        AssertNoPlannedArtifacts(fixture);
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
            new SetupRecoveryResult(
                SetupRecoveryDisposition.Recovered,
                code,
                recoveredId,
                operation,
                CreateRecoveryEvidence(
                    recoveredId,
                    operation == SetupRecoveryOperation.Apply
                        ? SetupChangeSetState.Applied
                        : SetupChangeSetState.RolledBack,
                    code)),
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
            CreateRecoveryEvidence(
                recoveredId,
                SetupChangeSetState.Partial,
                SetupCodes.InterruptedRecoveryFailed)));

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
    public void DispatchPlan_WhenRecoveryRequiresManualActionReturnsPublicCodeWithoutCorrelationAndStopsPlanning()
    {
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000903");
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => new SetupRecoveryResult(
            SetupRecoveryDisposition.Failed,
            SetupCodes.RecoveryRequired,
            recoveredId,
            SetupRecoveryOperation.Apply,
            CreateRecoveryEvidence(
                recoveredId,
                SetupChangeSetState.Partial,
                SetupCodes.InterruptedRecoveryFailed)));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal(0, adapter.PlanCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("none-fields")]
    [InlineData("recovered-missing-effective")]
    [InlineData("recovered-mismatched-id")]
    [InlineData("recovered-wrong-state")]
    [InlineData("recovered-wrong-outcome")]
    [InlineData("failed-interrupted-missing-effective")]
    [InlineData("failed-interrupted-mismatched-id")]
    [InlineData("failed-interrupted-wrong-state")]
    [InlineData("failed-interrupted-wrong-outcome")]
    [InlineData("failed-recovery-required-uncorrelated")]
    [InlineData("failed-recovery-required-mismatched-id")]
    [InlineData("failed-recovery-required-wrong-state")]
    [InlineData("failed-recovery-required-wrong-outcome")]
    [InlineData("failed-ledger-with-evidence")]
    [InlineData("invalid-disposition")]
    [InlineData("unknown-code")]
    public void DispatchPlan_WhenRecoveryEvidenceIsMalformedFailsClosedWithoutPlanning(string variant)
    {
        var adapter = new RecordingAdapter("test-adapter", _ => throw new InvalidOperationException("adapter must not run"));
        var fixture = DispatcherFixture.Create(adapter, _ => CreateMalformedRecovery(variant));

        var result = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal(0, adapter.PlanCalls);
        Assert.DoesNotContain("PRIVATE_RECOVERY_CODE", SetupJson.Serialize(result), StringComparison.Ordinal);
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

    [Fact]
    public void DispatchStatus_ProductionConstructorUsesRealServiceAndReturnsValidatorValidStatus()
    {
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateGuidanceRecord(RecordId)])));
        var fixture = DispatcherFixture.CreateProduction(adapter);
        _ = fixture.Dispatcher.Dispatch(CreatePlanOptions());

        var result = fixture.Dispatcher.Dispatch(CreateStatusOptions("test-adapter"));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.StatusReady, result.Code);
        Assert.Equal(SetupCommand.Status, result.Command);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Single(result.ChangeSets);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchStatus_HistoricalManifestRowSurvivesRealServiceAndSetupJsonSerialization()
    {
        using var historical = CreateHistoricalVsCodeManifest();
        var historicalJson = historical.RootElement.GetRawText();
        var targetPath = "C:\\status-historical-manifest.json";
        var adapter = new RecordingAdapter("test-adapter", request =>
        {
            var record = CreateManifestRecord(request);
            return SetupPlanResult.Planned(CreatePlan(request,
            [record with
            {
                TargetLocation = targetPath,
                BaseStateHash = SetupHash.File(true, Encoding.UTF8.GetBytes("old-file")),
                DesiredState = "new-file",
            }]));
        });
        var fixture = DispatcherFixture.CreateProduction(adapter);
        fixture.Platform.SeedDirectory("C:\\");
        fixture.Platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old-file"));
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions());
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        using (var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var ledger = fixture.LedgerStore.LoadForRecovery();
            var row = Assert.Single(ledger.ChangeSets);
            var target = Assert.Single(row.Targets);
            fixture.LedgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [row with
                {
                    Targets = [target with
                    {
                        StatusProjection = target.StatusProjection with
                        {
                            ExpectedResult = historical.RootElement.Clone(),
                        },
                    }],
                }],
            });
        }
        _ = fixture.Dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        var result = fixture.Dispatcher.Dispatch(CreateStatusOptions("test-adapter"));
        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));
        var expectedResult = serialized.RootElement.GetProperty("change_sets")[0]
            .GetProperty("targets")[0].GetProperty("expected_result");

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.StatusReady, result.Code);
        AssertHistoricalManifest(expectedResult, historicalJson);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchStatus_DelegatesValidatedSameInstanceWithoutOuterLockOrRecovery()
    {
        var recoveryCalls = 0;
        var statusCalls = 0;
        string? observedFilter = null;
        var expected = CreateStatusResult("missing-adapter");
        var fixture = DispatcherFixture.Create(
            [],
            _ =>
            {
                recoveryCalls++;
                return NoRecovery();
            },
            adapter =>
            {
                statusCalls++;
                observedFilter = adapter;
                return expected;
            });

        var result = fixture.Dispatcher.Dispatch(CreateStatusOptions("missing-adapter"));

        Assert.Same(expected, result);
        Assert.Equal("missing-adapter", observedFilter);
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, recoveryCalls);
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation == $"file.lock:{fixture.Paths.Lock}");
    }

    [Fact]
    public void DispatchStatus_PassesNullFilterUnchanged()
    {
        string? observedFilter = "not-null";
        var expected = CreateStatusResult(null);
        var fixture = DispatcherFixture.Create(
            [],
            _ => throw new InvalidOperationException("recovery must not run"),
            adapter =>
            {
                observedFilter = adapter;
                return expected;
            });

        var result = fixture.Dispatcher.Dispatch(CreateStatusOptions(null));

        Assert.Same(expected, result);
        Assert.Null(observedFilter);
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation == $"file.lock:{fixture.Paths.Lock}");
    }

    [Theory]
    [InlineData("malformed", "test-adapter")]
    [InlineData("null", null)]
    [InlineData("exception", "test-adapter")]
    public void DispatchStatus_SafeFallbackForInvalidOrExceptionalServiceOutput(
        string variant,
        string? requestedFilter)
    {
        const string rawMarker = "PRIVATE_STATUS_FAILURE";
        var recoveryCalls = 0;
        var statusCalls = 0;
        string? observedFilter = "not-observed";
        var malformed = CreateStatusResult(requestedFilter) with
        {
            ChangeSets = [null!],
        };
        var fixture = DispatcherFixture.Create(
            [],
            _ =>
            {
                recoveryCalls++;
                return NoRecovery();
            },
            filter =>
            {
                statusCalls++;
                observedFilter = filter;
                return variant switch
                {
                    "malformed" => malformed,
                    "null" => null!,
                    "exception" => throw new InvalidOperationException(rawMarker),
                    _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
                };
            });

        var result = fixture.Dispatcher.Dispatch(CreateStatusOptions(requestedFilter));
        var json = SetupJson.Serialize(result);

        Assert.Equal(1, statusCalls);
        Assert.Equal(0, recoveryCalls);
        Assert.Equal(requestedFilter, observedFilter);
        Assert.False(result.Success);
        Assert.Equal(SetupCommand.Status, result.Command);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Null(result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal(requestedFilter, result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.ChangeSets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.False(result.Truncated);
        SetupContractValidator.Validate(result);
        Assert.DoesNotContain(rawMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Platform.Operations, operation =>
            operation == $"file.lock:{fixture.Paths.Lock}");
    }

    [Fact]
    public void DispatchRollback_AppliedRowDelegatesToCoordinatorAndMapsSuccess()
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var secondRecordId = Guid.Parse("00000000-0000-7000-8000-000000000102");
        var trusted = CreateRollbackChangeSet(
            changeSetId,
            SetupCodes.RollbackSucceeded,
            [CreateOwnedApplyTarget(secondRecordId), CreateOwnedApplyTarget(RecordId)]);
        var recoveryCalls = 0;
        var rollbackCalls = 0;
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ =>
            {
                recoveryCalls++;
                return NoRecovery();
            },
            (_, requestedId) =>
            {
                rollbackCalls++;
                Assert.Equal(changeSetId, requestedId);
                return new SetupRollbackExecutionResult(
                    requestedId,
                    true,
                    SetupCodes.RollbackSucceeded,
                    trusted,
                    null);
            });
        var operationCount = fixture.Platform.Operations.Count;

        var result = dispatcher.Dispatch(CreateRollbackOptions(changeSetId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal(SetupCommand.Rollback, result.Command);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Equal([secondRecordId.ToString("D"), RecordId.ToString("D")], result.Targets.Select(target => target.RecordId));
        Assert.All(result.Targets, target => Assert.False(target.RollbackAvailable));
        Assert.Equal(1, rollbackCalls);
        Assert.Equal(0, recoveryCalls);
        Assert.Single(
            fixture.Platform.Operations.Skip(operationCount),
            operation => operation == $"file.lock:{fixture.Paths.Lock}");
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchRollback_TrustedHistoricalManifestRowSurvivesDispatchAndSetupJsonSerialization()
    {
        using var historical = CreateHistoricalVsCodeManifest();
        var historicalJson = historical.RootElement.GetRawText();
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var target = CreateOwnedApplyTarget(RecordId) with
        {
            TargetKind = SetupTargetKind.Json,
            TargetLabel = "vscode-stable-default-user-settings",
            StatusProjection = CreateApplyStatusProjection(
                SetupOperation.Replace,
                expectedResult: historical.RootElement.Clone()),
        };
        var trusted = CreateRollbackChangeSet(
            changeSetId,
            SetupCodes.RollbackSucceeded,
            [target]);
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => NoRecovery(),
            (_, requestedId) => new SetupRollbackExecutionResult(
                requestedId,
                true,
                SetupCodes.RollbackSucceeded,
                trusted,
                null));

        var result = dispatcher.Dispatch(CreateRollbackOptions(changeSetId));
        using var serialized = JsonDocument.Parse(SetupJson.Serialize(result));
        var expectedResult = serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result");

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        AssertHistoricalManifest(expectedResult, historicalJson);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchRollback_WhenBusyKeepsRequestedCorrelationAndStopsBeforeDelegates()
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveryCalls = 0;
        var rollbackCalls = 0;
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ =>
            {
                recoveryCalls++;
                return NoRecovery();
            },
            (_, _) =>
            {
                rollbackCalls++;
                throw new InvalidOperationException("rollback must not run");
            });
        using var held = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = dispatcher.Dispatch(CreateRollbackOptions(changeSetId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.SetupBusy, result.Code);
        Assert.Equal(SetupCommand.Rollback, result.Command);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal(0, rollbackCalls);
        Assert.Equal(0, recoveryCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.InvalidArguments, false, false)]
    [InlineData(SetupCodes.RollbackSucceeded, true, true)]
    [InlineData(SetupCodes.RollbackNotAvailable, false, true)]
    [InlineData(SetupCodes.RollbackStale, false, true)]
    [InlineData(SetupCodes.UnsafePath, false, true)]
    [InlineData(SetupCodes.PartialRollback, false, true)]
    [InlineData(SetupCodes.RecoveryRequired, false, true)]
    [InlineData(SetupCodes.InternalError, false, true)]
    [InlineData(SetupCodes.InterruptedApplyRecovered, true, false)]
    [InlineData(SetupCodes.InterruptedRollbackRecovered, true, false)]
    [InlineData(SetupCodes.InterruptedRecoveryFailed, false, false)]
    [InlineData(SetupCodes.LedgerCorrupt, false, false)]
    [InlineData(SetupCodes.LedgerVersionUnsupported, false, false)]
    public void DispatchRollback_EveryCoordinatorCodeUsesItsClosedMapping(
        string code,
        bool success,
        bool projectsTrustedRow)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var rollbackCalls = 0;
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, requestedId) =>
            {
                rollbackCalls++;
                return CreateRollbackExecutionForCode(requestedId, code, projectsTrustedRow);
            });

        var result = dispatcher.Dispatch(CreateRollbackOptions(changeSetId));

        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(SetupCommand.Rollback, result.Command);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal(projectsTrustedRow ? "test-adapter" : null, result.Adapter);
        Assert.Equal(projectsTrustedRow ? 1 : 0, result.Targets.Count);
        Assert.All(result.Targets, target => Assert.False(target.RollbackAvailable));
        Assert.Equal(1, rollbackCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.RecoveryRequired)]
    [InlineData(SetupCodes.InternalError)]
    public void DispatchRollback_UntrustedIdentityFailureKeepsCodeAndReturnsEmptyProjection(string code)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, requestedId) => new SetupRollbackExecutionResult(requestedId, false, code, null, null));

        var result = dispatcher.Dispatch(CreateRollbackOptions(changeSetId));

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupRecoveryOperation.Apply, SetupCodes.InterruptedApplyRecovered, SetupChangeSetState.Applied)]
    [InlineData(SetupRecoveryOperation.Rollback, SetupCodes.InterruptedRollbackRecovered, SetupChangeSetState.RolledBack)]
    public void DispatchRollback_ConsumedRecoveryKeepsRequestedAndRecoveredCorrelationWithRerunAction(
        SetupRecoveryOperation operation,
        string code,
        SetupChangeSetState recoveredState)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000711");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, _) => new SetupRollbackExecutionResult(
                requestedId,
                true,
                code,
                null,
                new SetupRecoveryResult(
                    SetupRecoveryDisposition.Recovered,
                    code,
                    recoveredId,
                    operation,
                    CreateRecoveryEvidence(recoveredId, recoveredState, code))));

        var result = dispatcher.Dispatch(CreateRollbackOptions(requestedId));

        Assert.True(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(operation, result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], result.NextActions);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.InterruptedRecoveryFailed, true)]
    [InlineData(SetupCodes.RecoveryRequired, false)]
    public void DispatchRollback_ConsumedFailedRecoveryUsesCanonicalCorrelationRules(
        string code,
        bool keepsRecoveredCorrelation)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000711");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, _) => new SetupRollbackExecutionResult(
                requestedId,
                false,
                code,
                null,
                new SetupRecoveryResult(
                    SetupRecoveryDisposition.Failed,
                    code,
                    recoveredId,
                    SetupRecoveryOperation.Rollback,
                    CreateRecoveryEvidence(
                        recoveredId,
                        SetupChangeSetState.Partial,
                        SetupCodes.InterruptedRecoveryFailed))));

        var result = dispatcher.Dispatch(CreateRollbackOptions(requestedId));

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Equal(keepsRecoveredCorrelation ? recoveredId.ToString("D") : null, result.RecoveredChangeSetId);
        Assert.Equal(keepsRecoveredCorrelation ? SetupRecoveryOperation.Rollback : null, result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.NextActions);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.RollbackNotAvailable)]
    [InlineData(SetupCodes.RollbackStale)]
    [InlineData(SetupCodes.UnsafePath)]
    [InlineData(SetupCodes.PartialRollback)]
    public void DispatchRollback_MalformedDirectEnvelopeWithoutRequiredTrustedRowFailsClosed(string code)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, _) => new SetupRollbackExecutionResult(requestedId, false, code, null, null));

        var result = dispatcher.Dispatch(CreateRollbackOptions(requestedId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("rollback-succeeded-wrong-state")]
    [InlineData("rollback-succeeded-wrong-outcome")]
    [InlineData("partial-rollback-wrong-state")]
    [InlineData("partial-rollback-wrong-outcome")]
    [InlineData("rollback-not-available-wrong-outcome")]
    [InlineData("rollback-stale-wrong-state")]
    [InlineData("rollback-stale-wrong-outcome")]
    [InlineData("unsafe-path-wrong-state")]
    public void DispatchRollback_MalformedDirectEnvelopeWithIncompatibleTrustedRowFailsClosed(string variant)
    {
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, _) => CreateMalformedDirectRollbackExecution(requestedId, variant));

        var result = dispatcher.Dispatch(CreateRollbackOptions(requestedId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("unknown-code")]
    [InlineData("success-mismatch")]
    [InlineData("requested-id-mismatch")]
    [InlineData("direct-interrupted-code")]
    [InlineData("success-without-trusted-row")]
    [InlineData("invalid-arguments-with-trusted-row")]
    [InlineData("ledger-code-with-trusted-row")]
    [InlineData("recovery-code-mismatch")]
    [InlineData("recovery-success-mismatch")]
    [InlineData("malformed-recovery-evidence")]
    public void DispatchRollback_MalformedCoordinatorEnvelopeFailsClosedWithoutRawText(string variant)
    {
        const string rawMarker = "PRIVATE_ROLLBACK_CODE";
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var dispatcher = CreateRollbackDispatcher(
            fixture,
            _ => throw new InvalidOperationException("dispatcher recovery must not run"),
            (_, _) => CreateMalformedRollbackExecution(requestedId, variant, rawMarker));

        var result = dispatcher.Dispatch(CreateRollbackOptions(requestedId));
        var json = SetupJson.Serialize(result);

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(SetupCommand.Rollback, result.Command);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.DoesNotContain(rawMarker, json, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_WhenBusyKeepsRequestedCorrelationWithoutRecoveryOrAdapter()
    {
        var fixture = DispatcherFixture.Create([], _ => throw new InvalidOperationException("recovery must not run"));
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            [],
            _ => throw new InvalidOperationException("recovery must not run"),
            applyCalls);
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        using var held = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.Equal(SetupCodes.SetupBusy, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.Adapter);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_RecoveryKeepsRequestedIdAndCorrelatesRecoveredTransactionBeforeLedgerRead()
    {
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000700");
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var recoveryCalls = 0;
        var operationCount = fixture.Platform.Operations.Count;
        var effective = CreateApplyChangeSet([CreateOwnedApplyTarget(RecordId)]) with
        {
            ChangeSetId = recoveredId,
            OutcomeCode = SetupCodes.InterruptedApplyRecovered,
        };
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            [],
            _ =>
            {
                recoveryCalls++;
                return new SetupRecoveryResult(
                    SetupRecoveryDisposition.Recovered,
                    SetupCodes.InterruptedApplyRecovered,
                    recoveredId,
                    SetupRecoveryOperation.Apply,
                    effective);
            },
            applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(requestedId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], result.NextActions);
        Assert.DoesNotContain(
            fixture.Platform.Operations.Skip(operationCount),
            operation => operation.Contains(fixture.Paths.OwnershipLedger, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_FailedRecoveryKeepsRequestedIdAndProjectsRecoveredCorrelationWithoutAdapter()
    {
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000700");
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var effective = CreateApplyChangeSet([CreateOwnedApplyTarget(RecordId)]) with
        {
            ChangeSetId = recoveredId,
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        };
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            [],
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                recoveredId,
                SetupRecoveryOperation.Apply,
                effective),
            applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(requestedId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Equal(recoveredId.ToString("D"), result.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.ChangeSets);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchApply_MissingLedgerRowReturnsInvalidArgumentsWithoutReadingPlan(bool orphanPlan)
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        if (orphanPlan)
        {
            var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
            requestedId = Guid.Parse(planned.ChangeSetId!);
            using var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
            fixture.LedgerStore.Save(setupLock.Lock!, new SetupOwnershipLedger(1, []));
        }

        var baseline = fixture.Platform.Operations.Count;
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(fixture, [adapter], _ => NoRecovery(), applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(requestedId));
        var operations = fixture.Platform.Operations.Skip(baseline).ToArray();

        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.Adapter);
        Assert.DoesNotContain(operations, operation =>
            operation.Contains(fixture.Paths.GetPlan(requestedId), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("mismatch")]
    public void DispatchApply_RowArtifactFailureReturnsRecoveryRequiredWithPersistedAdapter(string variant)
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var originalPlan = fixture.PlanStore.Load(changeSetId)!;
        using (var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            fixture.PlanStore.Delete(setupLock.Lock!, changeSetId);
            if (variant == "unreadable")
            {
                fixture.Platform.SeedFile(fixture.Paths.GetPlan(changeSetId), Encoding.UTF8.GetBytes("{not-json"));
            }
            else if (variant == "mismatch")
            {
                fixture.PlanStore.Create(setupLock.Lock!, originalPlan with { SelectedTarget = "different-target" });
            }
        }

        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(fixture, [], _ => NoRecovery(), applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal("persisted-adapter", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_NonPlannedRowWithIdentityMismatchReturnsRecoveryRequiredWithoutProjection()
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var originalPlan = fixture.PlanStore.Load(changeSetId)!;
        using (var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var ledger = fixture.LedgerStore.LoadForRecovery();
            var row = Assert.Single(ledger.ChangeSets);
            fixture.PlanStore.Delete(setupLock.Lock!, changeSetId);
            fixture.PlanStore.Create(setupLock.Lock!, originalPlan with { SelectedTarget = "different-target" });
            fixture.LedgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [row with
                {
                    State = SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    UpdatedAt = Timestamp.AddSeconds(1),
                }],
            });
        }

        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(fixture, [], _ => NoRecovery(), applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal("persisted-adapter", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal(1, adapter.PlanCalls);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_ApplicableRowRunsExactlyOneLockAcquisitionAndOneRecoveryPass()
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var recoveryCalls = new CallCounter();
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            [adapter],
            _ =>
            {
                recoveryCalls.Value++;
                return NoRecovery();
            },
            applyCalls);
        var operationCount = fixture.Platform.Operations.Count;

        _ = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.Equal(1, recoveryCalls.Value);
        Assert.Single(
            fixture.Platform.Operations.Skip(operationCount),
            operation => operation == $"file.lock:{fixture.Paths.Lock}");
    }

    [Fact]
    public void DispatchApply_ValidNonPlannedRowProjectsHistoricalLedgerBeforeAdapterResolution()
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(request, [CreateManifestRecord(request)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        using var historical = CreateHistoricalVsCodeManifest();
        var historicalJson = historical.RootElement.GetRawText();
        using (var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var ledger = fixture.LedgerStore.LoadForRecovery();
            var row = Assert.Single(ledger.ChangeSets);
            var target = Assert.Single(row.Targets);
            fixture.LedgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [row with
                {
                    State = SetupChangeSetState.Applied,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    UpdatedAt = Timestamp.AddSeconds(1),
                    Targets = [target with
                    {
                        AppliedStateHash = new string('b', 64),
                        BackupReference = target.RecordId.ToString("D"),
                        OutcomeCode = SetupCodes.ApplySucceeded,
                        RollbackStatus = SetupLedgerRollbackStatus.Pending,
                        StatusProjection = target.StatusProjection with
                        {
                            ExpectedResult = historical.RootElement.Clone(),
                        },
                    }],
                }],
            });
        }

        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(fixture, [], _ => NoRecovery(), applyCalls);
        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));
        var json = SetupJson.Serialize(result);
        using var serialized = JsonDocument.Parse(json);
        var expectedResult = serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result");

        Assert.Equal(SetupCodes.InvalidArguments, result.Code);
        Assert.Equal("persisted-adapter", result.Adapter);
        var projected = Assert.Single(result.Targets);
        Assert.False(projected.RollbackAvailable);
        AssertHistoricalManifest(projected.ExpectedResult!.Value, historicalJson);
        AssertHistoricalManifest(expectedResult, historicalJson);
        Assert.Equal(1, adapter.PlanCalls);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(false, SetupCodes.UnsupportedAdapter)]
    [InlineData(true, SetupCodes.InternalError)]
    public void DispatchApply_ValidPlannedRowPrevalidatesThenResolvesBeforeCoordinatorHandoff(
        bool registered,
        string expectedCode)
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(request, [CreateManifestRecord(request)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        using (var historical = CreateHistoricalVsCodeManifest())
        using (var setupLock = SetupLock.TryAcquire(fixture.Platform, fixture.Paths))
        {
            var ledger = fixture.LedgerStore.LoadForRecovery();
            var row = Assert.Single(ledger.ChangeSets);
            var target = Assert.Single(row.Targets);
            fixture.LedgerStore.Save(setupLock.Lock!, ledger with
            {
                ChangeSets = [row with
                {
                    Targets = [target with
                    {
                        StatusProjection = target.StatusProjection with
                        {
                            ExpectedResult = historical.RootElement.Clone(),
                        },
                    }],
                }],
            });
        }
        var planBytes = fixture.Platform.ReadSeededFile(fixture.Paths.GetPlan(changeSetId));
        var ledgerBytes = fixture.Platform.ReadSeededFile(fixture.Paths.OwnershipLedger);
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            registered ? [adapter] : [],
            _ => NoRecovery(),
            applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal(registered ? null : "persisted-adapter", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.ChangeSets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(registered ? 1 : 0, applyCalls.Value);
        Assert.Equal(planBytes, fixture.Platform.ReadSeededFile(fixture.Paths.GetPlan(changeSetId)));
        Assert.Equal(ledgerBytes, fixture.Platform.ReadSeededFile(fixture.Paths.OwnershipLedger));
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_PlannedRowInvokesCoordinatorAndMapsAppliedResult()
    {
        var adapter = new RecordingAdapter("persisted-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateRecord(RecordId, "first-target", SetupOperation.Replace)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions("persisted-adapter"));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var plannedRow = Assert.Single(fixture.LedgerStore.LoadForRecovery().ChangeSets);
        var plannedTarget = Assert.Single(plannedRow.Targets);
        var applied = plannedRow with
        {
            State = SetupChangeSetState.Applied,
            OutcomeCode = SetupCodes.ApplySucceeded,
            UpdatedAt = Timestamp.AddSeconds(1),
            Targets = [plannedTarget with
            {
                AppliedStateHash = new string('b', 64),
                BackupReference = plannedTarget.RecordId.ToString("D"),
                OutcomeCode = SetupCodes.ApplySucceeded,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
            }],
        };
        var applyCalls = 0;
        var dispatcher = new SetupCommandDispatcher(
            fixture.Platform,
            fixture.Paths,
            fixture.PlanStore,
            fixture.LedgerStore,
            new SetupAdapterRegistry([adapter]),
            "1.2.3",
            _ => NoRecovery(),
            (_, requestedId) =>
            {
                applyCalls++;
                Assert.Equal(changeSetId, requestedId);
                return SetupPlanResult.Success(
                    applied,
                    [],
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]);
            },
            (_, _) => throw new InvalidOperationException("rollback must not run"),
            _ => throw new InvalidOperationException("status must not run"));

        var result = dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.ApplySucceeded, result.Code);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal("persisted-adapter", result.Adapter);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RestartVsCode], result.NextActions);
        Assert.True(Assert.Single(result.Targets).RollbackAvailable);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_NoChangesMapsCoordinatorValueAndDiagnosticsWithoutRollback()
    {
        var context = CreatePlannedApplyFixture(operation: SetupOperation.NoOp);
        var noChanges = context.PlannedRow with
        {
            State = SetupChangeSetState.NoChanges,
            OutcomeCode = SetupCodes.NoChanges,
            UpdatedAt = Timestamp.AddSeconds(1),
        };
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, requestedId) =>
            {
                applyCalls++;
                Assert.Equal(context.ChangeSetId, requestedId);
                return SetupPlanResult.Success(
                    noChanges,
                    [],
                    [SetupCodes.MonitorNotRunning],
                    [SetupCodes.StartLocalMonitor]);
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.NoChanges, result.Code);
        Assert.Equal(context.ChangeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal([SetupCodes.MonitorNotRunning], result.Warnings);
        Assert.Equal([SetupCodes.StartLocalMonitor], result.NextActions);
        Assert.False(Assert.Single(result.Targets).RollbackAvailable);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_SemanticallyEquivalentExpectedResultMapsSuccessfulCoordinatorValue()
    {
        var context = CreatePlannedApplyFixture(recordFactory: CreateManifestRecord);
        var plannedTarget = Assert.Single(context.PlannedRow.Targets);
        var expectedResult = plannedTarget.StatusProjection.ExpectedResult!.Value;
        var source = JsonNode.Parse(expectedResult.GetRawText())!.AsObject();
        var reordered = new JsonObject();
        foreach (var property in source.Reverse())
        {
            reordered.Add(property.Key, property.Value?.DeepClone());
        }

        using var reorderedDocument = JsonDocument.Parse(reordered.ToJsonString());
        var applied = context.PlannedRow with
        {
            State = SetupChangeSetState.Applied,
            OutcomeCode = SetupCodes.ApplySucceeded,
            UpdatedAt = Timestamp.AddSeconds(1),
            Targets = [plannedTarget with
            {
                AppliedStateHash = new string('b', 64),
                BackupReference = plannedTarget.RecordId.ToString("D"),
                OutcomeCode = SetupCodes.ApplySucceeded,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
                StatusProjection = plannedTarget.StatusProjection with
                {
                    ExpectedResult = reorderedDocument.RootElement.Clone(),
                },
            }],
        };
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) => SetupPlanResult.Success(applied, [], [], []));

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.ApplySucceeded, result.Code);
        Assert.True(JsonElement.DeepEquals(expectedResult, Assert.Single(result.Targets).ExpectedResult!.Value));
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("foreign-change-set")]
    [InlineData("same-id-adapter-mismatch")]
    [InlineData("same-id-target-mismatch")]
    [InlineData("same-id-target-label-mismatch")]
    [InlineData("same-id-owning-adapter-mismatch")]
    [InlineData("same-id-restart-requirement-mismatch")]
    [InlineData("same-id-target-tool-version-mismatch")]
    [InlineData("same-id-status-projection-mismatch")]
    [InlineData("same-id-status-operation-mismatch")]
    [InlineData("same-id-status-changes-mismatch")]
    [InlineData("same-id-expected-result-mismatch")]
    [InlineData("same-id-expected-result-null-mismatch")]
    [InlineData("same-id-guidance-null-mismatch")]
    [InlineData("applied-state-outcome-mismatch")]
    [InlineData("no-changes-state-outcome-mismatch")]
    public void DispatchApply_MismatchedSuccessfulCoordinatorCarrierFailsClosed(string variant)
    {
        var noChanges = variant is "no-changes-state-outcome-mismatch" or "same-id-guidance-null-mismatch";
        Func<SetupPlanRequest, SetupChangeRecord>? recordFactory = variant switch
        {
            "same-id-expected-result-mismatch" => CreateManifestRecord,
            "same-id-guidance-null-mismatch" => _ => CreateGuidanceRecord(RecordId),
            _ => null,
        };
        var context = CreatePlannedApplyFixture(
            operation: noChanges ? SetupOperation.NoOp : SetupOperation.Replace,
            recordFactory: recordFactory);
        var plannedTarget = Assert.Single(context.PlannedRow.Targets);
        var successful = context.PlannedRow with
        {
            State = noChanges ? SetupChangeSetState.NoChanges : SetupChangeSetState.Applied,
            OutcomeCode = noChanges ? SetupCodes.NoChanges : SetupCodes.ApplySucceeded,
            UpdatedAt = Timestamp.AddSeconds(1),
            Targets = noChanges
                ? [plannedTarget with { OutcomeCode = SetupCodes.NoChanges }]
                : [plannedTarget with
                {
                    AppliedStateHash = new string('b', 64),
                    BackupReference = plannedTarget.RecordId.ToString("D"),
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                }],
        };
        var foreignRecordId = Guid.Parse("00000000-0000-7000-8000-000000000712");
        using var historicalManifest = CreateHistoricalVsCodeManifest();
        using var jsonNull = JsonDocument.Parse("null");
        successful = variant switch
        {
            "foreign-change-set" => successful with
            {
                ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000711"),
            },
            "same-id-adapter-mismatch" => successful with
            {
                Adapter = "foreign-adapter",
                Targets = [Assert.Single(successful.Targets) with { OwningAdapter = "foreign-adapter" }],
            },
            "same-id-target-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    RecordId = foreignRecordId,
                    TargetLabel = "foreign-target",
                    BackupReference = foreignRecordId.ToString("D"),
                }],
            },
            "same-id-target-label-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with { TargetLabel = "foreign-target" }],
            },
            "same-id-owning-adapter-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with { OwningAdapter = "foreign-adapter" }],
            },
            "same-id-restart-requirement-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    RestartRequirement = SetupRestartRequirement.RestartVsCode,
                }],
            },
            "same-id-target-tool-version-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with { ToolVersion = "9.9.9" }],
            },
            "same-id-status-projection-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with
                    {
                        DetectedVersion = "2.0.0",
                        EffectiveSource = SetupEffectiveSource.Environment,
                        Endpoint = "http://127.0.0.1:4318",
                    },
                }],
            },
            "same-id-status-operation-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with
                    {
                        Operation = SetupOperation.Add,
                        Changes = [Assert.Single(successful.Targets[0].StatusProjection.Changes) with
                        {
                            Operation = SetupOperation.Add,
                        }],
                    },
                }],
            },
            "same-id-status-changes-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with
                    {
                        Changes = [Assert.Single(successful.Targets[0].StatusProjection.Changes) with
                        {
                            PreviousState = "foreign_previous",
                            NewState = "foreign_new",
                            Conflict = "foreign_conflict",
                            Managed = true,
                        }],
                    },
                }],
            },
            "same-id-expected-result-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with
                    {
                        ExpectedResult = historicalManifest.RootElement.Clone(),
                    },
                }],
            },
            "same-id-expected-result-null-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with
                    {
                        ExpectedResult = jsonNull.RootElement.Clone(),
                    },
                }],
            },
            "same-id-guidance-null-mismatch" => successful with
            {
                Targets = [Assert.Single(successful.Targets) with
                {
                    StatusProjection = Assert.Single(successful.Targets).StatusProjection with { Guidance = null },
                }],
            },
            "applied-state-outcome-mismatch" => successful with { OutcomeCode = SetupCodes.NoChanges },
            "no-changes-state-outcome-mismatch" => successful with { OutcomeCode = SetupCodes.ApplySucceeded },
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        if (variant is "foreign-change-set" or "same-id-adapter-mismatch" or "same-id-target-mismatch" or
            "same-id-target-label-mismatch" or "same-id-restart-requirement-mismatch" or
            "same-id-status-projection-mismatch" or "same-id-status-changes-mismatch" or
            "same-id-expected-result-mismatch" or "same-id-expected-result-null-mismatch")
        {
            SetupStorageValidation.ValidateLedger(new SetupOwnershipLedger(1, [successful]));
        }

        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) =>
            {
                applyCalls++;
                return SetupPlanResult.Success(
                    successful,
                    [],
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]);
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(context.ChangeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.ChangeSets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.TargetNotInstalled)]
    [InlineData(SetupCodes.UnsupportedVersion)]
    [InlineData(SetupCodes.ManagedPolicyConflict)]
    [InlineData(SetupCodes.EnvironmentOverrideConflict)]
    [InlineData(SetupCodes.MalformedSettings)]
    [InlineData(SetupCodes.PermissionDenied)]
    [InlineData(SetupCodes.UnsafePath)]
    [InlineData(SetupCodes.StalePlan)]
    [InlineData(SetupCodes.PortOwnedByForeignProcess)]
    [InlineData(SetupCodes.PartialApply)]
    [InlineData(SetupCodes.RecoveryRequired)]
    [InlineData(SetupCodes.InternalError)]
    public void DispatchApply_TypedCoordinatorFailureMapsCodeDiagnosticsAndReloadedTargets(string code)
    {
        var context = CreatePlannedApplyFixture();
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, requestedId) =>
            {
                applyCalls++;
                Assert.Equal(context.ChangeSetId, requestedId);
                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    code,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(context.ChangeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Equal("persisted-adapter", result.Adapter);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RestartVsCode], result.NextActions);
        Assert.False(Assert.Single(result.Targets).RollbackAvailable);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.SetupBusy)]
    [InlineData(SetupCodes.InvalidArguments)]
    [InlineData(SetupCodes.LedgerCorrupt)]
    [InlineData(SetupCodes.LedgerVersionUnsupported)]
    public void DispatchApply_OutOfUnionTypedCoordinatorFailureReturnsSafeInternalError(string code)
    {
        var context = CreatePlannedApplyFixture();
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) =>
            {
                applyCalls++;
                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    code,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal(context.ChangeSetId.ToString("D"), result.ChangeSetId);
        Assert.Null(result.RecoveredChangeSetId);
        Assert.Null(result.RecoveryOperation);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_TypedFailureProjectsPersistedPostFailureRowWithoutStaleSnapshotFallback()
    {
        var context = CreatePlannedApplyFixture();
        var plannedTarget = Assert.Single(context.PlannedRow.Targets);
        var postFailure = context.PlannedRow with
        {
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.PartialApply,
            UpdatedAt = Timestamp.AddSeconds(1),
            Targets = [plannedTarget with
            {
                AppliedStateHash = new string('b', 64),
                BackupReference = plannedTarget.RecordId.ToString("D"),
                OutcomeCode = SetupCodes.PartialApply,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
                StatusProjection = plannedTarget.StatusProjection with
                {
                    Detected = false,
                    DetectedVersion = null,
                },
            }],
        };
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (setupLock, _) =>
            {
                context.Fixture.LedgerStore.Save(
                    setupLock,
                    new SetupOwnershipLedger(1, [postFailure]));
                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    SetupCodes.PartialApply,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.Equal(SetupCodes.PartialApply, result.Code);
        var target = Assert.Single(result.Targets);
        Assert.False(target.Detected);
        Assert.Null(target.DetectedVersion);
        Assert.False(target.RollbackAvailable);
        var persisted = Assert.Single(context.Fixture.LedgerStore.LoadForRecovery().ChangeSets);
        Assert.Equal(SetupChangeSetState.Partial, persisted.State);
        Assert.Equal(SetupCodes.PartialApply, persisted.OutcomeCode);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_PostFailureReloadWithReboundIdentityReturnsEmptyTargets()
    {
        var context = CreatePlannedApplyFixture();
        var plannedTarget = Assert.Single(context.PlannedRow.Targets);
        var reboundRecordId = Guid.Parse("00000000-0000-7000-8000-000000000712");
        var rebound = context.PlannedRow with
        {
            Adapter = "rebound-adapter",
            SelectedTarget = "rebound-target",
            CreatedAt = Timestamp.AddSeconds(1),
            UpdatedAt = Timestamp.AddSeconds(2),
            ToolVersion = "9.9.9",
            State = SetupChangeSetState.Partial,
            OutcomeCode = SetupCodes.PartialApply,
            Targets = [plannedTarget with
            {
                RecordId = reboundRecordId,
                TargetLabel = "rebound-target",
                OwningAdapter = "rebound-adapter",
                AppliedStateHash = new string('b', 64),
                BackupReference = reboundRecordId.ToString("D"),
                OutcomeCode = SetupCodes.PartialApply,
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
                ToolVersion = "9.9.9",
                StatusProjection = plannedTarget.StatusProjection with
                {
                    Detected = false,
                    DetectedVersion = null,
                },
            }],
        };
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (setupLock, _) =>
            {
                context.Fixture.LedgerStore.Save(
                    setupLock,
                    new SetupOwnershipLedger(1, [rebound]));
                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    SetupCodes.PartialApply,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));
        var json = SetupJson.Serialize(result);

        Assert.Equal(SetupCodes.PartialApply, result.Code);
        Assert.Equal("persisted-adapter", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RestartVsCode], result.NextActions);
        Assert.DoesNotContain("rebound", json, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    public void DispatchApply_PostFailureReloadWithoutRequestedRowReturnsEmptyTargets(string variant)
    {
        var context = CreatePlannedApplyFixture();
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (setupLock, _) =>
            {
                applyCalls++;
                if (variant == "missing")
                {
                    context.Fixture.LedgerStore.Save(setupLock, new SetupOwnershipLedger(1, []));
                }
                else
                {
                    context.Fixture.Platform.SeedFile(
                        context.Fixture.Paths.OwnershipLedger,
                        Encoding.UTF8.GetBytes("{PRIVATE_UNREADABLE_LEDGER"));
                }

                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    SetupCodes.PartialApply,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));
        var json = SetupJson.Serialize(result);

        Assert.Equal(SetupCodes.PartialApply, result.Code);
        Assert.Empty(result.Targets);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RestartVsCode], result.NextActions);
        Assert.Equal(1, applyCalls);
        Assert.DoesNotContain("PRIVATE_UNREADABLE_LEDGER", json, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
    }

    [Theory]
    [InlineData(SetupCodes.UnsupportedAdapter)]
    [InlineData(SetupCodes.UnsupportedTarget)]
    public void DispatchApply_ExceptionalCoordinatorFailureRetainsAdapterWithEmptyPayloads(string code)
    {
        var context = CreatePlannedApplyFixture("github-copilot");
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) =>
            {
                applyCalls++;
                throw new SetupApplyException(SetupPlanResult.Failure<SetupLedgerChangeSet>(
                    code,
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]));
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal("github-copilot", result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_UnexpectedCoordinatorExceptionReturnsSafeInternalError()
    {
        const string rawMarker = "PRIVATE_COORDINATOR_FAILURE";
        var context = CreatePlannedApplyFixture();
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) =>
            {
                applyCalls++;
                throw new InvalidOperationException(rawMarker);
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));
        var json = SetupJson.Serialize(result);

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(1, applyCalls);
        Assert.DoesNotContain(rawMarker, json, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_UnexpectedCoordinatorSuccessStateReturnsSafeInternalError()
    {
        var context = CreatePlannedApplyFixture();
        var applyCalls = 0;
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            (_, _) =>
            {
                applyCalls++;
                return SetupPlanResult.Success(
                    context.PlannedRow with
                    {
                        State = SetupChangeSetState.Restored,
                        OutcomeCode = SetupCodes.InternalError,
                    },
                    [],
                    [SetupCodes.ManagedPolicyUnverified],
                    [SetupCodes.RestartVsCode]);
            });

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(1, applyCalls);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_MandatoryRecoveryRequiredStopsBeforeCoordinatorWithEmptyTargets()
    {
        var requestedId = Guid.Parse("00000000-0000-7000-8000-000000000710");
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000711");
        var fixture = DispatcherFixture.Create([], _ => NoRecovery());
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            fixture,
            [],
            _ => new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                recoveredId,
                SetupRecoveryOperation.Apply,
                CreateRecoveryEvidence(
                    recoveredId,
                    SetupChangeSetState.Partial,
                    SetupCodes.InterruptedRecoveryFailed)),
            applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(requestedId));

        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal(requestedId.ToString("D"), result.ChangeSetId);
        Assert.Empty(result.Targets);
        Assert.Equal(0, applyCalls.Value);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchApply_UnreadableLedgerBeforeCoordinatorReturnsStorageFailureWithEmptyTargets()
    {
        const string rawMarker = "PRIVATE_LEDGER_FAILURE";
        var context = CreatePlannedApplyFixture();
        context.Fixture.Platform.SeedFile(
            context.Fixture.Paths.OwnershipLedger,
            Encoding.UTF8.GetBytes("{" + rawMarker));
        var applyCalls = new CallCounter();
        var dispatcher = CreateApplyDispatcher(
            context.Fixture,
            [context.Adapter],
            _ => NoRecovery(),
            applyCalls);

        var result = dispatcher.Dispatch(CreateApplyOptions(context.ChangeSetId));
        var json = SetupJson.Serialize(result);

        Assert.Equal(SetupCodes.LedgerCorrupt, result.Code);
        Assert.Null(result.Adapter);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Equal(0, applyCalls.Value);
        Assert.DoesNotContain(rawMarker, json, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void ProjectApplyTargets_CopiesImmutableLedgerProjectionInOriginalOrder()
    {
        var secondRecordId = Guid.Parse("00000000-0000-7000-8000-000000000702");
        var second = CreateOwnedApplyTarget(secondRecordId) with
        {
            TargetLabel = "second-target",
            RestartRequirement = SetupRestartRequirement.RestartTerminalSession,
            StatusProjection = CreateApplyStatusProjection(
                SetupOperation.Replace,
                detected: false,
                detectedVersion: null,
                effectiveSource: SetupEffectiveSource.Environment,
                endpoint: null),
        };
        var first = CreateOwnedApplyTarget(RecordId) with
        {
            TargetLabel = "first-target",
            StatusProjection = CreateApplyStatusProjection(
                SetupOperation.Replace,
                detected: true,
                detectedVersion: "1.2.3",
                effectiveSource: SetupEffectiveSource.UserSetting,
                endpoint: "http://127.0.0.1:4320"),
        };
        var changeSet = CreateApplyChangeSet([second, first]);

        var targets = SetupCommandDispatcher.ProjectApplyTargets(changeSet, SetupCodes.ApplySucceeded);

        Assert.Equal([secondRecordId.ToString("D"), RecordId.ToString("D")],
            targets.Select(target => target.RecordId));
        var projectedSecond = targets[0];
        Assert.Equal(second.TargetKind, projectedSecond.TargetKind);
        Assert.Equal(second.TargetLabel, projectedSecond.TargetLabel);
        Assert.Equal(second.StatusProjection.Detected, projectedSecond.Detected);
        Assert.Equal(second.StatusProjection.DetectedVersion, projectedSecond.DetectedVersion);
        Assert.Equal(second.StatusProjection.Operation, projectedSecond.Operation);
        Assert.Equal(second.StatusProjection.EffectiveSource, projectedSecond.EffectiveSource);
        Assert.Equal(second.RestartRequirement, projectedSecond.RestartRequirement);
        Assert.Equal(second.StatusProjection.Endpoint, projectedSecond.Endpoint);
        Assert.Null(projectedSecond.ReferenceState);
        Assert.Null(projectedSecond.CurrentState);
        Assert.True(projectedSecond.RollbackAvailable);
        Assert.Equivalent(second.StatusProjection.Changes, projectedSecond.Changes, strict: true);
    }

    [Theory]
    [InlineData("complete", SetupCodes.ApplySucceeded, true)]
    [InlineData("missing-applied-hash", SetupCodes.ApplySucceeded, false)]
    [InlineData("missing-backup", SetupCodes.ApplySucceeded, false)]
    [InlineData("mismatched-backup", SetupCodes.ApplySucceeded, false)]
    [InlineData("uppercase-backup", SetupCodes.ApplySucceeded, false)]
    [InlineData("not-available", SetupCodes.ApplySucceeded, false)]
    [InlineData("succeeded", SetupCodes.ApplySucceeded, false)]
    [InlineData("failed", SetupCodes.ApplySucceeded, false)]
    [InlineData("stale", SetupCodes.ApplySucceeded, false)]
    [InlineData("all-no-op", SetupCodes.ApplySucceeded, false)]
    [InlineData("guidance", SetupCodes.ApplySucceeded, false)]
    [InlineData("complete", SetupCodes.NoChanges, false)]
    [InlineData("complete", SetupCodes.PartialApply, false)]
    [InlineData("complete", SetupCodes.InternalError, false)]
    public void ProjectApplyTargets_ReportsRollbackOnlyForCompleteChangedOwnership(
        string variant,
        string code,
        bool expected)
    {
        var alphabeticRecordId = Guid.Parse("abcdefab-cdef-7abc-8def-abcdefabcdef");
        var target = variant switch
        {
            "missing-applied-hash" => CreateOwnedApplyTarget(RecordId) with { AppliedStateHash = null },
            "missing-backup" => CreateOwnedApplyTarget(RecordId) with { BackupReference = null },
            "mismatched-backup" => CreateOwnedApplyTarget(RecordId) with
            {
                BackupReference = "00000000-0000-7000-8000-000000000799",
            },
            "uppercase-backup" => CreateOwnedApplyTarget(alphabeticRecordId) with
            {
                BackupReference = alphabeticRecordId.ToString("D").ToUpperInvariant(),
            },
            "not-available" => CreateOwnedApplyTarget(RecordId) with
            {
                RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
            },
            "succeeded" => CreateOwnedApplyTarget(RecordId) with
            {
                RollbackStatus = SetupLedgerRollbackStatus.Succeeded,
            },
            "failed" => CreateOwnedApplyTarget(RecordId) with
            {
                RollbackStatus = SetupLedgerRollbackStatus.Failed,
            },
            "stale" => CreateOwnedApplyTarget(RecordId) with
            {
                RollbackStatus = SetupLedgerRollbackStatus.Stale,
            },
            "all-no-op" => CreateAllNoOpApplyTarget(RecordId),
            "guidance" => CreateApplyGuidanceTarget(RecordId),
            _ => CreateOwnedApplyTarget(RecordId),
        };

        var projected = Assert.Single(SetupCommandDispatcher.ProjectApplyTargets(
            CreateApplyChangeSet([target]),
            code));

        Assert.Equal(expected, projected.RollbackAvailable);
    }

    [Fact]
    public void ProjectApplyTargets_PreservesHistoricalManifestThroughValidatedResultAndJson()
    {
        var manifestDocument = CreateHistoricalVsCodeManifest();
        var historicalJson = manifestDocument.RootElement.GetRawText();
        var target = CreateOwnedApplyTarget(RecordId) with
        {
            TargetKind = SetupTargetKind.Json,
            TargetLabel = "vscode-stable-default-user-settings",
            StatusProjection = CreateApplyStatusProjection(
                SetupOperation.Replace,
                expectedResult: manifestDocument.RootElement),
        };
        var changeSet = CreateApplyChangeSet([target]);
        var targets = SetupCommandDispatcher.ProjectApplyTargets(changeSet, SetupCodes.ApplySucceeded);
        manifestDocument.Dispose();
        var result = new SetupCommandResult(
            SetupCommand.Apply,
            true,
            SetupCodes.ApplySucceeded,
            changeSet.ChangeSetId.ToString("D"),
            null,
            null,
            changeSet.Adapter,
            targets,
            [],
            [],
            [],
            false);

        SetupContractValidator.Validate(result);
        var json = SetupJson.Serialize(result);
        using var serialized = JsonDocument.Parse(json);
        var expectedResult = serialized.RootElement.GetProperty("targets")[0].GetProperty("expected_result");

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(historicalJson),
            JsonNode.Parse(expectedResult.GetRawText())));
        Assert.Equal("planned", expectedResult.GetProperty("support_status").GetString());
        Assert.Equal("preview", expectedResult.GetProperty("stability").GetString());
        Assert.DoesNotContain("\"support_status\":\"active\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectApplyTargets_RehydratesOnlyFixedGuidanceSample()
    {
        var source = CreateApplyGuidanceTarget(RecordId);
        var changeSet = CreateApplyChangeSet([source]);

        var projected = Assert.Single(SetupCommandDispatcher.ProjectApplyTargets(
            changeSet,
            SetupCodes.NoChanges));
        var result = new SetupCommandResult(
            SetupCommand.Apply,
            true,
            SetupCodes.NoChanges,
            changeSet.ChangeSetId.ToString("D"),
            null,
            null,
            changeSet.Adapter,
            [projected],
            [],
            [],
            [],
            false);

        var expected = SetupContractValidator.RehydrateStatusGuidance(source.StatusProjection.Guidance!);
        Assert.Equal(expected, projected.Guidance);
        Assert.Empty(projected.Changes);
        Assert.Null(projected.ExpectedResult);
        Assert.False(projected.RollbackAvailable);
        Assert.DoesNotContain("C:\\", projected.Guidance!.Sample, StringComparison.Ordinal);
        SetupContractValidator.Validate(result);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(
            expected.Sample,
            document.RootElement.GetProperty("targets")[0].GetProperty("guidance").GetProperty("sample").GetString());
    }

    [Fact]
    public void ProjectApplyTargets_SnapshotsCollectionsWithoutMutatingLedgerInput()
    {
        var sourceChanges = new List<SetupMemberChangeResult>
        {
            CreateApplyChange(SetupOperation.Replace),
        };
        var sourceTarget = CreateOwnedApplyTarget(RecordId) with
        {
            StatusProjection = CreateApplyStatusProjection(SetupOperation.Replace) with
            {
                Changes = sourceChanges,
            },
        };
        var sourceTargets = new List<SetupLedgerTarget> { sourceTarget };
        var changeSet = CreateApplyChangeSet(sourceTargets);

        var projected = SetupCommandDispatcher.ProjectApplyTargets(changeSet, SetupCodes.ApplySucceeded);

        Assert.Single(changeSet.Targets);
        Assert.Equal(SetupOperation.Replace, changeSet.Targets[0].StatusProjection.Changes[0].Operation);
        sourceChanges[0] = CreateApplyChange(SetupOperation.Remove);
        sourceTargets.Clear();
        Assert.Single(projected);
        Assert.Equal(SetupOperation.Replace, projected[0].Changes[0].Operation);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SetupTargetResult>)projected)[0] = projected[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SetupMemberChangeResult>)projected[0].Changes)[0] = projected[0].Changes[0]);
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

    [Fact]
    public void DispatchApply_ProductionConstructorInvokesRealCoordinatorForNoChanges()
    {
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateGuidanceRecord(RecordId)])));
        var fixture = DispatcherFixture.CreateProduction(adapter);
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions());
        var changeSetId = Guid.Parse(planned.ChangeSetId!);

        var result = fixture.Dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.NoChanges, result.Code);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.False(Assert.Single(result.Targets).RollbackAvailable);
        var persisted = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupChangeSetState.NoChanges, persisted.State);
        Assert.Equal(SetupCodes.NoChanges, persisted.OutcomeCode);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchRollback_ProductionConstructorInvokesRealCoordinatorForNoChangesRow()
    {
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateGuidanceRecord(RecordId)])));
        var fixture = DispatcherFixture.CreateProduction(adapter);
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions());
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var applied = fixture.Dispatcher.Dispatch(CreateApplyOptions(changeSetId));

        var result = fixture.Dispatcher.Dispatch(CreateRollbackOptions(changeSetId));

        Assert.Equal(SetupCodes.NoChanges, applied.Code);
        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, result.Code);
        Assert.Equal(SetupCommand.Rollback, result.Command);
        Assert.Equal(changeSetId.ToString("D"), result.ChangeSetId);
        Assert.Equal("test-adapter", result.Adapter);
        Assert.False(Assert.Single(result.Targets).RollbackAvailable);
        var persisted = Assert.Single(fixture.LedgerStore.Load().ChangeSets);
        Assert.Equal(SetupCodes.RollbackNotAvailable, persisted.OutcomeCode);
        SetupContractValidator.Validate(result);
    }

    [Fact]
    public void DispatchPlan_ProductionConstructorReturnsActualInterruptedApplyRecoveryEvidence()
    {
        var platform = new SetupTestPlatform(Timestamp);
        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
        var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
        var journalStore = new SetupTransactionJournalStore(platform, paths);
        var targetPath = Path.Combine(platform.LocalApplicationData, "dispatcher-settings.json");
        platform.SeedDirectory("C:\\");
        platform.SeedDirectory(platform.LocalApplicationData);
        platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("old-file"));
        var adapter = new RecordingAdapter("test-adapter", request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [CreateApplicableFileRecord(RecordId, targetPath)])));
        var registry = new SetupAdapterRegistry([adapter]);
        var dispatcher = new SetupCommandDispatcher(
            platform,
            paths,
            planStore,
            ledgerStore,
            journalStore,
            registry,
            "1.2.3");
        var planned = dispatcher.Dispatch(CreatePlanOptions());
        var plannedId = Guid.Parse(planned.ChangeSetId!);
        platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterCommitBeforeLedger}",
            new IOException("PRIVATE_APPLY_INTERRUPTION"));
        using (var applyLock = SetupLock.TryAcquire(platform, paths))
        {
            var apply = new SetupApplyCoordinator(
                platform,
                paths,
                planStore,
                ledgerStore,
                journalStore,
                registry);

            var exception = Assert.Throws<SetupApplyException>(() =>
                apply.Apply(applyLock.Lock!, plannedId));

            Assert.Equal(SetupCodes.InternalError, exception.Code);
        }

        var recovered = dispatcher.Dispatch(CreatePlanOptions());
        var json = SetupJson.Serialize(recovered);

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedApplyRecovered, recovered.Code);
        Assert.Null(recovered.ChangeSetId);
        Assert.Equal(plannedId.ToString("D"), recovered.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Apply, recovered.RecoveryOperation);
        Assert.Equal([SetupCodes.RerunRequestedSetupCommand], recovered.NextActions);
        Assert.Equal(1, adapter.PlanCalls);
        Assert.Equal("new-file", Encoding.UTF8.GetString(platform.ReadSeededFile(targetPath)));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("change_set_id").ValueKind);
        Assert.Equal(plannedId.ToString("D"), document.RootElement.GetProperty("recovered_change_set_id").GetString());
        Assert.Equal("apply", document.RootElement.GetProperty("recovery_operation").GetString());
        Assert.Equal(
            SetupCodes.RerunRequestedSetupCommand,
            document.RootElement.GetProperty("next_actions")[0].GetString());
        Assert.DoesNotContain("PRIVATE_APPLY_INTERRUPTION", json, StringComparison.Ordinal);
        SetupContractValidator.Validate(recovered);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("paths")]
    [InlineData("planStore")]
    [InlineData("ledgerStore")]
    [InlineData("journalStore")]
    [InlineData("adapterRegistry")]
    [InlineData("toolVersion")]
    public void ProductionConstructor_GuardsEveryDependencyBeforeCapturingRecovery(string dependency)
    {
        var platform = new SetupTestPlatform(Timestamp);
        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
        var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
        var journalStore = new SetupTransactionJournalStore(platform, paths);
        var registry = new SetupAdapterRegistry([]);

        var exception = Assert.Throws<ArgumentNullException>(() => new SetupCommandDispatcher(
            dependency == "platform" ? null! : platform,
            dependency == "paths" ? null! : paths,
            dependency == "planStore" ? null! : planStore,
            dependency == "ledgerStore" ? null! : ledgerStore,
            dependency == "journalStore" ? null! : journalStore,
            dependency == "adapterRegistry" ? null! : registry,
            dependency == "toolVersion" ? null! : "1.2.3"));

        Assert.Equal(dependency, exception.ParamName);
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

    private static SetupOptions CreateApplyOptions(Guid changeSetId) => new(
        SetupCommand.Apply,
        null,
        null,
        null,
        false,
        changeSetId);

    private static SetupOptions CreateRollbackOptions(Guid changeSetId) => new(
        SetupCommand.Rollback,
        null,
        null,
        null,
        false,
        changeSetId);

    private static SetupOptions CreateStatusOptions(string? adapter) => new(
        SetupCommand.Status,
        adapter,
        null,
        null,
        false,
        null);

    private static SetupCommandResult CreateStatusResult(string? adapter) => new(
        SetupCommand.Status,
        true,
        SetupCodes.StatusReady,
        null,
        null,
        null,
        adapter,
        [],
        [],
        [],
        [],
        false);

    private static SetupCommandDispatcher CreateApplyDispatcher(
        DispatcherFixture fixture,
        IEnumerable<ISetupAdapter> adapters,
        Func<SetupLock, SetupRecoveryResult> recover,
        CallCounter applyCalls) => new(
        fixture.Platform,
        fixture.Paths,
        fixture.PlanStore,
        fixture.LedgerStore,
        new SetupAdapterRegistry(adapters),
        "1.2.3",
        recover,
        (_, _) =>
        {
            applyCalls.Value++;
            throw new InvalidOperationException("apply must not run");
        },
        (_, _) => throw new InvalidOperationException("rollback must not run"),
        _ => throw new InvalidOperationException("status must not run"));

    private static SetupCommandDispatcher CreateApplyDispatcher(
        DispatcherFixture fixture,
        IEnumerable<ISetupAdapter> adapters,
        Func<SetupLock, SetupRecoveryResult> recover,
        Func<SetupLock, Guid, SetupPlanSuccess<SetupLedgerChangeSet>> apply) => new(
        fixture.Platform,
        fixture.Paths,
        fixture.PlanStore,
        fixture.LedgerStore,
        new SetupAdapterRegistry(adapters),
        "1.2.3",
        recover,
        apply,
        (_, _) => throw new InvalidOperationException("rollback must not run"),
        _ => throw new InvalidOperationException("status must not run"));

    private static SetupCommandDispatcher CreateRollbackDispatcher(
        DispatcherFixture fixture,
        Func<SetupLock, SetupRecoveryResult> recover,
        Func<SetupLock, Guid, SetupRollbackExecutionResult> rollback) => new(
        fixture.Platform,
        fixture.Paths,
        fixture.PlanStore,
        fixture.LedgerStore,
        new SetupAdapterRegistry([]),
        "1.2.3",
        recover,
        (_, _) => throw new InvalidOperationException("apply must not run"),
        rollback,
        _ => throw new InvalidOperationException("status must not run"));

    private static (
        DispatcherFixture Fixture,
        RecordingAdapter Adapter,
        Guid ChangeSetId,
        SetupLedgerChangeSet PlannedRow) CreatePlannedApplyFixture(
            string adapterId = "persisted-adapter",
            SetupOperation operation = SetupOperation.Replace,
            Func<SetupPlanRequest, SetupChangeRecord>? recordFactory = null)
    {
        var adapter = new RecordingAdapter(adapterId, request =>
            SetupPlanResult.Planned(CreatePlan(
                request,
                [recordFactory?.Invoke(request) ?? CreateRecord(RecordId, "first-target", operation)])));
        var fixture = DispatcherFixture.Create(adapter, _ => NoRecovery());
        var planned = fixture.Dispatcher.Dispatch(CreatePlanOptions(adapterId));
        var changeSetId = Guid.Parse(planned.ChangeSetId!);
        var plannedRow = Assert.Single(fixture.LedgerStore.LoadForRecovery().ChangeSets);
        return (fixture, adapter, changeSetId, plannedRow);
    }

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

    private static SetupLedgerChangeSet CreateApplyChangeSet(IReadOnlyList<SetupLedgerTarget> targets) => new(
        Guid.Parse("00000000-0000-7000-8000-000000000700"),
        "test-adapter",
        "sample",
        Timestamp,
        Timestamp,
        "1.2.3",
        SetupCodes.ApplySucceeded,
        SetupChangeSetState.Applied,
        targets);

    private static SetupLedgerChangeSet CreateRollbackChangeSet(
        Guid changeSetId,
        string code,
        IReadOnlyList<SetupLedgerTarget>? targets = null) => CreateApplyChangeSet(
        targets ?? [CreateOwnedApplyTarget(RecordId)]) with
        {
            ChangeSetId = changeSetId,
            OutcomeCode = code == SetupCodes.UnsafePath ? SetupCodes.ApplySucceeded : code,
            State = code switch
            {
                SetupCodes.RollbackSucceeded => SetupChangeSetState.RolledBack,
                SetupCodes.PartialRollback => SetupChangeSetState.Partial,
                _ => SetupChangeSetState.Applied,
            },
        };

    private static SetupRollbackExecutionResult CreateMalformedDirectRollbackExecution(
        Guid requestedId,
        string variant)
    {
        return variant switch
        {
            "rollback-succeeded-wrong-state" => new(
                requestedId,
                true,
                SetupCodes.RollbackSucceeded,
                CreateRollbackChangeSet(requestedId, SetupCodes.RollbackSucceeded) with
                {
                    State = SetupChangeSetState.Applied,
                },
                null),
            "rollback-succeeded-wrong-outcome" => new(
                requestedId,
                true,
                SetupCodes.RollbackSucceeded,
                CreateRollbackChangeSet(requestedId, SetupCodes.RollbackSucceeded) with
                {
                    OutcomeCode = SetupCodes.RollbackStale,
                },
                null),
            "partial-rollback-wrong-state" => new(
                requestedId,
                false,
                SetupCodes.PartialRollback,
                CreateRollbackChangeSet(requestedId, SetupCodes.PartialRollback) with
                {
                    State = SetupChangeSetState.Applied,
                },
                null),
            "partial-rollback-wrong-outcome" => new(
                requestedId,
                false,
                SetupCodes.PartialRollback,
                CreateRollbackChangeSet(requestedId, SetupCodes.PartialRollback) with
                {
                    OutcomeCode = SetupCodes.InternalError,
                },
                null),
            "rollback-not-available-wrong-outcome" => new(
                requestedId,
                false,
                SetupCodes.RollbackNotAvailable,
                CreateRollbackChangeSet(requestedId, SetupCodes.RollbackNotAvailable) with
                {
                    OutcomeCode = SetupCodes.ApplySucceeded,
                },
                null),
            "rollback-stale-wrong-state" => new(
                requestedId,
                false,
                SetupCodes.RollbackStale,
                CreateRollbackChangeSet(requestedId, SetupCodes.RollbackStale) with
                {
                    State = SetupChangeSetState.RolledBack,
                },
                null),
            "rollback-stale-wrong-outcome" => new(
                requestedId,
                false,
                SetupCodes.RollbackStale,
                CreateRollbackChangeSet(requestedId, SetupCodes.RollbackStale) with
                {
                    OutcomeCode = SetupCodes.ApplySucceeded,
                },
                null),
            "unsafe-path-wrong-state" => new(
                requestedId,
                false,
                SetupCodes.UnsafePath,
                CreateRollbackChangeSet(requestedId, SetupCodes.UnsafePath) with
                {
                    State = SetupChangeSetState.Partial,
                },
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    private static SetupRollbackExecutionResult CreateRollbackExecutionForCode(
        Guid requestedId,
        string code,
        bool trusted)
    {
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000711");
        if (code is SetupCodes.InterruptedApplyRecovered or SetupCodes.InterruptedRollbackRecovered)
        {
            var operation = code == SetupCodes.InterruptedApplyRecovered
                ? SetupRecoveryOperation.Apply
                : SetupRecoveryOperation.Rollback;
            var state = operation == SetupRecoveryOperation.Apply
                ? SetupChangeSetState.Applied
                : SetupChangeSetState.RolledBack;
            return new SetupRollbackExecutionResult(
                requestedId,
                true,
                code,
                null,
                new SetupRecoveryResult(
                    SetupRecoveryDisposition.Recovered,
                    code,
                    recoveredId,
                    operation,
                    CreateRecoveryEvidence(recoveredId, state, code)));
        }

        if (code == SetupCodes.InterruptedRecoveryFailed)
        {
            return new SetupRollbackExecutionResult(
                requestedId,
                false,
                code,
                null,
                new SetupRecoveryResult(
                    SetupRecoveryDisposition.Failed,
                    code,
                    recoveredId,
                    SetupRecoveryOperation.Rollback,
                    CreateRecoveryEvidence(
                        recoveredId,
                        SetupChangeSetState.Partial,
                        SetupCodes.InterruptedRecoveryFailed)));
        }

        return new SetupRollbackExecutionResult(
            requestedId,
            code == SetupCodes.RollbackSucceeded,
            code,
            trusted ? CreateRollbackChangeSet(requestedId, code) : null,
            null);
    }

    private static SetupRollbackExecutionResult CreateMalformedRollbackExecution(
        Guid requestedId,
        string variant,
        string rawMarker)
    {
        var otherId = Guid.Parse("00000000-0000-7000-8000-000000000712");
        var trusted = CreateRollbackChangeSet(requestedId, SetupCodes.RollbackSucceeded);
        var recoveredId = Guid.Parse("00000000-0000-7000-8000-000000000711");
        var recovered = new SetupRecoveryResult(
            SetupRecoveryDisposition.Recovered,
            SetupCodes.InterruptedApplyRecovered,
            recoveredId,
            SetupRecoveryOperation.Apply,
            CreateRecoveryEvidence(
                recoveredId,
                SetupChangeSetState.Applied,
                SetupCodes.InterruptedApplyRecovered));
        return variant switch
        {
            "unknown-code" => new(requestedId, false, rawMarker, null, null),
            "success-mismatch" => new(
                requestedId,
                false,
                SetupCodes.RollbackSucceeded,
                trusted,
                null),
            "requested-id-mismatch" => new(
                otherId,
                true,
                SetupCodes.RollbackSucceeded,
                CreateRollbackChangeSet(otherId, SetupCodes.RollbackSucceeded),
                null),
            "direct-interrupted-code" => new(
                requestedId,
                true,
                SetupCodes.InterruptedApplyRecovered,
                null,
                null),
            "success-without-trusted-row" => new(
                requestedId,
                true,
                SetupCodes.RollbackSucceeded,
                null,
                null),
            "invalid-arguments-with-trusted-row" => new(
                requestedId,
                false,
                SetupCodes.InvalidArguments,
                trusted,
                null),
            "ledger-code-with-trusted-row" => new(
                requestedId,
                false,
                SetupCodes.LedgerCorrupt,
                trusted,
                null),
            "recovery-code-mismatch" => new(
                requestedId,
                true,
                SetupCodes.InterruptedRollbackRecovered,
                null,
                recovered),
            "recovery-success-mismatch" => new(
                requestedId,
                false,
                SetupCodes.InterruptedApplyRecovered,
                null,
                recovered),
            "malformed-recovery-evidence" => new(
                requestedId,
                true,
                SetupCodes.InterruptedApplyRecovered,
                null,
                recovered with { EffectiveChangeSet = null }),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

    private static SetupLedgerTarget CreateOwnedApplyTarget(Guid recordId) => new(
        recordId,
        SetupTargetKind.File,
        "apply-target",
        "test-adapter",
        [new SetupLedgerMember("setting_1", SetupOperation.Replace)],
        new string('a', 64),
        new string('b', 64),
        recordId.ToString("D"),
        SetupCodes.ApplySucceeded,
        SetupLedgerRollbackStatus.Pending,
        SetupRestartRequirement.RestartVsCode,
        CreateApplyStatusProjection(SetupOperation.Replace),
        "1.2.3");

    private static SetupLedgerTarget CreateAllNoOpApplyTarget(Guid recordId) =>
        CreateOwnedApplyTarget(recordId) with
        {
            Members = [new SetupLedgerMember("setting_1", SetupOperation.NoOp)],
            AppliedStateHash = null,
            BackupReference = null,
            RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
            StatusProjection = CreateApplyStatusProjection(SetupOperation.NoOp),
        };

    private static SetupLedgerTarget CreateApplyGuidanceTarget(Guid recordId) =>
        CreateOwnedApplyTarget(recordId) with
        {
            TargetKind = SetupTargetKind.Guidance,
            TargetLabel = "github-copilot-app-sdk-guidance",
            Members = [],
            AppliedStateHash = null,
            BackupReference = null,
            RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
            RestartRequirement = SetupRestartRequirement.None,
            StatusProjection = new SetupStatusProjection(
                false,
                null,
                SetupOperation.NoOp,
                null,
                null,
                null,
                new SetupStatusGuidance("caller_managed_sample", "dotnet"),
                []),
        };

    private static SetupStatusProjection CreateApplyStatusProjection(
        SetupOperation operation,
        bool detected = true,
        string? detectedVersion = "1.2.3",
        SetupEffectiveSource? effectiveSource = SetupEffectiveSource.UserSetting,
        string? endpoint = null,
        JsonElement? expectedResult = null) => new(
        detected,
        detectedVersion,
        operation,
        effectiveSource,
        endpoint,
        expectedResult,
        null,
        [CreateApplyChange(operation)]);

    private static SetupMemberChangeResult CreateApplyChange(SetupOperation operation) => new(
        "setting_1",
        operation,
        operation == SetupOperation.NoOp ? "present_same" : "present_different",
        "configured",
        "none",
        false);

    private static JsonDocument CreateHistoricalVsCodeManifest()
    {
        var current = SourceCapabilityManifestLoader
            .LoadForTarget(GitHubCopilotSetupTarget.VsCode)!
            .CanonicalJson;
        var historical = JsonNode.Parse(current.GetRawText())!.AsObject();
        historical["support_status"] = "planned";
        historical["stability"] = "preview";
        return JsonDocument.Parse(historical.ToJsonString());
    }

    private static void AssertHistoricalManifest(JsonElement actual, string historicalJson)
    {
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(historicalJson), JsonNode.Parse(actual.GetRawText())));
        Assert.Equal("planned", actual.GetProperty("support_status").GetString());
        Assert.Equal("preview", actual.GetProperty("stability").GetString());
    }

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

    private static SetupChangeRecord CreateManifestRecord(SetupPlanRequest request)
    {
        var current = SourceCapabilityManifestLoader
            .LoadForTarget(GitHubCopilotSetupTarget.VsCode)!
            .CanonicalJson;
        var change = CreateApplyChange(SetupOperation.Replace);
        return new SetupChangeRecord(
            RecordId,
            SetupTargetKind.Json,
            "private://vscode-stable-default-user-settings",
            "vscode-stable-default-user-settings",
            new string('a', 64),
            "configured",
            [new SetupPrivatePlanMember(change.SettingKey, change.Operation, "configured")],
            SetupRestartRequirement.RestartVsCode,
            new SetupStatusProjection(
                true,
                "1.2.3",
                SetupOperation.Replace,
                SetupEffectiveSource.UserSetting,
                request.Endpoint,
                current.Clone(),
                null,
                [change]));
    }

    private static SetupChangeRecord CreateApplicableFileRecord(Guid recordId, string targetPath)
    {
        var change = new SetupMemberChangeResult(
            "setting_1",
            SetupOperation.Replace,
            "present_different",
            "configured",
            "none",
            false);
        return new SetupChangeRecord(
            recordId,
            SetupTargetKind.Json,
            targetPath,
            "dispatcher-settings",
            SetupHash.File(true, Encoding.UTF8.GetBytes("old-file")),
            "new-file",
            [new SetupPrivatePlanMember(change.SettingKey, change.Operation, "new-file")],
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                "1.0.0",
                SetupOperation.Replace,
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

    private static SetupChangeRecord CreateInvalidGuidanceRecord(Guid recordId, string sample)
    {
        var record = CreateGuidanceRecord(recordId);
        return record with
        {
            Guidance = new SetupGuidance(record.Guidance!.Kind, record.Guidance.Language, sample),
        };
    }

    private static void AssertNoPlannedArtifacts(DispatcherFixture fixture)
    {
        Assert.Empty(fixture.LedgerStore.Load().ChangeSets);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetPlan(Guid.Parse("00000000-0000-7000-8000-000000000001"))));
    }

    private static SetupLedgerChangeSet CreateRecoveryEvidence(
        Guid changeSetId,
        SetupChangeSetState state,
        string outcomeCode) => new(
        changeSetId,
        "test-adapter",
        "sample",
        Timestamp,
        Timestamp,
        "1.2.3",
        outcomeCode,
        state,
        []);

    private static SetupRecoveryResult CreateMalformedRecovery(string variant)
    {
        var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000904");
        var otherChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000905");
        var recovered = CreateRecoveryEvidence(
            changeSetId,
            SetupChangeSetState.Applied,
            SetupCodes.InterruptedApplyRecovered);
        var failed = CreateRecoveryEvidence(
            changeSetId,
            SetupChangeSetState.Partial,
            SetupCodes.InterruptedRecoveryFailed);
        return variant switch
        {
            "none-fields" => new(SetupRecoveryDisposition.None, SetupCodes.LedgerCorrupt, null, null, null),
            "recovered-missing-effective" => new(
                SetupRecoveryDisposition.Recovered,
                SetupCodes.InterruptedApplyRecovered,
                changeSetId,
                SetupRecoveryOperation.Apply,
                null),
            "recovered-mismatched-id" => new(
                SetupRecoveryDisposition.Recovered,
                SetupCodes.InterruptedApplyRecovered,
                otherChangeSetId,
                SetupRecoveryOperation.Apply,
                recovered),
            "recovered-wrong-state" => new(
                SetupRecoveryDisposition.Recovered,
                SetupCodes.InterruptedApplyRecovered,
                changeSetId,
                SetupRecoveryOperation.Apply,
                recovered with { State = SetupChangeSetState.Partial }),
            "recovered-wrong-outcome" => new(
                SetupRecoveryDisposition.Recovered,
                SetupCodes.InterruptedApplyRecovered,
                changeSetId,
                SetupRecoveryOperation.Apply,
                recovered with { OutcomeCode = SetupCodes.InterruptedRollbackRecovered }),
            "failed-interrupted-missing-effective" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                changeSetId,
                SetupRecoveryOperation.Apply,
                null),
            "failed-interrupted-mismatched-id" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                otherChangeSetId,
                SetupRecoveryOperation.Apply,
                failed),
            "failed-interrupted-wrong-state" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                changeSetId,
                SetupRecoveryOperation.Apply,
                failed with { State = SetupChangeSetState.Applied }),
            "failed-interrupted-wrong-outcome" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.InterruptedRecoveryFailed,
                changeSetId,
                SetupRecoveryOperation.Apply,
                failed with { OutcomeCode = SetupCodes.ApplySucceeded }),
            "failed-recovery-required-uncorrelated" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                null,
                null,
                null),
            "failed-recovery-required-mismatched-id" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                otherChangeSetId,
                SetupRecoveryOperation.Apply,
                failed),
            "failed-recovery-required-wrong-state" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                changeSetId,
                SetupRecoveryOperation.Apply,
                failed with { State = SetupChangeSetState.Applied }),
            "failed-recovery-required-wrong-outcome" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                changeSetId,
                SetupRecoveryOperation.Apply,
                failed with { OutcomeCode = SetupCodes.ApplySucceeded }),
            "failed-ledger-with-evidence" => new(
                SetupRecoveryDisposition.Failed,
                SetupCodes.LedgerCorrupt,
                changeSetId,
                SetupRecoveryOperation.Apply,
                failed),
            "invalid-disposition" => new((SetupRecoveryDisposition)999, null, null, null, null),
            "unknown-code" => new(
                SetupRecoveryDisposition.Failed,
                "PRIVATE_RECOVERY_CODE",
                null,
                null,
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }

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

    private sealed class CallCounter
    {
        public int Value { get; set; }
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
            Func<SetupLock, SetupRecoveryResult> recover,
            Func<string?, SetupCommandResult>? status = null)
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
                new SetupAdapterRegistry(adapters),
                "1.2.3",
                recover,
                (_, _) => throw new InvalidOperationException("apply must not run"),
                (_, _) => throw new InvalidOperationException("rollback must not run"),
                status ?? CreateStatusResult);
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

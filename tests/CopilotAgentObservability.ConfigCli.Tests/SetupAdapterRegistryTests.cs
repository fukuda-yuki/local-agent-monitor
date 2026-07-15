using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupAdapterRegistryTests
{
    private static readonly Guid ChangeSetId = Guid.Parse("018f3b9a-0000-7000-8000-000000000001");
    private static readonly Guid RecordId = Guid.Parse("018f3b9a-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_ProjectsOneAdapterAggregateIntoImmutableArtifactsAndDiagnostics()
    {
        var adapter = new RecordingAdapter(
            "test-adapter",
            warnings: [SetupCodes.MonitorNotRunning],
            nextActions: [SetupCodes.StartLocalMonitor]);
        var registry = new SetupAdapterRegistry([adapter]);
        var request = CreateRequest("test-adapter");

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(registry.Plan(request));

        var planned = result.Value;
        var privatePlan = planned.PrivatePlan;
        var ledger = planned.PlannedChangeSet;
        var target = Assert.Single(result.Targets);
        Assert.Equal([SetupCodes.MonitorNotRunning], result.Warnings);
        Assert.Equal([SetupCodes.StartLocalMonitor], result.NextActions);
        Assert.Equal(ChangeSetId, privatePlan.ChangeSetId);
        Assert.Equal("test-adapter", privatePlan.Adapter);
        Assert.Equal("sample", privatePlan.SelectedTarget);
        Assert.Equal(CreatedAt, privatePlan.CreatedAt);
        Assert.Equal("1.2.3", privatePlan.ToolVersion);
        var privateTarget = Assert.Single(privatePlan.Targets);
        Assert.Equal(RecordId, privateTarget.RecordId);
        Assert.Equal(SetupTargetKind.Env, privateTarget.TargetKind);
        Assert.Equal("private://copilot-cli-user-environment", privateTarget.TargetLocation);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", privateTarget.BaseStateHash);
        Assert.Equal("configured", privateTarget.DesiredState);
        var privateMember = Assert.Single(privateTarget.Members);
        Assert.Equal("COPILOT_OTEL_ENABLED", privateMember.SettingKey);
        Assert.Equal("true", privateMember.DesiredValue);

        Assert.Equal(ChangeSetId, ledger.ChangeSetId);
        Assert.Equal(SetupChangeSetState.Planned, ledger.State);
        Assert.Null(ledger.OutcomeCode);
        Assert.Equal(CreatedAt, ledger.UpdatedAt);
        var ledgerTarget = Assert.Single(ledger.Targets);
        Assert.Equal("copilot-cli-user-environment", ledgerTarget.TargetLabel);
        Assert.Equal("test-adapter", ledgerTarget.OwningAdapter);
        Assert.Equal(privateTarget.BaseStateHash, ledgerTarget.PreviousStateHash);
        Assert.Null(ledgerTarget.AppliedStateHash);
        Assert.Null(ledgerTarget.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, ledgerTarget.RollbackStatus);
        Assert.Equal(SetupRestartRequirement.RestartTerminalSession, ledgerTarget.RestartRequirement);
        Assert.Equal("1.0.4", ledgerTarget.StatusProjection.DetectedVersion);
        Assert.Equal(SetupEffectiveSource.Environment, ledgerTarget.StatusProjection.EffectiveSource);
        Assert.Equal("http://127.0.0.1:4320", ledgerTarget.StatusProjection.Endpoint);
        Assert.Equal("github-copilot-cli", ledgerTarget.StatusProjection.ExpectedResult?.GetProperty("source_surface").GetString());
        var snapshotChange = Assert.Single(ledgerTarget.StatusProjection.Changes);
        Assert.Equal("present_different", snapshotChange.PreviousState);
        Assert.Equal("configured_loopback", snapshotChange.NewState);

        Assert.Equal(RecordId, target.RecordId);
        Assert.Equal(SetupTargetKind.Env, target.TargetKind);
        Assert.Equal("copilot-cli-user-environment", target.TargetLabel);
        Assert.True(target.Detected);
        Assert.Equal("1.0.4", target.DetectedVersion);
        Assert.Equal(SetupOperation.Replace, target.Operation);
        Assert.Equal(SetupEffectiveSource.Environment, target.EffectiveSource);
        Assert.Equal(SetupRestartRequirement.RestartTerminalSession, target.RestartRequirement);
        Assert.True(target.ProspectiveRollbackAvailable);
        Assert.Equal("http://127.0.0.1:4320", target.Endpoint);
        Assert.Equal("github-copilot-cli", target.ExpectedResult?.GetProperty("source_surface").GetString());
        Assert.Null(target.Guidance);
        Assert.Equal(snapshotChange, Assert.Single(target.Changes));

    }

    [Fact]
    public void Plan_WhenAdapterReturnsSanitizedFailure_PreservesImmutableDiagnosticsWithoutArtifacts()
    {
        var adapter = new FixedResultAdapter(
            "test-adapter",
            SetupPlanResult.Failure<SetupChangePlan>(
                SetupCodes.UnsupportedTarget,
                [SetupCodes.ManagedPolicyUnverified],
                [SetupCodes.RunVsCodePolicyDiagnostics]));
        var registry = new SetupAdapterRegistry([adapter]);

        var result = Assert.IsType<SetupPlanFailure<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics], result.NextActions);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.NextActions)[0] = "changed");
        Assert.Null(result.GetType().GetProperty("Value"));
        Assert.Null(result.GetType().GetProperty("Targets"));
        Assert.DoesNotContain(
            result.GetType().GetProperties(),
            property => property.PropertyType == typeof(SetupPrivatePlan) ||
                property.PropertyType == typeof(SetupLedgerChangeSet) ||
                property.PropertyType == typeof(SetupPlannedChangeSet));
    }

    [Fact]
    public void Plan_SnapshotsEveryAdapterOwnedArrayBeforeReturning()
    {
        var members = new List<SetupPrivatePlanMember>
        {
            new("COPILOT_OTEL_ENABLED", SetupOperation.Replace, "true"),
        };
        var changes = new List<SetupMemberChangeResult>
        {
            new("COPILOT_OTEL_ENABLED", SetupOperation.Replace, "present_different", "configured_loopback", "none", false),
        };
        var records = new List<SetupChangeRecord>
        {
            CreateRecord(RecordId, "copilot-cli-user-environment") with
            {
                Members = members,
                StatusProjection = CreateRecord(RecordId, "copilot-cli-user-environment").StatusProjection with { Changes = changes },
            },
        };
        var warnings = new List<string> { SetupCodes.MonitorNotRunning };
        var nextActions = new List<string> { SetupCodes.StartLocalMonitor };
        var carrier = SetupPlanResult.Planned(
            CreatePlan(records),
            warnings,
            nextActions);
        records.Clear();
        members.Clear();
        changes.Clear();
        warnings.Clear();
        nextActions.Clear();
        var registry = new SetupAdapterRegistry([new FixedResultAdapter("test-adapter", carrier)]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.Single(result.Value.PrivatePlan.Targets);
        Assert.Single(result.Value.PrivatePlan.Targets[0].Members);
        Assert.Single(result.Targets);
        Assert.Single(result.Targets[0].Changes);
        Assert.Equal([SetupCodes.MonitorNotRunning], result.Warnings);
        Assert.Equal([SetupCodes.StartLocalMonitor], result.NextActions);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<SetupPlanTarget>)result.Targets)[0] = result.Targets[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupMemberChangeResult>)result.Targets[0].Changes)[0] = result.Targets[0].Changes[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupPrivatePlanTarget>)result.Value.PrivatePlan.Targets)[0] = result.Value.PrivatePlan.Targets[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupPrivatePlanMember>)result.Value.PrivatePlan.Targets[0].Members)[0] = result.Value.PrivatePlan.Targets[0].Members[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupLedgerTarget>)result.Value.PlannedChangeSet.Targets)[0] = result.Value.PlannedChangeSet.Targets[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<SetupLedgerMember>)result.Value.PlannedChangeSet.Targets[0].Members)[0] = result.Value.PlannedChangeSet.Targets[0].Members[0]);
    }

    [Fact]
    public void Plan_AcceptsTheExactTargetAndMemberBounds()
    {
        var records = Enumerable.Range(1, 16)
            .Select(index => CreatePhysicalRecord(
                CreateRecordId(index),
                $"target-{index}",
                index == 1 ? 32 : 1,
                SetupOperation.Replace))
            .ToArray();
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(CreatePlan(records)))]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal(16, result.Targets.Count);
        Assert.Equal(32, result.Targets[0].Changes.Count);
    }

    [Fact]
    public void Plan_WhenAdapterReturnsTooManyTargets_FailsWithFixedSafeMessage()
    {
        var records = Enumerable.Range(1, 17)
            .Select(index => CreatePhysicalRecord(CreateRecordId(index), $"target-{index}", 1, SetupOperation.Replace))
            .ToArray();
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(CreatePlan(records)))]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
    }

    [Fact]
    public void Plan_WhenAdapterReturnsTooManyMemberChanges_FailsWithFixedSafeMessage()
    {
        var record = CreatePhysicalRecord(RecordId, "bounded-target", 33, SetupOperation.Replace);
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(CreatePlan([record])))]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
    }

    [Fact]
    public void Plan_PreservesProspectiveRollbackOnlyForChangedPhysicalTargets()
    {
        var records = new[]
        {
            CreatePhysicalRecord(CreateRecordId(1), "changed-target", 1, SetupOperation.Replace),
            CreatePhysicalRecord(CreateRecordId(2), "no-op-target", 1, SetupOperation.NoOp),
        };
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(CreatePlan(records)))]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.True(result.Targets[0].ProspectiveRollbackAvailable);
        Assert.False(result.Targets[1].ProspectiveRollbackAvailable);
        Assert.Contains(result.Targets, target => target.ProspectiveRollbackAvailable);
        Assert.Contains(result.Targets, target => !target.ProspectiveRollbackAvailable);
    }

    [Fact]
    public void Plan_AllNoOpTargetsExposeTheNoChangesSignalWithoutRollbackOwnership()
    {
        var records = new[]
        {
            CreatePhysicalRecord(CreateRecordId(1), "first-target", 1, SetupOperation.NoOp),
            CreatePhysicalRecord(CreateRecordId(2), "second-target", 2, SetupOperation.NoOp),
        };
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(CreatePlan(records)))]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.All(result.Targets, target => Assert.False(target.ProspectiveRollbackAvailable));
        Assert.All(result.Value.PlannedChangeSet.Targets, target =>
            Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, target.RollbackStatus));
    }

    [Fact]
    public void Plan_DoesNotExposePublicResultDtosFromTheRegistryBridge()
    {
        var methodTypes = typeof(SetupAdapterRegistry)
            .GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType));
        var plannedProperties = typeof(SetupPlannedChangeSet).GetProperties().Select(property => property.PropertyType);
        var revalidateMethod = Assert.Single(
            typeof(ISetupApplyRevalidator).GetMethods(),
            method => method.Name == nameof(ISetupApplyRevalidator.Revalidate));

        Assert.DoesNotContain(typeof(SetupCommandResult), methodTypes);
        Assert.DoesNotContain(typeof(SetupTargetResult), methodTypes);
        Assert.DoesNotContain(typeof(SetupCommandResult), plannedProperties);
        Assert.DoesNotContain(typeof(SetupTargetResult), plannedProperties);
        Assert.Equal(typeof(SetupPlanResult<SetupRevalidation>), revalidateMethod.ReturnType);
        Assert.DoesNotContain(
            revalidateMethod.GetParameters().Select(parameter => parameter.ParameterType).Append(revalidateMethod.ReturnType),
            type => type == typeof(SetupCommandResult) || type == typeof(SetupTargetResult));
    }

    [Fact]
    public void Plan_WhenAdapterReturnsMalformedDiagnostics_FailsWithoutRawText()
    {
        const string rawMarker = "PRIVATE_SECRET";
        var adapter = new FixedResultAdapter(
            "test-adapter",
            SetupPlanResult.Failure<SetupChangePlan>(rawMarker));
        var registry = new SetupAdapterRegistry([adapter]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
        Assert.DoesNotContain(rawMarker, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_DoesNotInterpretTheClosedDiagnosticCatalog()
    {
        var adapter = new FixedResultAdapter(
            "test-adapter",
            SetupPlanResult.Failure<SetupChangePlan>(
                "future_failure",
                warnings: ["future_warning"],
                nextActions: ["future_action"]));
        var registry = new SetupAdapterRegistry([adapter]);

        var result = Assert.IsType<SetupPlanFailure<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal("future_failure", result.Code);
        Assert.Equal(["future_warning"], result.Warnings);
        Assert.Equal(["future_action"], result.NextActions);
    }

    [Fact]
    public void Plan_WhenRecordAndDiagnosticOrderDisagree_FailsWithFixedSafeMessage()
    {
        var first = CreatePhysicalRecord(CreateRecordId(1), "first-target", 1, SetupOperation.Replace);
        var second = CreatePhysicalRecord(CreateRecordId(2), "second-target", 1, SetupOperation.Replace);
        var plan = CreatePlan([first, second]);
        var adapter = new FixedResultAdapter(
            "test-adapter",
            SetupPlanResult.Success(
                plan,
                [SetupPlanTarget.FromRecord(second), SetupPlanTarget.FromRecord(first)],
                [],
                []));
        var registry = new SetupAdapterRegistry([adapter]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
    }

    [Fact]
    public void Plan_WhenVsCodeJsonRecordUsesLegacyInlineDesiredState_FailsClosed()
    {
        var record = CreatePhysicalRecord(
            CreateRecordId(1),
            "vscode-stable-default-user-settings",
            1,
            SetupOperation.Replace) with
        {
            TargetKind = SetupTargetKind.Json,
            StatusProjection = CreatePhysicalRecord(
                CreateRecordId(1),
                "vscode-stable-default-user-settings",
                1,
                SetupOperation.Replace).StatusProjection with
            {
                ExpectedResult = SourceCapabilityManifestLoader
                    .LoadForSurface("github-copilot-vscode")
                    .CanonicalJson,
            },
        };
        var plan = new SetupChangePlan(
            ChangeSetId,
            "github-copilot",
            "sample",
            CreatedAt,
            "1.2.3",
            [record]);
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("github-copilot", SetupPlanResult.Planned(plan))]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Plan(CreateRequest("github-copilot")));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
    }

    [Theory]
    [InlineData("vscode-stable-default-user-settings")]
    [InlineData("vscode-insiders-default-user-settings")]
    public void Plan_AcceptsTaggedDesiredStateForExactGithubCopilotVsCodeJsonLabels(string label)
    {
        var record = CreateCarrierRecord(
            SetupTargetKind.Json,
            label,
            new SetupJsoncOwnedValuesDesiredState(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                [new SetupJsoncOwnedValue("setting_1", "boolean", true)]));
        var registry = CreateRegistryForPlan("github-copilot", [record]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("github-copilot")));

        Assert.IsType<SetupJsoncOwnedValuesDesiredState>(Assert.Single(result.Value.PrivatePlan.Targets).DesiredState);
    }

    [Theory]
    [InlineData(SetupTargetKind.File)]
    [InlineData(SetupTargetKind.Toml)]
    public void Plan_AcceptsInlineDesiredStateForGenericNonTaggedTargets(SetupTargetKind targetKind)
    {
        var record = CreateCarrierRecord(
            targetKind,
            "generic-target",
            new SetupInlineDesiredState("legacy-inline"));
        var registry = CreateRegistryForPlan("github-copilot", [record]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("github-copilot")));

        Assert.IsType<SetupInlineDesiredState>(Assert.Single(result.Value.PrivatePlan.Targets).DesiredState);
    }

    [Theory]
    [InlineData("tagged_file")]
    [InlineData("tagged_toml")]
    [InlineData("tagged_other_adapter")]
    [InlineData("tagged_other_label")]
    [InlineData("inline_stable")]
    [InlineData("inline_insiders")]
    public void Plan_RejectsDesiredStateArmOutsideItsExactAdapterTargetAndLabelRelation(string variant)
    {
        var tagged = new SetupJsoncOwnedValuesDesiredState(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            [new SetupJsoncOwnedValue("setting_1", "boolean", true)]);
        var (adapter, record) = variant switch
        {
            "tagged_file" => ("github-copilot", CreateCarrierRecord(SetupTargetKind.File, "generic-target", tagged)),
            "tagged_toml" => ("github-copilot", CreateCarrierRecord(SetupTargetKind.Toml, "generic-target", tagged)),
            "tagged_other_adapter" => ("other-adapter", CreateCarrierRecord(
                SetupTargetKind.Json, "vscode-stable-default-user-settings", tagged)),
            "tagged_other_label" => ("github-copilot", CreateCarrierRecord(
                SetupTargetKind.Json, "other-json-target", tagged)),
            "inline_stable" => ("github-copilot", CreateCarrierRecord(
                SetupTargetKind.Json,
                "vscode-stable-default-user-settings",
                new SetupInlineDesiredState("legacy-inline"))),
            "inline_insiders" => ("github-copilot", CreateCarrierRecord(
                SetupTargetKind.Json,
                "vscode-insiders-default-user-settings",
                new SetupInlineDesiredState("legacy-inline"))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var registry = CreateRegistryForPlan(adapter, [record]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest(adapter)));

        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
    }

    [Fact]
    public void Planned_SnapshotsTaggedOwnedValuesBeforeTheRegistryBridge()
    {
        var values = new List<SetupJsoncOwnedValue>
        {
            new("setting_1", "boolean", true),
        };
        var record = CreateCarrierRecord(
            SetupTargetKind.Json,
            "vscode-stable-default-user-settings",
            new SetupJsoncOwnedValuesDesiredState(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                values));
        var carrier = SetupPlanResult.Planned(CreateAdapterPlan("github-copilot", [record]));
        values.Clear();
        var registry = new SetupAdapterRegistry([new FixedResultAdapter("github-copilot", carrier)]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("github-copilot")));
        var desiredState = Assert.IsType<SetupJsoncOwnedValuesDesiredState>(
            Assert.Single(result.Value.PrivatePlan.Targets).DesiredState);

        Assert.Single(desiredState.OwnedValues);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SetupJsoncOwnedValue>)desiredState.OwnedValues)[0] = new("changed", "boolean", false));
    }

    [Theory]
    [InlineData("inline_exact_label")]
    [InlineData("tagged_other_label")]
    public void Revalidate_PersistedDesiredStateArmMismatchRequiresRecoveryBeforeAdapterCall(string variant)
    {
        var record = CreateCarrierRecord(
            SetupTargetKind.Json,
            "vscode-stable-default-user-settings",
            new SetupJsoncOwnedValuesDesiredState(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                [new SetupJsoncOwnedValue("setting_1", "boolean", true)]));
        var adapter = new RecordingAdapter("github-copilot", [record]);
        var registry = new SetupAdapterRegistry([adapter]);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("github-copilot"))).Value;
        var plan = planned.PrivatePlan;
        var changeSet = planned.PlannedChangeSet;
        if (variant == "inline_exact_label")
        {
            plan = plan with
            {
                Targets =
                [
                    plan.Targets[0] with
                    {
                        DesiredState = new SetupInlineDesiredState("legacy-inline"),
                    },
                ],
            };
        }
        else
        {
            changeSet = changeSet with
            {
                Targets =
                [
                    changeSet.Targets[0] with
                    {
                        TargetLabel = "other-json-target",
                        StatusProjection = changeSet.Targets[0].StatusProjection with { ExpectedResult = null },
                    },
                ],
            };
        }

        var exception = Assert.Throws<SetupStorageException>(() =>
            ((ISetupApplyRevalidator)registry).Revalidate(plan, changeSet));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(0, adapter.RevalidationCalls);
    }

    [Theory]
    [InlineData("change-set")]
    [InlineData("adapter")]
    [InlineData("target")]
    [InlineData("created-at")]
    [InlineData("tool-version")]
    public void Plan_WhenPlanIdentityDoesNotExactlyMatchRequest_FailsSafely(string field)
    {
        var plan = CreatePlan([CreateRecord(RecordId, "copilot-cli-user-environment")]);
        var mismatched = field switch
        {
            "change-set" => plan with { ChangeSetId = CreateRecordId(99) },
            "adapter" => plan with { Adapter = "other-adapter" },
            "target" => plan with { SelectedTarget = "other-target" },
            "created-at" => plan with { CreatedAt = plan.CreatedAt.AddSeconds(1) },
            "tool-version" => plan with { ToolVersion = "9.9.9" },
            _ => throw new InvalidOperationException(),
        };
        var registry = new SetupAdapterRegistry(
            [new FixedResultAdapter("test-adapter", SetupPlanResult.Planned(mismatched))]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Plan(CreateRequest("test-adapter")));

        Assert.DoesNotContain("other-target", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsDuplicateAdapterIdsDeterministically()
    {
        var exception = Assert.Throws<ArgumentException>(() => new SetupAdapterRegistry(
        [
            new RecordingAdapter("test-adapter"),
            new RecordingAdapter("test-adapter"),
        ]));

        Assert.Equal("adapterId", exception.ParamName);
    }

    [Fact]
    public void Resolve_RejectsNonCanonicalAdapterId()
    {
        var registry = new SetupAdapterRegistry([new RecordingAdapter("test-adapter")]);

        var exception = Assert.Throws<ArgumentException>(() => registry.Resolve("TEST-ADAPTER"));

        Assert.Equal("adapterId", exception.ParamName);
    }

    [Fact]
    public void Resolve_RequiresAnExactRegisteredAdapterId()
    {
        var registry = new SetupAdapterRegistry([new RecordingAdapter("test-adapter")]);

        var exception = Assert.Throws<SetupAdapterNotRegisteredException>(() => registry.Resolve("other-adapter"));

        Assert.Equal("other-adapter", exception.AdapterId);
    }

    [Fact]
    public void Plan_PreservesTheAdapterRecordOrderAcrossEveryProducedArtifact()
    {
        var secondRecordId = Guid.Parse("018f3b9a-0000-7000-8000-000000000003");
        var adapter = new RecordingAdapter("test-adapter", [
            CreateRecord(secondRecordId, "copilot-cli-user-environment"),
            CreateRecord(RecordId, "copilot-cli-user-environment"),
        ]);
        var registry = new SetupAdapterRegistry([adapter]);

        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));
        var planned = result.Value;

        Assert.Equal([secondRecordId, RecordId], planned.PrivatePlan.Targets.Select(target => target.RecordId));
        Assert.Equal([secondRecordId, RecordId], planned.PlannedChangeSet.Targets.Select(target => target.RecordId));
        Assert.Equal([secondRecordId, RecordId], result.Targets.Select(target => target.RecordId));
    }

    [Fact]
    public void Revalidate_ResolvesThePersistedAdapterOnceAndSnapshotsTargetlessSuccess()
    {
        var warnings = new List<string> { SetupCodes.ManagedPolicyUnverified };
        var nextActions = new List<string> { SetupCodes.RunVsCodePolicyDiagnostics };
        var carrier = SetupPlanResult.Revalidated(warnings, nextActions);
        warnings.Clear();
        nextActions.Clear();
        var adapter = new RecordingAdapter("test-adapter", revalidate: (_, _) => carrier);
        var registry = new SetupAdapterRegistry([adapter]);
        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));
        var planned = result.Value;

        var revalidation = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(
            ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet));

        Assert.Equal(1, adapter.RevalidationCalls);
        Assert.Same(SetupRevalidation.Instance, revalidation.Value);
        Assert.Empty(revalidation.Targets);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], revalidation.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics], revalidation.NextActions);
        Assert.NotSame(carrier.Warnings, revalidation.Warnings);
        Assert.NotSame(carrier.NextActions, revalidation.NextActions);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)revalidation.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)revalidation.NextActions)[0] = "changed");
        Assert.Same(planned.PrivatePlan, adapter.RevalidatedPlan);
        Assert.Same(planned.PlannedChangeSet, adapter.RevalidatedChangeSet);
    }

    [Fact]
    public void Revalidate_WhenPersistedOwnerIsMissing_ReturnsUnsupportedAdapterWithoutCallingAnotherAdapter()
    {
        var ownerRegistry = new SetupAdapterRegistry([new RecordingAdapter("removed-adapter")]);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            ownerRegistry.Plan(CreateRequest("removed-adapter"))).Value;
        var otherAdapter = new RecordingAdapter("other-adapter");
        var registry = new SetupAdapterRegistry([otherAdapter]);

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(
            ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet));

        Assert.Equal(SetupCodes.UnsupportedAdapter, result.Code);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.NextActions);
        Assert.Null(result.GetType().GetProperty("Value"));
        Assert.Null(result.GetType().GetProperty("Targets"));
        Assert.Equal(0, otherAdapter.RevalidationCalls);
    }

    [Fact]
    public void Revalidate_WhenAdapterReturnsFailure_CopiesCodeAndImmutableDiagnostics()
    {
        var carrier = SetupPlanResult.Failure<SetupRevalidation>(
            SetupCodes.UnsupportedTarget,
            [SetupCodes.ManagedPolicyUnverified],
            [SetupCodes.RunVsCodePolicyDiagnostics]);
        var adapter = new RecordingAdapter("test-adapter", revalidate: (_, _) => carrier);
        var registry = new SetupAdapterRegistry([adapter]);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter"))).Value;

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(
            ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet));

        Assert.Equal(1, adapter.RevalidationCalls);
        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics], result.NextActions);
        Assert.NotSame(carrier.Warnings, result.Warnings);
        Assert.NotSame(carrier.NextActions, result.NextActions);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.NextActions)[0] = "changed");
    }

    [Fact]
    public void Revalidate_DoesNotInterpretTheClosedDiagnosticCatalog()
    {
        var carrier = SetupPlanResult.Failure<SetupRevalidation>(
            "future_failure",
            ["future_warning"],
            ["future_action"]);
        var adapter = new RecordingAdapter("test-adapter", revalidate: (_, _) => carrier);
        var registry = new SetupAdapterRegistry([adapter]);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter"))).Value;

        var result = Assert.IsType<SetupPlanFailure<SetupRevalidation>>(
            ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet));

        Assert.Equal("future_failure", result.Code);
        Assert.Equal(["future_warning"], result.Warnings);
        Assert.Equal(["future_action"], result.NextActions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("failure-code")]
    [InlineData("warning")]
    [InlineData("next-action")]
    [InlineData("success-target")]
    public void Revalidate_WhenAdapterReturnsMalformedOutput_FailsWithFixedSafeMessage(string malformedPart)
    {
        const string rawMarker = "PRIVATE_SECRET";
        var adapter = new RecordingAdapter(
            "test-adapter",
            revalidate: (_, _) => CreateMalformedRevalidation(malformedPart, rawMarker)!);
        var registry = new SetupAdapterRegistry([adapter]);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter"))).Value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet));

        Assert.Equal(1, adapter.RevalidationCalls);
        Assert.Equal("Setup adapter returned invalid output.", exception.Message);
        Assert.DoesNotContain(rawMarker, exception.ToString(), StringComparison.Ordinal);
    }

    private static SetupPlanResult<SetupRevalidation>? CreateMalformedRevalidation(
        string malformedPart,
        string rawMarker) => malformedPart switch
        {
            "null" => null,
            "failure-code" => SetupPlanResult.Failure<SetupRevalidation>(rawMarker),
            "warning" => SetupPlanResult.Revalidated([rawMarker], []),
            "next-action" => SetupPlanResult.Revalidated([], [rawMarker]),
            "success-target" => SetupPlanResult.Success(
                SetupRevalidation.Instance,
                [SetupPlanTarget.FromRecord(CreateRecord(RecordId, "copilot-cli-user-environment"))],
                [],
                []),
            _ => throw new InvalidOperationException(),
        };

    private static SetupPlanRequest CreateRequest(string adapter) => new(
        adapter,
        "sample",
        "http://127.0.0.1:4320",
        false,
        ChangeSetId,
        CreatedAt,
        "1.2.3");

    private static SetupAdapterRegistry CreateRegistryForPlan(
        string adapter,
        IReadOnlyList<SetupChangeRecord> records) =>
        new([new FixedResultAdapter(adapter, SetupPlanResult.Planned(CreateAdapterPlan(adapter, records)))]);

    private static SetupChangePlan CreateAdapterPlan(
        string adapter,
        IReadOnlyList<SetupChangeRecord> records) => new(
        ChangeSetId,
        adapter,
        "sample",
        CreatedAt,
        "1.2.3",
        records);

    private static SetupChangePlan CreatePlan(IReadOnlyList<SetupChangeRecord> records) => new(
        ChangeSetId,
        "test-adapter",
        "sample",
        CreatedAt,
        "1.2.3",
        records);

    private static Guid CreateRecordId(int value) =>
        Guid.Parse($"018f3b9a-0000-7000-8000-{value:x12}");

    private static SetupChangeRecord CreateCarrierRecord(
        SetupTargetKind targetKind,
        string targetLabel,
        SetupPrivateDesiredState desiredState)
    {
        var record = CreatePhysicalRecord(CreateRecordId(1), targetLabel, 1, SetupOperation.Replace);
        System.Text.Json.JsonElement? expectedResult = targetKind == SetupTargetKind.Json &&
            targetLabel is "vscode-stable-default-user-settings" or "vscode-insiders-default-user-settings"
                ? SourceCapabilityManifestLoader.LoadForSurface("github-copilot-vscode").CanonicalJson
                : null;
        return record with
        {
            TargetKind = targetKind,
            DesiredState = desiredState,
            StatusProjection = record.StatusProjection with { ExpectedResult = expectedResult },
        };
    }

    private static SetupChangeRecord CreatePhysicalRecord(
        Guid recordId,
        string targetLabel,
        int memberCount,
        SetupOperation operation)
    {
        var members = Enumerable.Range(1, memberCount)
            .Select(index => new SetupPrivatePlanMember($"setting_{index}", operation, "configured"))
            .ToArray();
        var changes = members
            .Select(member => new SetupMemberChangeResult(
                member.SettingKey,
                member.Operation,
                operation == SetupOperation.NoOp ? "present_same" : "present_different",
                "configured",
                "none",
                false))
            .ToArray();
        return new SetupChangeRecord(
            recordId,
            SetupTargetKind.File,
            "private://" + targetLabel,
            targetLabel,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new SetupInlineDesiredState("configured"),
            members,
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                "1.0.0",
                operation,
                SetupEffectiveSource.UserSetting,
                null,
                null,
                null,
                changes));
    }

    private static SetupChangeRecord CreateRecord(Guid recordId, string targetLabel) => new(
        recordId,
        SetupTargetKind.Env,
        "private://" + targetLabel,
        targetLabel,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        new SetupInlineDesiredState("configured"),
        [new SetupPrivatePlanMember("COPILOT_OTEL_ENABLED", SetupOperation.Replace, "true")],
        SetupRestartRequirement.RestartTerminalSession,
        new SetupStatusProjection(
            true,
            "1.0.4",
            SetupOperation.Replace,
            SetupEffectiveSource.Environment,
            "http://127.0.0.1:4320",
            SourceCapabilityManifestLoader.LoadForSurface("github-copilot-cli").CanonicalJson,
            null,
            [new SetupMemberChangeResult(
                "COPILOT_OTEL_ENABLED",
                SetupOperation.Replace,
                "present_different",
                "configured_loopback",
                "none",
                false)]));

    private sealed class RecordingAdapter : ISetupAdapter
    {
        private readonly IReadOnlyList<SetupChangeRecord>? records;
        private readonly IReadOnlyList<string> warnings;
        private readonly IReadOnlyList<string> nextActions;
        private readonly Func<SetupPrivatePlan, SetupLedgerChangeSet, SetupPlanResult<SetupRevalidation>>? revalidate;

        public RecordingAdapter(
            string adapterId,
            IReadOnlyList<SetupChangeRecord>? records = null,
            IReadOnlyList<string>? warnings = null,
            IReadOnlyList<string>? nextActions = null,
            Func<SetupPrivatePlan, SetupLedgerChangeSet, SetupPlanResult<SetupRevalidation>>? revalidate = null)
        {
            AdapterId = adapterId;
            this.records = records;
            this.warnings = warnings ?? [];
            this.nextActions = nextActions ?? [];
            this.revalidate = revalidate;
        }

        public string AdapterId { get; }

        public SetupPrivatePlan? RevalidatedPlan { get; private set; }

        public SetupLedgerChangeSet? RevalidatedChangeSet { get; private set; }

        public int RevalidationCalls { get; private set; }

        public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request) => SetupPlanResult.Planned(
            new SetupChangePlan(
                request.ChangeSetId,
                request.Adapter,
                request.SelectedTarget,
                request.CreatedAt,
                request.ToolVersion,
                records ?? [CreateRecord(RecordId, "copilot-cli-user-environment")]),
            warnings,
            nextActions);

        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidationCalls++;
            RevalidatedPlan = plan;
            RevalidatedChangeSet = plannedChangeSet;
            return revalidate is null
                ? SetupPlanResult.Revalidated()
                : revalidate(plan, plannedChangeSet);
        }
    }

    private sealed class FixedResultAdapter : ISetupAdapter
    {
        private readonly SetupPlanResult<SetupChangePlan> result;

        public FixedResultAdapter(string adapterId, SetupPlanResult<SetupChangePlan> result)
        {
            AdapterId = adapterId;
            this.result = result;
        }

        public string AdapterId { get; }

        public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request) => result;

        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }
}

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
        Assert.Equal("private://user-environment", privateTarget.TargetLocation);
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
        Assert.Equal("user-environment", ledgerTarget.TargetLabel);
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
        Assert.Equal("user-environment", target.TargetLabel);
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
    public void Plan_WhenAdapterReturnsSanitizedFailure_PreservesOnlyTypedDiagnostics()
    {
        var diagnosticTarget = new SetupPlanTarget(
            RecordId,
            SetupTargetKind.Guidance,
            "diagnostic-guidance",
            false,
            null,
            SetupOperation.NoOp,
            null,
            SetupRestartRequirement.None,
            null,
            null,
            null,
            []);
        var adapter = new FixedResultAdapter(
            "test-adapter",
            SetupPlanResult.Failure<SetupChangePlan>(
                SetupCodes.UnsupportedTarget,
                [diagnosticTarget],
                [SetupCodes.ManagedPolicyUnverified],
                [SetupCodes.RunVsCodePolicyDiagnostics]));
        var registry = new SetupAdapterRegistry([adapter]);

        var result = Assert.IsType<SetupPlanFailure<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));

        Assert.Equal(SetupCodes.UnsupportedTarget, result.Code);
        Assert.Same(diagnosticTarget, Assert.Single(result.Targets));
        Assert.False(result.Targets[0].ProspectiveRollbackAvailable);
        Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
        Assert.Equal([SetupCodes.RunVsCodePolicyDiagnostics], result.NextActions);
        Assert.Null(result.GetType().GetProperty("Value"));
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
            CreateRecord(RecordId, "user-environment") with
            {
                Members = members,
                StatusProjection = CreateRecord(RecordId, "user-environment").StatusProjection with { Changes = changes },
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

        Assert.DoesNotContain(typeof(SetupCommandResult), methodTypes);
        Assert.DoesNotContain(typeof(SetupTargetResult), methodTypes);
        Assert.DoesNotContain(typeof(SetupCommandResult), plannedProperties);
        Assert.DoesNotContain(typeof(SetupTargetResult), plannedProperties);
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

    [Theory]
    [InlineData("change-set")]
    [InlineData("adapter")]
    [InlineData("target")]
    [InlineData("created-at")]
    [InlineData("tool-version")]
    public void Plan_WhenPlanIdentityDoesNotExactlyMatchRequest_FailsSafely(string field)
    {
        var plan = CreatePlan([CreateRecord(RecordId, "user-environment")]);
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
            CreateRecord(secondRecordId, "user-environment"),
            CreateRecord(RecordId, "user-environment"),
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
    public void Revalidate_ResolvesThePersistedAdapterAndPassesProductionArtifacts()
    {
        var adapter = new RecordingAdapter("test-adapter");
        var registry = new SetupAdapterRegistry([adapter]);
        var result = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            registry.Plan(CreateRequest("test-adapter")));
        var planned = result.Value;

        ((ISetupApplyRevalidator)registry).Revalidate(planned.PrivatePlan, planned.PlannedChangeSet);

        Assert.Same(planned.PrivatePlan, adapter.RevalidatedPlan);
        Assert.Same(planned.PlannedChangeSet, adapter.RevalidatedChangeSet);
    }

    private static SetupPlanRequest CreateRequest(string adapter) => new(
        adapter,
        "sample",
        "http://127.0.0.1:4320",
        false,
        ChangeSetId,
        CreatedAt,
        "1.2.3");

    private static SetupChangePlan CreatePlan(IReadOnlyList<SetupChangeRecord> records) => new(
        ChangeSetId,
        "test-adapter",
        "sample",
        CreatedAt,
        "1.2.3",
        records);

    private static Guid CreateRecordId(int value) =>
        Guid.Parse($"018f3b9a-0000-7000-8000-{value:x12}");

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
            "configured",
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
        "configured",
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

        public RecordingAdapter(
            string adapterId,
            IReadOnlyList<SetupChangeRecord>? records = null,
            IReadOnlyList<string>? warnings = null,
            IReadOnlyList<string>? nextActions = null)
        {
            AdapterId = adapterId;
            this.records = records;
            this.warnings = warnings ?? [];
            this.nextActions = nextActions ?? [];
        }

        public string AdapterId { get; }

        public SetupPrivatePlan? RevalidatedPlan { get; private set; }

        public SetupLedgerChangeSet? RevalidatedChangeSet { get; private set; }

        public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request) => SetupPlanResult.Planned(
            new SetupChangePlan(
                request.ChangeSetId,
                request.Adapter,
                request.SelectedTarget,
                request.CreatedAt,
                request.ToolVersion,
                records ?? [CreateRecord(RecordId, "user-environment")]),
            warnings,
            nextActions);

        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidatedPlan = plan;
            RevalidatedChangeSet = plannedChangeSet;
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

        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
        }
    }
}

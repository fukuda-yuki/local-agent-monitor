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
    public void Plan_ProjectsOneAdapterAggregateIntoPrivateLedgerAndPublicContracts()
    {
        var adapter = new RecordingAdapter("test-adapter");
        var registry = new SetupAdapterRegistry([adapter]);
        var request = CreateRequest("test-adapter");

        var planned = registry.Plan(request);

        var privatePlan = planned.PrivatePlan;
        var ledger = planned.PlannedChangeSet;
        var target = Assert.Single(planned.Targets);
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

        Assert.Equal(RecordId.ToString("D"), target.RecordId);
        Assert.Equal(SetupTargetKind.Env, target.TargetKind);
        Assert.Equal("user-environment", target.TargetLabel);
        Assert.True(target.Detected);
        Assert.Equal("1.0.4", target.DetectedVersion);
        Assert.Equal(SetupOperation.Replace, target.Operation);
        Assert.Equal(SetupEffectiveSource.Environment, target.EffectiveSource);
        Assert.Null(target.ReferenceState);
        Assert.Null(target.CurrentState);
        Assert.Equal(SetupRestartRequirement.RestartTerminalSession, target.RestartRequirement);
        Assert.False(target.RollbackAvailable);
        Assert.Equal("http://127.0.0.1:4320", target.Endpoint);
        Assert.Equal("github-copilot-cli", target.ExpectedResult?.GetProperty("source_surface").GetString());
        Assert.Null(target.Guidance);
        Assert.Equal(snapshotChange, Assert.Single(target.Changes));

        using var result = System.Text.Json.JsonDocument.Parse(SetupJson.Serialize(new SetupCommandResult(
            SetupCommand.Plan, true, SetupCodes.PlanReady, ChangeSetId.ToString("D"), null, null, "test-adapter",
            planned.Targets, [], [], [], false)));
        var serializedTarget = Assert.Single(result.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal("env", serializedTarget.GetProperty("target_kind").GetString());
        Assert.Equal("user-environment", serializedTarget.GetProperty("target_label").GetString());
        Assert.Equal("http://127.0.0.1:4320", serializedTarget.GetProperty("endpoint").GetString());
        Assert.Equal("github-copilot-cli", serializedTarget.GetProperty("expected_result").GetProperty("source_surface").GetString());
        Assert.Equal("configured_loopback", serializedTarget.GetProperty("changes")[0].GetProperty("new_state").GetString());
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

        var planned = registry.Plan(CreateRequest("test-adapter"));

        Assert.Equal([secondRecordId, RecordId], planned.PrivatePlan.Targets.Select(target => target.RecordId));
        Assert.Equal([secondRecordId, RecordId], planned.PlannedChangeSet.Targets.Select(target => target.RecordId));
        Assert.Equal([secondRecordId.ToString("D"), RecordId.ToString("D")], planned.Targets.Select(target => target.RecordId));
    }

    [Fact]
    public void Revalidate_ResolvesThePersistedAdapterAndPassesProductionArtifacts()
    {
        var adapter = new RecordingAdapter("test-adapter");
        var registry = new SetupAdapterRegistry([adapter]);
        var planned = registry.Plan(CreateRequest("test-adapter"));

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

        public RecordingAdapter(string adapterId, IReadOnlyList<SetupChangeRecord>? records = null)
        {
            AdapterId = adapterId;
            this.records = records;
        }

        public string AdapterId { get; }

        public SetupPrivatePlan? RevalidatedPlan { get; private set; }

        public SetupLedgerChangeSet? RevalidatedChangeSet { get; private set; }

        public SetupChangePlan Plan(SetupPlanRequest request) => new(
            request.ChangeSetId,
            request.Adapter,
            request.SelectedTarget,
            request.CreatedAt,
            request.ToolVersion,
            records ?? [CreateRecord(RecordId, "user-environment")]);

        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
        {
            RevalidatedPlan = plan;
            RevalidatedChangeSet = plannedChangeSet;
        }
    }
}

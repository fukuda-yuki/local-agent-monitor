using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters;

internal interface ISetupAdapter
{
    string AdapterId { get; }

    SetupChangePlan Plan(SetupPlanRequest request);

    void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet);
}

internal sealed record SetupPlanRequest(
    string Adapter,
    string SelectedTarget,
    string Endpoint,
    bool IncludeContentCapture,
    Guid ChangeSetId,
    DateTimeOffset CreatedAt,
    string ToolVersion);

internal sealed record SetupChangePlan(
    Guid ChangeSetId,
    string Adapter,
    string SelectedTarget,
    DateTimeOffset CreatedAt,
    string ToolVersion,
    IReadOnlyList<SetupChangeRecord> Records);

internal sealed record SetupChangeRecord(
    Guid RecordId,
    SetupTargetKind TargetKind,
    string TargetLocation,
    string TargetLabel,
    string BaseStateHash,
    string DesiredState,
    IReadOnlyList<SetupPrivatePlanMember> Members,
    SetupRestartRequirement RestartRequirement,
    SetupStatusProjection StatusProjection,
    SetupGuidance? Guidance = null);

internal sealed record SetupPlannedChangeSet(
    SetupPrivatePlan PrivatePlan,
    SetupLedgerChangeSet PlannedChangeSet,
    IReadOnlyList<SetupTargetResult> Targets);

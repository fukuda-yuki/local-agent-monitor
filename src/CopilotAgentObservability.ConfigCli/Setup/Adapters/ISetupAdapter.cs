using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters;

internal interface ISetupAdapter
{
    string AdapterId { get; }

    SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request);

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
    SetupLedgerChangeSet PlannedChangeSet);

internal abstract class SetupPlanResult<T>
    where T : class
{
    private protected SetupPlanResult(
        IEnumerable<string> warnings,
        IEnumerable<string> nextActions)
    {
        Warnings = Snapshot(warnings);
        NextActions = Snapshot(nextActions);
    }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> NextActions { get; }

    private protected static IReadOnlyList<TItem> Snapshot<TItem>(IEnumerable<TItem> values)
    {
        if (values is null)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        return Array.AsReadOnly(values.ToArray());
    }
}

internal sealed class SetupPlanSuccess<T> : SetupPlanResult<T>
    where T : class
{
    internal SetupPlanSuccess(
        T value,
        IEnumerable<SetupPlanTarget> targets,
        IEnumerable<string> warnings,
        IEnumerable<string> nextActions)
        : base(warnings, nextActions)
    {
        Value = value ?? throw SetupPlanResult.InvalidOutput();
        Targets = Snapshot(targets);
    }

    public T Value { get; }

    public IReadOnlyList<SetupPlanTarget> Targets { get; }
}

internal sealed class SetupPlanFailure<T> : SetupPlanResult<T>
    where T : class
{
    internal SetupPlanFailure(
        string code,
        IEnumerable<string> warnings,
        IEnumerable<string> nextActions)
        : base(warnings, nextActions)
    {
        Code = code ?? throw SetupPlanResult.InvalidOutput();
    }

    public string Code { get; }
}

internal static class SetupPlanResult
{
    private const string InvalidOutputMessage = "Setup adapter returned invalid output.";

    public static SetupPlanSuccess<SetupChangePlan> Planned(
        SetupChangePlan plan,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? nextActions = null)
    {
        var snapshot = SnapshotPlan(plan);
        return new SetupPlanSuccess<SetupChangePlan>(
            snapshot,
            snapshot.Records.Select(SetupPlanTarget.FromRecord),
            warnings ?? [],
            nextActions ?? []);
    }

    public static SetupPlanSuccess<T> Success<T>(
        T value,
        IEnumerable<SetupPlanTarget> targets,
        IEnumerable<string> warnings,
        IEnumerable<string> nextActions)
        where T : class =>
        new(value, targets, warnings, nextActions);

    public static SetupPlanFailure<T> Failure<T>(
        string code,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? nextActions = null)
        where T : class =>
        new(code, warnings ?? [], nextActions ?? []);

    internal static InvalidOperationException InvalidOutput() => new(InvalidOutputMessage);

    internal static SetupChangePlan SnapshotPlan(SetupChangePlan? plan)
    {
        if (plan?.Records is null)
        {
            throw InvalidOutput();
        }

        return plan with
        {
            Records = Array.AsReadOnly(plan.Records.Select(Snapshot).ToArray()),
        };
    }

    private static SetupChangeRecord Snapshot(SetupChangeRecord? record)
    {
        if (record?.Members is null || record.StatusProjection?.Changes is null)
        {
            throw InvalidOutput();
        }

        var projection = record.StatusProjection;
        return record with
        {
            Members = Array.AsReadOnly(record.Members
                .Select(member => member is null
                    ? throw InvalidOutput()
                    : new SetupPrivatePlanMember(member.SettingKey, member.Operation, member.DesiredValue))
                .ToArray()),
            StatusProjection = new SetupStatusProjection(
                projection.Detected,
                projection.DetectedVersion,
                projection.Operation,
                projection.EffectiveSource,
                projection.Endpoint,
                projection.ExpectedResult is { } expectedResult ? expectedResult.Clone() : null,
                projection.Guidance is { } statusGuidance
                    ? new SetupStatusGuidance(statusGuidance.Kind, statusGuidance.Language)
                    : null,
                Array.AsReadOnly(projection.Changes
                    .Select(change => change is null
                        ? throw InvalidOutput()
                        : new SetupMemberChangeResult(
                            change.SettingKey,
                            change.Operation,
                            change.PreviousState,
                            change.NewState,
                            change.Conflict,
                            change.Managed))
                    .ToArray())),
            Guidance = record.Guidance is { } guidance
                ? new SetupGuidance(guidance.Kind, guidance.Language, guidance.Sample)
                : null,
        };
    }
}

internal sealed class SetupPlanTarget
{
    internal SetupPlanTarget(
        Guid recordId,
        SetupTargetKind targetKind,
        string targetLabel,
        bool detected,
        string? detectedVersion,
        SetupOperation operation,
        SetupEffectiveSource? effectiveSource,
        SetupRestartRequirement restartRequirement,
        string? endpoint,
        System.Text.Json.JsonElement? expectedResult,
        SetupGuidance? guidance,
        IEnumerable<SetupMemberChangeResult> changes)
    {
        if (changes is null)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var changeSnapshot = changes
            .Select(change => change is null
                ? throw SetupPlanResult.InvalidOutput()
                : new SetupMemberChangeResult(
                    change.SettingKey,
                    change.Operation,
                    change.PreviousState,
                    change.NewState,
                    change.Conflict,
                    change.Managed))
            .ToArray();

        RecordId = recordId;
        TargetKind = targetKind;
        TargetLabel = targetLabel;
        Detected = detected;
        DetectedVersion = detectedVersion;
        Operation = operation;
        EffectiveSource = effectiveSource;
        RestartRequirement = restartRequirement;
        ProspectiveRollbackAvailable = targetKind != SetupTargetKind.Guidance &&
            changeSnapshot.Any(change => change.Operation != SetupOperation.NoOp);
        Endpoint = endpoint;
        ExpectedResult = expectedResult?.Clone();
        Guidance = guidance is null ? null : new SetupGuidance(guidance.Kind, guidance.Language, guidance.Sample);
        Changes = Array.AsReadOnly(changeSnapshot);
    }

    public Guid RecordId { get; }

    public SetupTargetKind TargetKind { get; }

    public string TargetLabel { get; }

    public bool Detected { get; }

    public string? DetectedVersion { get; }

    public SetupOperation Operation { get; }

    public SetupEffectiveSource? EffectiveSource { get; }

    public SetupRestartRequirement RestartRequirement { get; }

    public bool ProspectiveRollbackAvailable { get; }

    public string? Endpoint { get; }

    public System.Text.Json.JsonElement? ExpectedResult { get; }

    public SetupGuidance? Guidance { get; }

    public IReadOnlyList<SetupMemberChangeResult> Changes { get; }

    internal static SetupPlanTarget FromRecord(SetupChangeRecord record)
    {
        var projection = record.StatusProjection;
        return new SetupPlanTarget(
            record.RecordId,
            record.TargetKind,
            record.TargetLabel,
            projection.Detected,
            projection.DetectedVersion,
            projection.Operation,
            projection.EffectiveSource,
            record.RestartRequirement,
            projection.Endpoint,
            projection.ExpectedResult,
            record.Guidance,
            projection.Changes);
    }
}

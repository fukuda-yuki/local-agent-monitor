using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters;

internal sealed class SetupAdapterRegistry : ISetupApplyRevalidator
{
    private static readonly Regex AdapterIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IReadOnlyDictionary<string, ISetupAdapter> adapters;

    public SetupAdapterRegistry(IEnumerable<ISetupAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var registered = new Dictionary<string, ISetupAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            EnsureAdapterId(adapter.AdapterId, nameof(adapter.AdapterId));
            if (!registered.TryAdd(adapter.AdapterId, adapter))
            {
                throw new ArgumentException("Duplicate setup adapter ID.", "adapterId");
            }
        }

        this.adapters = registered;
    }

    public ISetupAdapter Resolve(string adapterId)
    {
        EnsureAdapterId(adapterId, nameof(adapterId));
        return adapters.TryGetValue(adapterId, out var adapter)
            ? adapter
            : throw new SetupAdapterNotRegisteredException(adapterId);
    }

    public SetupPlannedChangeSet Plan(SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adapter = Resolve(request.Adapter);
        var aggregate = adapter.Plan(request) ?? throw new InvalidOperationException("Setup adapter returned no plan.");
        RequireMatchingRequest(aggregate, request);
        var records = aggregate.Records?.ToArray() ?? throw new InvalidOperationException("Setup adapter returned no records.");
        ValidateGuidanceConsistency(records);

        var privatePlan = new SetupPrivatePlan(
            1,
            aggregate.ChangeSetId,
            aggregate.Adapter,
            aggregate.SelectedTarget,
            aggregate.CreatedAt,
            aggregate.ToolVersion,
            records.Select(record => new SetupPrivatePlanTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLocation,
                record.BaseStateHash,
                record.DesiredState,
                record.Members)).ToArray());

        var plannedChangeSet = new SetupLedgerChangeSet(
            aggregate.ChangeSetId,
            aggregate.Adapter,
            aggregate.SelectedTarget,
            aggregate.CreatedAt,
            aggregate.CreatedAt,
            aggregate.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            records.Select(record => new SetupLedgerTarget(
                record.RecordId,
                record.TargetKind,
                record.TargetLabel,
                aggregate.Adapter,
                record.Members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                record.BaseStateHash,
                null,
                null,
                null,
                SetupLedgerRollbackStatus.NotAvailable,
                record.RestartRequirement,
                record.StatusProjection,
                aggregate.ToolVersion)).ToArray());

        SetupStorageValidation.ValidatePlanAndLedger(privatePlan, plannedChangeSet);
        var targets = records.Select(ToPlanTarget).ToArray();
        SetupContractValidator.Validate(new SetupCommandResult(
            SetupCommand.Plan,
            true,
            SetupCodes.PlanReady,
            aggregate.ChangeSetId.ToString("D"),
            null,
            null,
            aggregate.Adapter,
            targets,
            [],
            [],
            [],
            false));
        return new SetupPlannedChangeSet(privatePlan, plannedChangeSet, targets);
    }

    void ISetupApplyRevalidator.Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plannedChangeSet);
        SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);
        Resolve(plan.Adapter).Revalidate(plan, plannedChangeSet);
    }

    private static SetupTargetResult ToPlanTarget(SetupChangeRecord record) => new(
        record.RecordId.ToString("D"),
        record.TargetKind,
        record.TargetLabel,
        record.StatusProjection.Detected,
        record.StatusProjection.DetectedVersion,
        record.StatusProjection.Operation,
        record.StatusProjection.EffectiveSource,
        null,
        null,
        record.RestartRequirement,
        false,
        record.StatusProjection.Endpoint,
        record.StatusProjection.ExpectedResult,
        record.Guidance,
        record.StatusProjection.Changes);

    private static void RequireMatchingRequest(SetupChangePlan aggregate, SetupPlanRequest request)
    {
        if (aggregate.ChangeSetId != request.ChangeSetId ||
            !string.Equals(aggregate.Adapter, request.Adapter, StringComparison.Ordinal) ||
            !string.Equals(aggregate.SelectedTarget, request.SelectedTarget, StringComparison.Ordinal) ||
            aggregate.CreatedAt != request.CreatedAt ||
            !string.Equals(aggregate.ToolVersion, request.ToolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Setup adapter returned a plan with mismatched identity.");
        }
    }

    private static void ValidateGuidanceConsistency(IEnumerable<SetupChangeRecord> records)
    {
        foreach (var record in records)
        {
            if (record is null)
            {
                throw new InvalidOperationException("Setup adapter returned an invalid record.");
            }

            if (record.StatusProjection is null)
            {
                throw new InvalidOperationException("Setup adapter returned an invalid status projection.");
            }

            var snapshotGuidance = record.StatusProjection.Guidance;
            if (record.Guidance is null && snapshotGuidance is null)
            {
                continue;
            }

            if (record.Guidance is null || snapshotGuidance is null ||
                !string.Equals(record.Guidance.Kind, snapshotGuidance.Kind, StringComparison.Ordinal) ||
                !string.Equals(record.Guidance.Language, snapshotGuidance.Language, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Setup adapter returned inconsistent guidance.");
            }
        }
    }

    private static void EnsureAdapterId(string? adapterId, string parameterName)
    {
        if (adapterId is null || adapterId.Length > 128 || !AdapterIdPattern.IsMatch(adapterId))
        {
            throw new ArgumentException("Setup adapter ID must be a lowercase slug.", parameterName);
        }
    }
}

internal sealed class SetupAdapterNotRegisteredException : Exception
{
    public SetupAdapterNotRegisteredException(string adapterId)
        : base("setup_adapter_not_registered")
    {
        AdapterId = adapterId;
    }

    public string AdapterId { get; }
}

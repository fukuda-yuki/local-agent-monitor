using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters;

internal sealed class SetupAdapterRegistry : ISetupApplyRevalidator
{
    private const int MaximumTargets = 16;
    private const int MaximumChangesPerTarget = 32;

    private static readonly Regex AdapterIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex DiagnosticCodePattern = new(
        "^[a-z][a-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TargetLabelPattern = new(
        "^[a-z][a-z0-9_-]{0,127}$",
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

    public SetupPlanResult<SetupPlannedChangeSet> Plan(SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adapter = Resolve(request.Adapter);
        var result = adapter.Plan(request) ?? throw SetupPlanResult.InvalidOutput();
        ValidateDiagnostics(result);

        if (result is SetupPlanFailure<SetupChangePlan> failure)
        {
            ValidateDiagnosticCode(failure.Code);
            return SetupPlanResult.Failure<SetupPlannedChangeSet>(
                failure.Code,
                failure.Warnings,
                failure.NextActions);
        }

        if (result is not SetupPlanSuccess<SetupChangePlan> success)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        ValidateTargets(success.Targets);
        var aggregate = SetupPlanResult.SnapshotPlan(success.Value);
        RequireMatchingRequest(aggregate, request);
        var records = aggregate.Records.ToArray();
        ValidateGuidanceConsistency(records);
        ValidateTargetIdentity(records, success.Targets);

        var privatePlan = new SetupPrivatePlan(
            1,
            aggregate.ChangeSetId,
            aggregate.Adapter,
            aggregate.SelectedTarget,
            aggregate.CreatedAt,
            aggregate.ToolVersion,
            Array.AsReadOnly(records.Select(record => new SetupPrivatePlanTarget(
                    record.RecordId,
                    record.TargetKind,
                    record.TargetLocation,
                    record.BaseStateHash,
                    record.DesiredState,
                    record.Members))
                .ToArray()));

        var plannedChangeSet = new SetupLedgerChangeSet(
            aggregate.ChangeSetId,
            aggregate.Adapter,
            aggregate.SelectedTarget,
            aggregate.CreatedAt,
            aggregate.CreatedAt,
            aggregate.ToolVersion,
            null,
            SetupChangeSetState.Planned,
            Array.AsReadOnly(records.Select(record => new SetupLedgerTarget(
                    record.RecordId,
                    record.TargetKind,
                    record.TargetLabel,
                    aggregate.Adapter,
                    Array.AsReadOnly(record.Members
                        .Select(member => new SetupLedgerMember(member.SettingKey, member.Operation))
                        .ToArray()),
                    record.BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    record.RestartRequirement,
                    record.StatusProjection,
                    aggregate.ToolVersion))
                .ToArray()));

        try
        {
            SetupStorageValidation.ValidatePlanAndLedger(privatePlan, plannedChangeSet);
        }
        catch (Exception exception) when (
            exception is FormatException ||
            exception is SetupStorageException { Code: SetupCodes.RecoveryRequired })
        {
            throw SetupPlanResult.InvalidOutput();
        }
        return SetupPlanResult.Success(
            new SetupPlannedChangeSet(privatePlan, plannedChangeSet),
            success.Targets,
            success.Warnings,
            success.NextActions);
    }

    SetupPlanResult<SetupRevalidation> ISetupApplyRevalidator.Revalidate(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plannedChangeSet);
        SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);

        if (!adapters.TryGetValue(plan.Adapter, out var adapter))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedAdapter);
        }

        var result = adapter.Revalidate(plan, plannedChangeSet) ?? throw SetupPlanResult.InvalidOutput();
        ValidateDiagnostics(result);

        if (result is SetupPlanFailure<SetupRevalidation> failure)
        {
            ValidateDiagnosticCode(failure.Code);
            return SetupPlanResult.Failure<SetupRevalidation>(
                failure.Code,
                failure.Warnings,
                failure.NextActions);
        }

        if (result is not SetupPlanSuccess<SetupRevalidation> success || success.Targets.Count != 0)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        return SetupPlanResult.Revalidated(success.Warnings, success.NextActions);
    }

    private static void RequireMatchingRequest(SetupChangePlan aggregate, SetupPlanRequest request)
    {
        if (aggregate.ChangeSetId != request.ChangeSetId ||
            !string.Equals(aggregate.Adapter, request.Adapter, StringComparison.Ordinal) ||
            !string.Equals(aggregate.SelectedTarget, request.SelectedTarget, StringComparison.Ordinal) ||
            aggregate.CreatedAt != request.CreatedAt ||
            !string.Equals(aggregate.ToolVersion, request.ToolVersion, StringComparison.Ordinal))
        {
            throw SetupPlanResult.InvalidOutput();
        }
    }

    private static void ValidateGuidanceConsistency(IEnumerable<SetupChangeRecord> records)
    {
        foreach (var record in records)
        {
            if (record is null)
            {
                throw SetupPlanResult.InvalidOutput();
            }

            if (record.StatusProjection is null)
            {
                throw SetupPlanResult.InvalidOutput();
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
                throw SetupPlanResult.InvalidOutput();
            }
        }
    }

    private static void ValidateDiagnostics<T>(SetupPlanResult<T> result)
        where T : class
    {
        foreach (var warning in result.Warnings)
        {
            ValidateDiagnosticCode(warning);
        }

        foreach (var nextAction in result.NextActions)
        {
            ValidateDiagnosticCode(nextAction);
        }
    }

    private static void ValidateTargets(IReadOnlyList<SetupPlanTarget> targets)
    {
        if (targets.Count > MaximumTargets)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var recordIds = new HashSet<Guid>();
        foreach (var target in targets)
        {
            if (target is null ||
                target.RecordId == Guid.Empty ||
                !recordIds.Add(target.RecordId) ||
                !Enum.IsDefined(target.TargetKind) ||
                target.TargetLabel is null ||
                !TargetLabelPattern.IsMatch(target.TargetLabel) ||
                target.DetectedVersion?.Length > 128 ||
                !Enum.IsDefined(target.Operation) ||
                target.EffectiveSource is { } effectiveSource && !Enum.IsDefined(effectiveSource) ||
                !Enum.IsDefined(target.RestartRequirement) ||
                target.Changes is null ||
                target.Changes.Count > MaximumChangesPerTarget ||
                target.Changes.Any(change => change is null || !Enum.IsDefined(change.Operation)))
            {
                throw SetupPlanResult.InvalidOutput();
            }
        }
    }

    private static void ValidateDiagnosticCode(string? code)
    {
        if (code is null || !DiagnosticCodePattern.IsMatch(code))
        {
            throw SetupPlanResult.InvalidOutput();
        }
    }

    private static void ValidateTargetIdentity(
        IReadOnlyList<SetupChangeRecord> records,
        IReadOnlyList<SetupPlanTarget> targets)
    {
        if (records.Count != targets.Count)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        for (var index = 0; index < records.Count; index++)
        {
            var expected = SetupPlanTarget.FromRecord(records[index]);
            var actual = targets[index];
            if (expected.RecordId != actual.RecordId ||
                expected.TargetKind != actual.TargetKind ||
                !string.Equals(expected.TargetLabel, actual.TargetLabel, StringComparison.Ordinal) ||
                expected.Detected != actual.Detected ||
                !string.Equals(expected.DetectedVersion, actual.DetectedVersion, StringComparison.Ordinal) ||
                expected.Operation != actual.Operation ||
                expected.EffectiveSource != actual.EffectiveSource ||
                expected.RestartRequirement != actual.RestartRequirement ||
                expected.ProspectiveRollbackAvailable != actual.ProspectiveRollbackAvailable ||
                !string.Equals(expected.Endpoint, actual.Endpoint, StringComparison.Ordinal) ||
                !JsonEquals(expected.ExpectedResult, actual.ExpectedResult) ||
                !GuidanceEquals(expected.Guidance, actual.Guidance) ||
                !expected.Changes.SequenceEqual(actual.Changes))
            {
                throw SetupPlanResult.InvalidOutput();
            }
        }
    }

    private static bool JsonEquals(
        System.Text.Json.JsonElement? left,
        System.Text.Json.JsonElement? right) =>
        left is null && right is null ||
        left is { } leftValue && right is { } rightValue &&
        string.Equals(leftValue.GetRawText(), rightValue.GetRawText(), StringComparison.Ordinal);

    private static bool GuidanceEquals(SetupGuidance? left, SetupGuidance? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Language, right.Language, StringComparison.Ordinal) &&
        string.Equals(left.Sample, right.Sample, StringComparison.Ordinal);

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

using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Cli;

internal sealed class SetupCommandDispatcher
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupAdapterRegistry adapterRegistry;
    private readonly string toolVersion;
    private readonly Func<SetupLock, SetupRecoveryResult> recover;

    public SetupCommandDispatcher(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore,
        SetupAdapterRegistry adapterRegistry,
        string toolVersion)
        : this(
            platform ?? throw new ArgumentNullException(nameof(platform)),
            paths ?? throw new ArgumentNullException(nameof(paths)),
            ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore)),
            adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry)),
            toolVersion ?? throw new ArgumentNullException(nameof(toolVersion)),
            CreateRecovery(
                platform,
                paths,
                planStore ?? throw new ArgumentNullException(nameof(planStore)),
                ledgerStore,
                journalStore ?? throw new ArgumentNullException(nameof(journalStore))))
    {
    }

    internal SetupCommandDispatcher(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupLedgerStore ledgerStore,
        SetupAdapterRegistry adapterRegistry,
        string toolVersion,
        Func<SetupLock, SetupRecoveryResult> recover)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        this.adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        this.toolVersion = toolVersion ?? throw new ArgumentNullException(nameof(toolVersion));
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
    }

    private static Func<SetupLock, SetupRecoveryResult> CreateRecovery(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore) =>
        new SetupRecoveryCoordinator(platform, paths, planStore, ledgerStore, journalStore).RecoverNext;

    public SetupCommandResult Dispatch(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Command == SetupCommand.Plan
            ? DispatchPlan(options)
            : Validate(Unimplemented(options));
    }

    private SetupCommandResult DispatchPlan(SetupOptions options)
    {
        try
        {
            using var acquisition = SetupLock.TryAcquire(platform, paths);
            if (!acquisition.Acquired)
            {
                return Validate(Failure(SetupCodes.SetupBusy, options.Adapter));
            }

            var recovery = recover(acquisition.Lock!);
            if (recovery.Disposition != SetupRecoveryDisposition.None)
            {
                return Validate(RecoveryResult(recovery, options.Adapter));
            }

            if (recovery.Code is not null ||
                recovery.RecoveredChangeSetId is not null ||
                recovery.Operation is not null ||
                recovery.EffectiveChangeSet is not null)
            {
                return Validate(Failure(SetupCodes.InternalError, options.Adapter));
            }

            var request = new SetupPlanRequest(
                options.Adapter!,
                options.Target!,
                options.Endpoint!,
                options.IncludeContentCapture,
                platform.Identifiers.CreateUuidV7(),
                platform.Clock.UtcNow,
                toolVersion);
            var plan = adapterRegistry.Plan(request);
            if (plan is SetupPlanFailure<SetupPlannedChangeSet> failure)
            {
                return Validate(Failure(
                    failure.Code,
                    options.Adapter,
                    failure.Warnings,
                    failure.NextActions));
            }

            if (plan is not SetupPlanSuccess<SetupPlannedChangeSet> success)
            {
                return Validate(Failure(SetupCodes.InternalError, options.Adapter));
            }

            var targets = Array.AsReadOnly(success.Targets.Select(Project).ToArray());
            var code = success.Targets.All(target =>
                target.TargetKind == SetupTargetKind.Guidance || target.Operation == SetupOperation.NoOp)
                ? SetupCodes.NoChanges
                : SetupCodes.PlanReady;
            var result = Validate(new SetupCommandResult(
                SetupCommand.Plan,
                true,
                code,
                request.ChangeSetId.ToString("D"),
                null,
                null,
                options.Adapter,
                targets,
                [],
                Snapshot(success.Warnings),
                Snapshot(success.NextActions),
                false));
            ledgerStore.PersistPlannedChangeSet(
                acquisition.Lock!,
                success.Value.PrivatePlan,
                success.Value.PlannedChangeSet);
            return result;
        }
        catch (SetupAdapterNotRegisteredException)
        {
            return Validate(Failure(SetupCodes.UnsupportedAdapter, options.Adapter));
        }
        catch (SetupStorageException exception)
        {
            return Validate(Failure(MapStorageCode(exception.Code), options.Adapter));
        }
        catch (Exception)
        {
            return Validate(Failure(SetupCodes.InternalError, options.Adapter));
        }
    }

    private static SetupCommandResult RecoveryResult(
        SetupRecoveryResult recovery,
        string? adapter)
    {
        if (recovery.Disposition == SetupRecoveryDisposition.Recovered &&
            recovery.RecoveredChangeSetId is { } recoveredId &&
            recovery.Operation is { } operation &&
            recovery.Code == RecoveredCode(operation) &&
            IsRecoveredEvidence(recovery.EffectiveChangeSet, recoveredId, operation, recovery.Code))
        {
            return new SetupCommandResult(
                SetupCommand.Plan,
                true,
                recovery.Code,
                null,
                recoveredId.ToString("D"),
                operation,
                adapter,
                [],
                [],
                [],
                [SetupCodes.RerunRequestedSetupCommand],
                false);
        }

        if (recovery.Disposition == SetupRecoveryDisposition.Failed &&
            recovery.Code == SetupCodes.InterruptedRecoveryFailed &&
            recovery.RecoveredChangeSetId is { } failedId &&
            recovery.Operation is { } failedOperation &&
            Enum.IsDefined(failedOperation) &&
            IsFailedEvidence(recovery.EffectiveChangeSet, failedId))
        {
            return new SetupCommandResult(
                SetupCommand.Plan,
                false,
                SetupCodes.InterruptedRecoveryFailed,
                null,
                failedId.ToString("D"),
                failedOperation,
                adapter,
                [],
                [],
                [],
                [],
                false);
        }

        if (recovery.Disposition == SetupRecoveryDisposition.Failed &&
            recovery.Code == SetupCodes.RecoveryRequired &&
            recovery.RecoveredChangeSetId is { } recoveryRequiredId &&
            (recovery.Operation is null || Enum.IsDefined(recovery.Operation.Value)) &&
            IsFailedEvidence(recovery.EffectiveChangeSet, recoveryRequiredId))
        {
            return Failure(SetupCodes.RecoveryRequired, adapter);
        }

        if (recovery.Disposition == SetupRecoveryDisposition.Failed &&
            recovery.RecoveredChangeSetId is null &&
            recovery.Operation is null &&
            recovery.EffectiveChangeSet is null)
        {
            return Failure(MapRecoveryCode(recovery.Code), adapter);
        }

        return Failure(SetupCodes.InternalError, adapter);
    }

    private static bool IsRecoveredEvidence(
        SetupLedgerChangeSet? effective,
        Guid recoveredId,
        SetupRecoveryOperation operation,
        string code) =>
        effective is not null &&
        effective.ChangeSetId == recoveredId &&
        string.Equals(effective.OutcomeCode, code, StringComparison.Ordinal) &&
        operation switch
        {
            SetupRecoveryOperation.Apply => effective.State is SetupChangeSetState.Applied or SetupChangeSetState.Restored,
            SetupRecoveryOperation.Rollback => effective.State == SetupChangeSetState.RolledBack,
            _ => false,
        };

    private static bool IsFailedEvidence(SetupLedgerChangeSet? effective, Guid failedId) =>
        effective is not null &&
        effective.ChangeSetId == failedId &&
        effective.State == SetupChangeSetState.Partial &&
        string.Equals(
            effective.OutcomeCode,
            SetupCodes.InterruptedRecoveryFailed,
            StringComparison.Ordinal);

    private static SetupTargetResult Project(SetupPlanTarget target) => new(
        target.RecordId.ToString("D"),
        target.TargetKind,
        target.TargetLabel,
        target.Detected,
        target.DetectedVersion,
        target.Operation,
        target.EffectiveSource,
        null,
        null,
        target.RestartRequirement,
        target.ProspectiveRollbackAvailable,
        target.Endpoint,
        target.ExpectedResult?.Clone(),
        target.Guidance is { } guidance
            ? new SetupGuidance(guidance.Kind, guidance.Language, guidance.Sample)
            : null,
        Array.AsReadOnly(target.Changes.Select(change => new SetupMemberChangeResult(
            change.SettingKey,
            change.Operation,
            change.PreviousState,
            change.NewState,
            change.Conflict,
            change.Managed)).ToArray()));

    internal static IReadOnlyList<SetupTargetResult> ProjectApplyTargets(
        SetupLedgerChangeSet changeSet,
        string code)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return Array.AsReadOnly(changeSet.Targets.Select(target => new SetupTargetResult(
            target.RecordId.ToString("D"),
            target.TargetKind,
            target.TargetLabel,
            target.StatusProjection.Detected,
            target.StatusProjection.DetectedVersion,
            target.StatusProjection.Operation,
            target.StatusProjection.EffectiveSource,
            null,
            null,
            target.RestartRequirement,
            HasAppliedOwnership(target, code),
            target.StatusProjection.Endpoint,
            target.StatusProjection.ExpectedResult?.Clone(),
            target.StatusProjection.Guidance is { } guidance
                ? SetupContractValidator.RehydrateStatusGuidance(guidance)
                : null,
            Array.AsReadOnly(target.StatusProjection.Changes.Select(change => new SetupMemberChangeResult(
                change.SettingKey,
                change.Operation,
                change.PreviousState,
                change.NewState,
                change.Conflict,
                change.Managed)).ToArray()))).ToArray());
    }

    private static bool HasAppliedOwnership(SetupLedgerTarget target, string code) =>
        code == SetupCodes.ApplySucceeded &&
        target.TargetKind != SetupTargetKind.Guidance &&
        target.Members.Any(member => member.Operation != SetupOperation.NoOp) &&
        target.AppliedStateHash is not null &&
        string.Equals(target.BackupReference, target.RecordId.ToString("D"), StringComparison.Ordinal) &&
        target.RollbackStatus == SetupLedgerRollbackStatus.Pending;

    private static SetupCommandResult Failure(
        string code,
        string? adapter,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? nextActions = null) => new(
        SetupCommand.Plan,
        false,
        code,
        null,
        null,
        null,
        adapter,
        [],
        [],
        Snapshot(warnings ?? []),
        Snapshot(nextActions ?? []),
        false);

    private static SetupCommandResult Unimplemented(SetupOptions options) => new(
        options.Command,
        false,
        SetupCodes.InternalError,
        options.Command is SetupCommand.Apply or SetupCommand.Rollback
            ? options.ChangeSetId?.ToString("D")
            : null,
        null,
        null,
        options.Adapter,
        [],
        [],
        [],
        [],
        false);

    private static SetupCommandResult Validate(SetupCommandResult result)
    {
        SetupContractValidator.Validate(result);
        return result;
    }

    private static IReadOnlyList<string> Snapshot(IEnumerable<string> values) =>
        Array.AsReadOnly(values.ToArray());

    private static string RecoveredCode(SetupRecoveryOperation operation) => operation switch
    {
        SetupRecoveryOperation.Apply => SetupCodes.InterruptedApplyRecovered,
        SetupRecoveryOperation.Rollback => SetupCodes.InterruptedRollbackRecovered,
        _ => SetupCodes.InternalError,
    };

    private static string MapRecoveryCode(string? code) => code switch
    {
        SetupCodes.LedgerCorrupt => SetupCodes.LedgerCorrupt,
        SetupCodes.LedgerVersionUnsupported => SetupCodes.LedgerVersionUnsupported,
        _ => SetupCodes.InternalError,
    };

    private static string MapStorageCode(string? code) => code switch
    {
        SetupCodes.RecoveryRequired => SetupCodes.RecoveryRequired,
        SetupCodes.LedgerCorrupt => SetupCodes.LedgerCorrupt,
        SetupCodes.LedgerVersionUnsupported => SetupCodes.LedgerVersionUnsupported,
        _ => SetupCodes.InternalError,
    };
}

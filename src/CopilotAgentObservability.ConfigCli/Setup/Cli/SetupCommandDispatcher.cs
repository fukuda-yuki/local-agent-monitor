using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Cli;

internal sealed class SetupCommandDispatcher
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;
    private readonly SetupLedgerStore ledgerStore;
    private readonly SetupAdapterRegistry adapterRegistry;
    private readonly string toolVersion;
    private readonly Func<SetupLock, SetupRecoveryResult> recover;
    private readonly Func<SetupLock, Guid, SetupPlanSuccess<SetupLedgerChangeSet>> apply;
    private readonly Func<SetupLock, Guid, SetupRollbackExecutionResult> rollback;
    private readonly Func<string?, SetupCommandResult> status;

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
            planStore ?? throw new ArgumentNullException(nameof(planStore)),
            ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore)),
            adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry)),
            toolVersion ?? throw new ArgumentNullException(nameof(toolVersion)),
            CreateRecovery(
                platform,
                paths,
                planStore,
                ledgerStore,
                journalStore ?? throw new ArgumentNullException(nameof(journalStore))),
            CreateApply(
                platform,
                paths,
                planStore,
                ledgerStore,
                journalStore,
                adapterRegistry),
            CreateRollback(
                platform,
                paths,
                planStore,
                ledgerStore,
                journalStore),
            CreateStatus(
                platform,
                paths,
                planStore,
                ledgerStore,
                journalStore))
    {
    }

    internal SetupCommandDispatcher(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupAdapterRegistry adapterRegistry,
        string toolVersion,
        Func<SetupLock, SetupRecoveryResult> recover,
        Func<SetupLock, Guid, SetupPlanSuccess<SetupLedgerChangeSet>> apply,
        Func<SetupLock, Guid, SetupRollbackExecutionResult> rollback,
        Func<string?, SetupCommandResult> status)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        this.adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        this.toolVersion = toolVersion ?? throw new ArgumentNullException(nameof(toolVersion));
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
        this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
        this.rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
    }

    private static Func<SetupLock, SetupRecoveryResult> CreateRecovery(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore) =>
        new SetupRecoveryCoordinator(platform, paths, planStore, ledgerStore, journalStore).RecoverNext;

    private static Func<SetupLock, Guid, SetupPlanSuccess<SetupLedgerChangeSet>> CreateApply(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore,
        SetupAdapterRegistry adapterRegistry) =>
        new SetupApplyCoordinator(
            platform,
            paths,
            planStore,
            ledgerStore,
            journalStore,
            adapterRegistry).Apply;

    private static Func<SetupLock, Guid, SetupRollbackExecutionResult> CreateRollback(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore) =>
        new SetupRollbackCoordinator(
            platform,
            paths,
            planStore,
            ledgerStore,
            journalStore).Rollback;

    private static Func<string?, SetupCommandResult> CreateStatus(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore) =>
        new SetupStatusService(
            platform,
            paths,
            planStore,
            ledgerStore,
            journalStore).Status;

    public SetupCommandResult Dispatch(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Command switch
        {
            SetupCommand.Plan => DispatchPlan(options),
            SetupCommand.Apply => DispatchApply(options),
            SetupCommand.Rollback => DispatchRollback(options),
            SetupCommand.Status => DispatchStatus(options),
            _ => throw new InvalidOperationException(SetupContractValidator.InvalidContractCode),
        };
    }

    private SetupCommandResult DispatchStatus(SetupOptions options) =>
        Validate(status(options.Adapter));

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
                return Validate(RecoveryResult(recovery, SetupCommand.Plan, null, options.Adapter));
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

    private SetupCommandResult DispatchApply(SetupOptions options)
    {
        var changeSetId = options.ChangeSetId!.Value;
        var correlationId = changeSetId.ToString("D");
        try
        {
            using var acquisition = SetupLock.TryAcquire(platform, paths);
            if (!acquisition.Acquired)
            {
                return Validate(ApplyFailure(SetupCodes.SetupBusy, correlationId, null));
            }

            var recovery = recover(acquisition.Lock!);
            if (recovery.Disposition != SetupRecoveryDisposition.None)
            {
                return Validate(RecoveryResult(
                    recovery,
                    SetupCommand.Apply,
                    correlationId,
                    null));
            }

            if (recovery.Code is not null ||
                recovery.RecoveredChangeSetId is not null ||
                recovery.Operation is not null ||
                recovery.EffectiveChangeSet is not null)
            {
                return Validate(ApplyFailure(SetupCodes.InternalError, correlationId, null));
            }

            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = ledger.ChangeSets.SingleOrDefault(candidate => candidate.ChangeSetId == changeSetId);
            if (changeSet is null)
            {
                return Validate(ApplyFailure(SetupCodes.InvalidArguments, correlationId, null));
            }

            SetupPrivatePlan? plan;
            try
            {
                plan = planStore.Load(changeSetId);
            }
            catch (Exception exception) when (
                exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
            {
                return Validate(ApplyFailure(
                    SetupCodes.RecoveryRequired,
                    correlationId,
                    changeSet.Adapter));
            }

            if (plan is null)
            {
                return Validate(ApplyFailure(
                    SetupCodes.RecoveryRequired,
                    correlationId,
                    changeSet.Adapter));
            }

            try
            {
                SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
            }
            catch (Exception exception) when (
                exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
            {
                return Validate(ApplyFailure(
                    SetupCodes.RecoveryRequired,
                    correlationId,
                    changeSet.Adapter));
            }

            if (changeSet.State != SetupChangeSetState.Planned)
            {
                return Validate(ApplyFailure(
                    SetupCodes.InvalidArguments,
                    correlationId,
                    changeSet.Adapter,
                    ProjectApplyTargets(changeSet, SetupCodes.InvalidArguments)));
            }

            try
            {
                SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet);
            }
            catch (Exception exception) when (
                exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
            {
                return Validate(ApplyFailure(
                    SetupCodes.RecoveryRequired,
                    correlationId,
                    changeSet.Adapter));
            }

            _ = Validate(new SetupCommandResult(
                SetupCommand.Apply,
                true,
                SetupCodes.ApplySucceeded,
                correlationId,
                null,
                null,
                changeSet.Adapter,
                ProjectApplyTargets(changeSet, SetupCodes.ApplySucceeded),
                [],
                [],
                [],
                false));

            try
            {
                _ = adapterRegistry.Resolve(changeSet.Adapter);
            }
            catch (SetupAdapterNotRegisteredException)
            {
                return Validate(ApplyFailure(
                    SetupCodes.UnsupportedAdapter,
                    correlationId,
                    changeSet.Adapter));
            }

            SetupPlanSuccess<SetupLedgerChangeSet> applied;
            try
            {
                applied = apply(acquisition.Lock!, changeSetId);
            }
            catch (SetupApplyException exception)
            {
                if (exception.Code is SetupCodes.UnsupportedAdapter or SetupCodes.UnsupportedTarget)
                {
                    return Validate(ApplyFailure(
                        exception.Code,
                        correlationId,
                        changeSet.Adapter));
                }

                if (!IsNormalApplyFailureCode(exception.Code))
                {
                    return Validate(ApplyFailure(
                        SetupCodes.InternalError,
                        correlationId,
                        null));
                }

                return Validate(ApplyFailure(
                    exception.Code,
                    correlationId,
                    changeSet.Adapter,
                    ApplyFailureTargets(changeSetId, plan, exception.Code),
                    exception.Failure.Warnings,
                    exception.Failure.NextActions));
            }

            var code = applied.Value.State switch
            {
                SetupChangeSetState.Applied => SetupCodes.ApplySucceeded,
                SetupChangeSetState.NoChanges => SetupCodes.NoChanges,
                _ => null,
            };
            if (code is null)
            {
                return Validate(ApplyFailure(
                    SetupCodes.InternalError,
                    correlationId,
                    changeSet.Adapter));
            }

            return Validate(new SetupCommandResult(
                SetupCommand.Apply,
                true,
                code,
                correlationId,
                null,
                null,
                changeSet.Adapter,
                ProjectApplyTargets(applied.Value, code),
                [],
                Snapshot(applied.Warnings),
                Snapshot(applied.NextActions),
                false));
        }
        catch (SetupStorageException exception)
        {
            return Validate(ApplyFailure(MapStorageCode(exception.Code), correlationId, null));
        }
        catch (Exception)
        {
            return Validate(ApplyFailure(SetupCodes.InternalError, correlationId, null));
        }
    }

    private SetupCommandResult DispatchRollback(SetupOptions options)
    {
        var changeSetId = options.ChangeSetId!.Value;
        var correlationId = changeSetId.ToString("D");
        try
        {
            using var acquisition = SetupLock.TryAcquire(platform, paths);
            if (!acquisition.Acquired)
            {
                return Validate(CommandFailure(
                    SetupCommand.Rollback,
                    SetupCodes.SetupBusy,
                    correlationId,
                    null));
            }

            var execution = rollback(acquisition.Lock!, changeSetId);
            return Validate(MapRollbackExecution(execution, changeSetId, correlationId));
        }
        catch (SetupStorageException exception)
        {
            return Validate(CommandFailure(
                SetupCommand.Rollback,
                MapStorageCode(exception.Code),
                correlationId,
                null));
        }
        catch (Exception)
        {
            return Validate(CommandFailure(
                SetupCommand.Rollback,
                SetupCodes.InternalError,
                correlationId,
                null));
        }
    }

    private static SetupCommandResult MapRollbackExecution(
        SetupRollbackExecutionResult? execution,
        Guid requestedChangeSetId,
        string correlationId)
    {
        if (execution is null ||
            execution.RequestedChangeSetId != requestedChangeSetId ||
            !IsRollbackExecutionCode(execution.Code) ||
            execution.Success != IsRollbackSuccessCode(execution.Code))
        {
            return CommandFailure(
                SetupCommand.Rollback,
                SetupCodes.InternalError,
                correlationId,
                null);
        }

        if (execution.Recovery is not null)
        {
            return IsValidRollbackRecoveryEnvelope(execution)
                ? RecoveryResult(
                    execution.Recovery,
                    SetupCommand.Rollback,
                    correlationId,
                    null)
                : CommandFailure(
                    SetupCommand.Rollback,
                    SetupCodes.InternalError,
                    correlationId,
                    null);
        }

        if (!IsValidDirectRollbackEnvelope(execution))
        {
            return CommandFailure(
                SetupCommand.Rollback,
                SetupCodes.InternalError,
                correlationId,
                null);
        }

        if (execution.Code == SetupCodes.RollbackSucceeded)
        {
            return execution.ChangeSet is { } succeeded
                ? new SetupCommandResult(
                    SetupCommand.Rollback,
                    true,
                    SetupCodes.RollbackSucceeded,
                    correlationId,
                    null,
                    null,
                    succeeded.Adapter,
                    ProjectApplyTargets(succeeded, SetupCodes.RollbackSucceeded),
                    [],
                    [],
                    [],
                    false)
                : CommandFailure(
                    SetupCommand.Rollback,
                    SetupCodes.InternalError,
                    correlationId,
                    null);
        }

        if (execution.Code is SetupCodes.InvalidArguments or
            SetupCodes.LedgerCorrupt or
            SetupCodes.LedgerVersionUnsupported)
        {
            return execution.ChangeSet is null
                ? CommandFailure(
                    SetupCommand.Rollback,
                    execution.Code,
                    correlationId,
                    null)
                : CommandFailure(
                    SetupCommand.Rollback,
                    SetupCodes.InternalError,
                    correlationId,
                    null);
        }

        if (IsNormalRollbackFailureCode(execution.Code))
        {
            return CommandFailure(
                SetupCommand.Rollback,
                execution.Code,
                correlationId,
                execution.ChangeSet?.Adapter,
                execution.ChangeSet is { } trusted
                    ? ProjectApplyTargets(trusted, execution.Code)
                    : []);
        }

        return CommandFailure(
            SetupCommand.Rollback,
            SetupCodes.InternalError,
            correlationId,
            null);
    }

    private static bool IsRollbackExecutionCode(string code) => code is
        SetupCodes.InvalidArguments or
        SetupCodes.RollbackSucceeded or
        SetupCodes.RollbackNotAvailable or
        SetupCodes.RollbackStale or
        SetupCodes.UnsafePath or
        SetupCodes.PartialRollback or
        SetupCodes.RecoveryRequired or
        SetupCodes.InternalError or
        SetupCodes.InterruptedApplyRecovered or
        SetupCodes.InterruptedRollbackRecovered or
        SetupCodes.InterruptedRecoveryFailed or
        SetupCodes.LedgerCorrupt or
        SetupCodes.LedgerVersionUnsupported;

    private static bool IsRollbackSuccessCode(string code) => code is
        SetupCodes.RollbackSucceeded or
        SetupCodes.InterruptedApplyRecovered or
        SetupCodes.InterruptedRollbackRecovered;

    private static bool IsNormalRollbackFailureCode(string code) => code is
        SetupCodes.RollbackNotAvailable or
        SetupCodes.RollbackStale or
        SetupCodes.UnsafePath or
        SetupCodes.PartialRollback or
        SetupCodes.RecoveryRequired or
        SetupCodes.InternalError;

    private static bool IsValidDirectRollbackEnvelope(SetupRollbackExecutionResult execution) =>
        execution.Code switch
        {
            SetupCodes.RollbackSucceeded => execution.ChangeSet is
            {
                State: SetupChangeSetState.RolledBack,
                OutcomeCode: SetupCodes.RollbackSucceeded,
            },
            SetupCodes.RollbackNotAvailable =>
                execution.ChangeSet?.OutcomeCode == SetupCodes.RollbackNotAvailable,
            SetupCodes.RollbackStale => execution.ChangeSet is
            {
                State: SetupChangeSetState.Applied,
                OutcomeCode: SetupCodes.RollbackStale,
            },
            SetupCodes.UnsafePath => execution.ChangeSet?.State == SetupChangeSetState.Applied,
            SetupCodes.PartialRollback => execution.ChangeSet is
            {
                State: SetupChangeSetState.Partial,
                OutcomeCode: SetupCodes.PartialRollback,
            },
            SetupCodes.RecoveryRequired or SetupCodes.InternalError => true,
            SetupCodes.InvalidArguments or
            SetupCodes.LedgerCorrupt or
            SetupCodes.LedgerVersionUnsupported => execution.ChangeSet is null,
            _ => false,
        };

    private static bool IsValidRollbackRecoveryEnvelope(SetupRollbackExecutionResult execution)
    {
        var recovery = execution.Recovery!;
        if (execution.ChangeSet is not null ||
            !string.Equals(execution.Code, recovery.Code, StringComparison.Ordinal))
        {
            return false;
        }

        return recovery.Disposition switch
        {
            SetupRecoveryDisposition.Recovered => execution.Code is
                SetupCodes.InterruptedApplyRecovered or
                SetupCodes.InterruptedRollbackRecovered,
            SetupRecoveryDisposition.Failed => execution.Code is
                SetupCodes.InterruptedRecoveryFailed or
                SetupCodes.RecoveryRequired or
                SetupCodes.LedgerCorrupt or
                SetupCodes.LedgerVersionUnsupported,
            _ => false,
        };
    }

    private static SetupCommandResult RecoveryResult(
        SetupRecoveryResult recovery,
        SetupCommand command,
        string? changeSetId,
        string? adapter)
    {
        if (recovery.Disposition == SetupRecoveryDisposition.Recovered &&
            recovery.RecoveredChangeSetId is { } recoveredId &&
            recovery.Operation is { } operation &&
            recovery.Code == RecoveredCode(operation) &&
            IsRecoveredEvidence(recovery.EffectiveChangeSet, recoveredId, operation, recovery.Code))
        {
            return new SetupCommandResult(
                command,
                true,
                recovery.Code,
                changeSetId,
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
                command,
                false,
                SetupCodes.InterruptedRecoveryFailed,
                changeSetId,
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
            return CommandFailure(command, SetupCodes.RecoveryRequired, changeSetId, adapter);
        }

        if (recovery.Disposition == SetupRecoveryDisposition.Failed &&
            recovery.RecoveredChangeSetId is null &&
            recovery.Operation is null &&
            recovery.EffectiveChangeSet is null)
        {
            return CommandFailure(command, MapRecoveryCode(recovery.Code), changeSetId, adapter);
        }

        return CommandFailure(command, SetupCodes.InternalError, changeSetId, adapter);
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

    private static bool IsNormalApplyFailureCode(string code) => code is
        SetupCodes.TargetNotInstalled or
        SetupCodes.UnsupportedVersion or
        SetupCodes.ManagedPolicyConflict or
        SetupCodes.EnvironmentOverrideConflict or
        SetupCodes.MalformedSettings or
        SetupCodes.PermissionDenied or
        SetupCodes.UnsafePath or
        SetupCodes.StalePlan or
        SetupCodes.PortOwnedByForeignProcess or
        SetupCodes.PartialApply or
        SetupCodes.RecoveryRequired or
        SetupCodes.InternalError;

    private IReadOnlyList<SetupTargetResult> ApplyFailureTargets(
        Guid changeSetId,
        SetupPrivatePlan plan,
        string code)
    {
        try
        {
            var changeSet = ledgerStore.LoadForRecovery().ChangeSets
                .SingleOrDefault(candidate => candidate.ChangeSetId == changeSetId);
            if (changeSet is null)
            {
                return [];
            }

            SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
            return ProjectApplyTargets(changeSet, code);
        }
        catch (Exception exception) when (
            exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

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

    private static SetupCommandResult ApplyFailure(
        string code,
        string changeSetId,
        string? adapter,
        IReadOnlyList<SetupTargetResult>? targets = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? nextActions = null) => CommandFailure(
        SetupCommand.Apply,
        code,
        changeSetId,
        adapter,
        targets,
        warnings,
        nextActions);

    private static SetupCommandResult CommandFailure(
        SetupCommand command,
        string code,
        string? changeSetId,
        string? adapter,
        IReadOnlyList<SetupTargetResult>? targets = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? nextActions = null) => command == SetupCommand.Plan
        ? Failure(code, adapter, warnings, nextActions)
        : new SetupCommandResult(
            command,
            false,
            code,
            changeSetId,
            null,
            null,
            adapter,
            targets ?? [],
            [],
            Snapshot(warnings ?? []),
            Snapshot(nextActions ?? []),
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

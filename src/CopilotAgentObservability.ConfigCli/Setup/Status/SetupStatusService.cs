using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Status;

internal sealed class SetupStatusService
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupLedgerStore ledgerStore;
    private readonly Func<SetupLock, SetupRecoveryResult> recover;
    private readonly Func<SetupLedgerChangeSet, SetupChangeSetStatusResult> project;
    private readonly Func<SetupLedgerChangeSet, SetupLedgerChangeSet, SetupChangeSetStatusResult> projectFailedRecovery;

    public SetupStatusService(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupPlanStore planStore,
        SetupLedgerStore ledgerStore,
        SetupTransactionJournalStore journalStore)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        var recoveryCoordinator = new SetupRecoveryCoordinator(
            platform,
            paths,
            planStore ?? throw new ArgumentNullException(nameof(planStore)),
            ledgerStore,
            journalStore ?? throw new ArgumentNullException(nameof(journalStore)));
        var projector = new SetupStatusProjector(platform, paths, planStore, journalStore);
        recover = recoveryCoordinator.RecoverNext;
        project = projector.Project;
        projectFailedRecovery = projector.ProjectFailedRecovery;
    }

    internal SetupStatusService(
        ISetupPlatform platform,
        SetupRuntimePaths paths,
        SetupLedgerStore ledgerStore,
        Func<SetupLock, SetupRecoveryResult> recover,
        Func<SetupLedgerChangeSet, SetupChangeSetStatusResult> project,
        Func<SetupLedgerChangeSet, SetupLedgerChangeSet, SetupChangeSetStatusResult> projectFailedRecovery)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.projectFailedRecovery = projectFailedRecovery ??
            throw new ArgumentNullException(nameof(projectFailedRecovery));
    }

    public SetupCommandResult Status(string? adapterFilter)
    {
        try
        {
            using var acquisition = SetupLock.TryAcquire(platform, paths);
            if (!acquisition.Acquired)
            {
                return Failure(SetupCodes.SetupBusy, adapterFilter);
            }

            var recovery = recover(acquisition.Lock!);
            if (recovery.Disposition == SetupRecoveryDisposition.Failed &&
                recovery.Code != SetupCodes.InterruptedRecoveryFailed)
            {
                return Failure(MapFailureCode(recovery.Code), adapterFilter);
            }

            SetupOwnershipLedger ledger;
            try
            {
                ledger = ledgerStore.Load();
            }
            catch (SetupStorageException exception)
            {
                return Failure(MapFailureCode(exception.Code), adapterFilter);
            }

            var composition = Compose(recovery, ledger.ChangeSets);
            if (composition is null)
            {
                return Failure(SetupCodes.InternalError, adapterFilter);
            }

            var result = ResultFor(recovery, adapterFilter);
            return SetupStatusListProjector.Project(
                result,
                composition.ChangeSets,
                adapterFilter,
                null,
                changeSet => ProjectChangeSet(changeSet, composition));
        }
        catch (SetupStorageException exception)
        {
            return Failure(MapFailureCode(exception.Code), adapterFilter);
        }
        catch (Exception)
        {
            return Failure(SetupCodes.InternalError, adapterFilter);
        }
    }

    private SetupChangeSetStatusResult ProjectChangeSet(
        SetupLedgerChangeSet changeSet,
        SetupStatusComposition composition)
    {
        if (composition.RecoveredChangeSetId != changeSet.ChangeSetId)
        {
            return project(changeSet);
        }

        if (!composition.UseFailedRecoveryProjection)
        {
            return project(changeSet);
        }

        return projectFailedRecovery(composition.EvidenceChangeSet!, changeSet);
    }

    private static SetupStatusComposition? Compose(
        SetupRecoveryResult recovery,
        IReadOnlyList<SetupLedgerChangeSet> durableChangeSets)
    {
        if (recovery.Disposition == SetupRecoveryDisposition.None)
        {
            return new SetupStatusComposition(durableChangeSets, null, null, false);
        }

        if (recovery.RecoveredChangeSetId is not { } recoveredChangeSetId ||
            recovery.Operation is null ||
            recovery.EffectiveChangeSet is not { } effective ||
            effective.ChangeSetId != recoveredChangeSetId)
        {
            return null;
        }

        var changeSets = durableChangeSets.ToArray();
        var index = Array.FindIndex(changeSets, item => item.ChangeSetId == recoveredChangeSetId);
        if (index < 0)
        {
            return null;
        }

        if (recovery.Disposition == SetupRecoveryDisposition.Recovered)
        {
            changeSets[index] = effective;
            return new SetupStatusComposition(changeSets, recoveredChangeSetId, null, false);
        }

        if (recovery.Code != SetupCodes.InterruptedRecoveryFailed)
        {
            return null;
        }

        var durable = changeSets[index];
        SetupFailedRecoveryOverlayValidator.Validate(durable, effective, requireTerminalEvidence: false);
        changeSets[index] = effective;
        var useFailedRecoveryProjection = IsTerminal(durable.State);
        return new SetupStatusComposition(
            changeSets,
            recoveredChangeSetId,
            useFailedRecoveryProjection ? durable : null,
            useFailedRecoveryProjection);
    }

    private static SetupCommandResult ResultFor(SetupRecoveryResult recovery, string? adapterFilter) =>
        recovery.Disposition switch
        {
            SetupRecoveryDisposition.None => new SetupCommandResult(
                SetupCommand.Status,
                true,
                SetupCodes.StatusReady,
                null,
                null,
                null,
                adapterFilter,
                [],
                [],
                [],
                [],
                false),
            SetupRecoveryDisposition.Recovered => new SetupCommandResult(
                SetupCommand.Status,
                true,
                recovery.Code!,
                null,
                recovery.RecoveredChangeSetId!.Value.ToString("D"),
                recovery.Operation,
                adapterFilter,
                [],
                [],
                [],
                [SetupCodes.RerunRequestedSetupCommand],
                false),
            SetupRecoveryDisposition.Failed => new SetupCommandResult(
                SetupCommand.Status,
                false,
                SetupCodes.InterruptedRecoveryFailed,
                null,
                recovery.RecoveredChangeSetId!.Value.ToString("D"),
                recovery.Operation,
                adapterFilter,
                [],
                [],
                [],
                [],
                false),
            _ => throw new InvalidOperationException(),
        };

    private static SetupCommandResult Failure(string code, string? adapterFilter) => new(
        SetupCommand.Status,
        false,
        code,
        null,
        null,
        null,
        adapterFilter,
        [],
        [],
        [],
        [],
        false);

    private static string MapFailureCode(string? code) => code switch
    {
        SetupCodes.RecoveryRequired => SetupCodes.RecoveryRequired,
        SetupCodes.LedgerCorrupt => SetupCodes.LedgerCorrupt,
        SetupCodes.LedgerVersionUnsupported => SetupCodes.LedgerVersionUnsupported,
        _ => SetupCodes.InternalError,
    };

    private static bool IsTerminal(SetupChangeSetState state) => state is
        SetupChangeSetState.Applied or
        SetupChangeSetState.Restored or
        SetupChangeSetState.RolledBack;

    private sealed record SetupStatusComposition(
        IReadOnlyList<SetupLedgerChangeSet> ChangeSets,
        Guid? RecoveredChangeSetId,
        SetupLedgerChangeSet? EvidenceChangeSet,
        bool UseFailedRecoveryProjection);
}

using System.Collections;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal static class GitHubCopilotDoctorOrchestrator
{
    private const string DoctorAdapter = "github-copilot-doctor";

    public static DoctorResult EvaluateSetup(
        TimeProvider timeProvider,
        SetupCommandResult setupResult,
        string selectedTarget)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return TryMap(setupResult, selectedTarget, SetupUse.Evaluate, timeProvider.GetUtcNow(), out var snapshot)
            ? DoctorEvaluator.Evaluate(snapshot!)
            : Error(DoctorResultCode.InvalidArguments);
    }

    public static DoctorResult Start(
        string databasePath,
        TimeProvider timeProvider,
        SetupCommandResult setupResult,
        string selectedTarget,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!TryMap(setupResult, selectedTarget, SetupUse.Start, timeProvider.GetUtcNow(), out var snapshot))
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        return NormalizeVerification(Service(databasePath, timeProvider).Start(
            snapshot!.SourceSurface,
            DoctorAdapter,
            expiresAt));
    }

    public static DoctorResult Status(
        string databasePath,
        TimeProvider timeProvider,
        SetupCommandResult setupResult,
        string selectedTarget,
        string verificationId,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!TryMap(setupResult, selectedTarget, SetupUse.Status, timeProvider.GetUtcNow(), out var snapshot) ||
            expectedRevision <= 0)
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        var status = Service(databasePath, timeProvider).Status(verificationId);
        if (status.Verification is not { } verification)
        {
            return status;
        }
        if (!Matches(verification, snapshot!.SourceSurface))
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        return verification.Revision == expectedRevision
            ? status
            : Error(DoctorResultCode.VerificationStale);
    }

    public static DoctorResult ContinueSelected(
        string databasePath,
        TimeProvider timeProvider,
        SetupCommandResult setupResult,
        string selectedTarget,
        string verificationId,
        int expectedRevision,
        long rawRecordId,
        GitHubCopilotNativeSessionSelection? nativeSession)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!TryMap(setupResult, selectedTarget, SetupUse.Apply, timeProvider.GetUtcNow(), out var setupSnapshot) ||
            expectedRevision <= 0 || rawRecordId <= 0 ||
            selectedTarget == "app-sdk" && nativeSession is null)
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        var service = Service(databasePath, timeProvider);
        var status = service.Status(verificationId);
        if (status.Verification is not { } verification)
        {
            return status;
        }
        if (!Matches(verification, setupSnapshot!.SourceSurface))
        {
            return Error(DoctorResultCode.InvalidArguments);
        }
        if (verification.Revision != expectedRevision)
        {
            return Error(DoctorResultCode.VerificationStale);
        }
        if (status.Code != DoctorResultCode.VerificationActive ||
            verification.State != DoctorVerificationState.Active)
        {
            return status;
        }

        var evidence = GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            timeProvider,
            new GitHubCopilotDoctorEvidenceSelection(
                verificationId,
                selectedTarget,
                rawRecordId,
                nativeSession));
        if (evidence.ObservationResult.Code != DoctorResultCode.VerificationActive ||
            evidence.EvidenceRefs.Count == 0)
        {
            return evidence.ObservationResult;
        }

        var merged = Merge(setupSnapshot, evidence.Snapshot, hasRealEvidence: true);
        return service.Complete(
            verificationId,
            expectedRevision,
            merged,
            evidence.EvidenceRefs);
    }

    public static DoctorResult CancelAfterRollback(
        string databasePath,
        TimeProvider timeProvider,
        SetupCommandResult setupResult,
        string selectedTarget,
        string verificationId,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!TryMap(setupResult, selectedTarget, SetupUse.Rollback, timeProvider.GetUtcNow(), out var snapshot) ||
            expectedRevision <= 0)
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        var service = Service(databasePath, timeProvider);
        var status = service.Status(verificationId);
        if (status.Verification is not { } verification)
        {
            return status;
        }
        if (!Matches(verification, snapshot!.SourceSurface))
        {
            return Error(DoctorResultCode.InvalidArguments);
        }

        return service.Cancel(verificationId, expectedRevision);
    }

    private static DoctorFactSnapshot Merge(
        DoctorFactSnapshot setup,
        DoctorFactSnapshot runtime,
        bool hasRealEvidence) => setup with
    {
        ObservedAt = runtime.ObservedAt,
        VerificationId = runtime.VerificationId,
        SourceVersionAndSchemaDiagnostics = runtime.SourceVersionAndSchemaDiagnostics,
        LastIngest = runtime.LastIngest,
        RawPersistence = runtime.RawPersistence,
        Projection = runtime.Projection,
        ExactSessionBinding = runtime.ExactSessionBinding,
        CompletenessAndContent = runtime.CompletenessAndContent,
        RestartOrNewProcess = hasRealEvidence
            ? new RestartOrNewProcessFacts(RestartRequirement.NotRequired)
            : setup.RestartOrNewProcess,
    };

    private static bool TryMap(
        SetupCommandResult setupResult,
        string selectedTarget,
        SetupUse use,
        DateTimeOffset observedAt,
        out DoctorFactSnapshot? snapshot)
    {
        snapshot = null;
        if (setupResult is null)
        {
            return false;
        }

        try
        {
            SetupContractValidator.Validate(setupResult);
            if (!IsLifecycleAllowed(setupResult, selectedTarget, use))
            {
                return false;
            }
            snapshot = GitHubCopilotDoctorFactMapper.FromSetup(setupResult, selectedTarget, observedAt);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsLifecycleAllowed(
        SetupCommandResult? setupResult,
        string selectedTarget,
        SetupUse use)
    {
        if (setupResult is null || !setupResult.Success ||
            !string.Equals(setupResult.Adapter, "github-copilot", StringComparison.Ordinal) ||
            selectedTarget is not ("vscode" or "cli" or "app-sdk"))
        {
            return false;
        }

        if (use == SetupUse.Evaluate)
        {
            return true;
        }

        if (use == SetupUse.Rollback)
        {
            return setupResult.Command == SetupCommand.Rollback &&
                setupResult.Code == SetupCodes.RollbackSucceeded;
        }

        if (use is SetupUse.Apply or SetupUse.Start && setupResult.Command == SetupCommand.Apply)
        {
            return setupResult.Code is SetupCodes.ApplySucceeded or SetupCodes.NoChanges;
        }

        if (use is not (SetupUse.Start or SetupUse.Status) ||
            setupResult.Command != SetupCommand.Status ||
            setupResult.Code != SetupCodes.StatusReady ||
            setupResult.ChangeSets.Count != 1)
        {
            return false;
        }

        var changeSet = setupResult.ChangeSets[0];
        if (!string.Equals(changeSet.Adapter, "github-copilot", StringComparison.Ordinal) ||
            !string.Equals(changeSet.SelectedTarget, selectedTarget, StringComparison.Ordinal))
        {
            return false;
        }

        if (selectedTarget == "app-sdk")
        {
            return changeSet.State == SetupChangeSetState.NoChanges &&
                changeSet.CurrentState == SetupCurrentState.NotApplicable &&
                changeSet.Targets.Count == 1 &&
                changeSet.Targets[0].ReferenceState == SetupReferenceState.None &&
                changeSet.Targets[0].CurrentState == SetupCurrentState.NotApplicable;
        }

        return changeSet.State is SetupChangeSetState.Applied or SetupChangeSetState.NoChanges &&
            changeSet.CurrentState == SetupCurrentState.Current &&
            changeSet.Targets.Count is >= 1 and <= 2 &&
            changeSet.Targets.All(target =>
                target.ReferenceState == SetupReferenceState.Desired &&
                target.CurrentState == SetupCurrentState.Current);
    }

    private static bool Matches(DoctorVerification verification, string sourceSurface) =>
        string.Equals(verification.ExpectedSourceSurface, sourceSurface, StringComparison.Ordinal) &&
        string.Equals(verification.ExpectedSourceAdapter, DoctorAdapter, StringComparison.Ordinal);

    private static SqliteDoctorApplicationService Service(string databasePath, TimeProvider timeProvider) =>
        SqliteDoctorApplicationService.Create(new SqliteDoctorVerificationStore(databasePath, timeProvider));

    private static DoctorResult Error(DoctorResultCode code) => new(
        DoctorSchemaVersions.ResultV1,
        Success: false,
        code,
        Evaluation: null,
        Verification: null);

    private static DoctorResult NormalizeVerification(DoctorResult result) =>
        result.Verification is not { } verification
            ? result
            : result with
            {
                Verification = verification with
                {
                    AcceptedEvidenceRefs = new ValueEvidenceReferences(verification.AcceptedEvidenceRefs),
                },
            };

    private sealed class ValueEvidenceReferences(IReadOnlyList<string> values) :
        IReadOnlyList<string>,
        IEquatable<IReadOnlyList<string>>
    {
        public int Count => values.Count;

        public string this[int index] => values[index];

        public IEnumerator<string> GetEnumerator() => values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Equals(IReadOnlyList<string>? other) =>
            other is not null && values.SequenceEqual(other, StringComparer.Ordinal);

        public override bool Equals(object? obj) =>
            obj is IReadOnlyList<string> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in values)
            {
                hash.Add(value, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }
    }

    private enum SetupUse
    {
        Evaluate,
        Apply,
        Start,
        Status,
        Rollback,
    }
}

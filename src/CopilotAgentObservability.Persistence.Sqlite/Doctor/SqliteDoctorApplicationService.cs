using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteDoctorApplicationService
{
    private readonly SqliteDoctorVerificationStore store;
    private readonly Func<DoctorFactSnapshot, DoctorResult> evaluator;
    private readonly DoctorResultCode? initializationFailure;

    private SqliteDoctorApplicationService(
        SqliteDoctorVerificationStore store,
        Func<DoctorFactSnapshot, DoctorResult> evaluator,
        DoctorResultCode? initializationFailure)
    {
        this.store = store;
        this.evaluator = evaluator;
        this.initializationFailure = initializationFailure;
    }

    public static SqliteDoctorApplicationService Create(
        SqliteDoctorVerificationStore store,
        Func<DoctorFactSnapshot, DoctorResult>? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var initialization = store.CreateSchema();
        DoctorResultCode? failure = initialization.Code == DoctorResultCode.VerificationActive
            ? null
            : initialization.Code;
        return new(store, evaluator ?? DoctorEvaluator.Evaluate, failure);
    }

    public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => evaluator(snapshot);

    public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt) =>
        initializationFailure is { } failure
            ? Error(failure)
            : Project(store.Start(sourceSurface, sourceAdapter, expiresAt));

    public DoctorResult Status(string verificationId) =>
        initializationFailure is { } failure
            ? Error(failure)
            : Project(store.Get(verificationId));

    public DoctorResult ObserveCandidate(DoctorEvidenceCandidate candidate) =>
        initializationFailure is { } failure
            ? Error(failure)
            : Project(store.ObserveCandidate(candidate));

    public DoctorResult Complete(
        string verificationId,
        int expectedRevision,
        DoctorFactSnapshot snapshot,
        IReadOnlyList<string> acceptedEvidenceRefs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(acceptedEvidenceRefs);
        if (initializationFailure is { } failure)
        {
            return Error(failure);
        }
        if (!string.Equals(snapshot.SchemaVersion, DoctorSchemaVersions.FactsV1, StringComparison.Ordinal))
        {
            return Error(DoctorResultCode.UnsupportedSchemaVersion);
        }
        if (!DoctorValidation.IsValidFactSnapshot(snapshot)
            || snapshot.Observations.Count != 0
            || !string.Equals(snapshot.VerificationId, verificationId, StringComparison.Ordinal))
        {
            return Error(DoctorResultCode.InvalidInput);
        }

        DoctorResult? evaluation = null;
        var outcome = store.Complete(
            verificationId,
            expectedRevision,
            snapshot.SourceSurface,
            snapshot.ExpectedSourceAdapter,
            acceptedEvidenceRefs,
            candidates =>
            {
                var trustedSnapshot = snapshot with
                {
                    Observations = candidates.Select(ToObservation).ToArray(),
                };
                evaluation = evaluator(trustedSnapshot);
                return CompletionDecision(evaluation);
            });

        if (evaluation is null
            || outcome.Code is not (DoctorResultCode.EvaluationCompleted
                or DoctorResultCode.PartialFactSnapshot
                or DoctorResultCode.VerificationCompleted))
        {
            return Project(outcome);
        }

        return evaluation with
        {
            Success = outcome.Code == DoctorResultCode.VerificationCompleted || evaluation.Success,
            Code = outcome.Code == DoctorResultCode.VerificationCompleted
                ? DoctorResultCode.VerificationCompleted
                : evaluation.Code,
            Verification = outcome.Verification,
        };
    }

    public DoctorResult Cancel(string verificationId, int expectedRevision) =>
        initializationFailure is { } failure
            ? Error(failure)
            : Project(store.Cancel(verificationId, expectedRevision));

    private static DoctorObservation ToObservation(DoctorEvidenceCandidate candidate) => new(
        candidate.SourceSurface,
        candidate.SourceAdapter,
        candidate.EvidenceClass,
        candidate.EvidenceKind,
        candidate.EvidenceRef,
        candidate.ObservedAt);

    private static DoctorCompletionDecision CompletionDecision(DoctorResult result)
    {
        if (result.Code == DoctorResultCode.PartialFactSnapshot)
        {
            return DoctorCompletionDecision.Partial;
        }

        return result.Evaluation?.PrimaryState?.StateCode == DoctorStateCode.FirstTraceReady
            ? DoctorCompletionDecision.Ready
            : DoctorCompletionDecision.NotReady;
    }

    private static DoctorResult Project(DoctorStoreOutcome outcome) => new(
        DoctorSchemaVersions.ResultV1,
        Success: outcome.Code is DoctorResultCode.VerificationStarted
            or DoctorResultCode.VerificationActive
            or DoctorResultCode.VerificationCompleted
            or DoctorResultCode.VerificationCancelled,
        outcome.Code,
        Evaluation: null,
        outcome.Verification);

    private static DoctorResult Error(DoctorResultCode code) => new(
        DoctorSchemaVersions.ResultV1,
        Success: false,
        code,
        Evaluation: null,
        Verification: null);
}

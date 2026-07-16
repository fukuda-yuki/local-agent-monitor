namespace CopilotAgentObservability.Doctor;

public sealed record DoctorVerification(
    string VerificationId,
    string ExpectedSourceSurface,
    string? ExpectedSourceAdapter,
    DoctorVerificationState State,
    int Revision,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<string> AcceptedEvidenceRefs);

public sealed record DoctorEvidenceCandidate(
    string CandidateId,
    string VerificationId,
    string SourceSurface,
    string? SourceAdapter,
    DoctorEvidenceClass EvidenceClass,
    DoctorEvidenceKind EvidenceKind,
    string EvidenceRef,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt);

public enum DoctorVerificationState { Active, Completed, Cancelled, Expired }

public interface IDoctorClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IDoctorVerificationStore
{
    DoctorVerification Start(DoctorVerification verification);

    DoctorVerification? Find(string verificationId);

    void ObserveCandidate(DoctorEvidenceCandidate candidate);

    IReadOnlyList<DoctorEvidenceCandidate> ResolveCandidates(
        string verificationId,
        IReadOnlyList<string> evidenceRefs,
        DateTimeOffset observedAt);

    DoctorVerification? Complete(
        string verificationId,
        int expectedRevision,
        IReadOnlyList<string> acceptedEvidenceRefs,
        DateTimeOffset completedAt);

    DoctorVerification? Cancel(
        string verificationId,
        int expectedRevision,
        DateTimeOffset cancelledAt);
}

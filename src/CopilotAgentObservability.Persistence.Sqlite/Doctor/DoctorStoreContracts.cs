using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum DoctorCompletionDecision
{
    Ready,
    NotReady,
    Partial,
}

internal sealed record DoctorStoreOutcome(
    DoctorResultCode Code,
    DoctorVerification? Verification = null,
    IReadOnlyList<DoctorEvidenceCandidate>? Candidates = null,
    IReadOnlyList<DoctorVerification>? Verifications = null)
{
    public IReadOnlyList<DoctorEvidenceCandidate> ResolvedCandidates { get; } = Candidates ?? [];
}

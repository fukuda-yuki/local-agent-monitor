using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal sealed record FirstTraceEnvelope(
    string Command,
    bool Success,
    string Code,
    string? Adapter,
    string? SourceSurface,
    string? VerificationId,
    DoctorResult? Doctor,
    DoctorResult? EvaluationPreview,
    IReadOnlyList<FirstTraceGuidance> Guidance,
    IReadOnlyList<DoctorEvidenceCandidate> Candidates,
    bool Truncated);

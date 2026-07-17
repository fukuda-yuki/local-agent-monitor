using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal interface IFirstTraceSourceAdapter
{
    string AdapterId { get; }

    string SourceSurface { get; }

    string ExpectedSourceAdapter { get; }

    bool TryNormalizeEndpoint(string? endpoint, out string normalizedEndpoint);

    bool IsValidInteraction(string? interaction);

    DoctorFactSnapshot CollectFacts(
        string databasePath,
        string normalizedEndpoint,
        DoctorVerification? verification);

    IReadOnlyList<FirstTraceGuidance> GetGuidance(
        string? interaction,
        bool includeSetupPlan);

    FirstTraceEvidenceSelection SelectEvidence(
        IReadOnlyList<DoctorEvidenceCandidate> candidates,
        DateTimeOffset now);
}

internal sealed record FirstTraceGuidance(
    string Interaction,
    string Text,
    string? Command);

internal sealed record FirstTraceEvidenceSelection(
    bool RequiresExplicitSelection,
    IReadOnlyList<string> EvidenceRefs)
{
    public static FirstTraceEvidenceSelection Explicit { get; } = new(true, []);

    public static FirstTraceEvidenceSelection Auto(IReadOnlyList<string> evidenceRefs) =>
        new(false, evidenceRefs);
}

internal record FirstTraceRequest(
    string Command,
    string DatabasePath,
    string? Adapter,
    string? VerificationId,
    int? ExpectedRevision,
    string? Endpoint,
    string? Interaction,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> EvidenceRefs);

internal static class FirstTraceCodes
{
    public const string ContractVersion = "first_trace.v1";
    public const string VerificationStarted = "first_trace_verification_started";
    public const string Blocked = "first_trace_blocked";
    public const string ActiveVerificationExists = "active_verification_exists";
    public const string StatusReported = "first_trace_status_reported";
    public const string Completed = "first_trace_completed";
    public const string NotReady = "first_trace_not_ready";
    public const string Cancelled = "first_trace_cancelled";
    public const string ExplicitEvidenceSelectionRequired = "explicit_evidence_selection_required";
    public const string DoctorFailed = "first_trace_doctor_failed";
    public const string InvalidArguments = "invalid_arguments";
}

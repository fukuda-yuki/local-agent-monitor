namespace CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

internal sealed record ClaudeDoctorFactInputs(
    ClaudeLivenessProbeClassification LivenessProbe,
    bool? MonitorDatabaseFileExists,
    ClaudeSourceVersionClassification SourceVersion,
    string CanonicalMonitorOrigin,
    ClaudeEndpointValueClassification Endpoint,
    ClaudeProtocolValueClassification Protocol,
    ClaudeGateValueClassification TelemetryGate,
    ClaudeGateValueClassification ExporterGate,
    bool? ReadinessProbeSucceeded,
    ClaudeSourceCompatibilityClassification SourceCompatibility,
    ClaudeDoctorVerificationWindow? VerificationWindow,
    ClaudeEffectiveContentGate EffectiveContentGate,
    ClaudeRuntimeRawAccessClassification RuntimeRawAccess,
    ClaudeSetupLedgerClassification SetupLedger);

internal sealed record ClaudeDoctorVerificationWindow(
    bool AcceptedIngestExists,
    bool RejectedIngestExists,
    bool RawPersistenceCandidateExists,
    bool ProjectionCandidateExists,
    ClaudeProjectionEvidence ProjectionEvidence,
    bool ExactSessionBindingCandidateExists,
    ClaudeBoundSessionCompleteness BoundSessionCompleteness,
    ClaudeAgreedContentState AgreedContentState);

internal enum ClaudeLivenessProbeClassification
{
    MonitorLive,
    PositiveNoListener,
    OtherForeign,
    ProbeUnavailable,
}

internal enum ClaudeSourceVersionClassification
{
    Supported,
    Unsupported,
    Undetectable,
}

internal enum ClaudeEndpointValueClassification
{
    Match,
    Different,
    Absent,
    Conflict,
    Unreadable,
}

internal enum ClaudeProtocolValueClassification
{
    HttpProtobuf,
    Different,
    Absent,
    Conflict,
    Unreadable,
}

internal enum ClaudeGateValueClassification
{
    Enabled,
    Disabled,
    Absent,
    Conflict,
    Unreadable,
}

internal enum ClaudeSourceCompatibilityClassification
{
    NoRows,
    Matching,
    Drift,
    Incompatible,
    Unreadable,
}

internal enum ClaudeProjectionEvidence
{
    NotStarted,
    Pending,
    Failed,
}

internal enum ClaudeBoundSessionCompleteness
{
    Unavailable,
    Unbound,
    Partial,
    Rich,
    Full,
}

internal enum ClaudeAgreedContentState
{
    None,
    Available,
    Redacted,
    NotCaptured,
    Unsupported,
    Unreadable,
}

internal enum ClaudeEffectiveContentGate
{
    Enabled,
    Disabled,
    Unreadable,
}

internal enum ClaudeRuntimeRawAccessClassification
{
    Available,
    SanitizedOnly,
    Absent,
    Unreadable,
}

internal enum ClaudeSetupLedgerClassification
{
    NoAppliedChangeSet,
    AwaitingAcceptedIngest,
    AcceptedIngestAfterApply,
    Unreadable,
}

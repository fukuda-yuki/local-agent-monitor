namespace CopilotAgentObservability.Doctor;

public static class DoctorSchemaVersions
{
    public const string FactsV1 = "doctor.facts.v1";
    public const string ResultV1 = "doctor.v1";
}

public sealed record DoctorFactSnapshot(
    string SchemaVersion,
    string SourceSurface,
    string? ExpectedSourceAdapter,
    DateTimeOffset ObservedAt,
    string? VerificationId,
    IReadOnlyList<DoctorObservation> Observations,
    InstallAndSourceVersionFacts? InstallAndSourceVersion,
    ProcessReceiverAndPortFacts? ProcessReceiverAndPort,
    SourceEffectiveConfigurationFacts? SourceEffectiveConfiguration,
    EndpointReachabilityFacts? EndpointReachability,
    ProtocolAndSignalCompatibilityFacts? ProtocolAndSignalCompatibility,
    SourceVersionAndSchemaDiagnosticsFacts? SourceVersionAndSchemaDiagnostics,
    LastIngestFacts? LastIngest,
    RawPersistenceFacts? RawPersistence,
    ProjectionFacts? Projection,
    ExactSessionBindingFacts? ExactSessionBinding,
    CompletenessAndContentFacts? CompletenessAndContent,
    RestartOrNewProcessFacts? RestartOrNewProcess);

public sealed record DoctorObservation(
    string SourceSurface,
    string? SourceAdapter,
    DoctorEvidenceClass EvidenceClass,
    DoctorEvidenceKind EvidenceKind,
    string EvidenceRef,
    DateTimeOffset ObservedAt);

public sealed record InstallAndSourceVersionFacts(
    MonitorInstallStatus MonitorInstall,
    SourceVersionStatus SourceVersion,
    SourceFeatureStatus SourceFeature);

public sealed record ProcessReceiverAndPortFacts(
    MonitorProcessStatus MonitorProcess,
    ReceiverBindStatus ReceiverBind,
    PortOwnerStatus PortOwner);

public sealed record SourceEffectiveConfigurationFacts(EndpointAlignmentStatus EndpointAlignment);

public sealed record EndpointReachabilityFacts(ReachabilityStatus Reachability);

public sealed record ProtocolAndSignalCompatibilityFacts(
    ProtocolStatus Protocol,
    TraceSignalStatus TraceSignal);

public sealed record SourceVersionAndSchemaDiagnosticsFacts(
    SourceCompatibilityStatus Compatibility,
    SchemaStatus Schema);

public sealed record LastIngestFacts(LastIngestOutcome Outcome);

public sealed record RawPersistenceFacts(RawPersistenceOutcome Outcome);

public sealed record ProjectionFacts(ProjectionOutcome Outcome);

public sealed record ExactSessionBindingFacts(
    ExactSessionBindingRequirement Requirement,
    ExactSessionBindingOutcome Outcome);

public sealed record CompletenessAndContentFacts(
    DoctorCompleteness Completeness,
    ContentCaptureStatus ContentCapture,
    RawAccessStatus RawAccess);

public sealed record RestartOrNewProcessFacts(RestartRequirement Requirement);

public sealed record DoctorResult(
    string SchemaVersion,
    bool Success,
    DoctorResultCode Code,
    DoctorEvaluation? Evaluation,
    DoctorVerification? Verification);

public sealed record DoctorEvaluation(
    string SourceSurface,
    DoctorState? PrimaryState,
    IReadOnlyList<DoctorState> States,
    IReadOnlyList<string> MissingFactFamilies);

public sealed record DoctorState(
    string SchemaVersion,
    DoctorStateCode StateCode,
    DoctorSeverity Severity,
    string SourceSurface,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<DoctorStateCode> ReasonCodes,
    DoctorNextAction NextAction,
    DoctorRetryability Retryability,
    DateTimeOffset ObservedAt,
    string? VerificationId);

public enum MonitorInstallStatus { Unknown, Installed, NotInstalled }
public enum SourceVersionStatus { Unknown, Supported, Unsupported }
public enum SourceFeatureStatus { Unknown, Available, Unavailable }
public enum MonitorProcessStatus { Unknown, Running, NotRunning }
public enum ReceiverBindStatus { Unknown, Bound, NotBound }
public enum PortOwnerStatus { Unknown, Monitor, Foreign, None }
public enum EndpointAlignmentStatus { Unknown, Match, Mismatch }
public enum ReachabilityStatus { Unknown, Reachable, Unreachable }
public enum ProtocolStatus { Unknown, HttpProtobuf, Mismatch }
public enum TraceSignalStatus { Unknown, Enabled, Disabled }
public enum SourceCompatibilityStatus { Unknown, Supported, UnsupportedSourceVersion, FeatureUnavailable }
public enum SchemaStatus { Unknown, Matching, DriftDetected }
public enum LastIngestOutcome { Unknown, None, Accepted, Rejected }
public enum RawPersistenceOutcome { Unknown, NotPersisted, Persisted }
public enum ProjectionOutcome { Unknown, NotStarted, Pending, Completed, Failed }
public enum ExactSessionBindingRequirement { Unknown, Required, NotRequired }
public enum ExactSessionBindingOutcome { Unknown, Unbound, ExactBound, NotApplicable }
public enum DoctorCompleteness { Unknown, Unbound, Partial, Rich, Full }
public enum ContentCaptureStatus { Unknown, Enabled, Disabled, Unsupported }
public enum RawAccessStatus { Unknown, Available, SanitizedOnly }
public enum RestartRequirement { Unknown, Required, NotRequired }
public enum DoctorEvidenceClass { RealSource, SyntheticProbe }
public enum DoctorEvidenceKind { Ingest, RawPersistence, Projection, ExactSessionBinding, CompletenessContent }
public enum DoctorStateCode
{
    MonitorNotInstalled,
    MonitorNotRunning,
    ReceiverNotBound,
    PortOwnedByForeignProcess,
    EndpointMismatch,
    ProtocolMismatch,
    SignalDisabled,
    UnsupportedSourceVersion,
    FeatureUnavailable,
    AgentRestartRequired,
    EndpointUnreachable,
    PayloadRejected,
    RawPersistedProjectionPending,
    ProjectionFailed,
    SessionUnbound,
    ContentCaptureDisabled,
    SanitizedOnlyRawUnavailable,
    SchemaDriftDetected,
    ReadyNoRealTrace,
    FirstTraceReady
}

public enum DoctorSeverity { Error, Warning, Info }
public enum DoctorRetryability { AfterAction, Automatic, None }
public enum DoctorNextAction
{
    InstallMonitor,
    StartMonitor,
    RestartMonitor,
    FreeOrChangePort,
    UpdateSourceEndpoint,
    UseHttpProtobuf,
    EnableTraceSignal,
    UseSupportedSourceVersion,
    UseSupportedSourceSurface,
    RestartSourceProcess,
    VerifyEndpointReachability,
    InspectRejectedPayload,
    WaitForProjection,
    OpenProjectionDiagnostics,
    SelectExactSession,
    EnableContentCaptureIfDesired,
    RestartWithoutSanitizedOnlyIfDesired,
    ReviewSourceDiagnostics,
    RunBoundedSourceInteraction,
    OpenVerifiedTraceOrSession
}

public enum DoctorResultCode
{
    EvaluationCompleted,
    VerificationStarted,
    VerificationActive,
    VerificationCompleted,
    VerificationCancelled,
    InvalidArguments,
    InvalidInput,
    UnsupportedSchemaVersion,
    PartialFactSnapshot,
    VerificationNotFound,
    VerificationStale,
    VerificationExpired,
    VerificationAlreadyCancelled,
    VerificationAlreadyCompleted,
    ExpectedSourceMismatch,
    EvidenceNotFound,
    EvidenceExpired,
    DoctorStoreBusy,
    DoctorStoreUnavailable,
    InternalError
}

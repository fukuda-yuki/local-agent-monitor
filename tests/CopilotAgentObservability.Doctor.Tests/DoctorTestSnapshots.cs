namespace CopilotAgentObservability.Doctor.Tests;

internal static class DoctorTestSnapshots
{
    internal static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);

    internal static DoctorFactSnapshot ReadyNoRealTrace() => new(
        DoctorSchemaVersions.FactsV1,
        "github-copilot-vscode",
        ExpectedSourceAdapter: null,
        ObservedAt,
        VerificationId: null,
        Observations: [],
        new InstallAndSourceVersionFacts(MonitorInstallStatus.Installed, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
        new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
        new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Match),
        new EndpointReachabilityFacts(ReachabilityStatus.Reachable),
        new ProtocolAndSignalCompatibilityFacts(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled),
        new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.Matching),
        new LastIngestFacts(LastIngestOutcome.None),
        new RawPersistenceFacts(RawPersistenceOutcome.NotPersisted),
        new ProjectionFacts(ProjectionOutcome.NotStarted),
        new ExactSessionBindingFacts(ExactSessionBindingRequirement.NotRequired, ExactSessionBindingOutcome.NotApplicable),
        new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Enabled, RawAccessStatus.Available),
        new RestartOrNewProcessFacts(RestartRequirement.NotRequired));

    internal static DoctorFactSnapshot FirstTraceReady(bool exactBindingRequired = false)
    {
        var observations = new List<DoctorObservation>
        {
            Observation(DoctorEvidenceKind.Ingest, "event-ingest"),
            Observation(DoctorEvidenceKind.RawPersistence, "event-raw"),
            Observation(DoctorEvidenceKind.Projection, "event-projection"),
        };
        if (exactBindingRequired)
        {
            observations.Add(Observation(DoctorEvidenceKind.ExactSessionBinding, "event-binding"));
        }

        observations.Add(Observation(DoctorEvidenceKind.CompletenessContent, "event-content"));

        return ReadyNoRealTrace() with
        {
            Observations = observations,
            LastIngest = new LastIngestFacts(LastIngestOutcome.Accepted),
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
            Projection = new ProjectionFacts(ProjectionOutcome.Completed),
            ExactSessionBinding = exactBindingRequired
                ? new ExactSessionBindingFacts(ExactSessionBindingRequirement.Required, ExactSessionBindingOutcome.ExactBound)
                : new ExactSessionBindingFacts(ExactSessionBindingRequirement.NotRequired, ExactSessionBindingOutcome.NotApplicable),
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Enabled, RawAccessStatus.Available),
        };
    }

    internal static DoctorObservation Observation(
        DoctorEvidenceKind kind,
        string evidenceRef,
        DoctorEvidenceClass evidenceClass = DoctorEvidenceClass.RealSource,
        string sourceSurface = "github-copilot-vscode",
        string? sourceAdapter = null) =>
        new(sourceSurface, sourceAdapter, evidenceClass, kind, evidenceRef, ObservedAt);
}

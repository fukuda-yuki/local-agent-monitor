using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

internal static class ClaudeDoctorFactMapper
{
    private const string DefaultSourceSurface = "claude-code";
    private const string DefaultExpectedAdapter = "claude-code-otel";
    private static readonly IReadOnlyList<DoctorObservation> EmptyObservations =
        Array.Empty<DoctorObservation>();

    public static DoctorFactSnapshot Map(
        ClaudeDoctorFactInputs inputs,
        DateTimeOffset observedAt,
        string? verificationId,
        string sourceSurface = DefaultSourceSurface,
        string expectedAdapter = DefaultExpectedAdapter)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var window = inputs.VerificationWindow;
        var projection = MapProjection(window);
        var binding = MapBinding(window, projection);
        return new DoctorFactSnapshot(
            DoctorSchemaVersions.FactsV1,
            sourceSurface,
            expectedAdapter,
            observedAt,
            verificationId,
            EmptyObservations,
            MapInstallAndSourceVersion(inputs),
            MapProcessReceiverAndPort(inputs),
            new SourceEffectiveConfigurationFacts(MapEndpointAlignment(inputs.Endpoint)),
            new EndpointReachabilityFacts(MapReachability(inputs.ReadinessProbeSucceeded)),
            MapProtocolAndSignal(inputs),
            MapSourceVersionAndSchema(inputs),
            MapLastIngest(window),
            MapRawPersistence(window),
            projection,
            binding,
            MapCompletenessAndContent(inputs, window, binding),
            new RestartOrNewProcessFacts(MapRestartRequirement(inputs.SetupLedger)));
    }

    private static InstallAndSourceVersionFacts MapInstallAndSourceVersion(
        ClaudeDoctorFactInputs inputs) =>
        new(
            MapMonitorInstall(inputs),
            inputs.SourceVersion switch
            {
                ClaudeSourceVersionClassification.Supported => SourceVersionStatus.Supported,
                ClaudeSourceVersionClassification.Unsupported => SourceVersionStatus.Unsupported,
                _ => SourceVersionStatus.Unknown,
            },
            inputs.SourceVersion switch
            {
                ClaudeSourceVersionClassification.Supported => SourceFeatureStatus.Available,
                ClaudeSourceVersionClassification.Unsupported => SourceFeatureStatus.Unavailable,
                _ => SourceFeatureStatus.Unknown,
            });

    private static MonitorInstallStatus MapMonitorInstall(ClaudeDoctorFactInputs inputs) =>
        inputs.LivenessProbe == ClaudeLivenessProbeClassification.MonitorLive
            ? MonitorInstallStatus.Installed
            : inputs.MonitorDatabaseFileExists switch
            {
                true => MonitorInstallStatus.Installed,
                false => MonitorInstallStatus.NotInstalled,
                _ => MonitorInstallStatus.Unknown,
            };

    private static ProcessReceiverAndPortFacts MapProcessReceiverAndPort(
        ClaudeDoctorFactInputs inputs) =>
        inputs.LivenessProbe switch
        {
            ClaudeLivenessProbeClassification.MonitorLive => new(
                MonitorProcessStatus.Running,
                ReceiverBindStatus.Bound,
                PortOwnerStatus.Monitor),
            ClaudeLivenessProbeClassification.PositiveNoListener => new(
                MonitorProcessStatus.NotRunning,
                ReceiverBindStatus.NotBound,
                PortOwnerStatus.None),
            ClaudeLivenessProbeClassification.OtherForeign => new(
                MonitorProcessStatus.NotRunning,
                ReceiverBindStatus.NotBound,
                PortOwnerStatus.Foreign),
            _ => new(
                MonitorProcessStatus.Unknown,
                ReceiverBindStatus.Unknown,
                PortOwnerStatus.Unknown),
        };

    private static EndpointAlignmentStatus MapEndpointAlignment(
        ClaudeEndpointValueClassification endpoint) =>
        endpoint switch
        {
            ClaudeEndpointValueClassification.Match => EndpointAlignmentStatus.Match,
            ClaudeEndpointValueClassification.Different or ClaudeEndpointValueClassification.Absent or
                ClaudeEndpointValueClassification.Conflict => EndpointAlignmentStatus.Mismatch,
            _ => EndpointAlignmentStatus.Unknown,
        };

    private static ReachabilityStatus MapReachability(bool? readinessProbeSucceeded) =>
        readinessProbeSucceeded switch
        {
            true => ReachabilityStatus.Reachable,
            false => ReachabilityStatus.Unreachable,
            _ => ReachabilityStatus.Unknown,
        };

    private static ProtocolAndSignalCompatibilityFacts MapProtocolAndSignal(
        ClaudeDoctorFactInputs inputs) =>
        new(
            inputs.Protocol switch
            {
                ClaudeProtocolValueClassification.HttpProtobuf => ProtocolStatus.HttpProtobuf,
                ClaudeProtocolValueClassification.Different or ClaudeProtocolValueClassification.Absent =>
                    ProtocolStatus.Mismatch,
                _ => ProtocolStatus.Unknown,
            },
            MapTraceSignal(inputs.TelemetryGate, inputs.ExporterGate));

    private static TraceSignalStatus MapTraceSignal(
        ClaudeGateValueClassification telemetryGate,
        ClaudeGateValueClassification exporterGate)
    {
        if (IsDisabled(telemetryGate) || IsDisabled(exporterGate))
        {
            return TraceSignalStatus.Disabled;
        }

        if (telemetryGate == ClaudeGateValueClassification.Enabled &&
            exporterGate == ClaudeGateValueClassification.Enabled)
        {
            return TraceSignalStatus.Enabled;
        }

        return TraceSignalStatus.Unknown;
    }

    private static bool IsDisabled(ClaudeGateValueClassification value) =>
        value is ClaudeGateValueClassification.Disabled or ClaudeGateValueClassification.Absent;

    private static SourceVersionAndSchemaDiagnosticsFacts MapSourceVersionAndSchema(
        ClaudeDoctorFactInputs inputs) =>
        new(
            MapCompatibility(inputs.SourceVersion, inputs.SourceCompatibility),
            inputs.SourceCompatibility switch
            {
                ClaudeSourceCompatibilityClassification.Drift => SchemaStatus.DriftDetected,
                ClaudeSourceCompatibilityClassification.Matching or
                    ClaudeSourceCompatibilityClassification.Incompatible => SchemaStatus.Matching,
                _ => SchemaStatus.Unknown,
            });

    private static SourceCompatibilityStatus MapCompatibility(
        ClaudeSourceVersionClassification version,
        ClaudeSourceCompatibilityClassification compatibility) =>
        version == ClaudeSourceVersionClassification.Unsupported ||
        compatibility == ClaudeSourceCompatibilityClassification.Incompatible
            ? SourceCompatibilityStatus.UnsupportedSourceVersion
            : version == ClaudeSourceVersionClassification.Supported &&
              compatibility is ClaudeSourceCompatibilityClassification.Matching or
                  ClaudeSourceCompatibilityClassification.Drift
                ? SourceCompatibilityStatus.Supported
                : SourceCompatibilityStatus.Unknown;

    private static LastIngestFacts MapLastIngest(ClaudeDoctorVerificationWindow? window) =>
        window is null
            ? new LastIngestFacts(LastIngestOutcome.None)
            : new LastIngestFacts(
                window.AcceptedIngestExists
                    ? LastIngestOutcome.Accepted
                    : window.RejectedIngestExists
                        ? LastIngestOutcome.Rejected
                        : LastIngestOutcome.None);

    private static RawPersistenceFacts MapRawPersistence(ClaudeDoctorVerificationWindow? window) =>
        window is null
            ? new RawPersistenceFacts(RawPersistenceOutcome.NotPersisted)
            : new RawPersistenceFacts(
                window.RawPersistenceCandidateExists
                    ? RawPersistenceOutcome.Persisted
                    : window.AcceptedIngestExists
                        ? RawPersistenceOutcome.NotPersisted
                        : RawPersistenceOutcome.Unknown);

    private static ProjectionFacts MapProjection(ClaudeDoctorVerificationWindow? window) =>
        window is null
            ? new ProjectionFacts(ProjectionOutcome.NotStarted)
            : new ProjectionFacts(
                window.ProjectionCandidateExists
                    ? ProjectionOutcome.Completed
                    : window.RawPersistenceCandidateExists
                        ? window.ProjectionEvidence switch
                        {
                            ClaudeProjectionEvidence.NotStarted => ProjectionOutcome.NotStarted,
                            ClaudeProjectionEvidence.Pending => ProjectionOutcome.Pending,
                            ClaudeProjectionEvidence.Failed => ProjectionOutcome.Failed,
                            _ => ProjectionOutcome.Unknown,
                        }
                        : ProjectionOutcome.Unknown);

    private static ExactSessionBindingFacts MapBinding(
        ClaudeDoctorVerificationWindow? window,
        ProjectionFacts projection) =>
        projection.Outcome == ProjectionOutcome.Completed
            ? new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.Required,
                window!.ExactSessionBindingCandidateExists
                    ? ExactSessionBindingOutcome.ExactBound
                    : ExactSessionBindingOutcome.Unbound)
            : new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.NotRequired,
                ExactSessionBindingOutcome.NotApplicable);

    private static CompletenessAndContentFacts MapCompletenessAndContent(
        ClaudeDoctorFactInputs inputs,
        ClaudeDoctorVerificationWindow? window,
        ExactSessionBindingFacts binding) =>
        new(
            MapCompleteness(window, binding),
            MapContentCapture(inputs.EffectiveContentGate, window, binding),
            MapRawAccess(inputs.LivenessProbe, inputs.RuntimeRawAccess));

    private static DoctorCompleteness MapCompleteness(
        ClaudeDoctorVerificationWindow? window,
        ExactSessionBindingFacts binding) =>
        binding.Outcome == ExactSessionBindingOutcome.ExactBound
            ? window!.BoundSessionCompleteness switch
            {
                ClaudeBoundSessionCompleteness.Unbound => DoctorCompleteness.Unbound,
                ClaudeBoundSessionCompleteness.Partial => DoctorCompleteness.Partial,
                ClaudeBoundSessionCompleteness.Rich => DoctorCompleteness.Rich,
                ClaudeBoundSessionCompleteness.Full => DoctorCompleteness.Full,
                _ => DoctorCompleteness.Unknown,
            }
            : DoctorCompleteness.Unknown;

    private static ContentCaptureStatus MapContentCapture(
        ClaudeEffectiveContentGate effectiveContentGate,
        ClaudeDoctorVerificationWindow? window,
        ExactSessionBindingFacts binding)
    {
        if (binding.Outcome == ExactSessionBindingOutcome.ExactBound)
        {
            return window!.AgreedContentState switch
            {
                ClaudeAgreedContentState.Available or ClaudeAgreedContentState.Redacted =>
                    ContentCaptureStatus.Enabled,
                ClaudeAgreedContentState.NotCaptured => ContentCaptureStatus.Disabled,
                ClaudeAgreedContentState.Unsupported => ContentCaptureStatus.Unsupported,
                ClaudeAgreedContentState.Unreadable => ContentCaptureStatus.Unknown,
                _ => MapEffectiveContentGate(effectiveContentGate),
            };
        }

        return MapEffectiveContentGate(effectiveContentGate);
    }

    private static ContentCaptureStatus MapEffectiveContentGate(
        ClaudeEffectiveContentGate effectiveContentGate) =>
        effectiveContentGate switch
        {
            ClaudeEffectiveContentGate.Enabled => ContentCaptureStatus.Enabled,
            ClaudeEffectiveContentGate.Disabled => ContentCaptureStatus.Disabled,
            _ => ContentCaptureStatus.Unknown,
        };

    private static RawAccessStatus MapRawAccess(
        ClaudeLivenessProbeClassification liveness,
        ClaudeRuntimeRawAccessClassification runtimeRawAccess) =>
        liveness == ClaudeLivenessProbeClassification.MonitorLive
            ? runtimeRawAccess switch
            {
                ClaudeRuntimeRawAccessClassification.Available => RawAccessStatus.Available,
                ClaudeRuntimeRawAccessClassification.SanitizedOnly => RawAccessStatus.SanitizedOnly,
                _ => RawAccessStatus.Unknown,
            }
            : RawAccessStatus.Unknown;

    private static RestartRequirement MapRestartRequirement(
        ClaudeSetupLedgerClassification setupLedger) =>
        setupLedger switch
        {
            ClaudeSetupLedgerClassification.AwaitingAcceptedIngest => RestartRequirement.Required,
            ClaudeSetupLedgerClassification.NoAppliedChangeSet or
                ClaudeSetupLedgerClassification.AcceptedIngestAfterApply => RestartRequirement.NotRequired,
            _ => RestartRequirement.Unknown,
        };
}

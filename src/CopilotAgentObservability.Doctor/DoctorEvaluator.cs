namespace CopilotAgentObservability.Doctor;

public static class DoctorEvaluator
{
    private static readonly DoctorStateCode[] AdvisoryOrder =
    [
        DoctorStateCode.ContentCaptureDisabled,
        DoctorStateCode.SanitizedOnlyRawUnavailable,
        DoctorStateCode.SchemaDriftDetected,
    ];

    public static DoctorResult Evaluate(DoctorFactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.Equals(snapshot.SchemaVersion, DoctorSchemaVersions.FactsV1, StringComparison.Ordinal))
        {
            return Error(DoctorResultCode.UnsupportedSchemaVersion);
        }

        if (!DoctorValidation.IsValidFactSnapshot(snapshot))
        {
            return Error(DoctorResultCode.InvalidInput);
        }

        var applicable = GetApplicableStates(snapshot);
        var missingFactFamilies = GetMissingFactFamilies(snapshot);
        var blockers = DoctorCatalog.Entries
            .Take(15)
            .Where(entry => applicable.Contains(entry.StateCode))
            .Select(entry => CreateState(snapshot, entry.StateCode))
            .ToArray();

        if (blockers.Length > 0)
        {
            return Completed(snapshot, blockers[0], blockers, missingFactFamilies);
        }

        if (UnknownPreventsConclusion(snapshot))
        {
            return new DoctorResult(
                DoctorSchemaVersions.ResultV1,
                Success: false,
                DoctorResultCode.PartialFactSnapshot,
                new DoctorEvaluation(snapshot.SourceSurface, PrimaryState: null, States: [], missingFactFamilies),
                Verification: null);
        }

        var terminalCode = IsFirstTraceReady(snapshot)
            ? DoctorStateCode.FirstTraceReady
            : DoctorStateCode.ReadyNoRealTrace;
        var states = new List<DoctorState> { CreateState(snapshot, terminalCode) };
        states.AddRange(AdvisoryOrder
            .Where(applicable.Contains)
            .Select(code => CreateState(snapshot, code)));

        return Completed(snapshot, states[0], states, missingFactFamilies);
    }

    private static IReadOnlyList<string> GetMissingFactFamilies(DoctorFactSnapshot snapshot)
    {
        var missing = new List<string>(12);
        AddMissing(
            missing,
            "install_and_source_version",
            snapshot.InstallAndSourceVersion is null
                || snapshot.InstallAndSourceVersion.MonitorInstall == MonitorInstallStatus.Unknown
                || snapshot.InstallAndSourceVersion.SourceVersion == SourceVersionStatus.Unknown
                || snapshot.InstallAndSourceVersion.SourceFeature == SourceFeatureStatus.Unknown);
        AddMissing(
            missing,
            "process_receiver_and_port",
            snapshot.ProcessReceiverAndPort is null
                || snapshot.ProcessReceiverAndPort.MonitorProcess == MonitorProcessStatus.Unknown
                || snapshot.ProcessReceiverAndPort.ReceiverBind == ReceiverBindStatus.Unknown
                || snapshot.ProcessReceiverAndPort.PortOwner == PortOwnerStatus.Unknown);
        AddMissing(missing, "source_effective_configuration", snapshot.SourceEffectiveConfiguration?.EndpointAlignment is null or EndpointAlignmentStatus.Unknown);
        AddMissing(missing, "endpoint_reachability", snapshot.EndpointReachability?.Reachability is null or ReachabilityStatus.Unknown);
        AddMissing(
            missing,
            "protocol_and_signal_compatibility",
            snapshot.ProtocolAndSignalCompatibility is null
                || snapshot.ProtocolAndSignalCompatibility.Protocol == ProtocolStatus.Unknown
                || snapshot.ProtocolAndSignalCompatibility.TraceSignal == TraceSignalStatus.Unknown);
        AddMissing(
            missing,
            "source_version_and_schema_diagnostics",
            snapshot.SourceVersionAndSchemaDiagnostics is null
                || snapshot.SourceVersionAndSchemaDiagnostics.Compatibility == SourceCompatibilityStatus.Unknown
                || snapshot.SourceVersionAndSchemaDiagnostics.Schema == SchemaStatus.Unknown);
        AddMissing(missing, "last_ingest", snapshot.LastIngest?.Outcome is null or LastIngestOutcome.Unknown);
        AddMissing(missing, "raw_persistence", snapshot.RawPersistence?.Outcome is null or RawPersistenceOutcome.Unknown);
        AddMissing(missing, "projection", snapshot.Projection?.Outcome is null or ProjectionOutcome.Unknown);
        AddMissing(
            missing,
            "exact_session_binding",
            snapshot.ExactSessionBinding is null
                || snapshot.ExactSessionBinding.Requirement == ExactSessionBindingRequirement.Unknown
                || snapshot.ExactSessionBinding.Outcome == ExactSessionBindingOutcome.Unknown);
        AddMissing(
            missing,
            "completeness_and_content",
            snapshot.CompletenessAndContent is null
                || snapshot.CompletenessAndContent.Completeness == DoctorCompleteness.Unknown
                || snapshot.CompletenessAndContent.ContentCapture == ContentCaptureStatus.Unknown
                || snapshot.CompletenessAndContent.RawAccess == RawAccessStatus.Unknown);
        AddMissing(missing, "restart_or_new_process", snapshot.RestartOrNewProcess?.Requirement is null or RestartRequirement.Unknown);
        return missing;
    }

    private static bool UnknownPreventsConclusion(DoctorFactSnapshot snapshot)
    {
        var install = snapshot.InstallAndSourceVersion;
        var process = snapshot.ProcessReceiverAndPort;
        var protocol = snapshot.ProtocolAndSignalCompatibility;
        var diagnostics = snapshot.SourceVersionAndSchemaDiagnostics;
        var binding = snapshot.ExactSessionBinding;

        if (install is null
            || install.MonitorInstall == MonitorInstallStatus.Unknown
            || install.SourceVersion == SourceVersionStatus.Unknown
            || install.SourceFeature == SourceFeatureStatus.Unknown
            || process is null
            || process.MonitorProcess == MonitorProcessStatus.Unknown
            || process.ReceiverBind == ReceiverBindStatus.Unknown
            || process.PortOwner == PortOwnerStatus.Unknown
            || snapshot.SourceEffectiveConfiguration?.EndpointAlignment is null or EndpointAlignmentStatus.Unknown
            || snapshot.EndpointReachability?.Reachability is null or ReachabilityStatus.Unknown
            || protocol is null
            || protocol.Protocol == ProtocolStatus.Unknown
            || protocol.TraceSignal == TraceSignalStatus.Unknown
            || diagnostics is null
            || diagnostics.Compatibility == SourceCompatibilityStatus.Unknown
            || snapshot.LastIngest?.Outcome is null or LastIngestOutcome.Unknown
            || snapshot.RawPersistence?.Outcome is null or RawPersistenceOutcome.Unknown
            || snapshot.Projection?.Outcome is null or ProjectionOutcome.Unknown
            || binding is null
            || binding.Requirement == ExactSessionBindingRequirement.Unknown
            || (binding.Requirement == ExactSessionBindingRequirement.Required
                && binding.Outcome == ExactSessionBindingOutcome.Unknown)
            || snapshot.RestartOrNewProcess?.Requirement is null or RestartRequirement.Unknown)
        {
            return true;
        }

        var content = snapshot.CompletenessAndContent;
        var contentUnknown = content is null
            || content.Completeness == DoctorCompleteness.Unknown;
        return contentUnknown && MeetsFirstTraceRequirementsExceptKnownContent(snapshot);
    }

    private static bool MeetsFirstTraceRequirementsExceptKnownContent(DoctorFactSnapshot snapshot)
    {
        var binding = snapshot.ExactSessionBinding;
        return snapshot.LastIngest?.Outcome == LastIngestOutcome.Accepted
            && snapshot.RawPersistence?.Outcome == RawPersistenceOutcome.Persisted
            && snapshot.Projection?.Outcome == ProjectionOutcome.Completed
            && binding is not null
            && binding.Requirement != ExactSessionBindingRequirement.Unknown
            && (binding.Requirement != ExactSessionBindingRequirement.Required
                || (binding.Outcome == ExactSessionBindingOutcome.ExactBound
                    && HasMatchingRealObservation(snapshot, DoctorEvidenceKind.ExactSessionBinding)))
            && HasMatchingRealObservation(snapshot, DoctorEvidenceKind.Ingest)
            && HasMatchingRealObservation(snapshot, DoctorEvidenceKind.RawPersistence)
            && HasMatchingRealObservation(snapshot, DoctorEvidenceKind.Projection)
            && HasMatchingRealObservation(snapshot, DoctorEvidenceKind.CompletenessContent);
    }

    private static HashSet<DoctorStateCode> GetApplicableStates(DoctorFactSnapshot snapshot)
    {
        var states = new HashSet<DoctorStateCode>();
        var install = snapshot.InstallAndSourceVersion;
        var process = snapshot.ProcessReceiverAndPort;
        var configuration = snapshot.SourceEffectiveConfiguration;
        var reachability = snapshot.EndpointReachability;
        var protocol = snapshot.ProtocolAndSignalCompatibility;
        var diagnostics = snapshot.SourceVersionAndSchemaDiagnostics;
        var ingest = snapshot.LastIngest;
        var raw = snapshot.RawPersistence;
        var projection = snapshot.Projection;
        var binding = snapshot.ExactSessionBinding;
        var content = snapshot.CompletenessAndContent;
        var restart = snapshot.RestartOrNewProcess;

        AddIf(states, install?.MonitorInstall == MonitorInstallStatus.NotInstalled, DoctorStateCode.MonitorNotInstalled);
        AddIf(states, install?.MonitorInstall == MonitorInstallStatus.Installed && process?.MonitorProcess == MonitorProcessStatus.NotRunning, DoctorStateCode.MonitorNotRunning);
        AddIf(states, process?.MonitorProcess == MonitorProcessStatus.Running && process.ReceiverBind == ReceiverBindStatus.NotBound, DoctorStateCode.ReceiverNotBound);
        AddIf(states, process?.PortOwner == PortOwnerStatus.Foreign, DoctorStateCode.PortOwnedByForeignProcess);
        AddIf(states, configuration?.EndpointAlignment == EndpointAlignmentStatus.Mismatch, DoctorStateCode.EndpointMismatch);
        AddIf(states, protocol?.Protocol == ProtocolStatus.Mismatch, DoctorStateCode.ProtocolMismatch);
        AddIf(states, protocol?.TraceSignal == TraceSignalStatus.Disabled, DoctorStateCode.SignalDisabled);
        AddIf(
            states,
            install?.SourceVersion == SourceVersionStatus.Unsupported
                || diagnostics?.Compatibility == SourceCompatibilityStatus.UnsupportedSourceVersion,
            DoctorStateCode.UnsupportedSourceVersion);
        AddIf(
            states,
            install?.SourceFeature == SourceFeatureStatus.Unavailable
                || diagnostics?.Compatibility == SourceCompatibilityStatus.FeatureUnavailable,
            DoctorStateCode.FeatureUnavailable);
        AddIf(states, restart?.Requirement == RestartRequirement.Required, DoctorStateCode.AgentRestartRequired);
        AddIf(states, reachability?.Reachability == ReachabilityStatus.Unreachable, DoctorStateCode.EndpointUnreachable);
        AddIf(states, ingest?.Outcome == LastIngestOutcome.Rejected, DoctorStateCode.PayloadRejected);
        AddIf(
            states,
            raw?.Outcome == RawPersistenceOutcome.Persisted
                && projection?.Outcome is ProjectionOutcome.NotStarted or ProjectionOutcome.Pending,
            DoctorStateCode.RawPersistedProjectionPending);
        AddIf(states, projection?.Outcome == ProjectionOutcome.Failed, DoctorStateCode.ProjectionFailed);
        AddIf(
            states,
            binding?.Requirement == ExactSessionBindingRequirement.Required
                && binding.Outcome == ExactSessionBindingOutcome.Unbound,
            DoctorStateCode.SessionUnbound);
        AddIf(
            states,
            content?.ContentCapture is ContentCaptureStatus.Disabled or ContentCaptureStatus.Unsupported,
            DoctorStateCode.ContentCaptureDisabled);
        AddIf(states, content?.RawAccess == RawAccessStatus.SanitizedOnly, DoctorStateCode.SanitizedOnlyRawUnavailable);
        AddIf(states, diagnostics?.Schema == SchemaStatus.DriftDetected, DoctorStateCode.SchemaDriftDetected);
        return states;
    }

    private static bool IsFirstTraceReady(DoctorFactSnapshot snapshot)
    {
        if (snapshot.LastIngest?.Outcome != LastIngestOutcome.Accepted
            || snapshot.RawPersistence?.Outcome != RawPersistenceOutcome.Persisted
            || snapshot.Projection?.Outcome != ProjectionOutcome.Completed
            || snapshot.CompletenessAndContent is null
            || snapshot.CompletenessAndContent.Completeness == DoctorCompleteness.Unknown)
        {
            return false;
        }

        var binding = snapshot.ExactSessionBinding;
        if (binding?.Requirement == ExactSessionBindingRequirement.Required
            && binding.Outcome != ExactSessionBindingOutcome.ExactBound)
        {
            return false;
        }

        if (binding is null || binding.Requirement == ExactSessionBindingRequirement.Unknown)
        {
            return false;
        }

        var requiredKinds = new List<DoctorEvidenceKind>
        {
            DoctorEvidenceKind.Ingest,
            DoctorEvidenceKind.RawPersistence,
            DoctorEvidenceKind.Projection,
        };
        if (binding.Requirement == ExactSessionBindingRequirement.Required)
        {
            requiredKinds.Add(DoctorEvidenceKind.ExactSessionBinding);
        }

        requiredKinds.Add(DoctorEvidenceKind.CompletenessContent);
        return requiredKinds.All(kind => HasMatchingRealObservation(snapshot, kind));
    }

    private static bool HasMatchingRealObservation(
        DoctorFactSnapshot snapshot,
        DoctorEvidenceKind kind) =>
        snapshot.Observations.Any(observation =>
            observation.EvidenceClass == DoctorEvidenceClass.RealSource
            && observation.EvidenceKind == kind
            && string.Equals(observation.SourceSurface, snapshot.SourceSurface, StringComparison.Ordinal)
            && (snapshot.ExpectedSourceAdapter is null
                || string.Equals(observation.SourceAdapter, snapshot.ExpectedSourceAdapter, StringComparison.Ordinal)));

    private static DoctorState CreateState(DoctorFactSnapshot snapshot, DoctorStateCode code)
    {
        var entry = DoctorCatalog.Get(code);
        return new DoctorState(
            DoctorSchemaVersions.ResultV1,
            code,
            entry.Severity,
            snapshot.SourceSurface,
            EvidenceReferences(snapshot, code),
            entry.ReasonCodes,
            entry.NextAction,
            entry.Retryability,
            snapshot.ObservedAt,
            snapshot.VerificationId);
    }

    private static IReadOnlyList<string> EvidenceReferences(DoctorFactSnapshot snapshot, DoctorStateCode code)
    {
        var kinds = code switch
        {
            DoctorStateCode.PayloadRejected => [DoctorEvidenceKind.Ingest],
            DoctorStateCode.RawPersistedProjectionPending => [DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection],
            DoctorStateCode.ProjectionFailed => [DoctorEvidenceKind.Projection],
            DoctorStateCode.SessionUnbound => [DoctorEvidenceKind.ExactSessionBinding],
            DoctorStateCode.ContentCaptureDisabled or DoctorStateCode.SanitizedOnlyRawUnavailable => [DoctorEvidenceKind.CompletenessContent],
            DoctorStateCode.ReadyNoRealTrace => Enum.GetValues<DoctorEvidenceKind>(),
            DoctorStateCode.FirstTraceReady => Enum.GetValues<DoctorEvidenceKind>(),
            _ => [],
        };

        return snapshot.Observations
            .Where(observation => kinds.Contains(observation.EvidenceKind)
                && (code != DoctorStateCode.FirstTraceReady
                    || observation.EvidenceClass == DoctorEvidenceClass.RealSource))
            .Select(observation => observation.EvidenceRef)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static DoctorResult Completed(
        DoctorFactSnapshot snapshot,
        DoctorState primaryState,
        IReadOnlyList<DoctorState> states,
        IReadOnlyList<string> missingFactFamilies) =>
        new(
            DoctorSchemaVersions.ResultV1,
            Success: true,
            DoctorResultCode.EvaluationCompleted,
            new DoctorEvaluation(snapshot.SourceSurface, primaryState, states, missingFactFamilies),
            Verification: null);

    private static DoctorResult Error(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, Success: false, code, Evaluation: null, Verification: null);

    private static void AddIf(HashSet<DoctorStateCode> states, bool condition, DoctorStateCode state)
    {
        if (condition)
        {
            states.Add(state);
        }
    }

    private static void AddMissing(List<string> missing, string familyName, bool isMissing)
    {
        if (isMissing)
        {
            missing.Add(familyName);
        }
    }
}

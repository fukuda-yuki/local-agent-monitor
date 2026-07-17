using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeDoctorFactMapperTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-17T00:00:00Z");

    private const string VerificationId = "019bf5d2-3c7a-7c01-8b2a-1234567890ab";

    [Fact]
    public void Map_HealthyNoWindow_ProducesFixedPreWindowShapeAndReadyNoRealTrace()
    {
        var snapshot = Map(HealthyInputs());

        Assert.Equal(DoctorSchemaVersions.FactsV1, snapshot.SchemaVersion);
        Assert.Equal("claude-code", snapshot.SourceSurface);
        Assert.Equal("claude-code-otel", snapshot.ExpectedSourceAdapter);
        Assert.Equal(ObservedAt, snapshot.ObservedAt);
        Assert.Equal(VerificationId, snapshot.VerificationId);
        Assert.Empty(snapshot.Observations);
        Assert.Equal(
            new InstallAndSourceVersionFacts(
                MonitorInstallStatus.Installed,
                SourceVersionStatus.Supported,
                SourceFeatureStatus.Available),
            snapshot.InstallAndSourceVersion);
        Assert.Equal(
            new ProcessReceiverAndPortFacts(
                MonitorProcessStatus.Running,
                ReceiverBindStatus.Bound,
                PortOwnerStatus.Monitor),
            snapshot.ProcessReceiverAndPort);
        Assert.Equal(
            new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Match),
            snapshot.SourceEffectiveConfiguration);
        Assert.Equal(
            new EndpointReachabilityFacts(ReachabilityStatus.Reachable),
            snapshot.EndpointReachability);
        Assert.Equal(
            new ProtocolAndSignalCompatibilityFacts(
                ProtocolStatus.HttpProtobuf,
                TraceSignalStatus.Enabled),
            snapshot.ProtocolAndSignalCompatibility);
        Assert.Equal(
            new SourceVersionAndSchemaDiagnosticsFacts(
                SourceCompatibilityStatus.Supported,
                SchemaStatus.Matching),
            snapshot.SourceVersionAndSchemaDiagnostics);
        Assert.Equal(new LastIngestFacts(LastIngestOutcome.None), snapshot.LastIngest);
        Assert.Equal(
            new RawPersistenceFacts(RawPersistenceOutcome.NotPersisted),
            snapshot.RawPersistence);
        Assert.Equal(
            new ProjectionFacts(ProjectionOutcome.NotStarted),
            snapshot.Projection);
        Assert.Equal(
            new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.NotRequired,
                ExactSessionBindingOutcome.NotApplicable),
            snapshot.ExactSessionBinding);
        Assert.Equal(
            new CompletenessAndContentFacts(
                DoctorCompleteness.Unknown,
                ContentCaptureStatus.Enabled,
                RawAccessStatus.Available),
            snapshot.CompletenessAndContent);
        Assert.Equal(
            new RestartOrNewProcessFacts(RestartRequirement.NotRequired),
            snapshot.RestartOrNewProcess);

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.NotNull(result.Evaluation);
        Assert.Equal(
            DoctorStateCode.ReadyNoRealTrace,
            result.Evaluation!.PrimaryState!.StateCode);
    }

    [Theory]
    [InlineData((int)ClaudeEndpointValueClassification.Different)]
    [InlineData((int)ClaudeEndpointValueClassification.Absent)]
    public void Map_EndpointNotAligned_EvaluatesEndpointMismatch(
        int endpointValue)
    {
        var endpoint = (ClaudeEndpointValueClassification)endpointValue;
        var result = Evaluate(HealthyInputs() with { Endpoint = endpoint });

        AssertPrimaryState(result, DoctorStateCode.EndpointMismatch);
        Assert.Equal(EndpointAlignmentStatus.Mismatch, Map(HealthyInputs() with { Endpoint = endpoint })
            .SourceEffectiveConfiguration!.EndpointAlignment);
    }

    [Fact]
    public void Map_EndpointUnreadable_EmitsUnknownAlignment()
    {
        var snapshot = Map(HealthyInputs() with
        {
            Endpoint = ClaudeEndpointValueClassification.Unreadable,
        });

        Assert.Equal(EndpointAlignmentStatus.Unknown, snapshot.SourceEffectiveConfiguration!.EndpointAlignment);
    }

    [Fact]
    public void Map_EndpointConflict_EmitsUnknownAlignment()
    {
        var snapshot = Map(HealthyInputs() with
        {
            Endpoint = ClaudeEndpointValueClassification.Conflict,
        });

        Assert.Equal(EndpointAlignmentStatus.Unknown, snapshot.SourceEffectiveConfiguration!.EndpointAlignment);
        Assert.Equal(DoctorResultCode.PartialFactSnapshot, DoctorEvaluator.Evaluate(snapshot).Code);
    }

    [Fact]
    public void Map_DisabledSignal_EvaluatesSignalDisabled()
    {
        var result = Evaluate(HealthyInputs() with
        {
            TelemetryGate = ClaudeGateValueClassification.Disabled,
        });

        AssertPrimaryState(result, DoctorStateCode.SignalDisabled);
        Assert.Equal(
            TraceSignalStatus.Disabled,
            Map(HealthyInputs() with { TelemetryGate = ClaudeGateValueClassification.Disabled })
                .ProtocolAndSignalCompatibility!.TraceSignal);
    }

    [Fact]
    public void Map_NonHttpProtocol_EvaluatesProtocolMismatch()
    {
        var snapshot = Map(HealthyInputs() with
        {
            Protocol = ClaudeProtocolValueClassification.Different,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        AssertPrimaryState(result, DoctorStateCode.ProtocolMismatch);
        Assert.Equal(ProtocolStatus.Mismatch, snapshot.ProtocolAndSignalCompatibility!.Protocol);
    }

    [Fact]
    public void Map_ReadinessFailure_EvaluatesEndpointUnreachable()
    {
        var snapshot = Map(HealthyInputs() with
        {
            ReadinessProbeSucceeded = false,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        AssertPrimaryState(result, DoctorStateCode.EndpointUnreachable);
        Assert.Equal(ReachabilityStatus.Unreachable, snapshot.EndpointReachability!.Reachability);
    }

    [Theory]
    [InlineData((int)ClaudeGateValueClassification.Absent)]
    [InlineData((int)ClaudeGateValueClassification.Conflict)]
    [InlineData((int)ClaudeGateValueClassification.Unreadable)]
    public void Map_ExporterGateNotEnabled_UsesNormativeSignalState(
        int exporterGateValue)
    {
        var exporterGate = (ClaudeGateValueClassification)exporterGateValue;
        var snapshot = Map(HealthyInputs() with { ExporterGate = exporterGate });

        Assert.Equal(
            exporterGate == ClaudeGateValueClassification.Absent
                ? TraceSignalStatus.Disabled
                : TraceSignalStatus.Unknown,
            snapshot.ProtocolAndSignalCompatibility!.TraceSignal);
    }

    [Fact]
    public void Map_UnsupportedSourceVersion_EvaluatesUnsupportedSourceVersion()
    {
        var result = Evaluate(HealthyInputs() with
        {
            SourceVersion = ClaudeSourceVersionClassification.Unsupported,
        });

        AssertPrimaryState(result, DoctorStateCode.UnsupportedSourceVersion);
        var snapshot = Map(HealthyInputs() with
        {
            SourceVersion = ClaudeSourceVersionClassification.Unsupported,
        });
        Assert.Equal(SourceVersionStatus.Unsupported, snapshot.InstallAndSourceVersion!.SourceVersion);
        Assert.Equal(SourceFeatureStatus.Unavailable, snapshot.InstallAndSourceVersion.SourceFeature);
        Assert.Equal(
            SourceCompatibilityStatus.UnsupportedSourceVersion,
            snapshot.SourceVersionAndSchemaDiagnostics!.Compatibility);
    }

    [Fact]
    public void Map_ForeignProbe_EvaluatesPortOwnedByForeignProcess()
    {
        var snapshot = Map(HealthyInputs() with
        {
            LivenessProbe = ClaudeLivenessProbeClassification.OtherForeign,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Contains(
            result.Evaluation!.States,
            state => state.StateCode == DoctorStateCode.PortOwnedByForeignProcess);
        Assert.Equal(
            new ProcessReceiverAndPortFacts(
                MonitorProcessStatus.NotRunning,
                ReceiverBindStatus.NotBound,
                PortOwnerStatus.Foreign),
            Map(HealthyInputs() with
            {
                LivenessProbe = ClaudeLivenessProbeClassification.OtherForeign,
            }).ProcessReceiverAndPort);
    }

    [Fact]
    public void Map_NoListenerProbe_EvaluatesMonitorNotRunning()
    {
        var result = Evaluate(HealthyInputs() with
        {
            LivenessProbe = ClaudeLivenessProbeClassification.PositiveNoListener,
        });

        AssertPrimaryState(result, DoctorStateCode.MonitorNotRunning);
        Assert.Equal(
            new ProcessReceiverAndPortFacts(
                MonitorProcessStatus.NotRunning,
                ReceiverBindStatus.NotBound,
                PortOwnerStatus.None),
            Map(HealthyInputs() with
            {
                LivenessProbe = ClaudeLivenessProbeClassification.PositiveNoListener,
            }).ProcessReceiverAndPort);
    }

    [Fact]
    public void Map_RejectedIngest_EvaluatesPayloadRejected()
    {
        var result = Evaluate(HealthyInputs(Window(
            acceptedIngestExists: false,
            rejectedIngestExists: true)));

        AssertPrimaryState(result, DoctorStateCode.PayloadRejected);
        Assert.Equal(LastIngestOutcome.Rejected, Map(HealthyInputs(Window(
            acceptedIngestExists: false,
            rejectedIngestExists: true))).LastIngest!.Outcome);
    }

    [Fact]
    public void Map_RawPersistedProjectionPending_EvaluatesProjectionPending()
    {
        var result = Evaluate(HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.Pending)));

        AssertPrimaryState(result, DoctorStateCode.RawPersistedProjectionPending);
        var snapshot = Map(HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.Pending)));
        Assert.Equal(ProjectionOutcome.Pending, snapshot.Projection!.Outcome);
    }

    [Fact]
    public void Map_ProjectionFailed_EvaluatesProjectionFailed()
    {
        var result = Evaluate(HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.Failed)));

        AssertPrimaryState(result, DoctorStateCode.ProjectionFailed);
        Assert.Equal(
            ProjectionOutcome.Failed,
            Map(HealthyInputs(Window(
                acceptedIngestExists: true,
                rawPersistenceCandidateExists: true,
                projectionEvidence: ClaudeProjectionEvidence.Failed))).Projection!.Outcome);
    }

    [Fact]
    public void Map_CompletedProjectionWithoutBinding_EvaluatesSessionUnbound()
    {
        var result = Evaluate(HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: false)));

        AssertPrimaryState(result, DoctorStateCode.SessionUnbound);
        Assert.Equal(
            new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.Required,
                ExactSessionBindingOutcome.Unbound),
            Map(HealthyInputs(Window(
                acceptedIngestExists: true,
                rawPersistenceCandidateExists: true,
                projectionCandidateExists: true,
                exactSessionBindingCandidateExists: false))).ExactSessionBinding);
    }

    [Fact]
    public void Map_ExactBindingWithUnboundSessionCompleteness_NormalizesCompletenessToUnknown()
    {
        var snapshot = Map(HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Unbound)));

        Assert.Equal(
            ExactSessionBindingOutcome.ExactBound,
            snapshot.ExactSessionBinding!.Outcome);
        Assert.Equal(DoctorCompleteness.Unknown, snapshot.CompletenessAndContent!.Completeness);
        Assert.True(DoctorValidation.IsValidFactSnapshot(snapshot));
        AssertPrimaryState(DoctorEvaluator.Evaluate(snapshot), DoctorStateCode.ReadyNoRealTrace);
    }

    [Fact]
    public void Map_AppliedChangeSetWithoutPostApplyIngest_EvaluatesRestartRequired()
    {
        var result = Evaluate(HealthyInputs() with
        {
            SetupLedger = ClaudeSetupLedgerClassification.AwaitingAcceptedIngest,
        });

        AssertPrimaryState(result, DoctorStateCode.AgentRestartRequired);
    }

    [Fact]
    public void Map_DriftRows_EmitsSchemaDriftAdvisory()
    {
        var snapshot = Map(HealthyInputs() with
        {
            SourceCompatibility = ClaudeSourceCompatibilityClassification.Drift,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Contains(
            result.Evaluation!.States,
            state => state.StateCode == DoctorStateCode.SchemaDriftDetected);
        Assert.Equal(SchemaStatus.DriftDetected, snapshot.SourceVersionAndSchemaDiagnostics!.Schema);
        Assert.Equal(SourceCompatibilityStatus.Supported, snapshot.SourceVersionAndSchemaDiagnostics.Compatibility);
    }

    [Fact]
    public void Map_ContentDisabled_EmitsContentCaptureDisabledAdvisory()
    {
        var snapshot = Map(HealthyInputs() with
        {
            EffectiveContentGate = ClaudeEffectiveContentGate.Disabled,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Contains(
            result.Evaluation!.States,
            state => state.StateCode == DoctorStateCode.ContentCaptureDisabled);
        Assert.Equal(ContentCaptureStatus.Disabled, snapshot.CompletenessAndContent!.ContentCapture);
    }

    [Fact]
    public void Map_SanitizedOnlyLiveRuntime_EvaluatesSanitizedOnlyRawUnavailableAdvisory()
    {
        var snapshot = Map(HealthyInputs() with
        {
            RuntimeRawAccess = ClaudeRuntimeRawAccessClassification.SanitizedOnly,
        });
        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Contains(
            result.Evaluation!.States,
            state => state.StateCode == DoctorStateCode.SanitizedOnlyRawUnavailable);
        Assert.Equal(RawAccessStatus.SanitizedOnly, snapshot.CompletenessAndContent!.RawAccess);
    }

    [Fact]
    public void Map_RuntimeStateFromNonLiveProbe_LeavesRawAccessUnknown()
    {
        var snapshot = Map(HealthyInputs() with
        {
            LivenessProbe = ClaudeLivenessProbeClassification.OtherForeign,
            RuntimeRawAccess = ClaudeRuntimeRawAccessClassification.SanitizedOnly,
        });

        Assert.Equal(RawAccessStatus.Unknown, snapshot.CompletenessAndContent!.RawAccess);
        Assert.DoesNotContain(
            DoctorEvaluator.Evaluate(snapshot).Evaluation!.States,
            state => state.StateCode == DoctorStateCode.SanitizedOnlyRawUnavailable);
    }

    [Theory]
    [MemberData(nameof(RepresentativeInputs))]
    public void Map_RepresentativeInputMatrix_ProducesValidFactSnapshot(
        object input)
    {
        var inputs = Assert.IsType<ClaudeDoctorFactInputs>(input);
        Assert.True(DoctorValidation.IsValidFactSnapshot(Map(inputs)));
    }

    [Fact]
    public void Map_AllWindowStateCartesianCombinations_ProduceValidAndExpectedEvaluations()
    {
        var projectionStates = new[]
        {
            (
                Name: "unknown",
                AcceptedIngestExists: true,
                RawPersistenceCandidateExists: false,
                ProjectionCandidateExists: false,
                ProjectionEvidence: ClaudeProjectionEvidence.NotStarted,
                ExpectedCode: DoctorResultCode.PartialFactSnapshot,
                ExpectedPrimary: (DoctorStateCode?)null),
            (
                Name: "not_started",
                AcceptedIngestExists: true,
                RawPersistenceCandidateExists: true,
                ProjectionCandidateExists: false,
                ProjectionEvidence: ClaudeProjectionEvidence.NotStarted,
                ExpectedCode: DoctorResultCode.EvaluationCompleted,
                ExpectedPrimary: (DoctorStateCode?)DoctorStateCode.RawPersistedProjectionPending),
            (
                Name: "pending",
                AcceptedIngestExists: true,
                RawPersistenceCandidateExists: true,
                ProjectionCandidateExists: false,
                ProjectionEvidence: ClaudeProjectionEvidence.Pending,
                ExpectedCode: DoctorResultCode.EvaluationCompleted,
                ExpectedPrimary: (DoctorStateCode?)DoctorStateCode.RawPersistedProjectionPending),
            (
                Name: "failed",
                AcceptedIngestExists: true,
                RawPersistenceCandidateExists: true,
                ProjectionCandidateExists: false,
                ProjectionEvidence: ClaudeProjectionEvidence.Failed,
                ExpectedCode: DoctorResultCode.EvaluationCompleted,
                ExpectedPrimary: (DoctorStateCode?)DoctorStateCode.ProjectionFailed),
            (
                Name: "completed",
                AcceptedIngestExists: true,
                RawPersistenceCandidateExists: true,
                ProjectionCandidateExists: true,
                ProjectionEvidence: ClaudeProjectionEvidence.NotStarted,
                ExpectedCode: DoctorResultCode.EvaluationCompleted,
                ExpectedPrimary: (DoctorStateCode?)DoctorStateCode.ReadyNoRealTrace),
        };

        foreach (var projectionState in projectionStates)
        {
            foreach (var exactSessionBindingCandidateExists in new[] { false, true })
            {
                foreach (var boundSessionCompleteness in Enum.GetValues<ClaudeBoundSessionCompleteness>())
                {
                    foreach (var agreedContentState in Enum.GetValues<ClaudeAgreedContentState>())
                    {
                        var inputs = HealthyInputs(Window(
                            acceptedIngestExists: projectionState.AcceptedIngestExists,
                            rawPersistenceCandidateExists: projectionState.RawPersistenceCandidateExists,
                            projectionCandidateExists: projectionState.ProjectionCandidateExists,
                            projectionEvidence: projectionState.ProjectionEvidence,
                            exactSessionBindingCandidateExists: exactSessionBindingCandidateExists,
                            boundSessionCompleteness: boundSessionCompleteness,
                            agreedContentState: agreedContentState));
                        var snapshot = Map(inputs);
                        var result = DoctorEvaluator.Evaluate(snapshot);
                        var caseName = $"{projectionState.Name}/binding={exactSessionBindingCandidateExists}/" +
                            $"completeness={boundSessionCompleteness}/content={agreedContentState}";
                        var expectedPrimary = projectionState.ExpectedPrimary;
                        if (projectionState.Name == "completed" && !exactSessionBindingCandidateExists)
                        {
                            expectedPrimary = DoctorStateCode.SessionUnbound;
                        }

                        Assert.True(DoctorValidation.IsValidFactSnapshot(snapshot), caseName);
                        Assert.NotEqual(DoctorResultCode.InvalidInput, result.Code);
                        Assert.Equal(projectionState.ExpectedCode, result.Code);
                        if (expectedPrimary is { } primary)
                        {
                            AssertPrimaryState(result, primary);
                        }
                        else
                        {
                            Assert.NotNull(result.Evaluation);
                            Assert.Null(result.Evaluation!.PrimaryState);
                        }
                    }
                }
            }
        }

        var preWindowSnapshot = Map(HealthyInputs());
        var preWindowResult = DoctorEvaluator.Evaluate(preWindowSnapshot);

        Assert.True(DoctorValidation.IsValidFactSnapshot(preWindowSnapshot), "pre-window");
        AssertPrimaryState(preWindowResult, DoctorStateCode.ReadyNoRealTrace);
    }

    [Fact]
    public void Map_SameInputs_ProducesIdenticalSnapshot()
    {
        var inputs = HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Full,
            agreedContentState: ClaudeAgreedContentState.Redacted));

        Assert.Equal(Map(inputs), Map(inputs));
    }

    public static IEnumerable<object[]> RepresentativeInputs()
    {
        yield return [HealthyInputs()];
        yield return [HealthyInputs() with { LivenessProbe = ClaudeLivenessProbeClassification.PositiveNoListener }];
        yield return [HealthyInputs() with { LivenessProbe = ClaudeLivenessProbeClassification.OtherForeign }];
        yield return [HealthyInputs() with { LivenessProbe = ClaudeLivenessProbeClassification.ProbeUnavailable }];
        yield return [HealthyInputs() with { MonitorDatabaseFileExists = false }];
        yield return [HealthyInputs() with { MonitorDatabaseFileExists = null }];
        yield return [HealthyInputs() with { SourceVersion = ClaudeSourceVersionClassification.Unsupported }];
        yield return [HealthyInputs() with { SourceVersion = ClaudeSourceVersionClassification.Undetectable }];
        yield return [HealthyInputs() with { Endpoint = ClaudeEndpointValueClassification.Different }];
        yield return [HealthyInputs() with { Endpoint = ClaudeEndpointValueClassification.Absent }];
        yield return [HealthyInputs() with { Endpoint = ClaudeEndpointValueClassification.Conflict }];
        yield return [HealthyInputs() with { Endpoint = ClaudeEndpointValueClassification.Unreadable }];
        yield return [HealthyInputs() with { Protocol = ClaudeProtocolValueClassification.Different }];
        yield return [HealthyInputs() with { Protocol = ClaudeProtocolValueClassification.Absent }];
        yield return [HealthyInputs() with { Protocol = ClaudeProtocolValueClassification.Conflict }];
        yield return [HealthyInputs() with { Protocol = ClaudeProtocolValueClassification.Unreadable }];
        yield return [HealthyInputs() with { TelemetryGate = ClaudeGateValueClassification.Absent }];
        yield return [HealthyInputs() with { TelemetryGate = ClaudeGateValueClassification.Conflict }];
        yield return [HealthyInputs() with { TelemetryGate = ClaudeGateValueClassification.Unreadable }];
        yield return [HealthyInputs() with { ExporterGate = ClaudeGateValueClassification.Disabled }];
        yield return [HealthyInputs() with { ReadinessProbeSucceeded = false }];
        yield return [HealthyInputs() with { ReadinessProbeSucceeded = null }];
        yield return [HealthyInputs() with { SourceCompatibility = ClaudeSourceCompatibilityClassification.NoRows }];
        yield return [HealthyInputs() with { SourceCompatibility = ClaudeSourceCompatibilityClassification.Drift }];
        yield return [HealthyInputs() with { SourceCompatibility = ClaudeSourceCompatibilityClassification.Incompatible }];
        yield return [HealthyInputs() with { SourceCompatibility = ClaudeSourceCompatibilityClassification.Unreadable }];
        yield return [HealthyInputs(Window(acceptedIngestExists: false, rejectedIngestExists: true))];
        yield return [HealthyInputs(Window(acceptedIngestExists: true))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.NotStarted))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.Pending))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionEvidence: ClaudeProjectionEvidence.Failed))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: false))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Partial))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Rich))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Full,
            agreedContentState: ClaudeAgreedContentState.Available))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Full,
            agreedContentState: ClaudeAgreedContentState.Redacted))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Full,
            agreedContentState: ClaudeAgreedContentState.NotCaptured))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Full,
            agreedContentState: ClaudeAgreedContentState.Unsupported))];
        yield return [HealthyInputs(Window(
            acceptedIngestExists: true,
            rawPersistenceCandidateExists: true,
            projectionCandidateExists: true,
            exactSessionBindingCandidateExists: true,
            boundSessionCompleteness: ClaudeBoundSessionCompleteness.Unavailable,
            agreedContentState: ClaudeAgreedContentState.Unreadable))];
        yield return [HealthyInputs() with { EffectiveContentGate = ClaudeEffectiveContentGate.Disabled }];
        yield return [HealthyInputs() with { EffectiveContentGate = ClaudeEffectiveContentGate.Unreadable }];
        yield return [HealthyInputs() with { RuntimeRawAccess = ClaudeRuntimeRawAccessClassification.SanitizedOnly }];
        yield return [HealthyInputs() with { RuntimeRawAccess = ClaudeRuntimeRawAccessClassification.Absent }];
        yield return [HealthyInputs() with { RuntimeRawAccess = ClaudeRuntimeRawAccessClassification.Unreadable }];
        yield return [HealthyInputs() with { SetupLedger = ClaudeSetupLedgerClassification.AwaitingAcceptedIngest }];
        yield return [HealthyInputs() with { SetupLedger = ClaudeSetupLedgerClassification.AcceptedIngestAfterApply }];
        yield return [HealthyInputs() with { SetupLedger = ClaudeSetupLedgerClassification.Unreadable }];
    }

    private static ClaudeDoctorFactInputs HealthyInputs(
        ClaudeDoctorVerificationWindow? window = null) =>
        new(
            ClaudeLivenessProbeClassification.MonitorLive,
            MonitorDatabaseFileExists: true,
            ClaudeSourceVersionClassification.Supported,
            CanonicalMonitorOrigin: "http://127.0.0.1:4320",
            ClaudeEndpointValueClassification.Match,
            ClaudeProtocolValueClassification.HttpProtobuf,
            ClaudeGateValueClassification.Enabled,
            ClaudeGateValueClassification.Enabled,
            ReadinessProbeSucceeded: true,
            ClaudeSourceCompatibilityClassification.Matching,
            window,
            ClaudeEffectiveContentGate.Enabled,
            ClaudeRuntimeRawAccessClassification.Available,
            ClaudeSetupLedgerClassification.NoAppliedChangeSet);

    private static ClaudeDoctorVerificationWindow Window(
        bool acceptedIngestExists = false,
        bool rejectedIngestExists = false,
        bool rawPersistenceCandidateExists = false,
        bool projectionCandidateExists = false,
        ClaudeProjectionEvidence projectionEvidence = ClaudeProjectionEvidence.NotStarted,
        bool exactSessionBindingCandidateExists = false,
        ClaudeBoundSessionCompleteness boundSessionCompleteness = ClaudeBoundSessionCompleteness.Unavailable,
        ClaudeAgreedContentState agreedContentState = ClaudeAgreedContentState.None) =>
        new(
            acceptedIngestExists,
            rejectedIngestExists,
            rawPersistenceCandidateExists,
            projectionCandidateExists,
            projectionEvidence,
            exactSessionBindingCandidateExists,
            boundSessionCompleteness,
            agreedContentState);

    private static DoctorFactSnapshot Map(ClaudeDoctorFactInputs inputs) =>
        ClaudeDoctorFactMapper.Map(inputs, ObservedAt, VerificationId);

    private static DoctorResult Evaluate(ClaudeDoctorFactInputs inputs) =>
        DoctorEvaluator.Evaluate(Map(inputs));

    private static void AssertPrimaryState(DoctorResult result, DoctorStateCode expected)
    {
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.NotNull(result.Evaluation);
        Assert.NotNull(result.Evaluation!.PrimaryState);
        Assert.Equal(expected, result.Evaluation.PrimaryState!.StateCode);
    }
}

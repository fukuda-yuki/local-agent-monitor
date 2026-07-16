namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorEvaluatorTests
{
    private static readonly string[] FamilyOrder =
    [
        "install_and_source_version",
        "process_receiver_and_port",
        "source_effective_configuration",
        "endpoint_reachability",
        "protocol_and_signal_compatibility",
        "source_version_and_schema_diagnostics",
        "last_ingest",
        "raw_persistence",
        "projection",
        "exact_session_binding",
        "completeness_and_content",
        "restart_or_new_process",
    ];

    [Fact]
    public void Evaluate_MultipleBlockers_EmitsOnlyBlockersInCatalogOrder()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.NotBound, PortOwnerStatus.Foreign),
            SourceEffectiveConfiguration = new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Mismatch),
            EndpointReachability = new EndpointReachabilityFacts(ReachabilityStatus.Unreachable),
            ProtocolAndSignalCompatibility = new ProtocolAndSignalCompatibilityFacts(ProtocolStatus.Mismatch, TraceSignalStatus.Disabled),
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.DriftDetected),
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Disabled, RawAccessStatus.SanitizedOnly),
            RestartOrNewProcess = new RestartOrNewProcessFacts(RestartRequirement.Required),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(
            [
                DoctorStateCode.ReceiverNotBound,
                DoctorStateCode.PortOwnedByForeignProcess,
                DoctorStateCode.EndpointMismatch,
                DoctorStateCode.ProtocolMismatch,
                DoctorStateCode.SignalDisabled,
                DoctorStateCode.AgentRestartRequired,
                DoctorStateCode.EndpointUnreachable,
            ],
            evaluation.States.Select(state => state.StateCode));
        Assert.Equal(DoctorStateCode.ReceiverNotBound, evaluation.PrimaryState?.StateCode);
        Assert.DoesNotContain(evaluation.States, state => state.StateCode is
            DoctorStateCode.ReadyNoRealTrace or
            DoctorStateCode.FirstTraceReady or
            DoctorStateCode.ContentCaptureDisabled or
            DoctorStateCode.SanitizedOnlyRawUnavailable or
            DoctorStateCode.SchemaDriftDetected);
    }

    [Fact]
    public void Evaluate_NoBlockers_EmitsTerminalThenAdvisoriesInFixedOrder()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.DriftDetected),
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Unsupported, RawAccessStatus.SanitizedOnly),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(
            [
                DoctorStateCode.ReadyNoRealTrace,
                DoctorStateCode.ContentCaptureDisabled,
                DoctorStateCode.SanitizedOnlyRawUnavailable,
                DoctorStateCode.SchemaDriftDetected,
            ],
            evaluation.States.Select(state => state.StateCode));
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, evaluation.PrimaryState?.StateCode);
    }

    [Fact]
    public void Evaluate_CompleteRealTraceWithUnrelatedSchemaDrift_RemainsFirstTraceReady()
    {
        var snapshot = DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true) with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.DriftDetected),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(
            [DoctorStateCode.FirstTraceReady, DoctorStateCode.SchemaDriftDetected],
            evaluation.States.Select(state => state.StateCode));
        Assert.Equal(DoctorStateCode.FirstTraceReady, evaluation.PrimaryState?.StateCode);
        Assert.Equal(
            ["event-ingest", "event-raw", "event-projection", "event-binding", "event-content"],
            evaluation.PrimaryState?.EvidenceRefs);
    }

    [Fact]
    public void Evaluate_EachApplicabilityRule_EmitsItsCatalogState()
    {
        foreach (var (stateCode, snapshot) in StateSnapshots())
        {
            var result = DoctorEvaluator.Evaluate(snapshot);
            var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
            var state = Assert.Single(evaluation.States, state => state.StateCode == stateCode);
            var catalog = DoctorCatalog.Get(stateCode);

            Assert.Equal(catalog.Severity, state.Severity);
            Assert.Equal(catalog.Retryability, state.Retryability);
            Assert.Equal(catalog.NextAction, state.NextAction);
            Assert.Equal(catalog.ReasonCodes, state.ReasonCodes);
        }
    }

    [Fact]
    public void Evaluate_AllTwelveUnknownFamilies_ReturnsFixedPartialProjection()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            InstallAndSourceVersion = null,
            ProcessReceiverAndPort = null,
            SourceEffectiveConfiguration = null,
            EndpointReachability = null,
            ProtocolAndSignalCompatibility = null,
            SourceVersionAndSchemaDiagnostics = null,
            LastIngest = null,
            RawPersistence = null,
            Projection = null,
            ExactSessionBinding = null,
            CompletenessAndContent = null,
            RestartOrNewProcess = null,
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.False(result.Success);
        Assert.Equal(DoctorResultCode.PartialFactSnapshot, result.Code);
        Assert.Null(result.Verification);
        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Null(evaluation.PrimaryState);
        Assert.Empty(evaluation.States);
        Assert.Equal(FamilyOrder, evaluation.MissingFactFamilies);
    }

    [Fact]
    public void Evaluate_EachExplicitUnknownFamily_ReturnsItsOrderedPartialProjection()
    {
        foreach (var (familyName, snapshot) in ExplicitUnknownFamilySnapshots())
        {
            var result = DoctorEvaluator.Evaluate(snapshot);

            Assert.False(result.Success);
            Assert.Equal(DoctorResultCode.PartialFactSnapshot, result.Code);
            Assert.Null(result.Verification);
            var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
            Assert.Null(evaluation.PrimaryState);
            Assert.Empty(evaluation.States);
            Assert.Equal([familyName], evaluation.MissingFactFamilies);
        }
    }

    [Fact]
    public void Evaluate_AdvisoryOnlyUnknowns_KeepSupportedTerminalAndReportMissingFamilies()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.Unknown),
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Unknown, RawAccessStatus.Unknown),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, evaluation.PrimaryState?.StateCode);
        Assert.Equal(
            ["source_version_and_schema_diagnostics", "completeness_and_content"],
            evaluation.MissingFactFamilies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_AdvisoryOnlyUnknownContentFact_PreservesFirstTraceReady(bool contentCaptureUnknown)
    {
        var snapshot = DoctorTestSnapshots.FirstTraceReady() with
        {
            CompletenessAndContent = new CompletenessAndContentFacts(
                DoctorCompleteness.Full,
                contentCaptureUnknown ? ContentCaptureStatus.Unknown : ContentCaptureStatus.Enabled,
                contentCaptureUnknown ? RawAccessStatus.Available : RawAccessStatus.Unknown),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(DoctorStateCode.FirstTraceReady, evaluation.PrimaryState?.StateCode);
        Assert.Equal([DoctorStateCode.FirstTraceReady], evaluation.States.Select(state => state.StateCode));
        Assert.Equal(["completeness_and_content"], evaluation.MissingFactFamilies);
    }

    [Fact]
    public void Evaluate_UnknownContentFactWithMissingRealReceipt_KeepsReadyNoRealTrace()
    {
        var snapshot = DoctorTestSnapshots.FirstTraceReady() with
        {
            Observations =
            [
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.RawPersistence, "event-raw"),
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.Projection, "event-projection"),
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.CompletenessContent, "event-content"),
            ],
            CompletenessAndContent = new CompletenessAndContentFacts(
                DoctorCompleteness.Full,
                ContentCaptureStatus.Unknown,
                RawAccessStatus.Available),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(["completeness_and_content"], result.Evaluation?.MissingFactFamilies);
    }

    [Fact]
    public void Evaluate_SyntheticReceiptPersistenceAndProjection_CannotSatisfyFirstTraceReady()
    {
        var snapshot = DoctorTestSnapshots.FirstTraceReady() with
        {
            Observations =
            [
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.Ingest, "probe-ingest", DoctorEvidenceClass.SyntheticProbe),
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.RawPersistence, "probe-raw", DoctorEvidenceClass.SyntheticProbe),
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.Projection, "probe-projection", DoctorEvidenceClass.SyntheticProbe),
                DoctorTestSnapshots.Observation(DoctorEvidenceKind.CompletenessContent, "event-content"),
            ],
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, evaluation.PrimaryState?.StateCode);
        Assert.DoesNotContain(evaluation.States, state => state.StateCode == DoctorStateCode.FirstTraceReady);
    }

    [Fact]
    public void Evaluate_SyntheticPersistenceHealth_RemainsAValidPendingDiagnosisWithoutRealReceipt()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            Observations =
            [
                DoctorTestSnapshots.Observation(
                    DoctorEvidenceKind.RawPersistence,
                    "probe-raw",
                    DoctorEvidenceClass.SyntheticProbe),
            ],
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
            Projection = new ProjectionFacts(ProjectionOutcome.Pending),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.Equal(
            DoctorStateCode.RawPersistedProjectionPending,
            Assert.IsType<DoctorEvaluation>(result.Evaluation).PrimaryState?.StateCode);
    }

    [Fact]
    public void Evaluate_NotRequiredExactBinding_IsValidAndDoesNotCreateSessionBlocker()
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            ExactSessionBinding = new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.NotRequired,
                ExactSessionBindingOutcome.ExactBound),
        };

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.True(result.Success);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, result.Evaluation?.PrimaryState?.StateCode);
    }

    [Fact]
    public void Evaluate_NoExpectedAdapter_AllowsCanonicalObservationAdapterAndFirstReadyUsesRealReferencesOnly()
    {
        var snapshot = DoctorTestSnapshots.FirstTraceReady();
        var observations = snapshot.Observations
            .Select(observation => observation with { SourceAdapter = "adapter-v1" })
            .Append(DoctorTestSnapshots.Observation(
                DoctorEvidenceKind.Ingest,
                "probe-ingest",
                DoctorEvidenceClass.SyntheticProbe,
                sourceAdapter: "adapter-v1"))
            .ToArray();

        var result = DoctorEvaluator.Evaluate(snapshot with { Observations = observations });

        Assert.True(result.Success);
        var evaluation = Assert.IsType<DoctorEvaluation>(result.Evaluation);
        Assert.Equal(DoctorStateCode.FirstTraceReady, evaluation.PrimaryState?.StateCode);
        Assert.Equal(
            ["event-ingest", "event-raw", "event-projection", "event-content"],
            evaluation.PrimaryState?.EvidenceRefs);
    }

    internal static IEnumerable<(DoctorStateCode, DoctorFactSnapshot)> StateSnapshots()
    {
        var baseline = DoctorTestSnapshots.ReadyNoRealTrace();
        yield return (DoctorStateCode.MonitorNotInstalled, baseline with
        {
            InstallAndSourceVersion = new InstallAndSourceVersionFacts(MonitorInstallStatus.NotInstalled, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
        });
        yield return (DoctorStateCode.MonitorNotRunning, baseline with
        {
            ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.NotRunning, ReceiverBindStatus.NotBound, PortOwnerStatus.None),
        });
        yield return (DoctorStateCode.ReceiverNotBound, baseline with
        {
            ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.NotBound, PortOwnerStatus.None),
        });
        yield return (DoctorStateCode.PortOwnedByForeignProcess, baseline with
        {
            ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.NotBound, PortOwnerStatus.Foreign),
        });
        yield return (DoctorStateCode.EndpointMismatch, baseline with
        {
            SourceEffectiveConfiguration = new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Mismatch),
        });
        yield return (DoctorStateCode.ProtocolMismatch, baseline with
        {
            ProtocolAndSignalCompatibility = new ProtocolAndSignalCompatibilityFacts(ProtocolStatus.Mismatch, TraceSignalStatus.Enabled),
        });
        yield return (DoctorStateCode.SignalDisabled, baseline with
        {
            ProtocolAndSignalCompatibility = new ProtocolAndSignalCompatibilityFacts(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Disabled),
        });
        yield return (DoctorStateCode.UnsupportedSourceVersion, baseline with
        {
            InstallAndSourceVersion = new InstallAndSourceVersionFacts(MonitorInstallStatus.Installed, SourceVersionStatus.Unsupported, SourceFeatureStatus.Available),
        });
        yield return (DoctorStateCode.FeatureUnavailable, baseline with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.FeatureUnavailable, SchemaStatus.Matching),
        });
        yield return (DoctorStateCode.AgentRestartRequired, baseline with
        {
            RestartOrNewProcess = new RestartOrNewProcessFacts(RestartRequirement.Required),
        });
        yield return (DoctorStateCode.EndpointUnreachable, baseline with
        {
            EndpointReachability = new EndpointReachabilityFacts(ReachabilityStatus.Unreachable),
        });
        yield return (DoctorStateCode.PayloadRejected, baseline with
        {
            LastIngest = new LastIngestFacts(LastIngestOutcome.Rejected),
        });
        yield return (DoctorStateCode.RawPersistedProjectionPending, baseline with
        {
            LastIngest = new LastIngestFacts(LastIngestOutcome.Accepted),
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
            Projection = new ProjectionFacts(ProjectionOutcome.Pending),
        });
        yield return (DoctorStateCode.ProjectionFailed, baseline with
        {
            LastIngest = new LastIngestFacts(LastIngestOutcome.Accepted),
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
            Projection = new ProjectionFacts(ProjectionOutcome.Failed),
        });
        yield return (DoctorStateCode.SessionUnbound, baseline with
        {
            ExactSessionBinding = new ExactSessionBindingFacts(ExactSessionBindingRequirement.Required, ExactSessionBindingOutcome.Unbound),
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Unbound, ContentCaptureStatus.Enabled, RawAccessStatus.Available),
        });
        yield return (DoctorStateCode.ContentCaptureDisabled, baseline with
        {
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Disabled, RawAccessStatus.Available),
        });
        yield return (DoctorStateCode.SanitizedOnlyRawUnavailable, baseline with
        {
            CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Enabled, RawAccessStatus.SanitizedOnly),
        });
        yield return (DoctorStateCode.SchemaDriftDetected, baseline with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Supported, SchemaStatus.DriftDetected),
        });
        yield return (DoctorStateCode.ReadyNoRealTrace, baseline);
        yield return (DoctorStateCode.FirstTraceReady, DoctorTestSnapshots.FirstTraceReady());
    }

    private static IEnumerable<(string FamilyName, DoctorFactSnapshot Snapshot)> ExplicitUnknownFamilySnapshots()
    {
        var baseline = DoctorTestSnapshots.ReadyNoRealTrace();
        yield return (FamilyOrder[0], baseline with
        {
            InstallAndSourceVersion = new InstallAndSourceVersionFacts(
                MonitorInstallStatus.Unknown,
                SourceVersionStatus.Supported,
                SourceFeatureStatus.Available),
        });
        yield return (FamilyOrder[1], baseline with
        {
            ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(
                MonitorProcessStatus.Unknown,
                ReceiverBindStatus.Bound,
                PortOwnerStatus.Monitor),
        });
        yield return (FamilyOrder[2], baseline with
        {
            SourceEffectiveConfiguration = new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Unknown),
        });
        yield return (FamilyOrder[3], baseline with
        {
            EndpointReachability = new EndpointReachabilityFacts(ReachabilityStatus.Unknown),
        });
        yield return (FamilyOrder[4], baseline with
        {
            ProtocolAndSignalCompatibility = new ProtocolAndSignalCompatibilityFacts(
                ProtocolStatus.Unknown,
                TraceSignalStatus.Enabled),
        });
        yield return (FamilyOrder[5], baseline with
        {
            SourceVersionAndSchemaDiagnostics = new SourceVersionAndSchemaDiagnosticsFacts(
                SourceCompatibilityStatus.Unknown,
                SchemaStatus.Matching),
        });
        yield return (FamilyOrder[6], baseline with
        {
            LastIngest = new LastIngestFacts(LastIngestOutcome.Unknown),
        });
        yield return (FamilyOrder[7], baseline with
        {
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Unknown),
        });
        yield return (FamilyOrder[8], baseline with
        {
            Projection = new ProjectionFacts(ProjectionOutcome.Unknown),
        });
        yield return (FamilyOrder[9], baseline with
        {
            ExactSessionBinding = new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.Unknown,
                ExactSessionBindingOutcome.Unknown),
        });
        yield return (FamilyOrder[10], DoctorTestSnapshots.FirstTraceReady() with
        {
            CompletenessAndContent = new CompletenessAndContentFacts(
                DoctorCompleteness.Unknown,
                ContentCaptureStatus.Enabled,
                RawAccessStatus.Available),
        });
        yield return (FamilyOrder[11], baseline with
        {
            RestartOrNewProcess = new RestartOrNewProcessFacts(RestartRequirement.Unknown),
        });
    }
}

using System.Reflection;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorSourceHandoffContractTests
{
    private const string InvalidCompositionMessage =
        "Source handoff produced an invalid Doctor fact snapshot.";
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T00:00:00.0000000Z");
    private static readonly string[] ExpectedSourceSurfaces =
        ["claude-code", "github-copilot-cli", "github-copilot-vscode"];

    [Fact]
    public void DirectComposition_MapsFixedAuthorityAndPreservesObservations()
    {
        var setup = CreateSetupContribution();
        var runtime = CreateRuntimeContribution();
        var observations = new[]
        {
            new DoctorObservation(
                "github-copilot-vscode",
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.Ingest,
                "ingest-receipt-1",
                ObservedAt),
        };

        var snapshot = InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            setup,
            runtime,
            observations);

        Assert.Equal(DoctorSchemaVersions.FactsV1, snapshot.SchemaVersion);
        Assert.Equal("github-copilot-vscode", snapshot.SourceSurface);
        Assert.Null(snapshot.ExpectedSourceAdapter);
        Assert.Null(snapshot.VerificationId);
        Assert.Equal(observations, snapshot.Observations.ToArray());
        Assert.Equal(new InstallAndSourceVersionFacts(
            MonitorInstallStatus.Installed,
            SourceVersionStatus.Supported,
            SourceFeatureStatus.Available), snapshot.InstallAndSourceVersion);
        Assert.Equal(new ProcessReceiverAndPortFacts(
            MonitorProcessStatus.Running,
            ReceiverBindStatus.Bound,
            PortOwnerStatus.Monitor), snapshot.ProcessReceiverAndPort);
        Assert.Equal(new SourceEffectiveConfigurationFacts(
            EndpointAlignmentStatus.Match), snapshot.SourceEffectiveConfiguration);
        Assert.Equal(new EndpointReachabilityFacts(
            ReachabilityStatus.Reachable), snapshot.EndpointReachability);
        Assert.Equal(new ProtocolAndSignalCompatibilityFacts(
            ProtocolStatus.HttpProtobuf,
            TraceSignalStatus.Enabled), snapshot.ProtocolAndSignalCompatibility);
        Assert.Equal(new SourceVersionAndSchemaDiagnosticsFacts(
            SourceCompatibilityStatus.Supported,
            SchemaStatus.Matching), snapshot.SourceVersionAndSchemaDiagnostics);
        Assert.Equal(new LastIngestFacts(LastIngestOutcome.Accepted), snapshot.LastIngest);
        Assert.Equal(new RawPersistenceFacts(RawPersistenceOutcome.Persisted), snapshot.RawPersistence);
        Assert.Equal(new ProjectionFacts(ProjectionOutcome.Completed), snapshot.Projection);
        Assert.Equal(new ExactSessionBindingFacts(
            ExactSessionBindingRequirement.Required,
            ExactSessionBindingOutcome.ExactBound), snapshot.ExactSessionBinding);
        Assert.Equal(new CompletenessAndContentFacts(
            DoctorCompleteness.Full,
            ContentCaptureStatus.Enabled,
            RawAccessStatus.Available), snapshot.CompletenessAndContent);
        Assert.Equal(new RestartOrNewProcessFacts(
            RestartRequirement.NotRequired), snapshot.RestartOrNewProcess);
    }

    [Fact]
    public void VerificationComposition_UsesVerificationIdentityAndNoCallerObservations()
    {
        var verification = ActiveVerification();

        var snapshot = InvokeCompletion(
            verification,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution());

        Assert.Equal("claude-code", snapshot.SourceSurface);
        Assert.Null(snapshot.ExpectedSourceAdapter);
        Assert.Equal(verification.VerificationId, snapshot.VerificationId);
        Assert.Empty(snapshot.Observations);
    }

    [Fact]
    public void CandidateComposition_CopiesVerificationScopeAndExpiry()
    {
        var verification = ActiveVerification();
        var candidate = InvokeCandidate(
            verification,
            "01890abc-def0-7000-8000-000000000003",
            DoctorEvidenceClass.RealSource,
            DoctorEvidenceKind.Ingest,
            "ingest-receipt-2",
            ObservedAt.AddMinutes(1));

        Assert.Equal("01890abc-def0-7000-8000-000000000003", candidate.CandidateId);
        Assert.Equal(verification.VerificationId, candidate.VerificationId);
        Assert.Equal(verification.ExpectedSourceSurface, candidate.SourceSurface);
        Assert.Equal(verification.ExpectedSourceAdapter, candidate.SourceAdapter);
        Assert.Equal(DoctorEvidenceClass.RealSource, candidate.EvidenceClass);
        Assert.Equal(DoctorEvidenceKind.Ingest, candidate.EvidenceKind);
        Assert.Equal("ingest-receipt-2", candidate.EvidenceRef);
        Assert.Equal(ObservedAt.AddMinutes(1), candidate.ObservedAt);
        Assert.Equal(verification.ExpiresAt, candidate.ExpiresAt);
    }

    [Fact]
    public void CandidateOutsideVerificationWindow_UsesFixedSanitizedError()
    {
        AssertInvalidComposition(() => InvokeCandidate(
            ActiveVerification(),
            "01890abc-def0-7000-8000-000000000004",
            DoctorEvidenceClass.RealSource,
            DoctorEvidenceKind.Ingest,
            "ingest-receipt-3",
            ObservedAt.AddMinutes(5)));
    }

    [Fact]
    public void UnsafeObservation_UsesFixedSanitizedError()
    {
        var observations = new[]
        {
            new DoctorObservation(
                "github-copilot-vscode",
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.Ingest,
                "prompt: secret-value",
                ObservedAt),
        };

        AssertInvalidComposition(() => InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            observations));
    }

    [Fact]
    public void InvalidSourceIdentity_UsesFixedSanitizedError()
    {
        AssertInvalidComposition(() => InvokeDirect(
            "Claude Code",
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            []));
    }

    [Fact]
    public void SourceMismatchedObservation_UsesFixedSanitizedError()
    {
        var observations = new[]
        {
            new DoctorObservation(
                "claude-code",
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.Ingest,
                "mismatched-ingest-receipt",
                ObservedAt),
        };

        AssertInvalidComposition(() => InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            observations));
    }

    [Fact]
    public void InactiveVerification_UsesFixedSanitizedError()
    {
        var verification = new DoctorVerification(
            "01890abc-def0-7000-8000-000000000002",
            "claude-code",
            null,
            DoctorVerificationState.Cancelled,
            2,
            ObservedAt,
            ObservedAt.AddMinutes(5),
            null,
            ObservedAt.AddMinutes(1),
            []);

        AssertInvalidComposition(() => InvokeCompletion(
            verification,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution()));
    }

    [Theory]
    [InlineData("github-copilot-vscode")]
    [InlineData("claude-code")]
    public void SyntheticEvidence_DoesNotSatisfyFirstTraceReadyAcrossSources(string sourceSurface)
    {
        var observations = new[]
        {
            new DoctorObservation(
                sourceSurface,
                null,
                DoctorEvidenceClass.SyntheticProbe,
                DoctorEvidenceKind.Ingest,
                "synthetic-ingest-receipt",
                ObservedAt),
            new DoctorObservation(
                sourceSurface,
                null,
                DoctorEvidenceClass.SyntheticProbe,
                DoctorEvidenceKind.RawPersistence,
                "synthetic-raw-receipt",
                ObservedAt),
            new DoctorObservation(
                sourceSurface,
                null,
                DoctorEvidenceClass.SyntheticProbe,
                DoctorEvidenceKind.Projection,
                "synthetic-projection-receipt",
                ObservedAt),
            new DoctorObservation(
                sourceSurface,
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.ExactSessionBinding,
                "exact-binding-receipt",
                ObservedAt),
            new DoctorObservation(
                sourceSurface,
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.CompletenessContent,
                "completeness-receipt",
                ObservedAt),
        };
        var snapshot = InvokeDirect(
            sourceSurface,
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            observations);

        var result = DoctorEvaluator.Evaluate(snapshot);

        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, result.Evaluation?.PrimaryState?.StateCode);
        Assert.DoesNotContain(
            result.Evaluation?.States ?? [],
            state => state.StateCode == DoctorStateCode.FirstTraceReady);
    }

    [Fact]
    public void DoctorCoreDefinesNoSourceSpecificDoctorEnum()
    {
        var prohibited = typeof(DoctorFactSnapshot).Assembly.GetTypes()
            .Where(type => type.IsEnum)
            .Where(type =>
                type.Name.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(prohibited);
    }

    [Fact]
    public void SourceHandoffRegistrations_AreUniqueManifestBackedAndOutsideDoctorCore()
    {
        var doctorAssembly = typeof(DoctorFactSnapshot).Assembly;
        var registrations = SourceHandoffRegistrations();
        var surfaces = registrations.Select(registration => registration.SourceSurface).ToArray();

        Assert.DoesNotContain(registrations, registration => registration.Type.Assembly == doctorAssembly);
        Assert.Equal(surfaces.Length, surfaces.Distinct(StringComparer.Ordinal).Count());
        Assert.All(surfaces, surface => Assert.Contains(surface, ExpectedSourceSurfaces));
    }

    [Fact]
    public void GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
        AssertSourceHandoff("github-copilot-vscode");

    [Fact]
    public void GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore() =>
        AssertSourceHandoff("github-copilot-cli");

    [Fact]
    public void ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
        AssertSourceHandoff("claude-code");

    private static DoctorVerification ActiveVerification() => new(
        "01890abc-def0-7000-8000-000000000001",
        "claude-code",
        null,
        DoctorVerificationState.Active,
        1,
        ObservedAt,
        ObservedAt.AddMinutes(5),
        null,
        null,
        []);

    private static void AssertInvalidComposition(Action action)
    {
        var exception = Assert.Throws<TargetInvocationException>(action);
        var argument = Assert.IsType<ArgumentException>(exception.InnerException);

        Assert.Equal(InvalidCompositionMessage, argument.Message);
        Assert.DoesNotContain("secret-value", argument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", argument.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Claude Code", argument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ingest-receipt-3", argument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mismatched-ingest-receipt", argument.Message, StringComparison.Ordinal);
    }

    private static void AssertSourceHandoff(string expectedSourceSurface)
    {
        var registration = Assert.Single(SourceHandoffRegistrations().Where(registration => string.Equals(
            registration.SourceSurface,
            expectedSourceSurface,
            StringComparison.Ordinal)));
        var instance = Assert.IsAssignableFrom<IDoctorSourceHandoff>(
            Activator.CreateInstance(registration.Type, nonPublic: true));

        Assert.Equal(expectedSourceSurface, instance.SourceSurface);
        Assert.Null(instance.ExpectedSourceAdapter);
    }

    private static IReadOnlyList<(Type Type, string SourceSurface)> SourceHandoffRegistrations()
    {
        var doctorAssembly = typeof(DoctorFactSnapshot).Assembly;
        var interfaceType = RequireDoctorType("IDoctorSourceHandoff");
        var attributeType = RequireDoctorType("DoctorSourceHandoffAttribute");
        var implementationTypes = new[]
        {
            doctorAssembly,
            typeof(CliApplication).Assembly,
            typeof(MonitorHost).Assembly,
        }
        .Distinct()
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsAbstract && !type.IsInterface && interfaceType.IsAssignableFrom(type))
        .ToArray();

        return implementationTypes.Select(type =>
        {
            var attribute = Assert.Single(type.GetCustomAttributes(attributeType, inherit: false));
            var sourceSurface = Assert.IsType<string>(
                attributeType.GetProperty("SourceSurface")!.GetValue(attribute));
            return (type, sourceSurface);
        }).ToArray();
    }

    private static object CreateSetupContribution() => Activator.CreateInstance(
        RequireDoctorType("DoctorSetupFactContribution"),
        new InstallAndSourceVersionFacts(
            MonitorInstallStatus.Installed,
            SourceVersionStatus.Supported,
            SourceFeatureStatus.Available),
        new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Match),
        new EndpointReachabilityFacts(ReachabilityStatus.Reachable),
        new ProtocolAndSignalCompatibilityFacts(
            ProtocolStatus.HttpProtobuf,
            TraceSignalStatus.Enabled),
        new RestartOrNewProcessFacts(RestartRequirement.NotRequired))!;

    private static object CreateRuntimeContribution() => Activator.CreateInstance(
        RequireDoctorType("DoctorRuntimeFactContribution"),
        new ProcessReceiverAndPortFacts(
            MonitorProcessStatus.Running,
            ReceiverBindStatus.Bound,
            PortOwnerStatus.Monitor),
        new SourceVersionAndSchemaDiagnosticsFacts(
            SourceCompatibilityStatus.Supported,
            SchemaStatus.Matching),
        new LastIngestFacts(LastIngestOutcome.Accepted),
        new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
        new ProjectionFacts(ProjectionOutcome.Completed),
        new ExactSessionBindingFacts(
            ExactSessionBindingRequirement.Required,
            ExactSessionBindingOutcome.ExactBound),
        new CompletenessAndContentFacts(
            DoctorCompleteness.Full,
            ContentCaptureStatus.Enabled,
            RawAccessStatus.Available))!;

    private static DoctorFactSnapshot InvokeDirect(
        string sourceSurface,
        string? sourceAdapter,
        DateTimeOffset observedAt,
        object setup,
        object runtime,
        IReadOnlyList<DoctorObservation> observations) =>
        (DoctorFactSnapshot)InvokeComposer(
            "ComposeDirectEvaluation",
            sourceSurface,
            sourceAdapter,
            observedAt,
            setup,
            runtime,
            observations)!;

    private static DoctorFactSnapshot InvokeCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        object setup,
        object runtime) =>
        (DoctorFactSnapshot)InvokeComposer(
            "ComposeVerificationCompletion",
            verification,
            observedAt,
            setup,
            runtime)!;

    private static DoctorEvidenceCandidate InvokeCandidate(
        DoctorVerification verification,
        string candidateId,
        DoctorEvidenceClass evidenceClass,
        DoctorEvidenceKind evidenceKind,
        string evidenceRef,
        DateTimeOffset observedAt) =>
        (DoctorEvidenceCandidate)InvokeComposer(
            "ComposeCandidate",
            verification,
            candidateId,
            evidenceClass,
            evidenceKind,
            evidenceRef,
            observedAt)!;

    private static object? InvokeComposer(string methodName, params object?[] arguments)
    {
        var composer = RequireDoctorType("DoctorSourceHandoffComposer");
        var method = Assert.Single(composer.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName));
        return method.Invoke(null, arguments);
    }

    private static Type RequireDoctorType(string name)
    {
        var type = typeof(DoctorFactSnapshot).Assembly.GetType(
            $"CopilotAgentObservability.Doctor.{name}",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(type is not null, $"Missing Doctor contract type: {name}");
        return type!;
    }
}

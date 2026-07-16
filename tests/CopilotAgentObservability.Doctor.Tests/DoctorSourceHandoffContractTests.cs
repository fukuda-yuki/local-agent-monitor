using System.Reflection;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorSourceHandoffContractTests
{
    private const string InvalidCompositionMessage =
        "Source handoff produced an invalid Doctor fact snapshot.";
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T00:00:00.0000000Z");

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
        var verification = new DoctorVerification(
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
    public void InvalidComposition_UsesFixedSanitizedError()
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

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            observations));
        var argument = Assert.IsType<ArgumentException>(exception.InnerException);

        Assert.Equal(InvalidCompositionMessage, argument.Message);
        Assert.DoesNotContain("secret-value", argument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", argument.Message, StringComparison.OrdinalIgnoreCase);
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
    public void ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore()
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
        .Where(type => !type.IsAbstract && interfaceType.IsAssignableFrom(type))
        .ToArray();

        Assert.DoesNotContain(implementationTypes, type => type.Assembly == doctorAssembly);

        var actualSurfaces = implementationTypes
            .Select(type => type.GetCustomAttributes(attributeType, inherit: false).SingleOrDefault())
            .Where(attribute => attribute is not null)
            .Select(attribute => Assert.IsType<string>(
                attributeType.GetProperty("SourceSurface")!.GetValue(attribute)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["claude-code", "github-copilot-cli", "github-copilot-vscode"],
            actualSurfaces);
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

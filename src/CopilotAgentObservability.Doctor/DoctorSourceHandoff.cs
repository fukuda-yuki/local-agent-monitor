namespace CopilotAgentObservability.Doctor;

public sealed record DoctorSetupFactContribution(
    InstallAndSourceVersionFacts? InstallAndSourceVersion,
    SourceEffectiveConfigurationFacts? SourceEffectiveConfiguration,
    EndpointReachabilityFacts? EndpointReachability,
    ProtocolAndSignalCompatibilityFacts? ProtocolAndSignalCompatibility,
    RestartOrNewProcessFacts? RestartOrNewProcess);

public sealed record DoctorRuntimeFactContribution(
    ProcessReceiverAndPortFacts? ProcessReceiverAndPort,
    SourceVersionAndSchemaDiagnosticsFacts? SourceVersionAndSchemaDiagnostics,
    LastIngestFacts? LastIngest,
    RawPersistenceFacts? RawPersistence,
    ProjectionFacts? Projection,
    ExactSessionBindingFacts? ExactSessionBinding,
    CompletenessAndContentFacts? CompletenessAndContent);

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DoctorSourceHandoffAttribute : Attribute
{
    public DoctorSourceHandoffAttribute(string sourceSurface)
    {
        SourceSurface = sourceSurface;
    }

    public string SourceSurface { get; }
}

public interface IDoctorSourceHandoff
{
    string SourceSurface { get; }

    string? ExpectedSourceAdapter { get; }

    DoctorFactSnapshot ComposeDirectEvaluation(
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations);

    DoctorFactSnapshot ComposeVerificationCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts);
}

public static class DoctorSourceHandoffComposer
{
    private const string InvalidCompositionMessage =
        "Source handoff produced an invalid Doctor fact snapshot.";

    public static DoctorFactSnapshot ComposeDirectEvaluation(
        string sourceSurface,
        string? expectedSourceAdapter,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations)
    {
        if (setupFacts is null || runtimeFacts is null || observations is null)
        {
            throw InvalidComposition();
        }

        return Compose(
            sourceSurface,
            expectedSourceAdapter,
            observedAt,
            verificationId: null,
            setupFacts,
            runtimeFacts,
            observations);
    }

    public static DoctorFactSnapshot ComposeVerificationCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts)
    {
        if (verification is null
            || verification.State != DoctorVerificationState.Active
            || !DoctorValidation.IsValidVerification(verification)
            || setupFacts is null
            || runtimeFacts is null)
        {
            throw InvalidComposition();
        }

        return Compose(
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            observedAt,
            verification.VerificationId,
            setupFacts,
            runtimeFacts,
            []);
    }

    private static DoctorFactSnapshot Compose(
        string sourceSurface,
        string? expectedSourceAdapter,
        DateTimeOffset observedAt,
        string? verificationId,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations)
    {
        var snapshot = new DoctorFactSnapshot(
            DoctorSchemaVersions.FactsV1,
            sourceSurface,
            expectedSourceAdapter,
            observedAt,
            verificationId,
            observations,
            setupFacts.InstallAndSourceVersion,
            runtimeFacts.ProcessReceiverAndPort,
            setupFacts.SourceEffectiveConfiguration,
            setupFacts.EndpointReachability,
            setupFacts.ProtocolAndSignalCompatibility,
            runtimeFacts.SourceVersionAndSchemaDiagnostics,
            runtimeFacts.LastIngest,
            runtimeFacts.RawPersistence,
            runtimeFacts.Projection,
            runtimeFacts.ExactSessionBinding,
            runtimeFacts.CompletenessAndContent,
            setupFacts.RestartOrNewProcess);

        if (!DoctorValidation.IsValidFactSnapshot(snapshot))
        {
            throw InvalidComposition();
        }

        return snapshot;
    }

    private static ArgumentException InvalidComposition() =>
        new(InvalidCompositionMessage);
}

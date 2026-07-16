using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli;

internal sealed record DoctorCompletionInput(
    DoctorFactSnapshot FactSnapshot,
    IReadOnlyList<string> AcceptedEvidenceRefs);

internal interface IDoctorCliApplication
{
    DoctorResult Evaluate(DoctorFactSnapshot snapshot);

    DoctorResult Start(
        string databasePath,
        string sourceSurface,
        string? sourceAdapter,
        DateTimeOffset expiresAt);

    DoctorResult Status(string databasePath, string verificationId);

    DoctorResult Complete(
        string databasePath,
        string verificationId,
        int expectedRevision,
        DoctorCompletionInput input);

    DoctorResult Cancel(string databasePath, string verificationId, int expectedRevision);
}

internal sealed class StatelessDoctorCliApplication : IDoctorCliApplication
{
    public static StatelessDoctorCliApplication Instance { get; } = new();

    private StatelessDoctorCliApplication()
    {
    }

    public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => DoctorEvaluator.Evaluate(snapshot);

    public DoctorResult Start(
        string databasePath,
        string sourceSurface,
        string? sourceAdapter,
        DateTimeOffset expiresAt) => StoreUnavailable();

    public DoctorResult Status(string databasePath, string verificationId) => StoreUnavailable();

    public DoctorResult Complete(
        string databasePath,
        string verificationId,
        int expectedRevision,
        DoctorCompletionInput input) => StoreUnavailable();

    public DoctorResult Cancel(string databasePath, string verificationId, int expectedRevision) => StoreUnavailable();

    private static DoctorResult StoreUnavailable() =>
        new(
            DoctorSchemaVersions.ResultV1,
            Success: false,
            DoctorResultCode.DoctorStoreUnavailable,
            Evaluation: null,
            Verification: null);
}

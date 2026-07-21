using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.DoctorUi;

public sealed class DoctorUiApplicationTests
{
    private const string VerificationId = "0190c7a0-0000-7000-8000-000000000001";
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 21, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletedResult_AuthorizesOnlyPersistedAcceptedEvidenceReferences()
    {
        var result = Result(
            DoctorVerificationState.Completed,
            acceptedEvidenceRefs: ["accepted-trace", "accepted-diagnostic"],
            evaluationEvidenceRefs: ["unaccepted-evaluation"]);

        var references = DoctorUiApplication.EvidenceReferences(result);

        Assert.Equal(["accepted-trace", "accepted-diagnostic"], references);
    }

    [Fact]
    public void ActiveResult_PreservesEvaluationStateEvidenceAuthorization()
    {
        var result = Result(
            DoctorVerificationState.Active,
            acceptedEvidenceRefs: [],
            evaluationEvidenceRefs: ["active-evaluation"]);

        var references = DoctorUiApplication.EvidenceReferences(result);

        Assert.Equal(["active-evaluation"], references);
    }

    [Fact]
    public void CompletedResult_DoesNotAuthorizeMalformedAcceptedEvidenceReference()
    {
        var result = Result(
            DoctorVerificationState.Completed,
            acceptedEvidenceRefs: [@"C:\private\trace.json"],
            evaluationEvidenceRefs: []);

        Assert.Empty(DoctorUiApplication.EvidenceReferences(result));
    }

    [Theory]
    [InlineData(DoctorVerificationState.Cancelled)]
    [InlineData(DoctorVerificationState.Expired)]
    public void NonCompletedTerminalResult_DoesNotAuthorizeNavigation(DoctorVerificationState state)
    {
        var result = Result(
            state,
            acceptedEvidenceRefs: ["invalid-terminal-accepted"],
            evaluationEvidenceRefs: ["invalid-terminal-evaluation"]);

        Assert.Empty(DoctorUiApplication.EvidenceReferences(result));
    }

    [Fact]
    public void CompletedProjection_DoesNotReturnTargetPersistedForAnotherVerification()
    {
        using var database = new TempDatabase();
        var doctor = new SqliteDoctorVerificationStore(database.Path, new FixedTimeProvider(ObservedAt));
        Assert.Equal(DoctorResultCode.VerificationActive, doctor.CreateSchema().Code);
        var first = Assert.IsType<DoctorVerification>(
            doctor.Start("github-copilot-cli", "github-copilot-doctor", TimeSpan.FromMinutes(5)).Verification);
        var second = Assert.IsType<DoctorVerification>(
            doctor.Start("github-copilot-cli", "github-copilot-doctor", TimeSpan.FromMinutes(5)).Verification);
        var candidate = new DoctorEvidenceCandidate(
            Guid.CreateVersion7(first.StartedAt).ToString("D"),
            first.VerificationId,
            first.ExpectedSourceSurface,
            first.ExpectedSourceAdapter,
            DoctorEvidenceClass.RealSource,
            DoctorEvidenceKind.Ingest,
            "shared-safe-reference",
            first.StartedAt,
            first.ExpiresAt);
        Assert.Equal(DoctorResultCode.VerificationActive, doctor.ObserveCandidate(candidate).Code);
        var navigation = new SqliteFirstTraceNavigationStore(database.Path);
        navigation.Record(
            first.VerificationId,
            candidate.EvidenceRef,
            FirstTraceNavigationTargetKind.Trace,
            "0123456789abcdef0123456789abcdef");
        var firstResult = CompletedResult(first, candidate.EvidenceRef);
        var secondResult = CompletedResult(second, candidate.EvidenceRef);

        var exact = DoctorUiApplication.NavigationIdentities(first.VerificationId, firstResult, navigation);
        var crossVerification = DoctorUiApplication.NavigationIdentities(second.VerificationId, secondResult, navigation);

        Assert.Equal(
            [new DoctorUiNavigationIdentity(candidate.EvidenceRef, DoctorUiNavigationTargetKind.Trace, "0123456789abcdef0123456789abcdef")],
            exact);
        Assert.Empty(crossVerification);
    }

    private static DoctorResult Result(
        DoctorVerificationState state,
        IReadOnlyList<string> acceptedEvidenceRefs,
        IReadOnlyList<string> evaluationEvidenceRefs) =>
        new(
            DoctorSchemaVersions.ResultV1,
            Success: true,
            state switch
            {
                DoctorVerificationState.Completed => DoctorResultCode.VerificationCompleted,
                DoctorVerificationState.Cancelled => DoctorResultCode.VerificationCancelled,
                DoctorVerificationState.Expired => DoctorResultCode.VerificationExpired,
                _ => DoctorResultCode.VerificationActive,
            },
            new DoctorEvaluation(
                "github-copilot-cli",
                PrimaryState: null,
                [new DoctorState(
                    DoctorSchemaVersions.ResultV1,
                    DoctorStateCode.FirstTraceReady,
                    DoctorSeverity.Info,
                    "github-copilot-cli",
                    evaluationEvidenceRefs,
                    [DoctorStateCode.FirstTraceReady],
                    DoctorNextAction.OpenVerifiedTraceOrSession,
                    DoctorRetryability.None,
                    ObservedAt,
                    VerificationId)],
                MissingFactFamilies: []),
            new DoctorVerification(
                VerificationId,
                "github-copilot-cli",
                "github-copilot-doctor",
                state,
                state == DoctorVerificationState.Active ? 1 : 2,
                ObservedAt,
                ObservedAt.AddMinutes(10),
                state == DoctorVerificationState.Completed ? ObservedAt.AddMinutes(1) : null,
                state == DoctorVerificationState.Cancelled ? ObservedAt.AddMinutes(1) : null,
                acceptedEvidenceRefs));

    private static DoctorResult CompletedResult(DoctorVerification verification, string evidenceRef) =>
        new(
            DoctorSchemaVersions.ResultV1,
            Success: true,
            DoctorResultCode.VerificationCompleted,
            Evaluation: null,
            verification with
            {
                State = DoctorVerificationState.Completed,
                Revision = 2,
                CompletedAt = verification.StartedAt.AddMinutes(1),
                AcceptedEvidenceRefs = [evidenceRef],
            });

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"doctor-ui-{Guid.NewGuid():N}");

        public TempDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.db");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

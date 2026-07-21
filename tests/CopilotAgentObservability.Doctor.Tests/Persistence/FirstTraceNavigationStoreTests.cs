namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class FirstTraceNavigationStoreTests
{
    [Fact]
    public void RecordAndList_RequirePersistedEvidenceAndReturnExactTargetsInCanonicalOrder()
    {
        using var database = new DoctorTestDatabase();
        var doctor = new SqliteDoctorVerificationStore(database.Path, new DoctorTestTimeProvider(DoctorTestData.Now));
        Assert.Equal(DoctorResultCode.VerificationActive, doctor.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(
            doctor.Start("claude-code", "claude-code-otel", TimeSpan.FromMinutes(5)).Verification);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            doctor.ObserveCandidate(DoctorTestData.Candidate(
                verification, "opaque-session", sourceAdapter: "claude-code-otel")).Code);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            doctor.ObserveCandidate(DoctorTestData.Candidate(
                verification, "opaque-trace", evidenceKind: DoctorEvidenceKind.Projection,
                sourceAdapter: "claude-code-otel")).Code);

        var store = new SqliteFirstTraceNavigationStore(database.Path);
        store.Record(verification.VerificationId, "opaque-session", FirstTraceNavigationTargetKind.Session,
            "01890abc-def0-7000-8000-000000000001");
        store.Record(verification.VerificationId, "opaque-trace", FirstTraceNavigationTargetKind.Trace,
            "0123456789abcdef0123456789abcdef");

        Assert.Equal(
            [
                new FirstTraceNavigationTarget("opaque-session", FirstTraceNavigationTargetKind.Session, "01890abc-def0-7000-8000-000000000001"),
                new FirstTraceNavigationTarget("opaque-trace", FirstTraceNavigationTargetKind.Trace, "0123456789abcdef0123456789abcdef"),
            ],
            store.List(verification.VerificationId, ["opaque-session", "opaque-trace"]));

        using var connection = database.Open();
        Assert.Equal(1L, DoctorTestDatabase.Scalar(
            connection,
            "SELECT version FROM schema_version WHERE component='first_trace_navigation';"));
        Assert.Equal(2L, DoctorTestDatabase.Scalar(connection, "SELECT count(*) FROM first_trace_evidence_navigation;"));
    }

    [Fact]
    public void Record_RejectsUnknownEvidenceUnsafeIdentityAndCrossVerificationReference()
    {
        using var database = new DoctorTestDatabase();
        var doctor = new SqliteDoctorVerificationStore(database.Path, new DoctorTestTimeProvider(DoctorTestData.Now));
        Assert.Equal(DoctorResultCode.VerificationActive, doctor.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(
            doctor.Start("github-copilot-cli", "github-copilot-doctor", TimeSpan.FromMinutes(5)).Verification);
        var store = new SqliteFirstTraceNavigationStore(database.Path);

        Assert.Throws<InvalidOperationException>(() => store.Record(
            verification.VerificationId,
            "not-persisted",
            FirstTraceNavigationTargetKind.Trace,
            "0123456789abcdef0123456789abcdef"));
        Assert.Throws<ArgumentException>(() => store.Record(
            verification.VerificationId,
            "safe-ref",
            FirstTraceNavigationTargetKind.Session,
            "C:\\Users\\person\\session"));
    }

    [Fact]
    public void List_DoesNotParseOpaqueReferencesOrReturnUnselectedTargets()
    {
        using var database = new DoctorTestDatabase();
        var doctor = new SqliteDoctorVerificationStore(database.Path, new DoctorTestTimeProvider(DoctorTestData.Now));
        Assert.Equal(DoctorResultCode.VerificationActive, doctor.CreateSchema().Code);
        var verification = Assert.IsType<DoctorVerification>(
            doctor.Start("claude-code", "claude-code-otel", TimeSpan.FromMinutes(5)).Verification);
        var encodedLookingReference = "claude-otel-binding-0123456789abcdef0123456789abcdef-01890abc-def0-7000-8000-000000000001";
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            doctor.ObserveCandidate(DoctorTestData.Candidate(
                verification, encodedLookingReference, sourceAdapter: "claude-code-otel")).Code);

        var store = new SqliteFirstTraceNavigationStore(database.Path);

        Assert.Empty(store.List(verification.VerificationId, [encodedLookingReference]));
    }
}

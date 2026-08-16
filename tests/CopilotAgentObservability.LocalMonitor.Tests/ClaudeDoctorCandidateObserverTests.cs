using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ClaudeDoctorCandidateObserverTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-17T00:00:00Z");

    [Fact]
    public void RunOnce_RawGrantLostAfterMaterialization_PersistsNoEvidence()
    {
        using var temp = new MonitorTempDirectory();
        var time = new FixedTimeProvider(ObservedAt);
        temp.TimeProvider = time;
        var rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSourceCompatibilityStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var doctor = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(temp.DatabasePath, time));
        var verification = Assert.IsType<DoctorVerification>(doctor.Start(
            "claude-code",
            "claude-code-otel",
            ObservedAt.AddMinutes(5)).Verification);
        CommitClaudeRaw(temp.DatabasePath);
        var observer = new ClaudeDoctorCandidateObserver(
            temp.DatabasePath,
            doctor,
            rawStore,
            time,
            () => Execute(
                temp.DatabasePath,
                "DELETE FROM retention_leases WHERE lease_kind='operation';"));

        observer.RunOnce();

        Assert.Equal(
            0L,
            Scalar(
                temp.DatabasePath,
                "SELECT COUNT(*) FROM doctor_verification_evidence WHERE verification_id=$verification_id;",
                verification.VerificationId));
    }

    private static void CommitClaudeRaw(string databasePath)
    {
        const string payload = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"1111111111111111","name":"RAW_PAYLOAD_MARKER"}]}]}]}
            """;
        var inventory = OtlpJsonStructuralWalker.Build(payload, ObservedAt.AddSeconds(1));
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            "claude-code",
            sourceApplicationVersion: null,
            "claude-code-otel",
            "claude-otel-v1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code",
                sourceApplicationVersion: null,
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            ObservedAt.AddSeconds(1));
        var raw = RawOtlpIngestor.CreateRecordFromPayloadJson(
            payload,
            ObservedAt.AddSeconds(1));
        _ = new SqliteIngestionCommitStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation));
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(
        string databasePath,
        string sql,
        string verificationId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$verification_id", verificationId);
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

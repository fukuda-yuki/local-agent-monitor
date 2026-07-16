using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorEvidenceConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task Observe_SimultaneousIdenticalSelections_PersistOneCompleteEvidenceSet()
    {
        using var database = TemporaryDatabase.Create();
        var fixture = CreateExactFixture(database.Path);
        using var start = new Barrier(participantCount: 3);

        var first = Task.Run(() => ObserveAfterBarrier(database.Path, fixture, start));
        var second = Task.Run(() => ObserveAfterBarrier(database.Path, fixture, start));
        start.SignalAndWait();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
        {
            Assert.Equal(DoctorResultCode.VerificationActive, result.ObservationResult.Code);
            Assert.Equal(5, result.ObservedKinds.Count);
            Assert.Equal(5, result.EvidenceRefs.Count);
            Assert.Equal(5, result.EvidenceRefs.Distinct(StringComparer.Ordinal).Count());
        });
        Assert.Equal(results[0].ObservedKinds, results[1].ObservedKinds);
        Assert.Equal(results[0].EvidenceRefs, results[1].EvidenceRefs);
        Assert.Equal(5, CountCandidates(database.Path, fixture.VerificationId));
        Assert.Equal(5, CountDistinctCandidateReferences(database.Path, fixture.VerificationId));
        Assert.Equal(
            ["completeness_content", "exact_session_binding", "ingest", "projection", "raw_persistence"],
            ReadCandidateKinds(database.Path, fixture.VerificationId));
    }

    [Fact]
    public void Observe_ZeroRetryWriteLockBeforeFirstWrite_ReturnsStoreBusyWithZeroRows_ThenExplicitInvocationSucceeds()
    {
        using var database = TemporaryDatabase.Create();
        var fixture = CreateExactFixture(database.Path);
        using var lockConnection = Open(database.Path, defaultTimeoutSeconds: 0);
        using var lockTransaction = lockConnection.BeginTransaction(deferred: false);
        using (var lockCommand = lockConnection.CreateCommand())
        {
            lockCommand.Transaction = lockTransaction;
            lockCommand.CommandText = "UPDATE doctor_verifications SET revision = revision WHERE verification_id = $id;";
            lockCommand.Parameters.AddWithValue("$id", fixture.VerificationId);
            Assert.Equal(1, lockCommand.ExecuteNonQuery());
        }

        var lockedResult = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new FixedTimeProvider(Now),
            Selection(fixture),
            new GitHubCopilotDoctorEvidenceStorePolicy(BusyTimeoutMilliseconds: 0, RetryCount: 0));

        Assert.Equal(DoctorResultCode.DoctorStoreBusy, lockedResult.ObservationResult.Code);
        Assert.Empty(lockedResult.ObservedKinds);
        Assert.Empty(lockedResult.EvidenceRefs);
        AssertFullyUnknownUnattributed(lockedResult);
        var serialized = JsonSerializer.Serialize(lockedResult);
        Assert.DoesNotContain(database.Path, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"rawRecordId\":", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("exact-native", serialized, StringComparison.Ordinal);
        Assert.Equal(0, CountCandidates(database.Path, fixture.VerificationId));

        lockTransaction.Rollback();

        var explicitAttempt = Observe(database.Path, fixture);

        Assert.Equal(DoctorResultCode.VerificationActive, explicitAttempt.ObservationResult.Code);
        Assert.Equal(5, explicitAttempt.EvidenceRefs.Count);
        Assert.Equal(5, CountCandidates(database.Path, fixture.VerificationId));
    }

    [Fact]
    public void Observe_FaultAfterTwoNewCandidateWrites_ExplicitInvocationReusesPersistedRefsAndCompletesSet()
    {
        using var database = TemporaryDatabase.Create();
        var fixture = CreateExactFixture(database.Path);

        var interrupted = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            new FixedTimeProvider(Now),
            Selection(fixture),
            new GitHubCopilotDoctorEvidenceStorePolicy(
                BusyTimeoutMilliseconds: 0,
                RetryCount: 0,
                FaultAfterSuccessfulCandidatePersists: 2));

        Assert.Equal(DoctorResultCode.DoctorStoreBusy, interrupted.ObservationResult.Code);
        Assert.Empty(interrupted.ObservedKinds);
        Assert.Empty(interrupted.EvidenceRefs);
        AssertFullyUnknownUnattributed(interrupted);
        var refsAfterInterruption = ReadCandidateReferences(database.Path, fixture.VerificationId);
        Assert.Equal(2, refsAfterInterruption.Count);
        Assert.Equal(2, refsAfterInterruption.Distinct(StringComparer.Ordinal).Count());

        var explicitAttempt = Observe(database.Path, fixture);

        Assert.Equal(DoctorResultCode.VerificationActive, explicitAttempt.ObservationResult.Code);
        Assert.Equal(5, explicitAttempt.ObservedKinds.Count);
        Assert.Equal(5, explicitAttempt.EvidenceRefs.Count);
        Assert.Equal(5, explicitAttempt.EvidenceRefs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(refsAfterInterruption, reference => Assert.Contains(reference, explicitAttempt.EvidenceRefs));
        Assert.Equal(5, CountCandidates(database.Path, fixture.VerificationId));
        Assert.Equal(5, CountDistinctCandidateReferences(database.Path, fixture.VerificationId));

        var reopened = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, new FixedTimeProvider(Now)));
        var reopenedStatus = reopened.Status(fixture.VerificationId);

        Assert.Equal(DoctorResultCode.VerificationActive, reopenedStatus.Code);
        Assert.Equal(
            explicitAttempt.EvidenceRefs.Order(StringComparer.Ordinal),
            ReadCandidateReferences(database.Path, fixture.VerificationId));
    }

    [Fact]
    public void Observe_ManyUnattributedUniqueSelections_ReclaimsEverySensitiveObservationGateKey()
    {
        for (var index = 0; index < 64; index++)
        {
            using var database = TemporaryDatabase.Create();
            var verificationId = Guid.CreateVersion7().ToString("D");

            var result = GitHubCopilotDoctorEvidenceAdapter.Observe(
                database.Path,
                new FixedTimeProvider(Now),
                new GitHubCopilotDoctorEvidenceSelection(
                    verificationId,
                    "vscode",
                    RawRecordId: index + 1,
                    NativeSession: null));

            Assert.Equal(DoctorResultCode.VerificationNotFound, result.ObservationResult.Code);
            AssertFullyUnknownUnattributed(result);
            Assert.Equal(
                0,
                GitHubCopilotDoctorEvidenceAdapter.ObservationGateCount(database.Path, verificationId));
        }
    }

    private static GitHubCopilotDoctorEvidenceResult ObserveAfterBarrier(
        string databasePath,
        Fixture fixture,
        Barrier start)
    {
        start.SignalAndWait();
        return Observe(databasePath, fixture);
    }

    private static GitHubCopilotDoctorEvidenceResult Observe(string databasePath, Fixture fixture) =>
        GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            new FixedTimeProvider(Now),
            Selection(fixture));

    private static GitHubCopilotDoctorEvidenceSelection Selection(Fixture fixture) =>
        new(
            fixture.VerificationId,
            "vscode",
            fixture.RawRecordId,
            new GitHubCopilotNativeSessionSelection("vscode", "exact-native"));

    private static Fixture CreateExactFixture(string databasePath)
    {
        var doctor = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(databasePath, new FixedTimeProvider(Now)));
        var verification = Assert.IsType<DoctorVerification>(
            doctor.Start("github-copilot-vscode", "github-copilot-doctor", Now.AddMinutes(5)).Verification);
        var traceId = "0123456789abcdef0123456789abcdef";
        var rawRecordId = CommitRaw(databasePath, traceId);
        CompleteProjection(databasePath, rawRecordId);
        WriteSession(databasePath, traceId);
        return new(verification.VerificationId, rawRecordId);
    }

    private static long CommitRaw(string databasePath, string traceId)
    {
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var payload = "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"client.kind\",\"value\":{\"stringValue\":\"vscode-copilot-chat\"}}]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\""
            + traceId
            + "\",\"spanId\":\"span\"}]}]}]}";
        var inventory = OtlpJsonStructuralWalker.Build(payload, Now);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            RawTelemetrySources.RawOtlp,
            sourceApplicationVersion: null,
            RawTelemetrySources.RawOtlp,
            "1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                RawTelemetrySources.RawOtlp,
                sourceApplicationVersion: null,
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            Now);
        var raw = new RawTelemetryRecord(
            Id: null,
            RawTelemetrySources.RawOtlp,
            traceId,
            Now,
            "{\"client.kind\":\"vscode-copilot-chat\"}",
            payload);
        return new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation)).RawRecordId;
    }

    private static void CompleteProjection(string databasePath, long rawRecordId)
    {
        var store = new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var current = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        Assert.True(store.TryBeginProjection(rawRecordId, current.Revision, Now.AddSeconds(1)));
        current = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        Assert.True(store.ApplyProjection(
            rawRecordId,
            RawTelemetrySources.RawOtlp,
            Now,
            new MonitorRecordProjection(null, null, 1, []),
            Now.AddSeconds(2),
            current.Revision));
    }

    private static void WriteSession(string databasePath, string traceId)
    {
        var store = new SqliteSessionStore(databasePath, new FixedTimeProvider(Now));
        store.CreateSchema();
        var session = ObservedSession.Create(
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            repository: null,
            workspace: null,
            Now,
            Now,
            Now,
            SessionRawRetentionState.Expiring);
        var native = new SessionNativeId(
            session.SessionId,
            SessionSourceSurface.VisualStudioCode,
            "exact-native",
            SessionBindingKind.Native,
            Now);
        var run = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = traceId,
            StartedAt = Now,
            EndedAt = Now,
        };
        var @event = ObservedSessionEvent.Create(
            session.SessionId,
            run.RunId,
            "copilot-compatible-hook",
            "event-1",
            "assistant.completed",
            Now,
            SessionContentState.Available) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = traceId,
        };
        store.Write(new SessionWriteBatch(new SessionDetail(session, [native], [run], [@event]), []));
    }

    private static int CountCandidates(string databasePath, string verificationId) =>
        ExecuteScalar(databasePath, "SELECT COUNT(*) FROM doctor_verification_evidence WHERE verification_id = $id;", verificationId);

    private static int CountDistinctCandidateReferences(string databasePath, string verificationId) =>
        ExecuteScalar(databasePath, "SELECT COUNT(DISTINCT evidence_ref) FROM doctor_verification_evidence WHERE verification_id = $id;", verificationId);

    private static IReadOnlyList<string> ReadCandidateKinds(string databasePath, string verificationId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_kind FROM doctor_verification_evidence WHERE verification_id = $id ORDER BY evidence_kind;";
        command.Parameters.AddWithValue("$id", verificationId);
        using var reader = command.ExecuteReader();
        var kinds = new List<string>();
        while (reader.Read())
        {
            kinds.Add(reader.GetString(0));
        }
        return kinds;
    }

    private static IReadOnlyList<string> ReadCandidateReferences(string databasePath, string verificationId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_ref FROM doctor_verification_evidence WHERE verification_id = $id ORDER BY evidence_ref;";
        command.Parameters.AddWithValue("$id", verificationId);
        using var reader = command.ExecuteReader();
        var references = new List<string>();
        while (reader.Read())
        {
            references.Add(reader.GetString(0));
        }
        return references;
    }

    private static int ExecuteScalar(string databasePath, string sql, string verificationId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", verificationId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertFullyUnknownUnattributed(GitHubCopilotDoctorEvidenceResult result)
    {
        Assert.False(result.SessionUnbound);
        Assert.Empty(result.Snapshot.Observations);
        Assert.Null(result.Snapshot.InstallAndSourceVersion);
        Assert.Null(result.Snapshot.ProcessReceiverAndPort);
        Assert.Null(result.Snapshot.SourceEffectiveConfiguration);
        Assert.Null(result.Snapshot.EndpointReachability);
        Assert.Null(result.Snapshot.ProtocolAndSignalCompatibility);
        Assert.Equal(SourceCompatibilityStatus.Unknown, result.Snapshot.SourceVersionAndSchemaDiagnostics!.Compatibility);
        Assert.Equal(SchemaStatus.Unknown, result.Snapshot.SourceVersionAndSchemaDiagnostics.Schema);
        Assert.Equal(LastIngestOutcome.Unknown, result.Snapshot.LastIngest!.Outcome);
        Assert.Equal(RawPersistenceOutcome.Unknown, result.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(ProjectionOutcome.Unknown, result.Snapshot.Projection!.Outcome);
        Assert.Equal(ExactSessionBindingRequirement.Unknown, result.Snapshot.ExactSessionBinding!.Requirement);
        Assert.Equal(ExactSessionBindingOutcome.Unknown, result.Snapshot.ExactSessionBinding.Outcome);
        Assert.Equal(DoctorCompleteness.Unknown, result.Snapshot.CompletenessAndContent!.Completeness);
        Assert.Equal(ContentCaptureStatus.Unknown, result.Snapshot.CompletenessAndContent.ContentCapture);
        Assert.Equal(RawAccessStatus.Unknown, result.Snapshot.CompletenessAndContent.RawAccess);
        Assert.Null(result.Snapshot.RestartOrNewProcess);
    }

    private static SqliteConnection Open(string databasePath, int defaultTimeoutSeconds = 30)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = defaultTimeoutSeconds,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={defaultTimeoutSeconds * 1000};";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed record Fixture(string VerificationId, long RawRecordId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private TemporaryDatabase(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"issue103-concurrency-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}

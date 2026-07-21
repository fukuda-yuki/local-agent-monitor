using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorEvidenceMatrixTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-17T03:00:00Z");
    private const string TraceId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("supported", SourceCompatibilityStatus.Supported, SchemaStatus.Matching)]
    [InlineData("supported_with_unknown_fields", SourceCompatibilityStatus.Supported, SchemaStatus.Matching)]
    [InlineData("unsupported_source_version", SourceCompatibilityStatus.UnsupportedSourceVersion, SchemaStatus.Unknown)]
    [InlineData("schema_drift_detected", SourceCompatibilityStatus.Supported, SchemaStatus.DriftDetected)]
    [InlineData("recognized_record_drop_detected", SourceCompatibilityStatus.Unknown, SchemaStatus.DriftDetected)]
    public void Observe_ProjectsExactCompatibilityWithoutChangingIndependentRuntimeGates(
        string compatibilityState,
        SourceCompatibilityStatus expectedCompatibility,
        SchemaStatus expectedSchema)
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, SessionContentState.Available, SessionRawRetentionState.Expiring, "native-exact");
        Execute(database.Path,
            """
            UPDATE source_schema_observations
            SET compatibility_state = $value,
                reason_code = CASE $value
                    WHEN 'supported_with_unknown_fields' THEN 'unknown_fields_observed'
                    WHEN 'unsupported_source_version' THEN 'unsupported_source_version'
                    WHEN 'schema_drift_detected' THEN 'schema_drift_detected'
                    WHEN 'recognized_record_drop_detected' THEN 'recognized_record_drop_detected'
                    ELSE NULL
                END,
                next_action = CASE $value
                    WHEN 'supported' THEN 'none'
                    WHEN 'supported_with_unknown_fields' THEN 'review_unknown_fields'
                    WHEN 'unsupported_source_version' THEN 'use_compatible_source_or_update_adapter'
                    WHEN 'schema_drift_detected' THEN 'capture_fixture_and_review_mapping'
                    WHEN 'recognized_record_drop_detected' THEN 'restore_mapping_or_update_versioned_golden'
                END
            WHERE raw_record_id = $raw_record_id;
            """,
            rawRecordId, compatibilityState);

        var observed = Observe(database.Path, verification, rawRecordId, "native-exact");

        Assert.Equal(expectedCompatibility, observed.Snapshot.SourceVersionAndSchemaDiagnostics!.Compatibility);
        Assert.Equal(expectedSchema, observed.Snapshot.SourceVersionAndSchemaDiagnostics.Schema);
        Assert.Equal(LastIngestOutcome.Accepted, observed.Snapshot.LastIngest!.Outcome);
        Assert.Equal(RawPersistenceOutcome.Persisted, observed.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(ProjectionOutcome.Completed, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(ExactSessionBindingOutcome.ExactBound, observed.Snapshot.ExactSessionBinding!.Outcome);
        Assert.Equal(5, observed.EvidenceRefs.Count);
    }

    [Fact]
    public void Observe_UnrelatedProductionAdapterFailureIsNotSelectedForExactRawRecord()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        var compatibilityStore = new SqliteSourceCompatibilityStore(
            database.Path,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.RecordAdapterFailure(SourceAdapterFailureDraft.CreateAdapterException(
            Guid.CreateVersion7().ToString("D"),
            "unrelated-failure-batch",
            RawTelemetrySources.RawOtlp,
            sourceApplicationVersion: null,
            RawTelemetrySources.RawOtlp,
            "1",
            SourceCaptureContentState.Available,
            Now));

        var observed = Observe(database.Path, verification, rawRecordId, nativeId: null);

        Assert.Equal([DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection], observed.ObservedKinds);
        Assert.Equal(3, observed.EvidenceRefs.Count);
        Assert.Equal(SourceCompatibilityStatus.Supported, observed.Snapshot.SourceVersionAndSchemaDiagnostics!.Compatibility);
        Assert.Equal(SchemaStatus.Matching, observed.Snapshot.SourceVersionAndSchemaDiagnostics.Schema);
        Assert.Equal(LastIngestOutcome.Accepted, observed.Snapshot.LastIngest!.Outcome);
    }

    [Theory]
    [InlineData(SessionContentState.Available, SessionRawRetentionState.Expiring, ContentCaptureStatus.Enabled, RawAccessStatus.Available, null)]
    [InlineData(SessionContentState.NotCaptured, SessionRawRetentionState.Expiring, ContentCaptureStatus.Disabled, RawAccessStatus.Available, DoctorStateCode.ContentCaptureDisabled)]
    [InlineData(SessionContentState.Unsupported, SessionRawRetentionState.Expiring, ContentCaptureStatus.Unsupported, RawAccessStatus.Available, DoctorStateCode.ContentCaptureDisabled)]
    [InlineData(SessionContentState.Available, SessionRawRetentionState.NotCaptured, ContentCaptureStatus.Enabled, RawAccessStatus.SanitizedOnly, DoctorStateCode.SanitizedOnlyRawUnavailable)]
    public void Complete_ReportsContentAndSanitizedOnlyAsHonestAdvisories(
        SessionContentState contentState,
        SessionRawRetentionState retentionState,
        ContentCaptureStatus expectedCapture,
        RawAccessStatus expectedRawAccess,
        DoctorStateCode? expectedAdvisory)
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, contentState, retentionState, "native-content");
        var observed = Observe(database.Path, verification, rawRecordId, "native-content");

        Assert.Equal(expectedCapture, observed.Snapshot.CompletenessAndContent!.ContentCapture);
        Assert.Equal(expectedRawAccess, observed.Snapshot.CompletenessAndContent.RawAccess);
        var completed = Doctor(database.Path).Complete(
            verification.VerificationId,
            verification.Revision,
            WithReadyStaticFacts(observed.Snapshot),
            observed.EvidenceRefs);

        Assert.Equal(DoctorStateCode.FirstTraceReady, completed.Evaluation!.PrimaryState!.StateCode);
        if (expectedAdvisory is { } advisory)
        {
            Assert.Contains(completed.Evaluation.States, state => state.StateCode == advisory);
        }
        else
        {
            Assert.DoesNotContain(completed.Evaluation.States, state =>
                state.StateCode is DoctorStateCode.ContentCaptureDisabled or DoctorStateCode.SanitizedOnlyRawUnavailable);
        }
    }

    [Theory]
    [InlineData("complete", 5)]
    [InlineData("no_session", 3)]
    [InlineData("no_disposition", 4)]
    [InlineData("no_compatibility", 0)]
    public void Observe_CanonicalGatesRemainIndependent(string scenario, int expectedEvidenceCount)
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, SessionContentState.Available, SessionRawRetentionState.Expiring, "native-gates");
        if (scenario == "no_session")
        {
            Execute(database.Path, "DELETE FROM session_native_ids; DELETE FROM session_events; DELETE FROM session_runs; DELETE FROM sessions;", rawRecordId);
        }
        else if (scenario == "no_disposition")
        {
            Execute(database.Path, "DELETE FROM monitor_projection_dispositions WHERE raw_record_id = $raw_record_id;", rawRecordId);
        }
        else if (scenario == "no_compatibility")
        {
            Execute(database.Path, "DELETE FROM source_schema_observations WHERE raw_record_id = $raw_record_id;", rawRecordId);
        }

        var observed = Observe(database.Path, verification, rawRecordId, "native-gates");

        var expectedKinds = scenario switch
        {
            "complete" => new[]
            {
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.Projection,
                DoctorEvidenceKind.ExactSessionBinding,
                DoctorEvidenceKind.CompletenessContent,
            },
            "no_session" =>
            [
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.Projection,
            ],
            "no_disposition" =>
            [
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.ExactSessionBinding,
                DoctorEvidenceKind.CompletenessContent,
            ],
            _ => [],
        };
        var expectedProjection = scenario switch
        {
            "complete" or "no_session" => ProjectionOutcome.Completed,
            _ => ProjectionOutcome.Unknown,
        };
        var expectedBinding = scenario switch
        {
            "complete" or "no_disposition" => ExactSessionBindingOutcome.ExactBound,
            "no_session" => ExactSessionBindingOutcome.NotApplicable,
            _ => ExactSessionBindingOutcome.Unknown,
        };
        var expectedCompleteness = scenario switch
        {
            "complete" or "no_disposition" => DoctorCompleteness.Full,
            "no_session" => DoctorCompleteness.Unbound,
            _ => DoctorCompleteness.Unknown,
        };

        Assert.Equal(expectedKinds, observed.ObservedKinds);
        Assert.Equal(expectedEvidenceCount, observed.EvidenceRefs.Count);
        Assert.Equal(expectedEvidenceCount, CountCandidates(database.Path, verification.VerificationId));
        Assert.Equal(expectedEvidenceCount == 0 ? LastIngestOutcome.Unknown : LastIngestOutcome.Accepted, observed.Snapshot.LastIngest!.Outcome);
        Assert.Equal(expectedEvidenceCount == 0 ? RawPersistenceOutcome.Unknown : RawPersistenceOutcome.Persisted, observed.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(expectedProjection, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(expectedBinding, observed.Snapshot.ExactSessionBinding!.Outcome);
        Assert.Equal(expectedCompleteness, observed.Snapshot.CompletenessAndContent!.Completeness);
        Assert.Equal(
            scenario == "no_compatibility" ? ExactSessionBindingRequirement.Unknown : ExactSessionBindingRequirement.NotRequired,
            observed.Snapshot.ExactSessionBinding.Requirement);
        Assert.Equal(
            expectedCompleteness is DoctorCompleteness.Full or DoctorCompleteness.Unbound
                ? ContentCaptureStatus.Enabled
                : ContentCaptureStatus.Unknown,
            observed.Snapshot.CompletenessAndContent.ContentCapture);
        Assert.Equal(
            expectedCompleteness is DoctorCompleteness.Full or DoctorCompleteness.Unbound
                ? RawAccessStatus.Available
                : RawAccessStatus.Unknown,
            observed.Snapshot.CompletenessAndContent.RawAccess);
    }

    [Fact]
    public void OutputsAndErrorsNeverExposeRawContentCredentialsPiiOrSensitivePaths()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, SessionContentState.Available, SessionRawRetentionState.Expiring, SensitiveNativeId);
        var observed = Observe(database.Path, verification, rawRecordId, SensitiveNativeId);
        var completed = Doctor(database.Path).Complete(
            verification.VerificationId,
            verification.Revision,
            WithReadyStaticFacts(observed.Snapshot),
            observed.EvidenceRefs);
        var invalidTarget = Assert.Throws<ArgumentException>(() =>
            GitHubCopilotDoctorEvidenceAdapter.Observe(
                database.Path,
                new FixedTimeProvider(Now),
                new(verification.VerificationId, SensitivePath, rawRecordId, NativeSession: null)));
        var repositorySafeOutputs = string.Join('\n',
            observed.EvidenceRefs.Append(DoctorJson.SerializeResult(completed))
                .Append(DoctorHumanProjector.Project(completed))
                .Append(invalidTarget.ToString()));

        foreach (var sensitive in SensitiveValues)
        {
            Assert.DoesNotContain(sensitive, repositorySafeOutputs, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(observed.EvidenceRefs, reference => Assert.Matches("^[a-z0-9_-]{1,128}$", reference));
    }

    [Fact]
    public void RuntimeJsonAndHumanProjectTheSameSourceSpecificResult()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path);
        var rawRecordId = CommitRaw(database.Path, SensitivePayload);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, SessionContentState.NotCaptured, SessionRawRetentionState.NotCaptured, "native-parity");
        var observed = Observe(database.Path, verification, rawRecordId, "native-parity");
        var result = Doctor(database.Path).Complete(
            verification.VerificationId,
            verification.Revision,
            WithReadyStaticFacts(observed.Snapshot),
            observed.EvidenceRefs);

        var application = new ReturningDoctorCliApplication(result);
        using var jsonOutput = new StringWriter();
        using var jsonError = new StringWriter();
        using var humanOutput = new StringWriter();
        using var humanError = new StringWriter();
        var args = new[]
        {
            "verification", "status", "--database", "fixture.db",
            "--verification-id", verification.VerificationId,
        };

        var jsonExit = DoctorCli.Run([.. args, "--json"], jsonOutput, jsonError, application);
        var humanExit = DoctorCli.Run(args, humanOutput, humanError, application);
        var json = jsonOutput.ToString();
        var human = humanOutput.ToString();
        var reparsed = DoctorJson.DeserializeResult(json);

        Assert.Equal(0, jsonExit);
        Assert.Equal(0, humanExit);
        Assert.Equal(string.Empty, jsonError.ToString());
        Assert.Equal(string.Empty, humanError.ToString());
        Assert.Equal(DoctorJson.SerializeResult(result) + Environment.NewLine, json);
        Assert.Equal(DoctorHumanProjector.Project(result) + Environment.NewLine, human);
        Assert.Equal(result.Evaluation!.PrimaryState!.StateCode, reparsed.Evaluation!.PrimaryState!.StateCode);
        Assert.Equal(result.Evaluation.PrimaryState.NextAction, reparsed.Evaluation.PrimaryState.NextAction);
        Assert.Equal(result.Evaluation.PrimaryState.VerificationId, reparsed.Evaluation.PrimaryState.VerificationId);
        Assert.Equal(result.Evaluation.PrimaryState.EvidenceRefs, reparsed.Evaluation.PrimaryState.EvidenceRefs);
        Assert.Contains("Doctor: first_trace_ready", human, StringComparison.Ordinal);
        Assert.Contains("Next action: open_verified_trace_or_session", human, StringComparison.Ordinal);
    }

    private const string SensitiveNativeId = "native-user@example.test-Credential=fixture-token-C:\\Users\\Fixture\\private";
    private const string SensitivePath = "C:\\Users\\Fixture\\private\\secret.json";
    private const string SensitivePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"user@example.test"}},
          {"key":"authorization","value":{"stringValue":"Bearer fixture-token"}}
        ]},"scopeSpans":[{"spans":[{
          "traceId":"0123456789abcdef0123456789abcdef",
          "spanId":"span",
          "prompt":"raw prompt fixture",
          "response":"raw response fixture",
          "toolBody":"tool body fixture",
          "localPath":"C:\\Users\\Fixture\\private\\secret.json"
        }]}]}]}
        """;
    private static readonly string[] SensitiveValues =
    [
        "raw prompt fixture", "raw response fixture", "tool body fixture", "fixture-token",
        "Bearer fixture-token", "user@example.test", SensitiveNativeId, SensitivePath,
    ];

    private static GitHubCopilotDoctorEvidenceResult Observe(
        string databasePath,
        DoctorVerification verification,
        long rawRecordId,
        string? nativeId) =>
        GitHubCopilotDoctorEvidenceAdapter.Observe(
            databasePath,
            new FixedTimeProvider(Now),
            new(
                verification.VerificationId,
                "vscode",
                rawRecordId,
                nativeId is null ? null : new GitHubCopilotNativeSessionSelection("vscode", nativeId)));

    private static DoctorVerification Start(string databasePath) =>
        Assert.IsType<DoctorVerification>(Doctor(databasePath)
            .Start("github-copilot-vscode", "github-copilot-doctor", Now.AddMinutes(5)).Verification);

    private static SqliteDoctorApplicationService Doctor(string databasePath) =>
        SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(databasePath, new FixedTimeProvider(Now)));

    private static long CommitRaw(string databasePath, string payload)
    {
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var inventory = OtlpJsonStructuralWalker.Build(payload, Now);
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create(RawTelemetrySources.RawOtlp, "fixture-v1", inventory.SchemaFingerprint)],
            [], []);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"), RawTelemetrySources.RawOtlp, null,
            RawTelemetrySources.RawOtlp, "1", inventory,
            SourceCompatibilityEvaluator.Assess(RawTelemetrySources.RawOtlp, null, inventory, 1, registry),
            SourceCaptureContentState.Available, Now);
        var raw = new RawTelemetryRecord(
            null, RawTelemetrySources.RawOtlp, TraceId, Now,
            "{\"client.kind\":\"vscode-copilot-chat\"}", payload);
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
            rawRecordId, RawTelemetrySources.RawOtlp, Now,
            new MonitorRecordProjection(null, null, 1, []), Now.AddSeconds(2), current.Revision));
    }

    private static void WriteSession(
        string databasePath,
        SessionContentState contentState,
        SessionRawRetentionState retentionState,
        string nativeId)
    {
        var store = new SqliteSessionStore(databasePath, new FixedTimeProvider(Now));
        store.CreateSchema();
        var session = ObservedSession.Create(
            ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            Now, Now, Now, retentionState);
        var native = new SessionNativeId(
            session.SessionId, SessionSourceSurface.VisualStudioCode, nativeId, SessionBindingKind.Native, Now);
        var run = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = TraceId,
            StartedAt = Now,
            EndedAt = Now,
        };
        var @event = ObservedSessionEvent.Create(
            session.SessionId, run.RunId, "copilot-compatible-hook", "event-1",
            "assistant.completed", Now, contentState) with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            TraceId = TraceId,
        };
        store.Write(new SessionWriteBatch(new SessionDetail(session, [native], [run], [@event]), []));
    }

    private static DoctorFactSnapshot WithReadyStaticFacts(DoctorFactSnapshot snapshot) => snapshot with
    {
        InstallAndSourceVersion = new(MonitorInstallStatus.Installed, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
        ProcessReceiverAndPort = new(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
        SourceEffectiveConfiguration = new(EndpointAlignmentStatus.Match),
        EndpointReachability = new(ReachabilityStatus.Reachable),
        ProtocolAndSignalCompatibility = new(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled),
        RestartOrNewProcess = new(RestartRequirement.NotRequired),
    };

    private static void Execute(string databasePath, string sql, long rawRecordId, string? value = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static int CountCandidates(string databasePath, string verificationId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM doctor_verification_evidence WHERE verification_id = $verification_id;";
        command.Parameters.AddWithValue("$verification_id", verificationId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ReturningDoctorCliApplication(DoctorResult result) : IDoctorCliApplication
    {
        public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => result;

        public DoctorResult Start(
            string databasePath,
            string sourceSurface,
            string? sourceAdapter,
            DateTimeOffset expiresAt) => result;

        public DoctorResult Status(string databasePath, string verificationId) => result;

        public DoctorResult Complete(
            string databasePath,
            string verificationId,
            int expectedRevision,
            DoctorCompletionInput input) => result;

        public DoctorResult Cancel(string databasePath, string verificationId, int expectedRevision) => result;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TempDatabase(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDatabase Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"issue103-evidence-matrix-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

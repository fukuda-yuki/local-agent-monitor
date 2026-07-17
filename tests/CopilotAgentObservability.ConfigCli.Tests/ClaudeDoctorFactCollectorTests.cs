using System.Text;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.Doctor;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeDoctorFactCollectorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 17, 8, 9, 10, TimeSpan.Zero);
    private const string Origin = "http://127.0.0.1:4320";
    private const string ExpectedEndpoint = Origin + "/v1/traces";
    private const string InvocationDirectory = "C:\\claude-project";
    private const string ManagedSettingsPath = "C:\\claude-managed.json";
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string SpanId = "0123456789abcdef";

    [Theory]
    [MemberData(nameof(LivenessCases))]
    public void Collect_ClassifiesLiveness(
        SetupHttpProbeObservation live,
        int expected)
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true);
        var probe = new TestHttpProbe(live);

        var inputs = Collect(platform, database.Path, probe);

        Assert.Equal((ClaudeLivenessProbeClassification)expected, inputs.LivenessProbe);
        Assert.Contains(probe.Requests, request =>
            request.Path == "/health/live" &&
            request.TotalBudgetMilliseconds == 500 &&
            request.MaximumBodyBytes == 4096);
    }

    [Fact]
    public void Collect_ClassifiesProbeUnavailableWhenProbeCannotRun()
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true);
        var probe = new TestHttpProbe(SetupHttpProbeObservation.TransportFailure, throwForLive: true);

        var inputs = Collect(platform, database.Path, probe);

        Assert.Equal(ClaudeLivenessProbeClassification.ProbeUnavailable, inputs.LivenessProbe);
    }

    [Fact]
    public void Collect_ReportsReadinessFailure()
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true);
        var probe = new TestHttpProbe(LiveResponse, SetupHttpProbeObservation.TimedOut);

        var inputs = Collect(platform, database.Path, probe);

        Assert.False(inputs.ReadinessProbeSucceeded);
        Assert.Contains(probe.Requests, request => request.Path == "/health/ready");
    }

    [Fact]
    public void Collect_ReportsMissingDatabaseAndUnsupportedVersion()
    {
        var platform = CreatePlatform(database: null, supportedVersion: false);
        var probe = new TestHttpProbe(SetupHttpProbeObservation.Refused);

        var inputs = Collect(platform, "C:\\missing\\monitor.db", probe);

        Assert.False(inputs.MonitorDatabaseFileExists);
        Assert.Equal(ClaudeSourceVersionClassification.Unsupported, inputs.SourceVersion);
    }

    [Fact]
    public void Collect_ReportsUndetectableVersion()
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: null);

        var inputs = Collect(platform, database.Path, new TestHttpProbe(SetupHttpProbeObservation.Refused));

        Assert.Equal(ClaudeSourceVersionClassification.Undetectable, inputs.SourceVersion);
    }

    [Fact]
    public void Collect_UsesSetupEndpointDefaultAndCanonicalization()
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true, versionObservations: 2);
        var probe = new TestHttpProbe(LiveResponse);
        var collector = new ClaudeDoctorFactCollector(
            platform,
            probe,
            platform.Clock,
            InvocationDirectory,
            ManagedSettingsPath);

        var defaultInputs = collector.Collect(database.Path, null);
        var normalizedInputs = collector.Collect(database.Path, "http://localhost:4320/");

        Assert.Equal(Origin, defaultInputs.CanonicalMonitorOrigin);
        Assert.Equal("http://localhost:4320", normalizedInputs.CanonicalMonitorOrigin);
        Assert.All(probe.Requests, request => Assert.Equal(500, request.TotalBudgetMilliseconds));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("managed")]
    [InlineData("settings")]
    [InlineData("absent")]
    [InlineData("conflict")]
    public void Collect_ResolvesOwnedValuesBySetupPrecedence(string source)
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true, planningOs: SetupPlanningOs.Windows);
        switch (source)
        {
            case "process":
                platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", ExpectedEndpoint);
                break;
            case "managed":
                platform.SeedFile(ManagedSettingsPath, Settings(("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", ExpectedEndpoint)));
                break;
            case "settings":
                platform.SeedFile(
                    Path.Combine(InvocationDirectory, ".claude", "settings.local.json"),
                    Settings(("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", ExpectedEndpoint)));
                break;
            case "conflict":
                platform.SeedFile(
                    Path.Combine(InvocationDirectory, ".claude", "settings.local.json"),
                    Encoding.UTF8.GetBytes("{\"env\":{\"OTEL_EXPORTER_OTLP_TRACES_ENDPOINT\":\"" +
                        ExpectedEndpoint + "\",\"OTEL_EXPORTER_OTLP_TRACES_ENDPOINT\":\"http://127.0.0.1:4318/v1/traces\"}}"));
                break;
        }

        var inputs = Collect(platform, database.Path, new TestHttpProbe(SetupHttpProbeObservation.Refused));

        var expected = source switch
        {
            "process" or "managed" or "settings" => ClaudeEndpointValueClassification.Match,
            "conflict" => ClaudeEndpointValueClassification.Conflict,
            _ => ClaudeEndpointValueClassification.Absent,
        };
        Assert.Equal(expected, inputs.Endpoint);
    }

    [Fact]
    public void Collect_ReportsDriftRowsAndRuntimeStateWithoutWriting()
    {
        using var database = new TestDatabase();
        database.InsertSourceObservation(
            rawRecordId: null,
            state: "schema_drift_detected",
            observedAt: Now,
            reasonCode: "schema_drift_detected",
            nextAction: "capture_fixture_and_review_mapping",
            captureContentState: "available");
        database.CreateRuntimeState(sanitizedOnly: true);
        var platform = CreatePlatform(database, supportedVersion: true);
        var before = File.ReadAllBytes(database.Path);
        var operationsBefore = platform.Operations.Count;
        var paths = new SetupRuntimePaths(platform);

        var inputs = Collect(platform, database.Path, new TestHttpProbe(LiveResponse));

        Assert.Equal(ClaudeSourceCompatibilityClassification.Drift, inputs.SourceCompatibility);
        Assert.Equal(ClaudeRuntimeRawAccessClassification.SanitizedOnly, inputs.RuntimeRawAccess);
        Assert.Equal(before, File.ReadAllBytes(database.Path));
        Assert.False(platform.FileSystem.FileExists(paths.OwnershipLedger));
        AssertNoCollectorWrites(platform, operationsBefore);
    }

    [Fact]
    public void Collect_ReportsRuntimeRowAbsent()
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true);

        var inputs = Collect(platform, database.Path, new TestHttpProbe(LiveResponse));

        Assert.Equal(ClaudeRuntimeRawAccessClassification.Absent, inputs.RuntimeRawAccess);
    }

    [Fact]
    public void Collect_ReportsEveryPersistedCandidateKind()
    {
        using var database = new TestDatabase();
        var verification = database.StartVerification();
        database.InsertAcceptedRecord(Now.AddMinutes(1), ValidPayload());
        database.InsertCandidate(verification, "ingest", $"claude-otel-ingest-{TraceId}-{SpanId}");
        database.InsertCandidate(verification, "raw_persistence", $"claude-otel-raw-{TraceId}-{SpanId}");
        database.InsertCandidate(verification, "projection", $"claude-otel-projection-{TraceId}-{SpanId}");
        database.InsertCandidate(
            verification,
            "exact_session_binding",
            $"claude-otel-binding-{TraceId}-00000000-0000-7000-8000-000000000001");
        database.InsertSession("00000000-0000-7000-8000-000000000001", "full");
        var platform = CreatePlatform(database, supportedVersion: true);

        var inputs = Collect(platform, database.Path, new TestHttpProbe(LiveResponse), verification);
        var window = Assert.IsType<ClaudeDoctorVerificationWindow>(inputs.VerificationWindow);

        Assert.True(window.AcceptedIngestExists);
        Assert.False(window.RejectedIngestExists);
        Assert.True(window.RawPersistenceCandidateExists);
        Assert.True(window.ProjectionCandidateExists);
        Assert.Equal(ClaudeProjectionEvidence.NotStarted, window.ProjectionEvidence);
        Assert.True(window.ExactSessionBindingCandidateExists);
        Assert.Equal(ClaudeBoundSessionCompleteness.Full, window.BoundSessionCompleteness);
        Assert.Equal(ClaudeAgreedContentState.Available, window.AgreedContentState);
    }

    [Fact]
    public void Collect_ReportsRejectedIngestAndProjectionFailure()
    {
        using var database = new TestDatabase();
        var verification = database.StartVerification();
        database.InsertRejectedObservation(Now.AddMinutes(1));
        database.InsertAcceptedRecord(Now.AddMinutes(2), "not-json");
        database.InsertCandidate(verification, "raw_persistence", $"claude-otel-raw-{TraceId}-{SpanId}");
        var platform = CreatePlatform(database, supportedVersion: true);

        var inputs = Collect(platform, database.Path, new TestHttpProbe(LiveResponse), verification);
        var window = Assert.IsType<ClaudeDoctorVerificationWindow>(inputs.VerificationWindow);

        Assert.True(window.AcceptedIngestExists);
        Assert.True(window.RejectedIngestExists);
        Assert.True(window.RawPersistenceCandidateExists);
        Assert.False(window.ProjectionCandidateExists);
        Assert.Equal(ClaudeProjectionEvidence.Failed, window.ProjectionEvidence);
    }

    [Fact]
    public void Collect_ReportsPendingProjectionAndNoWindowUsesPreTraceShape()
    {
        using var database = new TestDatabase();
        var verification = database.StartVerification();
        var rawId = database.InsertAcceptedRecord(Now.AddMinutes(1), ValidPayload());
        database.InsertCandidate(verification, "raw_persistence", $"claude-otel-raw-{TraceId}-{SpanId}");
        database.InsertPendingIngestion(rawId, Now.AddMinutes(1));
        var platform = CreatePlatform(database, supportedVersion: true);

        var withWindow = Collect(platform, database.Path, new TestHttpProbe(LiveResponse), verification);
        var withoutWindow = Collect(platform, database.Path, new TestHttpProbe(LiveResponse));

        var window = Assert.IsType<ClaudeDoctorVerificationWindow>(withWindow.VerificationWindow);
        Assert.Equal(ClaudeProjectionEvidence.Pending, window.ProjectionEvidence);
        Assert.Null(withoutWindow.VerificationWindow);
    }

    [Theory]
    [InlineData("no-change", "NoAppliedChangeSet")]
    [InlineData("unreadable", "Unreadable")]
    [InlineData("applied-without-ingest", "AwaitingAcceptedIngest")]
    [InlineData("applied-with-ingest", "AcceptedIngestAfterApply")]
    public void Collect_ClassifiesSetupLedgerReadOnly(
        string ledgerCase,
        string expected)
    {
        using var database = new TestDatabase();
        var platform = CreatePlatform(database, supportedVersion: true);
        var paths = new SetupRuntimePaths(platform);
        if (ledgerCase == "unreadable")
        {
            platform.SeedFile(paths.OwnershipLedger, [1, 2, 3]);
        }
        else if (ledgerCase.StartsWith("applied", StringComparison.Ordinal))
        {
            platform.SeedFile(paths.OwnershipLedger, ClaudeAppliedLedger());
            if (ledgerCase == "applied-with-ingest")
            {
                database.InsertAcceptedRecord(Now, ValidPayload());
            }
        }

        var inputs = Collect(platform, database.Path, new TestHttpProbe(SetupHttpProbeObservation.Refused));

        Assert.Equal(Enum.Parse<ClaudeSetupLedgerClassification>(expected), inputs.SetupLedger);
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal) ||
            operation.StartsWith("file.lock", StringComparison.Ordinal) ||
            operation.StartsWith("directory.create", StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_IsDeterministicAndMapperSnapshotIsDeterministic()
    {
        using var database = new TestDatabase();
        database.CreateRuntimeState(sanitizedOnly: false);
        var platform = CreatePlatform(database, supportedVersion: true, versionObservations: 2);
        var probe = new TestHttpProbe(LiveResponse);
        var collector = new ClaudeDoctorFactCollector(
            platform,
            probe,
            platform.Clock,
            InvocationDirectory,
            ManagedSettingsPath);

        var first = collector.Collect(database.Path, Origin);
        var second = collector.Collect(database.Path, Origin);
        var snapshot1 = ClaudeDoctorFactMapper.Map(first, Now, null);
        var snapshot2 = ClaudeDoctorFactMapper.Map(second, Now, null);

        Assert.Equal(first, second);
        Assert.Equal(snapshot1, snapshot2);
    }

    public static IEnumerable<object[]> LivenessCases()
    {
        yield return [LiveResponse, (int)ClaudeLivenessProbeClassification.MonitorLive];
        yield return [SetupHttpProbeObservation.Refused, (int)ClaudeLivenessProbeClassification.PositiveNoListener];
        yield return [new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            null,
            Encoding.UTF8.GetBytes("{\"status\":\"live\",\"extra\":true}"),
            true), (int)ClaudeLivenessProbeClassification.OtherForeign];
        yield return [SetupHttpProbeObservation.TimedOut, (int)ClaudeLivenessProbeClassification.OtherForeign];
    }

    private static readonly SetupHttpProbeObservation LiveResponse = new(
        SetupHttpProbeOutcome.Response,
        200,
        null,
        Encoding.UTF8.GetBytes("{\"status\":\"live\"}"),
        true);

    private static readonly SetupHttpProbeObservation ReadyResponse = new(
        SetupHttpProbeOutcome.Response,
        200,
        null,
        Encoding.UTF8.GetBytes(
            "{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
        true);

    private static SetupTestPlatform CreatePlatform(
        TestDatabase? database,
        bool? supportedVersion,
        SetupPlanningOs planningOs = SetupPlanningOs.Windows,
        int versionObservations = 1)
    {
        var platform = planningOs == SetupPlanningOs.Windows
            ? new SetupTestPlatform(Now)
            : new SetupTestPlatform(
                Now,
                "/home/setup-test/.local/share",
                SetupPathStyle.Unix,
                planningOs,
                "/home/setup-test/.config",
                "/home/setup-test");
        if (database is not null)
        {
            platform.SeedFile(database.Path, [0]);
        }

        if (supportedVersion is true)
        {
            for (var index = 0; index < versionObservations; index++)
            {
                platform.ScriptProcess(
                    "claude",
                    ["--version"],
                    new(SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
            }
        }
        else if (supportedVersion is false)
        {
            platform.ScriptProcess(
                "claude",
                ["--version"],
                new(SetupProcessOutcome.Completed, 0, "2.1.206 (Claude Code)"));
        }

        return platform;
    }

    private static ClaudeDoctorFactInputs Collect(
        SetupTestPlatform platform,
        string databasePath,
        TestHttpProbe probe,
        DoctorVerification? verification = null) =>
        new ClaudeDoctorFactCollector(
            platform,
            probe,
            platform.Clock,
            InvocationDirectory,
            ManagedSettingsPath).Collect(databasePath, Origin, verification);

    private static byte[] Settings(params (string Key, string Value)[] values) =>
        Encoding.UTF8.GetBytes(
            "{\"env\":{" + string.Join(',', values.Select(value =>
                JsonString(value.Key) + ":" + JsonString(value.Value))) + "}}");

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static string ValidPayload() =>
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"" +
        TraceId + "\",\"spanId\":\"" + SpanId + "\"}]}]}]}";

    private static byte[] ClaudeAppliedLedger()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Setup",
            "v1",
            "ownership-ledger.v1.json");
        return Encoding.UTF8.GetBytes(
            File.ReadAllText(fixturePath).Replace("\"github-copilot\"", "\"claude-code\"", StringComparison.Ordinal));
    }

    private static void AssertNoCollectorWrites(SetupTestPlatform platform, int operationsBefore)
    {
        Assert.DoesNotContain(platform.Operations.Skip(operationsBefore), operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.delete", StringComparison.Ordinal) ||
            operation.StartsWith("file.lock", StringComparison.Ordinal) ||
            operation.StartsWith("directory.create", StringComparison.Ordinal));
    }

    private sealed class TestHttpProbe : ISetupHttpProbe
    {
        private readonly SetupHttpProbeObservation live;
        private readonly SetupHttpProbeObservation ready;
        private readonly bool throwForLive;

        public TestHttpProbe(
            SetupHttpProbeObservation live,
            SetupHttpProbeObservation? ready = null,
            bool throwForLive = false)
        {
            this.live = live;
            this.ready = ready ?? ReadyResponse;
            this.throwForLive = throwForLive;
        }

        public List<Request> Requests { get; } = [];

        public SetupHttpProbeObservation Get(
            string origin,
            string path,
            int totalBudgetMilliseconds,
            int maxBodyBytes)
        {
            Requests.Add(new(origin, path, totalBudgetMilliseconds, maxBodyBytes));
            if (path == "/health/live")
            {
                if (throwForLive)
                {
                    throw new IOException("synthetic probe failure");
                }

                return live;
            }

            return ready;
        }

        public sealed record Request(
            string Origin,
            string Path,
            int TotalBudgetMilliseconds,
            int MaximumBodyBytes);
    }

    private sealed class TestDatabase : IDisposable
    {
        private static int nextDirectory;

        public TestDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cao-claude-facts-" + Interlocked.Increment(ref nextDirectory));
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "monitor.sqlite");
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            Assert.Equal(
                DoctorResultCode.VerificationActive,
                new SqliteDoctorVerificationStore(
                    Path,
                    TimeProvider.System,
                    5_000,
                    checkpoint: null,
                    connectionFactory: null).CreateSchema().Code);
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public DoctorVerification StartVerification()
        {
            var store = new SqliteDoctorVerificationStore(
                Path,
                new FixedTimeProvider(Now),
                5_000,
                checkpoint: null,
                connectionFactory: null);
            var result = store.Start("claude-code", "claude-code-otel", Now.AddMinutes(5));
            return Assert.IsType<DoctorVerification>(result.Verification);
        }

        public long InsertAcceptedRecord(DateTimeOffset receivedAt, string payload)
        {
            var id = new RawTelemetryStore(Path).Insert(new(
                null,
                "raw-otlp",
                TraceId,
                receivedAt,
                null,
                payload));
            InsertSourceObservation(id, "supported", receivedAt, null, "none", "available");
            return id;
        }

        public void InsertRejectedObservation(DateTimeOffset observedAt) =>
            InsertSourceObservation(null, "adapter_failure", observedAt, "adapter_parse_failure", "validate_payload_and_protocol", null);

        public void InsertSourceObservation(
            long? rawRecordId,
            string state,
            DateTimeOffset observedAt,
            string? reasonCode,
            string nextAction,
            string? captureContentState)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO source_schema_observations (observation_id,raw_record_id,ingest_batch_id," +
                "source_surface,source_application_version,source_adapter,adapter_version,schema_fingerprint," +
                "inventory_hash,compatibility_state,reason_code,next_action,capture_content_state," +
                "unknown_span_count,unknown_event_count,unknown_attribute_count,overflow_distinct_count," +
                "overflow_occurrence_count,observed_at) VALUES ($id,$raw,$batch,'claude-code',NULL," +
                "'claude-code-otel',NULL,$fingerprint,NULL,$state,$reason,$action,$capture,0,0,0,0,0,$observed);";
            command.Parameters.AddWithValue("$id", "observation-" + Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$raw", rawRecordId is null ? DBNull.Value : rawRecordId);
            command.Parameters.AddWithValue("$batch", rawRecordId is null ? "batch-" + Guid.NewGuid().ToString("N") : DBNull.Value);
            command.Parameters.AddWithValue(
                "$fingerprint",
                state == "adapter_failure" ? DBNull.Value : "fingerprint");
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$reason", reasonCode is null ? DBNull.Value : reasonCode);
            command.Parameters.AddWithValue("$action", nextAction);
            command.Parameters.AddWithValue("$capture", captureContentState is null ? DBNull.Value : captureContentState);
            command.Parameters.AddWithValue("$observed", Timestamp(observedAt));
            command.ExecuteNonQuery();
        }

        public void InsertCandidate(DoctorVerification verification, string kind, string reference)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO doctor_verification_evidence (candidate_id,verification_id,source_surface," +
                "source_adapter,evidence_class,evidence_kind,evidence_ref,observed_at,expires_at,accepted," +
                "accepted_ordinal) VALUES ($candidate,$verification,'claude-code','claude-code-otel'," +
                "'real_source',$kind,$reference,$observed,$expires,0,NULL);";
            command.Parameters.AddWithValue("$candidate", CandidateId());
            command.Parameters.AddWithValue("$verification", verification.VerificationId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$reference", reference);
            command.Parameters.AddWithValue("$observed", DoctorTimestamp(verification.StartedAt.AddMinutes(1)));
            command.Parameters.AddWithValue("$expires", DoctorTimestamp(verification.ExpiresAt));
            command.ExecuteNonQuery();
        }

        public void InsertSession(string sessionId, string completeness)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS sessions (session_id TEXT PRIMARY KEY, completeness TEXT NOT NULL);" +
                "INSERT INTO sessions(session_id,completeness) VALUES($id,$completeness);";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$completeness", completeness);
            command.ExecuteNonQuery();
        }

        public void CreateRuntimeState(bool sanitizedOnly) =>
            new SqliteMonitorRuntimeStateStore(Path).Upsert(
                sanitizedOnly ? MonitorRawAccessMode.SanitizedOnly : MonitorRawAccessMode.Available,
                Now);

        public long InsertPendingIngestion(long rawRecordId, DateTimeOffset receivedAt)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO monitor_ingestions(raw_record_id,received_at,source,trace_id,client_kind," +
                "span_count,projected_at,span_projected_at) VALUES($raw,$received,'raw-otlp',$trace, NULL,1,$projected,NULL);";
            command.Parameters.AddWithValue("$raw", rawRecordId);
            command.Parameters.AddWithValue("$received", Timestamp(receivedAt));
            command.Parameters.AddWithValue("$trace", TraceId);
            command.Parameters.AddWithValue("$projected", Timestamp(receivedAt));
            command.ExecuteNonQuery();
            return rawRecordId;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static string CandidateId() =>
            $"00000000-0000-7000-8000-{Interlocked.Increment(ref candidateCounter):D12}";

        private static int candidateCounter;

        private static string Timestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        private static string DoctorTimestamp(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

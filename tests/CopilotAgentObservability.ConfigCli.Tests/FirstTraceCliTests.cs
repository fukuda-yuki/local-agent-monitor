using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class FirstTraceCliTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Begin_EmbedsDoctorResultByteIdenticallyAndUsesTenMinuteDefault()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var orchestrator = CreateOrchestrator(adapter);

        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var doctorJson = document.RootElement.GetProperty("doctor").GetRawText();
        var doctor = DoctorJson.DeserializeResult(doctorJson);
        Assert.Equal(DoctorJson.SerializeResult(doctor), doctorJson);
        var verification = doctor.Verification!;
        Assert.Equal(TimeSpan.FromMinutes(10), verification.ExpiresAt - verification.StartedAt);
        Assert.Equal(FirstTraceCodes.VerificationStarted, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Begin_WhenDoctorEvaluationBlocks_ReturnsGuidedBlockedEnvelope()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(monitorInstall: MonitorInstallStatus.NotInstalled);

        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            CreateOrchestrator(adapter));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(FirstTraceCodes.Blocked + Environment.NewLine, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(FirstTraceCodes.Blocked, document.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            "setup plan --adapter test-adapter --target cli",
            document.RootElement.GetProperty("guidance").EnumerateArray().SelectMany(value => new[]
            {
                value.GetProperty("text").GetString() ?? string.Empty,
                value.GetProperty("command").GetString() ?? string.Empty,
            }));
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public void InvalidAndUnknownArgumentsReturnInvalidArgumentsWithCanonicalFailureOutput(string[] args)
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();

        var result = Run(args.Select(value => value.Replace("{db}", database.Path, StringComparison.Ordinal)).ToArray(), CreateOrchestrator(adapter));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
        Assert.Equal(FirstTraceCodes.InvalidArguments, JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    public static TheoryData<string[]> InvalidArgumentCases()
    {
        var cases = new TheoryData<string[]>();
        cases.Add(["begin", "--database", "{db}", "--json"]);
        cases.Add(["begin", "--database", "{db}", "--adapter", "test-adapter", "--unknown", "value", "--json"]);
        cases.Add(["begin", "--database", "{db}", "--adapter", "test-adapter", "--adapter", "test-adapter", "--json"]);
        cases.Add(["begin", "--database", "{db}", "--adapter", "test-adapter", "--endpoint", "not-an-endpoint", "--json"]);
        cases.Add(["status", "--database", "{db}", "--verification-id", "00000000-0000-7000-8000-000000000001", "--endpoint", "not-an-endpoint", "--json"]);
        cases.Add(["complete", "--database", "{db}", "--verification-id", "00000000-0000-7000-8000-000000000001", "--json"]);
        cases.Add(["complete", "--database", "{db}", "--verification-id", "00000000-0000-7000-8000-000000000001", "--expected-revision", "1", "--evidence", "bad/ref", "--json"]);
        cases.Add(["cancel", "--database", "{db}", "--verification-id", "00000000-0000-7000-8000-000000000001", "--expected-revision", "1", "--unknown", "value", "--json"]);
        return cases;
    }

    [Fact]
    public void Begin_WhenActiveVerificationsExist_ReturnsSmallestStartedAndId()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteDoctorVerificationStore(database.Path, new FixedTimeProvider(Now));
        Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
        var first = Assert.IsType<DoctorVerification>(store.Start("test-source", "test-otel", TimeSpan.FromMinutes(5)).Verification);
        var second = Assert.IsType<DoctorVerification>(store.Start("test-source", "test-otel", TimeSpan.FromMinutes(5)).Verification);
        var adapter = new TestFirstTraceAdapter("test-adapter", "test-source", "test-otel");

        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            CreateOrchestrator(adapter));

        Assert.Equal(3, result.ExitCode);
        var envelope = JsonDocument.Parse(result.Output).RootElement;
        Assert.Equal(FirstTraceCodes.ActiveVerificationExists, envelope.GetProperty("code").GetString());
        var expected = new[] { first, second }
            .OrderBy(value => value.StartedAt)
            .ThenBy(value => value.VerificationId, StringComparer.Ordinal)
            .First();
        Assert.Equal(expected.VerificationId, envelope.GetProperty("verification_id").GetString());
        Assert.NotEqual(first.VerificationId, second.VerificationId);
    }

    [Fact]
    public void Status_ReturnsCandidatesAndStatelessEvaluationPreview()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(readyOnVerification: true);
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        InsertReadyCandidates(database.Path, verification);

        var result = Run(
            ["status", "--database", database.Path, "--verification-id", verification.VerificationId, "--json"],
            orchestrator);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(FirstTraceCodes.StatusReported, document.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("candidates").GetArrayLength());
        var preview = document.RootElement.GetProperty("evaluation_preview");
        Assert.Equal("evaluation_completed", preview.GetProperty("code").GetString());
        Assert.Equal("first_trace_ready", preview.GetProperty("evaluation").GetProperty("primary_state").GetProperty("state_code").GetString());
    }

    [Fact]
    public void Complete_WithOneCandidateChainAutoSelectsAndCompletes()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(readyOnVerification: true);
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        InsertReadyCandidates(database.Path, verification);

        var result = Run(
            ["complete", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
            orchestrator);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FirstTraceCodes.Completed, JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Complete_WithExplicitEvidencePassesItThroughAndReportsNotReady()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(readyOnVerification: false);
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        var candidate = InsertCandidate(database.Path, verification, "test-accepted");

        var result = Run(
            ["complete", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--evidence", candidate.EvidenceRef, "--json"],
            orchestrator);

        Assert.Equal(3, result.ExitCode);
        var envelope = JsonDocument.Parse(result.Output).RootElement;
        Assert.Equal(FirstTraceCodes.NotReady, envelope.GetProperty("code").GetString());
        Assert.Equal("evaluation_completed", envelope.GetProperty("doctor").GetProperty("code").GetString());
    }

    [Fact]
    public void Complete_WhenEvidenceChainsAreAmbiguousRequiresExplicitSelection()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(
            readyOnVerification: true,
            requireExplicitSelection: true);
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        InsertCandidate(database.Path, verification, "test-binding");

        var result = Run(
            ["complete", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
            orchestrator);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(FirstTraceCodes.ExplicitEvidenceSelectionRequired + Environment.NewLine, result.Error);
        Assert.Equal(
            FirstTraceCodes.ExplicitEvidenceSelectionRequired,
            JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Complete_WhenDoctorReportsStaleRevisionUsesDoctorExitMapping()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;

        var result = Run(
            ["complete", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "2", "--evidence", "test-ref", "--json"],
            orchestrator);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(FirstTraceCodes.DoctorFailed + Environment.NewLine, result.Error);
        Assert.Equal(
            "verification_stale",
            JsonDocument.Parse(result.Output).RootElement.GetProperty("doctor").GetProperty("code").GetString());
    }

    [Fact]
    public void Cancel_PassesThroughDoctorCancellation()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;

        var result = Run(
            ["cancel", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
            orchestrator);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FirstTraceCodes.Cancelled, JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    private static FirstTraceOrchestrator CreateOrchestrator(TestFirstTraceAdapter adapter) =>
        new([adapter], new FixedTimeProvider(Now));

    private static CommandResult Run(string[] args, FirstTraceOrchestrator orchestrator)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = FirstTraceCli.Run(args, output, error, orchestrator);
        return new(exitCode, output.ToString(), error.ToString());
    }

    private static DoctorEvidenceCandidate InsertCandidate(
        string path,
        DoctorVerification verification,
        string evidenceRef,
        DoctorEvidenceKind kind = DoctorEvidenceKind.Ingest)
    {
        var candidate = new DoctorEvidenceCandidate(
            Guid.CreateVersion7(Now).ToString("D"),
            verification.VerificationId,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            DoctorEvidenceClass.RealSource,
            kind,
            evidenceRef,
            Now,
            verification.ExpiresAt);
        Assert.Equal(DoctorResultCode.VerificationActive,
            new SqliteDoctorVerificationStore(path, new FixedTimeProvider(Now)).ObserveCandidate(candidate).Code);
        return candidate;
    }

    private static void InsertReadyCandidates(string path, DoctorVerification verification)
    {
        foreach (var (reference, kind) in new[]
        {
            ("test-ingest", DoctorEvidenceKind.Ingest),
            ("test-raw", DoctorEvidenceKind.RawPersistence),
            ("test-projection", DoctorEvidenceKind.Projection),
            ("test-binding", DoctorEvidenceKind.ExactSessionBinding),
            ("test-completeness", DoctorEvidenceKind.CompletenessContent),
        })
        {
            var store = new SqliteDoctorVerificationStore(path, new FixedTimeProvider(Now));
            var candidate = new DoctorEvidenceCandidate(
                Guid.CreateVersion7(Now).ToString("D"),
                verification.VerificationId,
                verification.ExpectedSourceSurface,
                verification.ExpectedSourceAdapter,
                DoctorEvidenceClass.RealSource,
                kind,
                reference,
                Now,
                verification.ExpiresAt);
            var result = store.ObserveCandidate(candidate);
            Assert.Equal(DoctorResultCode.VerificationActive, result.Code);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cao-first-trace-" + Guid.NewGuid().ToString("N"),
                "doctor.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class TestFirstTraceAdapter(
        string adapterId = "test-adapter",
        string sourceSurface = "test-source",
        string expectedSourceAdapter = "test-otel",
        bool readyOnVerification = false,
        bool requireExplicitSelection = false,
        MonitorInstallStatus monitorInstall = MonitorInstallStatus.Installed) : IFirstTraceSourceAdapter
    {
        public string AdapterId { get; } = adapterId;
        public string SourceSurface { get; } = sourceSurface;
        public string ExpectedSourceAdapter { get; } = expectedSourceAdapter;
        private bool RequireExplicitSelection { get; } = requireExplicitSelection;
        private MonitorInstallStatus MonitorInstall { get; } = monitorInstall;

        public bool TryNormalizeEndpoint(string? endpoint, out string normalizedEndpoint)
        {
            normalizedEndpoint = endpoint ?? "http://127.0.0.1:4320";
            return endpoint is null || endpoint == "http://127.0.0.1:4320";
        }

        public bool IsValidInteraction(string? interaction) => interaction is null or "variant";

        public DoctorFactSnapshot CollectFacts(string databasePath, string normalizedEndpoint, DoctorVerification? verification) =>
            new(
                DoctorSchemaVersions.FactsV1,
                SourceSurface,
                ExpectedSourceAdapter,
                Now,
                verification?.VerificationId,
                [],
                new(MonitorInstall, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
                new(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
                new(EndpointAlignmentStatus.Match),
                new(ReachabilityStatus.Reachable),
                new(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled),
                new(SourceCompatibilityStatus.Supported, SchemaStatus.Matching),
                new(verification is null ? LastIngestOutcome.None : readyOnVerification ? LastIngestOutcome.Accepted : LastIngestOutcome.None),
                new(verification is null ? RawPersistenceOutcome.NotPersisted : readyOnVerification ? RawPersistenceOutcome.Persisted : RawPersistenceOutcome.NotPersisted),
                new(verification is null ? ProjectionOutcome.NotStarted : readyOnVerification ? ProjectionOutcome.Completed : ProjectionOutcome.NotStarted),
                new(verification is null || !readyOnVerification ? ExactSessionBindingRequirement.NotRequired : ExactSessionBindingRequirement.Required,
                    verification is null || !readyOnVerification ? ExactSessionBindingOutcome.NotApplicable : ExactSessionBindingOutcome.ExactBound),
                new(verification is null || !readyOnVerification ? DoctorCompleteness.Unknown : DoctorCompleteness.Full,
                    ContentCaptureStatus.Enabled,
                    RawAccessStatus.Available),
                new(RestartRequirement.NotRequired));

        public IReadOnlyList<FirstTraceGuidance> GetGuidance(string? interaction, bool includeSetupPlan) =>
            [new("common", "test guidance", includeSetupPlan ? "setup plan --adapter test-adapter --target cli" : null)];

        public FirstTraceEvidenceSelection SelectEvidence(IReadOnlyList<DoctorEvidenceCandidate> candidates, DateTimeOffset now) =>
            RequireExplicitSelection || candidates.Count == 0
                ? FirstTraceEvidenceSelection.Explicit
                : FirstTraceEvidenceSelection.Auto(candidates.Select(candidate => candidate.EvidenceRef).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

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
    public void Begin_DefaultExpiryUsesTheSingleStoreClockReading()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var time = new SteppingTimeProvider(Now, Now.AddSeconds(1));

        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            new FirstTraceOrchestrator([adapter], time));

        Assert.Equal(0, result.ExitCode);
        var verification = DoctorJson.DeserializeResult(
            JsonDocument.Parse(result.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        Assert.Equal(TimeSpan.FromMinutes(10), verification.ExpiresAt - verification.StartedAt);
        Assert.Equal(1, time.ReadCount);
    }

    [Fact]
    public void Begin_ExplicitExpiryPassesThroughUnchanged()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var explicitExpiry = Now.AddMinutes(5);

        var result = Run(
            [
                "begin", "--database", database.Path, "--adapter", adapter.AdapterId,
                "--expires-at", explicitExpiry.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                "--json",
            ],
            CreateOrchestrator(adapter));

        Assert.Equal(0, result.ExitCode);
        var verification = DoctorJson.DeserializeResult(
            JsonDocument.Parse(result.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        Assert.Equal(explicitExpiry, verification.ExpiresAt);
        Assert.Equal(TimeSpan.FromMinutes(5), verification.ExpiresAt - verification.StartedAt);
    }

    [Theory]
    [InlineData("verification-started", FirstTraceCodes.VerificationStarted, 0)]
    [InlineData("blocked", FirstTraceCodes.Blocked, 3)]
    [InlineData("active", FirstTraceCodes.ActiveVerificationExists, 3)]
    [InlineData("status", FirstTraceCodes.StatusReported, 0)]
    [InlineData("completed", FirstTraceCodes.Completed, 0)]
    [InlineData("not-ready", FirstTraceCodes.NotReady, 3)]
    [InlineData("cancelled", FirstTraceCodes.Cancelled, 0)]
    [InlineData("explicit-selection", FirstTraceCodes.ExplicitEvidenceSelectionRequired, 3)]
    [InlineData("doctor-failed", FirstTraceCodes.DoctorFailed, 4)]
    [InlineData("invalid", FirstTraceCodes.InvalidArguments, 2)]
    public void EnvelopeCodesUseClosedExitAndStderrMapping(
        string scenario,
        string expectedCode,
        int expectedExitCode)
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(
            readyOnVerification: scenario is "completed" or "explicit-selection",
            requireExplicitSelection: scenario == "explicit-selection",
            monitorInstall: scenario == "blocked" ? MonitorInstallStatus.NotInstalled : MonitorInstallStatus.Installed);
        var result = RunEnvelopeScenario(scenario, database.Path, adapter);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedExitCode == 0 ? string.Empty : expectedCode + Environment.NewLine, result.Error);
        Assert.Equal(expectedCode, JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(DoctorResultCode.InvalidInput, 2)]
    [InlineData(DoctorResultCode.EvaluationCompleted, 3)]
    [InlineData(DoctorResultCode.VerificationStale, 4)]
    [InlineData(DoctorResultCode.DoctorStoreBusy, 5)]
    public void DoctorFailureUsesEveryDoctorCliExitMappingClass(
        DoctorResultCode doctorCode,
        int expectedExitCode)
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            CreateOrchestrator(
                adapter,
                _ => new DoctorResult(
                    DoctorSchemaVersions.ResultV1,
                    Success: false,
                    doctorCode,
                    Evaluation: null,
                    Verification: null)));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(FirstTraceCodes.DoctorFailed + Environment.NewLine, result.Error);
        Assert.Equal(
            FirstTraceCodes.DoctorFailed,
            JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Begin_ValidEndpointPassesThroughToAdapter()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        const string endpoint = "http://127.0.0.1:4320";

        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--endpoint", endpoint, "--json"],
            CreateOrchestrator(adapter));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(endpoint, adapter.LastNormalizedEndpoint);
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
        var previewJson = preview.GetRawText();
        Assert.Equal(
            DoctorJson.SerializeResult(DoctorJson.DeserializeResult(previewJson)),
            previewJson);
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
    public void Complete_WithNoEligibleCandidatesReturnsNotReadyAndKeepsVerificationActive()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var orchestrator = CreateOrchestrator(adapter);
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(
            JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;

        var result = Run(
            [
                "complete", "--database", database.Path, "--verification-id", verification.VerificationId,
                "--expected-revision", "1", "--json",
            ],
            orchestrator);

        Assert.Equal(3, result.ExitCode);
        using var complete = JsonDocument.Parse(result.Output);
        Assert.Equal(FirstTraceCodes.NotReady, complete.RootElement.GetProperty("code").GetString());
        var doctorJson = complete.RootElement.GetProperty("doctor").GetRawText();
        var doctor = DoctorJson.DeserializeResult(doctorJson);
        Assert.Equal("doctor.v1", complete.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
        Assert.Equal(DoctorResultCode.EvaluationCompleted, doctor.Code);
        Assert.True(doctor.Success);
        var evaluation = Assert.IsType<DoctorEvaluation>(doctor.Evaluation);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, evaluation.PrimaryState!.StateCode);
        Assert.Equal(DoctorJson.SerializeResult(doctor), doctorJson);

        var status = Run(
            [
                "status", "--database", database.Path, "--verification-id", verification.VerificationId,
                "--json",
            ],
            orchestrator);

        Assert.Equal(0, status.ExitCode);
        using var statusDocument = JsonDocument.Parse(status.Output);
        Assert.Equal(FirstTraceCodes.StatusReported, statusDocument.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "active",
            statusDocument.RootElement.GetProperty("doctor").GetProperty("verification").GetProperty("state").GetString());
    }

    [Fact]
    public void Complete_WithNoEligibleCandidatesUsesStatelessPreWindowEvaluationWhenWindowIsPartial()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter();
        var orchestrator = CreateOrchestrator(
            adapter,
            snapshot => snapshot.VerificationId is null
                ? DoctorEvaluator.Evaluate(snapshot)
                : new DoctorResult(
                    DoctorSchemaVersions.ResultV1,
                    Success: false,
                    DoctorResultCode.PartialFactSnapshot,
                    new DoctorEvaluation(snapshot.SourceSurface, null, [], ["raw_persistence"]),
                    Verification: null));
        var begin = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            orchestrator);
        var verification = DoctorJson.DeserializeResult(
            JsonDocument.Parse(begin.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;

        var result = Run(
            [
                "complete", "--database", database.Path, "--verification-id", verification.VerificationId,
                "--expected-revision", "1", "--json",
            ],
            orchestrator);

        Assert.Equal(3, result.ExitCode);
        using var complete = JsonDocument.Parse(result.Output);
        var doctor = complete.RootElement.GetProperty("doctor");
        Assert.Equal(FirstTraceCodes.NotReady, complete.RootElement.GetProperty("code").GetString());
        Assert.Equal("evaluation_completed", doctor.GetProperty("code").GetString());
        Assert.True(doctor.GetProperty("success").GetBoolean());
        Assert.Equal(
            "ready_no_real_trace",
            doctor.GetProperty("evaluation").GetProperty("primary_state").GetProperty("state_code").GetString());
        Assert.Empty(complete.RootElement.GetProperty("candidates").EnumerateArray());

        var status = Run(
            [
                "status", "--database", database.Path, "--verification-id", verification.VerificationId,
                "--json",
            ],
            orchestrator);

        Assert.Equal(0, status.ExitCode);
        Assert.Equal(
            "active",
            JsonDocument.Parse(status.Output).RootElement.GetProperty("doctor")
                .GetProperty("verification").GetProperty("state").GetString());
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
        var doctor = DoctorJson.DeserializeResult(envelope.GetProperty("doctor").GetRawText());
        Assert.Equal("evaluation_completed", envelope.GetProperty("doctor").GetProperty("code").GetString());
        Assert.Equal(DoctorVerificationState.Active, doctor.Verification!.State);
        Assert.Equal(1, doctor.Verification.Revision);
    }

    [Fact]
    public void Complete_DoesNotPassAdapterSuppliedObservationsToDoctorCompletion()
    {
        using var database = new TemporaryDatabase();
        var adapter = new TestFirstTraceAdapter(readyOnVerification: true, includeAdapterObservation: true);
        DoctorFactSnapshot? completionSnapshot = null;
        var result = Run(
            ["begin", "--database", database.Path, "--adapter", adapter.AdapterId, "--json"],
            CreateOrchestrator(
                adapter,
                snapshot =>
                {
                    if (snapshot.VerificationId is not null)
                    {
                        completionSnapshot = snapshot;
                    }

                    return DoctorEvaluator.Evaluate(snapshot);
                }));
        var verification = DoctorJson.DeserializeResult(
            JsonDocument.Parse(result.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
        InsertReadyCandidates(database.Path, verification);

        result = Run(
            ["complete", "--database", database.Path, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
            CreateOrchestrator(
                adapter,
                snapshot =>
                {
                    if (snapshot.VerificationId is not null)
                    {
                        completionSnapshot = snapshot;
                    }

                    return DoctorEvaluator.Evaluate(snapshot);
                }));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FirstTraceCodes.Completed, JsonDocument.Parse(result.Output).RootElement.GetProperty("code").GetString());
        Assert.NotNull(completionSnapshot);
        Assert.DoesNotContain(
            completionSnapshot!.Observations,
            observation => observation.EvidenceRef == "adapter-supplied-observation");
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

    private static FirstTraceOrchestrator CreateOrchestrator(
        TestFirstTraceAdapter adapter,
        Func<DoctorFactSnapshot, DoctorResult>? evaluator = null)
    {
        var time = new FixedTimeProvider(Now);
        return new(
            [adapter],
            time,
            databasePath => SqliteDoctorApplicationService.Create(
                new SqliteDoctorVerificationStore(databasePath, time),
                evaluator));
    }

    private static CommandResult RunEnvelopeScenario(
        string scenario,
        string databasePath,
        TestFirstTraceAdapter adapter)
    {
        var orchestrator = CreateOrchestrator(adapter);
        var begin = new[] { "begin", "--database", databasePath, "--adapter", adapter.AdapterId, "--json" };
        switch (scenario)
        {
            case "verification-started":
                return Run(begin, orchestrator);
            case "blocked":
                return Run(begin, orchestrator);
            case "active":
                var store = new SqliteDoctorVerificationStore(databasePath, new FixedTimeProvider(Now));
                Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
                Assert.NotNull(store.Start(adapter.SourceSurface, adapter.ExpectedSourceAdapter, TimeSpan.FromMinutes(5)).Verification);
                return Run(begin, orchestrator);
            case "status":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                return Run(
                    ["status", "--database", databasePath, "--verification-id", verification.VerificationId, "--json"],
                    orchestrator);
            }
            case "completed":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                InsertReadyCandidates(databasePath, verification);
                return Run(
                    ["complete", "--database", databasePath, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
                    orchestrator);
            }
            case "not-ready":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                var candidate = InsertCandidate(databasePath, verification, "test-not-ready");
                return Run(
                    ["complete", "--database", databasePath, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--evidence", candidate.EvidenceRef, "--json"],
                    orchestrator);
            }
            case "cancelled":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                return Run(
                    ["cancel", "--database", databasePath, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
                    orchestrator);
            }
            case "explicit-selection":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                InsertCandidate(databasePath, verification, "test-explicit-selection");
                return Run(
                    ["complete", "--database", databasePath, "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"],
                    orchestrator);
            }
            case "doctor-failed":
            {
                var started = Run(begin, orchestrator);
                var verification = DoctorJson.DeserializeResult(
                    JsonDocument.Parse(started.Output).RootElement.GetProperty("doctor").GetRawText()).Verification!;
                return Run(
                    ["complete", "--database", databasePath, "--verification-id", verification.VerificationId, "--expected-revision", "2", "--evidence", "test-ref", "--json"],
                    orchestrator);
            }
            case "invalid":
                return Run(["begin", "--database", databasePath, "--json"], orchestrator);
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

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

    private sealed class SteppingTimeProvider(
        DateTimeOffset first,
        DateTimeOffset subsequent) : TimeProvider
    {
        private int reads;

        public int ReadCount => reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref reads) == 1 ? first : subsequent;
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
        MonitorInstallStatus monitorInstall = MonitorInstallStatus.Installed,
        bool includeAdapterObservation = false) : IFirstTraceSourceAdapter
    {
        public string AdapterId { get; } = adapterId;
        public string SourceSurface { get; } = sourceSurface;
        public string ExpectedSourceAdapter { get; } = expectedSourceAdapter;
        private bool RequireExplicitSelection { get; } = requireExplicitSelection;
        private MonitorInstallStatus MonitorInstall { get; } = monitorInstall;
        private bool IncludeAdapterObservation { get; } = includeAdapterObservation;

        public string? LastNormalizedEndpoint { get; private set; }

        public bool TryNormalizeEndpoint(string? endpoint, out string normalizedEndpoint)
        {
            normalizedEndpoint = endpoint ?? "http://127.0.0.1:4320";
            return endpoint is null || endpoint == "http://127.0.0.1:4320";
        }

        public bool IsValidInteraction(string? interaction) => interaction is null or "variant";

        public DoctorFactSnapshot CollectFacts(string databasePath, string normalizedEndpoint, DoctorVerification? verification) =>
            CaptureEndpoint(normalizedEndpoint, new(
                DoctorSchemaVersions.FactsV1,
                SourceSurface,
                ExpectedSourceAdapter,
                Now,
                verification?.VerificationId,
                IncludeAdapterObservation && verification is not null
                    ? [new DoctorObservation(
                        SourceSurface,
                        ExpectedSourceAdapter,
                        DoctorEvidenceClass.RealSource,
                        DoctorEvidenceKind.Ingest,
                        "adapter-supplied-observation",
                        Now)]
                    : [],
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
                new(RestartRequirement.NotRequired)));

        private DoctorFactSnapshot CaptureEndpoint(string endpoint, DoctorFactSnapshot snapshot)
        {
            LastNormalizedEndpoint = endpoint;
            return snapshot;
        }

        public IReadOnlyList<FirstTraceGuidance> GetGuidance(string? interaction, bool includeSetupPlan) =>
            [new("common", "test guidance", includeSetupPlan ? "setup plan --adapter test-adapter --target cli" : null)];

        public FirstTraceEvidenceSelection SelectEvidence(IReadOnlyList<DoctorEvidenceCandidate> candidates, DateTimeOffset now) =>
            candidates.Count == 0
                ? FirstTraceEvidenceSelection.NoEligibleCandidates
                : RequireExplicitSelection
                    ? FirstTraceEvidenceSelection.Explicit
                : FirstTraceEvidenceSelection.Auto(candidates.Select(candidate => candidate.EvidenceRef).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

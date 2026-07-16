using System.Text;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class DoctorCliTests
{
    private const string VerificationId = "018f3b9a-0000-7000-8000-000000000001";
    private const string ExpiresAt = "2026-07-16T01:20:00.0000000Z";
    private const string SnapshotJson = """
        {
          "schema_version":"doctor.facts.v1",
          "source_surface":"github-copilot-vscode",
          "expected_source_adapter":null,
          "observed_at":"2026-07-16T01:02:03.0000000Z",
          "verification_id":null,
          "observations":[],
          "install_and_source_version":{"monitor_install":"installed","source_version":"supported","source_feature":"available"},
          "process_receiver_and_port":{"monitor_process":"not_running","receiver_bind":"not_bound","port_owner":"none"},
          "source_effective_configuration":{"endpoint_alignment":"match"},
          "endpoint_reachability":{"reachability":"reachable"},
          "protocol_and_signal_compatibility":{"protocol":"http_protobuf","trace_signal":"enabled"},
          "source_version_and_schema_diagnostics":{"compatibility":"supported","schema":"matching"},
          "last_ingest":{"outcome":"none"},
          "raw_persistence":{"outcome":"not_persisted"},
          "projection":{"outcome":"not_started"},
          "exact_session_binding":{"requirement":"not_required","outcome":"not_applicable"},
          "completeness_and_content":{"completeness":"full","content_capture":"enabled","raw_access":"available"},
          "restart_or_new_process":{"requirement":"not_required"}
        }
        """;

    [Fact]
    public void Evaluate_UsesInjectedApplicationAndProjectsItsSingleResult()
    {
        using var input = DoctorInputFile.Create(SnapshotJson);
        var expected = EvaluationResult(DoctorStateCode.MonitorNotRunning);
        var application = new RecordingDoctorApplication { Result = expected };

        var json = Run(
            ["doctor", "evaluate", "--input", input.Path, "--json"],
            application);
        var human = Run(
            ["doctor", "evaluate", "--input", input.Path],
            application);

        Assert.Equal(3, json.ExitCode);
        Assert.Equal(DoctorJson.SerializeResult(expected) + Environment.NewLine, json.Output);
        Assert.Equal(string.Empty, json.Error);
        Assert.Equal(3, human.ExitCode);
        Assert.Equal(DoctorHumanProjector.Project(expected) + Environment.NewLine, human.Output);
        Assert.Equal(string.Empty, human.Error);
        Assert.Equal(2, application.EvaluateCalls.Count);
        Assert.All(application.EvaluateCalls, snapshot => Assert.Equal("github-copilot-vscode", snapshot.SourceSurface));
    }

    [Fact]
    public void VerificationCommands_ParseStrictValuesAndUseInjectedApplication()
    {
        using var input = DoctorInputFile.Create(CompleteInputJson());
        var application = new RecordingDoctorApplication { Result = LifecycleResult(DoctorResultCode.VerificationStarted) };

        var start = Run(
            ["doctor", "verification", "start", "--expires-at", ExpiresAt, "--source-adapter", "github-copilot", "--database", "private-doctor.db", "--source-surface", "github-copilot-vscode", "--json"],
            application);
        application.Result = LifecycleResult(DoctorResultCode.VerificationActive);
        var status = Run(
            ["doctor", "verification", "status", "--verification-id", VerificationId, "--database", "private-doctor.db", "--json"],
            application);
        application.Result = LifecycleResult(DoctorResultCode.VerificationCompleted);
        var complete = Run(
            ["doctor", "verification", "complete", "--input", input.Path, "--expected-revision", "1", "--database", "private-doctor.db", "--verification-id", VerificationId, "--json"],
            application);
        application.Result = LifecycleResult(DoctorResultCode.VerificationCancelled);
        var cancel = Run(
            ["doctor", "verification", "cancel", "--expected-revision", "2", "--verification-id", VerificationId, "--database", "private-doctor.db", "--json"],
            application);

        Assert.All([start, status, complete, cancel], result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            Assert.DoesNotContain("private-doctor.db", result.Output, StringComparison.Ordinal);
            Assert.Single(result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        });
        Assert.Equal(("private-doctor.db", "github-copilot-vscode", "github-copilot", DateTimeOffset.Parse(ExpiresAt)), application.StartCalls.Single());
        Assert.Equal(("private-doctor.db", VerificationId), application.StatusCalls.Single());
        var completed = application.CompleteCalls.Single();
        Assert.Equal(("private-doctor.db", VerificationId, 1), (completed.DatabasePath, completed.VerificationId, completed.ExpectedRevision));
        Assert.Empty(completed.Input.FactSnapshot.Observations);
        Assert.Equal(["candidate-001"], completed.Input.AcceptedEvidenceRefs);
        Assert.Equal(("private-doctor.db", VerificationId, 2), application.CancelCalls.Single());
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public void Commands_RejectMissingDuplicateUnknownAndInvalidLexicalArguments(string[] args)
    {
        var application = new RecordingDoctorApplication();

        var result = Run(args, application);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Doctor: invalid_arguments" + Environment.NewLine, result.Output);
        Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
        Assert.Equal(0, application.TotalCalls);
        Assert.DoesNotContain(CliHelpText.Text, result.Output, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> InvalidArgumentCases()
    {
        var cases = new TheoryData<string[]>();
        cases.Add(["doctor"]);
        cases.Add(["doctor", "unknown"]);
        cases.Add(["doctor", "evaluate", "--input"]);
        cases.Add(["doctor", "evaluate", "--input", "one", "--input", "two"]);
        cases.Add(["doctor", "evaluate", "--unexpected", "secret-value"]);
        cases.Add(["doctor", "verification", "start", "--database", "db", "--source-surface", "Bad Source", "--expires-at", ExpiresAt]);
        cases.Add(["doctor", "verification", "start", "--database", "db", "--source-surface", new string('a', 65), "--expires-at", ExpiresAt]);
        cases.Add(["doctor", "verification", "start", "--database", "db", "--source-surface", "source", "--source-adapter", "Bad/Adapter", "--expires-at", ExpiresAt]);
        cases.Add(["doctor", "verification", "start", "--database", "db", "--source-surface", "source", "--expires-at", "2026-07-16T01:20:00Z"]);
        cases.Add(["doctor", "verification", "start", "--database", "db", "--source-surface", "source", "--expires-at", "2026-07-16T10:20:00.0000000+09:00"]);
        cases.Add(["doctor", "verification", "status", "--database", "db", "--verification-id", "018f3b9a-0000-4000-8000-000000000001"]);
        cases.Add(["doctor", "verification", "status", "--database", "db", "--verification-id", VerificationId.ToUpperInvariant()]);
        cases.Add(["doctor", "verification", "complete", "--database", "db", "--verification-id", VerificationId, "--expected-revision", "0", "--input", "facts.json"]);
        cases.Add(["doctor", "verification", "complete", "--database", "db", "--verification-id", VerificationId, "--expected-revision", "2147483648", "--input", "facts.json"]);
        cases.Add(["doctor", "verification", "cancel", "--database", "db", "--verification-id", VerificationId, "--expected-revision", "+1"]);
        cases.Add(["doctor", "verification", "cancel", "--database", "db", "--verification-id", VerificationId, "--expected-revision", "1", "--json", "--json"]);
        return cases;
    }

    [Fact]
    public void JsonFlag_OnInvalidArguments_SelectsCanonicalJsonProjection()
    {
        var result = Run(["doctor", "evaluate", "--json"], new RecordingDoctorApplication());

        Assert.Equal(2, result.ExitCode);
        var parsed = DoctorJson.DeserializeResult(result.Output);
        Assert.Equal(DoctorResultCode.InvalidArguments, parsed.Code);
        Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
    }

    [Theory]
    [InlineData("evaluate")]
    [InlineData("complete")]
    public void UnsupportedSchema_IsClassifiedBeforeV1ShapeOrApplication(string command)
    {
        var unsupportedSnapshot = SnapshotJson
            .Replace(DoctorSchemaVersions.FactsV1, "doctor.facts.v2", StringComparison.Ordinal)
            .Replace("{", "{\"v2_only_member\":true,", StringComparison.Ordinal);
        using var input = DoctorInputFile.Create(
            command == "complete"
                ? CompleteInputJson(snapshotJson: unsupportedSnapshot)
                : unsupportedSnapshot);
        var application = new RecordingDoctorApplication { Result = LifecycleResult(DoctorResultCode.DoctorStoreUnavailable) };
        var args = command == "complete"
            ? CompleteArgs(input.Path)
            : new[] { "doctor", "evaluate", "--input", input.Path, "--json" };

        var result = Run(args, application);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorResultCode.UnsupportedSchemaVersion, DoctorJson.DeserializeResult(result.Output).Code);
        Assert.Equal("unsupported_schema_version" + Environment.NewLine, result.Error);
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public void UnsupportedCompleteSchema_DefaultNoStoreDoesNotOverrideClassification()
    {
        using var input = DoctorInputFile.Create(CompleteInputJson(
            snapshotJson: SnapshotJson.Replace(DoctorSchemaVersions.FactsV1, "doctor.facts.v2", StringComparison.Ordinal)));

        var result = RunProduction(CompleteArgs(input.Path));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorResultCode.UnsupportedSchemaVersion, DoctorJson.DeserializeResult(result.Output).Code);
        Assert.Equal("unsupported_schema_version" + Environment.NewLine, result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    public void MalformedSchema_RemainsInvalidInput(string scenario)
    {
        var json = scenario switch
        {
            "missing" => SnapshotJson.Replace("  \"schema_version\":\"doctor.facts.v1\",\n", "", StringComparison.Ordinal),
            "null" => SnapshotJson.Replace("\"schema_version\":\"doctor.facts.v1\"", "\"schema_version\":null", StringComparison.Ordinal),
            "number" => SnapshotJson.Replace("\"schema_version\":\"doctor.facts.v1\"", "\"schema_version\":2", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        using var input = DoctorInputFile.Create(json);
        var application = new RecordingDoctorApplication();

        var result = Run(["doctor", "evaluate", "--input", input.Path, "--json"], application);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorResultCode.InvalidInput, DoctorJson.DeserializeResult(result.Output).Code);
        Assert.Equal(0, application.TotalCalls);
    }

    [Theory]
    [InlineData("evaluate", false, false)]
    [InlineData("evaluate", true, false)]
    [InlineData("evaluate", false, true)]
    [InlineData("evaluate", true, true)]
    [InlineData("complete", false, false)]
    [InlineData("complete", true, false)]
    [InlineData("complete", false, true)]
    [InlineData("complete", true, true)]
    public void OptionalSnapshotMembers_AcceptExplicitNullOrOmission(
        string command,
        bool omitExpectedAdapter,
        bool omitVerificationId)
    {
        var snapshotJson = SnapshotJson;
        if (omitExpectedAdapter)
        {
            snapshotJson = snapshotJson.Replace("  \"expected_source_adapter\":null,\n", "", StringComparison.Ordinal);
        }

        if (omitVerificationId)
        {
            snapshotJson = snapshotJson.Replace("  \"verification_id\":null,\n", "", StringComparison.Ordinal);
        }

        using var input = DoctorInputFile.Create(
            command == "complete"
                ? CompleteInputJson(snapshotJson: snapshotJson)
                : snapshotJson);
        var application = new RecordingDoctorApplication
        {
            Result = command == "complete"
                ? LifecycleResult(DoctorResultCode.VerificationCompleted)
                : EvaluationResult(DoctorStateCode.MonitorNotRunning),
        };
        var args = command == "complete"
            ? CompleteArgs(input.Path)
            : new[] { "doctor", "evaluate", "--input", input.Path, "--json" };

        var result = Run(args, application);

        Assert.Equal(command == "complete" ? 0 : 3, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(1, application.TotalCalls);
        var snapshot = command == "complete"
            ? application.CompleteCalls.Single().Input.FactSnapshot
            : application.EvaluateCalls.Single();
        Assert.Null(snapshot.ExpectedSourceAdapter);
        Assert.Null(snapshot.VerificationId);
    }

    [Fact]
    public void InputFiles_AcceptExactly64KiBAndRejectTheSentinelByte()
    {
        var prefixBytes = Encoding.UTF8.GetByteCount(SnapshotJson);
        using var bounded = DoctorInputFile.Create(SnapshotJson + new string(' ', 65_536 - prefixBytes));
        using var oversized = DoctorInputFile.Create(SnapshotJson + new string(' ', 65_537 - prefixBytes));
        var application = new RecordingDoctorApplication { Result = EvaluationResult(DoctorStateCode.MonitorNotRunning) };

        var accepted = Run(["doctor", "evaluate", "--input", bounded.Path, "--json"], application);
        var rejected = Run(["doctor", "evaluate", "--input", oversized.Path, "--json"], application);

        Assert.Equal(3, accepted.ExitCode);
        Assert.Equal(2, rejected.ExitCode);
        Assert.Equal("invalid_input" + Environment.NewLine, rejected.Error);
        Assert.Equal(1, application.TotalCalls);
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("malformed-json")]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("missing-family")]
    [InlineData("missing-family-member")]
    [InlineData("invalid-source")]
    [InlineData("evaluate-verification-context")]
    [InlineData("complete-observations")]
    [InlineData("complete-duplicate-reference")]
    public void InputFiles_RejectStrictJsonAndUntrustedCompletionShapes(string scenario)
    {
        using var input = scenario switch
        {
            "invalid-utf8" => DoctorInputFile.Create([0xff, 0xfe, 0xfd]),
            "malformed-json" => DoctorInputFile.Create("{") ,
            "duplicate-property" => DoctorInputFile.Create(SnapshotJson.Replace("\"schema_version\":\"doctor.facts.v1\",", "\"schema_version\":\"doctor.facts.v1\",\"schema_version\":\"doctor.facts.v1\",")),
            "unknown-property" => DoctorInputFile.Create(SnapshotJson.Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal)),
            "missing-family" => DoctorInputFile.Create(SnapshotJson.Replace(",\n  \"restart_or_new_process\":{\"requirement\":\"not_required\"}", "", StringComparison.Ordinal)),
            "missing-family-member" => DoctorInputFile.Create(SnapshotJson.Replace(",\"source_feature\":\"available\"", "", StringComparison.Ordinal)),
            "invalid-source" => DoctorInputFile.Create(SnapshotJson.Replace("github-copilot-vscode", "Bad Source", StringComparison.Ordinal)),
            "evaluate-verification-context" => DoctorInputFile.Create(SnapshotJson.Replace("\"verification_id\":null", $"\"verification_id\":\"{VerificationId}\"", StringComparison.Ordinal)),
            "complete-observations" => DoctorInputFile.Create(CompleteInputJson(observations: "[{\"source_surface\":\"github-copilot-vscode\",\"source_adapter\":null,\"evidence_class\":\"real_source\",\"evidence_kind\":\"ingest\",\"evidence_ref\":\"candidate-001\",\"observed_at\":\"2026-07-16T01:02:03.0000000Z\"}]")),
            "complete-duplicate-reference" => DoctorInputFile.Create(CompleteInputJson(references: "[\"candidate-001\",\"candidate-001\"]")),
            _ => throw new InvalidOperationException(),
        };
        var application = new RecordingDoctorApplication();
        var args = scenario.StartsWith("complete-", StringComparison.Ordinal)
            ? new[] { "doctor", "verification", "complete", "--database", "secret.db", "--verification-id", VerificationId, "--expected-revision", "1", "--input", input.Path, "--json" }
            : new[] { "doctor", "evaluate", "--input", input.Path, "--json" };

        var result = Run(args, application);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorResultCode.InvalidInput, DoctorJson.DeserializeResult(result.Output).Code);
        Assert.Equal("invalid_input" + Environment.NewLine, result.Error);
        Assert.Equal(0, application.TotalCalls);
        Assert.DoesNotContain(input.Path, result.Output + result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.db", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidFilesystemPath_IsSanitizedAsInvalidInput()
    {
        var result = Run(
            ["doctor", "evaluate", "--input", "invalid\0path", "--json"],
            new RecordingDoctorApplication());

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorResultCode.InvalidInput, DoctorJson.DeserializeResult(result.Output).Code);
        Assert.Equal("invalid_input" + Environment.NewLine, result.Error);
        Assert.DoesNotContain("path", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsafeEvidenceReferenceCases))]
    public void UnsafeEvidenceReference_DirectObservationNeverReachesApplicationOrOutput(string reference)
    {
        using var input = DoctorInputFile.Create(SnapshotWithObservation(reference));
        var application = new RecordingDoctorApplication();

        var result = Run(["doctor", "evaluate", "--input", input.Path, "--json"], application);

        AssertSanitizedInvalidInput(result, application);
    }

    [Theory]
    [MemberData(nameof(UnsafeEvidenceReferenceCases))]
    public void UnsafeEvidenceReference_CompleteSelectionNeverReachesApplicationOrOutput(string reference)
    {
        using var input = DoctorInputFile.Create(CompleteInputJson(
            references: System.Text.Json.JsonSerializer.Serialize(new[] { reference })));
        var application = new RecordingDoctorApplication();

        var result = Run(CompleteArgs(input.Path), application);

        AssertSanitizedInvalidInput(result, application);
    }

    public static TheoryData<string> UnsafeEvidenceReferenceCases()
    {
        var cases = new TheoryData<string>
        {
            "raw prompt",
            "RAW RESPONSE",
            "Tool.Arguments",
            "TOOL.RESULTS",
            "Authorization: Bearer abcdefghijklmnop",
            "credential=abcdefghi",
            "person@example.com",
            "user.id=person-001",
            "123-45-6789",
            "urn:uuid:018f3b9a-0000-7000-8000-000000000001",
            "HTTPS://example.invalid/evidence",
            " urn:uuid:018f3b9a-0000-7000-8000-000000000001",
            "urn:uuid:018f3b9a-0000-7000-8000-000000000001 ",
            "C:local.txt",
            "C:\\local\\evidence.txt",
            "/tmp/evidence",
            "\\rooted\\evidence",
            ".\\current",
            "./current",
            "..\\parent",
            "../parent",
            "~\\home",
            "~/home",
            "folder/local",
            "folder\\local",
            "line\nbreak",
            "control\u0001value",
            "",
            "   ",
            new string('a', 129),
        };
        return cases;
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(16, 0, 1)]
    [InlineData(17, 2, 0)]
    public void ActualCommand_CompleteReferenceCardinalityPinsOffByOneBoundaries(
        int referenceCount,
        int expectedExit,
        int expectedCalls)
    {
        var references = Enumerable.Range(0, referenceCount).Select(index => $"candidate-{index:D3}").ToArray();
        using var input = DoctorInputFile.Create(CompleteInputJson(
            references: System.Text.Json.JsonSerializer.Serialize(references)));
        var application = new RecordingDoctorApplication
        {
            Result = LifecycleResult(DoctorResultCode.VerificationCompleted),
        };

        var result = Run(CompleteArgs(input.Path), application);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(expectedCalls, application.TotalCalls);
        Assert.Equal(expectedExit == 0 ? string.Empty : "invalid_input" + Environment.NewLine, result.Error);
    }

    [Theory]
    [InlineData("evaluate", 1, true)]
    [InlineData("evaluate", 128, true)]
    [InlineData("evaluate", 0, false)]
    [InlineData("evaluate", 129, false)]
    [InlineData("complete", 1, true)]
    [InlineData("complete", 128, true)]
    [InlineData("complete", 0, false)]
    [InlineData("complete", 129, false)]
    public void ActualCommand_EvidenceReferenceLengthPinsOffByOneBoundaries(
        string command,
        int length,
        bool accepted)
    {
        var reference = new string('a', length);
        using var input = DoctorInputFile.Create(command == "complete"
            ? CompleteInputJson(references: System.Text.Json.JsonSerializer.Serialize(new[] { reference }))
            : SnapshotWithObservation(reference));
        var application = new RecordingDoctorApplication
        {
            Result = command == "complete"
                ? LifecycleResult(DoctorResultCode.VerificationCompleted)
                : EvaluationResult(DoctorStateCode.MonitorNotRunning),
        };

        var result = Run(
            command == "complete" ? CompleteArgs(input.Path) : ["doctor", "evaluate", "--input", input.Path, "--json"],
            application);

        Assert.Equal(accepted ? command == "complete" ? 0 : 3 : 2, result.ExitCode);
        Assert.Equal(accepted ? 1 : 0, application.TotalCalls);
        Assert.Equal(accepted ? string.Empty : "invalid_input" + Environment.NewLine, result.Error);
    }

    [Theory]
    [MemberData(nameof(ActualCompleteOutcomeCases))]
    public void ActualCommand_CompletePinsEvaluationConflictStoreAndInternalOutcomes(
        DoctorResultCode code,
        DoctorStateCode? primaryState,
        int expectedExit)
    {
        using var input = DoctorInputFile.Create(CompleteInputJson());
        var application = new RecordingDoctorApplication
        {
            Result = primaryState is null
                ? ResultForCode(code)
                : EvaluationResult(primaryState.Value),
        };

        var result = Run(CompleteArgs(input.Path), application);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Single(application.CompleteCalls);
        Assert.Equal(application.Result.Success ? string.Empty : SnakeCase(code) + Environment.NewLine, result.Error);
        Assert.Equal(code, DoctorJson.DeserializeResult(result.Output).Code);
    }

    public static TheoryData<DoctorResultCode, DoctorStateCode?, int> ActualCompleteOutcomeCases() => new()
    {
        { DoctorResultCode.EvaluationCompleted, DoctorStateCode.ReadyNoRealTrace, 3 },
        { DoctorResultCode.PartialFactSnapshot, null, 3 },
        { DoctorResultCode.VerificationStale, null, 4 },
        { DoctorResultCode.DoctorStoreBusy, null, 5 },
        { DoctorResultCode.InternalError, null, 5 },
    };

    [Theory]
    [InlineData("start", DoctorResultCode.VerificationStarted, 0)]
    [InlineData("start", DoctorResultCode.DoctorStoreUnavailable, 5)]
    [InlineData("status", DoctorResultCode.VerificationActive, 0)]
    [InlineData("status", DoctorResultCode.VerificationNotFound, 4)]
    [InlineData("status", DoctorResultCode.DoctorStoreBusy, 5)]
    [InlineData("cancel", DoctorResultCode.VerificationCancelled, 0)]
    [InlineData("cancel", DoctorResultCode.VerificationStale, 4)]
    [InlineData("cancel", DoctorResultCode.DoctorStoreUnavailable, 5)]
    public void ActualCommand_StartStatusCancelPinSuccessConflictAndStore(
        string command,
        DoctorResultCode code,
        int expectedExit)
    {
        var application = new RecordingDoctorApplication { Result = ResultForCode(code) };

        var result = Run(LifecycleArgs(command, "doctor.db"), application);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(1, application.TotalCalls);
        Assert.Equal(application.Result.Success ? string.Empty : SnakeCase(code) + Environment.NewLine, result.Error);
        Assert.Equal(code, DoctorJson.DeserializeResult(result.Output).Code);
    }

    [Fact]
    public void ActualCommand_CompletePins64KiBBoundaryAndSentinel()
    {
        var json = CompleteInputJson();
        var byteCount = Encoding.UTF8.GetByteCount(json);
        using var bounded = DoctorInputFile.Create(json + new string(' ', 65_536 - byteCount));
        using var oversized = DoctorInputFile.Create(json + new string(' ', 65_537 - byteCount));
        var application = new RecordingDoctorApplication
        {
            Result = LifecycleResult(DoctorResultCode.VerificationCompleted),
        };

        var accepted = Run(CompleteArgs(bounded.Path), application);
        var rejected = Run(CompleteArgs(oversized.Path), application);

        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(string.Empty, accepted.Error);
        Assert.Equal(2, rejected.ExitCode);
        Assert.Equal("invalid_input" + Environment.NewLine, rejected.Error);
        Assert.Equal(1, application.TotalCalls);
    }

    [Fact]
    public void ActualCommand_DefaultProductionLifecycleUsesSuppliedDatabase()
    {
        using var input = DoctorInputFile.Create(CompleteInputJson());
        var databasePath = System.IO.Path.Combine(input.DirectoryPath, "doctor.db");
        var expiresAt = TimeProvider.System.GetUtcNow().AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

        var started = RunProduction([
            "doctor", "verification", "start", "--database", databasePath,
            "--source-surface", "github-copilot-vscode", "--expires-at", expiresAt, "--json"]);
        var verification = Assert.IsType<DoctorVerification>(DoctorJson.DeserializeResult(started.Output).Verification);
        var status = RunProduction([
            "doctor", "verification", "status", "--database", databasePath,
            "--verification-id", verification.VerificationId, "--json"]);
        var cancelled = RunProduction([
            "doctor", "verification", "cancel", "--database", databasePath,
            "--verification-id", verification.VerificationId, "--expected-revision", "1", "--json"]);

        Assert.Equal(0, started.ExitCode);
        Assert.Equal(DoctorResultCode.VerificationStarted, DoctorJson.DeserializeResult(started.Output).Code);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal(DoctorResultCode.VerificationActive, DoctorJson.DeserializeResult(status.Output).Code);
        Assert.Equal(0, cancelled.ExitCode);
        Assert.Equal(DoctorResultCode.VerificationCancelled, DoctorJson.DeserializeResult(cancelled.Output).Code);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void ActualCommand_CompleteJsonAndHumanProjectTheSameResultOnce()
    {
        using var input = DoctorInputFile.Create(CompleteInputJson());
        var expected = LifecycleResult(DoctorResultCode.VerificationCompleted);
        var application = new RecordingDoctorApplication { Result = expected };

        var json = Run(CompleteArgs(input.Path), application);
        var humanArgs = CompleteArgs(input.Path).Where(argument => argument != "--json").ToArray();
        var human = Run(humanArgs, application);

        Assert.Equal(DoctorJson.SerializeResult(expected) + Environment.NewLine, json.Output);
        Assert.Equal(DoctorHumanProjector.Project(expected) + Environment.NewLine, human.Output);
        Assert.Equal(string.Empty, json.Error);
        Assert.Equal(string.Empty, human.Error);
        Assert.Equal(2, application.CompleteCalls.Count);
        Assert.Equal(2, application.TotalCalls);
    }

    [Theory]
    [MemberData(nameof(ExitCodeCases))]
    public void FixedResultCodes_MapToPinnedExitCategories(DoctorResultCode code, DoctorStateCode? primaryState, int expectedExit)
    {
        using var input = DoctorInputFile.Create(SnapshotJson);
        var result = primaryState is null
            ? new DoctorResult(DoctorSchemaVersions.ResultV1, IsSuccessfulCode(code), code, null, null)
            : EvaluationResult(primaryState.Value);
        var application = new RecordingDoctorApplication { Result = result };

        var actual = Run(["doctor", "evaluate", "--input", input.Path, "--json"], application);

        Assert.Equal(expectedExit, actual.ExitCode);
        Assert.Equal(result.Success ? string.Empty : SnakeCase(code) + Environment.NewLine, actual.Error);
    }

    public static TheoryData<DoctorResultCode, DoctorStateCode?, int> ExitCodeCases()
    {
        var cases = new TheoryData<DoctorResultCode, DoctorStateCode?, int>
        {
            { DoctorResultCode.EvaluationCompleted, DoctorStateCode.FirstTraceReady, 0 },
            { DoctorResultCode.EvaluationCompleted, DoctorStateCode.ReadyNoRealTrace, 3 },
            { DoctorResultCode.PartialFactSnapshot, null, 3 },
            { DoctorResultCode.InvalidArguments, null, 2 },
            { DoctorResultCode.InvalidInput, null, 2 },
            { DoctorResultCode.UnsupportedSchemaVersion, null, 2 },
            { DoctorResultCode.VerificationNotFound, null, 4 },
            { DoctorResultCode.VerificationStale, null, 4 },
            { DoctorResultCode.VerificationExpired, null, 4 },
            { DoctorResultCode.VerificationAlreadyCancelled, null, 4 },
            { DoctorResultCode.VerificationAlreadyCompleted, null, 4 },
            { DoctorResultCode.ExpectedSourceMismatch, null, 4 },
            { DoctorResultCode.EvidenceNotFound, null, 4 },
            { DoctorResultCode.EvidenceExpired, null, 4 },
            { DoctorResultCode.DoctorStoreBusy, null, 5 },
            { DoctorResultCode.DoctorStoreUnavailable, null, 5 },
            { DoctorResultCode.InternalError, null, 5 },
        };
        return cases;
    }

    [Fact]
    public void ApplicationFailure_EmitsOnlySanitizedInternalError()
    {
        var application = new RecordingDoctorApplication
        {
            Exception = new InvalidOperationException("SQLite failed at C:\\Users\\person\\secret.db Authorization: Bearer credential PII raw-json")
        };

        var result = Run(
            ["doctor", "verification", "status", "--database", "C:\\Users\\person\\secret.db", "--verification-id", VerificationId, "--json"],
            application);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("internal_error" + Environment.NewLine, result.Error);
        Assert.Equal(DoctorResultCode.InternalError, DoctorJson.DeserializeResult(result.Output).Code);
        foreach (var marker in new[] { "SQLite", "C:\\Users", "secret.db", "Authorization", "Bearer", "credential", "PII", "raw-json", "InvalidOperationException" })
        {
            Assert.DoesNotContain(marker, result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Help_ListsAllFiveDoctorCommandsWithoutChangingOtherHelp()
    {
        var result = Run(["--help"], new RecordingDoctorApplication());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("config-cli doctor evaluate --input <file> [--json]", result.Output, StringComparison.Ordinal);
        Assert.Contains("config-cli doctor verification start", result.Output, StringComparison.Ordinal);
        Assert.Contains("config-cli doctor verification status", result.Output, StringComparison.Ordinal);
        Assert.Contains("config-cli doctor verification complete", result.Output, StringComparison.Ordinal);
        Assert.Contains("config-cli doctor verification cancel", result.Output, StringComparison.Ordinal);
        Assert.Contains("config-cli setup plan", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    private static CommandResult Run(string[] args, IDoctorCliApplication application)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(args, output, error, setupDispatcher: null, application);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private static CommandResult RunProduction(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(args, output, error);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private static string[] CompleteArgs(string inputPath, string databasePath = "doctor.db") =>
        ["doctor", "verification", "complete", "--database", databasePath, "--verification-id", VerificationId, "--expected-revision", "1", "--input", inputPath, "--json"];

    private static string[] LifecycleArgs(string command, string databasePath) => command switch
    {
        "start" => ["doctor", "verification", "start", "--database", databasePath, "--source-surface", "github-copilot-vscode", "--expires-at", ExpiresAt, "--json"],
        "status" => ["doctor", "verification", "status", "--database", databasePath, "--verification-id", VerificationId, "--json"],
        "cancel" => ["doctor", "verification", "cancel", "--database", databasePath, "--verification-id", VerificationId, "--expected-revision", "1", "--json"],
        _ => throw new InvalidOperationException(),
    };

    private static string SnapshotWithObservation(string reference)
    {
        var serializedReference = System.Text.Json.JsonSerializer.Serialize(reference);
        var observations = $$"""[{"source_surface":"github-copilot-vscode","source_adapter":null,"evidence_class":"real_source","evidence_kind":"ingest","evidence_ref":{{serializedReference}},"observed_at":"2026-07-16T01:02:03.0000000Z"}]""";
        return SnapshotJson.Replace("\"observations\":[]", $"\"observations\":{observations}", StringComparison.Ordinal);
    }

    private static void AssertSanitizedInvalidInput(CommandResult result, RecordingDoctorApplication application)
    {
        var expected = new DoctorResult(
            DoctorSchemaVersions.ResultV1,
            Success: false,
            DoctorResultCode.InvalidInput,
            Evaluation: null,
            Verification: null);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(DoctorJson.SerializeResult(expected) + Environment.NewLine, result.Output);
        Assert.Equal("invalid_input" + Environment.NewLine, result.Error);
        Assert.Equal(0, application.TotalCalls);
    }

    private static DoctorResult EvaluationResult(DoctorStateCode stateCode)
    {
        var state = new DoctorState(
            DoctorSchemaVersions.ResultV1,
            stateCode,
            stateCode == DoctorStateCode.FirstTraceReady ? DoctorSeverity.Info : DoctorSeverity.Error,
            "github-copilot-vscode",
            [],
            [stateCode],
            stateCode == DoctorStateCode.FirstTraceReady ? DoctorNextAction.OpenVerifiedTraceOrSession : DoctorNextAction.StartMonitor,
            stateCode == DoctorStateCode.FirstTraceReady ? DoctorRetryability.None : DoctorRetryability.AfterAction,
            DateTimeOffset.Parse("2026-07-16T01:02:03.0000000Z"),
            null);
        return new DoctorResult(
            DoctorSchemaVersions.ResultV1,
            true,
            DoctorResultCode.EvaluationCompleted,
            new DoctorEvaluation("github-copilot-vscode", state, [state], []),
            null);
    }

    private static DoctorResult LifecycleResult(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, true, code, null, null);

    private static DoctorResult ResultForCode(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, IsSuccessfulCode(code), code, null, null);

    private static bool IsSuccessfulCode(DoctorResultCode code) =>
        code is DoctorResultCode.EvaluationCompleted
            or DoctorResultCode.VerificationStarted
            or DoctorResultCode.VerificationActive
            or DoctorResultCode.VerificationCompleted
            or DoctorResultCode.VerificationCancelled;

    private static string CompleteInputJson(
        string observations = "[]",
        string references = "[\"candidate-001\"]",
        string? snapshotJson = null)
    {
        var snapshot = snapshotJson
            ?? SnapshotJson.Replace("\"verification_id\":null", $"\"verification_id\":\"{VerificationId}\"", StringComparison.Ordinal);
        return $$"""
            {
              "fact_snapshot": {{snapshot.Replace("\"observations\":[]", $"\"observations\":{observations}")}},
              "accepted_evidence_refs": {{references}}
            }
            """;
    }

    private static string SnakeCase(DoctorResultCode code) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString());

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class RecordingDoctorApplication : IDoctorCliApplication
    {
        public DoctorResult Result { get; set; } = LifecycleResult(DoctorResultCode.VerificationActive);
        public Exception? Exception { get; init; }
        public List<DoctorFactSnapshot> EvaluateCalls { get; } = [];
        public List<(string DatabasePath, string SourceSurface, string? SourceAdapter, DateTimeOffset ExpiresAt)> StartCalls { get; } = [];
        public List<(string DatabasePath, string VerificationId)> StatusCalls { get; } = [];
        public List<(string DatabasePath, string VerificationId, int ExpectedRevision, DoctorCompletionInput Input)> CompleteCalls { get; } = [];
        public List<(string DatabasePath, string VerificationId, int ExpectedRevision)> CancelCalls { get; } = [];

        public int TotalCalls => EvaluateCalls.Count + StartCalls.Count + StatusCalls.Count + CompleteCalls.Count + CancelCalls.Count;

        public DoctorResult Evaluate(DoctorFactSnapshot snapshot)
        {
            EvaluateCalls.Add(snapshot);
            return ReturnResult();
        }

        public DoctorResult Start(string databasePath, string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt)
        {
            StartCalls.Add((databasePath, sourceSurface, sourceAdapter, expiresAt));
            return ReturnResult();
        }

        public DoctorResult Status(string databasePath, string verificationId)
        {
            StatusCalls.Add((databasePath, verificationId));
            return ReturnResult();
        }

        public DoctorResult Complete(string databasePath, string verificationId, int expectedRevision, DoctorCompletionInput input)
        {
            CompleteCalls.Add((databasePath, verificationId, expectedRevision, input));
            return ReturnResult();
        }

        public DoctorResult Cancel(string databasePath, string verificationId, int expectedRevision)
        {
            CancelCalls.Add((databasePath, verificationId, expectedRevision));
            return ReturnResult();
        }

        private DoctorResult ReturnResult() => Exception is null ? Result : throw Exception;
    }

    private sealed class DoctorInputFile : IDisposable
    {
        private DoctorInputFile(byte[] bytes)
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"doctor-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "input.json");
            File.WriteAllBytes(Path, bytes);
        }

        public string DirectoryPath { get; }
        public string Path { get; }

        public static DoctorInputFile Create(string text) => new(Encoding.UTF8.GetBytes(text));
        public static DoctorInputFile Create(byte[] bytes) => new(bytes);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}

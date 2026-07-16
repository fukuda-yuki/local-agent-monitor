using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class CliApplicationTests
{
    [Fact]
    public void Run_ListCollectionProfiles_WritesSupportedProfiles()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["list-collection-profiles"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("raw-only", output.ToString());
        Assert.Contains("docker-desktop-langfuse", output.ToString());
        Assert.Contains("raw-local-receiver", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_UsesProfileArgument()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "wsl2-docker-langfuse"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:CAO_COLLECTION_PROFILE=\"wsl2-docker-langfuse\"", output.ToString());
        Assert.Contains("http://<windows-reachable-wsl2-host>:3000/api/public/otel", output.ToString());
        Assert.Contains("Prefer localhost when WSL2 localhost forwarding exposes published container ports to Windows.", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ProfileCopilotCliEnv_ReadsEnvironmentProfile()
    {
        var previous = Environment.GetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName, "docker-desktop-collector-langfuse");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = CliApplication.Run(["profile-copilot-cli-env"], output, error);

            Assert.Equal(0, exitCode);
            Assert.Contains("$env:CAO_COLLECTION_PROFILE=\"docker-desktop-collector-langfuse\"", output.ToString());
            Assert.Contains("http://localhost:4318", output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void Run_ProfileCommand_ReturnsNonZeroWithoutProfile()
    {
        var previous = Environment.GetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName, null);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = CliApplication.Run(["profile-vscode-env"], output, error);

            Assert.Equal(1, exitCode);
            Assert.Contains("collection profile is required", error.ToString());
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_RawLocalReceiverWritesLocalReceiverEnvironment()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "raw-local-receiver"], output, error);

        var script = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("$env:CAO_COLLECTION_PROFILE=\"raw-local-receiver\"", script);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://127.0.0.1:4319\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://127.0.0.1:4319\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_PROTOCOL=\"http/protobuf\"", script);
        Assert.Contains("client.kind=vscode-copilot-chat", script);
        Assert.Contains("experiment.id=baseline", script);
        Assert.DoesNotContain("Authorization=Basic", script);
        Assert.DoesNotContain("x-langfuse-ingestion-version", script);
        Assert.DoesNotContain("<langfuse-host>", script);
        Assert.DoesNotContain("<collector-host>", script);
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_RawLocalReceiverTargetMonitorWritesMonitorEndpoint()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "raw-local-receiver", "--target", "monitor"], output, error);

        var script = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://127.0.0.1:4320\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://127.0.0.1:4320\"", script);
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_RawLocalReceiverEndpointOverrideWritesExplicitLoopbackEndpoint()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "raw-local-receiver", "--endpoint", "http://127.0.0.1:54321"], output, error);

        var script = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://127.0.0.1:54321\"", script);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://127.0.0.1:54321\"", script);
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_TargetWithNonRawLocalReceiverProfileReturnsError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "raw-only", "--target", "monitor"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--target and --endpoint apply only to raw-local-receiver.", error.ToString());
    }

    [Fact]
    public void Run_ProfileVsCodeEnv_EndpointWithNonLoopbackHostReturnsError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-vscode-env", "--profile", "raw-local-receiver", "--endpoint", "http://example.com:4320"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("profile-vscode-env --endpoint only allows localhost, 127.0.0.1, or ::1", error.ToString());
    }

    [Fact]
    public void Run_ProfileCopilotCliEnv_RejectsTargetAsUnknownOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-copilot-cli-env", "--profile", "raw-local-receiver", "--target", "monitor"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown collection profile option '--target'", error.ToString());
    }

    [Fact]
    public void Run_ProfileCodexAppConfig_RawLocalReceiverWritesTraceExporterWithoutRemoteHeaders()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["profile-codex-app-config", "--profile", "raw-local-receiver"], output, error);

        var config = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("CAO_COLLECTION_PROFILE=raw-local-receiver", config);
        Assert.Contains("endpoint = \"http://127.0.0.1:4319/v1/traces\"", config);
        Assert.Contains("protocol = \"binary\"", config);
        Assert.DoesNotContain("Authorization = \"Basic", config);
        Assert.DoesNotContain("x-langfuse-ingestion-version", config);
        Assert.DoesNotContain("<langfuse-host>", config);
        Assert.DoesNotContain("<collector-host>", config);
    }

    [Fact]
    public void Run_ServeRawLocalReceiver_RejectsNonLoopbackUrlBeforeBinding()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["serve-raw-local-receiver", "--url", "http://0.0.0.0:4319"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("serve-raw-local-receiver only allows localhost, 127.0.0.1, or ::1", error.ToString());
    }

    [Fact]
    public void Run_VsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("github.copilot.chat.otel.otlpEndpoint", output.ToString());
        Assert.Contains("http://localhost:3000/api/public/otel", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseVsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("http://localhost:3000/api/public/otel", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorVsCodeSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-vscode-settings"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("http://localhost:4318", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", output.ToString());
        Assert.Contains("$env:OTEL_RESOURCE_ATTRIBUTES", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseCopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorCopilotCliEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-copilot-cli-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:4318\"", output.ToString());
        Assert.DoesNotContain("Authorization=Basic", output.ToString());
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_HEADERS=\"Authorization=Basic $auth,x-langfuse-ingestion-version=4\"", output.ToString());
        Assert.Contains("client.kind=vscode-copilot-chat", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_LangfuseVsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["langfuse-vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:3000/api/public/otel\"", output.ToString());
        Assert.Contains("$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"http://localhost:3000/api/public/otel/v1/traces\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CollectorVsCodeEnv_WritesScriptToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["collector-vscode-env"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("$env:COPILOT_OTEL_ENDPOINT=\"http://localhost:4318\"", output.ToString());
        Assert.DoesNotContain("Authorization=Basic", output.ToString());
        Assert.Contains("Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeFileSettings_WritesSettingsToOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-file-settings", "tmp/copilot-chat-otel.jsonl"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"github.copilot.chat.otel.exporterType\": \"file\"", output.ToString());
        Assert.Contains("\"github.copilot.chat.otel.outfile\": \"tmp/copilot-chat-otel.jsonl\"", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_VsCodeFileSettings_ReturnsNonZeroWithoutOutfile()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["vscode-file-settings"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires exactly one output file path", error.ToString());
    }

    [Fact]
    public void Run_ValidateResourceAttributes_ReturnsNonZeroForMissingAttributes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["validate-resource-attributes", "client.kind=copilot-cli"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("missing required resource attribute", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_StoresSyntheticRawOtlpPayload()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", FixturePath(), "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("Ingested 1 raw telemetry record", output.ToString());
        Assert.Equal(string.Empty, error.ToString());

        var record = Assert.Single(new RawTelemetryStore(tempDirectory.DatabasePath).ListRecords());
        Assert.Equal(RawTelemetrySources.RawOtlp, record.Source);
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Contains("\"client.kind\":\"copilot-cli\"", record.ResourceAttributesJson);
        Assert.Equal(File.ReadAllText(FixturePath()), record.PayloadJson);
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroWithoutInput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("ingest-raw requires a raw OTLP JSON file path", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroWithoutDb()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("ingest-raw requires --db", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroWhenInputPositionContainsOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", "--db", "raw-store.db"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("ingest-raw requires a raw OTLP JSON file path", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForMissingDbValue()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", FixturePath(), "--db"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("--db requires a raw store database path", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForUnknownOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", FixturePath(), "--db", "raw-store.db", "--unexpected"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown ingest-raw option '--unexpected'", error.ToString());
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForMissingInputFile()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["ingest-raw", Path.Combine(tempDirectory.Path, "missing.json"), "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input file not found", error.ToString());
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForMalformedJson()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var inputPath = tempDirectory.WriteFile("malformed.json", "{");

        var exitCode = CliApplication.Run(["ingest-raw", inputPath, "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input JSON is invalid", error.ToString());
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForNonOtlpJson()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var inputPath = tempDirectory.WriteFile("not-otlp.json", """{"traces":[]}""");

        var exitCode = CliApplication.Run(["ingest-raw", inputPath, "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("raw OTLP JSON must contain a top-level resourceSpans array", error.ToString());
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForNonObjectJson()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var inputPath = tempDirectory.WriteFile("not-object.json", "[]");

        var exitCode = CliApplication.Run(["ingest-raw", inputPath, "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("raw OTLP JSON must contain a top-level resourceSpans array", error.ToString());
        Assert.False(File.Exists(tempDirectory.DatabasePath));
    }

    [Fact]
    public void Run_IngestRaw_ReturnsNonZeroForInvalidRawStoreDatabase()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        File.WriteAllText(tempDirectory.DatabasePath, "not a sqlite database");

        var exitCode = CliApplication.Run(["ingest-raw", FixturePath(), "--db", tempDirectory.DatabasePath], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("failed to write raw store", error.ToString());
    }

    [Fact]
    public void Run_RecognizedSetupPlan_DelegatesOnceAndWritesOnlySetupJson()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        SetupOptions? dispatched = null;
        var result = CreatePlanResult();

        var exitCode = CliApplication.Run(
            ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
            output,
            error,
            options =>
            {
                Assert.Null(dispatched);
                dispatched = options;
                return result;
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(dispatched);
        Assert.Equal(SetupCommand.Plan, dispatched.Command);
        Assert.Equal("github-copilot", dispatched.Adapter);
        Assert.Equal("vscode", dispatched.Target);
        Assert.Equal("http://127.0.0.1:4320", dispatched.Endpoint);
        Assert.False(dispatched.IncludeContentCapture);
        Assert.Null(dispatched.ChangeSetId);
        Assert.Equal(SetupJson.Serialize(result) + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("plan", "--adapter", "github-copilot")]
    [InlineData("apply", "--change-set", "not-a-uuid")]
    [InlineData("rollback", "--change-set", "not-a-uuid")]
    [InlineData("status", "--adapter", "INVALID")]
    public void Run_RecognizedSetupParseFailure_WritesCommandSpecificInvalidArgumentsJson(
        string command,
        string option,
        string value)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var expected = CreateInvalidArgumentsResult(command);

        var exitCode = CliApplication.Run(
            ["setup", command, option, value],
            output,
            error,
            _ => throw new InvalidOperationException("The dispatcher must not run for invalid setup arguments."));

        Assert.Equal(2, exitCode);
        Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
        Assert.Equal(SetupCodes.InvalidArguments + "\n", error.ToString());
    }

    [Theory]
    [InlineData()]
    [InlineData("unknown")]
    public void Run_BareOrUnknownSetupVerb_WritesOnlyFixedInvalidArgumentsError(params string[] suffix)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new[] { "setup" }.Concat(suffix).ToArray();

        var exitCode = CliApplication.Run(
            args,
            output,
            error,
            _ => throw new InvalidOperationException("The dispatcher must not run for an unrecognized setup verb."));

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(SetupCodes.InvalidArguments + "\n", error.ToString());
    }

    [Fact]
    public void Run_UnknownNonSetupTopLevelCommand_PreservesLegacyHelpBehavior()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["unknown-command"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal($"error: unknown command 'unknown-command'.{Environment.NewLine}{CliHelpText.Text}{Environment.NewLine}", error.ToString());
    }

    [Theory]
    [MemberData(nameof(ProcessResults))]
    public void Run_DispatchedSetupResult_MapsEveryResultCodeToExactProcessOutput(
        SetupCommandResult result,
        int expectedExitCode)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;

        var exitCode = CliApplication.Run(
            ArgumentsFor(result.Command),
            output,
            error,
            options =>
            {
                calls++;
                Assert.Equal(result.Command, options.Command);
                return result;
            });

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, calls);
        Assert.Equal(SetupJson.Serialize(result) + Environment.NewLine, output.ToString());
        Assert.Equal(
            result.Success ? string.Empty : result.Code + "\n",
            error.ToString());
    }

    [Fact]
    public void Run_RecognizedSetupWithNoDispatcher_FailsClosedAsInternalError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var expected = new SetupCommandResult(
            SetupCommand.Plan, false, SetupCodes.InternalError, null, null, null, "github-copilot",
            [], [], [], [], false);

        var exitCode = CliApplication.Run(
            ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
            output,
            error,
            null);

        Assert.Equal(5, exitCode);
        Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
        Assert.Equal(SetupCodes.InternalError + "\n", error.ToString());
    }

    [Fact]
    public void Run_PlanAndStatusBoundaryDefects_FailClosedWithParsedAdapterContext()
    {
        var invalidPlan = new SetupCommandResult(
            SetupCommand.Plan, true, SetupCodes.InternalError, "00000000-0000-7000-8000-000000000001", null, null,
            "github-copilot", [], [], [], [], false);
        var nullCollectionsPlan = new SetupCommandResult(
            SetupCommand.Plan, false, SetupCodes.InternalError, null, null, null,
            "github-copilot", null!, [], [], [], false);
        var mismatchedStatusAdapter = CreateResult(SetupCommand.Status, true, SetupCodes.StatusReady) with { Adapter = "other-adapter" };
        var cases = new (string[] Args, Func<SetupOptions, SetupCommandResult> Dispatcher, SetupCommandResult Expected)[]
        {
            (
                ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
                _ => throw new InvalidOperationException("must not escape"),
                CreateInternalErrorResult(SetupCommand.Plan, "github-copilot", null)),
            (
                ["setup", "status", "--adapter", "github-copilot"],
                _ => null!,
                CreateInternalErrorResult(SetupCommand.Status, "github-copilot", null)),
            (
                ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
                _ => CreateResult(SetupCommand.Apply, true, SetupCodes.ApplySucceeded),
                CreateInternalErrorResult(SetupCommand.Plan, "github-copilot", null)),
            (
                ["setup", "status", "--adapter", "github-copilot"],
                _ => mismatchedStatusAdapter,
                CreateInternalErrorResult(SetupCommand.Status, "github-copilot", null)),
            (
                ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
                _ => invalidPlan,
                CreateInternalErrorResult(SetupCommand.Plan, "github-copilot", null)),
            (
                ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
                _ => nullCollectionsPlan,
                CreateInternalErrorResult(SetupCommand.Plan, "github-copilot", null)),
        };

        foreach (var testCase in cases)
        {
            using var output = new StringWriter();
            using var error = new StringWriter { NewLine = "\r\n" };

            var exitCode = CliApplication.Run(testCase.Args, output, error, testCase.Dispatcher);

            Assert.Equal(5, exitCode);
            Assert.Equal(SetupJson.Serialize(testCase.Expected) + Environment.NewLine, output.ToString());
            Assert.Equal(SetupCodes.InternalError + "\n", error.ToString());
        }
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public void Run_ApplyAndRollbackBoundaryDefects_FailClosedWithRequestedChangeSet(string command)
    {
        var parsedCommand = command == "apply" ? SetupCommand.Apply : SetupCommand.Rollback;
        var requestedId = "00000000-0000-7000-8000-000000000001";
        var otherId = "00000000-0000-7000-8000-000000000002";
        var successCode = parsedCommand == SetupCommand.Apply ? SetupCodes.ApplySucceeded : SetupCodes.RollbackSucceeded;
        var invalid = CreateResult(parsedCommand, true, SetupCodes.InternalError) with { ChangeSetId = requestedId };
        var nullCollections = new SetupCommandResult(
            parsedCommand, false, SetupCodes.InternalError, requestedId, null, null,
            null, null!, [], [], [], false);
        var cases = new Func<SetupOptions, SetupCommandResult>[]
        {
            _ => throw new InvalidOperationException("must not escape"),
            _ => null!,
            _ => CreateResult(SetupCommand.Plan, true, SetupCodes.PlanReady),
            _ => CreateResult(parsedCommand, true, successCode) with { ChangeSetId = otherId },
            _ => invalid,
            _ => nullCollections,
        };
        var expected = CreateInternalErrorResult(parsedCommand, null, requestedId);

        foreach (var dispatcher in cases)
        {
            using var output = new StringWriter();
            using var error = new StringWriter { NewLine = "\r\n" };

            var exitCode = CliApplication.Run(
                ["setup", command, "--change-set", requestedId],
                output,
                error,
                dispatcher);

            Assert.Equal(5, exitCode);
            Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
            Assert.Equal(SetupCodes.InternalError + "\n", error.ToString());
        }
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public void Run_ContextMatchingApplyAndRollbackResult_ForwardsUnchanged(string command)
    {
        var parsedCommand = command == "apply" ? SetupCommand.Apply : SetupCommand.Rollback;
        var code = parsedCommand == SetupCommand.Apply ? SetupCodes.ApplySucceeded : SetupCodes.RollbackSucceeded;
        var result = CreateResult(parsedCommand, true, code);
        using var output = new StringWriter();
        using var error = new StringWriter { NewLine = "\r\n" };

        var exitCode = CliApplication.Run(
            ["setup", command, "--change-set", result.ChangeSetId!],
            output,
            error,
            _ => result);

        Assert.Equal(0, exitCode);
        Assert.Equal(SetupJson.Serialize(result) + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [MemberData(nameof(ChangeSetParseFailures))]
    public void Run_ChangeSetParseFailure_RetainsOnlyOneUnambiguousCanonicalRequestedId(string[] args, string? expectedChangeSetId)
    {
        using var output = new StringWriter();
        using var error = new StringWriter { NewLine = "\r\n" };
        var command = args[1] == "apply" ? SetupCommand.Apply : SetupCommand.Rollback;
        var expected = new SetupCommandResult(
            command, false, SetupCodes.InvalidArguments, expectedChangeSetId, null, null, null,
            [], [], [], [], false);

        var exitCode = CliApplication.Run(
            args,
            output,
            error,
            _ => throw new InvalidOperationException("The dispatcher must not run for invalid setup arguments."));

        Assert.Equal(2, exitCode);
        Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
        Assert.Equal(SetupCodes.InvalidArguments + "\n", error.ToString());
    }

    [Theory]
    [MemberData(nameof(ParsedSetupCommands))]
    public void Run_ParsedSetupWithoutDispatcher_SerializesKnownSafeContextualFallback(
        string[] args,
        SetupCommand command,
        string? adapter,
        string? changeSetId)
    {
        using var output = new StringWriter();
        using var error = new StringWriter { NewLine = "\r\n" };
        var expected = CreateInternalErrorResult(command, adapter, changeSetId);

        var exitCode = CliApplication.Run(args, output, error, null);

        Assert.Equal(5, exitCode);
        Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
        Assert.Equal(SetupCodes.InternalError + "\n", error.ToString());
    }

    [Fact]
    public void Run_MalformedDispatcherResult_SerializesFallbackBeforeWritingAnyStdout()
    {
        using var output = new SetupOutputWriter();
        using var error = new StringWriter { NewLine = "\r\n" };
        var expected = CreateInternalErrorResult(SetupCommand.Plan, "github-copilot", null);
        var malformed = new SetupCommandResult(
            SetupCommand.Plan, false, SetupCodes.InternalError, null, null, null,
            "github-copilot", null!, [], [], [], false);

        var exitCode = CliApplication.Run(
            ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
            output,
            error,
            _ => malformed);

        Assert.Equal(5, exitCode);
        Assert.Equal(1, output.WriteLineCalls);
        Assert.Equal(SetupJson.Serialize(expected) + Environment.NewLine, output.ToString());
        Assert.Equal(SetupCodes.InternalError + "\n", error.ToString());
    }

    [Fact]
    public void HelpText_ListsAllSetupCommandsAndAdaptersExactly()
    {
        Assert.Contains("  config-cli setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture]", CliHelpText.Text);
        Assert.Contains("  config-cli setup plan --adapter claude-code --target <cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture] [--allow-wsl2-routing]", CliHelpText.Text);
        Assert.Contains("  config-cli setup apply --change-set <uuid-v7>", CliHelpText.Text);
        Assert.Contains("  config-cli setup rollback --change-set <uuid-v7>", CliHelpText.Text);
        Assert.Contains("  config-cli setup status [--adapter <id>]", CliHelpText.Text);
    }

    public static TheoryData<SetupCommandResult, int> ProcessResults => new()
    {
        { CreateResult(SetupCommand.Plan, true, SetupCodes.PlanReady), 0 },
        { CreateResult(SetupCommand.Plan, true, SetupCodes.NoChanges), 0 },
        { CreateResult(SetupCommand.Apply, true, SetupCodes.ApplySucceeded), 0 },
        { CreateResult(SetupCommand.Rollback, true, SetupCodes.RollbackSucceeded), 0 },
        { CreateResult(SetupCommand.Status, true, SetupCodes.StatusReady), 0 },
        { CreateRecoveredResult(SetupCodes.InterruptedApplyRecovered, SetupRecoveryOperation.Apply), 0 },
        { CreateRecoveredResult(SetupCodes.InterruptedRollbackRecovered, SetupRecoveryOperation.Rollback), 0 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.InvalidArguments), 2 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.UnsupportedAdapter), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.UnsupportedTarget), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.TargetNotInstalled), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.UnsupportedVersion), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.ManagedPolicyConflict), 3 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.EnvironmentOverrideConflict), 3 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.MalformedSettings), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.PermissionDenied), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.UnsafePath), 5 },
        { CreateResult(SetupCommand.Apply, false, SetupCodes.StalePlan), 3 },
        { CreateResult(SetupCommand.Rollback, false, SetupCodes.RollbackStale), 3 },
        { CreateResult(SetupCommand.Rollback, false, SetupCodes.RollbackNotAvailable), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.PortOwnedByForeignProcess), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.EndpointUnreachable), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.HookCommandConflict), 3 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.ContentPolicyConflict), 3 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.Wsl2OptInRequired), 4 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.Wsl2RoutingUnavailable), 4 },
        { CreateResult(SetupCommand.Apply, false, SetupCodes.PartialApply), 5 },
        { CreateResult(SetupCommand.Rollback, false, SetupCodes.PartialRollback), 6 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.SetupBusy), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.RecoveryRequired), 5 },
        { CreateInterruptedRecoveryFailureResult(), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.LedgerCorrupt), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.LedgerVersionUnsupported), 5 },
        { CreateResult(SetupCommand.Plan, false, SetupCodes.InternalError), 5 },
    };

    public static TheoryData<string[], string?> ChangeSetParseFailures => new()
    {
        { ["setup", "apply", "--change-set", "00000000-0000-7000-8000-000000000001", "--unexpected"], "00000000-0000-7000-8000-000000000001" },
        { ["setup", "rollback", "--change-set", "00000000-0000-7000-8000-000000000001", "--change-set", "00000000-0000-7000-8000-000000000001"], null },
        { ["setup", "apply", "--change-set", "00000000-0000-7000-8000-000000000001", "--change-set", "not-a-uuid"], null },
        { ["setup", "apply", "--change-set", "0000000a-0000-7000-8000-000000000001".ToUpperInvariant(), "--unexpected"], null },
        { ["setup", "rollback", "--change-set"], null },
        { ["setup", "apply", "--unexpected"], null },
    };

    public static TheoryData<string[], SetupCommand, string?, string?> ParsedSetupCommands => new()
    {
        { ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"], SetupCommand.Plan, "github-copilot", null },
        { ["setup", "apply", "--change-set", "00000000-0000-7000-8000-000000000001"], SetupCommand.Apply, null, "00000000-0000-7000-8000-000000000001" },
        { ["setup", "rollback", "--change-set", "00000000-0000-7000-8000-000000000001"], SetupCommand.Rollback, null, "00000000-0000-7000-8000-000000000001" },
        { ["setup", "status", "--adapter", "github-copilot"], SetupCommand.Status, "github-copilot", null },
    };

    private static SetupCommandResult CreatePlanResult() => new(
        SetupCommand.Plan,
        true,
        SetupCodes.PlanReady,
        "00000000-0000-7000-8000-000000000001",
        null,
        null,
        "github-copilot",
        [],
        [],
        [],
        [],
        false);

    private static SetupCommandResult CreateInternalErrorResult(
        SetupCommand command,
        string? adapter,
        string? changeSetId) => new(
        command,
        false,
        SetupCodes.InternalError,
        changeSetId,
        null,
        null,
        adapter,
        [],
        [],
        [],
        [],
        false);

    private static SetupCommandResult CreateResult(SetupCommand command, bool success, string code) => new(
        command,
        success,
        code,
        command is SetupCommand.Apply or SetupCommand.Rollback ? "00000000-0000-7000-8000-000000000001" : success && command == SetupCommand.Plan ? "00000000-0000-7000-8000-000000000001" : null,
        null,
        null,
        command == SetupCommand.Status ? null : "github-copilot",
        [],
        [],
        [],
        [],
        false);

    private static SetupCommandResult CreateRecoveredResult(string code, SetupRecoveryOperation operation) => new(
        SetupCommand.Status,
        true,
        code,
        null,
        "00000000-0000-7000-8000-000000000001",
        operation,
        null,
        [],
        [],
        [],
        [SetupCodes.RerunRequestedSetupCommand],
        false);

    private static SetupCommandResult CreateInterruptedRecoveryFailureResult() => new(
        SetupCommand.Plan,
        false,
        SetupCodes.InterruptedRecoveryFailed,
        null,
        "00000000-0000-7000-8000-000000000001",
        SetupRecoveryOperation.Apply,
        "github-copilot",
        [],
        [],
        [],
        [],
        false);

    private static string[] ArgumentsFor(SetupCommand command) => command switch
    {
        SetupCommand.Plan => ["setup", "plan", "--adapter", "github-copilot", "--target", "vscode"],
        SetupCommand.Apply => ["setup", "apply", "--change-set", "00000000-0000-7000-8000-000000000001"],
        SetupCommand.Rollback => ["setup", "rollback", "--change-set", "00000000-0000-7000-8000-000000000001"],
        SetupCommand.Status => ["setup", "status"],
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static SetupCommandResult CreateInvalidArgumentsResult(string command) => new(
        command switch
        {
            "plan" => SetupCommand.Plan,
            "apply" => SetupCommand.Apply,
            "rollback" => SetupCommand.Rollback,
            "status" => SetupCommand.Status,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        },
        false,
        SetupCodes.InvalidArguments,
        null,
        null,
        null,
        null,
        [],
        [],
        [],
        [],
        false);

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "raw-otlp.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "raw-store.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public string WriteFile(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class SetupOutputWriter : StringWriter
    {
        public int WriteLineCalls { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteLineCalls++;
            base.WriteLine(value);
        }
    }
}

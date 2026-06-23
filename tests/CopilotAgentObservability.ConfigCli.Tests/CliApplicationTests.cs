using CopilotAgentObservability.ConfigCli;

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
}

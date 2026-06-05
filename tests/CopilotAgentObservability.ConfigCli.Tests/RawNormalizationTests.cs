using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawNormalizationTests
{
    [Fact]
    public void NormalizeRaw_WritesCsvAndJsonOutputsFromRawJson()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "measurements.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["normalize-raw", FixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Normalized 1 raw measurement row(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public void NormalizeRaw_MapsRawJsonToMeasurementJson()
    {
        using var tempDirectory = new TempDirectory();
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var exitCode = CliApplication.Run(
            ["normalize-raw", FixturePath(), "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var jsonText = File.ReadAllText(jsonPath);
        using var document = JsonDocument.Parse(jsonText);
        var rows = document.RootElement;
        var first = Assert.Single(rows.EnumerateArray());

        Assert.Equal("11111111111111111111111111111111", first.GetProperty("trace_id").GetString());
        Assert.Equal("baseline", first.GetProperty("experiment_id").GetString());
        Assert.Equal("copilot-cli", first.GetProperty("client_kind").GetString());
        Assert.Equal("maint-bug-001", first.GetProperty("task_id").GetString());
        Assert.Equal("bug-investigation", first.GetProperty("task_category").GetString());
        Assert.Equal(3, first.GetProperty("task_run_index").GetInt32());
        Assert.Equal("baseline", first.GetProperty("experiment_condition").GetString());
        Assert.Equal("v1", first.GetProperty("prompt_version").GetString());
        Assert.Equal("default", first.GetProperty("agent_variant").GetString());
        Assert.Equal("synthetic-dotnet-fixture-v1", first.GetProperty("repo_snapshot").GetString());
        Assert.Equal(10, first.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, first.GetProperty("output_tokens").GetInt32());
        Assert.Equal(15, first.GetProperty("total_tokens").GetInt32());
        Assert.Equal(1, first.GetProperty("turn_count").GetInt32());
        Assert.Equal(1, first.GetProperty("tool_call_count").GetInt32());
        Assert.Equal(3000, first.GetProperty("duration_ms").GetInt32());
        Assert.Equal(2, first.GetProperty("error_count").GetInt32());
        Assert.Equal("not-evaluated", first.GetProperty("success_status").GetString());

        var unknownSpan = Assert.Single(first.GetProperty("unknown_spans_json").EnumerateArray());
        Assert.Equal("5555555555555555", unknownSpan.GetProperty("id").GetString());
        Assert.Equal("copilot.experimental.span", unknownSpan.GetProperty("name").GetString());
        Assert.False(unknownSpan.TryGetProperty("attributes", out _));

        var unknownResourceAttributes = first.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes");
        Assert.Equal("synthetic-v2", unknownResourceAttributes.GetProperty("cli.wrapper.version").GetString());
        Assert.False(unknownResourceAttributes.TryGetProperty("user.id", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("user.email", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("prompt.content", out _));
        Assert.False(unknownResourceAttributes.TryGetProperty("authorization.token", out _));
        Assert.DoesNotContain("synthetic prompt resource attribute should not leak", jsonText);
        Assert.DoesNotContain("synthetic auth token should not leak", jsonText);
        Assert.DoesNotContain("synthetic unknown span prompt should not leak", jsonText);
        Assert.DoesNotContain("user@example.com", jsonText);
    }

    [Fact]
    public void NormalizeRaw_WritesCsvWithFixedColumnsAndBlankMissingValues()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("minimal.json", """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "copilot-cli" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "99999999999999999999999999999999",
                          "spanId": "aaaaaaaaaaaaaaaa",
                          "name": "invoke_agent"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        var csvPath = Path.Combine(tempDirectory.Path, "measurements.csv");

        var exitCode = CliApplication.Run(
            ["normalize-raw", inputPath, "--csv", csvPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var lines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', MeasurementOutputWriter.Columns), lines[0]);
        Assert.Contains("99999999999999999999999999999999,,copilot-cli", lines[1]);
        Assert.Contains(",,,,,,,,,,0,0,,0,not-evaluated", lines[1]);
    }

    [Fact]
    public void NormalizeRaw_ReadsRowsFromRawStoreInput()
    {
        using var tempDirectory = new TempDirectory();
        using var ingestOutput = new StringWriter();
        using var ingestError = new StringWriter();
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var ingestExitCode = CliApplication.Run(
            ["ingest-raw", FixturePath(), "--db", tempDirectory.DatabasePath],
            ingestOutput,
            ingestError);
        var normalizeExitCode = CliApplication.Run(
            ["normalize-raw", tempDirectory.DatabasePath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, ingestExitCode);
        Assert.Equal(0, normalizeExitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("11111111111111111111111111111111", row.GetProperty("trace_id").GetString());
        Assert.Equal(15, row.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void NormalizeRaw_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["normalize-raw", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    [Fact]
    public void NormalizeRaw_ReturnsNonZeroForMissingInput()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["normalize-raw", Path.Combine(tempDirectory.Path, "missing.json"), "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input file not found", error.ToString());
    }

    [Fact]
    public void NormalizeRaw_ReturnsNonZeroForInvalidJson()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var inputPath = tempDirectory.WriteFile("invalid.json", "{");

        var exitCode = CliApplication.Run(
            ["normalize-raw", inputPath, "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input JSON is invalid", error.ToString());
    }

    [Fact]
    public void NormalizeRaw_ReturnsNonZeroForInvalidRawStore()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        File.WriteAllText(tempDirectory.DatabasePath, "not a sqlite database");

        var exitCode = CliApplication.Run(
            ["normalize-raw", tempDirectory.DatabasePath, "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("failed to read raw store", error.ToString());
    }

    [Theory]
    [InlineData("--csv")]
    [InlineData("--json")]
    public void NormalizeRaw_ReturnsNonZeroWhenOutputOptionConsumesAnotherOption(string outputOption)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["normalize-raw", FixturePath(), outputOption, "--json"],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{outputOption} requires an output file path", error.ToString());
    }

    [Fact]
    public void NormalizeRaw_ReturnsNonZeroForUnknownOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["normalize-raw", FixturePath(), "--yaml", "out.yaml"],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown normalize-raw option '--yaml'", error.ToString());
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "raw-otlp.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m4-normalize-{Guid.NewGuid():N}");
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

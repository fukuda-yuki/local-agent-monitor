using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class MeasurementAggregationTests
{
    [Fact]
    public void AggregateMeasurements_WritesCsvAndJsonOutputs()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "measurements.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", FixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Aggregated 5 measurement row(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public void AggregateMeasurements_MapsFixtureToMeasurementJson()
    {
        using var tempDirectory = new TempDirectory();
        var jsonPath = Path.Combine(tempDirectory.Path, "measurements.json");

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", FixturePath(), "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var jsonText = File.ReadAllText(jsonPath);
        using var document = JsonDocument.Parse(jsonText);
        var rows = document.RootElement;
        Assert.Equal(5, rows.GetArrayLength());

        var first = rows[0];
        Assert.Equal("trace-001", first.GetProperty("trace_id").GetString());
        Assert.Equal("baseline", first.GetProperty("experiment_id").GetString());
        Assert.Equal("copilot-cli", first.GetProperty("client_kind").GetString());
        Assert.Equal("maint-bug-001", first.GetProperty("task_id").GetString());
        Assert.Equal("bug-investigation", first.GetProperty("task_category").GetString());
        Assert.Equal(1, first.GetProperty("task_run_index").GetInt32());
        Assert.Equal("v1", first.GetProperty("prompt_version").GetString());
        Assert.Equal("default", first.GetProperty("agent_variant").GetString());
        Assert.Equal("synthetic-dotnet-fixture-v1", first.GetProperty("repo_snapshot").GetString());
        Assert.Equal(100, first.GetProperty("input_tokens").GetInt32());
        Assert.Equal(40, first.GetProperty("output_tokens").GetInt32());
        Assert.Equal(140, first.GetProperty("total_tokens").GetInt32());
        Assert.Equal(1, first.GetProperty("turn_count").GetInt32());
        Assert.Equal(3, first.GetProperty("tool_call_count").GetInt32());
        Assert.Equal(1234, first.GetProperty("duration_ms").GetInt32());
        Assert.Equal(4, first.GetProperty("error_count").GetInt32());
        Assert.Equal("not-evaluated", first.GetProperty("success_status").GetString());
        var unknownSpanNames = first.GetProperty("unknown_spans_json")
            .EnumerateArray()
            .Select(span => span.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "copilot.experimental.span",
                "event_error",
                "status_message_error",
                "error_property_event",
                "non_error_event",
            ],
            unknownSpanNames);
        Assert.All(first.GetProperty("unknown_spans_json").EnumerateArray(), span => Assert.False(span.TryGetProperty("attributes", out _)));
        Assert.Equal(
            "synthetic-v1",
            first.GetProperty("unknown_attributes_json")
                .GetProperty("resourceAttributes")
                .GetProperty("cli.wrapper.version")
                .GetString());
        Assert.False(first.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes").TryGetProperty("prompt.content", out _));
        Assert.False(first.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes").TryGetProperty("tool.arguments", out _));
        Assert.False(first.GetProperty("unknown_attributes_json").GetProperty("resourceAttributes").TryGetProperty("authorization.token", out _));
        Assert.False(first.GetProperty("unknown_attributes_json").TryGetProperty("metadata", out _));
        Assert.False(first.GetProperty("unknown_attributes_json").TryGetProperty("prompt", out _));
        Assert.False(first.GetProperty("unknown_attributes_json").TryGetProperty("source", out _));
        Assert.DoesNotContain("synthetic prompt resource attribute should not leak", jsonText);
        Assert.DoesNotContain("synthetic tool arguments should not leak", jsonText);
        Assert.DoesNotContain("synthetic auth token should not leak", jsonText);
        Assert.DoesNotContain("synthetic unknown span prompt should not leak", jsonText);
        Assert.DoesNotContain("synthetic unknown span tool arguments should not leak", jsonText);

        var second = rows[1];
        Assert.Equal("trace-002", second.GetProperty("trace_id").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("experiment_id").ValueKind);
        Assert.Equal("vscode-copilot-chat", second.GetProperty("client_kind").GetString());
        Assert.Equal(25, second.GetProperty("total_tokens").GetInt32());
        Assert.Equal(2500, second.GetProperty("duration_ms").GetInt32());
        Assert.Equal(0, second.GetProperty("turn_count").GetInt32());
        Assert.Equal(0, second.GetProperty("tool_call_count").GetInt32());
        Assert.Equal(0, second.GetProperty("error_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("unknown_spans_json").ValueKind);

        var third = rows[2];
        Assert.Equal("trace-003", third.GetProperty("trace_id").GetString());
        Assert.Equal(12, third.GetProperty("total_tokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("turn_count").ValueKind);
        Assert.Equal(JsonValueKind.Null, third.GetProperty("tool_call_count").ValueKind);
        Assert.Equal(JsonValueKind.Null, third.GetProperty("error_count").ValueKind);

        var fourth = rows[3];
        Assert.Equal("trace-004", fourth.GetProperty("trace_id").GetString());
        Assert.Equal("vscode-copilot-chat", fourth.GetProperty("client_kind").GetString());
        Assert.Equal(2, fourth.GetProperty("turn_count").GetInt32());
        Assert.Equal(3, fourth.GetProperty("tool_call_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, fourth.GetProperty("unknown_spans_json").ValueKind);

        var fifth = rows[4];
        Assert.Equal("trace-005", fifth.GetProperty("trace_id").GetString());
        Assert.Equal(7, fifth.GetProperty("turn_count").GetInt32());
        Assert.Equal(4, fifth.GetProperty("tool_call_count").GetInt32());
    }

    [Fact]
    public void AggregateMeasurements_WritesCsvWithFixedColumnsAndBlankMissingValues()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "measurements.csv");

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", FixturePath(), "--csv", csvPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var lines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', MeasurementOutputWriter.Columns), lines[0]);
        Assert.Contains("trace-002,,vscode-copilot-chat", lines[2]);
        Assert.Contains(",12,8,25,0,0,2500,0,not-evaluated", lines[2]);
        Assert.Contains("\"[{\"\"id\"\":\"\"obs-unknown-001\"\"", lines[1]);
        Assert.Contains("trace-003,baseline,copilot-cli", lines[3]);
        Assert.Contains(",5,7,12,,,50,,not-evaluated", lines[3]);
    }

    [Fact]
    public void AggregateMeasurements_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["aggregate-measurements", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    [Fact]
    public void AggregateMeasurements_ReturnsNonZeroForMissingInput()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", Path.Combine(tempDirectory.Path, "missing.json"), "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input file not found", error.ToString());
    }

    [Fact]
    public void AggregateMeasurements_ReturnsNonZeroForMissingInputParentDirectory()
    {
        using var tempDirectory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            [
                "aggregate-measurements",
                Path.Combine(tempDirectory.Path, "missing-directory", "missing.json"),
                "--json",
                Path.Combine(tempDirectory.Path, "out.json")
            ],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input file not found", error.ToString());
    }

    [Fact]
    public void AggregateMeasurements_ReturnsNonZeroForInvalidJson()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        File.WriteAllText(inputPath, "{ invalid json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", inputPath, "--json", Path.Combine(tempDirectory.Path, "out.json")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("input JSON is invalid", error.ToString());
    }

    [Theory]
    [InlineData("--csv")]
    [InlineData("--json")]
    public void AggregateMeasurements_ReturnsNonZeroWhenOutputOptionConsumesAnotherOption(string outputOption)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", FixturePath(), outputOption, "--json"],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{outputOption} requires an output file path", error.ToString());
    }

    [Fact]
    public void AggregateMeasurements_ReturnsNonZeroForUnknownOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["aggregate-measurements", FixturePath(), "--yaml", "out.yaml"],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown aggregate-measurements option '--yaml'", error.ToString());
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "langfuse-legacy-traces.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m15-aggregation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

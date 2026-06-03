using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class DiagnosisValidationTests
{
    [Fact]
    public void ValidateDiagnoses_WritesCsvAndJsonOutputs()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "diagnoses.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", FixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Validated 3 diagnosis record(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public void ValidateDiagnoses_MapsFixtureToDiagnosisJson()
    {
        using var tempDirectory = new TempDirectory();
        var jsonPath = Path.Combine(tempDirectory.Path, "diagnoses.json");

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", FixturePath(), "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var jsonText = File.ReadAllText(jsonPath);
        using var document = JsonDocument.Parse(jsonText);
        var rows = document.RootElement;
        Assert.Equal(3, rows.GetArrayLength());

        var first = rows[0];
        Assert.Equal("trace-diagnosis-001", first.GetProperty("trace_id").GetString());
        Assert.Equal("maint-bug-001", first.GetProperty("task_id").GetString());
        Assert.Equal("bug-investigation", first.GetProperty("task_category").GetString());
        Assert.Equal("copilot-cli", first.GetProperty("client_kind").GetString());
        Assert.Equal(1, first.GetProperty("task_run_index").GetInt32());
        Assert.Equal("F-RUBRIC", first.GetProperty("failure_category_id").GetString());
        Assert.Equal("AP-BUG-CAUSE-FIX-CONFLATION", first.GetProperty("anti_pattern_id").GetString());
        Assert.Equal("major", first.GetProperty("severity").GetString());
        Assert.Equal("eval", first.GetProperty("recommended_improvement_target").GetString());
        Assert.Equal("needs-human-review", first.GetProperty("review_status").GetString());

        var second = rows[1];
        Assert.Equal("trace-diagnosis-001", second.GetProperty("trace_id").GetString());
        Assert.Equal("F-TRACE", second.GetProperty("failure_category_id").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("anti_pattern_id").ValueKind);
        Assert.Equal("minor", second.GetProperty("severity").GetString());
        Assert.Equal("accepted-for-proposal", second.GetProperty("review_status").GetString());

        var third = rows[2];
        Assert.Equal("trace-diagnosis-002", third.GetProperty("trace_id").GetString());
        Assert.Equal("F-COMPARISON", third.GetProperty("failure_category_id").GetString());
        Assert.Equal("AP-CONFOUND", third.GetProperty("anti_pattern_id").GetString());
        Assert.Equal("blocking", third.GetProperty("severity").GetString());
        Assert.Equal("rejected", third.GetProperty("review_status").GetString());
    }

    [Fact]
    public void ValidateDiagnoses_AcceptsTopLevelJsonArray()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnoses-array.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        File.WriteAllText(
            inputPath,
            """
            [
              {
                "trace_id": "trace-array-001",
                "task_id": "maint-test-001",
                "task_category": "test-generation",
                "client_kind": "copilot-cli",
                "failure_category_id": "F-TASK",
                "anti_pattern_id": "AP-TEST-MISSING-EDGE-CLASS",
                "severity": "major",
                "evidence_summary": "Boundary scenario was absent from the sanitized evaluation note.",
                "recommended_improvement_target": "eval",
                "review_status": "needs-human-review"
              }
            ]
            """);

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ValidateDiagnoses_AcceptsCsvWithFixedHeader()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "diagnoses.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "diagnoses.json");

        File.WriteAllLines(
            csvPath,
            [
                string.Join(',', DiagnosisOutputWriter.Columns),
                "trace-csv-001,maint-refactor-001,refactoring,vscode-copilot-chat,,baseline,baseline,v1,baseline,1,F-SPEC,AP-REF-CONTRACT-DRIFT,blocking,External contract drift was found in sanitized notes.,instruction,needs-human-review",
            ]);

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", csvPath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ValidateDiagnoses_AllowsSafeComparisonMetadataEvidence()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "safe-comparison.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var row = ValidRow();
        row["failure_category_id"] = "F-COMPARISON";
        row["anti_pattern_id"] = "AP-CONFOUND";
        row["evidence_summary"] = "prompt.version and agent.variant differed across compared runs.";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ValidateDiagnoses_AllowsSafeTokenMetricEvidence()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "safe-token-metric.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var row = ValidRow();
        row["failure_category_id"] = "F-MEASURE";
        row["anti_pattern_id"] = "AP-SCHEMA-DRIFT";
        row["evidence_summary"] = "total_tokens and input_tokens differed from the measurement row.";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("failure_category_id", "F-UNKNOWN", "failure_category_id 'F-UNKNOWN' is not allowed")]
    [InlineData("anti_pattern_id", "AP-UNKNOWN", "anti_pattern_id 'AP-UNKNOWN' is not allowed")]
    [InlineData("severity", "critical", "severity 'critical' is not allowed")]
    [InlineData("evidence_summary", "", "evidence_summary is required")]
    [InlineData("evidence_summary", "raw prompt included", "evidence_summary appears to contain raw content")]
    [InlineData("evidence_summary", "Authorization Basic cHVibGljOnNlY3JldA==", "evidence_summary appears to contain raw content")]
    [InlineData("evidence_summary", "cHVibGljOnNlY3JldA==", "evidence_summary appears to contain raw content")]
    [InlineData("evidence_summary", "access token was copied into the note", "evidence_summary appears to contain raw content")]
    [InlineData("evidence_summary", "reviewed by jane.doe@contoso.com", "evidence_summary appears to contain raw content")]
    [InlineData("evidence_summary", "line one\nline two", "evidence_summary appears to contain raw content")]
    public void ValidateDiagnoses_ReturnsNonZeroForInvalidRecord(string propertyName, string value, string expectedError)
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var row = ValidRow();
        row[propertyName] = value;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedError, error.ToString());
    }

    [Fact]
    public void ValidateDiagnoses_ReturnsNonZeroForInvalidJsonTaskRunIndex()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid-task-run-index.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var row = ValidRow();
        row["task_run_index"] = "abc";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("task_run_index", error.ToString());
        Assert.Contains("integer", error.ToString());
    }

    [Fact]
    public void ValidateDiagnoses_ReturnsNonZeroForInvalidCsvTaskRunIndex()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "invalid-task-run-index.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "diagnoses.json");

        File.WriteAllLines(
            csvPath,
            [
                string.Join(',', DiagnosisOutputWriter.Columns),
                "trace-csv-001,maint-refactor-001,refactoring,vscode-copilot-chat,,baseline,baseline,v1,baseline,abc,F-SPEC,AP-REF-CONTRACT-DRIFT,blocking,External contract drift was found in sanitized notes.,instruction,needs-human-review",
            ]);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("task_run_index", error.ToString());
        Assert.Contains("integer", error.ToString());
    }

    [Fact]
    public void ValidateDiagnoses_ReturnsNonZeroForNonStringJsonField()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "non-string.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        File.WriteAllText(
            inputPath,
            """
            [
              {
                "trace_id": "trace-non-string-001",
                "failure_category_id": "F-COMM",
                "severity": "minor",
                "evidence_summary": { "summary": "structured payload should not pass" },
                "recommended_improvement_target": "eval",
                "review_status": "needs-human-review"
              }
            ]
            """);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("evidence_summary", error.ToString());
        Assert.Contains("string or null", error.ToString());
    }

    [Fact]
    public void ValidateDiagnoses_ReturnsNonZeroForFailureTypeColumn()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var row = ValidRow();
        row["failure_type"] = "trace-missing";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["validate-diagnoses", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("failure_type is an M17 run exclusion field", error.ToString());
    }

    [Fact]
    public void ValidateDiagnoses_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["validate-diagnoses", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    private static Dictionary<string, object?> ValidRow()
    {
        return new Dictionary<string, object?>
        {
            ["trace_id"] = "trace-valid-001",
            ["task_id"] = "maint-review-001",
            ["task_category"] = "code-review",
            ["client_kind"] = "copilot-cli",
            ["comparison_id"] = null,
            ["experiment_id"] = "baseline",
            ["experiment_condition"] = "baseline",
            ["prompt_version"] = "v1",
            ["agent_variant"] = "baseline",
            ["task_run_index"] = 1,
            ["failure_category_id"] = "F-COMM",
            ["anti_pattern_id"] = "AP-UNCLEAR-SEVERITY",
            ["severity"] = "minor",
            ["evidence_summary"] = "Severity and residual risk were not separated in the sanitized note.",
            ["recommended_improvement_target"] = "eval",
            ["review_status"] = "needs-human-review",
        };
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "diagnoses.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m24-diagnosis-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

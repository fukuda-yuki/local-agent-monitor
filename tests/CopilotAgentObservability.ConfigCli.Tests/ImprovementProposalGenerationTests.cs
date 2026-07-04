using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class ImprovementProposalGenerationTests
{
    [Fact]
    public void GenerateImprovementProposals_WritesCsvAndJsonOutputs()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "proposals.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "proposals.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", FixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Generated 1 improvement proposal record(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));

        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', ImprovementProposalOutputWriter.Columns), csvLines[0]);
        Assert.Contains("proposal-0001,2,trace-diagnosis-001", csvLines[1]);
        Assert.Contains("needs-human-review", csvLines[1]);
    }

    [Fact]
    public void GenerateImprovementProposals_MapsAcceptedDiagnosisToProposalJson()
    {
        using var tempDirectory = new TempDirectory();
        var jsonPath = Path.Combine(tempDirectory.Path, "proposals.json");

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", FixturePath(), "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var rows = document.RootElement;
        Assert.Equal(1, rows.GetArrayLength());

        var proposal = rows[0];
        Assert.Equal("proposal-0001", proposal.GetProperty("proposal_id").GetString());
        Assert.Equal(2, proposal.GetProperty("source_diagnosis_index").GetInt32());
        Assert.Equal("trace-diagnosis-001", proposal.GetProperty("trace_id").GetString());
        Assert.Equal("F-TRACE", proposal.GetProperty("failure_category_id").GetString());
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("anti_pattern_id").ValueKind);
        Assert.Equal("minor", proposal.GetProperty("severity").GetString());
        Assert.Equal("workflow", proposal.GetProperty("improvement_target").GetString());
        Assert.Equal("needs-human-review", proposal.GetProperty("human_review_status").GetString());
        Assert.Contains("F-TRACE", proposal.GetProperty("proposal_title").GetString());
        Assert.Contains("sanitized evidence summary", proposal.GetProperty("proposed_change").GetString());
        Assert.Contains("without automatic adoption", proposal.GetProperty("acceptance_check").GetString());
    }

    [Fact]
    public void GenerateImprovementProposals_SequencesMultipleAcceptedDiagnosesInOutputOrder()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        File.WriteAllText(
            inputPath,
            JsonSerializer.Serialize(
                new[]
                {
                    ValidRow("accepted-for-proposal"),
                    ValidRow("needs-human-review"),
                    ValidRow("accepted-for-proposal"),
                }));

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement;
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("proposal-0001", rows[0].GetProperty("proposal_id").GetString());
        Assert.Equal(1, rows[0].GetProperty("source_diagnosis_index").GetInt32());
        Assert.Equal("proposal-0002", rows[1].GetProperty("proposal_id").GetString());
        Assert.Equal(3, rows[1].GetProperty("source_diagnosis_index").GetInt32());
    }

    [Fact]
    public void GenerateImprovementProposals_AcceptsTopLevelJsonArray()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnoses-array.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { ValidRow("accepted-for-proposal") }));

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void GenerateImprovementProposals_AcceptsCsvWithFixedDiagnosisHeader()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "diagnoses.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "proposals.json");

        File.WriteAllLines(
            csvPath,
            [
                string.Join(',', DiagnosisOutputWriter.Columns),
                "trace-csv-001,maint-refactor-001,refactoring,vscode-copilot-chat,,baseline,baseline,v1,baseline,1,F-SPEC,AP-REF-CONTRACT-DRIFT,blocking,External contract drift was found in sanitized notes.,instruction,accepted-for-proposal",
            ]);

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", csvPath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal("instruction", document.RootElement[0].GetProperty("improvement_target").GetString());
        Assert.Equal("AP-REF-CONTRACT-DRIFT", document.RootElement[0].GetProperty("anti_pattern_id").GetString());
    }

    [Fact]
    public void GenerateImprovementProposals_ReturnsEmptyOutputWhenNoDiagnosisIsAccepted()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        File.WriteAllText(
            inputPath,
            JsonSerializer.Serialize(new[] { ValidRow("needs-human-review"), ValidRow("rejected") }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Generated 0 improvement proposal record(s).", output.ToString());

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void GenerateImprovementProposals_ReturnsNonZeroForUnsafeCopiedMetadata()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "unsafe-metadata.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        var row = ValidRow("accepted-for-proposal");
        row["trace_id"] = "reviewed-by-jane.doe@contoso.com";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("proposal field 'trace_id' appears to contain raw content", error.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("evidence_summary", "raw prompt included", "evidence_summary appears to contain raw content")]
    [InlineData("failure_category_id", "F-UNKNOWN", "failure_category_id 'F-UNKNOWN' is not allowed")]
    public void GenerateImprovementProposals_ValidatesDiagnosisBeforeGenerating(string propertyName, string value, string expectedError)
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        var row = ValidRow("accepted-for-proposal");
        row[propertyName] = value;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedError, error.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void GenerateImprovementProposals_ReturnsNonZeroForFailureTypeColumn()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposals.json");
        var row = ValidRow("accepted-for-proposal");
        row["failure_type"] = "trace-missing";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { row }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("failure_type is an M17 run exclusion field", error.ToString());
    }

    [Fact]
    public void GenerateImprovementProposals_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-improvement-proposals", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    private static Dictionary<string, object?> ValidRow(string reviewStatus)
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
            ["review_status"] = reviewStatus,
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m25-proposals-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

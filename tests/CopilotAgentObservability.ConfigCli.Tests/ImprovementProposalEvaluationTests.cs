using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class ImprovementProposalEvaluationTests
{
    [Fact]
    public void EvaluateImprovementProposals_WritesCsvAndJsonOutputs()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", FixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Evaluated 1 improvement proposal record(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));

        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', ProposalEvaluationOutputWriter.Columns), csvLines[0]);
        Assert.Contains("proposal-0001,2,trace-diagnosis-001", csvLines[1]);
        Assert.Contains("ready-for-human-approval", csvLines[1]);
    }

    [Fact]
    public void EvaluateImprovementProposals_MapsProposalToReadyEvaluationJson()
    {
        using var tempDirectory = new TempDirectory();
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", FixturePath(), "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement;
        Assert.Equal(1, rows.GetArrayLength());

        var evaluation = rows[0];
        Assert.Equal("proposal-0001", evaluation.GetProperty("proposal_id").GetString());
        Assert.Equal(2, evaluation.GetProperty("source_diagnosis_index").GetInt32());
        Assert.Equal("trace-diagnosis-001", evaluation.GetProperty("trace_id").GetString());
        Assert.Equal("F-TRACE", evaluation.GetProperty("failure_category_id").GetString());
        Assert.Equal("workflow", evaluation.GetProperty("improvement_target").GetString());
        Assert.Equal("ready-for-human-approval", evaluation.GetProperty("proposal_evaluation_status").GetString());
        Assert.Contains("human review", evaluation.GetProperty("evaluator_findings").GetString());
        Assert.Contains("Confirm target", evaluation.GetProperty("required_human_checks").GetString());
    }

    [Fact]
    public void EvaluateImprovementProposals_AcceptsTopLevelJsonArray()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "proposals-array.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { ValidProposal() }));

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void EvaluateImprovementProposals_AcceptsCsvWithFixedProposalHeader()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "proposals.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");

        File.WriteAllLines(
            csvPath,
            [
                string.Join(',', ImprovementProposalOutputWriter.Columns),
                "proposal-csv-001,1,trace-csv-001,maint-review-001,code-review,vscode-copilot-chat,,baseline,baseline,v1,baseline,1,F-COMM,AP-UNCLEAR-SEVERITY,minor,eval,Severity and residual risk were not separated.,Review eval improvement for F-COMM / AP-UNCLEAR-SEVERITY,A minor diagnosis was accepted for proposal.,Prepare a human-reviewed eval improvement using only sanitized evidence.,A human reviewer can confirm the eval proposal boundaries.,needs-human-review",
            ]);

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", csvPath, "--json", jsonPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal("proposal-csv-001", document.RootElement[0].GetProperty("proposal_id").GetString());
        Assert.Equal("ready-for-human-approval", document.RootElement[0].GetProperty("proposal_evaluation_status").GetString());
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsNeedsRevisionWhenReviewContextIsIncomplete()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "needs-revision.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal["task_category"] = null;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("needs-revision", document.RootElement[0].GetProperty("proposal_evaluation_status").GetString());
        Assert.Contains("review context", document.RootElement[0].GetProperty("evaluator_findings").GetString());
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsBlockedForOutOfScopeAutomation()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "blocked.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal["proposed_change"] = "Prepare a human-reviewed workflow proposal, then generate patch content for the repository.";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("blocked", document.RootElement[0].GetProperty("proposal_evaluation_status").GetString());
        Assert.Contains("out-of-scope", document.RootElement[0].GetProperty("evaluator_findings").GetString());
    }

    [Theory]
    [InlineData("After human review, adopt this proposal and implement the improvement in repositories.")]
    [InlineData("After review, automatically implement the proposal.")]
    [InlineData("After review, edit repositories to apply the suggested workflow.")]
    [InlineData("After approval, create PR with the proposed change.")]
    [InlineData("After approval, raise a pull request with the proposed change.")]
    [InlineData("After approval, commit changes and push to remote.")]
    [InlineData("After comparison review, select winner for the variant.")]
    [InlineData("After review, evaluate improvement effectiveness for this proposal.")]
    [InlineData("After review, make repository changes for this proposal.")]
    public void EvaluateImprovementProposals_ReturnsBlockedForOutOfScopeActionVariants(string proposedChange)
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "blocked-variant.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal["proposed_change"] = proposedChange;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("blocked", document.RootElement[0].GetProperty("proposal_evaluation_status").GetString());
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsEmptyOutputForEmptyInput()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "empty.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        File.WriteAllText(inputPath, "[]");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Evaluated 0 improvement proposal record(s).", output.ToString());

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsNonZeroForUnsafeProposalContent()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "unsafe.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal["evidence_summary"] = "raw prompt included";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("proposal field 'evidence_summary' appears to contain raw content", error.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("human_review_status", "accepted", "human_review_status must be 'needs-human-review'")]
    [InlineData("severity", "critical", "severity 'critical' is not allowed")]
    [InlineData("improvement_target", "repository", "improvement_target 'repository' is not allowed")]
    [InlineData("failure_category_id", "F-UNKNOWN", "failure_category_id 'F-UNKNOWN' is not allowed")]
    [InlineData("anti_pattern_id", "AP-UNKNOWN", "anti_pattern_id 'AP-UNKNOWN' is not allowed")]
    public void EvaluateImprovementProposals_ValidatesProposalEnums(string propertyName, string value, string expectedError)
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "invalid.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal[propertyName] = value;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedError, error.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsNonZeroForUnknownColumn()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "unknown-column.json");
        var outputPath = Path.Combine(tempDirectory.Path, "proposal-evaluations.json");
        var proposal = ValidProposal();
        proposal["proposal_rank"] = "1";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { proposal }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", inputPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown improvement proposal column 'proposal_rank'", error.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void EvaluateImprovementProposals_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["evaluate-improvement-proposals", FixturePath()], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    private static Dictionary<string, object?> ValidProposal()
    {
        return new Dictionary<string, object?>
        {
            ["proposal_id"] = "proposal-valid-001",
            ["source_diagnosis_index"] = 1,
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
            ["improvement_target"] = "eval",
            ["evidence_summary"] = "Severity and residual risk were not separated in the sanitized note.",
            ["proposal_title"] = "Review eval improvement for F-COMM / AP-UNCLEAR-SEVERITY",
            ["proposal_summary"] = "A minor diagnosis for F-COMM / AP-UNCLEAR-SEVERITY was accepted for proposal.",
            ["proposed_change"] = "Prepare a human-reviewed eval improvement using only the sanitized evidence summary.",
            ["acceptance_check"] = "A human reviewer can confirm the eval proposal boundaries.",
            ["human_review_status"] = "needs-human-review",
        };
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "proposals.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m26-proposal-evaluation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

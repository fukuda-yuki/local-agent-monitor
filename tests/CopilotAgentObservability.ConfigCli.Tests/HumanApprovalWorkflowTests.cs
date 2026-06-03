using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class HumanApprovalWorkflowTests
{
    [Fact]
    public void RecordHumanDecisions_WritesCsvAndJsonOutputs()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var csvPath = Path.Combine(tempDirectory.Path, "decisions-output.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { ValidApprovedDecision() }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Recorded 1 human decision(s).", output.ToString());
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));

        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', HumanDecisionOutputWriter.Columns), csvLines[0]);
        Assert.Contains("proposal-0001", csvLines[1]);
        Assert.Contains("approved", csvLines[1]);
    }

    [Fact]
    public void RecordHumanDecisions_MapsDecisionToJsonOutput()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { ValidApprovedDecision() }));

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement;
        Assert.Equal(1, rows.GetArrayLength());

        var decision = rows[0];
        Assert.Equal("proposal-0001", decision.GetProperty("proposal_id").GetString());
        Assert.Equal("approved", decision.GetProperty("human_decision").GetString());
        Assert.Equal("Sanitized evidence supports improvement.", decision.GetProperty("decision_rationale").GetString());
        Assert.Equal("reviewer-a", decision.GetProperty("approver_id").GetString());
    }

    [Fact]
    public void RecordHumanDecisions_AcceptsRejectedDecision()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["human_decision"] = "rejected";
        decision["decision_rationale"] = "Not relevant to current priorities.";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("rejected", document.RootElement[0].GetProperty("human_decision").GetString());
    }

    [Fact]
    public void RecordHumanDecisions_AcceptsDeferredDecision()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["human_decision"] = "deferred";
        decision["decision_rationale"] = "Need more context before deciding.";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("deferred", document.RootElement[0].GetProperty("human_decision").GetString());
    }

    [Fact]
    public void RecordHumanDecisions_RejectsApprovalOfNeedsRevisionProposal()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["proposal_id"] = "proposal-0002";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot approve proposal 'proposal-0002'", error.ToString());
        Assert.Contains("needs-revision", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_RejectsApprovalOfBlockedProposal()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["proposal_id"] = "proposal-0003";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot approve proposal 'proposal-0003'", error.ToString());
        Assert.Contains("blocked", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_AllowsRejectionOfNeedsRevisionProposal()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["proposal_id"] = "proposal-0002";
        decision["human_decision"] = "rejected";
        decision["decision_rationale"] = "Revision not worth pursuing.";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void RecordHumanDecisions_RejectsUnknownProposalId()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["proposal_id"] = "proposal-9999";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("proposal_id 'proposal-9999' not found in evaluations", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_RejectsInvalidDecisionValue()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["human_decision"] = "maybe";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("human_decision 'maybe' is not allowed", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_RejectsUnsafeContentInRationale()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["decision_rationale"] = "raw prompt included in the evidence";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("decision field 'decision_rationale' appears to contain raw content", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_RejectsUnknownColumn()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        var decision = ValidApprovedDecision();
        decision["priority"] = "high";
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { decision }));
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown human decision column 'priority'", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_ReturnsNonZeroWithoutOutputOption()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        File.WriteAllText(decisionsPath, "[]");
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    [Fact]
    public void RecordHumanDecisions_AcceptsCsvInputDecision()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.csv");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");

        File.WriteAllLines(
            decisionsPath,
            [
                string.Join(',', HumanDecisionOutputWriter.Columns),
                "proposal-0001,approved,Sanitized evidence supports improvement.,reviewer-a,2026-06-03T15:00:00Z,",
            ]);

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("proposal-0001", document.RootElement[0].GetProperty("proposal_id").GetString());
        Assert.Equal("approved", document.RootElement[0].GetProperty("human_decision").GetString());
    }

    [Fact]
    public void RecordHumanDecisions_AcceptsTopLevelDecisionJsonArray()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions-array.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(new[] { ValidApprovedDecision() }));

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void RecordHumanDecisions_HandlesEmptyDecisionInput()
    {
        using var tempDirectory = new TempDirectory();
        var decisionsPath = Path.Combine(tempDirectory.Path, "empty-decisions.json");
        var outputPath = Path.Combine(tempDirectory.Path, "decisions-output.json");
        File.WriteAllText(decisionsPath, "[]");
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(
            ["record-human-decisions", EvaluationFixturePath(), decisionsPath, "--json", outputPath],
            output,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Recorded 0 human decision(s).", output.ToString());
    }

    // --- generate-decision-template tests ---

    [Fact]
    public void GenerateDecisionTemplate_CreatesTemplateForReadyProposalsOnly()
    {
        using var tempDirectory = new TempDirectory();
        var csvPath = Path.Combine(tempDirectory.Path, "template.csv");
        var jsonPath = Path.Combine(tempDirectory.Path, "template.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-decision-template", EvaluationFixturePath(), "--csv", csvPath, "--json", jsonPath],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Generated decision template with 1 row(s).", output.ToString());

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal("proposal-0001", document.RootElement[0].GetProperty("proposal_id").GetString());
        Assert.Equal(string.Empty, document.RootElement[0].GetProperty("human_decision").GetString());
        Assert.Equal(string.Empty, document.RootElement[0].GetProperty("decision_rationale").GetString());

        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(string.Join(',', HumanDecisionOutputWriter.Columns), csvLines[0]);
        Assert.Contains("proposal-0001", csvLines[1]);
    }

    [Fact]
    public void GenerateDecisionTemplate_AcceptsTopLevelJsonArray()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "evals-array.json");
        var outputPath = Path.Combine(tempDirectory.Path, "template.json");
        var evaluation = ValidReadyEvaluation();
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { evaluation }));

        var exitCode = CliApplication.Run(
            ["generate-decision-template", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void GenerateDecisionTemplate_ReturnsEmptyForNoReadyProposals()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "no-ready.json");
        var outputPath = Path.Combine(tempDirectory.Path, "template.json");
        var evaluation = ValidReadyEvaluation();
        evaluation["proposal_evaluation_status"] = "needs-revision";
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { evaluation }));
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-decision-template", inputPath, "--json", outputPath],
            output,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Generated decision template with 0 row(s).", output.ToString());

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void GenerateDecisionTemplate_ReturnsNonZeroWithoutOutputOption()
    {
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-decision-template", EvaluationFixturePath()],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    // --- E2E pipeline test: M23→M24→M25→M26→M27 ---

    [Fact]
    public void EndToEnd_DiagnosisThroughHumanDecision()
    {
        using var tempDirectory = new TempDirectory();

        // M24: validate diagnoses
        var diagnosesPath = Path.Combine(AppContext.BaseDirectory, "TestData", "diagnoses.synthetic.json");
        var validatedDiagnosesPath = Path.Combine(tempDirectory.Path, "validated-diagnoses.json");
        var diagnosisExitCode = CliApplication.Run(
            ["validate-diagnoses", diagnosesPath, "--json", validatedDiagnosesPath],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, diagnosisExitCode);

        // M25: generate proposals
        var proposalsPath = Path.Combine(tempDirectory.Path, "proposals.json");
        var proposalExitCode = CliApplication.Run(
            ["generate-improvement-proposals", validatedDiagnosesPath, "--json", proposalsPath],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, proposalExitCode);

        // M26: evaluate proposals
        var evaluationsPath = Path.Combine(tempDirectory.Path, "evaluations.json");
        var evaluationExitCode = CliApplication.Run(
            ["evaluate-improvement-proposals", proposalsPath, "--json", evaluationsPath],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, evaluationExitCode);

        // M27a: generate decision template
        var templatePath = Path.Combine(tempDirectory.Path, "template.json");
        var templateExitCode = CliApplication.Run(
            ["generate-decision-template", evaluationsPath, "--json", templatePath],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, templateExitCode);

        // Read template to find which proposals are ready
        using var templateDocument = JsonDocument.Parse(File.ReadAllText(templatePath));
        var templateRows = templateDocument.RootElement;

        // Fill in decisions for template rows
        var filledDecisions = new List<Dictionary<string, object?>>();
        foreach (var templateRow in templateRows.EnumerateArray())
        {
            filledDecisions.Add(new Dictionary<string, object?>
            {
                ["proposal_id"] = templateRow.GetProperty("proposal_id").GetString(),
                ["human_decision"] = "approved",
                ["decision_rationale"] = "Sanitized evidence justifies this improvement.",
                ["approver_id"] = "reviewer-e2e",
                ["approved_at"] = "2026-06-03T15:00:00Z",
                ["conditions_or_notes"] = null,
            });
        }

        var filledDecisionsPath = Path.Combine(tempDirectory.Path, "filled-decisions.json");
        File.WriteAllText(filledDecisionsPath, JsonSerializer.Serialize(filledDecisions));

        // M27b: record human decisions
        var finalOutputPath = Path.Combine(tempDirectory.Path, "final-decisions.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var decisionExitCode = CliApplication.Run(
            ["record-human-decisions", evaluationsPath, filledDecisionsPath, "--json", finalOutputPath],
            output,
            error);

        Assert.Equal(0, decisionExitCode);
        Assert.Equal(string.Empty, error.ToString());

        using var finalDocument = JsonDocument.Parse(File.ReadAllText(finalOutputPath));
        Assert.True(finalDocument.RootElement.GetArrayLength() > 0);
        foreach (var finalDecision in finalDocument.RootElement.EnumerateArray())
        {
            Assert.Equal("approved", finalDecision.GetProperty("human_decision").GetString());
        }
    }

    private static Dictionary<string, object?> ValidApprovedDecision()
    {
        return new Dictionary<string, object?>
        {
            ["proposal_id"] = "proposal-0001",
            ["human_decision"] = "approved",
            ["decision_rationale"] = "Sanitized evidence supports improvement.",
            ["approver_id"] = "reviewer-a",
            ["approved_at"] = "2026-06-03T15:00:00Z",
            ["conditions_or_notes"] = null,
        };
    }

    private static Dictionary<string, object?> ValidReadyEvaluation()
    {
        return new Dictionary<string, object?>
        {
            ["proposal_id"] = "proposal-template-001",
            ["source_diagnosis_index"] = 1,
            ["trace_id"] = "trace-template-001",
            ["task_id"] = "maint-bug-001",
            ["task_category"] = "bug-investigation",
            ["client_kind"] = "copilot-cli",
            ["comparison_id"] = null,
            ["experiment_id"] = "baseline",
            ["experiment_condition"] = "baseline",
            ["prompt_version"] = "v1",
            ["agent_variant"] = "baseline",
            ["task_run_index"] = 1,
            ["failure_category_id"] = "F-TRACE",
            ["anti_pattern_id"] = null,
            ["severity"] = "minor",
            ["improvement_target"] = "workflow",
            ["proposal_title"] = "Review workflow improvement for F-TRACE",
            ["proposal_evaluation_status"] = "ready-for-human-approval",
            ["evaluator_findings"] = "Proposal is schema-valid and limited to human review.",
            ["required_human_checks"] = "Confirm target, sanitized evidence, and non-scope boundaries before approval.",
            ["evaluator_notes"] = "No automatic adoption or repository modification is performed.",
        };
    }

    private static string EvaluationFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "evaluations.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m27-human-approval-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

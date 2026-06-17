using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class ImprovementCandidatePipelineTests
{
    [Fact]
    public void GenerateImprovementCandidates_MapsDiagnosisJsonToCandidateJson()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.json");
        var outputPath = Path.Combine(tempDirectory.Path, "improvement-candidates.json");
        File.WriteAllText(
            inputPath,
            JsonSerializer.Serialize(new[]
            {
                DiagnosisCandidate("diagcand-0001", "trace-error", "F-ERROR", null, "major", "workflow", "auto-eligible"),
                DiagnosisCandidate("diagcand-0002", "trace-data", "F-DATA", "AP-RAW-CONTENT", "blocking", "workflow", "blocked"),
            }));

        var exitCode = CliApplication.Run(
            ["generate-improvement-candidates", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement.EnumerateArray().ToArray();
        var row = Assert.Single(rows);
        Assert.Equal("impcand-0001", row.GetProperty("improvement_candidate_id").GetString());
        Assert.Equal("diagcand-0001", row.GetProperty("source_diagnosis_candidate_id").GetString());
        Assert.Equal("trace-error", row.GetProperty("trace_id").GetString());
        Assert.Equal("F-ERROR", row.GetProperty("failure_category_id").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("anti_pattern_id").ValueKind);
        Assert.Equal("workflow", row.GetProperty("improvement_target").GetString());
        Assert.Equal("workflow", row.GetProperty("proposed_change_kind").GetString());
        Assert.Equal("auto-eligible", row.GetProperty("candidate_status").GetString());
        Assert.DoesNotContain("diagcand-0002", File.ReadAllText(outputPath));
    }

    [Fact]
    public void GenerateImprovementCandidates_AcceptsCsvAndWritesFixedHeader()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.csv");
        var outputPath = Path.Combine(tempDirectory.Path, "improvement-candidates.csv");
        File.WriteAllLines(
            inputPath,
            [
                string.Join(',', DiagnosisCandidateOutputWriter.Columns),
                DiagnosisCandidateCsvRow("diagcand-0001", "trace-loop", "F-TOOL", "AP-TOOL-LOOP", "major", "workflow", "candidate"),
            ]);

        var exitCode = CliApplication.Run(
            ["generate-improvement-candidates", inputPath, "--csv", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(string.Join(',', ImprovementCandidateOutputWriter.Columns), lines[0]);
        Assert.Contains("impcand-0001,diagcand-0001,trace-loop,F-TOOL,AP-TOOL-LOOP,major,workflow", lines[1]);
    }

    [Fact]
    public void GenerateAutoDecisions_WritesAutoApprovedHumanReviewAndBlocked()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "improvement-candidates.json");
        var outputPath = Path.Combine(tempDirectory.Path, "auto-decisions.json");
        File.WriteAllText(
            inputPath,
            JsonSerializer.Serialize(new[]
            {
                ImprovementCandidate("impcand-0001", "diagcand-0001", "major", "workflow", sensitiveBundlePath: null),
                ImprovementCandidate("impcand-0002", "diagcand-0002", "major", "workflow", sensitiveBundlePath: Path.Combine(tempDirectory.Path, "bundle")),
                ImprovementCandidate("impcand-0003", "diagcand-0003", "blocking", "workflow", sensitiveBundlePath: null),
            }));

        var exitCode = CliApplication.Run(
            ["generate-auto-decisions", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);

        Assert.Equal("autodec-0001", rows[0].GetProperty("auto_decision_id").GetString());
        Assert.Equal("auto-approved", rows[0].GetProperty("decision_status").GetString());
        Assert.Equal("DEC-AUTO-APPROVE-SAFE-METADATA-V1", rows[0].GetProperty("decision_rule_id").GetString());
        Assert.Equal("record-for-sprint4-planning", rows[0].GetProperty("next_action").GetString());
        Assert.False(rows[0].GetProperty("sensitive_content_included").GetBoolean());

        Assert.Equal("needs-human-review", rows[1].GetProperty("decision_status").GetString());
        Assert.Equal("DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1", rows[1].GetProperty("decision_rule_id").GetString());
        Assert.Equal("request-human-review", rows[1].GetProperty("next_action").GetString());
        Assert.True(rows[1].GetProperty("sensitive_content_included").GetBoolean());

        Assert.Equal("needs-human-review", rows[2].GetProperty("decision_status").GetString());
        Assert.Equal("DEC-HUMAN-REVIEW-DEFAULT-V1", rows[2].GetProperty("decision_rule_id").GetString());
        Assert.Equal("request-human-review", rows[2].GetProperty("next_action").GetString());
    }

    [Theory]
    [InlineData("Apply patch to improve the workflow.")]
    [InlineData("Generate a diff for the repository file.")]
    [InlineData("Modify repositories with the suggested workflow.")]
    [InlineData("Make repository changes for the improvement.")]
    [InlineData("Commit and push the approved proposal.")]
    [InlineData("Open a pull request for the improvement.")]
    [InlineData("Select the automatic winner for this experiment.")]
    public void GenerateAutoDecisions_BlocksScopeOverreachPatterns(string proposalSummary)
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "scope-overreach.json");
        var outputPath = Path.Combine(tempDirectory.Path, "auto-decisions.json");
        var candidate = ImprovementCandidate("impcand-0001", "diagcand-0001", "major", "workflow", sensitiveBundlePath: null);
        candidate["proposal_summary"] = proposalSummary;
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[] { candidate }));

        var exitCode = CliApplication.Run(
            ["generate-auto-decisions", inputPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("blocked", row.GetProperty("decision_status").GetString());
        Assert.Equal("DEC-BLOCK-SCOPE-OVERREACH-V1", row.GetProperty("decision_rule_id").GetString());
        Assert.Equal("do-not-implement", row.GetProperty("next_action").GetString());
    }

    [Fact]
    public void GenerateAutoDecisions_AcceptsCsvAndWritesFixedHeader()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = Path.Combine(tempDirectory.Path, "improvement-candidates.csv");
        var outputPath = Path.Combine(tempDirectory.Path, "auto-decisions.csv");
        File.WriteAllLines(
            inputPath,
            [
                string.Join(',', ImprovementCandidateOutputWriter.Columns),
                ImprovementCandidateCsvRow("impcand-0001", "diagcand-0001", "major", "workflow"),
            ]);

        var exitCode = CliApplication.Run(
            ["generate-auto-decisions", inputPath, "--csv", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(string.Join(',', AutoDecisionOutputWriter.Columns), lines[0]);
        Assert.Contains("autodec-0001,impcand-0001,diagcand-0001,trace-impcand-0001,auto-approved", lines[1]);
    }

    [Fact]
    public void GenerateImprovementCandidates_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-improvement-candidates", "diagnosis-candidates.json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    [Fact]
    public void GenerateAutoDecisions_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-auto-decisions", "improvement-candidates.json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv, --json, or both", error.ToString());
    }

    private static Dictionary<string, object?> DiagnosisCandidate(
        string diagnosisCandidateId,
        string traceId,
        string failureCategoryId,
        string? antiPatternId,
        string severity,
        string target,
        string candidateStatus)
    {
        return new Dictionary<string, object?>
        {
            ["diagnosis_candidate_id"] = diagnosisCandidateId,
            ["trace_id"] = traceId,
            ["source_record_ref"] = "measurements.json#row=1",
            ["rule_id"] = "DIAG-METRIC-ERROR-COUNT-V1",
            ["failure_category_id"] = failureCategoryId,
            ["anti_pattern_id"] = antiPatternId,
            ["severity"] = severity,
            ["recommended_improvement_target"] = target,
            ["evidence_summary"] = "Synthetic sanitized evidence summary.",
            ["evidence_ref"] = $"measurement:measurements.json#row=1:{diagnosisCandidateId}",
            ["content_included"] = false,
            ["sensitive_bundle_path"] = null,
            ["confidence"] = "high",
            ["required_human_checks"] = "Confirm the candidate against sanitized trace evidence.",
            ["candidate_status"] = candidateStatus,
        };
    }

    private static Dictionary<string, object?> ImprovementCandidate(
        string improvementCandidateId,
        string sourceDiagnosisCandidateId,
        string severity,
        string target,
        string? sensitiveBundlePath)
    {
        return new Dictionary<string, object?>
        {
            ["improvement_candidate_id"] = improvementCandidateId,
            ["source_diagnosis_candidate_id"] = sourceDiagnosisCandidateId,
            ["trace_id"] = $"trace-{improvementCandidateId}",
            ["failure_category_id"] = "F-ERROR",
            ["anti_pattern_id"] = null,
            ["severity"] = severity,
            ["improvement_target"] = target,
            ["proposal_title"] = $"Review {target} candidate for F-ERROR",
            ["proposal_summary"] = $"A {severity} diagnosis candidate suggests a {target} improvement.",
            ["proposed_change_kind"] = target,
            ["evidence_ref"] = $"measurement:measurements.json#row=1:{sourceDiagnosisCandidateId}",
            ["sensitive_bundle_path"] = sensitiveBundlePath,
            ["candidate_status"] = "auto-eligible",
        };
    }

    private static string DiagnosisCandidateCsvRow(
        string diagnosisCandidateId,
        string traceId,
        string failureCategoryId,
        string antiPatternId,
        string severity,
        string target,
        string candidateStatus)
    {
        var values = DiagnosisCandidateOutputWriter.Columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal);
        values["diagnosis_candidate_id"] = diagnosisCandidateId;
        values["trace_id"] = traceId;
        values["source_record_ref"] = "measurements.csv#row=2";
        values["rule_id"] = "DIAG-METRIC-TOOL-LOOP-V1";
        values["failure_category_id"] = failureCategoryId;
        values["anti_pattern_id"] = antiPatternId;
        values["severity"] = severity;
        values["recommended_improvement_target"] = target;
        values["evidence_summary"] = "Synthetic sanitized evidence summary.";
        values["evidence_ref"] = "measurement:measurements.csv#row=2";
        values["content_included"] = "false";
        values["confidence"] = "medium";
        values["required_human_checks"] = "Confirm loop behavior.";
        values["candidate_status"] = candidateStatus;
        return string.Join(',', DiagnosisCandidateOutputWriter.Columns.Select(column => values[column]));
    }

    private static string ImprovementCandidateCsvRow(
        string improvementCandidateId,
        string sourceDiagnosisCandidateId,
        string severity,
        string target)
    {
        var values = ImprovementCandidateOutputWriter.Columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal);
        values["improvement_candidate_id"] = improvementCandidateId;
        values["source_diagnosis_candidate_id"] = sourceDiagnosisCandidateId;
        values["trace_id"] = $"trace-{improvementCandidateId}";
        values["failure_category_id"] = "F-ERROR";
        values["severity"] = severity;
        values["improvement_target"] = target;
        values["proposal_title"] = $"Review {target} candidate for F-ERROR";
        values["proposal_summary"] = $"A {severity} diagnosis candidate suggests a {target} improvement.";
        values["proposed_change_kind"] = target;
        values["evidence_ref"] = $"measurement:measurements.csv#row=2:{sourceDiagnosisCandidateId}";
        values["candidate_status"] = "auto-eligible";
        return string.Join(',', ImprovementCandidateOutputWriter.Columns.Select(column => values[column]));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m4-candidate-pipeline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

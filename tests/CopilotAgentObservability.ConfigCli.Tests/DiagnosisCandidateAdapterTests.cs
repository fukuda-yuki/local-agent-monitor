using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class DiagnosisCandidateAdapterTests
{
    [Fact]
    public void AdaptDiagnosisCandidates_MapsJsonToM24DiagnosisJson()
    {
        using var tempDirectory = new TempDirectory();
        var candidatesPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.json");
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        File.WriteAllText(
            candidatesPath,
            JsonSerializer.Serialize(new[]
            {
                DiagnosisCandidate("diagcand-0001", "trace-error", "F-ERROR", null, "major", "workflow", "auto-eligible", "measurement:measurements.json#row=1"),
                DiagnosisCandidate("diagcand-0002", "trace-loop", "F-TOOL", "AP-TOOL-LOOP", "major", "workflow", "candidate", "measurement:measurements.json#row=2"),
                DiagnosisCandidate("diagcand-0003", "trace-data", "F-DATA", "AP-RAW-CONTENT", "blocking", "workflow", "blocked", "bundle:bundle:diagcand-0003"),
            }));
        File.WriteAllText(
            measurementsPath,
            JsonSerializer.Serialize(new[]
            {
                Measurement("trace-error", "copilot-cli", "bug-investigation", taskId: "adapter-task-001", taskRunIndex: 1),
                Measurement("trace-loop", "vscode-copilot-chat", "code-review", taskId: "adapter-task-002", taskRunIndex: 2),
                Measurement("trace-data", "copilot-cli", "bug-investigation", taskId: "adapter-task-003", taskRunIndex: 3),
            }));

        var exitCode = CliApplication.Run(
            ["adapt-diagnosis-candidates", candidatesPath, measurementsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);

        Assert.Equal("trace-error", rows[0].GetProperty("trace_id").GetString());
        Assert.Equal("adapter-task-001", rows[0].GetProperty("task_id").GetString());
        Assert.Equal("bug-investigation", rows[0].GetProperty("task_category").GetString());
        Assert.Equal("copilot-cli", rows[0].GetProperty("client_kind").GetString());
        Assert.Equal("baseline", rows[0].GetProperty("experiment_id").GetString());
        Assert.Equal("baseline", rows[0].GetProperty("experiment_condition").GetString());
        Assert.Equal("v1", rows[0].GetProperty("prompt_version").GetString());
        Assert.Equal("default", rows[0].GetProperty("agent_variant").GetString());
        Assert.Equal(1, rows[0].GetProperty("task_run_index").GetInt32());
        Assert.Equal("F-ERROR", rows[0].GetProperty("failure_category_id").GetString());
        Assert.Equal("workflow", rows[0].GetProperty("recommended_improvement_target").GetString());
        Assert.Equal("accepted-for-proposal", rows[0].GetProperty("review_status").GetString());
        Assert.Contains("rule_id=DIAG-METRIC-ERROR-COUNT-V1", rows[0].GetProperty("evidence_summary").GetString());
        Assert.Contains("evidence_ref=measurement:measurements.json#row=1", rows[0].GetProperty("evidence_summary").GetString());

        Assert.Equal("needs-human-review", rows[1].GetProperty("review_status").GetString());
        Assert.Equal("rejected", rows[2].GetProperty("review_status").GetString());
        var outputText = File.ReadAllText(outputPath);
        Assert.DoesNotContain("sensitive_bundle_path", outputText);
        Assert.DoesNotContain("tmp/sprint3-sensitive", outputText);
    }

    [Fact]
    public void AdaptDiagnosisCandidates_AcceptsCsvAndWritesFixedM24Header()
    {
        using var tempDirectory = new TempDirectory();
        var candidatesPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.csv");
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.csv");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.csv");
        File.WriteAllLines(
            candidatesPath,
            [
                string.Join(',', DiagnosisCandidateOutputWriter.Columns),
                DiagnosisCandidateCsvRow("diagcand-0001", "trace-loop", "F-TOOL", "AP-TOOL-LOOP", "major", "workflow", "candidate", $"{measurementsPath}#row=2"),
            ]);
        File.WriteAllLines(
            measurementsPath,
            [
                string.Join(',', MeasurementOutputWriter.Columns),
                MeasurementCsvRow("trace-loop", "copilot-cli", "code-review", taskId: "adapter-task-csv", taskRunIndex: "5"),
            ]);

        var exitCode = CliApplication.Run(
            ["adapt-diagnosis-candidates", candidatesPath, measurementsPath, "--csv", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(string.Join(',', DiagnosisOutputWriter.Columns), lines[0]);
        Assert.Contains("trace-loop,adapter-task-csv,code-review,copilot-cli,,baseline,baseline,v1,default,5,F-TOOL,AP-TOOL-LOOP,major", lines[1]);
        Assert.Contains("evidence_ref=measurement:measurements.csv#row=2", lines[1]);
        Assert.DoesNotContain(tempDirectory.Path, lines[1]);
        Assert.Contains("needs-human-review", lines[1]);
    }

    [Fact]
    public void AdaptDiagnosisCandidates_LeavesContextBlankForAmbiguousTraceMatch()
    {
        using var tempDirectory = new TempDirectory();
        var candidatesPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.json");
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        File.WriteAllText(
            candidatesPath,
            JsonSerializer.Serialize(new[]
            {
                DiagnosisCandidate("diagcand-0001", "trace-ambiguous", "F-ERROR", null, "major", "workflow", "candidate", "raw:trace-ambiguous:span-error"),
            }));
        File.WriteAllText(
            measurementsPath,
            JsonSerializer.Serialize(new[]
            {
                Measurement("trace-ambiguous", "copilot-cli", "bug-investigation", taskId: "adapter-task-001", taskRunIndex: 1),
                Measurement("trace-ambiguous", "copilot-cli", "bug-investigation", taskId: "adapter-task-002", taskRunIndex: 2),
            }));

        var exitCode = CliApplication.Run(
            ["adapt-diagnosis-candidates", candidatesPath, measurementsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("task_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("client_kind").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("task_run_index").ValueKind);
        Assert.Equal("trace-ambiguous", row.GetProperty("trace_id").GetString());
        Assert.Equal("needs-human-review", row.GetProperty("review_status").GetString());
    }

    [Fact]
    public void AdaptDiagnosisCandidates_OutputCanBeValidatedByExistingM24Command()
    {
        using var tempDirectory = new TempDirectory();
        var candidatesPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.json");
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var adaptedPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        var validatedPath = Path.Combine(tempDirectory.Path, "validated-diagnoses.json");
        File.WriteAllText(
            candidatesPath,
            JsonSerializer.Serialize(new[]
            {
                DiagnosisCandidate("diagcand-0001", "trace-error", "F-ERROR", null, "major", "workflow", "auto-eligible", "measurement:measurements.json#row=1"),
            }));
        File.WriteAllText(
            measurementsPath,
            JsonSerializer.Serialize(new[]
            {
                Measurement("trace-error", "copilot-cli", "bug-investigation", taskId: "adapter-task-001", taskRunIndex: 1),
            }));

        var adaptExitCode = CliApplication.Run(
            ["adapt-diagnosis-candidates", candidatesPath, measurementsPath, "--json", adaptedPath],
            new StringWriter(),
            new StringWriter());
        var validateExitCode = CliApplication.Run(
            ["validate-diagnoses", adaptedPath, "--json", validatedPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, adaptExitCode);
        Assert.Equal(0, validateExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(validatedPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("accepted-for-proposal", row.GetProperty("review_status").GetString());
    }

    [Fact]
    public void AdaptDiagnosisCandidates_MapsMissingTraceCandidateToValidM24TracePlaceholder()
    {
        using var tempDirectory = new TempDirectory();
        var candidatesPath = Path.Combine(tempDirectory.Path, "diagnosis-candidates.json");
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var outputPath = Path.Combine(tempDirectory.Path, "diagnoses.json");
        File.WriteAllText(
            candidatesPath,
            JsonSerializer.Serialize(new[]
            {
                DiagnosisCandidate("diagcand-0001", null, "F-MEASURE", "AP-SCHEMA-DRIFT", "major", "eval", "auto-eligible", "measurement:measurements.json#row=1"),
            }));
        File.WriteAllText(
            measurementsPath,
            JsonSerializer.Serialize(new[]
            {
                Measurement(null, "copilot-cli", "bug-investigation", taskId: "adapter-task-001", taskRunIndex: 1),
            }));

        var exitCode = CliApplication.Run(
            ["adapt-diagnosis-candidates", candidatesPath, measurementsPath, "--json", outputPath],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("missing-trace-diagcand-0001", row.GetProperty("trace_id").GetString());
        Assert.Equal("accepted-for-proposal", row.GetProperty("review_status").GetString());
    }

    private static Dictionary<string, object?> DiagnosisCandidate(
        string diagnosisCandidateId,
        string? traceId,
        string failureCategoryId,
        string? antiPatternId,
        string severity,
        string target,
        string candidateStatus,
        string evidenceRef)
    {
        return new Dictionary<string, object?>
        {
            ["diagnosis_candidate_id"] = diagnosisCandidateId,
            ["trace_id"] = traceId,
            ["source_record_ref"] = evidenceRef.StartsWith("measurement:", StringComparison.Ordinal)
                ? evidenceRef["measurement:".Length..]
                : evidenceRef,
            ["rule_id"] = RuleId(failureCategoryId),
            ["failure_category_id"] = failureCategoryId,
            ["anti_pattern_id"] = antiPatternId,
            ["severity"] = severity,
            ["recommended_improvement_target"] = target,
            ["evidence_summary"] = "Synthetic sanitized evidence summary.",
            ["evidence_ref"] = evidenceRef,
            ["content_included"] = evidenceRef.StartsWith("bundle:", StringComparison.Ordinal),
            ["sensitive_bundle_path"] = evidenceRef.StartsWith("bundle:", StringComparison.Ordinal) ? "tmp/sprint3-sensitive/bundle" : null,
            ["confidence"] = "high",
            ["required_human_checks"] = "Confirm the candidate against sanitized trace evidence.",
            ["candidate_status"] = candidateStatus,
        };
    }

    private static Dictionary<string, object?> Measurement(
        string? traceId,
        string clientKind,
        string taskCategory,
        string taskId,
        int taskRunIndex)
    {
        return new Dictionary<string, object?>
        {
            ["trace_id"] = traceId,
            ["experiment_id"] = "baseline",
            ["client_kind"] = clientKind,
            ["task_id"] = taskId,
            ["task_category"] = taskCategory,
            ["task_run_index"] = taskRunIndex,
            ["experiment_condition"] = "baseline",
            ["prompt_version"] = "v1",
            ["agent_variant"] = "default",
            ["repo_snapshot"] = "synthetic-repo",
            ["input_tokens"] = 1,
            ["output_tokens"] = 1,
            ["total_tokens"] = 2,
            ["turn_count"] = 1,
            ["tool_call_count"] = 1,
            ["duration_ms"] = 100,
            ["error_count"] = 1,
            ["success_status"] = "fail",
            ["evaluator_id"] = null,
            ["evaluation_notes"] = null,
            ["evaluated_at"] = null,
            ["unknown_spans_json"] = null,
            ["unknown_attributes_json"] = null,
            ["aggregation_notes"] = "synthetic measurement",
        };
    }

    private static string DiagnosisCandidateCsvRow(
        string diagnosisCandidateId,
        string traceId,
        string failureCategoryId,
        string antiPatternId,
        string severity,
        string target,
        string candidateStatus,
        string sourceRecordRef)
    {
        var values = DiagnosisCandidateOutputWriter.Columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal);
        values["diagnosis_candidate_id"] = diagnosisCandidateId;
        values["trace_id"] = traceId;
        values["source_record_ref"] = sourceRecordRef;
        values["rule_id"] = RuleId(failureCategoryId);
        values["failure_category_id"] = failureCategoryId;
        values["anti_pattern_id"] = antiPatternId;
        values["severity"] = severity;
        values["recommended_improvement_target"] = target;
        values["evidence_summary"] = "Synthetic sanitized evidence summary.";
        values["evidence_ref"] = $"measurement:{sourceRecordRef}";
        values["content_included"] = "false";
        values["confidence"] = "medium";
        values["required_human_checks"] = "Confirm candidate behavior.";
        values["candidate_status"] = candidateStatus;
        return string.Join(',', DiagnosisCandidateOutputWriter.Columns.Select(column => CsvEscaper.Escape(values[column])));
    }

    private static string MeasurementCsvRow(
        string traceId,
        string clientKind,
        string taskCategory,
        string taskId,
        string taskRunIndex)
    {
        var values = MeasurementOutputWriter.Columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal);
        values["trace_id"] = traceId;
        values["experiment_id"] = "baseline";
        values["client_kind"] = clientKind;
        values["task_id"] = taskId;
        values["task_category"] = taskCategory;
        values["task_run_index"] = taskRunIndex;
        values["experiment_condition"] = "baseline";
        values["prompt_version"] = "v1";
        values["agent_variant"] = "default";
        values["repo_snapshot"] = "synthetic-repo";
        values["input_tokens"] = "1";
        values["output_tokens"] = "1";
        values["total_tokens"] = "2";
        values["turn_count"] = "1";
        values["tool_call_count"] = "1";
        values["duration_ms"] = "100";
        values["error_count"] = "1";
        values["success_status"] = "fail";
        values["aggregation_notes"] = "synthetic measurement";
        return string.Join(',', MeasurementOutputWriter.Columns.Select(column => CsvEscaper.Escape(values[column])));
    }

    private static string RuleId(string failureCategoryId)
    {
        return failureCategoryId switch
        {
            "F-TOOL" => "DIAG-METRIC-TOOL-LOOP-V1",
            "F-DATA" => "DIAG-CONTENT-SENSITIVE-LEAK-V1",
            _ => "DIAG-METRIC-ERROR-COUNT-V1",
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m5-diagnosis-adapter-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

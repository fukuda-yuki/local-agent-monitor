using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class DashboardDatasetGenerationTests
{
    [Fact]
    public void GenerateDashboardDataset_WritesM2TablesWithSyntheticDerivedMetrics()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = tempDirectory.WriteFile("measurements.json", MeasurementsJson());
        var rawPath = tempDirectory.WriteFile("raw-dashboard.synthetic.json", RawDashboardJson());
        var diagnosisPath = tempDirectory.WriteFile("diagnosis-candidates.json", DiagnosisCandidatesJson());
        var improvementPath = tempDirectory.WriteFile("improvement-candidates.json", ImprovementCandidatesJson(tempDirectory.Path));
        var decisionsPath = tempDirectory.WriteFile("auto-decisions.json", AutoDecisionsJson(tempDirectory.Path));
        var outputPath = Path.Combine(tempDirectory.Path, "dashboard.json");
        var csvDirectory = Path.Combine(tempDirectory.Path, "csv");

        var exitCode = CliApplication.Run(
            [
                "generate-dashboard-dataset",
                measurementsPath,
                "--raw",
                rawPath,
                "--diagnosis-candidates",
                diagnosisPath,
                "--improvement-candidates",
                improvementPath,
                "--auto-decisions",
                decisionsPath,
                "--json",
                outputPath,
                "--csv-dir",
                csvDirectory,
            ],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var root = document.RootElement;
        Assert.Equal("sprint4-m2-v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("day", root.GetProperty("time_bucket_granularity").GetString());
        Assert.Equal(600000, root.GetProperty("parameters").GetProperty("long_running_trace_threshold_ms").GetInt32());

        var runRows = root.GetProperty("dashboard_run_summary").EnumerateArray().ToArray();
        Assert.Equal(2, runRows.Length);
        var firstRun = runRows.Single(row => row.GetProperty("trace_id").GetString() == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.Equal("2024-03-09T00:00:00.0000000+00:00", firstRun.GetProperty("time_bucket_start_utc").GetString());
        Assert.Equal("copilot-cli", firstRun.GetProperty("client_kind").GetString());
        Assert.Equal("error", firstRun.GetProperty("status").GetString());
        Assert.Equal(750, firstRun.GetProperty("ttft_ms").GetInt32());
        Assert.Equal("derived-first-generation-event", firstRun.GetProperty("ttft_source").GetString());
        Assert.Equal("unit-price-table", firstRun.GetProperty("cost_source").GetString());
        Assert.True(firstRun.GetProperty("estimated_cost").GetDecimal() > 0);
        Assert.True(firstRun.GetProperty("long_running_trace").GetBoolean());
        Assert.True(firstRun.GetProperty("stuck_session").GetBoolean());
        Assert.False(firstRun.GetProperty("sensitive_bundle_present").GetBoolean());

        var secondRun = runRows.Single(row => row.GetProperty("trace_id").GetString() == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(111, secondRun.GetProperty("ttft_ms").GetInt32());
        Assert.Equal("direct-attribute", secondRun.GetProperty("ttft_source").GetString());
        Assert.Equal("unavailable-unit-price", secondRun.GetProperty("cost_source").GetString());
        Assert.Equal(JsonValueKind.Null, secondRun.GetProperty("estimated_cost").ValueKind);
        Assert.True(secondRun.GetProperty("sensitive_bundle_present").GetBoolean());

        var operationRows = root.GetProperty("dashboard_operation_summary").EnumerateArray().ToArray();
        var shellOperation = operationRows.Single(row =>
            row.GetProperty("trace_id").GetString() == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            && row.GetProperty("operation_kind").GetString() == "tool"
            && row.GetProperty("tool_name").GetString() == "shell");
        Assert.Equal("error", shellOperation.GetProperty("status").GetString());
        Assert.Equal(2, shellOperation.GetProperty("retry_count").GetInt32());
        Assert.Equal(130000, shellOperation.GetProperty("total_duration_ms").GetInt32());
        Assert.True(shellOperation.GetProperty("long_running_tool").GetBoolean());

        var approvalOperation = operationRows.Single(row => row.GetProperty("operation_kind").GetString() == "approval");
        Assert.Equal(45000, approvalOperation.GetProperty("approval_wait_ms").GetInt32());
        Assert.Equal("denied", approvalOperation.GetProperty("permission_result").GetString());

        var subagentOperation = operationRows.Single(row => row.GetProperty("operation_kind").GetString() == "subagent");
        Assert.Equal(1, subagentOperation.GetProperty("subagent_call_count").GetInt32());
        Assert.Equal(1, subagentOperation.GetProperty("nested_agent_call_count").GetInt32());

        var candidateRows = root.GetProperty("dashboard_candidate_summary").EnumerateArray().ToArray();
        Assert.Contains(candidateRows, row => row.GetProperty("candidate_kind").GetString() == "diagnosis");
        Assert.Contains(candidateRows, row => row.GetProperty("candidate_kind").GetString() == "improvement");
        Assert.Contains(candidateRows, row => row.GetProperty("candidate_kind").GetString() == "auto-decision");
        Assert.Contains(candidateRows, row =>
            row.GetProperty("auto_decision_id").GetString() == "autodec-0002"
            && row.GetProperty("decision_status").GetString() == "needs-human-review"
            && row.GetProperty("sensitive_bundle_present").GetBoolean());
        Assert.All(candidateRows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("backlog_age_hours").ValueKind));

        var healthRows = root.GetProperty("dashboard_collection_health").EnumerateArray().ToArray();
        Assert.Contains(healthRows, row =>
            row.GetProperty("health_check_kind").GetString() == "unknown-telemetry"
            && row.GetProperty("unknown_span_count").GetInt32() == 1
            && row.GetProperty("unknown_attribute_count").GetInt32() == 1);

        Assert.Equal(string.Join(',', DashboardDatasetOutputWriter.RunSummaryColumns), File.ReadLines(Path.Combine(csvDirectory, "dashboard_run_summary.csv")).First());
        Assert.Equal(string.Join(',', DashboardDatasetOutputWriter.OperationSummaryColumns), File.ReadLines(Path.Combine(csvDirectory, "dashboard_operation_summary.csv")).First());
        Assert.Equal(string.Join(',', DashboardDatasetOutputWriter.CandidateSummaryColumns), File.ReadLines(Path.Combine(csvDirectory, "dashboard_candidate_summary.csv")).First());
        Assert.Equal(string.Join(',', DashboardDatasetOutputWriter.CollectionHealthColumns), File.ReadLines(Path.Combine(csvDirectory, "dashboard_collection_health.csv")).First());
    }

    [Fact]
    public void GenerateDashboardDataset_DoesNotLeakRawContentCredentialsOrSensitivePaths()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = tempDirectory.WriteFile("measurements.json", MeasurementsJson());
        var rawPath = tempDirectory.WriteFile("raw-dashboard.synthetic.json", RawDashboardJson());
        var diagnosisPath = tempDirectory.WriteFile("diagnosis-candidates.json", DiagnosisCandidatesJson());
        var improvementPath = tempDirectory.WriteFile("improvement-candidates.json", ImprovementCandidatesJson(tempDirectory.Path));
        var decisionsPath = tempDirectory.WriteFile("auto-decisions.json", AutoDecisionsJson(tempDirectory.Path));
        var outputPath = Path.Combine(tempDirectory.Path, "dashboard.json");

        var exitCode = CliApplication.Run(
            [
                "generate-dashboard-dataset",
                measurementsPath,
                "--raw",
                rawPath,
                "--diagnosis-candidates",
                diagnosisPath,
                "--improvement-candidates",
                improvementPath,
                "--auto-decisions",
                decisionsPath,
                "--json",
                outputPath,
            ],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);
        var output = File.ReadAllText(outputPath);
        Assert.DoesNotContain("synthetic prompt content should not leak", output);
        Assert.DoesNotContain("synthetic response content should not leak", output);
        Assert.DoesNotContain("synthetic tool arguments should not leak", output);
        Assert.DoesNotContain("synthetic auth token should not leak", output);
        Assert.DoesNotContain("Authorization=Basic", output);
        Assert.DoesNotContain("user@example.com", output);
        Assert.DoesNotContain("Synthetic User", output);
        Assert.DoesNotContain(tempDirectory.Path.Replace('\\', '/'), output.Replace('\\', '/'));
        Assert.DoesNotContain("sensitive-bundle", output);
        Assert.DoesNotContain("sensitive_bundle_path", output);
    }

    [Fact]
    public void GenerateDashboardDataset_ReturnsNonZeroWithoutOutputOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-dashboard-dataset", "measurements.json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --csv-dir, --json, or both", error.ToString());
    }

    private static string MeasurementsJson()
    {
        return """
            [
              {
                "trace_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "experiment_id": "baseline",
                "client_kind": "copilot-cli",
                "task_id": "maint-bug-001",
                "task_category": "bug-investigation",
                "task_run_index": 1,
                "experiment_condition": "baseline",
                "prompt_version": "v1",
                "agent_variant": "default",
                "repo_snapshot": "synthetic-dotnet-fixture-v1",
                "input_tokens": 100,
                "output_tokens": 50,
                "total_tokens": 150,
                "turn_count": 2,
                "tool_call_count": 3,
                "duration_ms": 910000,
                "error_count": 1,
                "success_status": "not-evaluated",
                "evaluator_id": null,
                "evaluation_notes": null,
                "evaluated_at": null,
                "unknown_spans_json": [{ "id": "span-unknown", "name": "copilot.experimental.span" }],
                "unknown_attributes_json": { "resourceAttributes": { "safe.detail": "kept" } },
                "aggregation_notes": "Synthetic dashboard measurement."
              },
              {
                "trace_id": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "experiment_id": "variant-a",
                "client_kind": "vscode-copilot-chat",
                "task_id": "maint-bug-001",
                "task_category": "bug-investigation",
                "task_run_index": 1,
                "experiment_condition": "variant",
                "prompt_version": "v2",
                "agent_variant": "short-instructions",
                "repo_snapshot": "synthetic-dotnet-fixture-v1",
                "input_tokens": 40,
                "output_tokens": 20,
                "total_tokens": 60,
                "turn_count": 1,
                "tool_call_count": 1,
                "duration_ms": 120000,
                "error_count": 0,
                "success_status": "not-evaluated",
                "evaluator_id": null,
                "evaluation_notes": null,
                "evaluated_at": null,
                "unknown_spans_json": null,
                "unknown_attributes_json": null,
                "aggregation_notes": "Synthetic dashboard measurement."
              }
            ]
            """;
    }

    private static string DiagnosisCandidatesJson()
    {
        return """
            [
              {
                "diagnosis_candidate_id": "diagcand-0001",
                "trace_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "source_record_ref": "measurements.json#row=1",
                "rule_id": "DIAG-METRIC-ERROR-COUNT-V1",
                "failure_category_id": "F-ERROR",
                "anti_pattern_id": null,
                "severity": "major",
                "recommended_improvement_target": "workflow",
                "evidence_summary": "Synthetic sanitized evidence summary.",
                "evidence_ref": "measurement:measurements.json#row=1",
                "content_included": false,
                "sensitive_bundle_path": null,
                "confidence": "high",
                "required_human_checks": "Confirm sanitized metric evidence.",
                "candidate_status": "auto-eligible"
              },
              {
                "diagnosis_candidate_id": "diagcand-0002",
                "trace_id": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "source_record_ref": "measurements.json#row=2",
                "rule_id": "DIAG-METRIC-TOKEN-VOLUME-V1",
                "failure_category_id": "F-TOKEN",
                "anti_pattern_id": "AP-PROMPT-LONG",
                "severity": "minor",
                "recommended_improvement_target": "prompt",
                "evidence_summary": "Synthetic sanitized evidence summary.",
                "evidence_ref": "bundle:synthetic:diagcand-0002",
                "content_included": false,
                "sensitive_bundle_path": "C:/sensitive-bundle/diagcand-0002",
                "confidence": "medium",
                "required_human_checks": "Confirm sanitized metric evidence.",
                "candidate_status": "candidate"
              }
            ]
            """;
    }

    private static string ImprovementCandidatesJson(string tempPath)
    {
        var escapedPath = JsonEncodedText.Encode(Path.Combine(tempPath, "sensitive-bundle", "impcand-0002")).ToString();
        return $$"""
            [
              {
                "improvement_candidate_id": "impcand-0001",
                "source_diagnosis_candidate_id": "diagcand-0001",
                "trace_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "failure_category_id": "F-ERROR",
                "anti_pattern_id": null,
                "severity": "major",
                "improvement_target": "workflow",
                "proposal_title": "Review workflow candidate for F-ERROR",
                "proposal_summary": "Synthetic sanitized proposal summary.",
                "proposed_change_kind": "workflow",
                "evidence_ref": "measurement:measurements.json#row=1",
                "sensitive_bundle_path": null,
                "candidate_status": "auto-eligible"
              },
              {
                "improvement_candidate_id": "impcand-0002",
                "source_diagnosis_candidate_id": "diagcand-0002",
                "trace_id": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "failure_category_id": "F-TOKEN",
                "anti_pattern_id": "AP-PROMPT-LONG",
                "severity": "minor",
                "improvement_target": "prompt",
                "proposal_title": "Review prompt candidate for F-TOKEN",
                "proposal_summary": "Synthetic sanitized proposal summary.",
                "proposed_change_kind": "prompt",
                "evidence_ref": "bundle:synthetic:diagcand-0002",
                "sensitive_bundle_path": "{{escapedPath}}",
                "candidate_status": "candidate"
              }
            ]
            """;
    }

    private static string AutoDecisionsJson(string tempPath)
    {
        var escapedPath = JsonEncodedText.Encode(Path.Combine(tempPath, "sensitive-bundle", "autodec-0002")).ToString();
        return $$"""
            [
              {
                "auto_decision_id": "autodec-0001",
                "source_improvement_candidate_id": "impcand-0001",
                "source_diagnosis_candidate_id": "diagcand-0001",
                "trace_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "decision_status": "auto-approved",
                "decision_rule_id": "DEC-AUTO-APPROVE-SAFE-METADATA-V1",
                "decision_reason": "Synthetic safe metadata decision.",
                "confidence": "high",
                "blocking_risk_checks": "",
                "sensitive_content_included": false,
                "sensitive_bundle_path": null,
                "implementation_target": "workflow",
                "next_action": "record-for-sprint4-planning"
              },
              {
                "auto_decision_id": "autodec-0002",
                "source_improvement_candidate_id": "impcand-0002",
                "source_diagnosis_candidate_id": "diagcand-0002",
                "trace_id": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "decision_status": "needs-human-review",
                "decision_rule_id": "DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1",
                "decision_reason": "Synthetic sensitive bundle reference requires review.",
                "confidence": "medium",
                "blocking_risk_checks": "sensitive-content",
                "sensitive_content_included": true,
                "sensitive_bundle_path": "{{escapedPath}}",
                "implementation_target": "prompt",
                "next_action": "request-human-review"
              }
            ]
            """;
    }

    private static string RawDashboardJson()
    {
        return """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "client.kind", "value": { "stringValue": "copilot-cli" } },
                      { "key": "user.email", "value": { "stringValue": "user@example.com" } },
                      { "key": "user.name", "value": { "stringValue": "Synthetic User" } },
                      { "key": "authorization.header", "value": { "stringValue": "Authorization=Basic synthetic auth token should not leak" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                          "spanId": "1000000000000001",
                          "name": "chat",
                          "startTimeUnixNano": "1710000000000000000",
                          "endTimeUnixNano": "1710000003000000000",
                          "attributes": [
                            { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                            { "key": "gen_ai.response.model", "value": { "stringValue": "gpt-4.1-mini" } },
                            { "key": "prompt.content", "value": { "stringValue": "synthetic prompt content should not leak" } }
                          ],
                          "events": [
                            {
                              "name": "first_token",
                              "timeUnixNano": "1710000000750000000",
                              "attributes": [
                                { "key": "response.content", "value": { "stringValue": "synthetic response content should not leak" } }
                              ]
                            }
                          ]
                        },
                        {
                          "traceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                          "spanId": "1000000000000002",
                          "name": "execute_tool shell",
                          "startTimeUnixNano": "1710000010000000000",
                          "endTimeUnixNano": "1710000140000000000",
                          "status": { "code": 2 },
                          "attributes": [
                            { "key": "gen_ai.tool.name", "value": { "stringValue": "shell" } },
                            { "key": "retry_count", "value": { "intValue": "2" } },
                            { "key": "error.type", "value": { "stringValue": "timeout" } },
                            { "key": "tool.arguments", "value": { "stringValue": "synthetic tool arguments should not leak" } }
                          ]
                        },
                        {
                          "traceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                          "spanId": "1000000000000003",
                          "name": "approval wait",
                          "startTimeUnixNano": "1710000140000000000",
                          "endTimeUnixNano": "1710000185000000000",
                          "attributes": [
                            { "key": "type", "value": { "stringValue": "approval" } },
                            { "key": "approval_wait_ms", "value": { "intValue": "45000" } },
                            { "key": "permission.result", "value": { "stringValue": "denied" } }
                          ]
                        },
                        {
                          "traceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                          "spanId": "1000000000000004",
                          "name": "subagent call",
                          "startTimeUnixNano": "1710000185000000000",
                          "endTimeUnixNano": "1710000200000000000",
                          "attributes": [
                            { "key": "type", "value": { "stringValue": "subagent" } },
                            { "key": "nested_agent_call_count", "value": { "intValue": "1" } }
                          ]
                        },
                        {
                          "traceId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                          "spanId": "2000000000000001",
                          "name": "chat",
                          "startTimeUnixNano": "1710000300000000000",
                          "endTimeUnixNano": "1710000310000000000",
                          "attributes": [
                            { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                            { "key": "gen_ai.response.model", "value": { "stringValue": "unknown-model" } },
                            { "key": "ttft_ms", "value": { "intValue": "111" } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m3-dashboard-dataset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
